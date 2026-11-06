using System.Security.Cryptography;
using System.Text;
using Club.Helper.Core.Updates;

namespace Club.Helper.Tests;

/// <summary>
/// Страж самообновления (spec 2026-11-06, п. 4.1, 4.4) и файлы состояния на временных папках: попытка считается, только
/// если прошлый запуск новой сборки не оставил «чистый выход» (перезагрузки при недоступном сервере — не попытки), после
/// трёх нечистых запусков — откат на <c>.old</c> с проверкой SHA256 (нет или не тот — <c>noRollback</c>, файлы не
/// трогаются); прерванная до переименований замена (<c>prepared</c>) — <c>.new</c> молча стёрт; откат — состояние
/// остаётся для отчёта; чужая сборка, нечитаемый <c>update.json</c>, хвосты; список ошибок — последние 20.
/// </summary>
public sealed class UpdateGuardTests : IDisposable
{
    private static readonly byte[] OldBuild = Encoding.UTF8.GetBytes("helper 1.8.0 " + new string('o', 5000));
    private static readonly byte[] NewBuild = Encoding.UTF8.GetBytes("helper 1.8.1 " + new string('n', 5000));
    private static readonly byte[] ForeignBuild = Encoding.UTF8.GetBytes("helper 1.8.5 by hand");
    private static readonly Guid JobId = Guid.Parse("6f1c2a3b-0000-4000-8000-000000000022");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-update-guard-{Guid.NewGuid():N}");
    private readonly UpdatePaths _paths;
    private readonly UpdateStateStore _store;
    private readonly ManualClock _clock = new();
    private readonly List<string> _journal = [];

    public UpdateGuardTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "Program Files", "ClubDiskless"), Path.Combine(_root, "ProgramData", "ClubDiskless"));
        Directory.CreateDirectory(_paths.InstallDir);
        Directory.CreateDirectory(_paths.StateDir);
        _store = new UpdateStateStore(_paths);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static string ShaOf(string path) => Sha(File.ReadAllBytes(path));

    /// <summary>Запуск службы сборкой <paramref name="running"/> (её SHA256 считает <c>Program.cs</c> до стража).</summary>
    private GuardOutcome Start(byte[] running, Func<string, string>? shaOf = null) =>
        UpdateGuard.Run(_paths, _store, Sha(running), shaOf ?? ShaOf, _clock, _journal);

    private UpdateState State(string phase, int attempts = 0, string? error = null, string? message = null) =>
        new(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", phase, attempts, CleanExit: false, _clock.GetUtcNow(), error, message);

    /// <summary>Замена прошла: exe — новая сборка, <c>.old</c> — прежняя, фаза <c>swapped</c>.</summary>
    private void Swapped(int attempts = 0, bool old = true)
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        if (old)
        {
            File.WriteAllBytes(_paths.Old, OldBuild);
        }

        _store.Write(State(UpdatePhases.Swapped, attempts));
    }

    private UpdateState Stored()
    {
        var state = _store.Read(out var unreadable);
        Assert.False(unreadable);
        return Assert.IsType<UpdateState>(state);
    }

    /// <summary>Служба остановлена по команде Windows (перезагрузка ПК): наследник WindowsServiceLifetime пишет «чистый выход».</summary>
    private void CleanExit() => _store.Write(Stored() with { CleanExit = true });

    private string[] InstallFiles() => [.. Directory.GetFileSystemEntries(_paths.InstallDir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Swapped_FirstStart_Attempt1_RunConfirming()
    {
        Swapped();
        File.WriteAllBytes(_paths.New, [1, 2, 3]);
        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));

        var state = Stored();
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Equal(1, state.Attempts);
        Assert.False(state.CleanExit);
        Assert.Equal(_clock.GetUtcNow(), state.StartedAt);
        Assert.Null(state.Error);
        // .old хранится до подтверждения; .new от прошлого процесса стёрт.
        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.old"], InstallFiles());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
    }

    [Fact]
    public void CleanExitThenStart_NoAttempt()
    {
        // Review Focus 2: сервер недоступен, подтверждения нет, ПК трижды перезагружают — Windows останавливает службу
        // командой, запуски после чистого выхода — не попытки, отката нет.
        Swapped();
        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
        Assert.Equal(1, Stored().Attempts);

        for (var reboot = 0; reboot < 3; reboot++)
        {
            _clock.Advance(TimeSpan.FromMinutes(3));
            CleanExit();
            Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
            var state = Stored();
            Assert.Equal(1, state.Attempts);
            Assert.False(state.CleanExit);
            Assert.Equal(_clock.GetUtcNow(), state.StartedAt);
        }

        // Ещё и два падения подряд после перезагрузок — всего три нечистых запуска, отката всё ещё нет.
        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
        Assert.Equal(3, Stored().Attempts);
        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.old"], InstallFiles());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Empty(_store.Failed());
    }

    [Fact]
    public void FourthUncleanStart_Rollback()
    {
        Swapped();
        for (var attempt = 1; attempt <= UpdateGuard.MaxAttempts; attempt++)
        {
            Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
            Assert.Equal(attempt, Stored().Attempts);
        }

        // Последний запуск упал: текст исключения записан в update.json (Program.cs, задача 9).
        _store.Write(Stored() with { Message = "System.InvalidOperationException: boom" });

        Assert.Equal(GuardOutcome.RollbackAndTerminate, Start(NewBuild));

        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.failed"], InstallFiles());
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Failed));
        var state = Stored();
        Assert.Equal(UpdatePhases.RolledBack, state.Phase);
        Assert.Equal(UpdateErrors.StartFailed, state.Error);
        Assert.Contains("boom", state.Message);
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.StartFailed)], _store.Failed());
        Assert.Contains(_journal, line => line.Contains("rolled back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rollback_OldMissing_NoRollback()
    {
        Swapped(attempts: UpdateGuard.MaxAttempts, old: false);

        Assert.Equal(GuardOutcome.Run, Start(NewBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        var state = Stored();
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Equal(UpdateErrors.NoRollback, state.Error);
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.NoRollback)], _store.Failed());

        // Дальше — работать как есть: ни отката, ни подтверждения, ошибка остаётся для отчёта.
        Assert.Equal(GuardOutcome.Run, Start(NewBuild));
        Assert.Equal(UpdateErrors.NoRollback, Stored().Error);
        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
    }

    [Fact]
    public void Rollback_OldWrongSha_NoRollback()
    {
        Swapped(attempts: UpdateGuard.MaxAttempts);
        File.WriteAllBytes(_paths.Old, ForeignBuild);

        Assert.Equal(GuardOutcome.Run, Start(NewBuild));

        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.old"], InstallFiles());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(ForeignBuild, File.ReadAllBytes(_paths.Old));
        Assert.Equal(UpdateErrors.NoRollback, Stored().Error);
    }

    [Fact]
    public void Rollback_RenameFails_FilesUntouched_NoRollback()
    {
        // На месте .failed — папка: первое переименование отката не проходит, имена не меняются.
        Swapped(attempts: UpdateGuard.MaxAttempts);
        Directory.CreateDirectory(_paths.Failed);
        File.WriteAllText(Path.Combine(_paths.Failed, "keep.txt"), "x");

        Assert.Equal(GuardOutcome.Run, Start(NewBuild));

        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
        var state = Stored();
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Equal(UpdateErrors.NoRollback, state.Error);
        Assert.Contains("cannot rename the exe to .failed", state.Message);
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.NoRollback)], _store.Failed());
    }

    [Fact]
    public void OldBuild_Prepared_DeletesNewSilently()
    {
        // Review Focus 5: update.json (prepared) записан, питание пропало до переименований — работает прежняя сборка.
        File.WriteAllBytes(_paths.Exe, OldBuild);
        File.WriteAllBytes(_paths.New, NewBuild);
        _store.Write(State(UpdatePhases.Prepared));

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Null(_store.Read(out var unreadable));
        Assert.False(unreadable);
        Assert.Empty(_store.Failed());
        Assert.False(File.Exists(_paths.StateFile + ".tmp"));
    }

    [Fact]
    public void OldBuild_RolledBack_KeepsStateForReport()
    {
        File.WriteAllBytes(_paths.Exe, OldBuild);
        File.WriteAllBytes(_paths.Failed, NewBuild);
        var rolledBack = State(UpdatePhases.RolledBack, attempts: 4, UpdateErrors.StartFailed, "boom");
        _store.Write(rolledBack);
        _store.AddFailed(new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.StartFailed));

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        Assert.Equal(rolledBack, Stored());
        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.failed"], InstallFiles());
        Assert.Single(_store.Failed());
    }

    [Fact]
    public void OldBuild_PreparedWithError_KeepsStateForReport()
    {
        // Замена не прошла, имена возвращены (swapFailed): ошибка уходит в отчётах, update.json не стирается.
        File.WriteAllBytes(_paths.Exe, OldBuild);
        var failed = State(UpdatePhases.Prepared, error: UpdateErrors.SwapFailed, message: "access denied");
        _store.Write(failed);

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        Assert.Equal(failed, Stored());
    }

    [Fact]
    public void OldBuild_Swapped_RollbackRenamedButStateNotWritten()
    {
        // Питание пропало после переименований отката, до записи rolledBack: работает прежняя сборка, фаза — swapped.
        File.WriteAllBytes(_paths.Exe, OldBuild);
        File.WriteAllBytes(_paths.Failed, NewBuild);
        _store.Write(State(UpdatePhases.Swapped, attempts: 4, message: "boom"));

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        var state = Stored();
        Assert.Equal(UpdatePhases.RolledBack, state.Phase);
        Assert.Equal(UpdateErrors.StartFailed, state.Error);
        Assert.Equal("boom", state.Message);
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.StartFailed)], _store.Failed());
        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.failed"], InstallFiles());
    }

    [Fact]
    public void NewBuild_Prepared_CountsAsSwapped()
    {
        // Питание пропало после обоих переименований, до записи swapped: работает новая сборка — первая попытка.
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        _store.Write(State(UpdatePhases.Prepared));

        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));

        var state = Stored();
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Equal(1, state.Attempts);
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
    }

    [Fact]
    public void ForeignBuild_CleansAll()
    {
        Swapped();
        File.WriteAllBytes(_paths.Exe, ForeignBuild);
        File.WriteAllBytes(_paths.New, NewBuild);
        File.WriteAllBytes(_paths.Failed, NewBuild);

        Assert.Equal(GuardOutcome.Run, Start(ForeignBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.Equal(ForeignBuild, File.ReadAllBytes(_paths.Exe));
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public void NewBuild_AfterRollback_InstalledByHand_CleansAll()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Failed, NewBuild);
        _store.Write(State(UpdatePhases.RolledBack, attempts: 4, UpdateErrors.StartFailed));
        _store.AddFailed(new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.StartFailed));

        Assert.Equal(GuardOutcome.Run, Start(NewBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.False(File.Exists(_paths.StateFile));
        // Поручение по-прежнему в списке ошибок: снова его не выполнять.
        Assert.Single(_store.Failed());
    }

    [Theory]
    [InlineData("{\"v\":1,\"id\":\"6f1c2a3b")]
    [InlineData("{\"v\":2,\"id\":\"6f1c2a3b-0000-4000-8000-000000000022\",\"fromSha\":\"a\",\"toSha\":\"b\",\"toVersion\":\"1.9.0\",\"phase\":\"swapped\"}")]
    [InlineData("{\"v\":1,\"id\":\"6f1c2a3b-0000-4000-8000-000000000022\",\"fromSha\":\"a\",\"toSha\":\"b\",\"toVersion\":\"1.9.0\",\"phase\":\"weird\"}")]
    public void Unreadable_KeepsOld(string content)
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        File.WriteAllBytes(_paths.New, [1, 2, 3]);
        File.WriteAllText(_paths.StateFile, content);
        Assert.Null(_store.Read(out var unreadable));
        Assert.True(unreadable);

        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));

        // fromSha восстановлен по .old: новая сборка подтверждается или откатывается на .old как обычно.
        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.old"], InstallFiles());
        var state = Stored();
        Assert.Equal(Sha(OldBuild), state.FromSha);
        Assert.Equal(Sha(NewBuild), state.ToSha);
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Equal(1, state.Attempts);

        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
        Assert.Equal(GuardOutcome.RunConfirming, Start(NewBuild));
        Assert.Equal(GuardOutcome.RollbackAndTerminate, Start(NewBuild));
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
    }

    [Fact]
    public void Unreadable_NoOld_CleansTails()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.New, [1]);
        File.WriteAllBytes(_paths.Failed, [2]);
        File.WriteAllText(_paths.StateFile, "garbage");

        Assert.Equal(GuardOutcome.Run, Start(NewBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public void NoState_CleansTails()
    {
        File.WriteAllBytes(_paths.Exe, OldBuild);
        File.WriteAllBytes(_paths.New, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        File.WriteAllBytes(_paths.Failed, NewBuild);

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
    }

    [Fact]
    public void NoState_NothingToDo_Run()
    {
        File.WriteAllBytes(_paths.Exe, OldBuild);

        Assert.Equal(GuardOutcome.Run, Start(OldBuild));

        Assert.Equal(["ClubDisklessHelper.exe"], InstallFiles());
        Assert.Empty(Directory.GetFiles(_paths.StateDir));
    }

    [Fact]
    public void UnexpectedError_ReturnsRun_LeavesFiles()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        File.WriteAllText(_paths.StateFile, "garbage");

        Assert.Equal(GuardOutcome.Run, Start(NewBuild, _ => throw new InvalidOperationException("sha failed")));

        Assert.Equal(["ClubDisklessHelper.exe", "ClubDisklessHelper.exe.old"], InstallFiles());
        Assert.Equal("garbage", File.ReadAllText(_paths.StateFile));
        Assert.Contains(_journal, line => line.Contains("sha failed", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedList_KeepsLast20()
    {
        var ids = Enumerable.Range(0, 25).Select(i => Guid.Parse($"00000000-0000-4000-8000-{i:D12}")).ToList();
        foreach (var id in ids)
        {
            _store.AddFailed(new FailedUpdate(id, Sha(NewBuild), UpdateErrors.HashMismatch));
        }

        // Список переживает перезапуск службы (новый экземпляр хранилища читает тот же файл).
        var reopened = new UpdateStateStore(_paths);
        Assert.Equal(ids[5..], reopened.Failed().Select(f => f.Id));

        // То же поручение ещё раз — одна запись, в конце списка.
        reopened.AddFailed(new FailedUpdate(ids[10], Sha(NewBuild), UpdateErrors.DownloadFailed));
        var failed = reopened.Failed();
        Assert.Equal(UpdateStateStore.FailedKept, failed.Count);
        Assert.Equal(new FailedUpdate(ids[10], Sha(NewBuild), UpdateErrors.DownloadFailed), failed[^1]);
        Assert.Single(failed, f => f.Id == ids[10]);

        File.WriteAllText(_paths.FailedFile, "[{");
        Assert.Empty(reopened.Failed());
        Assert.False(File.Exists(_paths.FailedFile + ".tmp"));
    }

    [Fact]
    public void Store_WritesWholeFileViaTmp_CamelCase()
    {
        File.WriteAllText(_paths.StateFile + ".tmp", "half of an old write");
        var state = State(UpdatePhases.Prepared);

        _store.Write(state);

        Assert.False(File.Exists(_paths.StateFile + ".tmp"));
        var json = File.ReadAllText(_paths.StateFile);
        Assert.StartsWith("{\"v\":1,\"id\":\"6f1c2a3b-0000-4000-8000-000000000022\",\"fromSha\":\"", json);
        Assert.Contains("\"phase\":\"prepared\",\"attempts\":0,\"cleanExit\":false", json);
        Assert.DoesNotContain("\"error\"", json);
        Assert.Equal(state, Stored());

        _store.Delete();
        Assert.False(File.Exists(_paths.StateFile));
        Assert.Null(_store.Read(out var unreadable));
        Assert.False(unreadable);
    }

    [Fact]
    public void Paths_Names()
    {
        var paths = new UpdatePaths(Path.Combine("C:", "Program Files", "ClubDiskless"), Path.Combine("C:", "ProgramData", "ClubDiskless"));
        Assert.Equal(Path.Combine(paths.InstallDir, "ClubDisklessHelper.exe"), paths.Exe);
        Assert.Equal(paths.Exe + ".new", paths.New);
        Assert.Equal(paths.Exe + ".old", paths.Old);
        Assert.Equal(paths.Exe + ".failed", paths.Failed);
        Assert.Equal(Path.Combine(paths.StateDir, "update.json"), paths.StateFile);
        Assert.Equal(Path.Combine(paths.StateDir, "update-failed.json"), paths.FailedFile);
    }
}
