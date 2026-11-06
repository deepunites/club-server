namespace Club.Helper.Core.Updates;

/// <summary>Что делать службе после стража (<see cref="UpdateGuard.Run"/>).</summary>
public enum GuardOutcome
{
    /// <summary>Обычный запуск: замены нет, она прервана или закончилась ошибкой (ошибка — в <c>update.json</c> для отчёта).</summary>
    Run,

    /// <summary>Работает новая сборка, которая ещё не подтверждена: прогресс <c>confirming</c>, <c>.old</c> хранится.</summary>
    RunConfirming,

    /// <summary>
    /// Откат сделан: exe — прежняя сборка. Запустить минимальный хост службы и, когда Windows считает службу запущенной,
    /// завершить процесс (<see cref="IServiceControl.TerminateSelf"/>), чтобы Windows запустила прежнюю сборку.
    /// </summary>
    RollbackAndTerminate,
}

/// <summary>
/// Страж самообновления (spec 2026-11-06, п. 4.1, 4.4) — первым в <c>Main</c>, до сборки хоста: только файлы и JSON из BCL,
/// без журнала и DI (строки для журнала событий — в <c>journal</c>, их пишет <c>Program.cs</c>). Решает по
/// <c>update.json</c> и SHA256 своего exe (<c>ownSha</c>):
/// <list type="bullet">
/// <item>своя сборка = <c>toSha</c> (фаза <c>swapped</c>; <c>prepared</c> — питание пропало после переименований, то же):
/// прошлый запуск не оставил «чистый выход» — попытка +1 (перезагрузка ПК и недоступный сервер — не попытки; первый запуск
/// — попытка 1). Попыток больше <see cref="MaxAttempts"/> — откат: <c>.old</c> есть и его SHA256 = <c>fromSha</c> — exe →
/// <c>.failed</c>, <c>.old</c> → exe (ошибка второго — имена обратно), <c>rolledBack</c>/<c>startFailed</c> с текстом
/// последнего исключения, <see cref="GuardOutcome.RollbackAndTerminate"/>; <c>.old</c> нет, он не тот или переименование
/// не прошло — ничего не трогать, <c>noRollback</c>, <see cref="GuardOutcome.Run"/> (и в следующих запусках — так же, без
/// подтверждения). Иначе — <see cref="GuardOutcome.RunConfirming"/>. После отката эту сборку поставили вручную
/// (<c>rolledBack</c>) — как чужая;</item>
/// <item>своя сборка = <c>fromSha</c>: <c>prepared</c> без ошибки — замена прервалась до переименований: <c>.new</c> и
/// <c>update.json</c> молча стираются, поручение продолжится; <c>rolledBack</c> или есть ошибка — состояние остаётся для
/// отчёта (<c>failed</c> в каждом отчёте G:, стирает его <c>SelfUpdater</c>); <c>swapped</c> — откат переименовал файлы,
/// но не записал <c>rolledBack</c>: дописать;</item>
/// <item>сборка ни та ни другая (поставили вручную): <c>update.json</c>, <c>.new</c>, <c>.old</c>, <c>.failed</c>
/// стираются;</item>
/// <item><c>update.json</c> не читается: <c>.old</c> есть и это другая сборка — не стирать, состояние восстанавливается с
/// <c>fromSha</c> = SHA256 <c>.old</c> (поручение неизвестно, <see cref="Guid.Empty"/>), дальше — как <c>swapped</c>;
/// <c>.old</c> нет — как без <c>update.json</c>;</item>
/// <item>без <c>update.json</c> — хвосты <c>.new</c>, <c>.old</c>, <c>.failed</c> стираются.</item>
/// </list>
/// <c>.new</c> при старте не нужен ни в одной ветке (загрузка идёт только в процессе, который меняет файлы) — стирается.
/// Поручение с ошибкой (<c>startFailed</c>, <c>noRollback</c>) записывается и в <c>update-failed.json</c>. Исключений наружу
/// нет: непредвиденная ошибка — строка в <c>journal</c>, файлы как есть, <see cref="GuardOutcome.Run"/>.
/// </summary>
public static class UpdateGuard
{
    /// <summary>Сколько нечистых запусков новой сборки допускается; следующий — откат.</summary>
    public const int MaxAttempts = 3;

    private const int RestoreTries = 3;

    private static readonly TimeSpan RestorePause = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Решить при старте службы. <paramref name="ownSha"/> — SHA256 своего exe; <paramref name="shaOf"/> — SHA256 файла
    /// (для <c>.old</c>); <paramref name="journal"/> — строки для журнала событий (по строке на шаг).
    /// </summary>
    public static GuardOutcome Run(
        UpdatePaths paths, UpdateStateStore store, string ownSha, Func<string, string> shaOf, TimeProvider clock, ICollection<string>? journal = null)
    {
        try
        {
            return Decide(new Context(paths, store, ownSha.ToLowerInvariant(), shaOf, clock.GetUtcNow(), journal));
        }
        catch (Exception ex)
        {
            Note(journal, $"Update guard failed, files left as they are: {ex.GetType().Name}: {ex.Message}");
            return GuardOutcome.Run;
        }
    }

    private sealed record Context(
        UpdatePaths Paths, UpdateStateStore Store, string Own, Func<string, string> ShaOf, DateTimeOffset Now, ICollection<string>? Journal);

    private static GuardOutcome Decide(Context c)
    {
        var state = c.Store.Read(out var unreadable);
        if (state is null)
        {
            return unreadable ? Unreadable(c) : NoState(c);
        }

        if (Same(c.Own, state.ToSha))
        {
            return state.Phase == UpdatePhases.RolledBack
                ? Forget(c, $"build {Short(c.Own)} was installed by hand after the rollback of update {state.Id}")
                : NewBuild(c, state);
        }

        if (Same(c.Own, state.FromSha))
        {
            return OldBuild(c, state);
        }

        return Forget(c, $"build {Short(c.Own)} is neither {Short(state.FromSha)} nor {Short(state.ToSha)} of update {state.Id} (installed by hand)");
    }

    /// <summary>Работает новая сборка: попытка, откат или проверка.</summary>
    private static GuardOutcome NewBuild(Context c, UpdateState state)
    {
        Remove(c, c.Paths.New);
        if (state.Error is not null)
        {
            Note(c.Journal, $"Update {state.Id} to {state.ToVersion} ({Short(state.ToSha)}) failed earlier ({state.Error}); running without confirmation");
            return GuardOutcome.Run;
        }

        var attempts = Math.Max(1, state.CleanExit ? state.Attempts : state.Attempts + 1);
        var started = state with { Phase = UpdatePhases.Swapped, Attempts = attempts, CleanExit = false, StartedAt = c.Now };
        if (attempts > MaxAttempts)
        {
            return Rollback(c, started);
        }

        c.Store.Write(started);
        Note(c.Journal, $"Update {state.Id}: new build {state.ToVersion} ({Short(state.ToSha)}) start {attempts} of {MaxAttempts}" +
            (state.CleanExit ? " (previous run stopped cleanly, not counted)" : "") + "; confirming");
        return GuardOutcome.RunConfirming;
    }

    /// <summary>Попыток больше <see cref="MaxAttempts"/>: вернуть <c>.old</c>, если это прежняя сборка.</summary>
    private static GuardOutcome Rollback(Context c, UpdateState state)
    {
        var why = OldMismatch(c, state);
        if (why is null)
        {
            try
            {
                File.Move(c.Paths.Exe, c.Paths.Failed, overwrite: true);
                try
                {
                    File.Move(c.Paths.Old, c.Paths.Exe);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    why = $"cannot rename .old to the exe: {ex.Message}; " +
                        (RestoreNames(c) ? "names restored" : "restoreFailed: the exe is missing, the PC needs a manual install");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why ??= $"cannot rename the exe to .failed: {ex.Message}";
            }
        }

        if (why is not null)
        {
            var message = state.Message is null ? why : $"{why}; last error: {state.Message}";
            c.Store.Write(state with { Error = UpdateErrors.NoRollback, Message = message });
            c.Store.AddFailed(new FailedUpdate(state.Id, state.ToSha, UpdateErrors.NoRollback));
            Note(c.Journal, $"Update {state.Id}: new build {Short(state.ToSha)} did not start cleanly {MaxAttempts} times, no rollback: {why}");
            return GuardOutcome.Run;
        }

        // Файлы уже переименованы: что бы ни случилось с записью состояния, этот процесс (новая сборка) должен уйти, а
        // Windows — запустить прежнюю. Не записалось — следующий запуск (прежняя сборка, фаза swapped) допишет откат.
        try
        {
            RecordRolledBack(c, state);
        }
        catch (Exception ex)
        {
            Note(c.Journal, $"Update {state.Id}: rolled back, but update.json was not written: {ex.GetType().Name}: {ex.Message}");
        }

        Note(c.Journal, $"Update {state.Id}: new build {state.ToVersion} ({Short(state.ToSha)}) did not start cleanly {MaxAttempts} times; " +
            $"rolled back to {Short(state.FromSha)} (exe -> .failed, .old -> exe)");
        return GuardOutcome.RollbackAndTerminate;
    }

    /// <summary>Почему откатываться нельзя: <c>.old</c> нет, не читается или это не <c>fromSha</c>; <c>null</c> — можно.</summary>
    private static string? OldMismatch(Context c, UpdateState state)
    {
        if (!File.Exists(c.Paths.Old))
        {
            return ".old is missing";
        }

        string oldSha;
        try
        {
            oldSha = c.ShaOf(c.Paths.Old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $".old is unreadable: {ex.Message}";
        }

        return Same(oldSha, state.FromSha) ? null : $".old is build {Short(oldSha)}, not {Short(state.FromSha)}";
    }

    /// <summary>Вернуть exe на место после неудачного второго переименования; <c>false</c> — не вышло.</summary>
    private static bool RestoreNames(Context c)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(c.Paths.Failed, c.Paths.Exe);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < RestoreTries)
            {
                Thread.Sleep(RestorePause);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note(c.Journal, $"Cannot rename .failed back to the exe: {ex.Message}");
                return false;
            }
        }
    }

    private static void RecordRolledBack(Context c, UpdateState state)
    {
        var message = state.Message ?? $"build {state.ToVersion} ({Short(state.ToSha)}) did not start cleanly {MaxAttempts} times";
        c.Store.Write(state with { Phase = UpdatePhases.RolledBack, Error = UpdateErrors.StartFailed, Message = message });
        c.Store.AddFailed(new FailedUpdate(state.Id, state.ToSha, UpdateErrors.StartFailed));
    }

    /// <summary>Работает прежняя сборка.</summary>
    private static GuardOutcome OldBuild(Context c, UpdateState state)
    {
        if (state.Phase == UpdatePhases.RolledBack || state.Error is not null)
        {
            Remove(c, c.Paths.New);
            Note(c.Journal, $"Update {state.Id} to {state.ToVersion} ({Short(state.ToSha)}) failed ({state.Error ?? UpdateErrors.StartFailed}); kept for the report");
            return GuardOutcome.Run;
        }

        if (state.Phase == UpdatePhases.Prepared)
        {
            // Review Focus 5: питание пропало после записи prepared, до переименований. Не ошибка: поручение продолжится,
            // сборка скачается снова.
            c.Store.Delete();
            Tails(c);
            Note(c.Journal, $"Update {state.Id} to {state.ToVersion} ({Short(state.ToSha)}) was interrupted before the swap; .new deleted, the update continues");
            return GuardOutcome.Run;
        }

        RecordRolledBack(c, state);
        Remove(c, c.Paths.New);
        Note(c.Journal, $"Update {state.Id}: the rollback to {Short(state.FromSha)} was done before the last start; recorded as {UpdateErrors.StartFailed}");
        return GuardOutcome.Run;
    }

    /// <summary><c>update.json</c> не читается: беречь <c>.old</c> и восстановить по нему <c>fromSha</c>.</summary>
    private static GuardOutcome Unreadable(Context c)
    {
        if (File.Exists(c.Paths.Old))
        {
            var oldSha = c.ShaOf(c.Paths.Old).ToLowerInvariant();
            if (!Same(oldSha, c.Own))
            {
                Remove(c, c.Paths.New);
                Remove(c, c.Paths.Failed);
                var state = new UpdateState(
                    UpdateState.CurrentVersion, Guid.Empty, oldSha, c.Own, "", UpdatePhases.Swapped, Attempts: 1, CleanExit: false, c.Now, Error: null, Message: null);
                c.Store.Write(state);
                Note(c.Journal, $"update.json is unreadable; restored from .old ({Short(oldSha)}): build {Short(c.Own)} start 1 of {MaxAttempts}; confirming");
                return GuardOutcome.RunConfirming;
            }
        }

        return Forget(c, "update.json is unreadable and there is no previous build in .old");
    }

    private static GuardOutcome NoState(Context c)
    {
        Tails(c);
        return GuardOutcome.Run;
    }

    /// <summary>Замены нет (сборку поставили вручную): стереть состояние и все файлы самообновления.</summary>
    private static GuardOutcome Forget(Context c, string why)
    {
        c.Store.Delete();
        Tails(c);
        Note(c.Journal, $"{why}; update.json removed");
        return GuardOutcome.Run;
    }

    /// <summary>Стереть <c>.new</c>, <c>.old</c>, <c>.failed</c>; строка в журнал — если что-то было.</summary>
    private static void Tails(Context c)
    {
        string[] removed = [.. new[] { c.Paths.New, c.Paths.Old, c.Paths.Failed }.Where(file => Remove(c, file)).Select(file => Path.GetExtension(file))];
        if (removed.Length > 0)
        {
            Note(c.Journal, $"Self-update leftovers removed: {string.Join(", ", removed)}");
        }
    }

    /// <summary>Стереть файл; <c>true</c> — он был и стёрт. Не стирается — строка в журнал, дальше.</summary>
    private static bool Remove(Context c, string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note(c.Journal, $"Cannot delete {Path.GetFileName(file)}: {ex.Message}");
            return false;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static void Note(ICollection<string>? journal, string line)
    {
        try
        {
            journal?.Add(line);
        }
        catch (Exception)
        {
            // Журнал стража — не повод не запустить службу.
        }
    }
}
