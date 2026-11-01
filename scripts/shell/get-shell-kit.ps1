<#
.SYNOPSIS
    Набор шелла ClubShell по сети клуба, одной командой на гибриде: номер места и имя PC-NN, Smart App Control,
    брандмауэр LAN-игр и ClubMaxRefresh, набор в C:\ClubShellKit со сверкой SHA256, запуск установки shell-install.ps1.
.DESCRIPTION
    PowerShell от администратора на гибриде, под «Club» (Win+X -> «Терминал (администратор)»):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/get-shell-kit.ps1 | iex
    Вместо флешки: сразу после установки ClubShell накопители USB на ПК выключены (политика MSI), а флешка нужна ещё и
    ноутбуку. Сервер клуба (VM 192.168.1.201) отдаёт файлы по HTTP, только в локальной сети клуба; что лежит на сервере —
    кладёт scripts/shell/publish-kit.sh. Сначала проверяет права администратора, потом:
      1. номер места: показывает имя Windows и спрашивает номер, как в панели club-server (Enter — оставить PC-NN).
         Другой номер — переименование в PC-NN и сразу перезагрузка; после неё та же команда ещё раз. ClubShell берёт
         номер из имени Windows один раз, при первой регистрации. Переименование, которое ждёт перезагрузки, — сначала
         перезагрузка. ClubShell уже стоит — номер не спрашивается;
      2. Smart App Control включён или в режиме оценки — как его выключить (сборка 1.0.18 не подписана, он её
         заблокирует) и вопрос, выключен ли (по умолчанию нет: preflight всё равно дал бы WARN/FAIL). До шага 3:
         install-max-refresh.ps1 компилирует и сразу запускает неподписанный ClubMaxRefresh.exe — SAC его тоже не даст;
      3. брандмауэр для LAN-игр (install-lan-firewall.ps1) и задача ClubMaxRefresh (install-max-refresh.ps1) — только
         если их нет (те же проверки, что в shell-preflight.ps1); скрипты с сервера клуба, как irm | iex, но каждый в
         своей области (их $ErrorActionPreference не остаётся в консоли);
      4. набор в C:\ClubShellKit из tools/shell-kit/: список SHA256SUMS (формат sha256sum: «<hex>  <имя>»), папка —
         только SYSTEM и Administrators, как у лаунчера с флешки; каждый файл — в <имя>.part, сверка SHA256, потом на
         место. Файл с верным SHA256 не качается снова (установщик 72 МБ — один раз). Несовпадение — стоп. SHA256 —
         только от ошибок передачи и недокачки, не от подмены: список и файлы приходят по HTTP с того же сервера, что и
         сама команда irm | iex (та же практика, что у install-*.ps1 и update-helper-net.ps1);
      5. запускает C:\ClubShellKit\shell-install.ps1 в этом же окне: ключ ПК, пароль Club, PIN, ожидание одобрения,
         киоск, вопрос о перезагрузке. ClubShell уже стоит — вместо установки предлагает kiosk-on.ps1. После установки
         смотрит автовход: не в player (ПК не одобрен за время ожидания) — жёлтым, что киоск ещё не включён.
    Повторный запуск безопасен: имя не меняется без «да», брандмауэр и ClubMaxRefresh второй раз не ставятся, набор
    докачивается и обновляется по SHA256SUMS, вторую установку не даст shell-preflight (служба уже есть — FAIL).
    Выполняется в области консоли (irm | iex): без param и без exit (он закрыл бы окно) — только return и throw;
    настройки ошибок и прогресса возвращаются в finally. Строки вывода — только ASCII, без BOM и без #Requires (irm в
    PowerShell 5.1 читает octet-stream как Latin-1).
#>
# irm | iex выполняет скрипт в области консоли: настройки возвращаются в finally, чтобы не остаться в консоли.
$savedErrorAction = $ErrorActionPreference
$savedProgress = $ProgressPreference
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы
try {
$ToolsUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools'
$KitUrl = "$ToolsUrl/shell-kit"
$KitDir = 'C:\ClubShellKit'
$KioskUser = 'player'
$GetKitCmd = "irm $ToolsUrl/get-shell-kit.ps1 | iex"
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'

function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "      $Text" }

function Get-RegValue([string]$Path, [string]$Name) {
    try { (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { $null }
}

# Да/нет. В русской раскладке клавиши Y и N дают «н» и «т» — их тоже понимаем, чтобы не переключать раскладку.
function Read-YesNo([string]$Question, [bool]$Default) {
    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $yesRu = [string][char]0x043D
    $noRu = [string][char]0x0442
    while ($true) {
        $answer = "$(Read-Host "$Question $suffix")".Trim().ToLowerInvariant()
        if (-not $answer) { return $Default }
        if ($answer -in @('y', 'yes', $yesRu)) { return $true }
        if ($answer -in @('n', 'no', $noRu)) { return $false }
        Write-Host '      type y or n'
    }
}

# Проверки — как в shell-preflight.ps1. Любая ошибка запроса — «нет»: установка повторяема, лишний запуск безвреден.
function Test-LanFirewall {
    try {
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
        $notifyOff = $profiles.Count -gt 0 -and @($profiles | Where-Object { "$($_.NotifyOnListen)" -ne 'False' }).Count -eq 0
        $notifyOff -and @(Get-NetFirewallRule -Group 'Club LAN games' -ErrorAction SilentlyContinue).Count -gt 0
    } catch { $false }
}
function Test-MaxRefresh {
    try { [bool](Get-ScheduledTask -TaskName 'ClubMaxRefresh' -ErrorAction SilentlyContinue) } catch { $false }
}

# Скрипт с сервера клуба в своей области (& scriptblock, а не iex): его $ErrorActionPreference = 'Stop' не остаётся здесь.
function Invoke-ServedScript([string]$Name) {
    $url = "$ToolsUrl/$Name"
    Write-Info "running $url"
    try { & ([scriptblock]::Create((Invoke-RestMethod -UseBasicParsing -Uri $url))) | Out-Host }
    catch { throw "$Name failed: $($_.Exception.Message)" }
}

# Файлы внутри — только наследуемые права от папки (как в shell-install.ps1). icacls — с 'Continue': в 5.1 перенаправленный
# stderr внешней программы при 'Stop' стал бы исключением; пустую папку не трогаем (icacls ответил бы ошибкой на «*»).
function Reset-KitFileAcl {
    $ErrorActionPreference = 'Continue'
    if (@(Get-ChildItem -LiteralPath $KitDir -Force -ErrorAction SilentlyContinue).Count -eq 0) { return }
    & icacls.exe "$KitDir\*" /reset /T /C /Q 2>&1 | Out-Null
}

function Test-FileSha256([string]$Path, [string]$Sha256) {
    # -eq без учёта регистра: hex из SHA256SUMS (строчные) и Get-FileHash (прописные) совпадают.
    (Test-Path -LiteralPath $Path) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -eq $Sha256
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator (Win+X -> Terminal (Admin)).'
}
Write-Host "ClubShell kit for $env:COMPUTERNAME from the club server ($ToolsUrl)" -ForegroundColor Cyan
$shellInstalled = [bool](Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)

# --- 1. Номер места и имя Windows -------------------------------------------------------------------------------------
# Номер в панели club-server (личные диски games-seat-NN / solo-seat-NN) меняет Claude на сервере заранее. Сверять их
# здесь нельзя: до перезагрузки ПК ещё подключён к дискам старого номера. Страховка — shell-preflight после перезагрузки:
# номер дисков не равен PC-NN — FAIL, установки нет.
Write-Title 'Step 1: seat number and Windows name'
$activeName = $env:COMPUTERNAME
$pendingName = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
if ($pendingName -and $pendingName -ne $activeName) {
    Write-Host "This PC was renamed to '$pendingName' but not rebooted yet (the Windows name is still $activeName)." -ForegroundColor Yellow
    if (-not (Read-YesNo 'Reboot now to finish the rename?' $true)) { throw "Reboot first, then run the same command again. Nothing was changed." }
    Write-Host 'After the reboot sign in as Club and run the same command again:' -ForegroundColor Yellow
    Write-Host "  $GetKitCmd" -ForegroundColor Yellow
    Restart-Computer -Force
    return
}
if ($shellInstalled) {
    Write-Info "ClubShell is already installed: the seat number stays $activeName (ClubShell took it at the first registration)"
} else {
    $current = $null
    if ($activeName -match '^PC-(\d{2})$') { $current = [int]$Matches[1]; Write-Info "Windows name: $activeName (seat $current)" }
    else { Write-Host "Windows name: $activeName (not PC-NN: ClubShell takes the seat number from the name PC-NN)" -ForegroundColor Yellow }
    $prompt = 'Seat number of this PC (1-99), as in the club-server panel'
    if ($current) { $prompt += " [$current]" }
    $answer = "$(Read-Host $prompt)".Trim()
    if (-not $answer) {
        if (-not $current) { throw "No seat number typed and '$activeName' is not PC-NN. Nothing was changed." }
        $seat = $current
    } elseif ($answer -match '^\d{1,2}$' -and [int]$answer -ge 1) {
        $seat = [int]$answer
    } else {
        throw "'$answer' is not a seat number 1-99. Nothing was changed."
    }
    $newName = 'PC-{0:D2}' -f $seat
    if ($newName -ne $activeName) {
        if (-not (Read-YesNo "Rename this PC from $activeName to $newName and reboot now?" $true)) { throw 'Not renamed. Nothing was changed.' }
        Write-Host 'After the reboot sign in as Club and run the same command again:' -ForegroundColor Yellow
        Write-Host "  $GetKitCmd" -ForegroundColor Yellow
        Rename-Computer -NewName $newName -Force -Restart
        return
    }
    Write-Pass "Windows name $activeName = seat $seat"
}

# --- 2. Smart App Control -----------------------------------------------------------------------------------------------
# 1 — включён (не даст запустить неподписанные ClubShellAgent.exe и clubshell-shell.exe), 2 — режим оценки. До шага 3:
# install-max-refresh.ps1 компилирует и запускает неподписанный ClubMaxRefresh.exe.
Write-Title 'Step 2: Smart App Control'
$sac = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' 'VerifiedAndReputablePolicyState'
if ($sac -eq 1 -or $sac -eq 2) {
    $mode = if ($sac -eq 1) { 'ON' } else { 'in evaluation mode' }
    Write-Host "Smart App Control is ${mode}: it blocks the unsigned ClubShell 1.0.18 (and ClubMaxRefresh)." -ForegroundColor Yellow
    Write-Host '  Turn it off: Windows Security -> App & browser control -> Smart App Control -> Off.' -ForegroundColor Yellow
    if (-not (Read-YesNo 'Is Smart App Control off now (continue)?' $false)) {
        throw "Stopped before the install. Turn Smart App Control off, then run the same command again: $GetKitCmd"
    }
} else {
    Write-Pass 'Smart App Control is off'
}

# --- 3. Брандмауэр LAN-игр и ClubMaxRefresh -----------------------------------------------------------------------------
Write-Title 'Step 3: LAN games firewall and ClubMaxRefresh'
if (Test-LanFirewall) { Write-Pass 'firewall for LAN games is set' }
else {
    Write-Info 'firewall for LAN games is not set: installing it'
    Invoke-ServedScript 'install-lan-firewall.ps1'
    if (-not (Test-LanFirewall)) { throw 'The LAN games firewall is still not set after install-lan-firewall.ps1 (see above).' }
    Write-Pass 'firewall for LAN games set'
}
if (Test-MaxRefresh) { Write-Pass 'ClubMaxRefresh task exists' }
else {
    Write-Info 'ClubMaxRefresh task is missing: installing it'
    Invoke-ServedScript 'install-max-refresh.ps1'
    if (-not (Test-MaxRefresh)) { throw 'The ClubMaxRefresh task is still missing after install-max-refresh.ps1 (see above).' }
    Write-Pass 'ClubMaxRefresh task installed'
}

# --- 4. Набор в C:\ClubShellKit ----------------------------------------------------------------------------------------
Write-Title "Step 4: kit into $KitDir"
New-Item -ItemType Directory -Force -Path $KitDir | Out-Null
# Только SYSTEM и администраторы: игрок (киоск) не должен подменять скрипты, которые потом запускает администратор.
$kitAcl = New-Object System.Security.AccessControl.DirectorySecurity
$kitAcl.SetSecurityDescriptorSddlForm('D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
Set-Acl -LiteralPath $KitDir -AclObject $kitAcl
Reset-KitFileAcl

$sumsPart = Join-Path $KitDir 'SHA256SUMS.part'
try { Invoke-WebRequest -UseBasicParsing -Uri "$KitUrl/SHA256SUMS" -OutFile $sumsPart }
catch { throw "Cannot get $KitUrl/SHA256SUMS ($($_.Exception.Message)): is the kit published on the club server?" }
$entries = @()
try {
    foreach ($line in [IO.File]::ReadAllLines($sumsPart)) {
        if (-not $line.Trim()) { continue }
        # Только простые имена файлов: имя из списка становится путём в C:\ClubShellKit и частью URL.
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64}) [ *]([A-Za-z0-9._-]+)$')
        if (-not $match.Success -or $match.Groups[2].Value -match '^\.+$') { throw "SHA256SUMS on the server has a bad line: '$line'. Nothing was downloaded." }
        $entries += [pscustomobject]@{ Sha256 = $match.Groups[1].Value; Name = $match.Groups[2].Value }
    }
} finally {
    Remove-Item -LiteralPath $sumsPart -Force -ErrorAction SilentlyContinue
}
if (-not ($entries | Where-Object { $_.Name -eq 'shell-install.ps1' })) { throw 'SHA256SUMS on the server lists no shell-install.ps1: the kit there is incomplete. Nothing was downloaded.' }

$fetched = 0
foreach ($entry in $entries) {
    $target = Join-Path $KitDir $entry.Name
    if (Test-FileSha256 $target $entry.Sha256) { continue }
    $part = "$target.part"
    Write-Info "downloading $($entry.Name)"
    try { Invoke-WebRequest -UseBasicParsing -Uri "$KitUrl/$($entry.Name)" -OutFile $part }
    catch {
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        throw "Download of $($entry.Name) failed: $($_.Exception.Message)"
    }
    if (-not (Test-FileSha256 $part $entry.Sha256)) {
        $got = (Get-FileHash -Algorithm SHA256 -LiteralPath $part).Hash
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        throw "$($entry.Name) from the server has SHA256 $got, SHA256SUMS says $($entry.Sha256): the kit must be published again. The old file was kept."
    }
    Move-Item -LiteralPath $part -Destination $target -Force
    $fetched++
}
Write-Pass ("kit in {0}: {1} files, SHA256 checked ({2} downloaded, {3} already up to date)" -f $KitDir, $entries.Count, $fetched, ($entries.Count - $fetched))

# --- 5. Установка -------------------------------------------------------------------------------------------------------
Write-Title 'Step 5: install'
if ($shellInstalled) {
    Write-Host 'ClubShell is already installed on this PC: no second install.'
    Write-Info "rollback: $KitDir\shell-uninstall-run-as-admin.cmd"
    $kioskAlreadyOn = (Get-RegValue $WinlogonKey 'AutoAdminLogon') -eq '1' -and (Get-RegValue $WinlogonKey 'DefaultUserName') -eq $KioskUser
    if ($kioskAlreadyOn) { Write-Info "auto-logon already goes to '$KioskUser' (kiosk on); running kiosk-on again is harmless" }
    if (Read-YesNo 'Turn the kiosk on now (kiosk-on, after the approval in the kassa)?' (-not $kioskAlreadyOn)) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $KitDir 'kiosk-on.ps1') -KitDir $KitDir -KioskUser $KioskUser
    }
} else {
    $installScript = Join-Path $KitDir 'shell-install.ps1'
    Write-Info "starting $installScript in this window (PC key, Club password, PIN, approval wait, kiosk, reboot)"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -KitDir $KitDir -KioskUser $KioskUser
    $installCode = $LASTEXITCODE
    # Код 0 бывает и без киоска: не одобрен за время ожидания — только WARN. Решает автовход, а не код выхода.
    $kioskOn = (Get-RegValue $WinlogonKey 'AutoAdminLogon') -eq '1' -and (Get-RegValue $WinlogonKey 'DefaultUserName') -eq $KioskUser
    Write-Host ''
    if ($installCode -eq 0 -and $kioskOn) { Write-Host 'shell-install finished without FAIL lines; the kiosk is on.' -ForegroundColor Green }
    elseif ($installCode -eq 0) { Write-Host 'shell-install finished without FAIL lines.' -ForegroundColor Green }
    else { Write-Host "shell-install ended with exit code $installCode (FAIL lines or stopped, see above). Logs: $KitDir\logs" -ForegroundColor Red }
    if (-not $kioskOn -and (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)) {
        Write-Host 'ClubShell is installed, but the kiosk is NOT on yet: this PC is not finished.' -ForegroundColor Yellow
        Write-Host "  After the approval in the kassa run the same command again (it offers kiosk-on) or $KitDir\kiosk-on-run-as-admin.cmd" -ForegroundColor Yellow
    }
}
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    $ErrorActionPreference = $savedErrorAction
    $ProgressPreference = $savedProgress
}
