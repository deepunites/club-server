#Requires -Version 5.1
<#
.SYNOPSIS
    Исправления эталона по итогам проверки первого места (2026-10-20): кодеки, образы NGEN, автозагрузка лаунчеров,
    Яндекс.Музыка.

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора. Повторный запуск безопасен.

    1. Кодеки Microsoft (VP9, Web Media, AV1, HEVC, HEIF, WebP, RAW, MPEG-2): ISO собран из файлов Windows Update без
       встроенных приложений, кодеков в образе не было — CS2 и Dota 2 онлайн вешались на анимированных WebM-аватарках
       игроков. Пакеты — с серверов Windows Update (SHA256 и подпись Microsoft проверены на сервере клуба), ставятся в
       образ для всех пользователей (provisioned) и текущему.
    2. Образы NGEN (.NET Framework): после обновлений и .NET 3.5 их надо достроить, иначе каждый запуск PowerShell на
       месте (сброс при каждой загрузке) компилирует всё заново — помощник бездиска грузил ЦП до 100%.
    3. Автозагрузка: Steam, Lesta, Epic, Battle.net, Ubisoft, Rockstar, Discord, обновлятор Edge — не стартуют при входе
       (на местах лаунчеры запускает шелл; каждый автозапуск — проверка обновлений при каждой загрузке).
    4. Яндекс.Музыка (пришла с установщиком Lesta) — удаляется.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools',
    [string]$Work = "$env:SystemDrive\ClubFixes"
)

$ErrorActionPreference = 'Continue'
$Codecs = @{ File = 'codecs-26300.zip'; Sha256 = 'd10a1187eb822c45fed07f0ccb195a5b1bd44a5eff83c91837186cd857d467fd' }
# Значения автозагрузки, которые убираем: по имени значения или по пути программы.
$AutostartPattern = 'steam|lgc|lesta|epicgames|battle\.net|ubisoft|upc\.exe|rockstar|discord|MicrosoftEdgeUpdate|Yandex'

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

function Measure-PsStart {
    $s = @(1..3 | ForEach-Object { (Measure-Command { & powershell.exe -NoProfile -NonInteractive -Command 'Get-IscsiSession | Out-Null' }).TotalSeconds })
    return ($s | Measure-Object -Minimum).Minimum
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

# Профиль по умолчанию (C:\Users\Default) — для автозагрузки новых учётных записей (игроки, шелл).
$DefaultHiveFile = "$env:SystemDrive\Users\Default\NTUSER.DAT"
$defaultLoaded = $false
if (Test-Path -LiteralPath $DefaultHiveFile) {
    & reg.exe load 'HKU\ClubDefault' $DefaultHiveFile | Out-Null
    $defaultLoaded = ($LASTEXITCODE -eq 0)
}

try {
    Write-Title 'Кодеки Microsoft: скачивание с сервера клуба'
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    $zip = Join-Path $Work $Codecs.File
    & "$env:SystemRoot\System32\curl.exe" -f --retry 3 -sS -o $zip "$BaseUrl/$($Codecs.File)"
    if ($LASTEXITCODE -ne 0) { throw "не скачан $($Codecs.File) (curl, код $LASTEXITCODE)" }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $Codecs.Sha256) { throw "SHA256 не совпадает: $($Codecs.File)" }
    & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $Work
    if ($LASTEXITCODE -ne 0) { throw "не распакован $($Codecs.File) (tar, код $LASTEXITCODE)" }
    Write-Pass "$($Codecs.File): SHA256 совпадает"

    Write-Title 'Кодеки Microsoft: установка в образ (для всех пользователей)'
    foreach ($appx in @(Get-ChildItem -LiteralPath $Work -Filter '*.appx')) {
        $name = ($appx.Name -split '_')[0]
        try {
            Add-AppxProvisionedPackage -Online -PackagePath $appx.FullName -SkipLicense -ErrorAction Stop | Out-Null
            try { Add-AppxPackage -Path $appx.FullName -ErrorAction Stop } catch { Write-Line "$name — текущему пользователю: $($_.Exception.Message)" }
            Write-Pass $name
        } catch { Write-Warn "$name не установлен: $($_.Exception.Message)" }
    }
    $prov = @(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -match 'VP9VideoExtensions|WebMediaExtensions')
    if ($prov.Count -ge 2) { Write-Pass 'VP9 и Web Media в образе — WebM-аватарки в CS2/Dota 2 будут показываться' }
    else { Write-Warn 'VP9 или Web Media нет в образе' }

    Write-Title 'Образы NGEN (.NET Framework): достройка (несколько минут, процессор загружен)'
    $before = Measure-PsStart
    foreach ($ngen in "$env:windir\Microsoft.NET\Framework64\v4.0.30319\ngen.exe", "$env:windir\Microsoft.NET\Framework\v4.0.30319\ngen.exe",
                      "$env:windir\Microsoft.NET\Framework64\v2.0.50727\ngen.exe", "$env:windir\Microsoft.NET\Framework\v2.0.50727\ngen.exe") {
        if (-not (Test-Path -LiteralPath $ngen)) { continue }
        & $ngen executeQueuedItems /nologo /silent | Out-Null
        Write-Line ("{0}: код {1}" -f ($ngen -replace '^.*\\Microsoft\.NET\\', ''), $LASTEXITCODE)
    }
    $after = Measure-PsStart
    $text = 'запуск PowerShell: было {0:N1} с, стало {1:N1} с' -f $before, $after
    if ($after -le 1.5) { Write-Pass $text } else { Write-Warn "$text — должно быть меньше 1,5 с" }

    Write-Title 'Автозагрузка: лаунчеры и обновлятор Edge не стартуют при входе'
    $runKeys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
                 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run')
    if ($defaultLoaded) { $runKeys += 'Registry::HKEY_USERS\ClubDefault\Software\Microsoft\Windows\CurrentVersion\Run' }
    foreach ($key in $runKeys) {
        $props = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if (-not $props) { continue }
        foreach ($p in $props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }) {
            if ($p.Name -match $AutostartPattern -or "$($p.Value)" -match $AutostartPattern) {
                Remove-ItemProperty -LiteralPath $key -Name $p.Name -ErrorAction SilentlyContinue
                Write-Pass ("убрано из автозагрузки: {0} ({1})" -f $p.Name, ($key -replace '^.*::', ''))
            }
        }
    }
    foreach ($folder in "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Startup", [Environment]::GetFolderPath('Startup')) {
        foreach ($lnk in @(Get-ChildItem -LiteralPath $folder -ErrorAction SilentlyContinue | Where-Object Name -match $AutostartPattern)) {
            Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction SilentlyContinue
            Write-Pass "убран ярлык автозагрузки: $($lnk.Name)"
        }
    }
    foreach ($svc in @(Get-Service -Name 'edgeupdate', 'edgeupdatem', 'MicrosoftEdgeElevationService' -ErrorAction SilentlyContinue)) {
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        Set-Service -Name $svc.Name -StartupType Disabled -ErrorAction SilentlyContinue
        if ((Get-Service -Name $svc.Name).StartType -eq 'Disabled') { Write-Pass "служба $($svc.Name) отключена" } else { Write-Warn "служба $($svc.Name) не отключена" }
    }
    foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'MicrosoftEdgeUpdate*' })) {
        $t | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
        Write-Pass "задание $($t.TaskName) отключено"
    }

    Write-Title 'Яндекс.Музыка: удаление'
    $yandex = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object Name -match 'Yandex')
    foreach ($p in $yandex) {
        try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop; Write-Pass "удалено: $($p.Name)" }
        catch { Write-Warn "не удалено: $($p.Name) ($($_.Exception.Message))" }
    }
    foreach ($prov in @(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -match 'Yandex')) {
        Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction SilentlyContinue | Out-Null
        Write-Pass "из образа: $($prov.DisplayName)"
    }
    if (-not $yandex) { Write-Line 'Яндекс.Музыки нет' }
    $win32 = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.Publisher -match 'Yandex|Яндекс' })
    foreach ($w in $win32) { Write-Warn "программа Яндекса: $($w.DisplayName) — удалите через «Приложения → Установленные приложения»" }
} catch {
    Write-Warn $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    if ($defaultLoaded) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload 'HKU\ClubDefault' | Out-Null
    }
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений. Перезагрузите ПК.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше). Перезагрузите ПК.' -f $script:WarnCount) -ForegroundColor Yellow }
