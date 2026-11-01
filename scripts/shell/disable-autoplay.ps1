<#
.SYNOPSIS
    Выключает автозапуск (AutoPlay/AutoRun) Windows на всех дисках для всех пользователей.
.DESCRIPTION
    Помощник подключает G: и H: (iSCSI) после входа, и Windows показывает игроку в киоске окно «Автозапуск: выберите
    действие». Политика HKLM «Отключить автозапуск» убирает это окно для всех учётных записей (player, Club) и
    запрещает autorun.inf с флешек и дисков. Шелл ClubShell этим не управляет.
    PowerShell от администратора; по сети: irm http://192.168.1.201:5080/pxe/v1/files/tools/disable-autoplay.ps1 | iex
    Повторный запуск безопасен. Строки вывода — только ASCII.
#>
# irm | iex выполняет скрипт в области консоли: настройка ошибок возвращается в finally, чтобы не остаться в консоли.
$savedErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Stop'
try {
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

function Set-Dword([string] $Key, [string] $Name, [int] $Value) {
    # New-Item -Force на существующем ключе реестра пересоздал бы его пустым — только для отсутствующего.
    if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key | Out-Null }
    New-ItemProperty -LiteralPath $Key -Name $Name -Value $Value -PropertyType DWord -Force | Out-Null
    Write-Host "  $Key\$Name = $Value"
}

Write-Host 'Turning AutoPlay / AutoRun off for every drive and every user'
Set-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoDriveTypeAutoRun' 255
Set-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoAutorun' 1
Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer' 'NoAutoplayfornonVolume' 1
Write-Host 'Done: no AutoPlay window from the next logon on (G:/H: and USB included).' -ForegroundColor Green
} finally {
    $ErrorActionPreference = $savedErrorAction
}
