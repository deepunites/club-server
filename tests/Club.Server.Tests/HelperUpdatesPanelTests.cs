using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Data;
using Club.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Club.Server.HelperUpdates.HelperUpdateCodes;

namespace Club.Server.Tests;

/// <summary>
/// API панели для самообновления помощника (spec 2026-11-06, п. 3.5): сборки из <c>tools</c>, запуск по номерам и «все
/// гибриды», очередь, продолжение и отмена; поля помощника в строке машины (<c>helperBuild</c>, <c>helperUpdate</c>,
/// <c>helperUpdatable</c>, <c>helperUpdateBlock</c>). Сервер на тест — свой (своя база и каталог <c>tools</c>), работник
/// очереди выключен (ServerFixture).
/// </summary>
public sealed class HelperUpdatesPanelTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private const string Build181 = "ClubDisklessHelper-1.8.1.exe";
    private const string Build170 = "ClubDisklessHelper-1.7.0.exe";
    private readonly ServerFixture _server = new();
    private readonly string _tools = Path.Combine(Path.GetTempPath(), $"club-helperpanel-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tools);
        Put(Build181, "helper 1.8.1");
        Put(Build170, "helper 1.7.0");
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["HelperUpdates:ToolsDir"] = _tools;
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        Directory.Delete(_tools, recursive: true);
    }

    private NpgsqlDataSource Db => _server.Services.GetRequiredService<NpgsqlDataSource>();

    /// <summary>Файл сборки в <c>tools</c>, изменён час назад (старше <c>MinFileAgeSec</c>).</summary>
    private void Put(string name, string content)
    {
        var path = Path.Combine(_tools, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
    }

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

    /// <summary>Машина с номером места; по умолчанию одобренный гибрид с помощником 1.8.0.</summary>
    private async Task<MachineRow> MachineAsync(string bootMode = "local", string helper = "1.8.0", string? helperBuild = null)
    {
        var machines = _server.Services.GetRequiredService<MachineRepository>();
        var b = Guid.NewGuid().ToByteArray();
        var machine = await machines.CreateAsync(
            new MachineRegistration(Guid.NewGuid().ToString("N"), "PC", [$"02:00:00:{b[0]:x2}:{b[1]:x2}:{b[2]:x2}"], "10.0.0.5", helper, null), approved: true);
        await using var c = await Db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE machines SET boot_mode = @bootMode, helper_build = @helperBuild WHERE id = @id", new { id = machine.Id, bootMode, helperBuild });
        return (await machines.FindAsync(machine.Id))!;
    }

    private async Task<JsonElement> LaunchAsync(object request) =>
        await OkAsync(await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", request));

    private async Task<JsonElement> UpdatesAsync() => await OkAsync(await Panel().GetAsync("/panel/api/v1/helper/updates"));

    private async Task<JsonElement> MachineViewAsync(MachineRow machine)
    {
        var overview = await OkAsync(await Panel().GetAsync("/panel/api/v1/machines"));
        return overview.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == machine.Id);
    }

    private static int[] Numbers(JsonElement array) => array.EnumerateArray().Select(n => n.GetInt32()).ToArray();

    private static (int, string)[] Refused(JsonElement result) =>
        result.GetProperty("refused").EnumerateArray().Select(r => (r.GetProperty("number").GetInt32(), r.GetProperty("reason").GetString()!)).ToArray();

    [Fact]
    public async Task EveryRoute_RequiresPanelToken()
    {
        var anonymous = _server.CreateClient();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/panel/api/v1/helper/builds"),
            (HttpMethod.Get, "/panel/api/v1/helper/updates"),
            (HttpMethod.Post, "/panel/api/v1/helper/updates"),
            (HttpMethod.Post, "/panel/api/v1/helper/updates/resume"),
            (HttpMethod.Post, "/panel/api/v1/helper/updates/cancel"),
            (HttpMethod.Post, $"/panel/api/v1/helper/updates/{Guid.NewGuid()}/cancel"),
        };
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new { file = Build181, numbers = new[] { 1 } });
            }

            using var response = await anonymous.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {response.StatusCode}");
        }

        // Ничего не запущено.
        Assert.Empty((await UpdatesAsync()).GetProperty("items").EnumerateArray());
    }

    /// <summary>Сборки: новые первыми; старше 1.8.0 — <c>updatable=false</c> («только вручную»).</summary>
    [Fact]
    public async Task Builds_ListedNewestFirst_WithUpdatable()
    {
        var builds = (await OkAsync(await Panel().GetAsync("/panel/api/v1/helper/builds"))).EnumerateArray().ToList();
        Assert.Equal([Build181, Build170], builds.Select(b => b.GetProperty("file").GetString()));
        Assert.Equal(["1.8.1", "1.7.0"], builds.Select(b => b.GetProperty("version").GetString()));
        Assert.Equal([true, false], builds.Select(b => b.GetProperty("updatable").GetBoolean()));
        Assert.Equal("helper 1.8.1".Length, builds[0].GetProperty("size").GetInt64());
        Assert.Matches("^[0-9a-f]{64}$", builds[0].GetProperty("sha256").GetString());
    }

    /// <summary>
    /// Запуск по номерам и «все гибриды»: место бездиска — в <c>refused</c> с <c>diskless</c>, сборка старше 1.8.0 —
    /// <c>targetTooOld</c> у каждого ПК; очередь — <c>{ items, paused, maxActive }</c>, <c>requested_by = panel</c>.
    /// </summary>
    [Fact]
    public async Task Launch_ByNumbersAndAllHybrids()
    {
        var a = await MachineAsync();
        var b = await MachineAsync();
        var seat = await MachineAsync(bootMode: "diskless");

        var first = await LaunchAsync(new { file = Build181, numbers = new[] { a.Number, seat.Number, 9999 } });
        Assert.Equal([a.Number], Numbers(first.GetProperty("queued")));
        Assert.False(first.GetProperty("paused").GetBoolean());
        Assert.Equal([(seat.Number, Refusals.Diskless), (9999, Refusals.Unknown)], Refused(first));

        var all = await LaunchAsync(new { file = Build181, numbers = new[] { seat.Number }, allHybrids = true });
        Assert.Equal([b.Number], Numbers(all.GetProperty("queued")));
        Assert.Equal([(a.Number, Refusals.Busy), (seat.Number, Refusals.Diskless)], Refused(all));

        var old = await LaunchAsync(new { file = Build170, allHybrids = true });
        Assert.Empty(old.GetProperty("queued").EnumerateArray());
        Assert.Equal([(a.Number, Refusals.TargetTooOld), (b.Number, Refusals.TargetTooOld)], Refused(old));

        var queue = await UpdatesAsync();
        Assert.False(queue.GetProperty("paused").GetBoolean());
        Assert.Equal(3, queue.GetProperty("maxActive").GetInt32());
        var items = queue.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([a.Number, b.Number], items.Select(i => i.GetProperty("number").GetInt32()));
        Assert.All(items, i => Assert.Equal((States.Queued, "1.8.1", Build181), (i.GetProperty("state").GetString(), i.GetProperty("version").GetString(), i.GetProperty("file").GetString())));

        await using var c = await Db.OpenConnectionAsync();
        Assert.Equal(["panel"], await c.QueryAsync<string>("SELECT DISTINCT requested_by FROM helper_updates"));
    }

    /// <summary>400 <c>validation</c>: нет файла или он не в списке, пустой выбор, больше 1000 номеров.</summary>
    [Fact]
    public async Task Launch_Validation()
    {
        var a = await MachineAsync();
        await AssertValidationAsync(await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { numbers = new[] { a.Number } }), "file", "required");
        await AssertValidationAsync(
            await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { file = "ClubDisklessHelper-9.9.9.exe", numbers = new[] { a.Number } }), "file", "unknown");
        await AssertValidationAsync(
            await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { file = "../ClubDisklessHelper-1.8.1.exe", numbers = new[] { a.Number } }), "file", "unknown");
        await AssertValidationAsync(await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { file = Build181 }), "numbers", "required");
        await AssertValidationAsync(
            await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { file = Build181, numbers = Array.Empty<int>(), allHybrids = false }), "numbers", "required");
        await AssertValidationAsync(
            await Panel().PostAsJsonAsync("/panel/api/v1/helper/updates", new { file = Build181, numbers = Enumerable.Range(1, 1001).ToArray() }), "numbers", "tooMany");

        Assert.Empty((await UpdatesAsync()).GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// Строка машины: место бездиска — <c>helperUpdatable=false</c>, <c>helperUpdateBlock="diskless"</c>; мастер эталона в
    /// сеансе мастера — можно; помощник старше 1.8.0 — <c>helperTooOld</c>; поручение за сутки — в <c>helperUpdate</c>.
    /// </summary>
    [Fact]
    public async Task MachineView_HelperUpdateFields()
    {
        var sha = new string('a', 64);
        var hybrid = await MachineAsync(helperBuild: sha);
        var seat = await MachineAsync(bootMode: "diskless");
        var master = await MachineAsync(bootMode: "diskless");
        var old = await MachineAsync(helper: "1.7.0");
        await using (var c = await Db.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE diskless_image SET master_machine_id = @id, updated_at = now() - interval '1 hour'", new { id = master.Id });
            await c.ExecuteAsync(
                """
                INSERT INTO seat_disks (machine_id, kind, zvol, target_name, initiator_iqn, chap_user, chap_secret, state, last_boot_at)
                VALUES (@id, 'master', 'tank/golden', 'golden', 'iqn.master', 'u', 's', 'ready', now() - interval '30 minutes')
                """,
                new { id = master.Id });
        }

        var view = await MachineViewAsync(seat);
        Assert.False(view.GetProperty("helperUpdatable").GetBoolean());
        Assert.Equal(Refusals.Diskless, view.GetProperty("helperUpdateBlock").GetString());
        Assert.False(view.TryGetProperty("helperUpdate", out _));

        view = await MachineViewAsync(master);
        Assert.True(view.GetProperty("helperUpdatable").GetBoolean());
        Assert.False(view.TryGetProperty("helperUpdateBlock", out _));

        view = await MachineViewAsync(old);
        Assert.False(view.GetProperty("helperUpdatable").GetBoolean());
        Assert.Equal(Refusals.HelperTooOld, view.GetProperty("helperUpdateBlock").GetString());

        Assert.Equal([hybrid.Number], Numbers((await LaunchAsync(new { file = Build181, numbers = new[] { hybrid.Number } })).GetProperty("queued")));
        view = await MachineViewAsync(hybrid);
        Assert.True(view.GetProperty("helperUpdatable").GetBoolean());
        Assert.Equal(sha, view.GetProperty("helperBuild").GetString());
        var update = view.GetProperty("helperUpdate");
        Assert.Equal((States.Queued, "1.8.1"), (update.GetProperty("state").GetString(), update.GetProperty("version").GetString()));

        // Отменённое поручение видно сутки.
        using (var cancel = await Panel().PostAsync($"/panel/api/v1/helper/updates/{update.GetProperty("id").GetGuid()}/cancel", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        Assert.Equal(States.Cancelled, (await MachineViewAsync(hybrid)).GetProperty("helperUpdate").GetProperty("state").GetString());
    }

    /// <summary>Отмена одного (204, повторно и чужой id — 404), продолжение после паузы и «отменить всё» — <c>{ count }</c>.</summary>
    [Fact]
    public async Task Cancel_One_Resume_And_CancelAll()
    {
        var a = await MachineAsync();
        var b = await MachineAsync();
        var d = await MachineAsync();
        await LaunchAsync(new { file = Build181, numbers = new[] { a.Number, b.Number, d.Number } });
        var items = (await UpdatesAsync()).GetProperty("items").EnumerateArray().ToList();
        var first = items.Single(i => i.GetProperty("number").GetInt32() == a.Number).GetProperty("id").GetGuid();

        using (var cancel = await Panel().PostAsync($"/panel/api/v1/helper/updates/{first}/cancel", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        foreach (var id in new[] { first, Guid.NewGuid() })
        {
            using var again = await Panel().PostAsync($"/panel/api/v1/helper/updates/{id}/cancel", null);
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        await using (var c = await Db.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE helper_updates SET state = 'paused' WHERE state = 'queued'");
        }

        var queue = await UpdatesAsync();
        Assert.True(queue.GetProperty("paused").GetBoolean());
        Assert.Equal(2, (await OkAsync(await Panel().PostAsync("/panel/api/v1/helper/updates/resume", null))).GetProperty("count").GetInt32());
        Assert.False((await UpdatesAsync()).GetProperty("paused").GetBoolean());

        Assert.Equal(2, (await OkAsync(await Panel().PostAsync("/panel/api/v1/helper/updates/cancel", null))).GetProperty("count").GetInt32());
        Assert.Equal(0, (await OkAsync(await Panel().PostAsync("/panel/api/v1/helper/updates/cancel", null))).GetProperty("count").GetInt32());
        var states = (await UpdatesAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("state").GetString()).ToList();
        Assert.Equal([States.Cancelled, States.Cancelled, States.Cancelled], states);
    }
}
