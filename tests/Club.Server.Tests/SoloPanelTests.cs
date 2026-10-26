using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Club.Server.Tests;

/// <summary>
/// API панели для двух библиотек: список <c>/libraries</c>, экран H: по <c>/libraries/solo/…</c> с теми же ответами, что
/// <c>/library</c> у G: (G: по старым путям не меняется), ПК и мастер-том H: — по отчётам H:, библиотеки на «Рабочих станциях».
/// </summary>
public sealed class SoloPanelTests : IAsyncLifetime
{
    private SoloStand _stand = null!;

    public async Task InitializeAsync() => _stand = await SoloStand.StartAsync();

    public async Task DisposeAsync() => await _stand.DisposeAsync();

    private async Task<JsonElement> GetAsync(string path, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await _stand.Panel().GetAsync($"/panel/api/v1{path}");
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body = null)
    {
        using var response = await _stand.Panel().PostAsJsonAsync($"/panel/api/v1{path}", body ?? new { });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static (int, int, int, int, int, int) Adoption(JsonElement overview)
    {
        var m = overview.GetProperty("machines");
        return (m.GetProperty("online").GetInt32(), m.GetProperty("onCurrent").GetInt32(), m.GetProperty("onOlder").GetInt32(),
            m.GetProperty("switchPending").GetInt32(), m.GetProperty("failed").GetInt32(), m.GetProperty("notMounted").GetInt32());
    }

    [Fact]
    public async Task Libraries_lists_g_then_h()
    {
        var list = (await GetAsync("/libraries")).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(("games", "Online Games", true, "G", "G", "1.5.0", "/library"), Info(list[0]));
        Assert.Equal(("solo", "Offline Games", true, "H", "H", "1.6.0", "/libraries/solo"), Info(list[1]));

        // Выключенная библиотека в списке остаётся (enabled=false), её экран — 404.
        _stand.Libraries.Extras.Single().Options.Enabled = false;
        Assert.False((await GetAsync("/libraries"))[1].GetProperty("enabled").GetBoolean());
        Assert.Equal("library", (await GetAsync("/libraries/solo", HttpStatusCode.NotFound)).GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        static (string?, string?, bool, string?, string?, string?, string?) Info(JsonElement e) => (
            e.GetProperty("key").GetString(), e.GetProperty("title").GetString(), e.GetProperty("enabled").GetBoolean(), e.GetProperty("driveLetter").GetString(),
            e.GetProperty("masterDriveLetter").GetString(), e.GetProperty("minHelper").GetString(), e.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Without_h_the_list_has_only_g_and_machines_have_no_libraries()
    {
        await using var stand = await SoloStand.StartAsync(solo: false);
        var (pc, _) = await TestMachine.RegisterAsync(stand.Server.CreateClient());
        using var http = stand.Panel();
        var list = await http.GetFromJsonAsync<JsonElement>("/panel/api/v1/libraries");
        Assert.Equal(["games"], list.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToArray());
        using (var solo = await http.GetAsync("/panel/api/v1/libraries/solo"))
        {
            Assert.Equal(HttpStatusCode.NotFound, solo.StatusCode);
        }

        var machines = await http.GetFromJsonAsync<JsonElement>("/panel/api/v1/machines");
        Assert.False(machines.TryGetProperty("libraries", out _));
        Assert.False(machines.GetProperty("machines")[0].TryGetProperty("libraries", out _));
    }

    [Fact]
    public async Task Games_key_and_old_paths_are_the_same_g_screen()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.RegisterAsync(1);
        var library = await GetAsync("/library");
        Assert.Equal(library.ToString(), (await GetAsync("/libraries/games")).ToString());
        Assert.Equal((await GetAsync("/library/operations")).ToString(), (await GetAsync("/libraries/games/operations")).ToString());

        // У G: в кандидатах правки нет supported: ответ /library не изменился.
        Assert.False(library.GetProperty("masterCandidates")[0].TryGetProperty("supported", out _));
        Assert.Equal(404, (int)(await PostAsync("/libraries/nope/rollback")).Status);
    }

    [Fact]
    public async Task H_screen_publishes_and_rolls_back_its_own_versions_with_the_same_shapes()
    {
        var (status, accepted) = await PostAsync("/libraries/solo/versions", new { label = "v1" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        var operationId = accepted.GetProperty("operationId").GetGuid();
        var queued = await GetAsync("/libraries/solo");
        Assert.Equal(operationId, queued.GetProperty("openOperations")[0].GetProperty("id").GetGuid());
        await _stand.PassAsync(_stand.Solo);

        var h = await GetAsync("/libraries/solo");
        Assert.Equal(("v1", "current"), (h.GetProperty("current").GetProperty("label").GetString(), h.GetProperty("current").GetProperty("role").GetString()));
        Assert.Equal("H", h.GetProperty("master").GetProperty("driveLetter").GetString());
        var operation = await GetAsync($"/libraries/solo/operations/{operationId}");
        Assert.Equal(("done", "v1"), (operation.GetProperty("status").GetString(), operation.GetProperty("versionLabel").GetString()));
        Assert.Equal(operationId, (await GetAsync("/libraries/solo/operations?limit=5"))[0].GetProperty("id").GetGuid());

        // G: этой версии не видит: ни версии, ни операции.
        var g = await GetAsync("/library");
        Assert.False(g.TryGetProperty("current", out _));
        Assert.Equal(0, g.GetProperty("versions").GetArrayLength());
        await GetAsync($"/library/operations/{operationId}", HttpStatusCode.NotFound);

        // Те же поля, что у экрана G:.
        await _stand.PublishAsync(_stand.Games, "v1");
        g = await GetAsync("/library");
        Assert.Equal(Names(g), Names(h));
        Assert.Equal(Names(g.GetProperty("current")), Names(h.GetProperty("current")));
        Assert.Equal(Names(g.GetProperty("master")), Names(h.GetProperty("master")));
        Assert.Equal(Names(g.GetProperty("machines")), Names(h.GetProperty("machines")));
        Assert.Equal(Names((await GetAsync("/library/operations"))[0]), Names(operation));

        var (refused, conflict) = await PostAsync("/libraries/solo/rollback");
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Contract.AssertError(conflict, "conflict", "noRollback");
        Assert.Equal("exists", (await PostAsync("/libraries/solo/versions", new { label = "v1" })).Body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/libraries/solo/operations/{operationId}/retry")).Status); // выполнена — notFailed
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/libraries/solo/operations/{Guid.NewGuid()}/retry")).Status);

        await _stand.PublishAsync(_stand.Solo, "v2");
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/libraries/solo/rollback")).Status);
        await _stand.PassAsync(_stand.Solo);
        Assert.Equal("v1", (await GetAsync("/libraries/solo")).GetProperty("current").GetProperty("label").GetString());
        Assert.Equal("v1", (await GetAsync("/library")).GetProperty("current").GetProperty("label").GetString());
    }

    [Fact]
    public async Task H_screen_counts_pcs_and_shows_its_master_from_h_reports()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1);
        var old = await _stand.RegisterAsync(2, helperVersion: "1.5.1");
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport("mounted", "v1")))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        // H: — по отчётам H: (ПК 1.5.1 их не шлёт: на связи, но тома H: нет); G: — по своим.
        var h = await GetAsync("/libraries/solo");
        Assert.Equal((2, 1, 0, 0, 0, 1), Adoption(h));
        Assert.Equal(1, h.GetProperty("current").GetProperty("mountedOn").GetInt32());
        var g = await GetAsync("/library");
        Assert.Equal((2, 0, 0, 0, 0, 2), Adoption(g));
        Assert.Equal(0, g.GetProperty("current").GetProperty("mountedOn").GetInt32());

        var candidates = h.GetProperty("masterCandidates").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetGuid(), c => c.GetProperty("supported").GetBoolean());
        Assert.Equal((true, false), (candidates[pc.MachineId], candidates[old.MachineId]));

        // Правка H: — только помощнику 1.6.0+.
        var (tooOld, refusal) = await PostAsync("/libraries/solo/master/open", new { machineId = old.MachineId });
        Assert.Equal(HttpStatusCode.Conflict, tooOld);
        Contract.AssertError(refusal, "conflict", "helperTooOld");
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/libraries/solo/master/open", new { machineId = pc.MachineId })).Status);
        await _stand.PassAsync(_stand.Solo);
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport(master: new { state = "failed", error = "drive letter H: is held by the library volume" })))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        var master = (await GetAsync("/libraries/solo")).GetProperty("master");
        Assert.Equal(("open", "PC-01", "failed", "drive letter H: is held by the library volume", "H"), (
            master.GetProperty("state").GetString(), master.GetProperty("machineName").GetString(), master.GetProperty("machineState").GetString(),
            master.GetProperty("machineError").GetString(), master.GetProperty("driveLetter").GetString()));
        var gMaster = (await GetAsync("/library")).GetProperty("master");
        Assert.Equal("closed", gMaster.GetProperty("state").GetString());
        Assert.False(gMaster.TryGetProperty("machineState", out _));

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/libraries/solo/master/close", new { force = true })).Status);
        await _stand.PassAsync(_stand.Solo);
        Assert.Equal("closed", (await GetAsync("/libraries/solo")).GetProperty("master").GetProperty("state").GetString());
    }

    [Fact]
    public async Task H_warnings_show_on_the_h_screen_only_and_without_the_key_prefix()
    {
        await _stand.Solo.Repository.ReplaceWarningsAsync([("retireBlocked", "solo:v1", "Version v1 is still in use")], DateTimeOffset.UtcNow, "solo");
        var warning = Assert.Single((await GetAsync("/libraries/solo")).GetProperty("warnings").EnumerateArray());
        Assert.Equal(("retireBlocked", "v1"), (warning.GetProperty("kind").GetString(), warning.GetProperty("subject").GetString()));
        Assert.Equal(0, (await GetAsync("/library")).GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, (await GetAsync("/storage/warnings")).GetArrayLength());
    }

    [Fact]
    public async Task Machines_screen_shows_h_per_pc_from_h_reports()
    {
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1);
        var old = await _stand.RegisterAsync(2, helperVersion: "1.5.1");
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport("mounted", "v1")))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        async Task<Dictionary<Guid, JsonElement>> SoloByMachineAsync()
        {
            var overview = await GetAsync("/machines");
            return overview.GetProperty("machines").EnumerateArray()
                .ToDictionary(m => m.GetProperty("id").GetGuid(), m => Assert.Single(m.GetProperty("libraries").EnumerateArray()));
        }

        var rows = await SoloByMachineAsync();
        var mine = rows[pc.MachineId];
        Assert.Equal(("solo", "H", true), (mine.GetProperty("key").GetString(), mine.GetProperty("driveLetter").GetString(), mine.GetProperty("supported").GetBoolean()));
        var volume = mine.GetProperty("volume");
        Assert.Equal(("mounted", "v1", false, true), (volume.GetProperty("state").GetString(), volume.GetProperty("libraryVersion").GetString(),
            volume.GetProperty("outdated").GetBoolean(), volume.GetProperty("personal").GetBoolean()));
        Assert.False(volume.TryGetProperty("readOnlyVerified", out _));
        Assert.False(mine.TryGetProperty("masterState", out _));
        var theirs = rows[old.MachineId];
        Assert.Equal((false, "none"), (theirs.GetProperty("supported").GetBoolean(), theirs.GetProperty("volume").GetProperty("state").GetString()));

        // Новая версия H: — ПК на старой отмечен outdated; мастер-том H: — своей отметкой.
        await _stand.PublishAsync(_stand.Solo, "v2");
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport("mounted", "v1", master: new { state = "mounting", error = "waiting for H:" })))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        mine = (await SoloByMachineAsync())[pc.MachineId];
        Assert.True(mine.GetProperty("volume").GetProperty("outdated").GetBoolean());
        Assert.Equal(("mounting", "waiting for H:"), (mine.GetProperty("masterState").GetString(), mine.GetProperty("masterError").GetString()));

        var library = Assert.Single((await GetAsync("/machines")).GetProperty("libraries").EnumerateArray());
        Assert.Equal(("solo", "Offline Games", "v2", "H"), (library.GetProperty("key").GetString(), library.GetProperty("title").GetString(),
            library.GetProperty("currentVersion").GetString(), library.GetProperty("masterDriveLetter").GetString()));

        // Столбцы G: машины — по отчёту G:, H: их не трогает.
        var machine = (await GetAsync("/machines")).GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == pc.MachineId);
        Assert.Equal("none", machine.GetProperty("volume").GetProperty("state").GetString());
        Assert.False(machine.TryGetProperty("masterState", out _));
    }
}
