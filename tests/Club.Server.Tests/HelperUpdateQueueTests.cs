using Club.Server.Data;
using Club.Server.HelperUpdates;
using Club.Server.Imaging;
using Club.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static Club.Server.HelperUpdates.HelperUpdateCodes;

namespace Club.Server.Tests;

/// <summary>
/// Очередь поручений самообновления помощника (<see cref="HelperUpdateRepository"/>, spec 2026-11-06, п. 3.3): запуск и
/// отказы, обработка отчёта G:, предел активных, работник очереди, пауза, отмена. Время — только из параметра <c>now</c>;
/// сборки — во временном каталоге <c>tools</c>, база — временная, общая на класс (таблицы чистятся перед каждым тестом).
/// </summary>
public sealed class HelperUpdateQueueTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private const string Build181 = "ClubDisklessHelper-1.8.1.exe";
    private const string Build170 = "ClubDisklessHelper-1.7.0.exe";
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _pxeRoot = Path.Combine(Path.GetTempPath(), $"club-helperqueue-{Guid.NewGuid():N}");
    private string Tools => Path.Combine(_pxeRoot, "tools");
    private NpgsqlDataSource Db => server.Services.GetRequiredService<NpgsqlDataSource>();

    public async Task InitializeAsync()
    {
        await using var c = await Db.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM helper_updates; UPDATE diskless_image SET master_machine_id = NULL; DELETE FROM machines");
        Put(Build181, "helper 1.8.1");
        Put(Build170, "helper 1.7.0");
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_pxeRoot))
        {
            Directory.Delete(_pxeRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private HelperUpdateRepository Repo(int maxActive = 3) =>
        new(Db, Builds(), new HelperUpdateOptions { MaxActive = maxActive }, NullLogger<HelperUpdateRepository>.Instance);

    private HelperBuilds Builds() =>
        new(new HelperUpdateOptions(), new ImagingOptions { PxeRoot = _pxeRoot }, TimeProvider.System, NullLogger<HelperBuilds>.Instance);

    private HelperBuild Build(string file) => Builds().Find(file)!;

    /// <summary>Файл сборки в <c>tools</c>, изменён час назад (старше <c>MinFileAgeSec</c>).</summary>
    private void Put(string name, string content)
    {
        Directory.CreateDirectory(Tools);
        var path = Path.Combine(Tools, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
    }

    /// <summary>Машина с номером места; по умолчанию одобренный гибрид с помощником 1.8.0, на связи в <see cref="T0"/>.</summary>
    private async Task<MachineRow> MachineAsync(
        bool approved = true, string bootMode = "local", string helper = "1.8.0", string? helperBuild = null, DateTimeOffset? lastSeen = null)
    {
        var machine = await server.Services.GetRequiredService<MachineRepository>().CreateAsync(
            new MachineRegistration(Guid.NewGuid().ToString("N"), "PC", [Mac()], "10.0.0.5", helper, null),
            approved);
        await using var c = await Db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE machines SET boot_mode = @bootMode, helper_build = @helperBuild, last_seen_at = @seen WHERE id = @id",
            new { id = machine.Id, bootMode, helperBuild, seen = lastSeen ?? T0 });
        return (await server.Services.GetRequiredService<MachineRepository>().FindAsync(machine.Id))!;
    }

    private static string Mac()
    {
        var b = Guid.NewGuid().ToByteArray();
        return $"02:00:00:{b[0]:x2}:{b[1]:x2}:{b[2]:x2}";
    }

    private async Task SeenAsync(MachineRow machine, DateTimeOffset at)
    {
        await using var c = await Db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE machines SET last_seen_at = @at WHERE id = @id", new { id = machine.Id, at });
    }

    /// <summary>Строка <c>helper_updates</c> для проверок (timestamptz → DateTimeOffset через DapperSetup).</summary>
    private sealed class Job
    {
        public Guid Id { get; init; }
        public string State { get; init; } = "";
        public string? Progress { get; init; }
        public string? Reason { get; init; }
        public string? Error { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? ActivatedAt { get; init; }
        public DateTimeOffset? ProgressAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string? RequestedBy { get; init; }
    }

    /// <summary>Последнее поручение ПК (из двух с одним временем — незавершённое).</summary>
    private async Task<Job> JobAsync(MachineRow machine)
    {
        DapperSetup.Ensure();
        await using var c = await Db.OpenConnectionAsync();
        return await c.QuerySingleAsync<Job>(
            """
            SELECT id, state, progress, reason, error, message, updated_at, activated_at, progress_at, finished_at, requested_by
            FROM helper_updates WHERE machine_id = @id ORDER BY created_at DESC, (finished_at IS NULL) DESC LIMIT 1
            """,
            new { id = machine.Id });
    }

    private static HelperUpdateReport Step(Guid id, string state, string? reason = null) => new(id.ToString(), state, reason, null, null);

    private Task<HelperUpdateOffer?> ReportAsync(HelperUpdateRepository repo, MachineRow machine, DateTimeOffset now,
        HelperUpdateReport? report = null, string helper = "1.8.0", string? helperBuild = null) =>
        repo.OnReportAsync(machine.Id, helper, helperBuild, report, now);

    [Fact]
    public async Task Launch_RefusalOrder()
    {
        var repo = Repo();
        var target = Build(Build181);
        // Каждый ПК не проходит свою проверку и все следующие: отказ — по первой в порядке spec п. 3.3.
        var notApproved = await MachineAsync(approved: false, bootMode: "diskless", helper: "1.7.0", helperBuild: target.Sha256);
        var diskless = await MachineAsync(bootMode: "diskless", helper: "1.7.0", helperBuild: target.Sha256);
        var tooOld = await MachineAsync(helper: "1.7.0+abc", helperBuild: target.Sha256);
        var busy = await MachineAsync();
        var current = await MachineAsync(helperBuild: target.Sha256);
        var ok = await MachineAsync(helperBuild: new string('0', 64));
        Assert.Equal([busy.Number], (await repo.LaunchAsync(Build181, [busy.Number], false, "panel", T0)).Queued);
        await using (var c = await Db.OpenConnectionAsync())
        {
            // Уже есть поручение — отказ раньше, чем «уже эта сборка».
            await c.ExecuteAsync("UPDATE machines SET helper_build = @sha WHERE id = @id", new { sha = target.Sha256, id = busy.Id });
        }

        var numbers = new[] { 9999, notApproved.Number, diskless.Number, tooOld.Number, busy.Number, current.Number, ok.Number };
        var result = await repo.LaunchAsync(Build181, numbers, false, "panel", T0);
        Assert.Equal([ok.Number], result.Queued);
        Assert.False(result.Paused);
        Assert.Equal(
            [
                new LaunchRefusal(notApproved.Number, Refusals.NotApproved), new LaunchRefusal(diskless.Number, Refusals.Diskless),
                new LaunchRefusal(tooOld.Number, Refusals.HelperTooOld), new LaunchRefusal(busy.Number, Refusals.Busy),
                new LaunchRefusal(current.Number, Refusals.Current), new LaunchRefusal(9999, Refusals.Unknown),
            ],
            result.Refused);

        var job = await JobAsync(ok);
        Assert.Equal((States.Queued, "panel"), (job.State, job.RequestedBy));

        // Сборка старше 1.8.0 — «только вручную»: отказ раньше «уже есть поручение» и «уже эта сборка».
        var old = await repo.LaunchAsync(Build170, [tooOld.Number, busy.Number, current.Number], false, "panel", T0);
        Assert.Empty(old.Queued);
        Assert.Equal(
            [
                new LaunchRefusal(tooOld.Number, Refusals.HelperTooOld), new LaunchRefusal(busy.Number, Refusals.TargetTooOld),
                new LaunchRefusal(current.Number, Refusals.TargetTooOld),
            ],
            old.Refused);
    }

    [Fact]
    public async Task Launch_AllHybrids_TakesLocalMachines()
    {
        var repo = Repo();
        var a = await MachineAsync();
        var seat = await MachineAsync(bootMode: "diskless");
        var b = await MachineAsync();
        var pending = await MachineAsync(approved: false);

        var result = await repo.LaunchAsync(Build181, [seat.Number, a.Number], allHybrids: true, "panel", T0);
        Assert.Equal([a.Number, b.Number], result.Queued);
        Assert.Equal([new LaunchRefusal(seat.Number, Refusals.Diskless), new LaunchRefusal(pending.Number, Refusals.NotApproved)], result.Refused);
    }

    [Fact]
    public async Task Launch_DuringPause_CreatesPaused()
    {
        var repo = Repo();
        var a = await MachineAsync();
        var b = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number], false, "panel", T0);
        await using (var c = await Db.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE helper_updates SET state = 'paused'");
        }

        var result = await repo.LaunchAsync(Build181, [b.Number], false, "panel", T0);
        Assert.Equal([b.Number], result.Queued);
        Assert.True(result.Paused);
        Assert.Equal(States.Paused, (await JobAsync(b)).State);

        // На паузе ПК поручение не получает.
        Assert.Null(await ReportAsync(repo, b, T0));
        Assert.Equal(States.Paused, (await JobAsync(b)).State);
    }

    [Fact]
    public async Task Report_PromotesOnlyOwnQueued_UpToMaxActive()
    {
        var repo = Repo(maxActive: 2);
        var pcs = new[] { await MachineAsync(), await MachineAsync(), await MachineAsync(), await MachineAsync() };
        await repo.LaunchAsync(Build181, pcs.Select(p => p.Number).ToList(), false, "panel", T0);
        var target = Build(Build181);

        var offer = await ReportAsync(repo, pcs[0], T0);
        var job = await JobAsync(pcs[0]);
        Assert.Equal(new HelperUpdateOffer(job.Id, "1.8.1", target.Sha256, target.Size, "pxe/v1/files/tools/" + Build181), offer);
        Assert.Equal((States.Active, T0, T0), (job.State, job.ActivatedAt, job.UpdatedAt));
        // Повышается только поручение того, кто отчитался.
        Assert.Equal(States.Queued, (await JobAsync(pcs[1])).State);

        Assert.NotNull(await ReportAsync(repo, pcs[1], T0.AddSeconds(5)));
        Assert.Null(await ReportAsync(repo, pcs[2], T0.AddSeconds(10)));
        Assert.Equal(States.Queued, (await JobAsync(pcs[2])).State);
        Assert.Equal(States.Queued, (await JobAsync(pcs[3])).State);

        // Активное поручение приходит в каждом ответе, то же самое.
        Assert.Equal(offer, await ReportAsync(repo, pcs[0], T0.AddSeconds(30)));
    }

    [Fact]
    public async Task Report_ConcurrentReports_ExactlyMaxActive()
    {
        var repo = Repo(maxActive: 3);
        var pcs = new List<MachineRow>();
        for (var i = 0; i < 12; i++)
        {
            pcs.Add(await MachineAsync());
        }

        await repo.LaunchAsync(Build181, pcs.Select(p => p.Number).ToList(), false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, pcs[0], T0));
        Assert.NotNull(await ReportAsync(repo, pcs[1], T0));

        // Десять ПК отчитываются в одну секунду при двух активных — активных ровно три (Review Focus 1).
        var offers = await Task.WhenAll(pcs.Skip(2).Select(p => ReportAsync(repo, p, T0.AddSeconds(1))));
        Assert.Single(offers, o => o is not null);
        await using var c = await Db.OpenConnectionAsync();
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM helper_updates WHERE state = 'active'"));
        Assert.Equal(9, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM helper_updates WHERE state = 'queued'"));
    }

    [Fact]
    public async Task Report_Confirmed_MarksDone()
    {
        var repo = Repo();
        var pc = await MachineAsync();
        var target = Build(Build181);
        await repo.LaunchAsync(Build181, [pc.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, pc, T0))!.Id;

        await ReportAsync(repo, pc, T0.AddSeconds(30), Step(id, Progress.Downloading));
        var job = await JobAsync(pc);
        Assert.Equal((Progress.Downloading, T0.AddSeconds(30), T0.AddSeconds(30)), (job.Progress, job.ProgressAt, job.UpdatedAt));

        // Новая сборка уже работает и проверяется — это ещё не «готово».
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddMinutes(5), Step(id, Progress.Confirming), helperBuild: target.Sha256));
        Assert.Equal((States.Active, Progress.Confirming), ((await JobAsync(pc)).State, (await JobAsync(pc)).Progress));

        Assert.Null(await ReportAsync(repo, pc, T0.AddMinutes(16), Step(id, Progress.Confirmed), helperBuild: target.Sha256));
        job = await JobAsync(pc);
        Assert.Equal((States.Done, T0.AddMinutes(16), T0.AddMinutes(16)), (job.State, job.FinishedAt, job.UpdatedAt));
    }

    [Fact]
    public async Task Report_HelperBuildEqualsTarget_DoneWithoutOffer()
    {
        var repo = Repo(maxActive: 2);
        var target = Build(Build181);
        var queued = await MachineAsync();
        var paused = await MachineAsync();
        var active = await MachineAsync();
        var applying = await MachineAsync();
        await repo.LaunchAsync(Build181, [queued.Number, paused.Number, active.Number, applying.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, active, T0));
        var applyingId = (await ReportAsync(repo, applying, T0))!.Id;
        await ReportAsync(repo, applying, T0.AddSeconds(30), Step(applyingId, Progress.Applying));
        await using (var c = await Db.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE helper_updates SET state = 'paused' WHERE machine_id = @id", new { id = paused.Id });
        }

        // ПК обновили вручную: отчёт показывает целевую сборку — поручение «готово» без выдачи.
        foreach (var pc in new[] { queued, paused, active })
        {
            Assert.Null(await ReportAsync(repo, pc, T0.AddMinutes(1), helperBuild: target.Sha256));
            var job = await JobAsync(pc);
            Assert.Equal((States.Done, T0.AddMinutes(1)), (job.State, job.FinishedAt));
        }

        // После applying целевая сборка — это сама замена: ждём confirmed.
        Assert.NotNull(await ReportAsync(repo, applying, T0.AddMinutes(1), helperBuild: target.Sha256));
        Assert.Equal(States.Active, (await JobAsync(applying)).State);
    }

    [Fact]
    public async Task Report_Failed_PausesQueue_ExceptHelperTooOld()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, a, T0))!.Id;

        Assert.Null(await ReportAsync(repo, a, T0.AddMinutes(2),
            new HelperUpdateReport(id.ToString(), Progress.Failed, null, Errors.HashMismatch, "sha256 " + new string('x', 600))));
        var job = await JobAsync(a);
        Assert.Equal((States.Failed, Errors.HashMismatch, T0.AddMinutes(2)), (job.State, job.Error, job.FinishedAt));
        Assert.Equal(500, job.Message!.Length);
        Assert.Equal(States.Paused, (await JobAsync(b)).State);
        Assert.Null(await ReportAsync(repo, b, T0.AddMinutes(3)));

        // Повтор отчёта по завершённому поручению ничего не меняет.
        await ReportAsync(repo, a, T0.AddMinutes(3), new HelperUpdateReport(id.ToString(), Progress.Failed, null, Errors.SwapFailed, null));
        Assert.Equal((Errors.HashMismatch, T0.AddMinutes(2)), ((await JobAsync(a)).Error, (await JobAsync(a)).UpdatedAt));

        // Незнакомая ошибка — пропускается, но ошибка остаётся ошибкой: поручение failed, пауза.
        Assert.Equal(1, await repo.ResumeAsync(T0.AddMinutes(4)));
        await repo.LaunchAsync(Build181, [c.Number], false, "panel", T0.AddMinutes(4));
        var bid = (await ReportAsync(repo, b, T0.AddMinutes(5)))!.Id;
        await ReportAsync(repo, b, T0.AddMinutes(6), new HelperUpdateReport(bid.ToString(), Progress.Failed, null, "weird", "boom"));
        job = await JobAsync(b);
        Assert.Equal((States.Failed, null, "boom"), (job.State, job.Error, job.Message));
        Assert.Equal(States.Paused, (await JobAsync(c)).State);
    }

    [Fact]
    public async Task Report_OldHelperOnActive_FailedHelperTooOld_NoPause()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, a, T0));

        // ПК перезалили (h1e с 1.6.0): поручение — ошибка helperTooOld, очередь не на паузе.
        Assert.Null(await ReportAsync(repo, a, T0.AddMinutes(1), helper: "1.6.0"));
        var job = await JobAsync(a);
        Assert.Equal((States.Failed, Errors.HelperTooOld, T0.AddMinutes(1)), (job.State, job.Error, job.FinishedAt));
        Assert.Equal(States.Queued, (await JobAsync(b)).State);
        Assert.NotNull(await ReportAsync(repo, b, T0.AddMinutes(2)));
    }

    [Fact]
    public async Task Offer_OnlyForActive_AndEligibleNow()
    {
        var repo = Repo(maxActive: 1);
        var master = await MachineAsync(bootMode: "diskless");
        var staleMaster = await MachineAsync(bootMode: "diskless");
        var other = await MachineAsync();
        await using var c = await Db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO seat_disks (machine_id, kind, zvol, target_name, initiator_iqn, chap_user, chap_secret, state, last_boot_at)
            VALUES (@master, 'master', 'tank/golden', 'golden', 'iqn.master', 'u', 's', 'ready', @boot),
                   (@stale, 'master', 'tank/golden2', 'golden2', 'iqn.master2', 'u', 's', 'ready', @staleBoot)
            """,
            new { master = master.Id, stale = staleMaster.Id, boot = T0.AddMinutes(-30), staleBoot = T0.AddHours(-2) });

        // Мастер эталона загрузился с эталона после включения режима — его можно обновлять (обновление попадёт в эталон).
        await c.ExecuteAsync("UPDATE diskless_image SET master_machine_id = @id, updated_at = @at", new { id = master.Id, at = T0.AddHours(-1) });
        Assert.Null(await repo.IneligibleReasonAsync(master.Id));
        Assert.Equal([master.Number], (await repo.LaunchAsync(Build181, [master.Number], false, "panel", T0)).Queued);

        // Режим мастера включён, а с эталона ПК ещё не загружался — это место бездиска.
        await c.ExecuteAsync("UPDATE diskless_image SET master_machine_id = @id, updated_at = @at", new { id = staleMaster.Id, at = T0.AddHours(-1) });
        Assert.Equal(Refusals.Diskless, await repo.IneligibleReasonAsync(staleMaster.Id));
        Assert.Equal([new LaunchRefusal(staleMaster.Number, Refusals.Diskless)], (await repo.LaunchAsync(Build181, [staleMaster.Number], false, "panel", T0)).Refused);

        await c.ExecuteAsync("UPDATE diskless_image SET master_machine_id = @id, updated_at = @at", new { id = master.Id, at = T0.AddHours(-1) });
        await repo.LaunchAsync(Build181, [other.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, master, T0));
        // Очередь занята мастером: другой ПК поручение не получает — поле только для активного.
        Assert.Null(await ReportAsync(repo, other, T0));

        // Сеанс мастера закрыли раньше, чем прошла замена: cancelled/diskless, поручения нет, место освобождается.
        await c.ExecuteAsync("UPDATE diskless_image SET master_machine_id = NULL, updated_at = @at", new { at = T0.AddMinutes(5) });
        Assert.Null(await ReportAsync(repo, master, T0.AddMinutes(6)));
        var job = await JobAsync(master);
        Assert.Equal((States.Cancelled, Errors.Diskless, T0.AddMinutes(6)), (job.State, job.Error, job.FinishedAt));
        Assert.NotNull(await ReportAsync(repo, other, T0.AddMinutes(7)));
    }

    [Fact]
    public async Task Report_UnknownValues_Ignored()
    {
        var repo = Repo();
        var pc = await MachineAsync();
        await repo.LaunchAsync(Build181, [pc.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, pc, T0))!.Id;

        // Незнакомые значения — журнал и пропуск, без исключений; поручение продолжается.
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddSeconds(30), new HelperUpdateReport("not-guid", "weird", null, "x", null)));
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddSeconds(60), new HelperUpdateReport(id.ToString(), "weird", "x", null, null)));
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddSeconds(90), new HelperUpdateReport(Guid.NewGuid().ToString(), Progress.Failed, null, Errors.HashMismatch, null)));
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddSeconds(120), new HelperUpdateReport(null, null, null, null, null), helperBuild: "zz"));
        Assert.NotNull(await ReportAsync(repo, pc, T0.AddSeconds(150), Step(id, Progress.Waiting, "lunch")));
        var job = await JobAsync(pc);
        Assert.Equal((States.Active, Progress.Waiting, null), (job.State, job.Progress, job.Reason));

        // Отчёт о чужом поручении не трогает его.
        var other = await MachineAsync();
        Assert.Null(await ReportAsync(repo, other, T0.AddSeconds(160), Step(id, Progress.Confirmed)));
        Assert.Equal(States.Active, (await JobAsync(pc)).State);
    }

    [Fact]
    public async Task Sweep_SilentDownloading_Requeued()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, a, T0))!.Id;
        await ReportAsync(repo, a, T0.AddSeconds(30), Step(id, Progress.Downloading));
        await SeenAsync(a, T0.AddSeconds(30));

        await repo.SweepAsync(T0.AddMinutes(10));
        Assert.Equal(States.Active, (await JobAsync(a)).State);

        // ПК выключили во время скачивания: через 10 минут молчания — снова в очереди, место — другому.
        await repo.SweepAsync(T0.AddMinutes(11));
        var job = await JobAsync(a);
        Assert.Equal((States.Queued, null, T0.AddMinutes(11)), (job.State, job.Progress, job.UpdatedAt));
        Assert.NotNull(await ReportAsync(repo, b, T0.AddMinutes(12)));
    }

    [Fact]
    public async Task Sweep_Waiting30Min_Requeued()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, a, T0))!.Id;
        await ReportAsync(repo, a, T0.AddMinutes(1), Step(id, Progress.Waiting, Reasons.Session));

        // Долгий сеанс: ПК отчитывается каждые 30 с, всё время «ждёт».
        for (var minute = 2; minute <= 31; minute++)
        {
            await ReportAsync(repo, a, T0.AddMinutes(minute), Step(id, Progress.Waiting, minute < 20 ? Reasons.Session : Reasons.Command));
            await SeenAsync(a, T0.AddMinutes(minute));
        }

        await repo.SweepAsync(T0.AddMinutes(30));
        var job = await JobAsync(a);
        Assert.Equal((States.Active, Reasons.Command), (job.State, job.Reason));

        await repo.SweepAsync(T0.AddMinutes(31).AddSeconds(1));
        Assert.Equal(States.Queued, (await JobAsync(a)).State);
    }

    [Fact]
    public async Task Sweep_ApplyingSilent_NoReturnAndPause()
    {
        var repo = Repo(maxActive: 2);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        var d = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number, c.Number, d.Number], false, "panel", T0);
        var aid = (await ReportAsync(repo, a, T0))!.Id;
        var bid = (await ReportAsync(repo, b, T0))!.Id;
        await ReportAsync(repo, a, T0.AddMinutes(1), Step(aid, Progress.Applying));
        await SeenAsync(a, T0.AddMinutes(1));
        await ReportAsync(repo, b, T0.AddMinutes(1), Step(bid, Progress.Confirming));

        // b отчитывается, но без поручения (новая сборка не может его прислать) — тоже noReturn.
        for (var minute = 2; minute <= 11; minute++)
        {
            await ReportAsync(repo, b, T0.AddMinutes(minute));
            await SeenAsync(b, T0.AddMinutes(minute));
        }

        await repo.SweepAsync(T0.AddMinutes(11));
        Assert.Equal(States.Active, (await JobAsync(a)).State);

        await repo.SweepAsync(T0.AddMinutes(11).AddSeconds(1));
        foreach (var pc in new[] { a, b })
        {
            var job = await JobAsync(pc);
            Assert.Equal((States.Failed, Errors.NoReturn, T0.AddMinutes(11).AddSeconds(1)), (job.State, job.Error, job.FinishedAt));
        }

        Assert.Equal(States.Paused, (await JobAsync(c)).State);
        Assert.Equal(States.Paused, (await JobAsync(d)).State);
    }

    [Fact]
    public async Task Sweep_ActiveNoProgressWhileReporting_NoProgressAndPause()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, a, T0));

        // Помощник отчитывается, но о поручении молчит. b тоже на связи, но его поручение в очереди — это не «без прогресса».
        await SeenAsync(a, T0.AddMinutes(10).AddSeconds(30));
        await SeenAsync(b, T0.AddMinutes(10).AddSeconds(30));
        await repo.SweepAsync(T0.AddMinutes(10));
        Assert.Equal(States.Active, (await JobAsync(a)).State);

        await repo.SweepAsync(T0.AddMinutes(10).AddSeconds(31));
        var job = await JobAsync(a);
        Assert.Equal((States.Failed, Errors.NoProgress), (job.State, job.Error));
        Assert.Equal(States.Paused, (await JobAsync(b)).State);
    }

    [Fact]
    public async Task Sweep_BuildReplaced_BuildChangedAllOpen()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, a, T0));

        // Файл заменили другой сборкой: прежние поручения — buildChanged; запуск на новую — не трогается, но очередь на паузе.
        Put(Build181, "helper 1.8.1 rebuilt");
        await repo.LaunchAsync(Build181, [c.Number], false, "panel", T0.AddMinutes(1));
        await repo.SweepAsync(T0.AddMinutes(2));
        foreach (var pc in new[] { a, b })
        {
            var job = await JobAsync(pc);
            Assert.Equal((States.Failed, Errors.BuildChanged, T0.AddMinutes(2)), (job.State, job.Error, job.FinishedAt));
        }

        Assert.Equal(States.Paused, (await JobAsync(c)).State);
    }

    [Fact]
    public async Task Sweep_BuildDeleted_BuildMissing()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        Assert.NotNull(await ReportAsync(repo, a, T0));

        File.Delete(Path.Combine(Tools, Build181));
        await repo.SweepAsync(T0.AddMinutes(1));
        Assert.Equal((States.Failed, Errors.BuildMissing), ((await JobAsync(a)).State, (await JobAsync(a)).Error));
        Assert.Equal((States.Failed, Errors.BuildMissing), ((await JobAsync(b)).State, (await JobAsync(b)).Error));

        // При выдаче — та же проверка: файла нет — поручения нет, ошибка, пауза.
        Put(Build181, "helper 1.8.1");
        Assert.Equal(0, await repo.ResumeAsync(T0.AddMinutes(2)));
        await repo.LaunchAsync(Build181, [c.Number], false, "panel", T0.AddMinutes(2));
        File.Delete(Path.Combine(Tools, Build181));
        Assert.Null(await ReportAsync(repo, c, T0.AddMinutes(3)));
        Assert.Equal((States.Failed, Errors.BuildMissing), ((await JobAsync(c)).State, (await JobAsync(c)).Error));
    }

    [Fact]
    public async Task Resume_PausedToQueued()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number, c.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, a, T0))!.Id;
        await ReportAsync(repo, a, T0.AddMinutes(1), new HelperUpdateReport(id.ToString(), Progress.Failed, null, Errors.DownloadFailed, null));
        Assert.Equal(States.Paused, (await JobAsync(b)).State);

        Assert.Equal(2, await repo.ResumeAsync(T0.AddMinutes(2)));
        var job = await JobAsync(b);
        Assert.Equal((States.Queued, T0.AddMinutes(2)), (job.State, job.UpdatedAt));
        Assert.Equal(States.Queued, (await JobAsync(c)).State);
        Assert.Equal(0, await repo.ResumeAsync(T0.AddMinutes(3)));
        Assert.NotNull(await ReportAsync(repo, b, T0.AddMinutes(3)));
    }

    [Fact]
    public async Task Cancel_All_And_One()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number, c.Number], false, "panel", T0);
        var id = (await ReportAsync(repo, a, T0))!.Id;
        await ReportAsync(repo, a, T0.AddMinutes(1), Step(id, Progress.Waiting, Reasons.Session));

        // Отмена активного: поручение больше не приходит — помощник бросит ожидание и сотрёт .new.
        Assert.True(await repo.CancelAsync(id, T0.AddMinutes(2)));
        Assert.False(await repo.CancelAsync(id, T0.AddMinutes(2)));
        Assert.False(await repo.CancelAsync(Guid.NewGuid(), T0.AddMinutes(2)));
        var job = await JobAsync(a);
        Assert.Equal((States.Cancelled, T0.AddMinutes(2), T0.AddMinutes(2)), (job.State, job.FinishedAt, job.UpdatedAt));
        Assert.Null(await ReportAsync(repo, a, T0.AddMinutes(3), Step(id, Progress.Waiting, Reasons.Session)));

        // Ошибка по отменённому за сутки поручению — только запись ошибки, без паузы.
        await ReportAsync(repo, a, T0.AddMinutes(4), new HelperUpdateReport(id.ToString(), Progress.Failed, null, Errors.SwapFailed, "restoreFailed"));
        job = await JobAsync(a);
        Assert.Equal((States.Cancelled, Errors.SwapFailed, "restoreFailed"), (job.State, job.Error, job.Message));
        Assert.Equal(States.Queued, (await JobAsync(b)).State);

        Assert.Equal(2, await repo.CancelAllAsync(T0.AddMinutes(5)));
        Assert.Equal(0, await repo.CancelAllAsync(T0.AddMinutes(5)));
        Assert.Equal(States.Cancelled, (await JobAsync(b)).State);
        Assert.Equal(States.Cancelled, (await JobAsync(c)).State);
    }

    [Fact]
    public async Task List_OpenAndFinishedWithinDay()
    {
        var repo = Repo(maxActive: 1);
        var a = await MachineAsync();
        var b = await MachineAsync();
        var c = await MachineAsync();
        await repo.LaunchAsync(Build181, [a.Number, b.Number], false, "panel", T0);
        Assert.True(await repo.CancelAsync((await JobAsync(a)).Id, T0.AddHours(-25)));
        await repo.LaunchAsync(Build181, [a.Number, c.Number], false, "panel", T0);
        Assert.True(await repo.CancelAsync((await JobAsync(c)).Id, T0.AddHours(-1)));

        var items = await repo.ListAsync(T0);
        Assert.Equal([(a.Number, States.Queued), (b.Number, States.Queued), (c.Number, States.Cancelled)], items.Select(i => (i.Number, i.State)));
        var item = items[0];
        Assert.Equal((a.Id, Build181, "1.8.1", Build(Build181).Sha256), (item.MachineId, item.File, item.Version, item.Sha256));
    }
}
