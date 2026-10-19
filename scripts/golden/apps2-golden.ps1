#Requires -Version 5.1
<#
.SYNOPSIS
    Вторая партия лаунчеров (для всех пользователей): Ubisoft Connect, Rockstar Games Launcher, Lesta Game Center;
    часовой пояс эталона — Ташкент (UTC+5).

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора, при открытой правке библиотеки (том правки —
    G:, как у мест). Клиенты ставятся в систему (C:), игры — в библиотеку:
      Ubisoft Connect — тихо; в его настройках «Загрузки» указать папку игр G:\Ubisoft Games;
      Rockstar и Lesta — откроются окна установщиков; игры ставить в G:\Rockstar Games и G:\Lesta.
    Пути к играм все трое хранят на C: (реестр / ProgramData) — после установки игр публикуются ОБЕ версии.
    VK Play, Discord, HoYoPlay, Roblox ставятся в профиль пользователя — их ставить под учётной записью игроков
    (после установки шелла), не под администратором.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools/apps',
    [string]$Work = "$env:SystemDrive\ClubApps2"
)

$ErrorActionPreference = 'Continue'

$Files = @(
    @{ File = 'UbisoftConnectInstaller.exe'; Sha256 = '3cec13adb297daf861528ca59c94467d9aee4b43b0089ebc211cf5de16618503' },
    @{ File = 'Rockstar-Games-Launcher.exe'; Sha256 = '321886457b7e4ab727a27671c17a815684a3943579eedf7e2248a3b9f0973746' },
    @{ File = 'Lesta_Game_Center_Install_RU.exe'; Sha256 = '4a94d6e484d08916edb772df00155902c41f99892dd727304d4078d9dbe9bf7d' }
)

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

try {
    # G: — именно том правки библиотеки (club-master, на запись), а не личный диск игр места.
    $library = Get-Volume -DriveLetter G -ErrorAction SilentlyContinue
    $session = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':club-master$' -and $_.IsConnected })
    $gDisk = Get-Partition -DriveLetter G -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue
    if (-not $library -or $library.FileSystemLabel -ne 'ClubGames' -or $session.Count -ne 1 -or -not $gDisk -or
        ($session[0] | Get-Disk).Number -ne $gDisk.Number -or $gDisk.IsReadOnly) {
        throw 'G: — не том правки библиотеки (club-master, на запись): откройте в панели правку библиотеки для этого ПК и подождите 30 с.'
    }
    Write-Pass ("библиотека G: — правка, свободно {0:N0} ГБ" -f ($library.SizeRemaining / 1GB))

    Write-Title 'Часовой пояс: Ташкент (UTC+5)'
    & tzutil.exe /s 'West Asia Standard Time'
    if ((& tzutil.exe /g) -eq 'West Asia Standard Time') { Write-Pass 'West Asia Standard Time' } else { Write-Warn 'часовой пояс не сменился' }

    Write-Title 'Скачивание установщиков с сервера клуба'
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    foreach ($f in $Files) {
        $path = Join-Path $Work $f.File
        & "$env:SystemRoot\System32\curl.exe" -f --retry 3 -sS -o $path "$BaseUrl/$($f.File)"
        if ($LASTEXITCODE -ne 0) { throw "не скачан $($f.File) (curl, код $LASTEXITCODE)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $f.Sha256) { throw "SHA256 не совпадает: $($f.File)" }
        Write-Pass "$($f.File): SHA256 совпадает"
    }
    foreach ($d in 'G:\Ubisoft Games', 'G:\Rockstar Games', 'G:\Lesta') { New-Item -ItemType Directory -Force -Path $d | Out-Null }

    Write-Title 'Ubisoft Connect (тихо, клиент в систему)'
    # Ждём только сам установщик: Start-Process -Wait ждал бы и запущенный им клиент (в трее) — скрипт бы висел.
    $p = Start-Process (Join-Path $Work 'UbisoftConnectInstaller.exe') -ArgumentList '/S' -PassThru
    $null = $p.Handle
    if (-not $p.WaitForExit(900000)) { Write-Warn 'Ubisoft Connect: установщик не завершился за 15 мин' }
    $ubi = Get-ChildItem "${env:ProgramFiles(x86)}\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe" -ErrorAction SilentlyContinue
    if ($ubi) { Write-Pass "Ubisoft Connect установлен (код $($p.ExitCode)); папка игр в его настройках — G:\Ubisoft Games" }
    else { Write-Warn "Ubisoft Connect: нет UbisoftConnect.exe (код $($p.ExitCode))" }

    # Окна установщиков: копии — на рабочий стол администратора (не общий), $Work удаляется.
    $desktop = [Environment]::GetFolderPath('Desktop')
    # По очереди: следующее окно — после закрытия предыдущего установщика.
    foreach ($name in 'Rockstar-Games-Launcher.exe', 'Lesta_Game_Center_Install_RU.exe') {
        Write-Title "$name — окно установщика (скрипт ждёт, пока вы его закончите)"
        $copy = Join-Path $desktop $name
        Copy-Item -LiteralPath (Join-Path $Work $name) -Destination $copy -Force -ErrorAction Stop
        $p = Start-Process $copy -PassThru -ErrorAction Stop
        $null = $p.Handle
        $p.WaitForExit()
        Write-Pass "установщик закрыт (код $($p.ExitCode)); копию на вашем рабочем столе удалите"
    }
    Write-Line 'Rockstar: клиент — по умолчанию; GTA V — в G:\Rockstar Games. Lesta: игры — в G:\Lesta.'
} catch {
    Write-Warn $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше).' -f $script:WarnCount) -ForegroundColor Yellow }
