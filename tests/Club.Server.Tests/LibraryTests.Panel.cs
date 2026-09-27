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
}
