using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Network;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>Адресный план клуба и его проверка (без базы).</summary>
public sealed class IpPlanTests
{
    public static NetworkSettings Default(string subnet = "192.168.77.0/24") =>
        new(true, subnet, 1, "eth0", "192.168.77.1", "192.168.77.1", ["192.168.77.1"], "192.168.77.200", "192.168.77.250", "192.168.77.101", 43200);

    [Fact]
    public void Seat_address_is_reserved_start_plus_number()
    {
        var plan = new IpPlan(Default());
        Assert.Equal("192.168.77.101", IpPlan.ToIp(plan.SeatAddress(1)!.Value));
        Assert.Equal("192.168.77.199", IpPlan.ToIp(plan.SeatAddress(99)!.Value));
        Assert.Null(plan.SeatAddress(100)); // .200 — начало пула
        Assert.Null(plan.SeatAddress(0));
    }

    [Fact]
    public void Seat_never_lands_on_gateway_or_outside_subnet()
    {
        var plan = new IpPlan(Default() with { Gateway = "192.168.77.105", PoolStart = "192.168.77.2", PoolEnd = "192.168.77.60" });
        Assert.Null(plan.SeatAddress(5));
        Assert.Equal("192.168.77.254", IpPlan.ToIp(plan.SeatAddress(154)!.Value));
        Assert.Null(plan.SeatAddress(155)); // .255 — broadcast
    }

    [Theory]
    [InlineData("192.168.77.5/24", "subnet", "notNetworkAddress")]
    [InlineData("192.168.77.0/31", "subnet", "format")]
    [InlineData("nonsense", "subnet", "format")]
    public void Subnet_must_be_a_network_address(string subnet, string field, string reason) =>
        Assert.Contains((field, reason), IpPlan.Validate(Default(subnet)));

    [Fact]
    public void Validation_catches_inconsistent_plan()
    {
        Assert.Empty(IpPlan.Validate(Default()));
        Assert.Contains(("reservedStart", "insidePool"), IpPlan.Validate(Default() with { ReservedStart = "192.168.77.210" }));
        Assert.Contains(("poolEnd", "beforeStart"), IpPlan.Validate(Default() with { PoolEnd = "192.168.77.100" }));
        Assert.Contains(("gateway", "outsideSubnet"), IpPlan.Validate(Default() with { Gateway = "10.0.0.1" }));
        Assert.Contains(("poolStart", "coversInfrastructure"), IpPlan.Validate(Default() with { PoolStart = "192.168.77.1" }));
        Assert.Contains(("dnsServers", "format"), IpPlan.Validate(Default() with { DnsServers = ["8.8.8"] }));
        Assert.Contains(("leaseTimeSec", "range"), IpPlan.Validate(Default() with { LeaseTimeSec = 60 }));
        Assert.Contains(("interface", "format"), IpPlan.Validate(Default() with { Interface = "eth0; rm" }));
    }

    [Fact]
    public void Hostname_is_dns_safe()
    {
        Assert.Equal("pc-01", KeaHostSync.Hostname("PC-01", 1));
        Assert.Equal("vip-pc-3", KeaHostSync.Hostname("VIP  PC 3", 3));
        Assert.Equal("pc-07", KeaHostSync.Hostname("Ноутбук", 7));
    }
}

/// <summary>
/// Резервации Kea на официальной схеме Kea 3.0 (временная база) и экран «Сеть». Конфиг проверяется настоящим
/// <c>kea-dhcp4 -t</c>, если задан <c>KEA_DHCP4</c> (путь к бинарнику; <c>KEA_HOOKS</c> — каталог хуков).
/// </summary>
public sealed class NetworkTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();
    private KeaDatabase _kea = null!;

    private static readonly object Settings = new
    {
        subnet = "192.168.77.0/24", keaSubnetId = 1, @interface = "eth0", dhcpServer = "192.168.77.1", gateway = "192.168.77.1",
        dnsServers = new[] { "192.168.77.1" }, poolStart = "192.168.77.200", poolEnd = "192.168.77.250", reservedStart = "192.168.77.101",
        leaseTimeSec = 43200,
    };

    public async Task InitializeAsync()
    {
        _kea = await KeaDatabase.CreateAsync();
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["Kea:Enabled"] = "true";
        _server.Settings["Kea:ConnectionString"] = _kea.ConnectionString;
        _server.Settings["Network:RunWorker"] = "false";
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _kea.DisposeAsync();
    }

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private Task SyncAsync() => NetworkWorker.RunOnceAsync(_server.Services, TimeProvider.System, CancellationToken.None);

    private async Task<JsonElement> StatusAsync()
    {
        using var response = await Panel().GetAsync("/panel/api/v1/network/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SaveSettingsAsync(object settings)
    {
        using var response = await Panel().PutAsJsonAsync("/panel/api/v1/network/settings", settings);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nothing_is_written_until_network_is_configured()
    {
        await TestMachine.RegisterAsync(_server.CreateClient(), "hw-1", "02:00:00:00:00:01");
        await SyncAsync();
        Assert.Empty(await _kea.HostsAsync());
        Assert.False((await StatusAsync()).TryGetProperty("sync", out var sync) && sync.ValueKind != JsonValueKind.Null);

        using var conf = await Panel().GetAsync("/panel/api/v1/network/kea-dhcp4.conf");
        Assert.Equal(HttpStatusCode.Conflict, conf.StatusCode);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_with_all_errors()
    {
        using var response = await Panel().PutAsJsonAsync("/panel/api/v1/network/settings", new
        {
            subnet = "192.168.77.0/24", keaSubnetId = 1, @interface = "eth0", dhcpServer = "192.168.77.1", gateway = "192.168.77.1",
            dnsServers = new[] { "192.168.77.1" }, poolStart = "192.168.77.200", poolEnd = "192.168.77.100", reservedStart = "192.168.77.101",
            leaseTimeSec = 10,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var details = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("details");
        var fields = details.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToList();
        Assert.Contains("poolEnd", fields);
        Assert.Contains("leaseTimeSec", fields);

        using var get = await Panel().GetAsync("/panel/api/v1/network/settings");
        Assert.False((await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Reservations_follow_registry_and_leave_manual_rows_alone()
    {
        var http = _server.CreateClient();
        await TestMachine.RegisterAsync(http, "hw-1", "02:00:00:00:00:01");
        await TestMachine.RegisterAsync(http, "hw-2", "02:00:00:00:00:02", "02:00:00:00:00:22");
        var (third, _) = await TestMachine.RegisterAsync(http, "hw-3", "02:00:00:00:00:03");

        // Ручная резервация администратора на MAC третьей машины: её не трогаем, машина остаётся без нашей строки.
        await _kea.ExecuteAsync(
            "INSERT INTO hosts (dhcp_identifier, dhcp_identifier_type, dhcp4_subnet_id, ipv4_address, hostname) VALUES ('\\x020000000003', 0, 1, 3232255290, 'manual')");

        await SaveSettingsAsync(Settings); // сохранение сразу синхронизирует

        var hosts = await _kea.HostsAsync();
        Assert.Equal(3, hosts.Count);
        Assert.Equal(("020000000003", "192.168.77.58", "manual", (string?)null), (hosts[0].Mac, hosts[0].Ip, hosts[0].Hostname, hosts[0].Context));
        Assert.Equal(("020000000001", 1L, "192.168.77.101", "pc-01"), (hosts[1].Mac, hosts[1].Subnet, hosts[1].Ip, hosts[1].Hostname));
        Assert.Equal(("020000000002", "192.168.77.102", "pc-02"), (hosts[2].Mac, hosts[2].Ip, hosts[2].Hostname));
        Assert.Contains("\"clubsrv\"", hosts[1].Context);

        var status = await StatusAsync();
        Assert.Contains(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "manualReservation");
        var sync = status.GetProperty("sync");
        Assert.True(sync.GetProperty("schemaOk").GetBoolean());
        Assert.Equal("29.0", sync.GetProperty("schemaVersion").GetString());
        Assert.Equal(2, sync.GetProperty("inserted").GetInt32());
        var reservations = status.GetProperty("reservations").EnumerateArray().ToList();
        Assert.Equal("192.168.77.101", reservations[0].GetProperty("ip").GetString());

        // Место №1 → №50: строка переезжает. №2 → №150 (в пуле): строка удаляется, машина в конфликте.
        var overview = await (await Panel().GetAsync("/panel/api/v1/machines")).Content.ReadFromJsonAsync<JsonElement>();
        var ids = overview.GetProperty("machines").EnumerateArray().ToDictionary(m => m.GetProperty("number").GetInt32(), m => m.GetProperty("id").GetGuid());
        (await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{ids[1]}", new { number = 50 })).EnsureSuccessStatusCode();
        (await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{ids[2]}", new { number = 150 })).EnsureSuccessStatusCode();
        await SyncAsync();

        hosts = await _kea.HostsAsync();
        Assert.Equal(["192.168.77.58", "192.168.77.150"], hosts.Select(h => h.Ip).ToArray());
        Assert.Equal("pc-01", hosts[1].Hostname);
        status = await StatusAsync();
        Assert.Contains(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "seatOutOfRange");
        Assert.Equal(1, status.GetProperty("sync").GetProperty("updated").GetInt32());
        Assert.Equal(1, status.GetProperty("sync").GetProperty("deleted").GetInt32());

        // Повтор без изменений ничего не пишет.
        await SyncAsync();
        status = await StatusAsync();
        Assert.Equal(0, status.GetProperty("sync").GetProperty("updated").GetInt32() + status.GetProperty("sync").GetProperty("inserted").GetInt32() + status.GetProperty("sync").GetProperty("deleted").GetInt32());
        _ = third;
    }

    [Fact]
    public async Task Swapped_network_cards_do_not_hit_the_unique_index()
    {
        var http = _server.CreateClient();
        await TestMachine.RegisterAsync(http, "hw-1", "02:00:00:00:00:01");
        await TestMachine.RegisterAsync(http, "hw-2", "02:00:00:00:00:02");
        await SaveSettingsAsync(Settings);

        // Карты переставили между ПК: помощники перерегистрировались с чужими MAC.
        await TestMachine.RegisterAsync(http, "hw-1", "02:00:00:00:00:02");
        await TestMachine.RegisterAsync(http, "hw-2", "02:00:00:00:00:01");
        await SyncAsync();

        var hosts = await _kea.HostsAsync();
        Assert.Equal([("020000000002", "192.168.77.101"), ("020000000001", "192.168.77.102")], hosts.Select(h => (h.Mac, h.Ip)).ToArray());
        Assert.Empty((await StatusAsync()).GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_kea_schema_blocks_writes()
    {
        await TestMachine.RegisterAsync(_server.CreateClient(), "hw-1", "02:00:00:00:00:01");
        await _kea.ExecuteAsync("UPDATE schema_version SET version = 30");
        await SaveSettingsAsync(Settings);

        Assert.Empty(await _kea.HostsAsync());
        var status = await StatusAsync();
        Assert.False(status.GetProperty("sync").GetProperty("schemaOk").GetBoolean());
        Assert.Contains(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "keaSchema");
    }

    [Fact]
    public async Task Unreachable_kea_database_is_a_warning_not_a_crash()
    {
        var options = _server.Services.GetRequiredService<KeaOptions>();
        options.ConnectionString = "Host=/var/run/postgresql;Database=kea_missing_" + Guid.NewGuid().ToString("N");
        await SaveSettingsAsync(Settings);

        var status = await StatusAsync();
        Assert.NotNull(status.GetProperty("sync").GetProperty("error").GetString());
        Assert.Contains(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "keaUnavailable");
    }

    [Fact]
    public async Task Foreign_dhcp_seen_by_a_pc_is_reported()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), "hw-1", "02:00:00:00:00:01");
        await SaveSettingsAsync(Settings);

        using (var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", bootTime = DateTimeOffset.UtcNow.AddMinutes(-5),
            volume = new { state = "none" },
            dhcpServers = new[] { "192.168.77.1", "192.168.1.1", "не адрес" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        await SyncAsync();
        var status = await StatusAsync();
        var foreign = Assert.Single(status.GetProperty("foreignDhcp").EnumerateArray());
        Assert.Equal("192.168.1.1", foreign.GetProperty("server").GetString());
        Assert.Equal("PC-01", foreign.GetProperty("seenBy")[0].GetString());
        Assert.Contains(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "foreignDhcp");

        // Помощник больше не видит чужой сервер — предупреждение закрывается.
        using (var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", volume = new { state = "none" }, dhcpServers = new[] { "192.168.77.1" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        await SyncAsync();
        status = await StatusAsync();
        Assert.Empty(status.GetProperty("foreignDhcp").EnumerateArray());
        Assert.DoesNotContain(status.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("kind").GetString() == "foreignDhcp");
    }

    [Fact]
    public async Task Generated_kea_config_passes_kea_config_test()
    {
        await SaveSettingsAsync(Settings);
        using var response = await Panel().GetAsync("/panel/api/v1/network/kea-dhcp4.conf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var dhcp4 = JsonDocument.Parse(text).RootElement.GetProperty("Dhcp4");
        var subnet = dhcp4.GetProperty("subnet4")[0];
        Assert.Equal("192.168.77.0/24", subnet.GetProperty("subnet").GetString());
        Assert.Equal("192.168.77.200 - 192.168.77.250", subnet.GetProperty("pools")[0].GetProperty("pool").GetString());
        Assert.Equal("postgresql", dhcp4.GetProperty("hosts-database").GetProperty("type").GetString());
        Assert.DoesNotContain("password", text);

        if (Environment.GetEnvironmentVariable("KEA_DHCP4") is not { Length: > 0 } kea)
        {
            return; // бинарника Kea нет — проверена только структура
        }

        // Kea проверяет, что интерфейс подсети есть в системе: на машине теста берём loopback.
        var settings = IpPlanTests.Default() with { Interface = "lo" };
        var hooks = Environment.GetEnvironmentVariable("KEA_HOOKS") ?? KeaConfig.HooksDirectory;
        var file = Path.Combine(Path.GetTempPath(), $"kea-test-{Guid.NewGuid():N}.conf");
        await File.WriteAllTextAsync(file, KeaConfig.Render(settings, hooksDirectory: hooks));
        try
        {
            var start = new ProcessStartInfo(kea, ["-t", file]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["KEA_HOOKS_PATH"] = hooks; // Kea 3.0 грузит хуки только из своего каталога
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
