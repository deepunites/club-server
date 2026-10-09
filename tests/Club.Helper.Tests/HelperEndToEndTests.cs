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

    private FakeIdentity? _identity;

    private HelperLoop Helper(HttpClient? http = null, IMachineIdentity? identity = null)
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1 };
        identity ??= _identity ??= new FakeIdentity(_hwid);
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
        machine = await MachineAsync();
        Assert.Equal(("switchPending", "v2"), (machine.VolumeState, machine.VolumeVersion)); // ПК ещё на v2 — панель считает его там
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v2"));

        _processes.Running.Clear();
        await helper.TickAsync(CancellationToken.None);
        machine = await MachineAsync();
        Assert.Equal(("mounted", "v3"), (machine.VolumeState, machine.VolumeVersion));
        Assert.False(_windows.EverWritableOnline);
    }

    /// <summary>Считает отчёты помощника (<c>PUT …/status</c>), принятые сервером.</summary>
    private sealed class ReportCounter : DelegatingHandler
    {
        public int Reports { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                Reports++;
            }

            return response;
        }
    }

    [Fact]
    public async Task Open_files_on_the_old_version_keep_the_pc_reporting_switch_pending_every_tick()
    {
        // Стенд 2026-10-02, помощник 1.4.1: Windows не завершала сессию старой версии (на G: открыт проводник / Steam) —
        // такт падал целиком, отчётов не было, ПК в панели выглядел выключенным.
        var counter = new ReportCounter();
        var helper = Helper(_server.CreateDefaultClient(counter));
        await PublishAsync("v1");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", helper.LastReport!.State);

        _windows.BusySessions[$"{Basename}:games-v1"] = FakeWindowsStorage.SessionBusy;
        await PublishAsync("v2");
        await helper.TickAsync(CancellationToken.None);
        var machine = await MachineAsync();
        Assert.Equal(("switchPending", $"{Basename}:games-v1", "v1"), (machine.VolumeState, machine.VolumeIqn, machine.VolumeVersion));
        Assert.Equal($"new version v2 waits: volume in use (open files on G:): {FakeWindowsStorage.SessionBusy}", machine.VolumeError);

        var before = counter.Reports;
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(before + 2, counter.Reports); // отчёт каждый такт
        Assert.Equal("switchPending", (await MachineAsync()).VolumeState);
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v1"));
        Assert.False(_windows.Sessions.ContainsKey($"{Basename}:games-v2"));

        _windows.BusySessions.Clear(); // проводник закрыли
        await helper.TickAsync(CancellationToken.None);
        machine = await MachineAsync();
        Assert.Equal(("mounted", "v2", (string?)null), (machine.VolumeState, machine.VolumeVersion, machine.VolumeError));
        Assert.False(_windows.Sessions.ContainsKey($"{Basename}:games-v1"));
        Assert.False(_windows.EverWritableOnline);
    }

    [Fact]
    public async Task Unexpected_failure_while_applying_the_volume_is_reported_as_failed()
    {
        var counter = new ReportCounter();
        var helper = Helper(_server.CreateDefaultClient(counter));
        await PublishAsync("v1");
        await helper.TickAsync(CancellationToken.None);

        _processes.Error = new UnauthorizedAccessException("Access is denied"); // опрос процессов при смене версии упал
        await PublishAsync("v2");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("failed", helper.LastReport!.State);
        var machine = await MachineAsync();
        Assert.Equal(("failed", $"{Basename}:games-v1", (string?)null), (machine.VolumeState, machine.VolumeIqn, machine.VolumeVersion));
        Assert.Equal("library v2: Access is denied", machine.VolumeError);
        Assert.True(_windows.Sessions.ContainsKey($"{Basename}:games-v1")); // том не тронут

        var before = counter.Reports;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(before + 1, counter.Reports);

        _processes.Error = null;
        await helper.TickAsync(CancellationToken.None);
        machine = await MachineAsync();
        Assert.Equal(("mounted", "v2"), (machine.VolumeState, machine.VolumeVersion));
    }

    [Fact]
    public async Task Unexpected_failure_of_the_master_volume_is_reported_as_failed()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var machine = await MachineAsync();
        await _server.Services.GetRequiredService<MasterEditor>().RequestOpenAsync(machine.Id, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        var masterIqn = $"{Basename}:club-master";

        // Исключение не из тех, что MasterManager ждёт (InvalidOperation, IO, Timeout): раньше оно роняло такт без отчёта.
        _windows.ConnectChapError = new UnauthorizedAccessException("Access is denied");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(new MasterReport("failed", masterIqn, "M", "Access is denied"), helper.LastMasterReport);
        machine = await MachineAsync();
        Assert.Equal(("failed", "Access is denied", "none"), (machine.MasterState, machine.MasterError, machine.VolumeState));

        _windows.ConnectChapError = null;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", (await MachineAsync()).MasterState);
        Assert.True(_windows.Sessions[masterIqn] is { ReadOnly: false, Offline: false, Letter: 'M' });
    }

    [Fact]
    public async Task Helper_restart_that_cannot_read_iscsi_sessions_keeps_the_master_state_on_the_server()
    {
        // Мастер-том открыт и подключён на ПК; администратор закрывает правку, а служба помощника в это время
        // перезапустилась, и первый же опрос сессий падает. Пустой отчёт о мастер-томе сервер прочёл бы как «не
        // подключён» и сбросил бы mounted, хотя том, возможно, ещё на ПК.
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var machine = await MachineAsync();
        var editor = _server.Services.GetRequiredService<MasterEditor>();
        await editor.RequestOpenAsync(machine.Id, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", (await MachineAsync()).MasterState);
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-test", "club-master");
        await editor.RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);

        var restarted = Helper();
        _windows.ConnectedTargetsError = new UnauthorizedAccessException("Access is denied");
        await restarted.TickAsync(CancellationToken.None);
        Assert.Equal(new MasterReport(MasterManager.UnknownState, Error: "Access is denied"), restarted.LastMasterReport);
        machine = await MachineAsync();
        Assert.Equal(("mounted", "failed"), (machine.MasterState, machine.VolumeState)); // отчёт ушёл, мастер-том не сброшен

        // То же при таймауте PowerShell: том уже упёрся в него, мастер-том в этом такте не опрашивается — unknown, а не
        // failed без таргета.
        _windows.ConnectedTargetsError = new TimeoutException("PowerShell did not finish in 120 s");
        var timedOut = Helper();
        await timedOut.TickAsync(CancellationToken.None);
        Assert.Equal(MasterManager.UnknownState, timedOut.LastMasterReport!.State);
        Assert.Equal("mounted", (await MachineAsync()).MasterState);

        _windows.ConnectedTargetsError = null;
        await restarted.TickAsync(CancellationToken.None);
        Assert.False(_windows.Sessions.ContainsKey($"{Basename}:club-master")); // правка закрыта — сбросил кэш и отключил
        Assert.Equal("none", (await MachineAsync()).MasterState);
    }

    [Fact]
    public async Task Failure_after_an_unknown_master_report_does_not_reset_the_master_state()
    {
        // Мастер-том подключён; правку закрывают, а Windows не отвечает: отчёт unknown, том не отключён. Следующий сбой —
        // уже не таймаут, и последний отчёт — unknown: отчёт без master сервер прочёл бы как «не подключён» и сбросил бы
        // mounted у ПК, где том всё ещё подключён. Опора — последний известный ответ (mounted): failed с таргетом.
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var machine = await MachineAsync();
        var editor = _server.Services.GetRequiredService<MasterEditor>();
        await editor.RequestOpenAsync(machine.Id, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", (await MachineAsync()).MasterState);
        var masterIqn = $"{Basename}:club-master";
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-test", "club-master");
        await editor.RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);

        _windows.ConnectedTargetsError = new TimeoutException("PowerShell did not finish in 120 s");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(MasterManager.UnknownState, helper.LastMasterReport!.State);
        Assert.Equal("mounted", (await MachineAsync()).MasterState);
        Assert.True(_windows.Sessions.ContainsKey(masterIqn));

        _windows.ConnectedTargetsError = new UnauthorizedAccessException("Access is denied");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(new MasterReport("failed", masterIqn, "M", "Access is denied"), helper.LastMasterReport);
        machine = await MachineAsync();
        Assert.Equal(("failed", "Access is denied"), (machine.MasterState, machine.MasterError));

        _windows.ConnectedTargetsError = null;
        await helper.TickAsync(CancellationToken.None);
        Assert.False(_windows.Sessions.ContainsKey(masterIqn)); // правка закрыта — сбросил кэш и отключил
        Assert.Equal("none", (await MachineAsync()).MasterState);
    }

    [Fact]
    public async Task Windows_timeout_on_the_volume_skips_the_master_query_for_that_tick()
    {
        // Таймаут PowerShell (120 с) на томе: опрос сессий ради мастер-тома почти наверняка ждал бы ещё столько же, и отчёт
        // вместе с ним. В этом такте мастер-том не опрашивается: master=unknown, сервер прежнее состояние не меняет.
        var helper = Helper();
        await PublishAsync("v1");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", helper.LastReport!.State);
        var v1 = $"{Basename}:games-v1";

        // Сбой ушёл из VolumeManager исключением (опрос сессий): один опрос за такт, а не два.
        _windows.ConnectedTargetsError = new TimeoutException("PowerShell did not finish in 120 s");
        var calls = _windows.ConnectedTargetsCalls;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(calls + 1, _windows.ConnectedTargetsCalls);
        Assert.Equal(new MasterReport(MasterManager.UnknownState, Error: "not checked: timeout"), helper.LastMasterReport);
        var machine = await MachineAsync();
        Assert.Equal(("failed", "v1"), (machine.VolumeState, machine.VolumeVersion));

        // Сбой пойман в VolumeManager (поиск диска текущей версии): том остаётся, мастер-том тоже не опрашивается.
        _windows.ConnectedTargetsError = null;
        _windows.FindDiskErrors[v1] = new TimeoutException("PowerShell did not finish in 120 s");
        calls = _windows.ConnectedTargetsCalls;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(calls + 1, _windows.ConnectedTargetsCalls);
        Assert.Equal(MasterManager.UnknownState, helper.LastMasterReport!.State);
        machine = await MachineAsync();
        Assert.Equal(("failed", "v1", "not checked: timeout"), (machine.VolumeState, machine.VolumeVersion, machine.VolumeError));
        Assert.True(_windows.Sessions[v1] is { ReadOnly: true, Offline: false, Letter: 'G' });

        // Windows отвечает — мастер-том снова опрашивается.
        _windows.FindDiskErrors.Clear();
        calls = _windows.ConnectedTargetsCalls;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(calls + 2, _windows.ConnectedTargetsCalls);
        Assert.Null(helper.LastMasterReport); // мастер-том не назначен и не подключён
        Assert.Equal("mounted", (await MachineAsync()).VolumeState);
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
    public async Task Initiator_iqn_that_appears_later_makes_the_pc_eligible_without_helper_restart()
    {
        // Стенд 2026-10-02, помощник 1.4.0: MSiSCSI остановлена при старте службы — IQN нет, ПК нельзя открыть мастер-том.
        var source = new FakeFactsSource(_hwid);
        var clock = new ManualClock();
        var options = new HelperOptions();
        var helper = Helper(identity: new CachedMachineIdentity(source, options, clock, NullLogger<CachedMachineIdentity>.Instance));
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal((1, 0), (source.FullReads, source.IqnReads)); // регистрация и отчёты первого такта не ждут MSiSCSI
        var editor = _server.Services.GetRequiredService<MasterEditor>();
        var machine = await MachineAsync();
        Assert.Equal("noInitiator", (await Assert.ThrowsAsync<LibraryRequestException>(() => editor.RequestOpenAsync(machine.Id, "test"))).Reason);

        source.InitiatorIqn = "iqn.1991-05.com.microsoft:pc-test"; // служба инициатора запущена
        clock.Advance(TimeSpan.FromSeconds(options.PollIntervalSec)); // следующий такт
        await helper.TickAsync(CancellationToken.None);

        Assert.Equal("iqn.1991-05.com.microsoft:pc-test", (await MachineAsync()).InitiatorIqn);
        await editor.RequestOpenAsync(machine.Id, "test");
        Assert.Equal((1, 1), (source.FullReads, source.IqnReads));
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
