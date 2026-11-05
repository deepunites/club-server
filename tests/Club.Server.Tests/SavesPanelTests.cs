using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Club.Server.Saves;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// API панели для сохранений игроков (spec 2026-11-05, п. 3.5): общие настройки, список папок с проверкой шаблонов
/// путей, игроки и их удаление, состояние диска сохранений. Своя база и свой корень сохранений (с меткой) на каждый тест.
/// </summary>
public sealed class SavesPanelTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private const string PlayerId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-saves-panel-{Guid.NewGuid():N}");
    private readonly ServerFixture _server = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, SavesOptions.MarkerFile), "");
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["Saves:Enabled"] = "true";
        _server.Settings["Saves:Root"] = _root;
        await _server.InitializeAsync();
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

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return body;
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field, string reason)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Contract.AssertError(body, "validation", reason);
        Assert.Equal(field, body.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    private async Task<JsonElement> CreateEntryAsync(string name, string path, bool? enabled = null)
    {
        using var response = await Panel().PostAsJsonAsync("/panel/api/v1/saves/entries", new { name, path, enabled });
        return await OkAsync(response, HttpStatusCode.Created);
    }

    private async Task<TestMachine> MachineAsync()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, "02:00:00:00:50:01");
        return machine;
    }

    /// <summary>Сохранение игрока через API помощника (задача 4): выдача на ПК, затем приём архива: два файла одной папки.</summary>
    private async Task SaveThroughHelperAsync(TestMachine machine, Guid entryId, string playerId = PlayerId)
    {
        var http = _server.CreateClient();
        using (var attach = await machine.SendAsync(HttpMethod.Post, $"/diskless/v1/machines/{machine.MachineId}/saves/{playerId}/attach"))
        {
            // 204 — у игрока ещё ничего нет, 200 — архив прошлой версии.
            Assert.Contains(attach.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.OK });
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in new[] { ("Сохранение 1.sav", "прогресс"), ("user.cfg", "fov=90") })
            {
                using var stream = archive.CreateEntry($"{entryId:D}/{name}").Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/saves/{playerId}") { Content = new ByteArrayContent(buffer.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machine.AccessToken);
        using var put = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    public async Task Settings_roundtrip()
    {
        using (var defaults = await Panel().GetAsync("/panel/api/v1/saves/settings"))
        {
            var body = await OkAsync(defaults);
            Assert.Equal(200, body.GetProperty("limitMb").GetInt32());
            Assert.Equal(["Logs", "Crashes", "CrashDumps", "*.dmp"], body.GetProperty("excludes").EnumerateArray().Select(e => e.GetString()));
        }

        using (var put = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { limitMb = 500, excludes = new[] { " Logs ", "*.tmp", "Кэш" } }))
        {
            var body = await OkAsync(put);
            Assert.Equal(500, body.GetProperty("limitMb").GetInt32());
            Assert.Equal(["Logs", "*.tmp", "Кэш"], body.GetProperty("excludes").EnumerateArray().Select(e => e.GetString()));
        }

        // Поле без значения не меняется; пустой список исключений — можно.
        using (var limitOnly = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { limitMb = 2000 }))
        {
            await OkAsync(limitOnly);
        }

        var saved = await Repository.SettingsAsync();
        Assert.Equal(2000, saved.LimitMb);
        Assert.Equal(["Logs", "*.tmp", "Кэш"], saved.Excludes);
        using (var none = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { excludes = Array.Empty<string>() }))
        {
            await OkAsync(none);
        }

        using (var get = await Panel().GetAsync("/panel/api/v1/saves/settings"))
        {
            var body = await OkAsync(get);
            Assert.Equal(2000, body.GetProperty("limitMb").GetInt32());
            Assert.Empty(body.GetProperty("excludes").EnumerateArray());
        }

        foreach (var limitMb in new[] { 0, 2001, -5 })
        {
            using var bad = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { limitMb });
            await AssertValidationAsync(bad, "limitMb", "range");
        }

        using (var many = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { excludes = Enumerable.Range(0, 51).Select(i => $"x{i}").ToArray() }))
        {
            await AssertValidationAsync(many, "excludes", "tooMany");
        }

        foreach (var exclude in new[] { "", "   ", new string('a', 101) })
        {
            using var bad = await Panel().PutAsJsonAsync("/panel/api/v1/saves/settings", new { excludes = new[] { "Logs", exclude } });
            await AssertValidationAsync(bad, "excludes", "length");
        }

        // Отказ ничего не поменял.
        Assert.Equal(2000, (await Repository.SettingsAsync()).LimitMb);
        Assert.Empty((await Repository.SettingsAsync()).Excludes);
    }

    [Fact]
    public async Task Entry_path_validation_codes()
    {
        foreach (var (path, reason) in new[]
        {
            (@"C:\x", "drive"),
            (@"\\server\share\saves", "drive"),
            (@"Games\Saves", "drive"),
            (@"%TEMP%\Game", "badVariable"),
            (@"%APPDATA%\..\Game", "dotDot"),
            ("%USERPROFILE%", "root"),
            (@"G:\", "root"),
            (@"%APPDATA%\Game<1>", "invalidChars"),
            ("", "empty"),
            (@"G:\" + new string('a', 300), "tooLong"),
        })
        {
            using var response = await Panel().PostAsJsonAsync("/panel/api/v1/saves/entries", new { name = "Игра", path });
            await AssertValidationAsync(response, "path", reason);
        }

        using (var noPath = await Panel().PostAsJsonAsync("/panel/api/v1/saves/entries", new { name = "Игра" }))
        {
            await AssertValidationAsync(noPath, "path", "empty");
        }

        // G: — буква библиотеки сервера (Library:DriveLetter по умолчанию); кириллица и пробелы в пути — как есть.
        var library = await CreateEntryAsync("CS 1.6", @"G:\Игры\CS 1.6\cstrike");
        Assert.Equal(@"G:\Игры\CS 1.6\cstrike", library.GetProperty("path").GetString());
        var profile = await CreateEntryAsync("Witcher 3", @"%documents%\The Witcher 3");
        Assert.Equal(@"%documents%\The Witcher 3", profile.GetProperty("path").GetString());

        // Та же проверка при изменении: путь не меняется.
        using (var put = await Panel().PutAsJsonAsync($"/panel/api/v1/saves/entries/{library.GetProperty("id").GetGuid()}", new { path = @"D:\Games" }))
        {
            await AssertValidationAsync(put, "path", "drive");
        }

        Assert.Equal(2, (await Repository.EntriesAsync(enabledOnly: false)).Count);
        Assert.Contains(await Repository.EntriesAsync(enabledOnly: false), e => e.Path == @"G:\Игры\CS 1.6\cstrike");
    }

    [Fact]
    public async Task Entries_crud()
    {
        var witcher = await CreateEntryAsync("  Witcher 3 ", @"%DOCUMENTS%\The Witcher 3");
        Assert.Equal("Witcher 3", witcher.GetProperty("name").GetString());
        Assert.True(witcher.GetProperty("enabled").GetBoolean());
        var cs = await CreateEntryAsync("CS 1.6", @"G:\Games\CS 1.6\cstrike", enabled: false);
        Assert.False(cs.GetProperty("enabled").GetBoolean());
        var witcherId = witcher.GetProperty("id").GetGuid();
        var csId = cs.GetProperty("id").GetGuid();

        using (var list = await Panel().GetAsync("/panel/api/v1/saves/entries"))
        {
            // Все записи, и выключенные, — по названию.
            var body = await OkAsync(list);
            Assert.Equal(
                [(csId, "CS 1.6", @"G:\Games\CS 1.6\cstrike", false), (witcherId, "Witcher 3", @"%DOCUMENTS%\The Witcher 3", true)],
                body.EnumerateArray().Select(e => (e.GetProperty("id").GetGuid(), e.GetProperty("name").GetString(), e.GetProperty("path").GetString(), e.GetProperty("enabled").GetBoolean())));
        }

        // Изменение — только переданные поля.
        using (var enable = await Panel().PutAsJsonAsync($"/panel/api/v1/saves/entries/{csId}", new { enabled = true }))
        {
            var body = await OkAsync(enable);
            Assert.Equal(("CS 1.6", @"G:\Games\CS 1.6\cstrike", true), (body.GetProperty("name").GetString(), body.GetProperty("path").GetString(), body.GetProperty("enabled").GetBoolean()));
        }

        using (var rename = await Panel().PutAsJsonAsync($"/panel/api/v1/saves/entries/{witcherId}", new { name = "Ведьмак 3", path = @"%SAVEDGAMES%\Witcher 3" }))
        {
            await OkAsync(rename);
        }

        Assert.Equal(
            [new SaveEntry(csId, "CS 1.6", @"G:\Games\CS 1.6\cstrike", true), new SaveEntry(witcherId, "Ведьмак 3", @"%SAVEDGAMES%\Witcher 3", true)],
            await Repository.EntriesAsync(enabledOnly: false));

        foreach (var name in new[] { "", "   ", new string('я', 61) })
        {
            using var bad = await Panel().PostAsJsonAsync("/panel/api/v1/saves/entries", new { name, path = @"%APPDATA%\Game" });
            await AssertValidationAsync(bad, "name", "length");
        }

        using (var noName = await Panel().PostAsJsonAsync("/panel/api/v1/saves/entries", new { path = @"%APPDATA%\Game" }))
        {
            await AssertValidationAsync(noName, "name", "length");
        }

        using (var badRename = await Panel().PutAsJsonAsync($"/panel/api/v1/saves/entries/{csId}", new { name = " " }))
        {
            await AssertValidationAsync(badRename, "name", "length");
        }

        using (var delete = await Panel().DeleteAsync($"/panel/api/v1/saves/entries/{csId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.Equal([witcherId], (await Repository.EntriesAsync(enabledOnly: false)).Select(e => e.Id));
        using (var again = await Panel().DeleteAsync($"/panel/api/v1/saves/entries/{csId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            Contract.AssertError(await again.Content.ReadFromJsonAsync<JsonElement>(), "notFound");
        }

        using (var missing = await Panel().PutAsJsonAsync($"/panel/api/v1/saves/entries/{Guid.NewGuid()}", new { enabled = false }))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task Players_list_after_save()
    {
        var entry = await CreateEntryAsync("Игра", @"%APPDATA%\Game");
        var machine = await MachineAsync();
        await SaveThroughHelperAsync(machine, entry.GetProperty("id").GetGuid());

        using (var list = await Panel().GetAsync("/panel/api/v1/saves/players"))
        {
            var body = await OkAsync(list);
            Assert.Equal(1, body.GetProperty("total").GetInt32());
            Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
            var player = Assert.Single(body.GetProperty("items").EnumerateArray());
            Assert.Equal(PlayerId, player.GetProperty("playerId").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount("прогресс" + "fov=90"), player.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(2, player.GetProperty("fileCount").GetInt32());
            Assert.Equal(machine.MachineId, player.GetProperty("machineId").GetGuid());
            Assert.False(string.IsNullOrEmpty(player.GetProperty("machineName").GetString()));
            Assert.Equal("ok", player.GetProperty("lastResult").GetString());
            Assert.Equal(JsonValueKind.String, player.GetProperty("savedAt").ValueKind);
        }

        // Поиск — подстрока ID без учёта регистра; страница за концом — пустая, total тот же.
        foreach (var (url, count) in new[]
        {
            ("/panel/api/v1/saves/players?query=0F8FAD5B", 1),
            ("/panel/api/v1/saves/players?query=d9cb-469f&page=1", 1),
            ("/panel/api/v1/saves/players?query=ffff", 0),
            ("/panel/api/v1/saves/players?page=2", 0),
        })
        {
            using var response = await Panel().GetAsync(url);
            var body = await OkAsync(response);
            Assert.Equal(count, body.GetProperty("items").GetArrayLength());
            Assert.Equal(url.Contains("query=") ? count : 1, body.GetProperty("total").GetInt32());
        }

        // Занято на диске сохранений — уже не ноль.
        using var status = await Panel().GetAsync("/panel/api/v1/saves/status");
        var state = await OkAsync(status);
        Assert.True(state.GetProperty("usedBytes").GetInt64() >= Encoding.UTF8.GetByteCount("прогресс" + "fov=90"));
    }

    [Fact]
    public async Task Delete_player_removes_files_and_lease()
    {
        var entry = await CreateEntryAsync("Игра", @"%APPDATA%\Game");
        var entryId = entry.GetProperty("id").GetGuid();
        var machine = await MachineAsync();
        await SaveThroughHelperAsync(machine, entryId);
        await SaveThroughHelperAsync(machine, entryId); // вторая версия: первая уходит в .prev
        Assert.True(Directory.Exists(Path.Combine(_root, PlayerId)));
        Assert.True(Directory.Exists(Path.Combine(_root, PlayerId + ".prev")));
        await Repository.TakeLeaseAsync(PlayerId, machine.MachineId);

        // ID в другом регистре — тот же игрок.
        using (var delete = await Panel().DeleteAsync($"/panel/api/v1/saves/players/{PlayerId.ToUpperInvariant()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId + ".prev")));
        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
        Assert.Equal(0, (await Repository.PlayersAsync(null, 1, 50)).Total);
        Assert.True(File.Exists(Path.Combine(_root, SavesOptions.MarkerFile)));

        // Игрока уже нет — тоже 204.
        using (var again = await Panel().DeleteAsync($"/panel/api/v1/saves/players/{PlayerId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        foreach (var bad in new[] { "bad_id", "a.b", new string('a', 65) })
        {
            using var response = await Panel().DeleteAsync($"/panel/api/v1/saves/players/{Uri.EscapeDataString(bad)}");
            await AssertValidationAsync(response, "playerId", "format");
        }
    }

    [Fact]
    public async Task Status_and_unmounted_disk()
    {
        using (var status = await Panel().GetAsync("/panel/api/v1/saves/status"))
        {
            var body = await OkAsync(status);
            Assert.True(body.GetProperty("enabled").GetBoolean());
            Assert.True(body.GetProperty("available").GetBoolean());
            Assert.Equal(_root, body.GetProperty("root").GetString());
            Assert.Equal(0, body.GetProperty("usedBytes").GetInt64());
        }

        // Без метки диск не смонтирован: занятое не считается, удалить игрока нельзя (на системный диск ВМ ничего не пишется).
        File.Delete(Path.Combine(_root, SavesOptions.MarkerFile));
        using (var status = await Panel().GetAsync("/panel/api/v1/saves/status"))
        {
            var body = await OkAsync(status);
            Assert.True(body.GetProperty("enabled").GetBoolean());
            Assert.False(body.GetProperty("available").GetBoolean());
            Assert.Equal(0, body.GetProperty("usedBytes").GetInt64());
        }

        using (var delete = await Panel().DeleteAsync($"/panel/api/v1/saves/players/{PlayerId}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.StatusCode);
            Contract.AssertError(await delete.Content.ReadFromJsonAsync<JsonElement>(), "serverUnavailable", "savesUnavailable");
        }

        Assert.False(Directory.Exists(Path.Combine(_root, ".tmp")));

        // Настройки и папки от диска не зависят.
        using var settings = await Panel().GetAsync("/panel/api/v1/saves/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    [Fact]
    public async Task Requires_panel_token()
    {
        var anonymous = _server.CreateClient();
        var wrong = _server.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var id = Guid.NewGuid();
        foreach (var http in new[] { anonymous, wrong })
        {
            foreach (var request in new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "/panel/api/v1/saves/settings"),
                new HttpRequestMessage(HttpMethod.Put, "/panel/api/v1/saves/settings") { Content = JsonContent.Create(new { limitMb = 10 }) },
                new HttpRequestMessage(HttpMethod.Get, "/panel/api/v1/saves/entries"),
                new HttpRequestMessage(HttpMethod.Post, "/panel/api/v1/saves/entries") { Content = JsonContent.Create(new { name = "x", path = @"%APPDATA%\x" }) },
                new HttpRequestMessage(HttpMethod.Put, $"/panel/api/v1/saves/entries/{id}") { Content = JsonContent.Create(new { enabled = false }) },
                new HttpRequestMessage(HttpMethod.Delete, $"/panel/api/v1/saves/entries/{id}"),
                new HttpRequestMessage(HttpMethod.Get, "/panel/api/v1/saves/players"),
                new HttpRequestMessage(HttpMethod.Delete, $"/panel/api/v1/saves/players/{PlayerId}"),
                new HttpRequestMessage(HttpMethod.Get, "/panel/api/v1/saves/status"),
            })
            {
                using var response = await http.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{request.Method} {request.RequestUri}: {response.StatusCode}");
                Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "panelToken");
            }
        }

        Assert.Equal(200, (await Repository.SettingsAsync()).LimitMb);
        Assert.Empty(await Repository.EntriesAsync(enabledOnly: false));
    }
}

/// <summary>Буквы библиотек для шаблонов путей — из настроек всех библиотек сервера: H: (solo) рядом с G:.</summary>
public sealed class SavesPanelLibraryLettersTests
{
    [Fact]
    public async Task Entry_path_on_extra_library_letter_is_accepted()
    {
        await using var stand = await SoloStand.StartAsync();
        var http = stand.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SoloStand.PanelToken);
        foreach (var path in new[] { @"H:\Игры\CS 1.6\cstrike", @"g:\Games\Saves" })
        {
            using var response = await http.PostAsJsonAsync("/panel/api/v1/saves/entries", new { name = "Игра", path });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var other = await http.PostAsJsonAsync("/panel/api/v1/saves/entries", new { name = "Игра", path = @"K:\Games" });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Contract.AssertError(await other.Content.ReadFromJsonAsync<JsonElement>(), "validation", "drive");
    }
}
