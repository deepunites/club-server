using Club.Helper.Core;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Поддельная Windows: iSCSI-сессии, диски, буквы. Как настоящая: без политики SAN OfflineShared новый диск приходит
/// online и на запись (OnlineAll клиентской Windows), с ней — offline.
/// </summary>
public sealed class FakeWindowsStorage : IWindowsStorage
{
    private readonly Dictionary<string, FakeDisk> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _nextDisk = 2;

    public List<string> Log { get; } = [];
    public bool SanOfflineShared { get; private set; }

    /// <summary>Диск «не принимает» атрибут read-only (как сбой драйвера) — помощник обязан отказаться монтировать.</summary>
    public bool RefuseReadOnly { get; set; }

    /// <summary>Хотя бы раз диск был online и доступен на запись одновременно — нарушение fail-closed.</summary>
    public bool EverWritableOnline { get; private set; }

    public sealed class FakeDisk
    {
        public int Number { get; init; }
        public bool ReadOnly { get; set; }
        public bool Offline { get; set; }
        public char? Letter { get; set; }
    }

    public IReadOnlyDictionary<string, FakeDisk> Sessions => _sessions;

    public void AddForeignSession(string iqn) => _sessions[iqn] = new FakeDisk { Number = _nextDisk++, ReadOnly = false, Offline = false, Letter = 'E' };

    public Task EnsureSanPolicyOfflineSharedAsync(CancellationToken ct)
    {
        Log.Add("san");
        SanOfflineShared = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ConnectedTargetsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(_sessions.Keys.ToList());

    public Task ConnectAsync(string targetIqn, string portalHost, int portalPort, CancellationToken ct)
    {
        Log.Add($"connect {targetIqn} {portalHost}:{portalPort}");
        var disk = new FakeDisk { Number = _nextDisk++, ReadOnly = false, Offline = SanOfflineShared, Letter = SanOfflineShared ? null : NextFreeLetter() };
        _sessions[targetIqn] = disk;
        Check(disk);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string targetIqn, CancellationToken ct)
    {
        Log.Add($"disconnect {targetIqn}");
        _sessions.Remove(targetIqn);
        return Task.CompletedTask;
    }

    public Task<DiskInfo?> FindDiskAsync(string targetIqn, CancellationToken ct) =>
        Task.FromResult(_sessions.TryGetValue(targetIqn, out var d) ? new DiskInfo(d.Number, d.ReadOnly, d.Offline, d.Letter) : null);

    public Task SetDiskReadOnlyAsync(int diskNumber, CancellationToken ct)
    {
        Log.Add($"ro {diskNumber}");
        if (!RefuseReadOnly)
        {
            Disk(diskNumber).ReadOnly = true;
        }

        return Task.CompletedTask;
    }

    public Task SetDiskOnlineAsync(int diskNumber, CancellationToken ct)
    {
        Log.Add($"online {diskNumber}");
        var disk = Disk(diskNumber);
        disk.Offline = false;
        Check(disk);
        return Task.CompletedTask;
    }

    public Task AssignDriveLetterAsync(int diskNumber, char letter, CancellationToken ct)
    {
        Log.Add($"letter {diskNumber} {letter}");
        if (_sessions.Values.Any(d => d.Number != diskNumber && d.Letter == letter))
        {
            throw new InvalidOperationException($"drive letter {letter} is in use");
        }

        Disk(diskNumber).Letter = letter;
        return Task.CompletedTask;
    }

    /// <summary>Вход с CHAP: какие учётные данные передал помощник (секрет проверяет «таргет» — поле ExpectedChap).</summary>
    public (string User, string Secret)? ExpectedChap { get; set; }

    public List<(string Iqn, string User, string Secret)> ChapLogins { get; } = [];

    public async Task ConnectChapAsync(string targetIqn, string portalHost, int portalPort, string chapUser, string chapSecret, CancellationToken ct)
    {
        ChapLogins.Add((targetIqn, chapUser, chapSecret));
        if (ExpectedChap is { } expected && (expected.User != chapUser || expected.Secret != chapSecret))
        {
            throw new InvalidOperationException("CHAP authentication failed");
        }

        await ConnectAsync(targetIqn, portalHost, portalPort, ct);
    }

    public Task SetDiskWritableAsync(int diskNumber, CancellationToken ct)
    {
        Log.Add($"writable {diskNumber}");
        Disk(diskNumber).ReadOnly = false;
        return Task.CompletedTask;
    }

    public Task FlushAndOfflineAsync(int diskNumber, char? driveLetter, CancellationToken ct)
    {
        Log.Add($"flush+offline {diskNumber} {driveLetter}");
        if (FailOffline)
        {
            throw new InvalidOperationException("The disk is in use");
        }

        Disk(diskNumber).Offline = true;
        return Task.CompletedTask;
    }

    /// <summary>Сбой отключения диска (файлы на томе открыты).</summary>
    public bool FailOffline { get; set; }

    /// <summary>Папки на томе по букве (состав версии).</summary>
    public Dictionary<char, List<string>> Folders { get; } = new();

    public int FolderListings { get; private set; }

    public Task<IReadOnlyList<string>> ListFoldersAsync(char driveLetter, CancellationToken ct)
    {
        FolderListings++;
        return Task.FromResult<IReadOnlyList<string>>(Folders.TryGetValue(driveLetter, out var list) ? list : []);
    }

    /// <summary>Внешнее вмешательство: кто-то снял read-only с диска таргета.</summary>
    public void MakeWritable(string iqn) => _sessions[iqn].ReadOnly = false;

    private FakeDisk Disk(int number) => _sessions.Values.Single(d => d.Number == number);

    private char NextFreeLetter()
    {
        for (var c = 'F'; c <= 'Z'; c++)
        {
            if (_sessions.Values.All(d => d.Letter != c))
            {
                return c;
            }
        }

        throw new InvalidOperationException("no free letters");
    }

    private void Check(FakeDisk disk) => EverWritableOnline |= !disk.Offline && !disk.ReadOnly;
}

public sealed class FakeProcesses : IProcessInspector
{
    public Dictionary<char, List<string>> Running { get; } = new();

    public Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Running.TryGetValue(driveLetter, out var list) ? list : []);
}

public sealed class FakeIdentity(string hwid) : IMachineIdentity
{
    public List<string> DhcpServers { get; } = [];

    public SecureBootFacts? SecureBoot { get; set; }

    public string? InitiatorIqn { get; set; } = "iqn.1991-05.com.microsoft:pc-test";

    public Task<MachineFacts> ReadAsync(CancellationToken ct) =>
        Task.FromResult(new MachineFacts(hwid, "PC-TEST", ["aa:bb:cc:dd:ee:01"], "Windows 11 Pro 24H2", DateTimeOffset.UtcNow.AddMinutes(-3), [.. DhcpServers], SecureBoot: SecureBoot, InitiatorIqn: InitiatorIqn));
}

/// <summary>
/// Поддельный источник фактов Windows для <see cref="CachedMachineIdentity"/>: считает полные и короткие опросы.
/// Как настоящий, полный опрос IQN не читает. IQN пуст, пока «служба инициатора» не отдала порт (как на ПК, где
/// MSiSCSI была остановлена при старте помощника).
/// </summary>
public sealed class FakeFactsSource(string hwid) : IMachineFactsSource
{
    public string? InitiatorIqn { get; set; }

    /// <summary>Чем падает короткий опрос IQN (powershell.exe завершился с ошибкой, таймаут); <c>null</c> — не падает.</summary>
    public Exception? IqnReadError { get; set; }

    public List<string> DhcpServers { get; } = [];

    public int FullReads { get; private set; }

    public int IqnReads { get; private set; }

    public Task<MachineFacts> ReadAllAsync(CancellationToken ct)
    {
        FullReads++;
        return Task.FromResult(new MachineFacts(hwid, "PC-TEST", ["aa:bb:cc:dd:ee:01"], "Windows 11 Pro 24H2", DateTimeOffset.UtcNow.AddMinutes(-3)));
    }

    public Task<string?> ReadInitiatorIqnAsync(CancellationToken ct)
    {
        IqnReads++;
        return IqnReadError is { } error ? Task.FromException<string?>(error) : Task.FromResult(InitiatorIqn);
    }

    public MachineFacts Refresh(MachineFacts facts) => facts with { DhcpServers = [.. DhcpServers] };
}

/// <summary>Часы, которые идут только по команде теста: и системное время, и монотонные отметки.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }

    /// <summary>Перевод системного времени (NTP поправил RTC): монотонные отметки не двигаются.</summary>
    public void StepWallClock(TimeSpan by) => _now += by;
}

/// <summary>Журнал в память: тесты проверяют, что и с каким уровнем попало бы в журнал Windows.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
