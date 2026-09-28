using System.Globalization;
using System.Text.Json;
using Club.Helper.Core;

namespace Club.Helper;

/// <summary>
/// iSCSI и диски через штатные модули Windows (iSCSI, Storage). Порядок вызовов задаёт
/// <see cref="VolumeManager"/>. [ГИПОТЕЗА: поведение командлетов на read-only iSCSI-диске проверить на стенде —
/// см. README помощника, раздел «Проверка на стенде».]
/// </summary>
public sealed class WindowsStorage : IWindowsStorage
{
    public Task EnsureSanPolicyOfflineSharedAsync(CancellationToken ct) =>
        PowerShell.RunAsync("Set-StorageSetting -NewDiskPolicy OfflineShared", null, ct);

    public async Task<IReadOnlyList<string>> ConnectedTargetsAsync(CancellationToken ct)
    {
        var json = await PowerShell.RunAsync(
            """
            $targets = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object IsConnected | ForEach-Object { $_.TargetNodeAddress })
            ConvertTo-Json -InputObject $targets -Compress
            """, null, ct);
        return ParseStrings(json);
    }

    public Task ConnectAsync(string targetIqn, string portalHost, int portalPort, CancellationToken ct) =>
        PowerShell.RunAsync(
            """
            Set-Service -Name MSiSCSI -StartupType Automatic
            Start-Service -Name MSiSCSI
            $port = [int]$env:CLUB_PORTAL_PORT
            $portal = Get-IscsiTargetPortal -ErrorAction SilentlyContinue |
                Where-Object { $_.TargetPortalAddress -eq $env:CLUB_PORTAL_HOST -and $_.TargetPortalPortNumber -eq $port }
            if (-not $portal) {
                New-IscsiTargetPortal -TargetPortalAddress $env:CLUB_PORTAL_HOST -TargetPortalPortNumber $port | Out-Null
            } else {
                Update-IscsiTargetPortal -TargetPortalAddress $env:CLUB_PORTAL_HOST -TargetPortalPortNumber $port | Out-Null
            }
            # Без -IsPersistent: при загрузке подключает сам помощник — актуальную версию, а не ту, что была вчера.
            Connect-IscsiTarget -NodeAddress $env:CLUB_IQN -TargetPortalAddress $env:CLUB_PORTAL_HOST -TargetPortalPortNumber $port -IsPersistent $false | Out-Null
            """,
            new Dictionary<string, string>
            {
                ["CLUB_IQN"] = targetIqn,
                ["CLUB_PORTAL_HOST"] = portalHost,
                ["CLUB_PORTAL_PORT"] = portalPort.ToString(CultureInfo.InvariantCulture),
            },
            ct);

    public Task DisconnectAsync(string targetIqn, CancellationToken ct) =>
        PowerShell.RunAsync(
            """
            $session = Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -eq $env:CLUB_IQN }
            if ($session) { Disconnect-IscsiTarget -NodeAddress $env:CLUB_IQN -Confirm:$false | Out-Null }
            """,
            new Dictionary<string, string> { ["CLUB_IQN"] = targetIqn },
            ct);

    public async Task<DiskInfo?> FindDiskAsync(string targetIqn, CancellationToken ct)
    {
        var json = await PowerShell.RunAsync(
            """
            $session = Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -eq $env:CLUB_IQN -and $_.IsConnected } | Select-Object -First 1
            if (-not $session) { 'null'; exit 0 }
            $disk = Get-Disk -iSCSISession $session -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $disk) { 'null'; exit 0 }
            $letter = Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue |
                Where-Object { [int][char]$_.DriveLetter -ne 0 } | Select-Object -First 1 -ExpandProperty DriveLetter
            [pscustomobject]@{
                number = $disk.Number
                isReadOnly = [bool]$disk.IsReadOnly
                isOffline = [bool]$disk.IsOffline
                driveLetter = if ($letter) { [string]$letter } else { $null }
            } | ConvertTo-Json -Compress
            """,
            new Dictionary<string, string> { ["CLUB_IQN"] = targetIqn },
            ct);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var letter = root.TryGetProperty("driveLetter", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) : (char?)null;
        return new DiskInfo(root.GetProperty("number").GetInt32(), root.GetProperty("isReadOnly").GetBoolean(), root.GetProperty("isOffline").GetBoolean(), letter);
    }

    public Task SetDiskReadOnlyAsync(int diskNumber, CancellationToken ct) =>
        PowerShell.RunAsync("Set-Disk -Number ([int]$env:CLUB_DISK) -IsReadOnly $true", Disk(diskNumber), ct);

    public Task SetDiskOnlineAsync(int diskNumber, CancellationToken ct) =>
        PowerShell.RunAsync("Set-Disk -Number ([int]$env:CLUB_DISK) -IsOffline $false", Disk(diskNumber), ct);

    public Task<IReadOnlyList<string>> ListFoldersAsync(char driveLetter, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(new DirectoryInfo($"{driveLetter}:\\").EnumerateDirectories()
            .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(d => d.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList());

    public Task AssignDriveLetterAsync(int diskNumber, char letter, CancellationToken ct) =>
        PowerShell.RunAsync(
            """
            if (Get-Volume -DriveLetter $env:CLUB_LETTER -ErrorAction SilentlyContinue) { throw "drive letter $($env:CLUB_LETTER) is in use" }
            $part = Get-Partition -DiskNumber ([int]$env:CLUB_DISK) |
                Where-Object { $_.Type -eq 'Basic' -or $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' } |
                Sort-Object -Property Size -Descending | Select-Object -First 1
            if (-not $part) { throw 'no data partition on the library disk' }
            Set-Partition -InputObject $part -NewDriveLetter $env:CLUB_LETTER
            """,
            new Dictionary<string, string> { ["CLUB_DISK"] = diskNumber.ToString(CultureInfo.InvariantCulture), ["CLUB_LETTER"] = letter.ToString() },
            ct);

    private static Dictionary<string, string> Disk(int number) => new() { ["CLUB_DISK"] = number.ToString(CultureInfo.InvariantCulture) };

    private static IReadOnlyList<string> ParseStrings(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList(),
            JsonValueKind.String => [doc.RootElement.GetString()!],
            _ => [],
        };
    }
}
