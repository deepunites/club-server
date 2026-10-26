#Requires -Version 5.1
<#
.SYNOPSIS
    Первая разметка мастер-тома библиотеки игр: GPT, один раздел NTFS (кластер 64 КБ = блок zvol). G: — метка
    Online Games, буква M:; H: (-Library solo) — метка Offline Games, буква H:.

.DESCRIPTION
    Только один раз, на ПК, которому в панели открыта правка этой библиотеки (помощник подключил таргет …:club-master,
    у H: — …:club-master-solo). Скрипт находит диск строго по iSCSI-сессии мастер-тома этой библиотеки и размечает его,
    только если он пустой (RAW, без разделов): системный диск, мастер-том другой библиотеки и уже размеченный мастер-том
    не трогает. После разметки помощник видит раздел и держит букву мастер-тома (MasterDriveLetter библиотеки).

.PARAMETER Library
    games (по умолчанию) — G:, «Online Games», таргет club-master; solo — H:, «Offline Games», таргет club-master-solo
    (помощник 1.6.0, docs/master-editing.md).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\init-library.ps1 -Library solo
#>
[CmdletBinding()]
param(
    [ValidateSet('games', 'solo')]
    [string]$Library = 'games'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

# Таргет мастер-тома — как на сервере (LibraryNames) и у помощника; буква первой разметки и метка тома.
if ($Library -eq 'solo') { $Target = 'club-master-solo'; $Letter = 'H'; $Label = 'Offline Games' }
else { $Target = 'club-master'; $Letter = 'M'; $Label = 'Online Games' }

$session = @(Get-IscsiSession | Where-Object { $_.TargetNodeAddress -match (':{0}$' -f [regex]::Escape($Target)) -and $_.IsConnected })
if ($session.Count -ne 1) { throw ('Нет подключения к мастер-тому библиотеки (…:{0}): откройте правку этой библиотеки в панели и подождите 30 с.' -f $Target) }
$disk = @($session[0] | Get-Disk)
if ($disk.Count -ne 1) { throw ('Диск мастер-тома не найден (Get-Disk по сессии {0}).' -f $Target) }
$disk = $disk[0]
$system = (Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) | Get-Disk).Number
Write-Host ("Диск {0}: {1}, {2:N0} ГБ, разметка {3}, только чтение {4}, offline {5}" -f $disk.Number, $disk.FriendlyName, ($disk.Size / 1GB), $disk.PartitionStyle, $disk.IsReadOnly, $disk.IsOffline)

if ($disk.Number -eq $system) { throw 'Это системный диск — отказ.' }
if ($disk.PartitionStyle -ne 'RAW' -or @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host 'Диск уже размечен — ничего не делаю (букву мастер-тома назначает помощник).' -ForegroundColor Yellow
    return
}

if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
Initialize-Disk -Number $disk.Number -PartitionStyle GPT
$part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $Letter
$vol = Format-Volume -Partition $part -FileSystem NTFS -AllocationUnitSize 65536 -NewFileSystemLabel $Label -Confirm:$false
Write-Host ("Готово: {0}: — {1}, {2:N0} ГБ свободно, кластер 64 КБ." -f $Letter, $vol.FileSystemLabel, ($vol.SizeRemaining / 1GB)) -ForegroundColor Green
if ($Library -eq 'solo') {
    # На H: — только папки игр с exe, без лаунчеров: Steam и его список библиотек — на G:, Epic, Ubisoft и Rockstar
    # записывают установки на C: (docs/master-editing.md).
    Write-Host 'Ставьте игры в H:\Games — папки игр, запуск exe без лаунчера (Steam, Epic, Ubisoft, Rockstar — только на G:).' -ForegroundColor Green
    Write-Host 'Потом — в панели «Закончить правку» и «Опубликовать» у Offline Games (и у Online Games, если правили G:).' -ForegroundColor Green
} else {
    Write-Host ('Ставьте Steam и игры на {0}:. Потом — в панели «Закончить правку» и «Опубликовать».' -f $Letter) -ForegroundColor Green
}
