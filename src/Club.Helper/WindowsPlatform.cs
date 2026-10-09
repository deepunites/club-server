using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Club.Helper.Core;

namespace Club.Helper;

/// <summary>
/// Процессы, чей исполняемый файл лежит на томе. Путь читается через QueryFullProcessImageName с правом
/// PROCESS_QUERY_LIMITED_INFORMATION — оно есть и у процессов под защитой античита, поэтому игра не «пропадёт». Путь
/// берётся в двух формах — Win32 (<c>G:\…</c>) и NT (<c>\Device\HarddiskVolume12\…</c>, PROCESS_NAME_NATIVE) — и
/// сравнивается с буквой и с устройством NT тома (QueryDosDevice): процесс с тома, если совпала любая. Стенд 2026-10-02,
/// помощник 1.4.1: служба по одному Win32-пути не нашла запущенные с G: cstrike.exe и steam.exe (причина не
/// установлена), поэтому обе формы и диагностика <see cref="DiagnoseAsync"/>. Сравнение путей — <see cref="VolumeImagePaths"/>.
/// </summary>
public sealed class ProcessInspector(ILogger<ProcessInspector> logger) : IProcessInspector
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint ProcessNameNative = 1;

    /// <summary>Причины «по одному пути Win32 не нашёлся бы», о которых уже предупредили: одна запись на причину, а не каждый такт.</summary>
    private readonly HashSet<string> _win32Misses = [];

    public Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct)
    {
        var device = NtDevice(driveLetter, out var deviceError);
        var scan = ReadImages(ct);
        var found = new List<string>();
        foreach (var image in scan.Images)
        {
            if (VolumeImagePaths.MatchImage(image.Win32Path, image.NativePath, driveLetter, device) is not { } match)
            {
                continue;
            }

            found.Add(match.Path);
            if (match.Win32Miss is { } miss)
            {
                ReportWin32Miss(image, miss, device);
            }
        }

        logger.LogDebug(
            "Processes from {Letter}: {Found} of {Total} (device {Device}); OpenProcess failed {Open}, Win32 path failed {Win32}, NT path failed {Native}",
            driveLetter, found.Count, scan.Images.Count, device ?? deviceError, scan.Open, scan.Win32, scan.Native);
        return Task.FromResult<IReadOnlyList<string>>(found);
    }

    public Task<ProcessScanReport> DiagnoseAsync(char driveLetter, CancellationToken ct)
    {
        var device = NtDevice(driveLetter, out var deviceError);
        var scan = ReadImages(ct);
        var matched = scan.Images.Count(i => VolumeImagePaths.Match(i.Win32Path, i.NativePath, driveLetter, device) is not null);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsNt = windows is [var drive, ':', ..] && NtDevice(drive, out _) is { } systemDevice ? systemDevice + windows[2..] : null;
        var outside = VolumeImagePaths.OutsideWindows(scan.Images, windows, windowsNt);
        return Task.FromResult(new ProcessScanReport(
            driveLetter, device, deviceError, scan.Images.Count, matched, scan.Open, scan.Win32, scan.Native, windows, outside.Count,
            outside.Take(ProcessScanReport.MaxSamples).ToList()));
    }

    /// <summary>
    /// Процесс с тома нашёлся, но не по пути Win32 под буквой (путь не прочитался, другая форма или префикс) — ради
    /// этого случая путь NT и читается (стенд 2026-10-02: 1.4.1 по пути Win32 не нашла запущенные с G: игру и Steam).
    /// Предупреждение с обеими формами пути подтверждает гипотезу; одно на причину за время работы службы.
    /// </summary>
    private void ReportWin32Miss(ProcessImage image, string miss, string? device)
    {
        lock (_win32Misses)
        {
            if (!_win32Misses.Add(miss))
            {
                return;
            }
        }

        logger.LogWarning(
            "Process [{Pid}] runs from the library volume, but a plain Win32 path check (helper 1.4.1) would miss it ({Miss}): Win32 path {Win32}, NT path {Native}, volume device {Device}",
            image.Pid, miss, image.Win32Path ?? "?", image.NativePath ?? "?", device);
    }

    /// <summary>Все процессы (не открылся — без путей) и сбои: OpenProcess, путь Win32, путь NT.</summary>
    private sealed record Scan(List<ProcessImage> Images, ErrorTally Open, ErrorTally Win32, ErrorTally Native);

    private static Scan ReadImages(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scan = new Scan([], new ErrorTally(), new ErrorTally(), new ErrorTally());
        var buffer = new char[32768];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                scan.Images.Add(Read(process.Id, buffer, scan));
            }
        }

        return scan;
    }

    private static ProcessImage Read(int pid, char[] buffer, Scan scan)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            scan.Open.Add(Marshal.GetLastPInvokeError());
            return new ProcessImage(pid, null, null);
        }

        try
        {
            return new ProcessImage(pid, ImagePath(handle, 0, buffer, scan.Win32), ImagePath(handle, ProcessNameNative, buffer, scan.Native));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ImagePath(IntPtr handle, uint flags, char[] buffer, ErrorTally failures)
    {
        var size = (uint)buffer.Length;
        if (QueryFullProcessImageNameW(handle, flags, buffer, ref size))
        {
            return new string(buffer, 0, (int)size);
        }

        failures.Add(Marshal.GetLastPInvokeError());
        return null;
    }

    /// <summary>Устройство NT тома по букве (QueryDosDevice, см. <see cref="VolumeImagePaths.ResolveNtDevice"/>); не вышло — <c>null</c> и причина.</summary>
    private static string? NtDevice(char letter, out string? error)
    {
        string? failure = null;
        var device = VolumeImagePaths.ResolveNtDevice(letter, name =>
        {
            var buffer = new char[4096];
            var length = QueryDosDeviceW(name, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                failure ??= $"QueryDosDevice({name}) error {Marshal.GetLastPInvokeError()}";
                return null;
            }

            // Ответ — несколько строк через \0; нужна первая (текущее значение имени).
            var end = Array.IndexOf(buffer, '\0', 0, (int)Math.Min(length, (uint)buffer.Length));
            return new string(buffer, 0, end < 0 ? (int)length : end);
        });
        error = device is null ? failure ?? "QueryDosDevice gave no NT device path" : null;
        return device;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Факты машины: HWID из SMBIOS UUID и серийника платы, MAC физических Ethernet-карт, IQN инициатора. Что из этого
/// кэшируется и что читается повторно, решает <see cref="CachedMachineIdentity"/>.
/// </summary>
public sealed class WindowsMachineFacts : IMachineFactsSource
{
    /// <summary>
    /// IQN инициатора iSCSI: по нему сервер пускает к мастер-тому только этот ПК (режим суперклиента). Пока служба
    /// MSiSCSI остановлена (так в Windows по умолчанию), <c>Get-InitiatorPort</c> не отдаёт iSCSI-порт, поэтому служба
    /// запускается здесь, а не только при подключении тома: иначе на новом стенде ПК суперклиента не сообщит IQN
    /// никогда. Ожидание запуска — не дольше 10 с: <c>Start-Service</c> в PowerShell 5.1 ждёт службу в StartPending
    /// без срока (и пишет предупреждения в stdout, ломая JSON), а <c>ServiceController.Start</c> и
    /// <c>WaitForStatus</c> с таймаутом ничего не выводят. Всё best effort — сбой не срывает опрос, IQN — <c>null</c>.
    /// </summary>
    private const string InitiatorIqnScript = """
        $iqn = $null
        try {
            $svc = Get-Service -Name MSiSCSI -ErrorAction Stop
            if ($svc.StartType -ne 'Automatic') { Set-Service -Name MSiSCSI -StartupType Automatic -ErrorAction Stop }
            if ($svc.Status -eq 'Stopped') { $svc.Start() }
            if ($svc.Status -ne 'Running') { $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(10)) }
        } catch { }
        try { $iqn = (Get-InitiatorPort -ErrorAction Stop | Where-Object { $_.ConnectionType -eq 'iSCSI' } | Select-Object -First 1).NodeAddress } catch { }
        ConvertTo-Json -InputObject $iqn -Compress
        """;

    /// <summary>
    /// Полный опрос без IQN: служба MSiSCSI, запускаемая ради IQN, не задерживает HWID и MAC — IQN читает
    /// <see cref="ReadInitiatorIqnAsync"/>, первый раз на следующем такте после этого опроса (<see cref="CachedMachineIdentity"/>).
    /// </summary>
    public async Task<MachineFacts> ReadAllAsync(CancellationToken ct)
    {
        var json = await PowerShell.RunAsync(
            """
            $product = Get-CimInstance -ClassName Win32_ComputerSystemProduct
            $board = Get-CimInstance -ClassName Win32_BaseBoard
            $os = Get-CimInstance -ClassName Win32_OperatingSystem
            $disk = Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) | Get-Disk
            # Secure Boot — проверки из руководства Microsoft по CVE-2023-24932 (строки сертификатов в db/dbx).
            $sb = $null
            try {
                $db = [Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes)
                $dbx = [Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI dbx).bytes)
                $sb = [pscustomobject]@{
                    enabled = [bool](Confirm-SecureBootUEFI)
                    thirdPartyCa2011 = $db -match 'Microsoft Corporation UEFI CA 2011'
                    windowsCa2023 = $db -match 'Windows UEFI CA 2023'
                    pca2011Revoked = $dbx -match 'Microsoft Windows Production PCA 2011'
                }
            } catch { }
            [pscustomobject]@{
                secureBoot = $sb
                uuid = [string]$product.UUID; board = [string]$board.SerialNumber; os = "$($os.Caption) $($os.Version)"
                diskSerial = ([string]$disk.SerialNumber).Trim(); diskModel = [string]$disk.FriendlyName; diskSize = [long]$disk.Size; diskBus = [string]$disk.BusType
            } | ConvertTo-Json -Compress
            """, null, ct);
        using var doc = JsonDocument.Parse(json);
        var uuid = doc.RootElement.GetProperty("uuid").GetString() ?? "";
        var board = doc.RootElement.GetProperty("board").GetString() ?? "";
        var hwid = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{uuid.Trim().ToLowerInvariant()}|{board.Trim()}")));
        // Основная карта (поднята, есть IPv4-шлюз) — первой: по первому MAC сервер резервирует адрес места в Kea.
        var macs = EthernetInterfaces()
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .Where(b => b.Length == 6)
            .Select(b => string.Join(':', b.Select(x => x.ToString("x2"))))
            .Distinct()
            .ToList();
        var root = doc.RootElement;
        var systemDisk = root.TryGetProperty("diskSize", out var size) && size.TryGetInt64(out var bytes) && bytes > 0
            ? new SystemDiskFacts(root.GetProperty("diskSerial").GetString(), root.GetProperty("diskModel").GetString(), bytes, root.GetProperty("diskBus").GetString() ?? "")
            : null;
        var secureBoot = root.TryGetProperty("secureBoot", out var sb) && sb.ValueKind == JsonValueKind.Object
            ? new SecureBootFacts(Flag(sb, "enabled"), Flag(sb, "thirdPartyCa2011"), Flag(sb, "windowsCa2023"), Flag(sb, "pca2011Revoked"))
            : null;
        return new MachineFacts(
            hwid, Environment.MachineName, macs, root.GetProperty("os").GetString() ?? "", BootTime(), SystemDisk: systemDisk, SecureBoot: secureBoot);
    }

    /// <summary>Короткий опрос одного IQN: полный (CIM, Secure Boot, диск) ради него не повторяется.</summary>
    public async Task<string?> ReadInitiatorIqnAsync(CancellationToken ct)
    {
        var json = await PowerShell.RunAsync(InitiatorIqnScript, null, ct);
        if (json.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
    }

    public MachineFacts Refresh(MachineFacts facts) =>
        facts with { BootTime = BootTime(), DhcpServers = DhcpServers(), ImageVersion = ImageVersion() };

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Версия образа, которую записал WinPE при заливке (<c>image.json</c> в папке состояния помощника).</summary>
    private static string? ImageVersion()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(StateDirectory.Path, "image.json")));
            return doc.RootElement.TryGetProperty("label", out var label) ? label.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<NetworkInterface> EthernetInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet);

    /// <summary>DHCP-серверы текущих аренд поднятых Ethernet-карт (на каждый отчёт заново: аренда могла смениться).</summary>
    private static List<string> DhcpServers() =>
        EthernetInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DhcpServerAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToList();

    private static DateTimeOffset BootTime() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Папка состояния помощника: доступ только SYSTEM и администраторам.</summary>
public static class StateDirectory
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubDiskless");

    public static string Ensure()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(Path);
        if (!directory.Exists)
        {
            directory.Create(security);
        }
        else
        {
            directory.SetAccessControl(security);
        }

        return Path;
    }
}

/// <summary>Токены машины под DPAPI (область машины) в папке, доступной только SYSTEM и администраторам.</summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("club-diskless-helper");
    private readonly string _file = System.IO.Path.Combine(StateDirectory.Ensure(), "credentials.bin");

    public async Task<StoredCredentials?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_file))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(_file, ct), Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<StoredCredentials>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null; // повреждённый файл — просто зарегистрируемся заново
        }
    }

    public async Task SaveAsync(StoredCredentials credentials, CancellationToken ct)
    {
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials), Entropy, DataProtectionScope.LocalMachine);
        var temp = _file + ".tmp";
        await File.WriteAllBytesAsync(temp, cipher, ct);
        File.Move(temp, _file, overwrite: true);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        File.Delete(_file);
        return Task.CompletedTask;
    }
}

/// <summary>Последнее назначение тома — на диске ПК, чтобы при недоступном сервере игры всё равно были.</summary>
public sealed class FileAssignmentCache : IAssignmentCache
{
    private readonly string _file = System.IO.Path.Combine(StateDirectory.Ensure(), "assignment.json");

    public async Task<VolumeAssignment?> LoadAsync(CancellationToken ct)
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<VolumeAssignment>(await File.ReadAllTextAsync(_file, ct)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(VolumeAssignment? assignment, CancellationToken ct)
    {
        if (assignment is null)
        {
            File.Delete(_file);
            return;
        }

        var temp = _file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(assignment), ct);
        File.Move(temp, _file, overwrite: true);
    }
}
