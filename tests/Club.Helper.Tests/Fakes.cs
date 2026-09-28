using Club.Helper.Core;

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

    public Task<MachineFacts> ReadAsync(CancellationToken ct) =>
        Task.FromResult(new MachineFacts(hwid, "PC-TEST", ["aa:bb:cc:dd:ee:01"], "Windows 11 Pro 24H2", DateTimeOffset.UtcNow.AddMinutes(-3), [.. DhcpServers], SecureBoot: SecureBoot));
}
