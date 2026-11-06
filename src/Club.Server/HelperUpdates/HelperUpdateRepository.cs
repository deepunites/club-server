using System.Text;
using Club.Server.Data;
using Dapper;
using Npgsql;
using static Club.Server.HelperUpdates.HelperUpdateCodes;

namespace Club.Server.HelperUpdates;

/// <summary>
/// Очередь поручений «поставить сборку помощника на ПК» (spec 2026-11-06, п. 3.3, таблица <c>helper_updates</c>, M0013).
/// Каждое действие — запуск из панели, обработка отчёта G:, проход работника, продолжение и отмена — одна транзакция под
/// advisory-блокировкой очереди (<c>pg_advisory_xact_lock</c>): действия не пересекаются, и предел активных
/// (<see cref="HelperUpdateOptions.MaxActive"/>) соблюдается строго даже при отчётах в одну секунду. Время — только из
/// параметра <c>now</c>; «ПК молчит» — по <c>machines.last_seen_at</c>, «без прогресса» — по <c>progress_at</c> (каждый
/// отчёт с прогрессом). Пауза очереди — поручения в <c>paused</c>: при ошибке все <c>queued</c> уходят на паузу.
/// Каждый переход пишет <c>updated_at</c>, прогресс — <c>progress_at</c>, завершение — <c>finished_at</c>; действие, ПК,
/// сборка и итог — строкой в журнал (после фиксации транзакции).
/// </summary>
public sealed class HelperUpdateRepository(NpgsqlDataSource db, HelperBuilds builds, HelperUpdateOptions options, ILogger<HelperUpdateRepository> logger)
{
    /// <summary>Откуда помощник скачивает сборку: относительный путь сервера, файлы <c>&lt;Imaging:PxeRoot&gt;/tools</c>.</summary>
    public const string DownloadPrefix = "pxe/v1/files/tools/";

    /// <summary>Самое длинное сообщение помощника, которое хранится (spec п. 3.4).</summary>
    public const int MaxMessageLength = 500;

    static HelperUpdateRepository() => DapperSetup.Ensure();

    private const string Open = "('queued', 'paused', 'active')";

    private const string RowColumns = """
        h.id, h.machine_id AS MachineId, m.number, h.file, h.version, h.sha256, h.size, h.state, h.progress, h.reason, h.error,
        h.message, h.created_at AS CreatedAt, h.activated_at AS ActivatedAt, h.progress_at AS ProgressAt,
        h.updated_at AS UpdatedAt, h.finished_at AS FinishedAt
        """;

    /// <summary>
    /// Запуск: поручение на сборку <paramref name="file"/> каждому ПК из <paramref name="numbers"/> и, если
    /// <paramref name="allHybrids"/>, — всем ПК с <c>boot_mode = local</c>. Отказы — по первой причине в порядке
    /// <see cref="Refusals"/>; сборки нет (удалили между выбором и запуском) — <see cref="Errors.BuildMissing"/> на месте
    /// проверки сборки. Во время паузы поручения создаются сразу на паузе.
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(string file, IReadOnlyList<int>? numbers, bool allHybrids, string requestedBy, DateTimeOffset now)
    {
        var build = builds.Find(file);
        var journal = new List<Action>();
        var result = await InLockAsync(async (c, tx) =>
        {
            var targets = await c.QueryAsync<Target>(
                """
                SELECT m.id, m.number, m.helper_build AS HelperBuild,
                       EXISTS (SELECT 1 FROM helper_updates h WHERE h.machine_id = m.id AND h.state IN ('queued', 'paused', 'active')) AS Busy
                FROM machines m WHERE m.number = ANY(@numbers) OR (@allHybrids AND m.boot_mode = 'local')
                """,
                new { numbers = (numbers ?? []).ToArray(), allHybrids }, tx);
            var byNumber = targets.ToDictionary(t => t.Number);
            var paused = await PausedAsync(c, tx);
            var state = paused ? States.Paused : States.Queued;
            var queued = new List<int>();
            var refused = new List<LaunchRefusal>();
            foreach (var number in byNumber.Keys.Concat(numbers ?? []).Distinct().Order())
            {
                var reason = byNumber.TryGetValue(number, out var target)
                    ? await IneligibleAsync(c, tx, target.Id, helperVersion: null)
                        ?? (build is null ? Errors.BuildMissing
                            : !build.Updatable ? Refusals.TargetTooOld
                            : target.Busy ? Refusals.Busy
                            : target.HelperBuild == build.Sha256 ? Refusals.Current
                            : null)
                    : Refusals.Unknown;
                if (reason is not null)
                {
                    refused.Add(new LaunchRefusal(number, reason));
                    journal.Add(() => logger.LogInformation("Helper update: launch of {File} for PC {Number} refused: {Reason}", file, number, reason));
                    continue;
                }

                await c.ExecuteAsync(
                    """
                    INSERT INTO helper_updates (id, machine_id, file, version, sha256, size, state, requested_by, created_at, updated_at)
                    VALUES (@id, @machine, @File, @Version, @Sha256, @Size, @state, @requestedBy, @now, @now)
                    """,
                    new { id = Guid.NewGuid(), machine = target!.Id, build!.File, build.Version, build.Sha256, build.Size, state, requestedBy, now }, tx);
                queued.Add(number);
                var sha = build.Sha256[..8];
                journal.Add(() => logger.LogInformation("Helper update: PC {Number} -> {File} ({Sha}): {State}, by {By}", number, file, sha, state, requestedBy));
            }

            return new LaunchResult(queued, paused, refused);
        }, journal);
        return result;
    }

    /// <summary>
    /// Отчёт G: машины <paramref name="machineId"/> (шаги 2–5 spec п. 3.3; <c>helper_build</c> уже записан отчётом):
    /// прогресс, <c>confirmed</c>, <c>failed</c> по поручению из отчёта; целевая сборка уже стоит — «готово» без выдачи;
    /// помощник старше 1.8.0 на активном — <c>helperTooOld</c>; повышение своего поручения из очереди; ответ — поручение,
    /// только если оно активно, ПК всё ещё можно обновлять и файл сборки на месте. Незнакомые значения отчёта — журнал и
    /// пропуск, без исключений.
    /// </summary>
    public async Task<HelperUpdateOffer?> OnReportAsync(Guid machineId, string helperVersion, string? helperBuild, HelperUpdateReport? report, DateTimeOffset now)
    {
        var journal = new List<Action>();
        return await InLockAsync(async (c, tx) =>
        {
            var number = await c.ExecuteScalarAsync<int?>("SELECT number FROM machines WHERE id = @machineId", new { machineId }, tx);
            if (number is not { } pc)
            {
                return null;
            }

            var pause = report is not null && await ApplyReportAsync(c, tx, machineId, pc, report, now, journal);
            var open = await OpenJobAsync(c, tx, machineId);

            // ПК обновили вручную: отчёт уже показывает целевую сборку. После applying это сама замена — ждём confirmed.
            if (open is not null && helperBuild is not null && string.Equals(helperBuild, open.Sha256, StringComparison.Ordinal)
                && open.Progress is not (Progress.Applying or Progress.Confirming))
            {
                await FinishAsync(c, tx, open, States.Done, null, null, now, journal, "target build already installed");
                open = null;
            }

            if (open is { State: States.Active } && !SupportsUpdates(helperVersion))
            {
                await FinishAsync(c, tx, open, States.Failed, Errors.HelperTooOld, null, now, journal, $"helper {helperVersion}");
                open = null;
            }

            if (open is { State: States.Queued } && !pause && !await PausedAsync(c, tx)
                && await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM helper_updates WHERE state = 'active'", transaction: tx) < options.MaxActive)
            {
                switch (await IneligibleAsync(c, tx, machineId, helperVersion))
                {
                    case null:
                        await c.ExecuteAsync(
                            """
                            UPDATE helper_updates SET state = 'active', activated_at = @now, progress = NULL, reason = NULL, progress_at = NULL,
                                   updated_at = @now
                            WHERE id = @Id
                            """,
                            new { open.Id, now }, tx);
                        open = open with { State = States.Active, Progress = null };
                        Log(journal, open, "active");
                        break;
                    case Refusals.Diskless:
                        await FinishAsync(c, tx, open, States.Cancelled, Errors.Diskless, null, now, journal, "not updatable now");
                        open = null;
                        break;
                    case Refusals.HelperTooOld:
                        await FinishAsync(c, tx, open, States.Failed, Errors.HelperTooOld, null, now, journal, $"helper {helperVersion}");
                        open = null;
                        break;
                }
            }

            HelperUpdateOffer? offer = null;
            if (open is { State: States.Active })
            {
                switch (await IneligibleAsync(c, tx, machineId, helperVersion))
                {
                    case null:
                        var (available, failed) = await CheckBuildAsync(c, tx, open.File, now, journal);
                        pause |= failed;
                        // Файл заменили — это поручение (с прежним SHA256) уже завершено проверкой.
                        if (available && await OpenJobAsync(c, tx, machineId) is { State: States.Active } still && still.Id == open.Id)
                        {
                            offer = new HelperUpdateOffer(open.Id, open.Version, open.Sha256, open.Size, DownloadPrefix + open.File);
                        }

                        break;
                    case Refusals.Diskless:
                        // Сеанс мастера закрыли раньше, чем прошла замена.
                        await FinishAsync(c, tx, open, States.Cancelled, Errors.Diskless, null, now, journal, "not updatable now");
                        break;
                }
            }

            if (pause)
            {
                await PauseAsync(c, tx, now, journal);
            }

            return offer;
        }, journal);
    }

    /// <summary>
    /// Проход работника очереди (spec п. 3.3): молчащий ПК до замены и долгое ожидание — назад в очередь; после замены нет
    /// ни <c>confirmed</c>, ни отчёта с прогрессом — <c>noReturn</c>; ПК отчитывается, а прогресса нет — <c>noProgress</c>;
    /// файл сборки заменили или удалили — <c>buildChanged</c>/<c>buildMissing</c>. Любая ошибка — пауза.
    /// </summary>
    public async Task SweepAsync(DateTimeOffset now)
    {
        var journal = new List<Action>();
        await InLockAsync(async (c, tx) =>
        {
            var silent = now - TimeSpan.FromMinutes(options.SilentMinutes);
            var failed = new List<JobRow>();
            failed.AddRange(await FinishWhereAsync(c, tx,
                "h.state = 'active' AND h.progress IN ('applying', 'confirming') AND COALESCE(h.progress_at, h.updated_at) < @silent",
                States.Failed, Errors.NoReturn, new { silent }, now, journal, "no confirmed and no progress after the swap"));
            failed.AddRange(await FinishWhereAsync(c, tx,
                """
                h.state = 'active' AND (h.progress IS NULL OR h.progress IN ('downloading', 'waiting'))
                AND COALESCE(h.progress_at, h.activated_at, h.updated_at) < @silent AND m.last_seen_at >= @silent
                """,
                States.Failed, Errors.NoProgress, new { silent }, now, journal, "the PC reports without progress"));

            var requeued = (await c.QueryAsync<JobRow>(
                $"""
                UPDATE helper_updates h SET state = 'queued', progress = NULL, reason = NULL, progress_at = NULL, updated_at = @now
                FROM machines m
                WHERE m.id = h.machine_id AND h.state = 'active'
                  AND ((h.progress IS NULL OR h.progress IN ('downloading', 'waiting')) AND (m.last_seen_at IS NULL OR m.last_seen_at < @silent)
                       OR h.progress = 'waiting' AND h.updated_at < @waiting)
                RETURNING {RowColumns}
                """,
                new { now, silent, waiting = now - TimeSpan.FromMinutes(options.WaitingMinutes) }, tx)).ToList();
            foreach (var job in requeued)
            {
                Log(journal, job, "requeued (the PC is silent or waits too long)");
            }

            var pause = failed.Count > 0;
            foreach (var file in await c.QueryAsync<string>($"SELECT DISTINCT file FROM helper_updates WHERE state IN {Open} ORDER BY file", transaction: tx))
            {
                pause |= (await CheckBuildAsync(c, tx, file, now, journal)).Failed;
            }

            if (pause || (requeued.Count > 0 && await PausedAsync(c, tx)))
            {
                await PauseAsync(c, tx, now, journal);
            }

            return 0;
        }, journal);
    }

    /// <summary>«Продолжить»: поручения на паузе — снова в очередь. Сколько продолжено.</summary>
    public async Task<int> ResumeAsync(DateTimeOffset now)
    {
        var journal = new List<Action>();
        return await InLockAsync(async (c, tx) =>
        {
            var count = await c.ExecuteAsync("UPDATE helper_updates SET state = 'queued', updated_at = @now WHERE state = 'paused'", new { now }, tx);
            journal.Add(() => logger.LogInformation("Helper update: queue resumed, {Count} update(s) queued again", count));
            return count;
        }, journal);
    }

    /// <summary>«Отменить всё»: все незавершённые поручения — <c>cancelled</c>. Сколько отменено.</summary>
    public async Task<int> CancelAllAsync(DateTimeOffset now)
    {
        var journal = new List<Action>();
        return await InLockAsync(async (c, tx) =>
            (await FinishWhereAsync(c, tx, "true", States.Cancelled, null, null, now, journal, "cancelled from the panel")).Count, journal);
    }

    /// <summary>Отмена одного поручения; false — нет такого незавершённого.</summary>
    public async Task<bool> CancelAsync(Guid id, DateTimeOffset now)
    {
        var journal = new List<Action>();
        return await InLockAsync(async (c, tx) =>
            (await FinishWhereAsync(c, tx, "h.id = @id", States.Cancelled, null, new { id }, now, journal, "cancelled from the panel")).Count > 0, journal);
    }

    /// <summary>Незавершённые поручения и завершённые за сутки до <paramref name="now"/>: сначала незавершённые, по номеру ПК.</summary>
    public async Task<IReadOnlyList<HelperUpdateRow>> ListAsync(DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<JobRow>(
            $"""
            SELECT {RowColumns} FROM helper_updates h JOIN machines m ON m.id = h.machine_id
            WHERE h.state IN {Open} OR h.finished_at > @since
            ORDER BY h.finished_at IS NOT NULL, m.number, h.created_at DESC
            """,
            new { since = now - TimeSpan.FromDays(1) });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>
    /// Почему ПК сейчас нельзя обновлять кнопкой: <c>unknown</c> (нет машины), <c>notApproved</c>, <c>diskless</c> (место
    /// бездиска; мастер эталона — только в сеансе мастера), <c>helperTooOld</c>; null — можно.
    /// </summary>
    public async Task<string?> IneligibleReasonAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await IneligibleAsync(c, null, machineId, helperVersion: null);
    }

    /// <summary>
    /// Проверка «кого можно обновлять» (spec п. 3.3). Мастер эталона (<c>diskless_image.master_machine_id</c>) — только в
    /// сеансе мастера: диск <c>seat_disks.kind = 'master'</c> и загрузка по сети после включения режима. Отдельной
    /// отметки времени включения режима нет: его пишет <c>diskless_image.updated_at</c> (<see cref="Diskless.DisklessRepository.SetMasterAsync"/>;
    /// публикация и откат версии эталона тоже его сдвигают — до следующей загрузки мастер считается местом бездиска).
    /// <paramref name="helperVersion"/> — версия из текущего отчёта, null — из реестра.
    /// </summary>
    private async Task<string?> IneligibleAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid machineId, string? helperVersion)
    {
        var machine = await c.QuerySingleOrDefaultAsync<Eligibility>(
            """
            SELECT m.approved, m.boot_mode AS BootMode, m.helper_version AS HelperVersion, i.master_machine_id IS NOT NULL AS Master,
                   COALESCE(i.master_machine_id IS NOT NULL AND s.kind = 'master' AND s.last_boot_at > i.updated_at, false) AS MasterSession
            FROM machines m
            LEFT JOIN diskless_image i ON i.master_machine_id = m.id
            LEFT JOIN seat_disks s ON s.machine_id = m.id
            WHERE m.id = @machineId
            """,
            new { machineId }, tx);
        if (machine is null)
        {
            return Refusals.Unknown;
        }

        if (!machine.Approved)
        {
            return Refusals.NotApproved;
        }

        if (machine.Master ? !machine.MasterSession : machine.BootMode != "local")
        {
            return Refusals.Diskless;
        }

        return SupportsUpdates(helperVersion ?? machine.HelperVersion) ? null : Refusals.HelperTooOld;
    }

    /// <summary>Помощник умеет самообновление (версия не ниже <see cref="HelperUpdateOptions.MinTargetVersion"/>); суффиксы -beta, +build не считаются.</summary>
    private bool SupportsUpdates(string helperVersion) =>
        Version.TryParse(helperVersion.Split('-', '+')[0], out var have) && Version.TryParse(options.MinTargetVersion, out var need) && have >= need;

    /// <summary>
    /// Отчёт помощника о поручении (шаг 2). true — ошибка, ставящая очередь на паузу. Поручение ищется по <c>id</c> только
    /// среди поручений этого ПК; по отменённому за сутки поручению записывается только ошибка.
    /// </summary>
    private async Task<bool> ApplyReportAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid machineId, int number, HelperUpdateReport report, DateTimeOffset now, List<Action> journal)
    {
        if (report.Id is null && report.State is null)
        {
            return false;
        }

        if (!Guid.TryParse(report.Id, out var id))
        {
            journal.Add(() => logger.LogWarning("Helper update: PC {Number} reported an update with id {Id}, skipped", number, Clip(report.Id)));
            return false;
        }

        var job = await c.QuerySingleOrDefaultAsync<JobRow>(
            $"SELECT {RowColumns} FROM helper_updates h JOIN machines m ON m.id = h.machine_id WHERE h.id = @id AND h.machine_id = @machineId",
            new { id, machineId }, tx);
        if (job is null)
        {
            journal.Add(() => logger.LogWarning("Helper update: PC {Number} reported unknown update {Id}, skipped", number, id));
            return false;
        }

        var message = StorableMessage(report.Message);
        var error = report.Error;
        if (report.State == Progress.Failed && error is not null && !Errors.FromHelper.Contains(error) && error != Errors.HelperTooOld)
        {
            journal.Add(() => logger.LogWarning("Helper update: PC {Number} reported unknown error {Error}, skipped", number, Clip(error)));
            error = null;
        }

        if (job.State is States.Cancelled && report.State == Progress.Failed && job.FinishedAt > now - TimeSpan.FromDays(1))
        {
            // Помощник уже начал замену, когда поручение отменили: ошибка видна в панели, очередь не трогается.
            var changed = await c.ExecuteAsync(
                """
                UPDATE helper_updates SET error = @error, message = @message, updated_at = @now
                WHERE id = @id AND (error IS DISTINCT FROM @error OR message IS DISTINCT FROM @message)
                """,
                new { id, error, message, now }, tx);
            if (changed > 0)
            {
                Log(journal, job, $"cancelled, the helper reports {error ?? "an error"}");
            }

            return false;
        }

        if (job.State is not (States.Queued or States.Paused or States.Active))
        {
            return false;
        }

        switch (report.State)
        {
            case { } state when Progress.Stored.Contains(state):
                var reason = state == Progress.Waiting ? report.Reason : null;
                if (reason is not null && !Reasons.All.Contains(reason))
                {
                    journal.Add(() => logger.LogWarning("Helper update: PC {Number} waits for unknown reason {Reason}, skipped", number, Clip(report.Reason)));
                    reason = null;
                }

                // updated_at — только при смене прогресса: от него считается, сколько ПК ждёт.
                await c.ExecuteAsync(
                    """
                    UPDATE helper_updates SET progress = @state, reason = @reason, progress_at = @now,
                           updated_at = CASE WHEN progress IS DISTINCT FROM @state THEN @now ELSE updated_at END
                    WHERE id = @id
                    """,
                    new { id, state, reason, now }, tx);
                if (job.Progress != state)
                {
                    Log(journal, job, reason is null ? state : $"{state} ({reason})");
                }

                return false;
            case Progress.Confirmed:
                await FinishAsync(c, tx, job, States.Done, null, null, now, journal, "confirmed by the helper");
                return false;
            case Progress.Failed:
                await FinishAsync(c, tx, job, States.Failed, error, message, now, journal, message);
                // Пауза — на любой ошибке, кроме helperTooOld (spec п. 3.3, шаг 2).
                return error != Errors.HelperTooOld;
            default:
                journal.Add(() => logger.LogWarning("Helper update: PC {Number} reported unknown state {State}, skipped", number, Clip(report.State)));
                return false;
        }
    }

    /// <summary>
    /// Файл сборки на месте и тот же (spec п. 3.1): удалили — все незавершённые поручения по нему <c>buildMissing</c>;
    /// заменили другим — поручения с прежним SHA256 <c>buildChanged</c>. <c>Available</c> — файл сейчас можно отдать;
    /// файл есть, но пока не берётся (моложе допустимого, не читается), — не отдаётся и не ошибка. <c>Failed</c> — какие-то
    /// поручения завершены ошибкой (очереди — пауза).
    /// </summary>
    private async Task<(bool Available, bool Failed)> CheckBuildAsync(NpgsqlConnection c, NpgsqlTransaction tx, string file, DateTimeOffset now, List<Action> journal)
    {
        var build = builds.Find(file);
        if (build is null && Exists(file))
        {
            return (false, false);
        }

        var failed = build is null
            ? await FinishWhereAsync(c, tx, "h.file = @file", States.Failed, Errors.BuildMissing, new { file }, now, journal, "build file is gone")
            : await FinishWhereAsync(c, tx, "h.file = @file AND h.sha256 <> @sha", States.Failed, Errors.BuildChanged, new { file, sha = build.Sha256 }, now, journal,
                $"build file now has SHA256 {build.Sha256[..8]}");
        return (build is not null, failed.Count > 0);
    }

    private bool Exists(string file)
    {
        try
        {
            return File.Exists(Path.Combine(builds.ToolsDir, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private async Task FinishAsync(NpgsqlConnection c, NpgsqlTransaction tx, JobRow job, string state, string? error, string? message, DateTimeOffset now,
        List<Action> journal, string? why) =>
        await FinishWhereAsync(c, tx, "h.id = @id", state, error, new { id = job.Id, message }, now, journal, why, keepMessage: message is null);

    /// <summary>
    /// Незавершённые поручения по условию <paramref name="where"/> (алиасы <c>h</c> — поручение, <c>m</c> — машина)
    /// завершаются состоянием <paramref name="state"/> с ошибкой <paramref name="error"/>. <paramref name="keepMessage"/>
    /// false — <c>message</c> из параметра <c>@message</c>.
    /// </summary>
    private async Task<IReadOnlyList<JobRow>> FinishWhereAsync(NpgsqlConnection c, NpgsqlTransaction tx, string where, string state, string? error,
        object? parameters, DateTimeOffset now, List<Action> journal, string? why, bool keepMessage = true)
    {
        var args = new DynamicParameters(parameters);
        args.AddDynamicParams(new { state, error, now });
        var rows = (await c.QueryAsync<JobRow>(
            $"""
            UPDATE helper_updates h SET state = @state, error = @error, {(keepMessage ? "" : "message = @message, ")}finished_at = @now, updated_at = @now
            FROM machines m
            WHERE m.id = h.machine_id AND h.state IN {Open} AND ({where})
            RETURNING {RowColumns}
            """,
            args, tx)).ToList();
        foreach (var row in rows)
        {
            Log(journal, row, (error is null ? state : $"{state} {error}") + (why is null ? "" : $": {Clip(why)}"));
        }

        return rows;
    }

    /// <summary>Пауза: все поручения в очереди — на паузу.</summary>
    private async Task PauseAsync(NpgsqlConnection c, NpgsqlTransaction tx, DateTimeOffset now, List<Action> journal)
    {
        var count = await c.ExecuteAsync("UPDATE helper_updates SET state = 'paused', updated_at = @now WHERE state = 'queued'", new { now }, tx);
        journal.Add(() => logger.LogWarning("Helper update: queue paused, {Count} queued update(s) paused", count));
    }

    private static async Task<bool> PausedAsync(NpgsqlConnection c, NpgsqlTransaction tx) =>
        await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM helper_updates WHERE state = 'paused')", transaction: tx);

    private static async Task<JobRow?> OpenJobAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid machineId) =>
        await c.QuerySingleOrDefaultAsync<JobRow>(
            $"SELECT {RowColumns} FROM helper_updates h JOIN machines m ON m.id = h.machine_id WHERE h.machine_id = @machineId AND h.state IN {Open}",
            new { machineId }, tx);

    /// <summary>
    /// Транзакция под блокировкой очереди: <c>hashtext</c> (int4) с ключами воркеров (bigint больше 2^31) не совпадает,
    /// совпадение с блокировкой игрока (<c>save:…</c>) лишь заставит подождать. Строки журнала — после фиксации.
    /// </summary>
    private async Task<T> InLockAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, List<Action> journal)
    {
        T result;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('helper-updates'))", transaction: tx);
            result = await work(c, tx);
            await tx.CommitAsync();
        }

        foreach (var line in journal)
        {
            line();
        }

        return result;
    }

    private void Log(List<Action> journal, JobRow job, string result) =>
        journal.Add(() => logger.LogInformation("Helper update: PC {Number} -> {Version} ({Sha}): {Result}", job.Number, job.Version, job.Sha256[..Math.Min(8, job.Sha256.Length)], result));

    /// <summary>
    /// Текст помощника для <c>message</c>: без NUL и непарных суррогатов (PostgreSQL и кодировщик Npgsql их не принимают —
    /// отчёт упал бы с 500), не длиннее <see cref="MaxMessageLength"/> символов, без половины пары на границе обрезки.
    /// </summary>
    private static string? StorableMessage(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var text = new StringBuilder(Math.Min(message.Length, MaxMessageLength));
        for (var i = 0; i < message.Length; i++)
        {
            var ch = message[i];
            if (char.IsHighSurrogate(ch) && i + 1 < message.Length && char.IsLowSurrogate(message[i + 1]))
            {
                if (text.Length + 2 > MaxMessageLength)
                {
                    break;
                }

                text.Append(ch).Append(message[++i]);
                continue;
            }

            if (ch == '\0' || char.IsSurrogate(ch))
            {
                continue;
            }

            if (text.Length + 1 > MaxMessageLength)
            {
                break;
            }

            text.Append(ch);
        }

        return text.ToString();
    }

    /// <summary>Значение из отчёта в журнал — не длиннее 100 символов.</summary>
    private static string? Clip(string? value) => value is { Length: > 100 } ? value[..100] + "…" : value;

    private sealed class Target
    {
        public Guid Id { get; init; }
        public int Number { get; init; }
        public string? HelperBuild { get; init; }
        public bool Busy { get; init; }
    }

    private sealed class Eligibility
    {
        public bool Approved { get; init; }
        public string BootMode { get; init; } = "local";
        public string HelperVersion { get; init; } = "";
        public bool Master { get; init; }
        public bool MasterSession { get; init; }
    }

    /// <summary>Строка выборки для Dapper (timestamptz → DateTimeOffset через DapperSetup).</summary>
    private sealed record JobRow
    {
        public Guid Id { get; init; }
        public Guid MachineId { get; init; }
        public int Number { get; init; }
        public string File { get; init; } = "";
        public string Version { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long Size { get; init; }
        public string State { get; init; } = "";
        public string? Progress { get; init; }
        public string? Reason { get; init; }
        public string? Error { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ActivatedAt { get; init; }
        public DateTimeOffset? ProgressAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }

        public HelperUpdateRow ToRecord() =>
            new(Id, MachineId, Number, File, Version, Sha256, State, Progress, Reason, Error, Message, CreatedAt, UpdatedAt, FinishedAt);
    }
}
