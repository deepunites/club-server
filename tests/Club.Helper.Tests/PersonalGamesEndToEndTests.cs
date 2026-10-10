using Club.Helper.Core;
using Club.Server.Data;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

/// <summary>
/// Личный слой игр (Library:PersonalGames): помощник 1.5 с поддельной Windows против настоящего сервера и поддельного
/// TrueNAS. Диск места подключается на запись с CHAP и сбрасывается при каждой загрузке ПК — и тогда, когда на сервере
/// висит сессия прошлой загрузки (ПК выключили кнопкой); при перезапуске одной службы — не сбрасывается.
/// </summary>
public sealed class PersonalGamesEndToEndTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Seats = "tank/club/seats";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string SeatTarget = $"{Basename}:games-seat-01";

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private LibraryPublisher _publisher = null!;
    private LibraryRepository _library = null!;

    private readonly FakeProcesses _processes = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly InMemoryAssignmentCache _cache = new();
    private readonly FakeIdentity _identity = new(Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem(Seats);
        _server = new ServerFixture();
        var nas = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:PersonalGames"] = "true",
            ["Library:PersonalParent"] = Seats,
            ["Library:MasterZvol"] = Master,
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["Library:DiscoveryAddress"] = _nas.IscsiPortal,
            ["Library:VerifyDelayMs"] = "0",
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

    /// <summary>Помощник, только что запущенный на ПК с Windows <paramref name="windows"/>.</summary>
    private HelperLoop Helper(FakeWindowsStorage windows, DelegatingHandler? handler = null)
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.5.0" };
        var api = new DisklessApiClient(handler is null ? _server.CreateClient() : _server.CreateDefaultClient(handler), options, _credentials, _identity);
        var volumes = new VolumeManager(windows, _processes, options, TimeProvider.System, NullLogger<VolumeManager>.Instance);
        return new HelperLoop(api, volumes, _cache, _identity, options, NullLogger<HelperLoop>.Instance, new MasterManager(windows, options, TimeProvider.System, NullLogger<MasterManager>.Instance));
    }

    private async Task PublishAsync(string label)
    {
        await _publisher.RequestPublishAsync(label, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
    }

    private async Task<MachineRow> MachineAsync() =>
        (await _server.Services.GetRequiredService<MachineRepository>().FindAsync(_credentials.Current!.MachineId))!;

    [Fact]
    public async Task Seat_disk_is_writable_and_reset_on_every_boot()
    {
        await PublishAsync("v1");
        var windows = new FakeWindowsStorage();
        var helper = Helper(windows);
        await helper.TickAsync(CancellationToken.None); // IQN сервер узнаёт из отчёта — назначение приходит в ответе на него

        Assert.Equal("mounted", helper.LastReport!.State);
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.Equal([(SeatTarget, "games-seat-01")], windows.ChapLogins.Select(l => (l.Iqn, l.User)).ToList());
        var machine = await MachineAsync();
        Assert.Equal(("mounted", "v1"), (machine.VolumeState, machine.VolumeVersion));

        // Личный диск монтируется только из ответа на запрос при старте (со сбросом), а не из ответа на отчёт.
        var resets = _nas.Rollbacks($"{Seats}/games-seat-01");
        Assert.Equal(1, resets);

        // Перезапуск одной службы: диск подключён — сервер его не сбрасывает.
        _nas.AddSession(_identity.InitiatorIqn!, "games-seat-01");
        await Helper(windows).TickAsync(CancellationToken.None);
        Assert.Equal(resets, _nas.Rollbacks($"{Seats}/games-seat-01"));

        // ПК выключили кнопкой: у новой Windows сессий нет, а на сервере висит старая — сброс всё равно.
        await PublishAsync("v2");
        var rebooted = new FakeWindowsStorage();
        helper = Helper(rebooted);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(("mounted", "v2"), (helper.LastReport!.State, helper.LastReport.LibraryVersion));
        Assert.True(rebooted.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });

        await Helper(new FakeWindowsStorage()).TickAsync(CancellationToken.None);
        Assert.Equal(resets + 1, _nas.Rollbacks($"{Seats}/games-seat-01"));
    }

    [Fact]
    public async Task Empty_reply_does_not_take_the_personal_disk_away()
    {
        await PublishAsync("v1");
        var windows = new FakeWindowsStorage();
        var dropVolume = new DropVolume();
        var helper = Helper(windows, dropVolume);
        await helper.TickAsync(CancellationToken.None);
        Assert.True(windows.Sessions.ContainsKey(SeatTarget));

        dropVolume.Enabled = true; // сервер ответил на отчёт без назначения (диск готовится, сбой TrueNAS)
        await helper.TickAsync(CancellationToken.None);
        Assert.True(windows.Sessions.ContainsKey(SeatTarget));
        Assert.Equal("mounted", helper.LastReport!.State);
    }

    [Fact]
    public async Task Cached_personal_disk_is_not_mounted_while_the_server_is_briefly_unreachable()
    {
        await PublishAsync("v1");
        await Helper(new FakeWindowsStorage()).TickAsync(CancellationToken.None);
        Assert.True(VolumeManager.IsPersonal(_cache.Current!));

        // Перезагрузка, сервер перезапускается: соединение отклонено. Диск без сброса (с данными прошлого игрока) не берём.
        var rebooted = new FakeWindowsStorage();
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.5.0" };
        var down = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var api = new DisklessApiClient(down, options, _credentials, _identity);
        var helper = new HelperLoop(api, new VolumeManager(rebooted, _processes, options, TimeProvider.System, NullLogger<VolumeManager>.Instance), _cache, _identity, options,
            NullLogger<HelperLoop>.Instance);
        await helper.TickAsync(CancellationToken.None);
        Assert.Empty(rebooted.Sessions);
        Assert.Equal("none", helper.LastReport!.State);
    }

    /// <summary>Убирает назначение из ответов на отчёт (<c>PUT …/status</c>), когда включён.</summary>
    private sealed class DropVolume : DelegatingHandler
    {
        public bool Enabled { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Enabled && request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                body.Remove("volume");
                response.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
