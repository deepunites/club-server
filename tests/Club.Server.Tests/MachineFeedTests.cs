using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Club.Server.Integration;
using Club.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Отправка списка ПК внешней системе (MachineFeed): настоящий HTTP-получатель, подпись по сырым байтам, снимок по
/// схеме из docs/diskless-api.yaml (webhooks.machinesSnapshot), повторы, панель.
/// </summary>
public sealed class MachineFeedTests : IAsyncLifetime
{
    private const string Secret = "feed-secret-0123456789abcdef-0123456789";
    private const string PanelToken = "panel-test-token";
    private readonly ManualClock _clock = new();
    private FakeFeedReceiver _receiver = null!;
    private Fixture _server = null!;

    public async Task InitializeAsync()
    {
        _receiver = await FakeFeedReceiver.StartAsync();
        _server = Start(_receiver.BaseUrl + "/integrations/club-7/machines");
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private Fixture Start(string url, string? caPath = null)
    {
        var server = new Fixture(_clock);
        server.Settings["Auth:AutoApprovePcs"] = "false";
        server.Settings["Panel:AdminToken"] = PanelToken;
        server.Settings["MachineFeed:Url"] = url;
        server.Settings["MachineFeed:Secret"] = Secret;
        server.Settings["MachineFeed:RunWorker"] = "false";
        if (caPath is not null)
        {
            server.Settings["MachineFeed:CaCertificatePath"] = caPath;
        }

        return server;
    }

    private Task<bool> RunAsync(Fixture? server = null) =>
        (server ?? _server).Services.GetRequiredService<MachineFeed>().RunOnceAsync(CancellationToken.None);

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    /// <summary>Регистрация как в клубе (автоодобрение выключено → 403 pendingApproval); id — из панели по MAC.</summary>
    private async Task<Guid> RegisterAsync(string hwid, params string[] macs)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/diskless/v1/machines/register") { Content = JsonContent.Create(TestMachine.RegisterBody(hwid, macs)) };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        using var response = await _server.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var overview = await Panel().GetFromJsonAsync<JsonElement>("/panel/api/v1/machines");
        return overview.GetProperty("machines").EnumerateArray()
            .Single(m => m.GetProperty("macAddresses").EnumerateArray().Any(a => a.GetString() == macs[0]))
            .GetProperty("id").GetGuid();
    }

    private async Task ApproveAsync(Guid id)
    {
        using var response = await Panel().PostAsync($"/panel/api/v1/machines/{id}/approve", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> FeedPanelAsync()
    {
        using var response = await Panel().GetAsync("/panel/api/v1/machines");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("machineFeed");
    }

    /// <summary>Тело последнего запроса после проверки подписи, заголовков и схемы — так, как проверит касса шелла.</summary>
    private JsonElement LastSnapshot()
    {
        var request = _receiver.Requests[^1];
        Assert.Equal("/integrations/club-7/machines", request.Path);
        Assert.Equal("machines.snapshot", request.Headers[MachineFeed.EventHeader]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"]);
        Assert.StartsWith("club-server/", request.Headers["User-Agent"]);
        var timestamp = request.Headers[MachineFeed.TimestampHeader];
        Assert.InRange(long.Parse(timestamp) - _clock.GetUtcNow().ToUnixTimeSeconds(), -1, 0);
        Assert.Equal(MachineFeed.Sign(Secret, timestamp, request.Body), request.Headers[MachineFeed.SignatureHeader]);
        Assert.NotEqual(MachineFeed.Sign(Secret + "x", timestamp, request.Body), request.Headers[MachineFeed.SignatureHeader]);
        var body = JsonDocument.Parse(request.Body).RootElement.Clone();
        Contract.AssertMatches("MachinesSnapshot", body);
        return body;
    }

    [Fact]
    public async Task Sends_a_signed_snapshot_of_approved_machines_only()
    {
        var first = await RegisterAsync("hwid-first", "aa:00:00:00:00:02", "aa:00:00:00:00:01");
        await RegisterAsync("hwid-pending", "bb:00:00:00:00:01");
        await ApproveAsync(first);

        Assert.True(await RunAsync());

        var snapshot = LastSnapshot();
        Assert.Equal("club-server.machines/1", snapshot.GetProperty("schema").GetString());
        var machine = Assert.Single(snapshot.GetProperty("machines").EnumerateArray());
        Assert.Equal(first, machine.GetProperty("id").GetGuid());
        Assert.Equal(1, machine.GetProperty("number").GetInt32());
        Assert.Equal("PC-01", machine.GetProperty("name").GetString());
        Assert.Equal("PC-TEST", machine.GetProperty("hostname").GetString());
        Assert.Equal(["aa:00:00:00:00:01", "aa:00:00:00:00:02"], machine.GetProperty("macAddresses").EnumerateArray().Select(m => m.GetString()));

        var panel = await FeedPanelAsync();
        Assert.Equal("ok", panel.GetProperty("state").GetString());
        Assert.Equal(new Uri(_receiver.BaseUrl).Authority, panel.GetProperty("target").GetString());
        Assert.Equal(1, panel.GetProperty("machines").GetInt32());
    }

    [Fact]
    public async Task Changes_are_sent_and_an_unchanged_list_is_repeated_only_after_the_resend_interval()
    {
        var first = await RegisterAsync("hwid-a", "aa:00:00:00:00:0a");
        await ApproveAsync(first);
        Assert.True(await RunAsync());
        var hash = LastSnapshot().GetProperty("contentHash").GetString();

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await RunAsync());
        Assert.Single(_receiver.Requests);

        // Одобрение и смена номера/имени в панели — новые снимки.
        var second = await RegisterAsync("hwid-b", "aa:00:00:00:00:0b");
        Assert.False(await RunAsync()); // ожидающий одобрения в список не входит
        await ApproveAsync(second);
        Assert.True(await RunAsync());
        Assert.Equal(2, LastSnapshot().GetProperty("machines").GetArrayLength());

        using (var patch = await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{second}", new { number = 7, name = "VIP-7" }))
        {
            Assert.True(patch.IsSuccessStatusCode);
        }

        Assert.True(await RunAsync());
        var snapshot = LastSnapshot();
        var last = snapshot.GetProperty("machines")[1];
        Assert.Equal((7, "VIP-7"), (last.GetProperty("number").GetInt32(), last.GetProperty("name").GetString()));
        var changedHash = snapshot.GetProperty("contentHash").GetString();
        Assert.NotEqual(hash, changedHash);
        Assert.Equal(3, _receiver.Requests.Count);

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(await RunAsync());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await RunAsync());
        Assert.Equal(changedHash, LastSnapshot().GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task A_rejected_delivery_is_retried_with_backoff_and_shown_in_the_panel()
    {
        await ApproveAsync(await RegisterAsync("hwid-c", "aa:00:00:00:00:0c"));
        _receiver.StatusCode = 500;

        Assert.True(await RunAsync());
        var panel = await FeedPanelAsync();
        Assert.Equal("failing", panel.GetProperty("state").GetString());
        Assert.Equal("HTTP 500 Internal Server Error", panel.GetProperty("error").GetString());
        Assert.False(panel.TryGetProperty("lastDeliveredAt", out _));

        Assert.False(await RunAsync()); // пауза 10 с
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await RunAsync());
        Assert.Equal(2, (await FeedPanelAsync()).GetProperty("failures").GetInt32());

        _receiver.StatusCode = 200;
        _clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(await RunAsync()); // вторая пауза — 20 с
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await RunAsync());
        panel = await FeedPanelAsync();
        Assert.Equal("ok", panel.GetProperty("state").GetString());
        Assert.False(panel.TryGetProperty("error", out _));
        Assert.Equal(3, _receiver.Requests.Count);
        Assert.Equal(MachineFeed.Backoff(1) * 30, MachineFeed.Backoff(10)); // потолок 5 мин
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        _receiver.RedirectTo = _receiver.BaseUrl + "/elsewhere";

        Assert.True(await RunAsync());

        var request = Assert.Single(_receiver.Requests);
        Assert.Equal("/integrations/club-7/machines", request.Path);
        Assert.Equal("HTTP 307 Temporary Redirect", (await FeedPanelAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Https_receiver_is_checked_against_the_configured_ca()
    {
        await using var https = await FakeFeedReceiver.StartAsync(https: true);
        var trusted = Start(https.BaseUrl + "/integrations/club-7/machines", https.CaPath);
        var untrusted = Start(https.BaseUrl + "/integrations/club-7/machines");
        await trusted.InitializeAsync();
        await untrusted.InitializeAsync();
        try
        {
            Assert.True(await RunAsync(trusted));
            Assert.Null(trusted.Services.GetRequiredService<MachineFeedState>().LastError);

            Assert.True(await RunAsync(untrusted)); // тестовый CA не в системном хранилище
            Assert.Contains("SSL", untrusted.Services.GetRequiredService<MachineFeedState>().LastError);
            Assert.Single(https.Requests);
        }
        finally
        {
            await ((IAsyncLifetime)trusted).DisposeAsync();
            await ((IAsyncLifetime)untrusted).DisposeAsync();
        }
    }

    [Fact]
    public async Task Bad_settings_stop_the_server_at_start_and_an_unset_url_disables_the_feed()
    {
        var weak = Start(_receiver.BaseUrl);
        weak.Settings["MachineFeed:Secret"] = "short";
        await weak.InitializeAsync();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => weak.CreateClient());
            Assert.Contains("MachineFeed:Secret", error.Message);
        }
        finally
        {
            await ((IAsyncLifetime)weak).DisposeAsync();
        }

        var off = Start("");
        await off.InitializeAsync();
        try
        {
            Assert.False(await RunAsync(off));
            var http = off.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
            var overview = await http.GetFromJsonAsync<JsonElement>("/panel/api/v1/machines");
            Assert.False(overview.TryGetProperty("machineFeed", out _));
        }
        finally
        {
            await ((IAsyncLifetime)off).DisposeAsync();
        }

        Assert.Empty(_receiver.Requests);
    }

    [Fact]
    public void Signature_matches_the_documented_example()
    {
        // Контрольный пример из docs/machine-feed.md — получатель сверяет с ним свою проверку подписи.
        var body = Encoding.UTF8.GetBytes("""{"schema":"club-server.machines/1"}""");
        Assert.Equal("v1=d77bd0633fede6b09679736f029b6cd328da9e829584fb6101e6fd462e52d70d", MachineFeed.Sign("s3cret", "1790000000", body));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Fixture(TimeProvider clock) : ServerFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }
    }
}
