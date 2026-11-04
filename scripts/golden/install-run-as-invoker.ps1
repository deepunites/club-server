<#
.SYNOPSIS
    Запуск без прав администратора для игр, чей exe их требует (requireAdministrator в манифесте): ICCup Launcher.
.DESCRIPTION
    Шелл (агент ClubShell) запускает игру от имени игрока — обычного пользователя без прав администратора. Exe с
    requireAdministrator в манифесте так не запускается: CreateProcess отвечает ERROR_ELEVATION_REQUIRED (740), и шелл
    показывает «Не удалось запустить игру». Слой совместимости RUNASINVOKER (тот же, что __COMPAT_LAYER=RunAsInvoker)
    запускает такой exe с правами того, кто его запускает: у игрока — с его обычными правами, без окна UAC.
    Что делает (PowerShell от администратора; на эталоне бездиска — в режиме мастера, до seal-golden.ps1): для каждого
    пути из $Exes пишет в HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers (и в 32-битное
    представление, WOW6432Node) значение «<полный путь>» = «~ RUNASINVOKER»; другие флаги этого значения остаются,
    RUNASADMIN убирается. HKLM, а не HKCU: агент — служба со своим HKCU, а настройка нужна всем пользователям ПК.
    Путь — как в кассе (поле «Путь к .exe»): слой привязан к пути, файла на ПК может ещё не быть (старая версия G:).
    Игре, которой права администратора правда нужны, слой не поможет: она запустится, но упадёт или не сможет что-то
    записать. Сначала проверить на одном ПК под игроком.
    Добавить игру — строку в $Exes и запустить скрипт снова; повторный запуск безопасен.
    Убрать слой у пути: Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' '<путь>'
    (и то же в HKLM:\SOFTWARE\WOW6432Node\…).
    По сети одной строкой: irm http://192.168.1.201:5080/pxe/v1/files/tools/install-run-as-invoker.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
$ErrorActionPreference = 'Stop'
$Exes = @(
    'G:\Warcraft 3\Launcher\Launcher.exe'   # ICCup Launcher (запускает war3.exe; requireAdministrator)
)
$Keys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

foreach ($key in $Keys) {
    if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
    $current = Get-ItemProperty -LiteralPath $key
    foreach ($exe in $Exes) {
        $old = "$($current.$exe)"
        # Значение: «~» и флаги через пробел (Windows 8+); остальные флаги (HIGHDPIAWARE и т.п.) сохраняются.
        $flags = @($old -split ' ' | Where-Object { $_ -and $_ -ne '~' -and $_ -ne 'RUNASADMIN' -and $_ -ne 'RUNASINVOKER' })
        $value = (@('~', 'RUNASINVOKER') + $flags) -join ' '
        if ($old -eq $value) { Write-Host "already set: $key [$exe] = $value"; continue }
        New-ItemProperty -LiteralPath $key -Name $exe -Value $value -PropertyType String -Force | Out-Null
        $was = if ($old) { " (was '$old')" } else { '' }
        Write-Host "set: $key [$exe] = $value$was" -ForegroundColor Yellow
    }
}
foreach ($exe in $Exes) {
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Host "note: $exe is not on this PC yet (older G: version?): the setting applies once it is there" -ForegroundColor Yellow
    }
}
Write-Host 'Done: these games start without administrator rights for the player' -ForegroundColor Green
