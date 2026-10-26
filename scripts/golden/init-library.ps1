#Requires -Version 5.1
<#
.SYNOPSIS
    Первая разметка мастер-тома библиотеки игр: GPT, один раздел NTFS (кластер 64 КБ = блок zvol), метка Online Games, буква M:.

.DESCRIPTION
    Только один раз, на ПК, которому в панели открыта правка библиотеки (помощник подключил таргет …:club-master).
    Скрипт находит диск строго по iSCSI-сессии club-master и размечает его, только если он пустой (RAW, без разделов):
    системный диск и уже размеченный мастер-том не трогает. После разметки помощник видит раздел и держит букву M:.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

$session = @(Get-IscsiSession | Where-Object { $_.TargetNodeAddress -match ':club-master$' -and $_.IsConnected })
if ($session.Count -ne 1) { throw 'Нет подключения к мастер-тому библиотеки (…:club-master): откройте правку библиотеки в панели и подождите 30 с.' }
$disk = @($session[0] | Get-Disk)
if ($disk.Count -ne 1) { throw 'Диск мастер-тома не найден (Get-Disk по сессии club-master).' }
$disk = $disk[0]
$system = (Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) | Get-Disk).Number
Write-Host ("Диск {0}: {1}, {2:N0} ГБ, разметка {3}, только чтение {4}, offline {5}" -f $disk.Number, $disk.FriendlyName, ($disk.Size / 1GB), $disk.PartitionStyle, $disk.IsReadOnly, $disk.IsOffline)

if ($disk.Number -eq $system) { throw 'Это системный диск — отказ.' }
if ($disk.PartitionStyle -ne 'RAW' -or @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host 'Диск уже размечен — ничего не делаю (помощник сам назначит M:).' -ForegroundColor Yellow
    return
}

if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
Initialize-Disk -Number $disk.Number -PartitionStyle GPT
$part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter M
$vol = Format-Volume -Partition $part -FileSystem NTFS -AllocationUnitSize 65536 -NewFileSystemLabel 'Online Games' -Confirm:$false
Write-Host ("Готово: M: — {0}, {1:N0} ГБ свободно, кластер 64 КБ." -f $vol.FileSystemLabel, ($vol.SizeRemaining / 1GB)) -ForegroundColor Green
Write-Host 'Ставьте Steam и игры на M:. Потом — в панели «Закончить правку» и «Опубликовать».' -ForegroundColor Green
