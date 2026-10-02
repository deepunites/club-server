using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Club.TestSupport;

/// <summary>
/// Рантайм SCST и iSCSI-портал поддельного TrueNAS: что на самом деле отдано инициаторам. Конфиг (<c>*.query</c>) и
/// рантайм расходятся, когда reload срывается, а middleware об этом не узнаёт (docs/research/truenas-api.md §8.4).
/// Модель reload — по scst.conf.mako и scstadmin TS-25.10.7: extent попадает в SCST только с reload от другого вызова
/// (extent.create его не делает), таргет без LUN пишется <c>enabled 0</c>, с <c>-force</c> сначала идёт проход удалений.
/// </summary>
public sealed partial class FakeTrueNas
{
    private const string IscsiBasename = "iqn.2005-10.org.freenas.ctl";

    private readonly Dictionary<string, (string Disk, bool ReadOnly)> _scstDevices = new();
    private readonly HashSet<string> _scstCopyManager = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScstTarget> _scstTargets = new();
    private readonly ConcurrentDictionary<string, int> _loseReload = new();
    private readonly Dictionary<string, int> _deviceCountdown = new();
    private readonly List<string> _discoveryInitiators = [];
    private readonly CancellationTokenSource _iscsiStop = new();
    private TcpListener? _iscsi;
    private int? _discoveryAnswersLeft;

    /// <summary>Служба iSCSI запущена. Остановленной middleware reload не делает (_service_change), портал не отвечает.</summary>
    public bool IscsiServiceRunning { get; set; } = true;

    /// <summary>
    /// Ошибка TrueNAS 25.10.7 (в 26 исправлена, NAS-131279): SCST сам добавляет каждое новое устройство в copy_manager_tgt;
    /// у read-only устройства атрибут LUN <c>read_only</c> помечен [key], а scst.conf перечисляет живые LUN copy_manager
    /// без атрибутов. <c>scstadmin -force</c> видит «configured differently», переназначает LUN, SCST отказывает (EINVAL,
    /// «Copy Manager does not support read only devices»), scstadmin выходит, ничего дальше не применив. Каждый такой срыв
    /// снимает одно устройство из copy_manager_tgt. Middleware отвечает успехом.
    /// </summary>
    public bool ReadOnlyCopyManagerBug { get; set; } = true;

    /// <summary>Каждый reload срывается по причине, которую повторный reload не лечит.</summary>
    public bool ReloadsFail { get; set; }

    /// <summary>Сколько вызовов extent.create после clone ещё не видят /dev/zvol (udev асинхронен).</summary>
    public int CloneDeviceDelay { get; set; }

    /// <summary>Discovery только с CHAP: вход без аутентификации отклоняется (status 0x0201).</summary>
    public bool DiscoveryAuthRequired { get; set; }

    /// <summary>Ответ SendTargets режется на PDU по столько байт (0 — одним PDU).</summary>
    public int DiscoveryTextChunk { get; set; }

    /// <summary>
    /// Сколько ещё соединений портал обслужит; потом закрывает их сразу (связь пропала посреди проверки). <c>null</c> —
    /// без ограничения.
    /// </summary>
    public int? DiscoveryAnswersLeft
    {
        get
        {
            lock (_discoveryInitiators)
            {
                return _discoveryAnswersLeft;
            }
        }
        set
        {
            lock (_discoveryInitiators)
            {
                _discoveryAnswersLeft = value;
            }
        }
    }

    public int IscsiPort { get; private set; }

    /// <summary>Портал для discovery с сервера (<c>Library:DiscoveryAddress</c>).</summary>
    public string IscsiPortal => $"127.0.0.1:{IscsiPort}";

    public int Reloads { get; private set; }

    public int FailedReloads { get; private set; }

    /// <summary>Имена инициаторов, которые входили в discovery.</summary>
    public IReadOnlyList<string> DiscoveryInitiators
    {
        get
        {
            lock (_discoveryInitiators)
            {
                return _discoveryInitiators.ToList();
            }
        }
    }

    /// <summary>Следующий reload, вызванный этим методом, сорвётся (scstadmin упал, middleware ответил успехом).</summary>
    public void LoseReloadOf(string method) => _loseReload[method] = 1;

    /// <summary>Таргет включён в SCST и у него есть LUN — ПК его найдут.</summary>
    public bool IsLive(string targetName)
    {
        lock (_lock)
        {
            return _scstTargets.TryGetValue(targetName, out var target) && target.Enabled;
        }
    }

    /// <summary>Устройства, сейчас назначенные copy_manager_tgt.</summary>
    public IReadOnlyCollection<string> CopyManagerDevices
    {
        get
        {
            lock (_lock)
            {
                return _scstCopyManager.ToList();
            }
        }
    }

    /// <summary>
    /// Стоп/старт службы iSCSI в интерфейсе TrueNAS: <c>scstadmin -clear_config</c>, затем <c>-config</c> без
    /// <c>-force</c> — прохода удалений нет, поэтому применяется всё. Каждое read-only устройство снова в copy_manager_tgt.
    /// </summary>
    public void RestartIscsiService()
    {
        lock (_lock)
        {
            IscsiServiceRunning = true;
            _scstDevices.Clear();
            _scstCopyManager.Clear();
            _scstTargets.Clear();
            var (devices, targets) = RenderScst();
            ApplyScst(devices, targets);
        }
    }

    /// <summary>Таргет выключен в SCST мимо конфига (так осталось после сорвавшегося reload у старого сервера).</summary>
    public void DisableInScst(string targetName)
    {
        lock (_lock)
        {
            if (_scstTargets.TryGetValue(targetName, out var target))
            {
                target.Luns.Clear();
            }
        }
    }

    /// <summary>
    /// reload iscsitarget: перегенерация scst.conf и <c>scstadmin -noprompt -force -config</c>; код возврата middleware
    /// не смотрит. Вызывается под <c>_lock</c> из методов, которые в middleware делают <c>_service_change(reload)</c>.
    /// </summary>
    private void Reload(string cause)
    {
        if (!IscsiServiceRunning)
        {
            return;
        }

        Reloads++;
        if (ReloadsFail || _loseReload.TryRemove(cause, out _))
        {
            FailedReloads++;
            return;
        }

        var (devices, targets) = RenderScst();

        // Проход удалений (-force), начиная с copy_manager_tgt. Секция copy_manager в scst.conf — его живые LUN, чьи
        // устройства есть в конфиге (calc_copy_manager_luns); лишние LUN снимаются.
        foreach (var device in _scstCopyManager.Order(StringComparer.Ordinal).ToList())
        {
            if (!devices.ContainsKey(device))
            {
                _scstCopyManager.Remove(device);
            }
            else if (ReadOnlyCopyManagerBug && _scstDevices.TryGetValue(device, out var live) && live.ReadOnly)
            {
                _scstCopyManager.Remove(device); // removeLun прошёл, addLun — EINVAL, FATAL, exit 1
                FailedReloads++;
                return;
            }
        }

        ApplyScst(devices, targets);
    }

    /// <summary>Что попадёт в scst.conf: включённые экстенты с узлом /dev, таргеты с их LUN и списком инициаторов.</summary>
    private (Dictionary<string, (string Disk, bool ReadOnly)> Devices, Dictionary<string, ScstTarget> Targets) RenderScst()
    {
        var devices = new Dictionary<string, (string Disk, bool ReadOnly)>();
        foreach (var extent in _extents.Values.Where(e => e["enabled"]!.GetValue<bool>()))
        {
            var disk = extent["disk"]!.GetValue<string>();
            if (_datasets.ContainsKey(disk["zvol/".Length..]) && DeviceReady(disk["zvol/".Length..]))
            {
                devices[extent["name"]!.GetValue<string>()] = (disk, extent["ro"]!.GetValue<bool>());
            }
        }

        var targets = new Dictionary<string, ScstTarget>();
        foreach (var target in _targets.Values)
        {
            var id = target["id"]!.GetValue<int>();
            var luns = _targetExtents.Values
                .Where(te => te["target"]!.GetValue<int>() == id)
                .Select(te => _extents.GetValueOrDefault(te["extent"]!.GetValue<int>())?["name"]?.GetValue<string>())
                .OfType<string>()
                .Where(devices.ContainsKey)
                .ToHashSet();

            // Группа без инициаторов (или с пустым списком) → INITIATOR *; таргет без групп не виден никому.
            var initiators = new HashSet<string>();
            foreach (var group in Groups(target))
            {
                var names = group["initiator"] is { } gid && _initiators.GetValueOrDefault(gid.GetValue<int>())?["initiators"] is JsonArray list
                    ? list.Select(i => i!.GetValue<string>()).ToList()
                    : [];
                initiators.UnionWith(names.Count == 0 ? ["*"] : names);
            }

            targets[target["name"]!.GetValue<string>()] = new ScstTarget(luns, initiators);
        }

        return (devices, targets);
    }

    private void ApplyScst(Dictionary<string, (string Disk, bool ReadOnly)> devices, Dictionary<string, ScstTarget> targets)
    {
        foreach (var name in _scstDevices.Keys.Where(d => !devices.ContainsKey(d)).ToList())
        {
            _scstDevices.Remove(name);
            _scstCopyManager.Remove(name);
        }

        foreach (var (name, device) in devices)
        {
            if (_scstDevices.TryAdd(name, device))
            {
                _scstCopyManager.Add(name); // auto_cm_assignment: новое устройство SCST сам добавляет в copy_manager_tgt
            }
        }

        _scstTargets.Clear();
        foreach (var (name, target) in targets)
        {
            _scstTargets[name] = target;
        }
    }

    private bool DeviceReady(string zvol) => _deviceCountdown.GetValueOrDefault(zvol) <= 0;

    /// <summary>scstadmin -rem_target при target.delete: таргет уходит из SCST сразу, даже если потом reload сорвётся.</summary>
    private void RemoveTargetFromScst(string name)
    {
        if (IscsiServiceRunning)
        {
            _scstTargets.Remove(name);
        }
    }

    private sealed record ScstTarget(HashSet<string> Luns, HashSet<string> Initiators)
    {
        public bool Enabled => Luns.Count > 0;

        /// <summary>
        /// Строки группы — шаблоны SCST: <c>*</c>, <c>?</c>, ведущий <c>!</c>, регистр не различается; хватает одной
        /// подходящей. Своя реализация (регулярное выражение), а не <c>ScstWildcard</c>: иначе тесты сервера проверяли бы
        /// его разбор шаблонов им же самим.
        /// </summary>
        public bool Allows(string initiator) => Enabled && Initiators.Any(pattern => pattern.StartsWith('!')
            ? !Glob(pattern[1..], initiator)
            : Glob(pattern, initiator));

        private static bool Glob(string pattern, string name) =>
            Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    // ---- портал: discovery-сессия и SendTargets (RFC 7143), независимо от клиента сервера ----------------------------

    private void StartIscsiPortal()
    {
        _iscsi = new TcpListener(IPAddress.Loopback, 0);
        _iscsi.Start();
        IscsiPort = ((IPEndPoint)_iscsi.LocalEndpoint).Port;
        _ = Task.Run(AcceptIscsiAsync);
    }

    private void StopIscsiPortal()
    {
        _iscsiStop.Cancel();
        _iscsi?.Stop();
        _iscsiStop.Dispose();
    }

    private async Task AcceptIscsiAsync()
    {
        while (!_iscsiStop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _iscsi!.AcceptTcpClientAsync(_iscsiStop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeIscsiAsync(client));
        }
    }

    private async Task ServeIscsiAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            if (!IscsiServiceRunning || !TakeDiscoveryAnswer())
            {
                return; // служба остановлена или связь пропала — портал не отвечает
            }

            var initiator = "";
            var fullFeature = false;
            var statSn = 100u;
            var text = Array.Empty<byte>();
            var sent = 0;
            while (!_iscsiStop.IsCancellationRequested)
            {
                var bhs = new byte[48];
                if (await stream.ReadAtLeastAsync(bhs, bhs.Length, throwOnEndOfStream: false) < bhs.Length)
                {
                    return;
                }

                var ahs = bhs[4] * 4;
                var length = (bhs[5] << 16) | (bhs[6] << 8) | bhs[7];
                var rest = new byte[ahs + ((length + 3) & ~3)];
                await stream.ReadExactlyAsync(rest);
                var keys = Encoding.UTF8.GetString(rest, ahs, length)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(kv => kv.Split('=', 2))
                    .Where(kv => kv.Length == 2)
                    .ToDictionary(kv => kv[0], kv => kv[1]);
                var itt = BinaryPrimitives.ReadUInt32BigEndian(bhs.AsSpan(16));

                switch (bhs[0] & 0x3F)
                {
                    case 0x03: // Login Request
                    {
                        var transit = (bhs[1] & 0x80) != 0;
                        var csg = (bhs[1] >> 2) & 0x03;
                        var nsg = bhs[1] & 0x03;
                        if (keys.TryGetValue("InitiatorName", out var name))
                        {
                            initiator = name;
                            lock (_discoveryInitiators)
                            {
                                _discoveryInitiators.Add(name);
                            }
                        }

                        var response = IscsiResponse(0x23, itt, statSn++);
                        bhs.AsSpan(8, 6).CopyTo(response.AsSpan(8)); // ISID
                        (byte, byte) status = initiator == "" ? ((byte)0x02, (byte)0x07) // нет InitiatorName
                            : keys.TryGetValue("SessionType", out var type) && type != "Discovery" ? ((byte)0x02, (byte)0x03) // обычные сессии не нужны
                            : DiscoveryAuthRequired ? ((byte)0x02, (byte)0x01)
                            : ((byte)0, (byte)0);
                        (response[36], response[37]) = status;
                        if (status.Item1 != 0)
                        {
                            await SendIscsiAsync(stream, response, ReadOnlyMemory<byte>.Empty);
                            return;
                        }

                        response[1] = (byte)((transit ? 0x80 | nsg : 0) | (csg << 2));
                        if (transit && nsg == 3)
                        {
                            fullFeature = true;
                            response[15] = 1; // TSIH
                        }

                        var reply = csg == 0 ? "AuthMethod=None\0" : "HeaderDigest=None\0DataDigest=None\0";
                        await SendIscsiAsync(stream, response, Encoding.UTF8.GetBytes(reply));
                        break;
                    }

                    case 0x04: // Text Request
                    {
                        if (!fullFeature)
                        {
                            return;
                        }

                        if (BinaryPrimitives.ReadUInt32BigEndian(bhs.AsSpan(20)) == 0xFFFFFFFF)
                        {
                            text = keys.GetValueOrDefault("SendTargets") == "All" ? Encoding.UTF8.GetBytes(SendTargetsText(initiator)) : [];
                            sent = 0;
                        }

                        var chunk = DiscoveryTextChunk > 0 ? Math.Min(DiscoveryTextChunk, text.Length - sent) : text.Length - sent;
                        var final = sent + chunk >= text.Length;
                        var response = IscsiResponse(0x24, itt, statSn++);
                        response[1] = (byte)(final ? 0x80 : 0x40); // F или C
                        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(20), final ? 0xFFFFFFFF : 0x10);
                        await SendIscsiAsync(stream, response, text.AsMemory(sent, chunk));
                        sent += chunk;
                        break;
                    }

                    case 0x06: // Logout Request
                    {
                        var response = IscsiResponse(0x26, itt, statSn);
                        response[1] = 0x80;
                        await SendIscsiAsync(stream, response, ReadOnlyMemory<byte>.Empty);
                        return;
                    }

                    default:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // клиент ушёл
        }
    }

    private bool TakeDiscoveryAnswer()
    {
        lock (_discoveryInitiators)
        {
            if (_discoveryAnswersLeft is not { } left)
            {
                return true;
            }

            if (left <= 0)
            {
                return false;
            }

            _discoveryAnswersLeft = left - 1;
            return true;
        }
    }

    /// <summary>Как iscsi-scstd: только включённые таргеты, где у этого инициатора есть LUN.</summary>
    private string SendTargetsText(string initiator)
    {
        lock (_lock)
        {
            return string.Concat(_scstTargets
                .Where(t => t.Value.Allows(initiator))
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $"TargetName={IscsiBasename}:{t.Key}\0TargetAddress=127.0.0.1:{IscsiPort},1\0"));
        }
    }

    private static byte[] IscsiResponse(byte opcode, uint itt, uint statSn)
    {
        var bhs = new byte[48];
        bhs[0] = opcode;
        BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(16), itt);
        BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(24), statSn);
        BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28), 1); // ExpCmdSN
        BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(32), 64); // MaxCmdSN
        return bhs;
    }

    private static async Task SendIscsiAsync(Stream stream, byte[] bhs, ReadOnlyMemory<byte> data)
    {
        bhs[5] = (byte)(data.Length >> 16);
        bhs[6] = (byte)(data.Length >> 8);
        bhs[7] = (byte)data.Length;
        var pdu = new byte[48 + ((data.Length + 3) & ~3)];
        bhs.CopyTo(pdu, 0);
        data.Span.CopyTo(pdu.AsSpan(48));
        await stream.WriteAsync(pdu);
    }
}
