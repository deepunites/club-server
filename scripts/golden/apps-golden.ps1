#Requires -Version 5.1
<#
.SYNOPSIS
    Браузер и лаунчеры для клуба: Chrome Enterprise и клиент Epic — в систему (C:), Steam — целиком в библиотеку (G:\Steam),
    установщик Battle.net — запуск для ручной установки.

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора, когда в панели открыта правка библиотеки и
    помощник подключил её как G: (тот же диск и та же буква, что у мест). Установщики — с официальных сайтов
    (подписи проверены на сервере); скрипт скачивает их с сервера клуба и сверяет SHA256.

    Chrome: обновления выключены службами и заданиями Google Update (политики Google Update действуют только на ПК в домене
    или под MDM) — на местах (сброс при каждой загрузке) Chrome качал бы обновление на каждом ПК при каждом включении.
    Обновлять Chrome — в сеансе мастера: этот скрипт с новым MSI.
    Epic и Battle.net: клиент в C:\Program Files (x86), игры — в G:\Epic Games и G:\Battle.net (папку выбрать в лаунчере).
    Пути игр оба хранят в C:\ProgramData — поэтому после установки игр публикуются ОБЕ версии: эталон и библиотека.
    Steam: весь клиент и игры в G:\Steam — его пути не зависят от системного диска. SteamSetup ставит только загрузчик:
    Steam надо запустить один раз здесь, чтобы клиент докачался в библиотеку, а не на каждом месте при каждой загрузке.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File $env:TEMP\apps-golden.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools/apps',
    [string]$Work = "$env:SystemDrive\ClubApps"
)

$ErrorActionPreference = 'Continue'

$Files = @(
    @{ File = 'googlechromestandaloneenterprise64.msi'; Sha256 = '9b7615c72f17f5ef7fa5cf3c7dbf872cebe9e8ebeae7786bb96b490214efb31c' },
    @{ File = 'EpicInstaller-20.3.3.msi'; Sha256 = 'd55d79710edfeaa107a62f2ea007bec1eeeac8b715a43c2ba3858eec7bc60337' },
    @{ File = 'SteamSetup.exe'; Sha256 = '7d3654531c32d941b8cae81c4137fc542172bfa9635f169cb392f245a0a12bcb' },
    @{ File = 'Battle.net-Setup.exe'; Sha256 = 'de5d32d4ea5eed5a9e120027fb68b370976dbfecc8f2a8f91305977f0b87fcaf' }
)

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

function Set-RegValue([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord') {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force -ErrorAction Stop | Out-Null
    } catch { Write-Warn ('{0} : {1} — {2}' -f $Path, $Name, $_.Exception.Message) }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

try {
    # G: должен быть именно томом правки (таргет …:club-master, на запись): метка Online Games (раньше ClubGames) есть и у личного диска игр
    # места — поставить туда значит потерять всё при следующей загрузке.
    $library = Get-Volume -DriveLetter G -ErrorAction SilentlyContinue
    $session = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':club-master$' -and $_.IsConnected })
    $gDisk = Get-Partition -DriveLetter G -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue
    if (-not $library -or $library.FileSystemLabel -notin @('Online Games', 'ClubGames') -or $session.Count -ne 1 -or -not $gDisk -or
        ($session[0] | Get-Disk).Number -ne $gDisk.Number -or $gDisk.IsReadOnly) {
        throw 'G: — не том правки библиотеки (club-master, на запись): откройте в панели правку библиотеки для этого ПК и подождите 30 с.'
    }
    Write-Pass ("библиотека G: — {0}, свободно {1:N0} ГБ" -f $library.FileSystemLabel, ($library.SizeRemaining / 1GB))

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

    Write-Title 'Google Chrome (в систему), обновления — только в сеансе мастера'
    $p = Start-Process msiexec.exe -ArgumentList "/i `"$(Join-Path $Work 'googlechromestandaloneenterprise64.msi')`" /qn /norestart" -Wait -PassThru
    if ($p.ExitCode -in 0, 3010, 1638) { Write-Pass "Chrome установлен (код $($p.ExitCode))" } else { Write-Warn "Chrome: msiexec код $($p.ExitCode)" }
    $gu = 'HKLM:\SOFTWARE\Policies\Google\Update'
    Set-RegValue $gu 'UpdateDefault' 0
    Set-RegValue $gu 'AutoUpdateCheckPeriodMinutes' 0
    Set-RegValue $gu 'Update{8A69D345-D564-463C-AFF1-A69D9E530F96}' 0
    $cp = 'HKLM:\SOFTWARE\Policies\Google\Chrome'
    Set-RegValue $cp 'BackgroundModeEnabled' 0
    Set-RegValue $cp 'PromotionsEnabled' 0
    Set-RegValue $cp 'DefaultBrowserSettingEnabled' 0
    Set-RegValue $cp 'BrowserSignin' 0
    Set-RegValue $cp 'SyncDisabled' 1
    $services = @(Get-Service -Name 'gupdate*', 'GoogleUpdater*' -ErrorAction SilentlyContinue)
    foreach ($svc in $services) {
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        Set-Service -Name $svc.Name -StartupType Disabled -ErrorAction SilentlyContinue
        if ((Get-Service -Name $svc.Name).StartType -eq 'Disabled') { Write-Pass "служба обновления $($svc.Name) отключена" } else { Write-Warn "служба $($svc.Name) не отключена" }
    }
    if (-not $services) { Write-Warn 'служб Google Update не найдено: Chrome будет обновляться на местах (имена служб сменились?)' }
    $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'GoogleUpdate*' })
    foreach ($t in $tasks) {
        $t | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
        if ((Get-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName).State -eq 'Disabled') { Write-Pass "задание $($t.TaskName) отключено" } else { Write-Warn "задание $($t.TaskName) не отключено" }
    }
    if (-not $tasks) { Write-Warn 'заданий Google Update не найдено: Chrome будет обновляться на местах' }

    Write-Title 'Epic Games Launcher (клиент в систему; игры — в G:\Epic Games)'
    $p = Start-Process msiexec.exe -ArgumentList "/i `"$(Join-Path $Work 'EpicInstaller-20.3.3.msi')`" /qn /norestart" -Wait -PassThru
    if ($p.ExitCode -in 0, 3010, 1638) { Write-Pass "Epic установлен (код $($p.ExitCode))" } else { Write-Warn "Epic: msiexec код $($p.ExitCode)" }
    New-Item -ItemType Directory -Force -Path 'G:\Epic Games' | Out-Null

    Write-Title 'Steam (целиком в G:\Steam)'
    $p = Start-Process (Join-Path $Work 'SteamSetup.exe') -ArgumentList '/S', '/D=G:\Steam' -Wait -PassThru
    if (Test-Path -LiteralPath 'G:\Steam\steam.exe') {
        Write-Pass "загрузчик Steam в G:\Steam (код $($p.ExitCode))"
        Write-Line 'Запустите Steam (ярлык на рабочем столе) и дождитесь окна входа: он докачает клиент в G:\Steam.'
    } else { Write-Warn "Steam: нет G:\Steam\steam.exe (код $($p.ExitCode))" }

    Write-Title 'Battle.net — установщик откроется окном'
    Write-Line 'Папка клиента — по умолчанию (C:\Program Files (x86)\Battle.net); игры потом ставить в G:\Battle.net.'
    # На рабочий стол администратора (не общий: его увидели бы все игроки); установщик идёт из копии — $Work удаляется.
    $setup = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Battle.net-Setup.exe'
    Copy-Item -LiteralPath (Join-Path $Work 'Battle.net-Setup.exe') -Destination $setup -Force -ErrorAction Stop
    Start-Process $setup -ArgumentList '--lang=ruRU' -ErrorAction Stop
    Write-Pass 'установщик Battle.net запущен (копия — на вашем рабочем столе, удалить после установки)'
} catch {
    Write-Warn $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше).' -f $script:WarnCount) -ForegroundColor Yellow }
