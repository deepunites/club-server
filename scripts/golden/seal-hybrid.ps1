#Requires -Version 5.1
<#
.SYNOPSIS
    Последний шаг на эталоне гибрида (PC-11) перед снятием образа: убирает регистрацию помощника бездиска, ищет
    блокеры sysprep и запускает sysprep /generalize /oobe /shutdown с unattend-hybrid.xml.

.DESCRIPTION
    Запускать на PC-11 (Windows на локальном SSD), PowerShell от администратора, когда всё остальное готово
    (prepare-hybrid.ps1 без WARN после перезагрузки, лаунчеры и игры проверены). unattend-hybrid.xml — рядом со скриптом.
    Sysprep запускается, только если нет ни одного WARN; иначе ПК не выключается — разберите WARN и запустите снова
    (повторный запуск безопасен).

    Что делает:
      - отказ, если системный диск по iSCSI (бездисковый ПК);
      - помощник: служба остановлена, удалены токены этого ПК и назначение личного диска (credentials.bin,
        assignment.json и их .tmp) — как seal-golden.ps1: иначе они достались бы всем гибридам;
      - образы NGEN (.NET Framework) — достройка;
      - приложения Store (Appx): пакет установлен пользователю, но не в образе (provisioned) или в другой версии —
        sysprep /generalize с ним падает («was installed for a user, but not provisioned for all users»). Копия
        удаляется у текущего пользователя и, по SID, у других (и удалённых учётных записей) — WARN: запустить снова;
      - BitLocker выключен, перезагрузка не ожидается, учётная запись — локальная (OOBE скрыта: после заливки
        остаются только учётные записи эталона);
      - sysprep; при сбое — где журналы setupact.log и setuperr.log и их последние строки.

    После выключения эту Windows больше НЕ загружать: следующая загрузка — сразу флешка WinPE и снятие образа (capture-hybrid.cmd, docs/imaging.md).

.PARAMETER NoSysprep
    Всё, кроме sysprep: проверки и очистка (помощник останется остановленным до перезагрузки).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\seal-hybrid.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoSysprep
)

$ErrorActionPreference = 'Continue'
$ServiceName = 'ClubDisklessHelper'
$StateDir = "$env:ProgramData\ClubDiskless"
$StateFiles = 'credentials.bin', 'credentials.bin.tmp', 'assignment.json', 'assignment.json.tmp'
$UnattendNs = 'urn:schemas-microsoft-com:unattend'
$SysprepDir = Join-Path $env:SystemRoot 'System32\Sysprep'
$SysprepExe = Join-Path $SysprepDir 'sysprep.exe'
$SuccessTag = Join-Path $SysprepDir 'Sysprep_succeeded.tag'
# $PSScriptRoot пуст в значениях параметров при запуске powershell.exe -File (PS 5.1) — папка скрипта здесь.
$UnattendSource = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'unattend-hybrid.xml'
# Копия без пробелов в пути и без зависимости от флешки; sysprep всё равно кладёт свою в %WINDIR%\Panther\unattend.xml.
$UnattendCopy = Join-Path $SysprepDir 'unattend-hybrid.xml'
# Лаунчеры и игры: по папке программы (Steam, Epic, Battle.net, Ubisoft, Rockstar, Lesta, Discord, Riot) или с диска игр G:.
$LauncherPathPattern = '(?i)\\(Steam|Epic Games|Battle\.net|Ubisoft|Rockstar Games|Lesta|Discord|Riot Games)\\|^G:\\'

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "INFO  $Text" }
function Write-Warn([string]$Text, [string]$Hint) {
    $script:WarnCount++
    Write-Host "WARN  $Text" -ForegroundColor Yellow
    if ($Hint) { Write-Host "      -> $Hint" -ForegroundColor Yellow }
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

function Show-LogTail([string]$Path, [int]$Lines, [string]$Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { Write-Line "нет файла $Path"; return }
    Write-Host ''
    Write-Host ('  {0} — последние строки{1}:' -f $Path, $(if ($Pattern) { ' с ошибками' } else { '' })) -ForegroundColor Yellow
    $content = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)
    if ($Pattern) { $content = @($content | Where-Object { $_ -match $Pattern }) }
    foreach ($line in ($content | Select-Object -Last $Lines)) { Write-Line $line }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}
# 32-разрядный PowerShell видит вместо System32 папку SysWOW64 — там нет sysprep.
if (-not [Environment]::Is64BitProcess) { throw 'Запустите 64-разрядный PowerShell (C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe).' }

Write-Title 'Системный диск — локальный?'
$disk = $null
try { $disk = Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch { }
if (-not $disk) { throw ('Системный диск {0} не прочитался — sysprep не запускаю, ничего не изменено.' -f $env:SystemDrive) }
if ("$($disk.BusType)" -eq 'iSCSI' -or "$($disk.BusType)" -eq '9') {
    throw ('Системный диск {0} подключён по iSCSI: это бездисковый ПК (эталон или место), а не эталон гибрида. Остановлено, ничего не изменено (для эталона бездиска — seal-golden.ps1).' -f $env:SystemDrive)
}
# Сессия таргета мастера или места (DisklessOptions) — тоже бездисковый ПК; у гибрида сессии только к дискам игр.
$bootSessions = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match ':(diskless-master|seat-\d+)$' })
if ($bootSessions) {
    throw ('Есть сессия iSCSI таргета бездиска ({0}): это бездисковый ПК, а не эталон гибрида. Остановлено, ничего не изменено.' -f (($bootSessions | ForEach-Object { "$($_.TargetNodeAddress)" }) -join ', '))
}
Write-Pass ('{0}: {1}, шина {2}' -f $env:SystemDrive, $disk.FriendlyName, $disk.BusType)

Write-Title 'Лаунчеры'
# Вход в лаунчеры под аккаунтами клуба остался бы в образе — игроки на всех гибридах получили бы эти аккаунты.
Write-Line 'Перед sysprep: выйдите из аккаунтов клуба в Steam, Epic, Battle.net, Ubisoft, Rockstar, Lesta (и Riot, если ставили),'
Write-Line 'закройте их и игры.'
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -match $LauncherPathPattern })
foreach ($p in ($running | Sort-Object Path -Unique)) { Write-Warn ('запущен {0} ({1})' -f $p.ProcessName, $p.Path) 'выйти из аккаунта клуба и закрыть (и в трее; остальное — «Диспетчер задач» -> «Снять задачу»)' }
if (-not $running) { Write-Pass 'лаунчеры и игры не запущены' }
if (@(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':club-master$' -and $_.IsConnected }).Count -gt 0) {
    Write-Warn 'открыта правка библиотеки (таргет club-master)' 'в панели «Закончить правку» библиотеки, потом снова этот скрипт'
}

Write-Title 'Файл ответов unattend-hybrid.xml'
if (-not (Test-Path -LiteralPath $UnattendSource)) {
    Write-Warn "нет $UnattendSource" 'положить unattend-hybrid.xml (scripts/golden) рядом с этим скриптом'
} else {
    try {
        [xml]$xml = Get-Content -LiteralPath $UnattendSource -Raw -Encoding UTF8 -ErrorAction Stop
        $nsm = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
        $nsm.AddNamespace('u', $UnattendNs)
        if ($xml.DocumentElement.LocalName -ne 'unattend' -or $xml.DocumentElement.NamespaceURI -ne $UnattendNs) {
            Write-Warn 'unattend-hybrid.xml — не файл ответов Windows (корень не unattend)'
        } elseif ($xml.SelectNodes('//u:ComputerName', $nsm).Count -gt 0) {
            Write-Warn 'в unattend-hybrid.xml есть ComputerName' 'убрать: имя ПК вписывает сервер при заливке'
        } elseif (-not $xml.SelectSingleNode("/u:unattend/u:settings[@pass='oobeSystem']", $nsm)) {
            Write-Warn 'в unattend-hybrid.xml нет прохода oobeSystem: OOBE не будет пропущена'
        } else {
            Write-Pass "unattend-hybrid.xml разобран: $UnattendSource"
        }
    } catch {
        Write-Warn ('unattend-hybrid.xml не разобран: {0}' -f $_.Exception.Message)
    }
}

Write-Title 'Помощник: остановка и удаление регистрации этого ПК'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Write-Warn 'службы ClubDisklessHelper нет — помощник не установлен' 'install-helper.ps1' }
else {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'ClubDisklessHelper' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    }
    if ((Get-Service -Name $ServiceName).Status -eq 'Stopped') { Write-Pass 'служба остановлена' } else { Write-Warn 'служба не остановилась' }
    if ((Get-Service -Name $ServiceName).StartType -ne 'Automatic') { Write-Warn 'запуск службы не «Автоматически»: на гибридах помощник не запустится (нет G:)' }
}
foreach ($f in $StateFiles) {
    $p = Join-Path $StateDir $f
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) { Write-Warn "не удалён $p" } else { Write-Pass "удалён $f" }
    }
}
foreach ($f in 'helper.json', 'club-ca.pem') {
    if (Test-Path -LiteralPath (Join-Path $StateDir $f)) { Write-Pass "на месте $f" } else { Write-Warn "нет $StateDir\$f — гибриды не зарегистрируются" }
}

# Образы NGEN: после обновлений и новых программ .NET Framework их надо достроить, иначе каждый запуск PowerShell
# компилирует всё заново (помощник бездиска грузил ЦП до 100%, 2026-10-20).
Write-Title 'Образы NGEN (.NET Framework): достройка'
foreach ($ngen in "$env:windir\Microsoft.NET\Framework64\v4.0.30319\ngen.exe", "$env:windir\Microsoft.NET\Framework\v4.0.30319\ngen.exe",
                  "$env:windir\Microsoft.NET\Framework64\v2.0.50727\ngen.exe", "$env:windir\Microsoft.NET\Framework\v2.0.50727\ngen.exe") {
    if (-not (Test-Path -LiteralPath $ngen)) { continue }
    & $ngen executeQueuedItems /nologo /silent | Out-Null
    Write-Line ("{0}: код {1}" -f ($ngen -replace '^.*\\Microsoft\.NET\\', ''), $LASTEXITCODE)
}
$psStart = @(1..3 | ForEach-Object { (Measure-Command { & powershell.exe -NoProfile -NonInteractive -Command 'exit' }).TotalSeconds } | Measure-Object -Minimum).Minimum
if ($psStart -le 1.5) { Write-Pass ('запуск PowerShell {0:N1} с' -f $psStart) } else { Write-Warn ('запуск PowerShell {0:N1} с — NGEN не достроен? (должно быть меньше 1,5 с)' -f $psStart) }

# Блокер sysprep /generalize: пакет установлен пользователю, а в образе (provisioned) его нет или он другой версии —
# так бывает с обновлёнными Store приложениями и с кодеками fixes-v2.ps1 (в образ и отдельно текущему пользователю).
# Системные (SignatureKind System), неудаляемые, платформы (framework) и ресурсные пакеты sysprep не проверяет.
Write-Title 'Приложения Store (Appx): пакеты пользователей, которых нет в образе'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
try {
    $provisioned = @{}
    foreach ($p in @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)) { $provisioned[$p.DisplayName] = "$($p.Version)" }
    $found = 0
    foreach ($pkg in @(Get-AppxPackage -AllUsers -ErrorAction Stop)) {
        if ($pkg.IsFramework -or $pkg.IsResourcePackage -or $pkg.NonRemovable -or "$($pkg.SignatureKind)" -eq 'System') { continue }
        $users = @($pkg.PackageUserInformation | Where-Object { "$($_.InstallState)" -eq 'Installed' })
        if (-not $users) { continue }
        $inImage = $provisioned[$pkg.Name]
        if ($inImage -and $inImage -eq "$($pkg.Version)") { continue }
        $found++
        $why = if ($inImage) { 'в образе версия {0}' -f $inImage } else { 'в образе его нет' }
        foreach ($u in $users) {
            $sid = "$($u.UserSecurityId.Sid)"
            $who = if ("$($u.UserSecurityId.Username)") { "$($u.UserSecurityId.Username)" } else { $sid }
            if ($sid -eq $me) {
                try {
                    Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
                    $back = if ($inImage) { 'копия из образа придёт пользователю при следующем входе' } else { 'если пакет нужен всем — Add-AppxProvisionedPackage, потом этот скрипт' }
                    Write-Warn ('{0} {1} ({2}): удалён у текущего пользователя {3}' -f $pkg.Name, $pkg.Version, $why, $who) "$back; запустить скрипт снова"
                } catch {
                    Write-Warn ('{0} {1} ({2}): у текущего пользователя не удалён: {3}' -f $pkg.Name, $pkg.Version, $why, $_.Exception.Message) "Remove-AppxPackage -Package $($pkg.PackageFullName)"
                }
            } else {
                # Другая учётная запись, удалённая (SID без имени) или defaultuser0 после OOBE: под удалённой не войти, а удаление
                # учётной записи её пакеты не убирает. От администратора Remove-AppxPackage -User <SID> удаляет копию по SID.
                try {
                    Remove-AppxPackage -Package $pkg.PackageFullName -User $sid -ErrorAction Stop
                    Write-Warn ('{0} {1} ({2}): удалён у пользователя {3}' -f $pkg.Name, $pkg.Version, $why, $who) 'запустить скрипт снова'
                } catch {
                    $hint = "Remove-AppxPackage -Package $($pkg.PackageFullName) -User $sid"
                    if (-not $inImage) { $hint += "; не помогло — Remove-AppxPackage -Package $($pkg.PackageFullName) -AllUsers (пакета нет в образе)" }
                    Write-Warn ('{0} {1} ({2}): у пользователя {3} не удалён: {4}' -f $pkg.Name, $pkg.Version, $why, $who, $_.Exception.Message) $hint
                }
            }
        }
    }
    if ($found -eq 0) { Write-Pass 'у пользователей нет пакетов Appx, которых нет в образе' }
} catch {
    Write-Warn ('пакеты Appx не прочитались: {0}' -f $_.Exception.Message)
}

Write-Title 'BitLocker и незавершённое обслуживание'
$volume = $null
try { $volume = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop } catch { }
if ($volume) {
    $text = 'BitLocker {0}: {1}, защита {2}' -f $volume.MountPoint, $volume.VolumeStatus, $volume.ProtectionStatus
    if ("$($volume.VolumeStatus)" -eq 'FullyDecrypted') { Write-Pass $text }
    else { Write-Warn $text ('sysprep с BitLocker падает: manage-bde -off {0} и дождаться «Fully Decrypted»' -f $env:SystemDrive) }
} else {
    $status = ((& manage-bde.exe -status $env:SystemDrive 2>&1) | ForEach-Object { "$_" }) -join "`n"
    if ($status -match 'Fully Decrypted|Полностью расшифрован') { Write-Pass ('BitLocker {0}: полностью расшифрован (manage-bde)' -f $env:SystemDrive) }
    else { Write-Warn ('BitLocker {0}: состояние не «полностью расшифрован»' -f $env:SystemDrive) ('manage-bde -status {0}; если шифруется — manage-bde -off {0}' -f $env:SystemDrive) }
}
$pending = @()
if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $pending += 'обслуживание компонентов (CBS)' }
if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $pending += 'Windows Update' }
if ($pending) { Write-Warn ('ожидается перезагрузка: {0}' -f ($pending -join ', ')) 'перезагрузить ПК и запустить скрипт снова (помощник после перезагрузки зарегистрируется — скрипт снова удалит регистрацию)' }
else { Write-Pass 'перезагрузка не ожидается' }
if (Get-Process -Name 'sysprep' -ErrorAction SilentlyContinue) { Write-Warn 'sysprep уже запущен' 'закрыть окно «Программа подготовки системы»' }

# OOBE в unattend-hybrid.xml скрыта целиком и учётных записей не создаёт: после заливки остаются учётные записи эталона.
Write-Title 'Учётные записи'
$current = $null
try { $current = Get-LocalUser -SID $me -ErrorAction Stop } catch { }
if (-not $current) { Write-Warn 'вы вошли не локальной учётной записью' 'войти локальным администратором эталона' }
elseif ($me -match '-500$') { Write-Warn ('вы вошли встроенной учётной записью {0}' -f $current.Name) 'создать отдельного локального администратора, войти им и запустить скрипт снова' }
elseif ("$($current.PrincipalSource)" -ne 'Local') { Write-Warn ('{0} связана с учётной записью Microsoft' -f $current.Name) 'её вход и токены достались бы всем гибридам: войти локальным администратором и отвязать или удалить её' }
else { Write-Pass ('локальный администратор {0} — останется после заливки' -f $current.Name) }
foreach ($u in @(Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -and $_.SID.Value -ne $me })) {
    if ("$($u.PrincipalSource)" -eq 'MicrosoftAccount') { Write-Warn ('{0} связана с учётной записью Microsoft' -f $u.Name) 'отвязать или удалить эту учётную запись' }
    else { Write-Info ('включена учётная запись {0}' -f $u.Name) }
}
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
if ("$(Get-RegValue $winlogon 'AutoAdminLogon')" -eq '1') {
    Write-Info ('автовход сейчас включён ({0}); unattend-hybrid.xml автовход не задаёт — после заливки проверить, остался ли' -f (Get-RegValue $winlogon 'DefaultUserName'))
} else {
    Write-Info 'автовход выключен (unattend-hybrid.xml его тоже не задаёт)'
}

Write-Host ''
if ($script:WarnCount -ne 0) {
    Write-Host ('Предупреждений: {0} (строки WARN выше). Sysprep не запускаю — разберите их и запустите скрипт снова.' -f $script:WarnCount) -ForegroundColor Yellow
    Write-Host 'Помощник остановлен до перезагрузки (Start-Service ClubDisklessHelper — вернуть G: сейчас).' -ForegroundColor Yellow
    return
}
if ($NoSysprep) { Write-Host 'Проверки пройдены. Sysprep не запускаю (-NoSysprep).' -ForegroundColor Green; return }

Write-Host 'Проверки пройдены. Дальше: sysprep /generalize /oobe /shutdown — ПК выключится сам.' -ForegroundColor Green
Write-Host 'ПОСЛЕ ВЫКЛЮЧЕНИЯ ЭТУ WINDOWS НЕ ЗАГРУЖАТЬ: при включении сразу F12 -> флешка WinPE (UEFI) -> capture-hybrid.cmd.' -ForegroundColor Red
Write-Host 'Иначе Windows пройдёт specialize и OOBE, помощник снова зарегистрируется, и эталон придётся готовить заново.' -ForegroundColor Red
[void](Read-Host 'Enter — запустить sysprep (закрыть окно — отмена)')

Copy-Item -LiteralPath $UnattendSource -Destination $UnattendCopy -Force -ErrorAction Stop
if (Test-Path -LiteralPath $SuccessTag) {
    Write-Line 'эта Windows уже проходила sysprep (Sysprep_succeeded.tag) — повторный /generalize допустим'
    Remove-Item -LiteralPath $SuccessTag -Force -ErrorAction SilentlyContinue
}
$sysprepArgs = '/generalize /oobe /shutdown /quiet /unattend:{0}' -f $UnattendCopy
Write-Line "> $SysprepExe $sysprepArgs"
$p = Start-Process -FilePath $SysprepExe -ArgumentList $sysprepArgs -PassThru
$null = $p.Handle
$p.WaitForExit()
if (Test-Path -LiteralPath $SuccessTag) {
    Write-Host 'Sysprep прошёл, ПК выключается. Не загружать эту Windows: сразу флешка WinPE -> capture-hybrid.cmd.' -ForegroundColor Green
    return
}

Write-Host ''
Write-Host ('Sysprep НЕ прошёл (код {0}). ПК не выключается.' -f $p.ExitCode) -ForegroundColor Red
$logDir = Join-Path $SysprepDir 'Panther'
Write-Host ('Журналы: {0}\setupact.log и {0}\setuperr.log (ход и ошибки generalize); {1}\Panther — журналы Setup.' -f $logDir, $env:SystemRoot) -ForegroundColor Yellow
Show-LogTail (Join-Path $logDir 'setuperr.log') 20
Show-LogTail (Join-Path $logDir 'setupact.log') 15 ', Error'
$appx = @(Get-Content -LiteralPath (Join-Path $logDir 'setupact.log') -ErrorAction SilentlyContinue | Where-Object { $_ -match 'installed for a user, but not provisioned for all users' })
if ($appx) {
    Write-Host ''
    Write-Host 'Причина — пакет Appx пользователя (строки выше): запустите этот скрипт снова, он уберёт копию текущего пользователя.' -ForegroundColor Yellow
}
Write-Host 'Исправьте причину и запустите этот скрипт снова (после перезагрузки помощник зарегистрируется — скрипт снова уберёт регистрацию).' -ForegroundColor Yellow
