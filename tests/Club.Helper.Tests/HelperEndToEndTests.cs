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

    private HelperLoop Helper(HttpClient? http = null)
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1 };
        var identity = new FakeIdentity(_hwid);
        var api = new DisklessApiClient(http ?? _server.CreateClient(), options, _credentials, identity);
        var volumes = new VolumeManager(_windows, _processes, options, TimeProvider.System, NullLogger<VolumeManager>.Instance);
        return new HelperLoop(api, volumes, _cache, identity, options, NullLogger<HelperLoop>.Instance);
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
