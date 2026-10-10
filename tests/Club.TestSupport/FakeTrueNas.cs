using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Club.TestSupport;

using Club.TrueNas;

/// <summary>
/// Поддельный middleware TrueNAS 25.10 для тестов адаптера: настоящий TLS (свой CA), JSON-RPC 2.0 по WebSocket,
/// состояние в памяти и формы ошибок из исходников middleware (docs/research/truenas-api.md §4, §7.6, §8).
/// </summary>
public sealed partial class FakeTrueNas : IAsyncDisposable
{
    public const string Username = "clubsrv";
    public const string ApiKey = "1-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly WebApplication _app;
    private readonly object _lock = new();
    private readonly Dictionary<string, JsonObject> _datasets = new();
    private readonly Dictionary<string, JsonObject> _snapshots = new();
    private readonly Dictionary<int, JsonObject> _extents = new();
    private readonly Dictionary<int, JsonObject> _targets = new();
    private readonly Dictionary<int, JsonObject> _targetExtents = new();
    private readonly Dictionary<int, JsonObject> _auths = new();
    private readonly Dictionary<int, JsonObject> _initiators = new();
    private readonly List<(string Initiator, string Target)> _sessions = [];
    private readonly Dictionary<string, int> _snapshotSeq = new();
    private readonly Dictionary<string, int> _rollbacks = new();
    private int _nextSnapshotSeq = 1;
    private readonly ConcurrentDictionary<string, int> _dropAfterExecute = new();
    private int _nextId = 1;

    public string CaPath { get; }
    public int Port { get; private set; }
    public string[] OfferedVersions { get; set; } = ["v25.10.4", "v25.10.5", "v26.0.0"];
    public string Release { get; set; } = "25.10.7";
    public ConcurrentBag<string> Calls { get; } = [];

    private readonly List<string> _callLog = [];

    /// <summary>Вызовы по порядку — для проверок последовательности (например, таргет удалён раньше группы инициаторов).</summary>
    public IReadOnlyList<string> CallLog
    {
        get
        {
            lock (_callLog)
            {
                return _callLog.ToList();
            }
        }
    }

    /// <summary>TrueNAS «лежит»: открытые соединения рвутся, новые и /api/versions отклоняются.</summary>
    public bool Down { get; set; }
    public int Connections;

    private FakeTrueNas(WebApplication app, string caPath)
    {
        _app = app;
        CaPath = caPath;
    }

    public static async Task<FakeTrueNas> StartAsync()
    {
        var (ca, server) = CreateCertificates();
        var caPath = Path.Combine(Path.GetTempPath(), $"fake-truenas-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(server)));
        var app = builder.Build();
        var fake = new FakeTrueNas(app, caPath);
        app.UseWebSockets();
        app.MapGet("/api/versions", () => fake.Down ? Results.StatusCode(503) : Results.Json(fake.OfferedVersions));
        app.Map("/api/{version}", (HttpContext context) => fake.HandleSocketAsync(context));
        await app.StartAsync();
        fake.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        fake.StartIscsiPortal();
        return fake;
    }

    public TrueNasOptions Options(string? apiKey = null, string? caPath = null) => new()
    {
        Host = "localhost",
        Port = Port,
        Username = Username,
        ApiKey = apiKey ?? ApiKey,
        CaCertificatePath = caPath ?? CaPath,
        PingIntervalSec = 1,
        CallTimeoutSec = 10,
    };

    /// <summary>Следующий вызов метода выполнится, но ответ «потеряется»: соединение закроется (сбой посреди операции).</summary>
    public void DropResponseAfterExecuting(string method) => _dropAfterExecute[method] = 1;

    public void AddSession(string initiator, string targetName)
    {
        lock (_lock)
        {
            _sessions.Add((initiator, targetName));
        }
    }

    /// <summary>Сколько раз том откатывали к снапшоту (сброс личного диска места).</summary>
    public int Rollbacks(string dataset)
    {
        lock (_lock)
        {
            return _rollbacks.GetValueOrDefault(dataset);
        }
    }

    public void ClearSessions()
    {
        lock (_lock)
        {
            _sessions.Clear();
        }
    }

    public int Count(string kind)
    {
        lock (_lock)
        {
            return kind switch
            {
                "dataset" => _datasets.Count,
                "snapshot" => _snapshots.Count,
                "extent" => _extents.Count,
                "target" => _targets.Count,
                "targetextent" => _targetExtents.Count,
                "auth" => _auths.Count,
                "initiator" => _initiators.Count,
                _ => throw new ArgumentException(kind),
            };
        }
    }

    /// <summary>Ручное вмешательство мимо сервера: снять readonly с датасета (для тестов сверки).</summary>
    public void MakeWritable(string id)
    {
        lock (_lock)
        {
            _datasets[id]["readonly"] = Prop("off");
        }
    }

    public void RemoveDataset(string id)
    {
        lock (_lock)
        {
            _datasets.Remove(id);
        }
    }

    public void AddFilesystem(string id)
    {
        lock (_lock)
        {
            _datasets[id] = Dataset(id, "FILESYSTEM", readOnly: false, origin: "", labels: []);
        }
    }

    private async Task HandleSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || Down)
        {
            context.Response.StatusCode = Down ? 503 : 400;
            return;
        }

        Interlocked.Increment(ref Connections);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var authenticated = false;
        var buffer = new byte[1 << 20];
        while (socket.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult result;
            var count = 0;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                count += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }

            if (Down)
            {
                socket.Abort();
                return;
            }

            var request = JsonNode.Parse(buffer.AsSpan(0, count))!.AsObject();
            var id = request["id"]?.DeepClone();
            var method = request["method"]!.GetValue<string>();
            var args = request["params"] as JsonArray ?? [];
            Calls.Add(method);
            lock (_callLog)
            {
                _callLog.Add(method);
            }

            JsonObject response;
            try
            {
                if (!authenticated && method != "auth.login_ex")
                {
                    throw CallError(207, "ENOTAUTHENTICATED", "Not authenticated");
                }

                var value = Dispatch(method, args, ref authenticated);
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = value };
            }
            catch (RpcError error)
            {
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error.Payload };
            }

            if (_dropAfterExecute.TryRemove(method, out _))
            {
                socket.Abort();
                return;
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes(response.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private JsonNode? Dispatch(string method, JsonArray args, ref bool authenticated)
    {
        lock (_lock)
        {
            switch (method)
            {
                case "auth.login_ex":
                    var login = args[0]!.AsObject();
                    var ok = login["mechanism"]?.GetValue<string>() == "API_KEY_PLAIN"
                        && login["username"]?.GetValue<string>() == Username
                        && login["api_key"]?.GetValue<string>() == ApiKey;
                    authenticated = ok;
                    return new JsonObject { ["response_type"] = ok ? "SUCCESS" : "AUTH_ERR" };
                case "core.ping":
                    return "pong";
                case "system.version_short":
                    return Release;

                case "pool.dataset.query":
                    return Query(_datasets.Values, args);
                case "pool.dataset.create":
                    return CreateZvol(args[0]!.AsObject());
                case "pool.dataset.update":
                    return UpdateDataset(args[0]!.GetValue<string>(), args[1]!.AsObject());
                case "pool.dataset.attachments":
                    return Attachments(args[0]!.GetValue<string>());
                case "pool.dataset.delete":
                    return DeleteDataset(args[0]!.GetValue<string>(), args.Count > 1 && args[1]?["recursive"]?.GetValue<bool>() == true);

                case "pool.snapshot.query":
                    return Query(_snapshots.Values, args);
                case "pool.snapshot.create":
                    return CreateSnapshot(args[0]!.AsObject());
                case "pool.snapshot.clone":
                    return Clone(args[0]!.AsObject());
                case "pool.snapshot.rollback":
                    return Rollback(args[0]!.GetValue<string>(), args.Count > 1 && args[1]?["recursive"]?.GetValue<bool>() == true);
                case "pool.snapshot.delete":
                    return DeleteSnapshot(args[0]!.GetValue<string>(), args.Count > 1 ? args[1]!.AsObject() : new JsonObject());

                case "iscsi.global.config":
                    return new JsonObject { ["basename"] = IscsiBasename };
                case "iscsi.global.sessions":
                    return new JsonArray(_sessions.Select(s => (JsonNode)new JsonObject
                    {
                        ["initiator"] = s.Initiator, ["initiator_addr"] = "10.0.1.12", ["target"] = "iqn.2005-10.org.freenas.ctl:" + s.Target,
                    }).ToArray());
                case "iscsi.extent.query":
                    return Query(_extents.Values, args);
                case "iscsi.extent.create":
                    return CreateExtent(args[0]!.AsObject());
                case "iscsi.extent.delete":
                    return DeleteExtent(args[0]!.GetValue<int>());
                case "iscsi.target.query":
                    return Query(_targets.Values, args);
                case "iscsi.target.create":
                    return CreateTarget(args[0]!.AsObject());
                case "iscsi.target.update":
                    return UpdateTarget(args[0]!.GetValue<int>(), args[1]!.AsObject());
                case "iscsi.target.delete":
                    return DeleteTarget(args[0]!.GetValue<int>(), args.Count > 1 && args[1]!.GetValue<bool>());
                case "iscsi.targetextent.query":
                    return Query(_targetExtents.Values, args);
                case "iscsi.targetextent.create":
                    return CreateTargetExtent(args[0]!.AsObject());
                case "iscsi.targetextent.delete":
                    return DeleteTargetExtent(args[0]!.GetValue<int>(), args.Count > 1 && args[1]!.GetValue<bool>());
                case "iscsi.auth.query":
                    return Query(_auths.Values.Select(a => new JsonObject { ["id"] = a["id"]!.DeepClone(), ["tag"] = a["tag"]!.DeepClone(), ["user"] = a["user"]!.DeepClone(), ["secret"] = a["secret"]!.DeepClone() }), args);
                case "iscsi.auth.create":
                    return CreateAuth(args[0]!.AsObject());
                case "iscsi.auth.update":
                    return UpdateAuth(args[0]!.GetValue<int>(), args[1]!.AsObject());
                case "iscsi.auth.delete":
                    return DeleteAuth(args[0]!.GetValue<int>());
                case "iscsi.initiator.query":
                    return Query(_initiators.Values, args);
                case "iscsi.initiator.create":
                    return CreateInitiator(args[0]!.AsObject());
                case "iscsi.initiator.update":
                    _initiators[args[0]!.GetValue<int>()]["initiators"] = args[1]!["initiators"]!.DeepClone();
                    Reload(method);
                    return _initiators[args[0]!.GetValue<int>()].DeepClone();
                case "iscsi.initiator.delete":
                    return DeleteInitiator(args[0]!.GetValue<int>());
                default:
                    throw new RpcError(new JsonObject { ["code"] = -32601, ["message"] = "Method does not exist" });
            }
        }
    }

    private JsonNode CreateZvol(JsonObject data)
    {
        var name = data["name"]!.GetValue<string>();
        if (data["volsize"] is null)
        {
            throw Validation("pool_dataset_create.volsize", "This field is required for VOLUME", 22);
        }

        if (_datasets.ContainsKey(name))
        {
            throw CallError(14, "EFAULT", $"Failed to create dataset: cannot create '{name}': dataset already exists");
        }

        var labels = (data["user_properties"] as JsonArray ?? []).ToDictionary(p => p!["key"]!.GetValue<string>(), p => p!["value"]!.GetValue<string>());
        var dataset = Dataset(name, "VOLUME", readOnly: false, origin: "", labels);
        _datasets[name] = dataset;
        return dataset.DeepClone();
    }

    private JsonNode UpdateDataset(string id, JsonObject data)
    {
        var dataset = _datasets.GetValueOrDefault(id) ?? throw NotFound(id);
        if (data["readonly"] is { } ro)
        {
            dataset["readonly"] = Prop(ro.GetValue<string>().ToLowerInvariant());
            foreach (var extent in _extents.Values.Where(e => e["disk"]!.GetValue<string>() == "zvol/" + id && e["enabled"]!.GetValue<bool>()))
            {
                extent["ro"] = ro.GetValue<string>() == "ON";
            }
        }

        foreach (var update in data["user_properties_update"] as JsonArray ?? [])
        {
            dataset["user_properties"]![update!["key"]!.GetValue<string>()] = Prop(update["value"]!.GetValue<string>());
        }

        return dataset.DeepClone();
    }

    private JsonNode Attachments(string id)
    {
        var extents = _extents.Values.Where(e => e["disk"]!.GetValue<string>() == "zvol/" + id).Select(e => e["id"]!.GetValue<int>()).ToArray();
        return extents.Length == 0
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["type"] = "iSCSI Extent", ["service"] = "iscsitarget", ["attachments"] = new JsonArray(extents.Select(x => (JsonNode)x).ToArray()) });
    }

    private JsonNode DeleteDataset(string id, bool recursive = false)
    {
        if (!_datasets.ContainsKey(id))
        {
            throw NotFound(id);
        }

        if (recursive)
        {
            foreach (var own in _snapshots.Values.Where(s => s["dataset"]!.GetValue<string>() == id).Select(s => s["id"]!.GetValue<string>()).ToList())
            {
                if (ClonesOf(own).Any())
                {
                    throw CallError(14, "EFAULT", $"Failed to delete dataset: cannot destroy '{own}': snapshot has dependent clones");
                }

                _snapshots.Remove(own);
            }
        }

        if (_snapshots.Values.Any(s => s["dataset"]!.GetValue<string>() == id))
        {
            throw CallError(14, "EFAULT", $"Failed to delete dataset: cannot destroy '{id}': volume has children\nuse '-r' to destroy the following datasets");
        }

        // Как в middleware: каскад по attachment delegates удаляет включённые экстенты и связки без проверки сессий.
        var cascade = _extents.Values.Where(e => e["disk"]!.GetValue<string>() == "zvol/" + id).ToList();
        foreach (var extent in cascade)
        {
            var extentId = extent["id"]!.GetValue<int>();
            foreach (var te in _targetExtents.Where(te => te.Value["extent"]!.GetValue<int>() == extentId).ToList())
            {
                _targetExtents.Remove(te.Key);
            }

            _extents.Remove(extentId);
        }

        if (cascade.Count > 0)
        {
            Reload("pool.dataset.delete");
        }

        _datasets.Remove(id);
        _deviceCountdown.Remove(id);
        foreach (var deferred in _snapshots.Values.Where(s => s["defer_destroy"]?.GetValue<bool>() == true).ToList())
        {
            if (!ClonesOf(deferred["id"]!.GetValue<string>()).Any())
            {
                _snapshots.Remove(deferred["id"]!.GetValue<string>());
            }
        }

        return true;
    }

    private JsonNode CreateSnapshot(JsonObject data)
    {
        var dataset = data["dataset"]!.GetValue<string>();
        var name = data["name"]!.GetValue<string>();
        var id = $"{dataset}@{name}";
        if (!_datasets.ContainsKey(dataset))
        {
            throw Validation("snapshot_create.dataset", "Dataset not found", 2);
        }

        if (_snapshots.ContainsKey(id))
        {
            throw CallError(17, "EEXIST", $"Failed to snapshot {id}: dataset already exists");
        }

        var properties = new JsonObject();
        foreach (var (key, value) in data["properties"] as JsonObject ?? [])
        {
            properties[key] = Prop(value!.GetValue<string>());
        }

        var snapshot = new JsonObject { ["id"] = id, ["name"] = id, ["dataset"] = dataset, ["snapshot_name"] = name, ["properties"] = properties };
        _snapshots[id] = snapshot;
        _snapshotSeq[id] = _nextSnapshotSeq++;
        return snapshot.DeepClone();
    }

    /// <summary>Как ZFS: откат только к последнему снапшоту тома (без recursive); настройки iSCSI не трогает.</summary>
    private JsonNode? Rollback(string id, bool recursive = false)
    {
        if (!_snapshots.TryGetValue(id, out var snapshot))
        {
            throw NotFound(id);
        }

        var dataset = snapshot["dataset"]!.GetValue<string>();
        var seq = _snapshotSeq.GetValueOrDefault(id);
        var newer = _snapshots.Values.Where(s => s["dataset"]!.GetValue<string>() == dataset && _snapshotSeq.GetValueOrDefault(s["id"]!.GetValue<string>()) > seq)
            .Select(s => s["id"]!.GetValue<string>()).ToList();
        if (recursive)
        {
            // zfs rollback -r: более новые снапшоты удаляются.
            newer.ForEach(n => _snapshots.Remove(n));
            newer.Clear();
        }

        if (newer.Count > 0)
        {
            throw CallError(17, "EEXIST", $"cannot rollback to '{id}': more recent snapshots or bookmarks exist");
        }

        _rollbacks[dataset] = _rollbacks.GetValueOrDefault(dataset) + 1;
        return null;
    }

    private JsonNode Clone(JsonObject data)
    {
        var snapshot = data["snapshot"]!.GetValue<string>();
        var target = data["dataset_dst"]!.GetValue<string>();
        if (!_snapshots.ContainsKey(snapshot))
        {
            throw CallError(14, "EFAULT", $"Failed to clone snapshot: Snapshot {snapshot} not found");
        }

        if (_datasets.ContainsKey(target))
        {
            throw CallError(14, "EFAULT", $"Failed to clone snapshot: cannot create '{target}': dataset already exists");
        }

        var parent = target[..target.LastIndexOf('/')];
        if (!_datasets.ContainsKey(parent))
        {
            throw CallError(14, "EFAULT", $"Failed to clone snapshot: parent '{parent}' does not exist");
        }

        var properties = (data["dataset_properties"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
        var readOnly = properties.Remove("readonly", out var ro) && ro == "on";
        _datasets[target] = Dataset(target, "VOLUME", readOnly, origin: snapshot, properties);
        if (CloneDeviceDelay > 0)
        {
            _deviceCountdown[target] = CloneDeviceDelay;
        }

        return true;
    }

    private JsonNode DeleteSnapshot(string id, JsonObject options)
    {
        if (!_snapshots.TryGetValue(id, out var snapshot))
        {
            throw NotFound(id);
        }

        var clones = ClonesOf(id).ToList();
        if (clones.Count > 0)
        {
            if (options["defer"]?.GetValue<bool>() != true)
            {
                throw Validation("options.defer", $"Please set this attribute as '{id}' snapshot has dependent clones: {string.Join(", ", clones)}", 22);
            }

            snapshot["defer_destroy"] = true;
            return true;
        }

        _snapshots.Remove(id);
        return true;
    }

    private JsonNode CreateExtent(JsonObject data)
    {
        var name = data["name"]!.GetValue<string>();
        var disk = data["disk"]!.GetValue<string>();
        if (_extents.Values.Any(e => e["name"]!.GetValue<string>() == name))
        {
            throw Validation("iscsi_extent_create.name", "Extent name must be unique", 22);
        }

        var zvol = disk["zvol/".Length..];
        if (!_datasets.ContainsKey(zvol) || !DeviceReady(zvol))
        {
            // clean_type_and_path: os.path.exists('/dev/zvol/…'); узел свежего клона udev создаёт не сразу.
            if (!DeviceReady(zvol))
            {
                _deviceCountdown[zvol]--;
            }

            throw Validation("iscsi_extent_create.disk", $"Device /dev/{disk} for volume does not exist", 2);
        }

        var id = _nextId++;
        var extent = new JsonObject
        {
            ["id"] = id, ["name"] = name, ["type"] = "DISK", ["disk"] = disk, ["ro"] = data["ro"]?.GetValue<bool>() ?? false,
            ["enabled"] = true, ["comment"] = data["comment"]?.GetValue<string>() ?? "",
        };
        _extents[id] = extent;
        return extent.DeepClone();
    }

    private JsonNode DeleteExtent(int id)
    {
        var extent = _extents.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        var targets = _targetExtents.Values.Where(te => te["extent"]!.GetValue<int>() == id).Select(te => te["target"]!.GetValue<int>()).ToList();
        if (targets.Any(t => _sessions.Any(s => s.Target == _targets[t]["name"]!.GetValue<string>())))
        {
            throw CallError(14, "EFAULT", $"Associated target(s) {string.Join(",", targets)} are in use.");
        }

        foreach (var te in _targetExtents.Where(te => te.Value["extent"]!.GetValue<int>() == id).ToList())
        {
            _targetExtents.Remove(te.Key);
        }

        _extents.Remove(id);
        Reload("iscsi.extent.delete");
        return extent["id"]!.DeepClone();
    }

    /// <summary>Секрет CHAP 12..16 символов; уникальность tag не проверяется — как в TrueNAS.</summary>
    private JsonNode CreateAuth(JsonObject data)
    {
        var secret = data["secret"]!.GetValue<string>();
        if (secret.Length is < 12 or > 16)
        {
            throw Validation("iscsi_auth_create.secret", "Secret must be between 12 and 16 characters.", 22);
        }

        var id = _nextId++;
        _auths[id] = new JsonObject { ["id"] = id, ["tag"] = data["tag"]!.GetValue<int>(), ["user"] = data["user"]!.GetValue<string>(), ["secret"] = secret, ["peeruser"] = "", ["peersecret"] = "" };
        Reload("iscsi.auth.create");
        return _auths[id].DeepClone();
    }

    private JsonNode UpdateAuth(int id, JsonObject data)
    {
        var auth = _auths.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        if (data["secret"]?.GetValue<string>() is { } secret)
        {
            if (secret.Length is < 12 or > 16)
            {
                throw Validation("iscsi_auth_update.secret", "Secret must be between 12 and 16 characters.", 22);
            }

            auth["secret"] = secret;
        }

        Reload("iscsi.auth.update");
        return auth.DeepClone();
    }

    /// <summary>Последнюю запись tag, на который ссылается таргет, удалить нельзя.</summary>
    private JsonNode DeleteAuth(int id)
    {
        var auth = _auths.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        var tag = auth["tag"]!.GetValue<int>();
        var lastOfTag = _auths.Values.Count(a => a["tag"]!.GetValue<int>() == tag) == 1;
        if (lastOfTag && _targets.Values.Any(t => Groups(t).Any(g => g["auth"]?.GetValue<int>() == tag)))
        {
            throw CallError(22, "EINVAL", $"Authorized access of {tag} is being used by following target(s)");
        }

        _auths.Remove(id);
        Reload("iscsi.auth.delete");
        return true;
    }

    private JsonNode CreateInitiator(JsonObject data)
    {
        var id = _nextId++;
        _initiators[id] = new JsonObject { ["id"] = id, ["initiators"] = data["initiators"]?.DeepClone() ?? new JsonArray(), ["comment"] = data["comment"]?.GetValue<string>() ?? "" };
        Reload("iscsi.initiator.create");
        return _initiators[id].DeepClone();
    }

    /// <summary>Как в TrueNAS (FK ondelete SET NULL): ссылки таргетов на удалённую группу обнуляются — таргет открыт всем.</summary>
    private JsonNode DeleteInitiator(int id)
    {
        _ = _initiators.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        foreach (var group in _targets.Values.SelectMany(Groups).Where(g => g["initiator"]?.GetValue<int>() == id))
        {
            group["initiator"] = null;
        }

        _initiators.Remove(id);
        Reload("iscsi.initiator.delete");
        return true;
    }

    private static IEnumerable<JsonObject> Groups(JsonObject target) =>
        target["groups"] is JsonArray groups ? groups.OfType<JsonObject>() : [];

    /// <summary>Таргет, открытый всем: группа без инициаторов или с пустым списком (так TrueNAS пишет INITIATOR *).</summary>
    public bool IsOpenToEveryone(string targetName)
    {
        lock (_lock)
        {
            var target = _targets.Values.FirstOrDefault(t => t["name"]!.GetValue<string>() == targetName);
            return target is not null && Groups(target).Any(g =>
                g["initiator"] is null || (_initiators.GetValueOrDefault(g["initiator"]!.GetValue<int>())?["initiators"] as JsonArray)?.Count is null or 0);
        }
    }

    /// <summary>Снимок объекта для проверок в тестах.</summary>
    public JsonObject? Find(string kind, Func<JsonObject, bool> predicate)
    {
        lock (_lock)
        {
            var rows = kind switch
            {
                "extent" => _extents.Values,
                "target" => _targets.Values,
                "auth" => _auths.Values,
                "initiator" => _initiators.Values,
                _ => throw new ArgumentException(kind),
            };
            return rows.FirstOrDefault(predicate)?.DeepClone().AsObject();
        }
    }

    private JsonNode CreateTarget(JsonObject data)
    {
        var name = data["name"]!.GetValue<string>();
        if (_targets.Values.Any(t => t["name"]!.GetValue<string>() == name))
        {
            throw Validation("iscsi_target_create.name", "Target name already exists", 22);
        }

        foreach (var group in data["groups"] is JsonArray g ? g.OfType<JsonObject>() : [])
        {
            if (group["authmethod"]?.GetValue<string>() is "CHAP" && !_auths.Values.Any(a => a["tag"]!.GetValue<int>() == group["auth"]?.GetValue<int>()))
            {
                throw Validation("iscsi_target_create.groups.0.auth", "Authentication group does not exist", 22);
            }
        }

        var id = _nextId++;
        var target = new JsonObject { ["id"] = id, ["name"] = name, ["alias"] = data["alias"]?.DeepClone(), ["mode"] = "ISCSI", ["groups"] = data["groups"]?.DeepClone() };
        _targets[id] = target;
        Reload("iscsi.target.create");
        return target.DeepClone();
    }

    /// <summary>Поля модели необязательны: update без изменений — только reload (как в middleware, targets.py do_update).</summary>
    private JsonNode UpdateTarget(int id, JsonObject data)
    {
        var target = _targets.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        if (data["name"]?.GetValue<string>() is { } name && _targets.Values.Any(t => t != target && t["name"]!.GetValue<string>() == name))
        {
            throw Validation("iscsi_target_update.name", "Target name already exists", 22);
        }

        foreach (var key in new[] { "name", "alias", "groups" }.Where(data.ContainsKey))
        {
            target[key] = data[key]?.DeepClone();
        }

        Reload("iscsi.target.update");
        return target.DeepClone();
    }

    /// <summary>
    /// Как targets.py do_delete (TS-25.10.7): каждая связка — через <c>iscsi.targetextent.delete</c> (у каждой свой
    /// reload), затем группы и запись, <c>scstadmin -rem_target</c> и ещё один reload.
    /// </summary>
    private JsonNode DeleteTarget(int id, bool force)
    {
        var target = _targets.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        if (!force && _sessions.Any(s => s.Target == target["name"]!.GetValue<string>()))
        {
            throw CallError(14, "EFAULT", $"Target {target["name"]} is in use.");
        }

        foreach (var te in _targetExtents.Where(te => te.Value["target"]!.GetValue<int>() == id).Select(te => te.Key).ToList())
        {
            DeleteTargetExtent(te, force);
        }

        _targets.Remove(id);
        RemoveTargetFromScst(target["name"]!.GetValue<string>());
        Reload("iscsi.target.delete");
        return true;
    }

    /// <summary>target_to_extent.py do_delete: при сессиях таргета без force — отказ; запись удаляется, затем reload.</summary>
    private JsonNode DeleteTargetExtent(int id, bool force)
    {
        var row = _targetExtents.GetValueOrDefault(id) ?? throw NotFound(id.ToString());
        var targetName = _targets.GetValueOrDefault(row["target"]!.GetValue<int>())?["name"]?.GetValue<string>();
        if (!force && _sessions.Any(s => s.Target == targetName))
        {
            throw CallError(14, "EFAULT", $"Associated target {targetName} is in use.");
        }

        _targetExtents.Remove(id);
        Reload("iscsi.targetextent.delete");
        return true;
    }

    private JsonNode CreateTargetExtent(JsonObject data)
    {
        var target = data["target"]!.GetValue<int>();
        var extent = data["extent"]!.GetValue<int>();
        if (_targetExtents.Values.Any(te => te["extent"]!.GetValue<int>() == extent))
        {
            throw Validation("iscsi_targetextent_create.extent", "Extent is already in use", 22);
        }

        var id = _nextId++;
        var row = new JsonObject { ["id"] = id, ["target"] = target, ["extent"] = extent, ["lunid"] = data["lunid"]?.GetValue<int>() ?? 0 };
        _targetExtents[id] = row;
        Reload("iscsi.targetextent.create");
        return row.DeepClone();
    }

    private IEnumerable<string> ClonesOf(string snapshot) =>
        _datasets.Values.Where(d => d["origin"]!["rawvalue"]!.GetValue<string>() == snapshot).Select(d => d["id"]!.GetValue<string>());

    private static JsonArray Query(IEnumerable<JsonObject> rows, JsonArray args)
    {
        var filters = args.Count > 0 && args[0] is JsonArray f ? f : [];
        return new JsonArray(rows.Where(row => filters.All(filter =>
        {
            var field = filter![0]!.GetValue<string>();
            return filter[1]!.GetValue<string>() switch
            {
                "=" => row[field]?.ToJsonString() == filter[2]!.ToJsonString(),
                "^" => row[field]?.GetValue<string>().StartsWith(filter[2]!.GetValue<string>(), StringComparison.Ordinal) == true,
                var op => throw new NotSupportedException($"fake filter operator {op}"),
            };
        })).Select(r => (JsonNode)r.DeepClone()).ToArray());
    }

    private static JsonObject Dataset(string id, string type, bool readOnly, string origin, Dictionary<string, string> labels)
    {
        var userProperties = new JsonObject();
        foreach (var (key, value) in labels)
        {
            userProperties[key] = Prop(value);
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["type"] = type,
            // 25.10: value в верхнем регистре, rawvalue — как в ZFS.
            ["readonly"] = Prop(readOnly ? "on" : "off"),
            ["origin"] = Prop(origin),
            ["user_properties"] = userProperties,
        };
    }

    private static JsonObject Prop(string raw) => new() { ["value"] = raw.ToUpperInvariant(), ["rawvalue"] = raw, ["source"] = "LOCAL" };

    private static RpcError NotFound(string id) => Validation(null, $"{id} does not exist", 2);

    private static RpcError Validation(string? attribute, string message, int errno) => new(new JsonObject
    {
        ["code"] = -32602,
        ["message"] = "Invalid params",
        ["data"] = new JsonObject { ["error"] = 22, ["errname"] = "EINVAL", ["extra"] = new JsonArray(new JsonArray(attribute, message, errno)) },
    });

    private static RpcError CallError(int errno, string errname, string reason) => new(new JsonObject
    {
        ["code"] = -32001,
        ["message"] = "Method call error",
        ["data"] = new JsonObject { ["error"] = errno, ["errname"] = errname, ["reason"] = $"[{errname}] {reason}", ["trace"] = null, ["extra"] = null },
    });

    /// <summary>Тестовый CA и сертификат <c>localhost</c>, выписанный им (для HTTPS поддельных серверов).</summary>
    public static (X509Certificate2 Ca, X509Certificate2 Server) CreateCertificates()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Club Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(8));
        var server = X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(serverKey).Export(X509ContentType.Pkcs12), null);
        return (ca, server);
    }

    public async ValueTask DisposeAsync()
    {
        StopIscsiPortal();
        await _app.StopAsync();
        await _app.DisposeAsync();
        File.Delete(CaPath);
    }

    private sealed class RpcError(JsonObject payload) : Exception
    {
        public JsonObject Payload { get; } = payload;
    }
}
