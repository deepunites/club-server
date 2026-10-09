using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Club.Server.Tests;

/// <summary>API панели для экрана «Библиотека игр».</summary>
public sealed partial class LibraryTests
{
    private HttpClient Panel(string? token = PanelToken)
    {
        var http = _server.CreateClient();
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    private async Task<JsonElement> OverviewAsync()
    {
        using var response = await Panel().GetAsync("/panel/api/v1/library");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Panel_requires_admin_token(string? token)
    {
        using var response = await Panel(token).GetAsync("/panel/api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "panelToken");
    }

    [Fact]
    public async Task Panel_publish_is_queued_then_shown_as_current()
    {
        using var accepted = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "2026-10" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var operationId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid();

        var queued = await OverviewAsync();
        Assert.True(queued.GetProperty("storageEnabled").GetBoolean());
        Assert.Equal(operationId, queued.GetProperty("openOperations")[0].GetProperty("id").GetGuid());
        Assert.Equal("publishing", queued.GetProperty("versions")[0].GetProperty("state").GetString());

        await PassAsync();
        var overview = await OverviewAsync();
        var current = overview.GetProperty("current");
        Assert.Equal("2026-10", current.GetProperty("label").GetString());
        Assert.Equal("current", current.GetProperty("role").GetString());
        Assert.Equal($"{Basename}:games-2026-10", current.GetProperty("targetIqn").GetString());
        Assert.Equal(0, overview.GetProperty("openOperations").GetArrayLength());

        using var operation = await Panel().GetAsync($"/panel/api/v1/library/operations/{operationId}");
        var op = await operation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", op.GetProperty("status").GetString());
        Assert.Equal("panel", op.GetProperty("requestedBy").GetString());
        Assert.Equal("2026-10", op.GetProperty("versionLabel").GetString());
    }

    [Fact]
    public async Task Panel_rejects_bad_and_duplicate_labels()
    {
        using var bad = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "../etc" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Contract.AssertError(await bad.Content.ReadFromJsonAsync<JsonElement>(), "validation");

        await PublishAsync("v1");
        using var duplicate = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "v1" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Contract.AssertError(await duplicate.Content.ReadFromJsonAsync<JsonElement>(), "conflict", "exists");
    }

    [Fact]
    public async Task Panel_rollback_needs_a_previous_version()
    {
        await PublishAsync("v1");
        using var refused = await Panel().PostAsync("/panel/api/v1/library/rollback", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Contract.AssertError(await refused.Content.ReadFromJsonAsync<JsonElement>(), "conflict", "noRollback");

        await PublishAsync("v2");
        using var accepted = await Panel().PostAsync("/panel/api/v1/library/rollback", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await PassAsync();
        var overview = await OverviewAsync();
        Assert.Equal("v1", overview.GetProperty("current").GetProperty("label").GetString());
        Assert.Equal("v2", overview.GetProperty("rollback").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Failed_publish_can_be_retried_from_panel()
    {
        _nas.RemoveDataset(Published); // клон некуда положить: публикация падает настоящей ошибкой TrueNAS
        using var accepted = await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "v1" });
        var operationId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetGuid();
        await PassAsync(reconcile: false);

        var failed = (await OverviewAsync()).GetProperty("versions")[0];
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("lastError").GetString()));

        using var notFailed = await Panel().PostAsync($"/panel/api/v1/library/operations/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, notFailed.StatusCode);

        _nas.AddFilesystem(Published); // администратор исправил причину
        using var retry = await Panel().PostAsync($"/panel/api/v1/library/operations/{operationId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await PassAsync();
        Assert.Equal("v1", (await OverviewAsync()).GetProperty("current").GetProperty("label").GetString());

        using var again = await Panel().PostAsync($"/panel/api/v1/library/operations/{operationId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // выполненную операцию повторять нельзя
    }

    [Fact]
    public async Task Panel_shows_reconcile_warnings()
    {
        await PublishAsync("v1");
        _nas.MakeWritable($"{Published}/lib-v1");
        await PassAsync();

        using var response = await Panel().GetAsync("/panel/api/v1/storage/warnings");
        var warnings = await response.Content.ReadFromJsonAsync<JsonElement>();
        var warning = Assert.Single(warnings.EnumerateArray());
        Assert.Equal("cloneWritable", warning.GetProperty("kind").GetString());
        Assert.Equal(1, (await OverviewAsync()).GetProperty("warnings").GetArrayLength());
    }

    private static object Report(string state, string? version, bool? readOnly = true, string[]? contents = null) => new
    {
        helperVersion = "1.1.0",
        volume = new { state, libraryVersion = version, targetIqn = version is null ? null : $"{Basename}:games-{version}", driveLetter = "G", readOnlyVerified = readOnly, contents },
    };

    [Fact]
    public async Task Panel_shows_which_pcs_run_which_version_and_what_is_inside()
    {
        await PublishAsync("v1");
        await PublishAsync("v2");
        var http = _server.CreateClient();
        async Task ReportAsync(string hwid, object body)
        {
            var (machine, _) = await TestMachine.RegisterAsync(http, hwid, $"02:00:00:00:00:0{hwid[^1]}");
            using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await ReportAsync("hw-1", Report("mounted", "v2", contents: ["Dota 2", "Counter-Strike 2", "  ", "bad\u0001name", "dota 2"]));
        await ReportAsync("hw-2", Report("switchPending", "v1"));
        await ReportAsync("hw-3", Report("failed", null, readOnly: null));
        // Том без подтверждённого read-only — его «состав» не принимается.
        await ReportAsync("hw-4", Report("mounted", "v1", readOnly: false, contents: ["Malware"]));

        var overview = await OverviewAsync();
        var machines = overview.GetProperty("machines");
        Assert.Equal((4, 1, 1, 1, 1, 0), (
            machines.GetProperty("online").GetInt32(), machines.GetProperty("onCurrent").GetInt32(), machines.GetProperty("onOlder").GetInt32(),
            machines.GetProperty("switchPending").GetInt32(), machines.GetProperty("failed").GetInt32(), machines.GetProperty("notMounted").GetInt32()));

        var versions = overview.GetProperty("versions").EnumerateArray().ToDictionary(v => v.GetProperty("label").GetString()!);
        Assert.Equal(1, versions["v2"].GetProperty("mountedOn").GetInt32());
        Assert.Equal(2, versions["v1"].GetProperty("mountedOn").GetInt32()); // ждёт освобождения тома + без проверки RO
        Assert.Equal(["Counter-Strike 2", "Dota 2"], versions["v2"].GetProperty("contents").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(versions["v1"].TryGetProperty("contents", out _));
        Assert.Equal(2, overview.GetProperty("current").GetProperty("contents").GetArrayLength());
    }
}
