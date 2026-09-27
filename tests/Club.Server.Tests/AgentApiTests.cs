using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Auth;
using Club.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

public sealed class AgentApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private readonly HttpClient _http = server.CreateClient();

    [Fact]
    public async Task Register_without_club_key_is_rejected()
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/agents/register", TestAgent.RegisterBody("hw-no-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "clubKey");
    }

    [Fact]
    public async Task Register_returns_contract_shape_and_disables_unimplemented_features()
    {
        var (_, body) = await TestAgent.RegisterAsync(_http);
        Contract.AssertMatches("AgentRegisterResponse", body);

        var features = body.GetProperty("config").GetProperty("shell").GetProperty("features");
        foreach (var feature in new[] { "shop", "chat", "booking", "tournaments", "topup", "apps", "callAdmin" })
        {
            Assert.False(features.GetProperty(feature).GetBoolean(), feature);
        }

        var games = body.GetProperty("config").GetProperty("games");
        Assert.False(games.GetProperty("accountPool").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Same_hwid_keeps_pc_and_revokes_previous_tokens()
    {
        var hwid = Guid.NewGuid().ToString("N");
        var (first, _) = await TestAgent.RegisterAsync(_http, hwid);
        var (second, _) = await TestAgent.RegisterAsync(_http, hwid);
        Assert.Equal(first.PcId, second.PcId);

        using var stale = await first.SendAsync(HttpMethod.Get, $"/api/v1/agents/{first.PcId}/config");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Contract.AssertError(await stale.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "revoked");
    }

    [Fact]
    public async Task Signed_heartbeat_matches_contract()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var response = await agent.SendAsync(HttpMethod.Post, $"/api/v1/agents/{agent.PcId}/heartbeat", TestAgent.Heartbeat());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Contract.ReadAsync(response, "HeartbeatResponse");
        Assert.Equal("free", body.GetProperty("pcStatus").GetString());
    }

    [Fact]
    public async Task Missing_or_wrong_signature_is_rejected()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var request = agent.Signed(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/config", null);
        request.Headers.Remove("X-Signature");
        request.Headers.Add("X-Signature", new string('0', 64));
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "signature");
    }

    [Fact]
    public async Task Signature_covers_query_string_exactly_as_sent()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var ok = await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/stable/manifest?component=agent&current=1.4.2&arch=x64");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        // Подпись без query не подходит к запросу с query.
        using var request = agent.Signed(HttpMethod.Get, "/api/v1/updates/stable/manifest", null);
        request.RequestUri = new Uri("/api/v1/updates/stable/manifest?component=agent&current=1.4.2", UriKind.Relative);
        using var mismatch = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
    }

    [Fact]
    public async Task Clock_skew_returns_401_with_server_time()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/config", timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "clockSkew");
        Assert.True(response.Headers.TryGetValues("X-Server-Time", out var values));
        Assert.True(DateTimeOffset.TryParse(values.Single(), out _));
    }

    [Fact]
    public async Task Replayed_request_is_rejected()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var heartbeat = TestAgent.Heartbeat();
        using var first = await agent.SendAsync(HttpMethod.Post, $"/api/v1/agents/{agent.PcId}/heartbeat", heartbeat, timestamp: ts);
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        using var second = await agent.SendAsync(HttpMethod.Post, $"/api/v1/agents/{agent.PcId}/heartbeat", heartbeat, timestamp: ts);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Contract.AssertError(await second.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "replay");
    }

    [Fact]
    public async Task Identical_reads_within_one_second_are_allowed()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var first = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/commands", timestamp: ts);
        using var second = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/commands", timestamp: ts);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public void Replay_guard_releases_triple_after_retryable_failure()
    {
        var guard = new ReplayGuard(TimeProvider.System);
        var key = ReplayGuard.Key(Guid.NewGuid(), "1789992930", new string('a', 64));
        var window = TimeSpan.FromMinutes(5);

        Assert.True(guard.TryBegin(key, window));
        Assert.False(guard.TryBegin(key, window)); // одновременный дубликат
        guard.Complete(key, 503);
        Assert.True(guard.TryBegin(key, window)); // повтор агента после 5xx с той же подписью
        guard.Complete(key, 200);
        Assert.False(guard.TryBegin(key, window)); // принятый запрос повторить нельзя
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_everything()
    {
        var hwid = Guid.NewGuid().ToString("N");
        var (agent, _) = await TestAgent.RegisterAsync(_http, hwid);

        using var rotated = await _http.PostAsJsonAsync("/api/v1/agents/refresh", new { refreshToken = agent.RefreshToken, hwid });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await Contract.ReadAsync(rotated, "AgentRefreshResponse");
        Assert.NotEqual(agent.RefreshToken, body.GetProperty("refreshToken").GetString());

        using var reused = await _http.PostAsJsonAsync("/api/v1/agents/refresh", new { refreshToken = agent.RefreshToken, hwid });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Contract.AssertError(await reused.Content.ReadFromJsonAsync<JsonElement>(), "unauthorized", "reused");

        using var fresh = await _http.PostAsJsonAsync("/api/v1/agents/refresh", new { refreshToken = body.GetProperty("refreshToken").GetString(), hwid });
        Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode); // после повтора отозваны и новые токены
    }

    [Fact]
    public async Task Config_and_policies_match_contract_and_support_etag()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var config = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/config");
        await Contract.ReadAsync(config, "AgentServerConfig");

        using var policies = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/policies");
        Assert.Equal(HttpStatusCode.OK, policies.StatusCode);
        await Contract.ReadAsync(policies, "Policy");

        var etag = policies.Headers.ETag!.ToString();
        using var cached = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/policies", etag: etag);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
    }

    [Fact]
    public async Task Other_pc_cannot_be_addressed()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        var (other, _) = await TestAgent.RegisterAsync(_http);
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{other.PcId}/config");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pc_view_matches_contract_without_hwid()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/pcs/{agent.PcId}");
        var body = await Contract.ReadAsync(response, "Pc");
        Assert.False(body.TryGetProperty("hwid", out _));
    }

    [Fact]
    public async Task Commands_are_delivered_over_rest_and_acked()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        var commands = server.Services.GetRequiredService<CommandRepository>();
        var payload = JsonSerializer.SerializeToElement(new { reason = "admin", message = "Подойдите к стойке" });
        var queued = await commands.EnqueueAsync(agent.PcId, "lock", payload, "test", TimeSpan.FromMinutes(5), null, DateTimeOffset.UtcNow);

        using var list = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/commands");
        var body = await Contract.ReadAsync(list, "ServerCommandsResponse");
        Assert.Equal(queued.Id, body.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var ack = await agent.SendAsync(HttpMethod.Post, $"/api/v1/agents/{agent.PcId}/commands/{queued.Id}/ack", new { ok = true });
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);

        using var empty = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/commands");
        Assert.Equal(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Every_not_implemented_contract_operation_answers_501_never_404()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_http);
        var checkedCount = 0;
        foreach (var path in Contract.Document.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (op.Value.ValueKind != JsonValueKind.Object || !op.Value.TryGetProperty("x-server-status", out var status) || status.GetString() != "notImplemented"
                    || Agents.AgentEndpoints.Implemented.Contains($"{op.Name.ToUpperInvariant()} {path.Name}"))
                {
                    continue;
                }

                var url = "/api/v1" + path.Name
                    .Replace("{pcId}", agent.PcId.ToString())
                    .Replace("{userId}", Guid.NewGuid().ToString())
                    .Replace("{id}", Guid.NewGuid().ToString())
                    .Replace("{leaseId}", Guid.NewGuid().ToString())
                    .Replace("{roomId}", "club")
                    .Replace("{channel}", "stable");
                var method = new HttpMethod(op.Name.ToUpperInvariant());
                using var response = await agent.SendAsync(method, url, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { });
                Assert.True(response.StatusCode == HttpStatusCode.NotImplemented, $"{method} {url} -> {(int)response.StatusCode}");
                Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "serverUnavailable", "notImplemented");
                checkedCount++;
            }
        }

        Assert.True(checkedCount > 20, $"only {checkedCount} notImplemented operations found");
    }
}
