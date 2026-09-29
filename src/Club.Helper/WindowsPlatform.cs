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
/// PROCESS_QUERY_LIMITED_INFORMATION — оно есть и у процессов под защитой античита, поэтому игра не «пропадёт».
/// </summary>
public sealed partial class ProcessInspector : IProcessInspector
{
    private const uint QueryLimitedInformation = 0x1000;

    public Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct)
    {
        var prefix = $"{char.ToUpperInvariant(driveLetter)}:\\";
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(path);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(found);
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Идентичность машины: HWID из SMBIOS UUID и серийника платы, MAC физических Ethernet-карт.</summary>
public sealed class MachineIdentity : IMachineIdentity
{
    private MachineFacts? _cached;

    public async Task<MachineFacts> ReadAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached with { BootTime = BootTime(), DhcpServers = DhcpServers(), ImageVersion = ImageVersion() };
        }

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
            # IQN инициатора iSCSI: по нему сервер пускает к мастер-тому только этот ПК (режим суперклиента).
            $iqn = $null
            try { $iqn = (Get-InitiatorPort -ErrorAction Stop | Where-Object { $_.ConnectionType -eq 'iSCSI' } | Select-Object -First 1).NodeAddress } catch { }
            [pscustomobject]@{
                secureBoot = $sb
                initiatorIqn = $iqn
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
        var initiatorIqn = root.TryGetProperty("initiatorIqn", out var iqn) && iqn.ValueKind == JsonValueKind.String ? iqn.GetString() : null;
        _cached = new MachineFacts(
            hwid, Environment.MachineName, macs, root.GetProperty("os").GetString() ?? "", BootTime(), SystemDisk: systemDisk, SecureBoot: secureBoot,
            InitiatorIqn: initiatorIqn);
        return _cached with { DhcpServers = DhcpServers(), ImageVersion = ImageVersion() };
    }

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
