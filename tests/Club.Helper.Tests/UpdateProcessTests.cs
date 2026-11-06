using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Club.Helper.Core.Updates;

namespace Club.Helper.Tests;

/// <summary>
/// Каркас процесса самообновления (spec 2026-11-06, п. 4.1, 4.4) — то, что из Windows-части вынесено в Core: действия
/// Windows при сбое службы (значение <c>FailureActions</c> службы и аргументы <c>sc.exe failure</c>), текст ненормального
/// конца процесса в <c>update.json</c> (только пока новая сборка на проверке) и SHA256 сборки — так, как его считает
/// <c>Program.cs</c> до стража.
/// </summary>
public sealed class UpdateProcessTests : IDisposable
{
    private static readonly byte[] OldBuild = Encoding.UTF8.GetBytes("helper 1.8.0 " + new string('o', 3000));
    private static readonly byte[] NewBuild = Encoding.UTF8.GetBytes("helper 1.8.1 " + new string('n', 3000));
    private static readonly Guid JobId = Guid.Parse("6f1c2a3b-0000-4000-8000-000000000023");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-update-process-{Guid.NewGuid():N}");
    private readonly UpdatePaths _paths;
    private readonly UpdateStateStore _store;
    private readonly ManualClock _clock = new();

    public UpdateProcessTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "Program Files", "ClubDiskless"), Path.Combine(_root, "ProgramData", "ClubDiskless"));
        Directory.CreateDirectory(_paths.InstallDir);
        Directory.CreateDirectory(_paths.StateDir);
        _store = new UpdateStateStore(_paths);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Значение <c>FailureActions</c> (REG_BINARY) службы так, как его хранит Windows: SERVICE_FAILURE_ACTIONS со
    /// смещениями вместо указателей (20 байт) и SC_ACTION по 8 байт (тип, задержка в мс).
    /// </summary>
    private static byte[] FailureActions(uint reset, params (uint Type, uint Delay)[] actions)
    {
        var value = new byte[20 + actions.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0), reset);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(12), (uint)actions.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), actions.Length == 0 ? 0u : 20u);
        for (var i = 0; i < actions.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(20 + i * 8), actions[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(24 + i * 8), actions[i].Delay);
        }

        return value;
    }

    private const uint Restart = 1;
    private const uint Reboot = 2;

    [Fact]
    public void Recovery_ScFailureValue_Matches()
    {
        Assert.True(ServiceRecovery.Matches(FailureActions(86400, (Restart, 5000), (Restart, 5000), (Restart, 30000))));
    }

    [Fact]
    public void Recovery_ActionsAfterFixedPart_EvenWithoutOffset_Matches()
    {
        var value = FailureActions(86400, (Restart, 5000), (Restart, 5000), (Restart, 30000));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), 0);

        Assert.True(ServiceRecovery.Matches(value));
    }

    public static TheoryData<byte[]?> OtherRecovery => new()
    {
        null,
        Array.Empty<byte>(),
        new byte[12],
        FailureActions(0),
        FailureActions(86400, (Restart, 5000), (Restart, 5000)),
        FailureActions(86400, (Restart, 5000), (Restart, 5000), (Restart, 5000)),
        FailureActions(3600, (Restart, 5000), (Restart, 5000), (Restart, 30000)),
        FailureActions(86400, (Restart, 5000), (Restart, 5000), (Reboot, 30000)),
        FailureActions(86400, (Restart, 5000), (Restart, 5000), (Restart, 30000), (Restart, 30000)),
        FailureActions(86400, (Restart, 5000), (Restart, 5000), (Restart, 30000))[..36],
    };

    [Theory]
    [MemberData(nameof(OtherRecovery))]
    public void Recovery_MissingOrOther_DoesNotMatch(byte[]? value)
    {
        Assert.False(ServiceRecovery.Matches(value));
    }

    [Fact]
    public void Recovery_ScFailureArguments()
    {
        Assert.Equal(
            ["failure", "ClubDisklessHelper", "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"],
            ServiceRecovery.FailureArguments("ClubDisklessHelper"));
        Assert.Equal(86400, ServiceRecovery.ResetSeconds);
        Assert.Equal([5000, 5000, 30000], ServiceRecovery.RestartDelaysMs);
    }

    private UpdateState State(string phase, string? error = null) =>
        new(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", phase, Attempts: 1, CleanExit: false, _clock.GetUtcNow(), error, Message: null);

    private UpdateState Stored()
    {
        var state = _store.Read(out var unreadable);
        Assert.False(unreadable);
        return Assert.IsType<UpdateState>(state);
    }

    [Fact]
    public void Crash_NewBuildConfirming_MessageWritten_Clipped()
    {
        _store.Write(State(UpdatePhases.Swapped));
        var crash = new CrashRecorder(_store, Sha(NewBuild));

        Assert.True(crash.Record("InvalidOperationException: " + new string('x', 900)));

        var state = Stored();
        Assert.Equal(SelfUpdater.MessageLength, state.Message!.Length);
        Assert.StartsWith("InvalidOperationException: xxx", state.Message, StringComparison.Ordinal);
        Assert.Equal(UpdatePhases.Swapped, state.Phase);
        Assert.Null(state.Error);
        Assert.Equal(1, state.Attempts);
        Assert.False(state.CleanExit);
    }

    [Fact]
    public void Crash_NoUpdate_NothingWritten()
    {
        var crash = new CrashRecorder(_store, Sha(NewBuild));

        Assert.False(crash.Record("boom"));

        Assert.False(File.Exists(_paths.StateFile));
        Assert.False(File.Exists(_paths.StateFile + ".tmp"));
    }

    [Fact]
    public void Crash_OldBuildOrFailedOrForeign_StateUntouched()
    {
        var prepared = State(UpdatePhases.Prepared);
        _store.Write(prepared);
        Assert.False(new CrashRecorder(_store, Sha(OldBuild)).Record("old build crashed"));
        Assert.Equal(prepared, Stored());

        var failed = State(UpdatePhases.Swapped, UpdateErrors.NoRollback);
        _store.Write(failed);
        Assert.False(new CrashRecorder(_store, Sha(NewBuild)).Record("new build crashed after noRollback"));
        Assert.Equal(failed, Stored());

        var swapped = State(UpdatePhases.Swapped);
        _store.Write(swapped);
        Assert.False(new CrashRecorder(_store, Sha(Encoding.UTF8.GetBytes("by hand"))).Record("foreign build crashed"));
        Assert.False(new CrashRecorder(_store, null).Record("build unknown"));
        Assert.Equal(swapped, Stored());
    }

    [Fact]
    public void Crash_StateDirMissing_NeverThrows()
    {
        var store = new UpdateStateStore(new UpdatePaths(Path.Combine(_root, "none"), Path.Combine(_root, "none", "state")));

        Assert.False(new CrashRecorder(store, Sha(NewBuild)).Record("boom"));
    }

    [Fact]
    public void Crash_Describe_TypeAndMessage_Innermost()
    {
        var inner = new InvalidOperationException("CLUB_TEST_FAULT_THROW: exception in HelperService.ExecuteAsync");

        Assert.Equal(
            "System.InvalidOperationException: CLUB_TEST_FAULT_THROW: exception in HelperService.ExecuteAsync",
            CrashRecorder.Describe(new AggregateException(inner)));
        Assert.Equal("System.IO.IOException: disk", CrashRecorder.Describe(new IOException("disk")));
    }

    /// <summary>
    /// Новая сборка падает при каждом запуске: текст последнего падения (запись <see cref="CrashRecorder"/>) — в
    /// <c>startFailed</c> после отката стражем.
    /// </summary>
    [Fact]
    public void Crash_ThreeUncleanStarts_RollbackCarriesLastCrashText()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        _store.Write(State(UpdatePhases.Swapped) with { Attempts = 0 });
        var crash = new CrashRecorder(_store, Sha(NewBuild));

        for (var start = 1; start <= UpdateGuard.MaxAttempts; start++)
        {
            Assert.Equal(GuardOutcome.RunConfirming, UpdateGuard.Run(_paths, _store, Sha(NewBuild), UpdatePaths.Sha256Of, _clock));
            Assert.True(crash.Record($"System.InvalidOperationException: crash {start}"));
        }

        Assert.Equal(GuardOutcome.RollbackAndTerminate, UpdateGuard.Run(_paths, _store, Sha(NewBuild), UpdatePaths.Sha256Of, _clock));

        var state = Stored();
        Assert.Equal(UpdatePhases.RolledBack, state.Phase);
        Assert.Equal(UpdateErrors.StartFailed, state.Error);
        Assert.Equal($"System.InvalidOperationException: crash {UpdateGuard.MaxAttempts}", state.Message);
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Failed));
    }

    [Fact]
    public void Sha256Of_LowercaseHex_WhileFileIsOpen()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        using var running = new FileStream(_paths.Exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(Sha(NewBuild), UpdatePaths.Sha256Of(_paths.Exe));
    }
}
