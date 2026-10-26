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

    /// <summary>Буквы локальных томов ПК, не iSCSI (картридер, второй диск): занятую ими букву тому библиотеки не назначить.</summary>
    public HashSet<char> LocalLetters { get; } = [];

    public Task EnsureSanPolicyOfflineSharedAsync(CancellationToken ct)
    {
        Log.Add("san");
        SanOfflineShared = true;
        return Task.CompletedTask;
    }

    /// <summary>Чем падает опрос сессий (PowerShell не ответил за 120 с, отказ доступа); <c>null</c> — не падает.</summary>
    public Exception? ConnectedTargetsError { get; set; }

    public int ConnectedTargetsCalls { get; private set; }

    public Task<IReadOnlyList<string>> ConnectedTargetsAsync(CancellationToken ct)
    {
        ConnectedTargetsCalls++;
        return ConnectedTargetsError is { } error
            ? Task.FromException<IReadOnlyList<string>>(error)
            : Task.FromResult<IReadOnlyList<string>>(_sessions.Keys.Where(t => !RecoveringSessions.Contains(t)).ToList());
    }

    /// <summary>
    /// Сессии, которые Windows восстанавливает (IsConnected = false): в ConnectedTargets их нет, диска служба не видит
    /// (FindDisk ищет диск подключённой сессии), второй вход в таргет Windows отклоняет. Сессия с диском (в
    /// <see cref="Sessions"/>) держит смонтированный том на его букве, пока её не отключат.
    /// </summary>
    public HashSet<string> RecoveringSessions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SessionTargetsCalls { get; private set; }

    /// <summary>Опросы сессий (Get-IscsiSession): и подключённых, и всех.</summary>
    public int SessionQueries => ConnectedTargetsCalls + SessionTargetsCalls;

    /// <summary>Тот же Get-IscsiSession, что у ConnectedTargets: падает тем же <see cref="ConnectedTargetsError"/>.</summary>
    public Task<IReadOnlyList<string>> SessionTargetsAsync(CancellationToken ct)
    {
        SessionTargetsCalls++;
        return ConnectedTargetsError is { } error
            ? Task.FromException<IReadOnlyList<string>>(error)
            : Task.FromResult<IReadOnlyList<string>>(_sessions.Keys.Concat(RecoveringSessions).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task ConnectAsync(string targetIqn, string portalHost, int portalPort, CancellationToken ct)
    {
        if (RecoveringSessions.Contains(targetIqn))
        {
            throw new InvalidOperationException("PowerShell failed (1): The target has already been logged in via an iSCSI session.");
        }

        Log.Add($"connect {targetIqn} {portalHost}:{portalPort}");
        var disk = new FakeDisk { Number = _nextDisk++, ReadOnly = false, Offline = SanOfflineShared, Letter = SanOfflineShared ? null : NextFreeLetter() };
        _sessions[targetIqn] = disk;
        Check(disk);
        return Task.CompletedTask;
    }

    /// <summary>Отказ Windows завершить сессию, пока на томе открыты файлы (английская Windows).</summary>
    public const string SessionBusy = "The session cannot be logged out since a device on that session is currently being used.";

    /// <summary>
    /// Таргеты, сессию которых Windows не завершает: на томе открыты файлы процессов, запущенных не с него (проводник,
    /// Steam). Значение — текст отказа: на нелатинской Windows он локализован.
    /// </summary>
    public Dictionary<string, string> BusySessions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Таргеты, отключение которых падает иначе, чем отказом Windows (таймаут PowerShell и т. п.).</summary>
    public Dictionary<string, Exception> FailingDisconnects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task DisconnectAsync(string targetIqn, CancellationToken ct)
    {
        Log.Add($"disconnect {targetIqn}");
        if (FailingDisconnects.TryGetValue(targetIqn, out var failure))
        {
            throw failure;
        }

        if (BusySessions.TryGetValue(targetIqn, out var refusal))
        {
            // Как PowerShell.RunAsync в службе: командлет упал — исключение с текстом ошибки.
            throw new InvalidOperationException($"PowerShell failed (1): {refusal}");
        }

        _sessions.Remove(targetIqn);
        RecoveringSessions.Remove(targetIqn);
        return Task.CompletedTask;
    }

    /// <summary>Таргеты, диск которых «служба не видит»: сессия есть, а поиск диска возвращает <c>null</c>.</summary>
    public HashSet<string> HiddenDisks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Чем падает поиск диска таргета (таймаут PowerShell и т. п.).</summary>
    public Dictionary<string, Exception> FindDiskErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Для каких таргетов искали диск — по порядку.</summary>
    public List<string> DiskLookups { get; } = [];

    /// <summary>Что делать при поиске диска таргета до ответа (тест двигает часы: «PowerShell шёл 120 с»).</summary>
    public Action<string>? OnFindDisk { get; set; }

    public Task<DiskInfo?> FindDiskAsync(string targetIqn, CancellationToken ct)
    {
        DiskLookups.Add(targetIqn);
        OnFindDisk?.Invoke(targetIqn);
        if (FindDiskErrors.TryGetValue(targetIqn, out var error))
        {
            return Task.FromException<DiskInfo?>(error);
        }

        return Task.FromResult(
            !HiddenDisks.Contains(targetIqn) && !RecoveringSessions.Contains(targetIqn) && _sessions.TryGetValue(targetIqn, out var d)
                ? new DiskInfo(d.Number, d.ReadOnly, d.Offline, d.Letter)
                : null);
    }

    /// <summary>Чем падает установка read-only (таймаут PowerShell); <c>null</c> — не падает.</summary>
    public Exception? SetReadOnlyError { get; set; }

    public Task SetDiskReadOnlyAsync(int diskNumber, CancellationToken ct)
    {
        Log.Add($"ro {diskNumber}");
        if (SetReadOnlyError is { } error)
        {
            throw error;
        }

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
        if (LocalLetters.Contains(letter) || _sessions.Values.Any(d => d.Number != diskNumber && d.Letter == letter))
        {
            throw new InvalidOperationException($"drive letter {letter} is in use");
        }

        Disk(diskNumber).Letter = letter;
        return Task.CompletedTask;
    }

    /// <summary>Для каких букв спрашивали, заняты ли они.</summary>
    public List<char> LetterChecks { get; } = [];

    public Task<bool> DriveLetterInUseAsync(char letter, CancellationToken ct)
    {
        LetterChecks.Add(letter);
        return Task.FromResult(LocalLetters.Contains(letter) || _sessions.Values.Any(d => d.Letter == letter));
    }

    /// <summary>Вход с CHAP: какие учётные данные передал помощник (секрет проверяет «таргет» — поле ExpectedChap).</summary>
    public (string User, string Secret)? ExpectedChap { get; set; }

    public List<(string Iqn, string User, string Secret)> ChapLogins { get; } = [];

    /// <summary>Чем падает вход с CHAP помимо неверного секрета (то, чего MasterManager не ждёт); <c>null</c> — не падает.</summary>
    public Exception? ConnectChapError { get; set; }

    public async Task ConnectChapAsync(string targetIqn, string portalHost, int portalPort, string chapUser, string chapSecret, CancellationToken ct)
    {
        if (ConnectChapError is { } error)
        {
            throw error;
        }

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
            if (!LocalLetters.Contains(c) && _sessions.Values.All(d => d.Letter != c))
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

    /// <summary>Чем падает опрос процессов (неожиданный сбой Win32); <c>null</c> — не падает.</summary>
    public Exception? Error { get; set; }

    public Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct) =>
        Error is { } error
            ? Task.FromException<IReadOnlyList<string>>(error)
            : Task.FromResult<IReadOnlyList<string>>(Running.TryGetValue(driveLetter, out var list) ? list : []);

    /// <summary>Сколько раз помощник просил подробный проход по процессам (дорогой — только при отказе Windows).</summary>
    public List<char> Diagnoses { get; } = [];

    /// <summary>
    /// Проход, когда процесса с тома не нашлось: устройство тома есть, с него — ни одного процесса (пути образов с него
    /// нашлись бы), вне папки Windows — Steam с C: (такой и держит файлы на G:) и служба NVIDIA. Не открылся один
    /// процесс (Idle: OpenProcess отвечает ошибкой 87).
    /// </summary>
    public Task<ProcessScanReport> DiagnoseAsync(char driveLetter, CancellationToken ct)
    {
        Diagnoses.Add(driveLetter);
        var open = new ErrorTally();
        open.Add(87);
        ProcessImage[] samples =
        [
            new(3312, @"C:\Program Files (x86)\Steam\steam.exe", @"\Device\HarddiskVolume3\Program Files (x86)\Steam\steam.exe"),
            new(2280, @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", @"\Device\HarddiskVolume3\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe"),
        ];
        return Task.FromResult(new ProcessScanReport(
            driveLetter, @"\Device\HarddiskVolume12", null, 180, 0, open, new ErrorTally(), new ErrorTally(), @"C:\Windows", samples.Length, samples));
    }
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

    /// <summary>Имя Windows сейчас: как настоящий источник, перечитывается на каждый отчёт (ПК переименовали после старта).</summary>
    public string Hostname { get; set; } = "PC-TEST";

    public int FullReads { get; private set; }

    public int IqnReads { get; private set; }

    public Task<MachineFacts> ReadAllAsync(CancellationToken ct)
    {
        FullReads++;
        return Task.FromResult(new MachineFacts(hwid, Hostname, ["aa:bb:cc:dd:ee:01"], "Windows 11 Pro 24H2", DateTimeOffset.UtcNow.AddMinutes(-3)));
    }

    public Task<string?> ReadInitiatorIqnAsync(CancellationToken ct)
    {
        IqnReads++;
        return IqnReadError is { } error ? Task.FromException<string?>(error) : Task.FromResult(InitiatorIqn);
    }

    public MachineFacts Refresh(MachineFacts facts) => facts with { DhcpServers = [.. DhcpServers], Hostname = Hostname };
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

/// <summary>Журналы в память по категориям (циклы библиотек пишут каждый под своей).</summary>
public sealed class ListLoggerFactory : ILoggerFactory
{
    public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<(LogLevel Level, string Message)> Of(string category)
    {
        lock (Entries)
        {
            return Entries.Where(e => e.Category == category).Select(e => (e.Level, e.Message)).ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ListLoggerFactory factory, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (factory.Entries)
            {
                factory.Entries.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }
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
