using Club.Helper.Core;
using Club.Server.Data;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

/// <summary>Помощник (с поддельной Windows) против настоящего сервера клуба и поддельного TrueNAS.</summary>
public sealed class HelperEndToEndTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Published = "tank/club/published";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private LibraryPublisher _publisher = null!;
    private LibraryRepository _library = null!;

    private readonly FakeWindowsStorage _windows = new();
    private readonly FakeProcesses _processes = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly InMemoryAssignmentCache _cache = new();
    private readonly string _hwid = Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem(Published);
        _server = new ServerFixture();
        var nas = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:MasterZvol"] = Master,
            ["Library:PublishedParent"] = Published,
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["TrueNas:Host"] = nas.Host,
            ["TrueNas:Port"] = nas.Port.ToString(),
            ["TrueNas:Username"] = nas.Username,
            ["TrueNas:ApiKey"] = nas.ApiKey,
            ["TrueNas:CaCertificatePath"] = nas.CaCertificatePath,
        })
        {
            _server.Settings[key] = value;
        }

        await _server.InitializeAsync();
        _publisher = _server.Services.GetRequiredService<LibraryPublisher>();
        _library = _server.Services.GetRequiredService<LibraryRepository>();
        await _server.Services.GetRequiredService<TrueNasStorage>().EnsureZvolAsync(Master, 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:managed"] = "1" });
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _nas.DisposeAsync();
    }

    private FakeIdentity? _identity;

    private HelperLoop Helper(HttpClient? http = null)
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1 };
        var identity = _identity ??= new FakeIdentity(_hwid);
        var api = new DisklessApiClient(http ?? _server.CreateClient(), options, _credentials, identity);
        var volumes = new VolumeManager(_windows, _processes, options, TimeProvider.System, NullLogger<VolumeManager>.Instance);
        var masters = new MasterManager(_windows, options, TimeProvider.System, NullLogger<MasterManager>.Instance);
        return new HelperLoop(api, volumes, _cache, identity, options, NullLogger<HelperLoop>.Instance, masters);
    }

    private async Task PublishAsync(string label)
    {
        await _publisher.RequestPublishAsync(label, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
    }

    private async Task<MachineRow> MachineAsync() =>
        (await _server.Services.GetRequiredService<MachineRepository>().FindAsync(_credentials.Current!.MachineId))!;

    [Fact]
    public async Task Helper_follows_published_versions_and_never_yanks_a_running_game()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("none", helper.LastReport!.State); // библиотеки ещё нет — ничего не монтирует
        Assert.Empty(_windows.Sessions);

        await PublishAsync("v1");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", helper.LastReport!.State);
        Assert.True(_windows.Sessions[$"{Basename}:games-v1"] is { ReadOnly: true, Offline: false, Letter: 'G' });
        var machine = await MachineAsync();
        Assert.Equal(("mounted", "v1", (bool?)true), (machine.VolumeState, machine.VolumeVersion, machine.VolumeRoVerified));

        await PublishAsync("v2");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("v2", helper.LastReport!.LibraryVersion);
        Assert.False(_windows.Sessions.ContainsKey($"{Basename}:games-v1"));

        _processes.Running['G'] = [@"G:\Riot Games\VALORANT\live\VALORANT.exe"];
        await PublishAsync("v3");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("switchPending", (await MachineAsync()).VolumeState);
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v2"));

        _processes.Running.Clear();
        await helper.TickAsync(CancellationToken.None);
        machine = await MachineAsync();
        Assert.Equal(("mounted", "v3"), (machine.VolumeState, machine.VolumeVersion));
        Assert.False(_windows.EverWritableOnline);
    }

    [Fact]
    public async Task Helper_reports_dhcp_servers_it_sees()
    {
        var helper = Helper();
        _identity!.DhcpServers.AddRange(["192.168.77.1", "192.168.1.1"]);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(["192.168.77.1", "192.168.1.1"], (await MachineAsync()).DhcpServers ?? []);
    }

    [Fact]
    public async Task Helper_reports_secure_boot_state()
    {
        var helper = Helper();
        _identity!.SecureBoot = new SecureBootFacts(true, true, true, false);
        await helper.TickAsync(CancellationToken.None);
        var reported = System.Text.Json.JsonDocument.Parse((await MachineAsync()).SecureBootJson!).RootElement;
        Assert.True(reported.GetProperty("enabled").GetBoolean());
        Assert.True(reported.GetProperty("thirdPartyCa2011").GetBoolean());
        Assert.False(reported.GetProperty("pca2011Revoked").GetBoolean());
    }

    [Fact]
    public async Task Helper_reports_version_contents_once_per_version()
    {
        _windows.Folders['G'] = ["Dota 2", "VALORANT"];
        var helper = Helper();
        await PublishAsync("v1");
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(1, _windows.FolderListings); // второй такт состав не шлёт
        var library = _server.Services.GetRequiredService<LibraryRepository>();
        Assert.Equal(["Dota 2", "VALORANT"], (await library.FindVersionAsync("v1"))!.Contents);

        _windows.Folders['G'] = ["Dota 2", "VALORANT", "Counter-Strike 2"];
        await PublishAsync("v2");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(["Counter-Strike 2", "Dota 2", "VALORANT"], (await library.FindVersionAsync("v2"))!.Contents);
    }

    [Fact]
    public async Task Superclient_edits_the_master_volume_and_closes_it_cleanly()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None); // регистрация, IQN инициатора
        var machine = await MachineAsync();
        Assert.Equal("iqn.1991-05.com.microsoft:pc-test", machine.InitiatorIqn);

        var editor = _server.Services.GetRequiredService<MasterEditor>();
        await editor.RequestOpenAsync(machine.Id, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        var masterIqn = "iqn.2005-10.org.freenas.ctl:club-master";

        // Помощник получает CHAP от сервера; «таргет» принимает только секрет, записанный в TrueNAS.
        var secret = _nas.Find("auth", a => a["user"]!.GetValue<string>() == "clubsrv-master")!["secret"]!.GetValue<string>();
        _windows.ExpectedChap = ("clubsrv-master", secret);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(new MasterReport("mounted", masterIqn, "M"), helper.LastMasterReport);
        Assert.True(_windows.Sessions[masterIqn] is { ReadOnly: false, Offline: false, Letter: 'M' });
        Assert.Equal("mounted", (await MachineAsync()).MasterState);
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-test", "club-master");

        // Закрытие: помощник сбрасывает кэш и отключается, только потом сервер убирает доступ.
        await editor.RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        Assert.Equal("closing", (await _server.Services.GetRequiredService<MasterRepository>().GetAsync()).State);

        await helper.TickAsync(CancellationToken.None);
        Assert.False(_windows.Sessions.ContainsKey(masterIqn));
        Assert.Contains(_windows.Log, l => l.StartsWith("flush+offline", StringComparison.Ordinal));
        Assert.Equal("none", (await MachineAsync()).MasterState);
        _nas.ClearSessions(); // сессия iSCSI закончилась вместе с отключением

        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        var closed = await _server.Services.GetRequiredService<MasterRepository>().GetAsync();
        Assert.Equal(("closed", false), (closed.State, closed.Dirty));
    }

    [Fact]
    public async Task Server_down_at_boot_mounts_the_last_known_version()
    {
        await PublishAsync("v1");
        await Helper().TickAsync(CancellationToken.None);
        Assert.Equal("v1", _cache.Current!.LibraryVersion);

        // ПК перезагрузился (сессий нет), сервер клуба недоступен.
        foreach (var iqn in _windows.Sessions.Keys.ToList())
        {
            await _windows.DisconnectAsync(iqn, CancellationToken.None);
        }

        var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var offline = Helper(dead);
        await offline.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", offline.LastReport!.State);
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v1"));

        // Сервер всё ещё лежит: том не отключается.
        await offline.TickAsync(CancellationToken.None);
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v1"));
    }

    [Fact]
    public async Task Revoked_tokens_lead_to_reregistration_on_the_same_seat()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var first = await MachineAsync();

        await _server.Services.GetRequiredService<MachineRepository>().RevokeCredentialsAsync(first.Id);
        await helper.TickAsync(CancellationToken.None);

        var again = await MachineAsync();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Number, again.Number);
        Assert.NotNull(again.LastSeenAt);
    }
}
