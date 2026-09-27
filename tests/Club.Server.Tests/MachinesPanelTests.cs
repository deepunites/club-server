using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.TestSupport;

namespace Club.Server.Tests;

/// <summary>Экран «Рабочие станции»: реестр, статусы, одобрение новых машин. Автоодобрение выключено, как в клубе.</summary>
public sealed class MachinesPanelTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();

    public async Task InitializeAsync()
    {
        _server.Settings["Auth:AutoApprovePcs"] = "false";
        _server.Settings["Panel:AdminToken"] = PanelToken;
        await _server.InitializeAsync();
    }

    public Task DisposeAsync() => ((IAsyncLifetime)_server).DisposeAsync();

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private async Task<HttpResponseMessage> RegisterAsync(string hwid)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/diskless/v1/machines/register") { Content = JsonContent.Create(TestMachine.RegisterBody(hwid)) };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        return await _server.CreateClient().SendAsync(request);
    }

    private async Task<JsonElement> OverviewAsync()
    {
        using var response = await Panel().GetAsync("/panel/api/v1/machines");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Single(JsonElement overview) => Assert.Single(overview.GetProperty("machines").EnumerateArray());

    [Fact]
    public async Task New_machine_waits_for_approval_then_gets_tokens()
    {
        using var first = await RegisterAsync("hw-new");
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Contract.AssertError(await first.Content.ReadFromJsonAsync<JsonElement>(), "forbidden", "pendingApproval");

        var overview = await OverviewAsync();
        var machine = Single(overview);
        Assert.Equal("pendingApproval", machine.GetProperty("status").GetString());
        Assert.Equal(1, overview.GetProperty("pending").GetInt32());
        Assert.Equal("aa:bb:cc:dd:ee:ff", machine.GetProperty("macAddresses")[0].GetString());

        using var approve = await Panel().PostAsync($"/panel/api/v1/machines/{machine.GetProperty("id").GetGuid()}/approve", null);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        using var second = await RegisterAsync("hw-new");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("neverSeen", Single(await OverviewAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejected_machine_leaves_the_registry_and_approved_cannot_be_rejected()
    {
        (await RegisterAsync("hw-a")).Dispose();
        var id = Single(await OverviewAsync()).GetProperty("id").GetGuid();

        using var reject = await Panel().PostAsync($"/panel/api/v1/machines/{id}/reject", null);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(0, (await OverviewAsync()).GetProperty("machines").GetArrayLength());

        (await RegisterAsync("hw-b")).Dispose();
        var approved = Single(await OverviewAsync()).GetProperty("id").GetGuid();
        (await Panel().PostAsync($"/panel/api/v1/machines/{approved}/approve", null)).Dispose();
        using var refused = await Panel().PostAsync($"/panel/api/v1/machines/{approved}/reject", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Status_follows_helper_reports_and_seat_can_be_renumbered()
    {
        (await RegisterAsync("hw-1")).Dispose();
        var id = Single(await OverviewAsync()).GetProperty("id").GetGuid();
        (await Panel().PostAsync($"/panel/api/v1/machines/{id}/approve", null)).Dispose();
        using var registered = await RegisterAsync("hw-1");
        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();
        var machine = new TestMachine(_server.CreateClient(), id, "hw-1", body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);

        using var status = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{id}/status", TestMachine.Status("mounted", "iqn.x:games-v1", "v1", readOnly: true));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var row = Single(await OverviewAsync());
        Assert.Equal("online", row.GetProperty("status").GetString());
        Assert.Equal("mounted", row.GetProperty("volume").GetProperty("state").GetString());
        Assert.Equal("v1", row.GetProperty("volume").GetProperty("libraryVersion").GetString());

        using var patch = await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{id}", new { number = 12, name = "VIP-12", maintenance = true });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        row = Single(await OverviewAsync());
        Assert.Equal((12, "VIP-12", "maintenance"), (row.GetProperty("number").GetInt32(), row.GetProperty("name").GetString(), row.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Seat_number_must_be_unique_and_zone_must_exist()
    {
        foreach (var hwid in new[] { "hw-x", "hw-y" })
        {
            (await RegisterAsync(hwid)).Dispose();
        }

        var machines = (await OverviewAsync()).GetProperty("machines").EnumerateArray().ToList();
        var second = machines[1].GetProperty("id").GetGuid();
        using var taken = await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{second}", new { number = machines[0].GetProperty("number").GetInt32() });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Contract.AssertError(await taken.Content.ReadFromJsonAsync<JsonElement>(), "conflict", "numberTaken");

        using var zone = await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{second}", new { zone = "no-such-zone" });
        Assert.Equal(HttpStatusCode.BadRequest, zone.StatusCode);
    }

    [Fact]
    public async Task Machines_screen_requires_panel_token()
    {
        using var response = await _server.CreateClient().GetAsync("/panel/api/v1/machines");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Panel_page_is_served_without_token()
    {
        var http = _server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var root = await http.GetAsync("/");
        Assert.Equal("/panel/", root.Headers.Location?.ToString());
        using var page = await http.GetAsync("/panel/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("app.js", await page.Content.ReadAsStringAsync());
    }
}
