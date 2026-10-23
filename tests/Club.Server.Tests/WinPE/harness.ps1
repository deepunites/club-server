# Прогон скрипта заливки WinPE (club-deploy.ps1) на Linux в pwsh против настоящего сервера клуба.
# Команды Windows (диски, dism, bcdboot, реестр, перезагрузка) — заглушки: пишут вызовы в журнал, буквы дисков —
# PSDrive на временные каталоги. Проверяется всё, что не зависит от Windows: API, докачка, sha256, порядок шагов.
param([string]$Script, [string]$Root)

$global:Calls = [System.Collections.Generic.List[string]]::new()
function global:Log([string]$Text) { $global:Calls.Add($Text) }

$env:SystemRoot = Join-Path $Root 'X'

function global:Get-CimInstance { param([Parameter(Position = 0)]$ClassName) [pscustomobject]@{ PhysicalAdapter = $true; MACAddress = '02:00:00:00:00:01' } }
# Офлайн-кусты залитого образа: ImageState — из MOCK_IMAGE_STATE (по умолчанию после sysprep /oobe, 'none' — нет значения).
function global:Get-ItemProperty {
    [CmdletBinding()] param([Parameter(Position = 0)][string]$Path)
    if ($Path -like '*ClubOfflineSOFTWARE*\Setup\State') {
        $state = $env:MOCK_IMAGE_STATE ?? 'IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE'
        return $(if ($state -eq 'none') { $null } else { [pscustomobject]@{ ImageState = $state } })
    }
    if ($Path -like '*ClubOfflineSYSTEM*\SysprepStatus') { return [pscustomobject]@{ GeneralizationState = 7 } }
    [pscustomobject]@{ PEFirmwareType = 2 }
}
$global:MockDisks = @(
    [pscustomobject]@{ Number = 0; SerialNumber = 'USB0001'; FriendlyName = 'Flash'; Size = 64GB; BusType = 'USB'; IsOffline = $false; IsReadOnly = $false; PartitionStyle = 'MBR'; LargestFreeExtent = 0 },
    [pscustomobject]@{ Number = 1; SerialNumber = 'S5GXNX0T123456  '; FriendlyName = 'Samsung 980'; Size = 500GB; BusType = 'NVMe'; IsOffline = $false; IsReadOnly = $false; PartitionStyle = 'GPT'; LargestFreeExtent = 500GB - 300MB }
)
function global:Get-Disk { param([int]$Number = -1) if ($Number -lt 0) { $global:MockDisks } else { $global:MockDisks | Where-Object Number -eq $Number } }
function global:Set-Disk { param($Number, $IsOffline, $IsReadOnly) Log "Set-Disk $Number" }
function global:Clear-Disk { param($Number, [switch]$RemoveData, [switch]$RemoveOEM, [switch]$Confirm) Log "Clear-Disk $Number" }
function global:Initialize-Disk { param($Number, $PartitionStyle) Log "Initialize-Disk $Number $PartitionStyle" }
function global:New-Partition { param($DiskNumber, $Size, $GptType, [switch]$UseMaximumSize) Log "New-Partition $DiskNumber $GptType"; [pscustomobject]@{ DiskNumber = $DiskNumber } }
function global:Format-Volume { param($Partition, $FileSystem, $NewFileSystemLabel, [switch]$Confirm) Log "Format-Volume $FileSystem $NewFileSystemLabel" }
function global:Set-Partition {
    param([Parameter(ValueFromPipeline = $true)]$InputObject, [string]$NewDriveLetter)
    $dir = Join-Path $Root $NewDriveLetter
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    New-PSDrive -Name $NewDriveLetter -PSProvider FileSystem -Root $dir -Scope Global | Out-Null
    Log "Set-Partition $NewDriveLetter"
}
function global:Get-Volume { @() }
function global:Remove-Partition { param($DriveLetter, [switch]$Confirm) Log "Remove-Partition $DriveLetter" }
function global:Resize-Partition { param($DriveLetter, $Size) Log "Resize-Partition $DriveLetter" }
function global:Get-PartitionSupportedSize { param($DriveLetter) [pscustomobject]@{ SizeMax = 1 } }
function global:Start-Sleep { param($Seconds) }
function global:Read-Host { Log 'Read-Host'; '' }
# MOCK_SYSPREP_UNATTEND — образ «с unattend от sysprep»: dism /Apply-Image кладёт его в Windows\Panther (UTF-8 с BOM).
function global:dism.exe {
    Log "dism $($args -join ' ')"
    $applyDir = $args | Where-Object { $_ -like '/ApplyDir:*' } | ForEach-Object { $_.Substring('/ApplyDir:'.Length) }
    if ($applyDir -and $env:MOCK_SYSPREP_UNATTEND) {
        $panther = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath("${applyDir}Windows\Panther")
        New-Item -ItemType Directory -Force -Path $panther | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $panther 'unattend.xml'), $env:MOCK_SYSPREP_UNATTEND, [System.Text.UTF8Encoding]::new($true))
    }
    $global:LASTEXITCODE = 0
}
# MOCK_REG_LOAD_FAIL=SOFTWARE — этот куст не загружается (reg load с кодом 1).
function global:reg.exe {
    Log "reg $($args[0]) $($args[1])"
    $fail = $args[0] -eq 'load' -and $env:MOCK_REG_LOAD_FAIL -and $args[1] -like "*\ClubOffline$($env:MOCK_REG_LOAD_FAIL)"
    $global:LASTEXITCODE = $(if ($fail) { 1 } else { 0 })
}
function global:bcdboot.exe { Log "bcdboot $($args -join ' ')"; $global:LASTEXITCODE = [int]($env:MOCK_BCDBOOT_EXIT ?? '0') }
function global:bcdedit.exe { Log "bcdedit $($args -join ' ')"; 'Firmware Boot Manager'; $global:LASTEXITCODE = [int]($env:MOCK_BCDEDIT_EXIT ?? '0') }
function global:wpeutil.exe {
    Log "wpeutil $($args -join ' ')"
    $global:Calls | ConvertTo-Json | Set-Content -Path (Join-Path $Root 'calls.json')
    exit 0
}

. $Script
