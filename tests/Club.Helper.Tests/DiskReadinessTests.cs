using System.Net;
using System.Text;
using Club.Helper.Core;
using Club.TestSupport;

namespace Club.Helper.Tests;

/// <summary>
/// Диски игр для шелла (SHELL_CHANGES п. 86, помощник 1.9.0): у каждого цикла — живое состояние диска
/// (<see cref="HelperLoop.Disk"/>), у помощника — <see cref="HelperLoops.Readiness"/>: <c>ready</c> и <c>disks</c> для
/// <c>ping</c>. <c>ready</c> — ни один диск не подключается (<c>attaching</c>) и не готовится сервером
/// (<c>preparing</c>). Сервер клуба настоящий (поверх поддельного TrueNAS); ответы с <c>X-Volume-Pending</c> и
/// <c>volumePending</c> для H: — подмена (<see cref="PendingSolo"/>): у настоящего сервера подготовка видна только через
/// 12 с ожидания.
/// </summary>
public sealed class DiskReadinessTests
{
    private static HelperOptions Options() => new() { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.9.0" };

    private static HelperLoops Helper(LibrariesStand stand, HttpClient http, FakeWindowsStorage windows, TimeProvider? clock = null)
    {
        var options = Options();
        var api = new DisklessApiClient(http, options, stand.Credentials, stand.Identity);
        return HelperLoops.Create(api, windows, new FakeProcesses(), stand.Identity, options, clock ?? TimeProvider.System, new ListLoggerFactory(), stand.Cache);
    }

    /// <summary>Перед каждым запросом к серверу — что помощник ответил бы на <c>ping</c> в этот момент.</summary>
    private sealed class Observer(Action<string> before) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            before($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Сервер, который готовит личный диск H:: 204 при старте с <c>X-Volume-Pending: <see cref="BootPending"/></c> (null —
    /// без заголовка) и ответ на отчёт H: без тома с <c>volumePending: <see cref="ReplyPending"/></c> (null — без поля) или
    /// 500 (<see cref="ReportFails"/>). G: — настоящий сервер.
    /// </summary>
    private sealed class PendingSolo : DelegatingHandler
    {
        public string? BootPending { get; set; } = "preparing";

        public string? ReplyPending { get; set; } = "preparing";

        /// <summary>Отчёт H: не принят: сервер отвечает 500.</summary>
        public bool ReportFails { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/libraries/solo/", StringComparison.Ordinal))
            {
                return base.SendAsync(request, cancellationToken);
            }

            if (request.Method == HttpMethod.Get)
            {
                var boot = new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
                if (BootPending is { } header)
                {
                    boot.Headers.Add(DisklessApiClient.VolumePendingHeader, header);
                }

                return Task.FromResult(boot);
            }

            if (ReportFails)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
            }

            var field = ReplyPending is { } pending ? $",\"volumePending\":\"{pending}\"" : "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($"{{\"serverTime\":\"2026-11-07T00:00:00Z\"{field}}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Disks_are_attaching_until_mounted_then_ready()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var windows = new FakeWindowsStorage();
        HelperLoops helper = null!;
        var atRequests = new List<(string Request, HelperReadiness Readiness)>();
        helper = Helper(stand, stand.Server.CreateDefaultClient(new Observer(request => atRequests.Add((request, helper.Readiness())))), windows);

        // Служба только что запустилась: первый такт не начался — оба диска подключаются, не готово.
        var start = helper.Readiness();
        Assert.False(start.Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Attaching), new LibraryDisk("solo", DiskStates.Attaching)], start.Disks);

        // Посреди подключения (вход в таргет сделан, ждём диск): буква уже известна, диск — attaching.
        var duringMount = new Dictionary<string, HelperReadiness>();
        windows.OnFindDisk = iqn => duringMount.TryAdd(iqn, helper.Readiness());
        await helper.TickAsync(CancellationToken.None);

        var g = duringMount[LibrariesStand.GamesSeat];
        Assert.False(g.Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Attaching, "G"), new LibraryDisk("solo", DiskStates.Attaching)], g.Disks);
        var h = duringMount[LibrariesStand.SoloSeat];
        Assert.False(h.Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Mounted, "G", "v1"), new LibraryDisk("solo", DiskStates.Attaching, "H")], h.Disks);

        // Гибрид в первый раз: IQN сервер узнаёт из отчёта G:, до него — 204 с X-Volume-Pending: preparing.
        var report = atRequests.First(r => r.Request.EndsWith("/status", StringComparison.Ordinal) && !r.Request.Contains("/libraries/", StringComparison.Ordinal));
        Assert.Equal(new LibraryDisk("games", DiskStates.Preparing), report.Readiness.Disks[0]);
        Assert.False(report.Readiness.Ready);

        var end = helper.Readiness();
        Assert.True(end.Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Mounted, "G", "v1"), new LibraryDisk("solo", DiskStates.Mounted, "H", "v1")], end.Disks);

        // Следующий такт проверяет подключённые тома: mounted всё время, без мигания attaching.
        duringMount.Clear();
        await helper.TickAsync(CancellationToken.None);
        Assert.NotEmpty(duringMount);
        Assert.All(duringMount.Values, r => Assert.True(r.Ready));
        Assert.True(helper.Readiness().Ready);
        Assert.Equal(end.Disks, helper.Readiness().Disks);
    }

    [Fact]
    public async Task Server_preparing_the_disk_is_preparing_until_the_reply_says_otherwise()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        var server = new PendingSolo();
        var helper = Helper(stand, stand.Server.CreateDefaultClient(server), new FakeWindowsStorage());

        // При старте 204 с preparing, ответ на отчёт — тоже preparing: диск H: ещё готовится, не готово.
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(new LibraryDisk("solo", DiskStates.Preparing), helper.Readiness().Disks[1]);
        Assert.False(helper.Readiness().Ready);

        // Ответ на отчёт без поля — монтировать нечего: прежний preparing от запроса при старте не держится.
        server.ReplyPending = null;
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(new LibraryDisk("solo", DiskStates.Unpublished), helper.Readiness().Disks[1]);
        Assert.True(helper.Readiness().Ready);

        // Сборка упала — failed (определённо недоступен); незнакомое значение более нового сервера — preparing.
        server.ReplyPending = "failed";
        await helper.TickAsync(CancellationToken.None);
        var failed = helper.Readiness().Disks[1];
        Assert.Equal((DiskStates.Failed, true), (failed.State, failed.Error is { Length: > 0 }));
        Assert.True(helper.Readiness().Ready);
        server.ReplyPending = "rebuilding";
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(DiskStates.Preparing, helper.Readiness().Disks[1].State);
        Assert.False(helper.Readiness().Ready);
    }

    [Theory]
    [InlineData("preparing", "preparing", false, DiskStates.Preparing)]
    [InlineData("preparing", null, false, DiskStates.Unpublished)]
    [InlineData(null, "preparing", false, DiskStates.Preparing)]
    [InlineData("failed", "failed", false, DiskStates.Failed)]
    [InlineData("preparing", null, true, DiskStates.Preparing)]
    [InlineData("failed", null, true, DiskStates.Failed)]
    [InlineData(null, null, true, DiskStates.Unpublished)]
    [InlineData("later", null, true, DiskStates.Preparing)]
    public async Task Status_reply_overrides_the_boot_answer(string? boot, string? reply, bool reportFails, string expected)
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        var server = new PendingSolo { BootPending = boot, ReplyPending = reply, ReportFails = reportFails };
        var helper = Helper(stand, stand.Server.CreateDefaultClient(server), new FakeWindowsStorage());

        // Ответ на отчёт решает; отчёт не принят (сервер ответил 500) — остаётся то, что сказал запрос при старте.
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(expected, helper.Libraries[0].Disk.State);
        Assert.Equal(expected is not DiskStates.Preparing, helper.Readiness().Ready);
    }

    [Fact]
    public async Task Without_the_server_disks_are_attaching_for_3_minutes_then_noServer()
    {
        await using var stand = await LibrariesStand.StartAsync();
        var clock = new ManualClock();
        var down = new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://club-server/") };
        var helper = Helper(stand, down, new FakeWindowsStorage(), clock);

        // Бездиск без сервера: назначения нет ни от сервера, ни в assignment.json — подключается, пока не прошло 3 минуты.
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal([DiskStates.Attaching, DiskStates.Attaching], helper.Readiness().Disks.Select(d => d.State));
        Assert.False(helper.Readiness().Ready);
        clock.Advance(HelperLoop.CachedPersonalAfter - TimeSpan.FromSeconds(1));
        Assert.False(helper.Readiness().Ready);

        // 3 минуты — noServer без нового такта (цикл H: может пропускать такты после медленного): готово, дисков нет.
        clock.Advance(TimeSpan.FromSeconds(1));
        var readiness = helper.Readiness();
        Assert.True(readiness.Ready);
        Assert.Equal([DiskStates.NoServer, DiskStates.NoServer], readiness.Disks.Select(d => d.State));
        Assert.All(readiness.Disks, d => Assert.False(string.IsNullOrEmpty(d.Error)));
    }

    [Fact]
    public async Task Hybrid_without_the_server_waits_for_its_cached_personal_disk()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var windows = new FakeWindowsStorage();
        await Helper(stand, stand.Http(), windows).TickAsync(CancellationToken.None);

        // Перезагрузка гибрида, сервер недоступен: сохранённые личные диски — через 3 минуты, до того attaching.
        var clock = new ManualClock();
        var rebooted = new FakeWindowsStorage();
        var helper = Helper(stand, new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://club-server/") }, rebooted, clock);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal([DiskStates.Attaching, DiskStates.Attaching], helper.Readiness().Disks.Select(d => d.State));

        // 3 минуты прошли, а такта ещё не было: диск будет подключён в такте — не noServer.
        clock.Advance(HelperLoop.CachedPersonalAfter);
        Assert.False(helper.Readiness().Ready);
        await helper.TickAsync(CancellationToken.None);
        Assert.True(helper.Readiness().Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Mounted, "G", "v1"), new LibraryDisk("solo", DiskStates.Mounted, "H", "v1")],
            helper.Readiness().Disks);
    }

    [Fact]
    public async Task Library_missing_on_the_server_is_disabled()
    {
        await using var stand = await LibrariesStand.StartAsync(solo: false);
        await stand.PublishAsync(stand.Games, "v1");
        var helper = Helper(stand, stand.Http(), new FakeWindowsStorage());

        await helper.TickAsync(CancellationToken.None);

        Assert.True(helper.Readiness().Ready);
        Assert.Equal([new LibraryDisk("games", DiskStates.Mounted, "G", "v1"), new LibraryDisk("solo", DiskStates.Disabled)], helper.Readiness().Disks);
    }

    [Fact]
    public async Task Nothing_published_is_unpublished_and_ready()
    {
        await using var stand = await LibrariesStand.StartAsync();
        var helper = Helper(stand, stand.Http(), new FakeWindowsStorage());

        await helper.TickAsync(CancellationToken.None);

        Assert.True(helper.Readiness().Ready);
        Assert.Equal([DiskStates.Unpublished, DiskStates.Unpublished], helper.Readiness().Disks.Select(d => d.State));
    }

    [Fact]
    public async Task Taken_letter_is_failed_and_a_reconnecting_session_is_attaching()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var windows = new FakeWindowsStorage();
        windows.LocalLetters.Add('H');
        var helper = Helper(stand, stand.Http(), windows);

        // H: занята картридером: попытка не удалась — failed с причиной; готово (G: подключён).
        await helper.TickAsync(CancellationToken.None);
        var h = helper.Readiness().Disks[1];
        Assert.Equal(("solo", DiskStates.Failed, "H", "drive letter H is in use"), (h.Library, h.State, h.Letter, h.Error));
        Assert.True(helper.Readiness().Ready);

        // Букву освободили — подключён; сессия G: восстанавливает связь — G: снова attaching, не готово.
        windows.LocalLetters.Remove('H');
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(DiskStates.Mounted, helper.Readiness().Disks[1].State);
        windows.RecoveringSessions.Add(LibrariesStand.GamesSeat);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(DiskStates.Attaching, helper.Readiness().Disks[0].State);
        Assert.False(helper.Readiness().Ready);
    }

    [Fact]
    public async Task Master_editing_on_this_pc_is_master()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var helper = Helper(stand, stand.Http(), new FakeWindowsStorage());
        await helper.TickAsync(CancellationToken.None);

        await stand.OpenMasterAsync(stand.Games);
        await helper.TickAsync(CancellationToken.None);

        Assert.True(helper.Readiness().Ready);
        Assert.Equal(new LibraryDisk("games", DiskStates.Master, "G", Error: "drive letter G: is given to the master volume (library editing)"),
            helper.Readiness().Disks[0]);
        Assert.Equal(DiskStates.Mounted, helper.Readiness().Disks[1].State);
    }

    [Fact]
    public async Task Tick_failing_before_the_mount_is_failed_not_attaching()
    {
        await using var stand = await LibrariesStand.StartAsync();
        await stand.PublishAsync(stand.Games, "v1");
        var options = Options();
        var api = new DisklessApiClient(stand.Http(), options, stand.Credentials, stand.Identity);
        var helper = HelperLoops.Create(api, new FakeWindowsStorage(), new FakeProcesses(), stand.Identity, options, TimeProvider.System, new ListLoggerFactory(),
            key => key is null ? new BrokenCache() : stand.Cache(key));

        // assignment.json не записать: такт G: падает после ответа сервера — не «подключается» навсегда, а failed.
        await helper.TickAsync(CancellationToken.None);
        var g = helper.Readiness().Disks[0];
        Assert.Equal((DiskStates.Failed, "There is not enough space on the disk."), (g.State, g.Error));
    }

    private sealed class BrokenCache : IAssignmentCache
    {
        public Task<VolumeAssignment?> LoadAsync(string hwid, CancellationToken ct) => Task.FromResult<VolumeAssignment?>(null);

        public Task SaveAsync(string hwid, VolumeAssignment? assignment, CancellationToken ct) => throw new IOException("There is not enough space on the disk.");
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
