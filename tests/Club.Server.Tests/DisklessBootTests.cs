using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Полный бездиск (docs/diskless-full.md): личный диск места — записываемый клон версии эталона с CHAP и группой из
/// IQN места; каждая загрузка — откат к @clean без изменения iSCSI; новая версия — новый клон; режим мастера.
/// </summary>
public sealed class DisklessBootTests : IAsyncLifetime
{
    private const string Image = "tank/club/diskless/win11";
    private const string Seats = "tank/club/diskless/seats";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string PanelToken = "panel-test-token";
    private const string Mac = "02:00:00:00:20:01";
    private const string MacPath = "02-00-00-00-20-01";

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private TrueNasStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem("tank/club/diskless");
        _nas.AddFilesystem(Seats);

        _server = new ServerFixture();
        var options = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:RunWorker"] = "false",
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["Library:DiscoveryAddress"] = _nas.IscsiPortal,
            ["Library:VerifyDelayMs"] = "0",
            ["Diskless:Enabled"] = "true",
            ["Diskless:ImageZvol"] = Image,
            ["Diskless:SeatsParent"] = Seats,
            ["Panel:AdminToken"] = PanelToken,
            ["TrueNas:Host"] = options.Host,
            ["TrueNas:Port"] = options.Port.ToString(),
            ["TrueNas:Username"] = options.Username,
            ["TrueNas:ApiKey"] = options.ApiKey,
            ["TrueNas:CaCertificatePath"] = options.CaCertificatePath,
        })
        {
            _server.Settings[key] = value;
        }

        await _server.InitializeAsync();
        _storage = _server.Services.GetRequiredService<TrueNasStorage>();
        await _storage.EnsureZvolAsync(Image, 64L << 30, "16K", new Dictionary<string, string> { ["clubsrv:role"] = "diskless-image" });
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _nas.DisposeAsync();
    }

    private HttpClient Panel()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", PanelToken);
        return client;
    }

    private async Task<TestMachine> DisklessSeatAsync(string mac = Mac)
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, mac);
        using var response = await Panel().PutAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/boot-mode", new { mode = "diskless" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return machine;
    }

    private async Task<(HttpStatusCode Status, string? Reason)> PublishAsync(string label)
    {
        using var response = await Panel().PostAsJsonAsync("/panel/api/v1/diskless/versions", new { label, comment = "test" });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? null
            : JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task<string> BootScriptAsync(string macPath = MacPath)
    {
        using var response = await _server.CreateClient().GetAsync($"/pxe/v1/machines/{macPath}/boot.ipxe");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Seat_waits_until_a_system_image_is_published()
    {
        await DisklessSeatAsync();
        var script = await BootScriptAsync();
        Assert.Contains("no system image is published yet", script);
        Assert.Contains("chain --autofree /pxe/v1/machines/${netX/mac:hexhyp}/boot.ipxe", script);
        Assert.DoesNotContain("sanboot", script);
    }

    [Fact]
    public async Task Seat_boots_its_own_writable_clone_behind_chap()
    {
        await DisklessSeatAsync();
        Assert.Equal((HttpStatusCode.NoContent, null), await PublishAsync("v1"));

        var script = await BootScriptAsync();
        Assert.StartsWith("#!ipxe\n", script);
        Assert.Contains("set initiator-iqn iqn.2026-10.local.club:seat-01\n", script);
        Assert.Contains("set username seat-01\n", script);
        Assert.Contains($"sanboot iscsi:192.168.77.10::3260::{Basename}:seat-01 || goto failed", script);

        var clone = await _storage.GetDatasetAsync($"{Seats}/seat-01");
        Assert.Equal(($"{Image}@img-v1", false), (clone!.Origin, clone.ReadOnly));
        Assert.NotNull(await _storage.GetSnapshotAsync($"{Seats}/seat-01@clean"));

        var extent = _nas.Find("extent", e => e["name"]!.GetValue<string>() == "seat-01")!;
        Assert.Equal(($"zvol/{Seats}/seat-01", false), (extent["disk"]!.GetValue<string>(), extent["ro"]!.GetValue<bool>()));
        var group = _nas.Find("initiator", g => g["comment"]!.GetValue<string>() == "clubsrv seat 01")!;
        Assert.Equal(["iqn.2026-10.local.club:seat-01"], group["initiators"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        var auth = _nas.Find("auth", a => a["user"]!.GetValue<string>() == "seat-01")!;
        Assert.Contains($"set password {auth["secret"]!.GetValue<string>()}\n", script);
        Assert.False(_nas.IsOpenToEveryone("seat-01"));
    }

    [Fact]
    public async Task Every_boot_resets_the_disk_without_changing_iscsi()
    {
        await DisklessSeatAsync();
        await PublishAsync("v1");
        await BootScriptAsync();
        var reloads = _nas.Reloads;
        var targets = _nas.Count("target");

        var again = await BootScriptAsync();
        Assert.Contains("sanboot iscsi:", again);
        Assert.Equal(1, _nas.Rollbacks($"{Seats}/seat-01"));
        Assert.Equal((reloads, targets), (_nas.Reloads, _nas.Count("target")));
    }

    [Fact]
    public async Task New_version_gives_the_seat_a_new_disk_on_next_boot()
    {
        await DisklessSeatAsync();
        await PublishAsync("v1");
        await BootScriptAsync();
        await PublishAsync("v2");

        await BootScriptAsync();
        Assert.Equal($"{Image}@img-v2", (await _storage.GetDatasetAsync($"{Seats}/seat-01"))!.Origin);
        Assert.Equal(0, _nas.Rollbacks($"{Seats}/seat-01"));
        Assert.NotNull(await _storage.GetSnapshotAsync($"{Image}@img-v1")); // откатная версия остаётся

        using var rollback = await Panel().PostAsync("/panel/api/v1/diskless/rollback", null);
        Assert.Equal(HttpStatusCode.NoContent, rollback.StatusCode);
        await BootScriptAsync();
        Assert.Equal($"{Image}@img-v1", (await _storage.GetDatasetAsync($"{Seats}/seat-01"))!.Origin);
    }

    [Fact]
    public async Task Running_seat_is_not_reset_by_a_boot_request()
    {
        var machine = await DisklessSeatAsync();
        await PublishAsync("v1");
        await BootScriptAsync();

        // Windows места работает: свежий отчёт помощника и сессия к диску места.
        using (var status = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new { helperVersion = "1.4.2", volume = new { state = "none" } }))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        _nas.AddSession("iqn.2026-10.local.club:seat-01", "seat-01");
        var script = await BootScriptAsync();
        Assert.Contains("still running from its disk", script);
        Assert.Equal(0, _nas.Rollbacks($"{Seats}/seat-01"));
    }

    [Fact]
    public async Task Master_install_hooks_the_image_and_starts_windows_setup()
    {
        var machine = await DisklessSeatAsync();
        using (var response = await Panel().PutAsJsonAsync("/panel/api/v1/diskless/master", new { machineId = machine.MachineId, install = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var script = await BootScriptAsync();
        Assert.Contains("set initiator-iqn iqn.2026-10.local.club:master\n", script);
        Assert.Contains($"sanhook --drive 0x80 iscsi:192.168.77.10::3260::{Basename}:diskless-master || goto failed", script);
        Assert.Contains("initrd /pxe/v1/files/winsetup/sources/boot.wim boot.wim", script);
        var extent = _nas.Find("extent", e => e["name"]!.GetValue<string>() == "diskless-master")!;
        Assert.Equal(($"zvol/{Image}", false), (extent["disk"]!.GetValue<string>(), extent["ro"]!.GetValue<bool>()));

        // Пока мастер подключён к эталону, публиковать нельзя.
        _nas.AddSession("iqn.2026-10.local.club:master", "diskless-master");
        Assert.Equal((HttpStatusCode.Conflict, "masterConnected"), await PublishAsync("v1"));
        _nas.ClearSessions();
        Assert.Equal((HttpStatusCode.NoContent, null), await PublishAsync("v1"));

        using (var response = await Panel().PutAsJsonAsync("/panel/api/v1/diskless/master", new { machineId = machine.MachineId, install = false }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Contains($"sanboot iscsi:192.168.77.10::3260::{Basename}:diskless-master", await BootScriptAsync());
    }

    [Fact]
    public async Task Local_machines_keep_booting_from_their_own_disk()
    {
        await TestMachine.RegisterAsync(_server.CreateClient(), null, Mac);
        await PublishAsync("v1");
        Assert.Contains("booting from local disk", await BootScriptAsync());
        Assert.Null(await _storage.GetDatasetAsync($"{Seats}/seat-01"));
    }

    [Fact]
    public async Task Boot_mode_is_validated()
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, Mac);
        using var bad = await Panel().PutAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/boot-mode", new { mode = "cloud" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var missing = await Panel().PutAsJsonAsync($"/panel/api/v1/machines/{Guid.NewGuid()}/boot-mode", new { mode = "diskless" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
