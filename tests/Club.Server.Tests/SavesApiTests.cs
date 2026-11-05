using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Club.Server.Saves;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// API сохранений для помощника (spec 2026-11-05, п. 3.3 и 3.4): список папок, выдача с арендой, приём с проверкой
/// аренды, лимита и архива, снятие аренды. Своя база и свой корень сохранений (с меткой) на каждый тест.
/// </summary>
public sealed class SavesApiTests : IAsyncLifetime
{
    private const string PlayerId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-saves-api-{Guid.NewGuid():N}");
    private readonly ServerFixture _server = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, SavesOptions.MarkerFile), "");
        _server.Settings["Saves:Enabled"] = "true";
        _server.Settings["Saves:Root"] = _root;
        await _server.InitializeAsync();
        _http = _server.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SavesRepository Repository => _server.Services.GetRequiredService<SavesRepository>();

    private Task<(TestMachine Machine, JsonElement Response)> MachineAsync(string mac) => TestMachine.RegisterAsync(_http, null, mac);

    private static string SavesPath(TestMachine machine, string playerId = PlayerId) => $"/diskless/v1/machines/{machine.MachineId}/saves/{playerId}";

    private static Task<HttpResponseMessage> AttachAsync(TestMachine machine, string playerId = PlayerId) =>
        machine.SendAsync(HttpMethod.Post, SavesPath(machine, playerId) + "/attach");

    /// <summary>PUT архива; <paramref name="chunked"/> — тело без Content-Length (потоком), как при упаковке на лету.</summary>
    private Task<HttpResponseMessage> PutAsync(TestMachine machine, byte[] zip, string playerId = PlayerId, TestMachine? token = null, bool chunked = false) =>
        PutAsync(_http, machine, zip, playerId, token, chunked);

    private static Task<HttpResponseMessage> PutAsync(HttpClient http, TestMachine machine, byte[] zip, string playerId = PlayerId, TestMachine? token = null,
        bool chunked = false)
    {
        HttpContent content = chunked ? new StreamContent(new Unseekable(zip)) : new ByteArrayContent(zip);
        var request = new HttpRequestMessage(HttpMethod.Put, SavesPath(machine, playerId)) { Content = content };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (token ?? machine).AccessToken);
        return http.SendAsync(request);
    }

    /// <summary>Zip из пар «имя записи — содержимое» (имена как есть: и плохие, для проверки приёма).</summary>
    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in files)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static (string, byte[]) Saved(Guid entryId, string relative, string content) => ($"{entryId:D}/{relative}", Encoding.UTF8.GetBytes(content));

    /// <summary>Записи архива выдачи: имя → содержимое.</summary>
    private static async Task<Dictionary<string, string>> ReadZipAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        await using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var files = new Dictionary<string, string>();
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[entry.FullName] = await reader.ReadToEndAsync();
        }

        return files;
    }

    /// <summary>Заголовок X-Club-Saves-Config: base64 от UTF-8 JSON — тот же SavesConfig, что у GET …/saves/config.</summary>
    private static JsonElement ConfigHeader(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(SavesEndpoints.ConfigHeader, out var values), "no config header");
        var config = JsonDocument.Parse(Convert.FromBase64String(Assert.Single(values))).RootElement.Clone();
        Contract.AssertMatches("SavesConfig", config);
        return config;
    }

    private async Task<PlayerSaveRow?> PlayerAsync(string playerId = PlayerId) =>
        (await Repository.PlayersAsync(playerId, 1, 50)).Items.SingleOrDefault(p => p.PlayerId == playerId);

    [Fact]
    public async Task Config_lists_enabled_entries()
    {
        var witcher = await Repository.CreateEntryAsync("Witcher 3", @"%DOCUMENTS%\The Witcher 3", enabled: true);
        var cs = await Repository.CreateEntryAsync("CS 1.6", @"H:\Игры\CS 1.6\cstrike", enabled: true);
        await Repository.CreateEntryAsync("Выключена", @"%APPDATA%\Off", enabled: false);
        var (machine, _) = await MachineAsync("02:00:00:00:40:01");

        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/saves/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Contract.ReadAsync(response, "SavesConfig");
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(200, body.GetProperty("limitMb").GetInt32());
        Assert.Equal(["Logs", "Crashes", "CrashDumps", "*.dmp"], body.GetProperty("excludes").EnumerateArray().Select(e => e.GetString()));
        // Только включённые, по названию; у каждой — id и шаблон пути (кириллица как есть).
        Assert.Equal(
            [(cs.Id, cs.Path), (witcher.Id, witcher.Path)],
            body.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("id").GetGuid(), e.GetProperty("path").GetString()!)));
    }

    [Fact]
    public async Task Attach_without_data_is_204_with_config_header()
    {
        var entry = await Repository.CreateEntryAsync("Игра", @"H:\Игры\Сохранения", enabled: true);
        var (machine, _) = await MachineAsync("02:00:00:00:40:11");

        using var response = await AttachAsync(machine);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var config = ConfigHeader(response);
        Assert.True(config.GetProperty("enabled").GetBoolean());
        var only = Assert.Single(config.GetProperty("entries").EnumerateArray());
        Assert.Equal(entry.Id, only.GetProperty("id").GetGuid());
        Assert.Equal(@"H:\Игры\Сохранения", only.GetProperty("path").GetString());

        // Заголовок — то же, что GET …/saves/config.
        using var plain = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/saves/config");
        Assert.Equal((await plain.Content.ReadFromJsonAsync<JsonElement>()).ToString(), config.ToString());

        // Аренда записана: игрок на этом ПК.
        Assert.Equal(machine.MachineId, await Repository.LeaseHolderAsync(PlayerId));
    }

    [Fact]
    public async Task Save_then_attach_on_other_machine_returns_files()
    {
        var profile = await Repository.CreateEntryAsync("Witcher 3", @"%DOCUMENTS%\The Witcher 3", enabled: true);
        var library = await Repository.CreateEntryAsync("CS 1.6", @"H:\Игры\CS 1.6\cstrike", enabled: true);
        var (a, _) = await MachineAsync("02:00:00:00:40:21");
        var (b, _) = await MachineAsync("02:00:00:00:40:22");

        using (var attach = await AttachAsync(a))
        {
            Assert.Equal(HttpStatusCode.NoContent, attach.StatusCode);
        }

        var zip = Zip(
            Saved(profile.Id, "gamesaves/Сохранение 1.sav", "прогресс"),
            Saved(profile.Id, "user.settings", "fov=90"),
            Saved(library.Id, "config.cfg", "name Игрок"));
        using (var put = await PutAsync(a, zip))
        {
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
        var row = await PlayerAsync();
        Assert.NotNull(row);
        Assert.Equal(3, row.FileCount);
        Assert.Equal(Encoding.UTF8.GetByteCount("прогресс" + "fov=90" + "name Игрок"), row.SizeBytes);
        Assert.Equal(a.MachineId, row.MachineId);
        Assert.Equal("ok", row.LastResult);
        Assert.NotNull(row.SavedAt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));

        using var other = await AttachAsync(b);
        ConfigHeader(other);
        var files = await ReadZipAsync(other);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [$"{profile.Id:D}/gamesaves/Сохранение 1.sav"] = "прогресс",
                [$"{profile.Id:D}/user.settings"] = "fov=90",
                [$"{library.Id:D}/config.cfg"] = "name Игрок",
            },
            files);
        Assert.Equal(b.MachineId, await Repository.LeaseHolderAsync(PlayerId));
    }

    [Fact]
    public async Task Put_without_lease_is_409_and_recorded()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (machine, _) = await MachineAsync("02:00:00:00:40:31");

        using var response = await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "x")));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "conflict", "notAttached");

        var row = await PlayerAsync();
        Assert.NotNull(row);
        Assert.Equal("rejected", row.LastResult);
        Assert.Null(row.SavedAt);
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
    }

    [Fact]
    public async Task Put_after_takeover_is_409()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (a, _) = await MachineAsync("02:00:00:00:40:41");
        var (b, _) = await MachineAsync("02:00:00:00:40:42");
        (await AttachAsync(a)).Dispose();
        (await AttachAsync(b)).Dispose();

        // ПК A пропал, игрок вошёл на B: опоздавшее сохранение с A не принимается и аренду B не снимает.
        using (var late = await PutAsync(a, Zip(Saved(entry.Id, "save.dat", "from A"))))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Contract.AssertError(await late.Content.ReadFromJsonAsync<JsonElement>(), "conflict", "notAttached");
        }

        Assert.Equal(b.MachineId, await Repository.LeaseHolderAsync(PlayerId));
        using (var fresh = await PutAsync(b, Zip(Saved(entry.Id, "save.dat", "from B"))))
        {
            Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
        }

        using var attach = await AttachAsync(a);
        Assert.Equal("from B", (await ReadZipAsync(attach))[$"{entry.Id:D}/save.dat"]);
    }

    [Fact]
    public async Task Put_over_limit_is_413_previous_kept()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        await Repository.UpdateSettingsAsync(new SaveSettings(1, []));
        var (machine, _) = await MachineAsync("02:00:00:00:40:51");
        (await AttachAsync(machine)).Dispose();
        (await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "good")))).Dispose();

        // Распакованное больше лимита (сжимается в маленький архив) и само тело больше лимита + 10 % — с Content-Length
        // и потоком без него (тогда считает сервер, читая тело).
        var unpacked = Zip(($"{entry.Id:D}/big.bin", new byte[2 * 1024 * 1024]));
        var body = Zip(($"{entry.Id:D}/random.bin", RandomNumberGenerator.GetBytes(2 * 1024 * 1024)));
        Assert.True(unpacked.Length < 1024 * 1024);
        foreach (var (zip, chunked) in new[] { (unpacked, false), (body, false), (body, true) })
        {
            (await AttachAsync(machine)).Dispose();
            using var response = await PutAsync(machine, zip, chunked: chunked);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation", "overLimit");

            // Аренда снята, прошлая версия на месте и в списке — с отметкой overLimit.
            Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
            var row = await PlayerAsync();
            Assert.NotNull(row);
            Assert.Equal("overLimit", row.LastResult);
            Assert.Equal(1, row.FileCount);
            Assert.Equal(4, row.SizeBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));
        }

        using var attach = await AttachAsync(machine);
        Assert.Equal(new Dictionary<string, string> { [$"{entry.Id:D}/save.dat"] = "good" }, await ReadZipAsync(attach));
    }

    [Fact]
    public async Task Put_bad_archive_is_400()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (machine, _) = await MachineAsync("02:00:00:00:40:61");
        (await AttachAsync(machine)).Dispose();

        var bad = new[]
        {
            Encoding.UTF8.GetBytes("not a zip"),
            Zip(("../evil.txt", [1])),
            Zip(($"{entry.Id:D}/../evil.txt", [1])),
            Zip(("/etc/evil.txt", [1])),
            Zip(($"{entry.Id:D}/C:/evil.txt", [1])),
            Zip(($"{entry.Id:D}/CON", [1])),
            Zip(("save.dat", [1])),
        };
        foreach (var zip in bad)
        {
            using var response = await PutAsync(machine, zip);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation", "badArchive");
        }

        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));

        // Плохой архив аренду не снимает: исправленное сохранение с того же ПК принимается.
        Assert.Equal(machine.MachineId, await Repository.LeaseHolderAsync(PlayerId));
        using var good = await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "ok")));
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task Put_unknown_entry_is_400()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var off = await Repository.CreateEntryAsync("Off", @"%APPDATA%\Off", enabled: false);
        var (machine, _) = await MachineAsync("02:00:00:00:40:71");
        (await AttachAsync(machine)).Dispose();

        // Записи нет в списке (удалили в панели) или она выключена — весь архив не принимается.
        foreach (var unknown in new[] { Guid.NewGuid(), off.Id })
        {
            using var response = await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "x"), Saved(unknown, "save.dat", "y")));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation", "badArchive");
        }

        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
    }

    [Fact]
    public async Task Player_id_case_is_one_folder()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (machine, _) = await MachineAsync("02:00:00:00:40:81");

        (await AttachAsync(machine, PlayerId.ToUpperInvariant())).Dispose();
        Assert.Equal(machine.MachineId, await Repository.LeaseHolderAsync(PlayerId));
        using (var put = await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "x")), PlayerId))
        {
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        Assert.True(Directory.Exists(Path.Combine(_root, PlayerId)));
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId.ToUpperInvariant())));
        using (var again = await AttachAsync(machine, PlayerId.ToUpperInvariant()))
        {
            Assert.Equal("x", (await ReadZipAsync(again))[$"{entry.Id:D}/save.dat"]);
        }

        // ID не по формату — 400 на каждом пути.
        foreach (var bad in new[] { "player_1", "игрок", new string('a', 65) })
        {
            using var attach = await AttachAsync(machine, bad);
            Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
            Contract.AssertError(await attach.Content.ReadFromJsonAsync<JsonElement>(), "validation");
            using var put = await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "x")), bad);
            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            using var lease = await machine.SendAsync(HttpMethod.Delete, SavesPath(machine, bad) + "/lease");
            Assert.Equal(HttpStatusCode.BadRequest, lease.StatusCode);
        }
    }

    [Fact]
    public async Task Release_lease_without_saving()
    {
        var (a, _) = await MachineAsync("02:00:00:00:40:91");
        var (b, _) = await MachineAsync("02:00:00:00:40:92");
        (await AttachAsync(a)).Dispose();

        // Чужую аренду не снимает (ответ тот же — помощнику нечего делать иначе).
        using (var other = await b.SendAsync(HttpMethod.Delete, SavesPath(b) + "/lease"))
        {
            Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        }

        Assert.Equal(a.MachineId, await Repository.LeaseHolderAsync(PlayerId));
        using (var own = await a.SendAsync(HttpMethod.Delete, SavesPath(a) + "/lease"))
        {
            Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        }

        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
    }

    /// <summary>
    /// Настоящий Kestrel (TestServer лимита тела не знает): его лимит по умолчанию — 30 МБ, на PUT он поднимается до
    /// лимита сохранений + 10 %. Тело в 31 МБ принимается — с Content-Length и потоком; тело больше лимита + 10 % потоком —
    /// 413 <c>overLimit</c> (Kestrel обрывает чтение сам).
    /// </summary>
    [Fact]
    public async Task Put_on_kestrel_raises_body_limit_to_saves_limit()
    {
        var kestrel = new ServerFixture();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        kestrel.Settings["Saves:Enabled"] = "true";
        kestrel.Settings["Saves:Root"] = _root;
        kestrel.UseKestrel(port);
        await kestrel.InitializeAsync();
        try
        {
            kestrel.StartServer();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var repository = kestrel.Services.GetRequiredService<SavesRepository>();
            var entry = await repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
            var (machine, _) = await TestMachine.RegisterAsync(http, null, "02:00:00:00:40:c1");

            const int Size = 31 * 1000 * 1000;
            byte[] large;
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                using (var stream = archive.CreateEntry($"{entry.Id:D}/big.bin", CompressionLevel.NoCompression).Open())
                {
                    stream.Write(new byte[Size]);
                }

                large = buffer.ToArray();
            }

            Assert.True(large.Length > 30_000_000);
            foreach (var chunked in new[] { false, true })
            {
                (await machine.SendAsync(HttpMethod.Post, SavesPath(machine) + "/attach")).Dispose();
                using var put = await PutAsync(http, machine, large, chunked: chunked);
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            }

            var row = (await repository.PlayersAsync(PlayerId, 1, 50)).Items.Single();
            Assert.Equal(Size, row.SizeBytes);

            await repository.UpdateSettingsAsync(new SaveSettings(1, []));
            (await machine.SendAsync(HttpMethod.Post, SavesPath(machine) + "/attach")).Dispose();
            using var over = await PutAsync(http, machine, Zip(($"{entry.Id:D}/random.bin", RandomNumberGenerator.GetBytes(2 * 1024 * 1024))), chunked: true);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
            Contract.AssertError(await over.Content.ReadFromJsonAsync<JsonElement>(), "validation", "overLimit");
            Assert.Null(await repository.LeaseHolderAsync(PlayerId));
            Assert.Equal(Size, (await repository.PlayersAsync(PlayerId, 1, 50)).Items.Single().SizeBytes);
        }
        finally
        {
            await ((IAsyncLifetime)kestrel).DisposeAsync();
        }
    }

    [Fact]
    public async Task Unavailable_without_marker_is_503()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (machine, _) = await MachineAsync("02:00:00:00:40:a1");
        File.Delete(Path.Combine(_root, SavesOptions.MarkerFile));

        // Диск не смонтирован: список отвечает, но выключено; остальное — 503, на диск ВМ ничего не пишется.
        using (var config = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/saves/config"))
        {
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);
            Assert.False((await Contract.ReadAsync(config, "SavesConfig")).GetProperty("enabled").GetBoolean());
        }

        var responses = new[]
        {
            await AttachAsync(machine),
            await PutAsync(machine, Zip(Saved(entry.Id, "save.dat", "x"))),
            await machine.SendAsync(HttpMethod.Delete, SavesPath(machine) + "/lease"),
        };
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "serverUnavailable", "savesUnavailable");
            }
        }

        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Other_machine_token_is_403()
    {
        var entry = await Repository.CreateEntryAsync("Game", @"%APPDATA%\Game", enabled: true);
        var (a, _) = await MachineAsync("02:00:00:00:40:b1");
        var (b, _) = await MachineAsync("02:00:00:00:40:b2");
        (await AttachAsync(a)).Dispose();

        var responses = new[]
        {
            await b.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{a.MachineId}/saves/config"),
            await b.SendAsync(HttpMethod.Post, SavesPath(a) + "/attach"),
            await PutAsync(a, Zip(Saved(entry.Id, "save.dat", "x")), token: b),
            await b.SendAsync(HttpMethod.Delete, SavesPath(a) + "/lease"),
        };
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "forbidden", "otherMachine");
            }
        }

        Assert.Equal(a.MachineId, await Repository.LeaseHolderAsync(PlayerId));

        // Без токена — 401.
        using var anonymous = await _http.PostAsync(SavesPath(a) + "/attach", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    /// <summary>Поток без длины: StreamContent не узнает размер и не пошлёт Content-Length.</summary>
    private sealed class Unseekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
