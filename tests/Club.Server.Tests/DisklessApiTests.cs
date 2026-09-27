using Club.TestSupport;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>API бездиска без модуля библиотеки: регистрация, токены, отчёты помощника.</summary>
public sealed class DisklessApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private readonly HttpClient _http = server.CreateClient();

    [Fact]
    public async Task Register_without_club_key_is_rejected()
    {
        using var response = await _http.PostAsJsonAsync("/diskless/v1/machines/register", TestMachine.RegisterBody("hw-no-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "clubKey");
    }

    [Fact]
    public async Task Register_returns_seat_and_tokens_per_spec()
    {
        var (_, body) = await TestMachine.RegisterAsync(_http);
        Contract.AssertMatches("MachineRegisterResponse", body);
        Assert.True(body.GetProperty("number").GetInt32() >= 1);
        Assert.Equal("standard", body.GetProperty("zone").GetString());
    }

    [Fact]
    public async Task Bad_mac_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/diskless/v1/machines/register")
        {
            Content = JsonContent.Create(TestMachine.RegisterBody("hw-bad-mac", "not-a-mac")),
        };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation");
    }

    [Fact]
    public async Task Same_hwid_keeps_seat_and_revokes_previous_tokens()
    {
        var hwid = Guid.NewGuid().ToString("N");
        var (first, a) = await TestMachine.RegisterAsync(_http, hwid);
        var (second, b) = await TestMachine.RegisterAsync(_http, hwid);
        Assert.Equal(first.MachineId, second.MachineId);
        Assert.Equal(a.GetProperty("number").GetInt32(), b.GetProperty("number").GetInt32());

        using var stale = await first.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{first.MachineId}/volume");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Contract.AssertError(await stale.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "revoked");
    }

    [Fact]
    public async Task No_published_library_means_no_volume()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/volume");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Status_is_recorded_as_facts_from_the_pc()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status",
            TestMachine.Status("mounted", "iqn.2005-10.org.freenas.ctl:games-v1", "v1", readOnly: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Contract.ReadAsync(response, "StatusAccepted");
        Assert.False(body.TryGetProperty("volume", out _));

        var row = await server.Services.GetRequiredService<MachineRepository>().FindAsync(machine.MachineId);
        Assert.Equal("mounted", row!.VolumeState);
        Assert.Equal("v1", row.VolumeVersion);
        Assert.True(row.VolumeRoVerified);
        Assert.NotNull(row.LastSeenAt);
    }

    [Fact]
    public async Task Unknown_volume_state_is_rejected()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", TestMachine.Status("exploded"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_machine_cannot_be_addressed()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        var (other, _) = await TestMachine.RegisterAsync(_http);
        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{other.MachineId}/volume");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_everything()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        using var rotated = await _http.PostAsJsonAsync("/diskless/v1/machines/refresh", new { refreshToken = machine.RefreshToken, hwid = machine.Hwid });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await Contract.ReadAsync(rotated, "RefreshResponse");

        using var reused = await _http.PostAsJsonAsync("/diskless/v1/machines/refresh", new { refreshToken = machine.RefreshToken, hwid = machine.Hwid });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Contract.AssertError(await reused.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "reused");

        using var fresh = await _http.PostAsJsonAsync("/diskless/v1/machines/refresh", new { refreshToken = body.GetProperty("refreshToken").GetString(), hwid = machine.Hwid });
        Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode);
    }

    [Fact]
    public async Task Refresh_with_other_hwid_is_rejected()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_http);
        using var response = await _http.PostAsJsonAsync("/diskless/v1/machines/refresh", new { refreshToken = machine.RefreshToken, hwid = "someone-else" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        using var response = await _http.GetAsync($"/diskless/v1/machines/{Guid.NewGuid()}/volume");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
