<#
.SYNOPSIS
    Ставит ClubShell 1.0.18 (касса клуба Deagle) на один гибрид: свой agent.json до установки, установщик без ключа в
    командной строке, проверки после установки, пароль Club и PIN администратора киоска, шаги в кассе.
.DESCRIPTION
    Запускать на гибриде под «Club», от администратора: shell-install-run-as-admin.cmd. Лаунчер сначала копирует папку
    набора в C:\ClubShellKit и работает оттуда: сразу после установки агент применяет политику, вшитую в MSI (касса ещё
    не одобрила ПК), — накопители USB выключаются на всей машине, вставленная флешка извлекается.
    По сети (irm | iex) этот скрипт не запускается: ему нужны установщик и соседние скрипты набора.
    Что делает, по порядку:
      1. shell-preflight.ps1: есть FAIL — стоп, ничего не изменено; есть WARN — спрашивает, продолжать ли;
      2. находит ClubShellSetup-1.0.18.exe (C:\ClubShellKit, рядом со скриптом, «Загрузки»; нет — предлагает скачать из
         релиза v1.0.18 на GitHub), сверяет размер и SHA256 из SHA256SUMS.txt релиза, снимает метку «из интернета»;
      3. запоминает DNS всех адаптеров и SHA256 помощника — для сверки после установки;
      4. спрашивает ключ ПК (ввод скрыт) и пишет C:\ProgramData\ClubShell\agent.json ДО установки, с правами только
         SYSTEM и Administrators. MSI видит готовый файл и оставляет его как есть (свой agent.json он пишет только на
         чистый ПК, KIOSKUSER в готовый файл не пишет), поэтому ключ не попадает ни в командную строку, ни в журналы
         установки (SHELL_CHANGES 70). При первом старте агент переносит ключ в secure\club.key (DPAPI) и стирает его в
         agent.json. В файле — только то, что касса не задаёт (настройки агента локальные, сервер их не присылает):
           server             — API ClubShell на Railway (clubshell-server-production), а не адрес кассы
                                (clubshell-admin-production — отдельное приложение, агенту не отвечает);
           shell.kioskUser    — киоск «player» (по умолчанию «club» = наш Club: агент отказался бы, и игрок остался бы
                                на рабочем столе администратора); removeFromPrivilegedGroups=false — иначе агент мог бы
                                вынуть совпавшую учётку из администраторов; перенаправление папок ВЫКЛ: иначе при первом
                                старте агент уводит Рабочий стол, Документы, Загрузки игрока на диск с наибольшим
                                свободным местом — наш H: или G: (личные диски, сбрасываются при загрузке), и выключение
                                потом этого не отменяет;
           games              — корни библиотек G:\Steam (библиотека Steam, даже без libraryfolders.vdf) и H:\ (игры
                                владельца лежат прямо в H:\<Игра>; записи каталога с полным путём H:\<Игра>\<exe>
                                находятся и без корней); Steam — G:\Steam\steam.exe (по умолчанию C:\Program Files (x86));
           storage.gamesShare — выкл: G: и H: подключает наш помощник (при нём агент и так ничего не монтирует);
           updates            — выкл: автообновление 1.0.18 не работает (манифест релиза не подписан — агент его
                                отвергнет, скачав 72 МБ); обновляем запуском нового установщика;
      5. ставит ClubShellSetup-1.0.18.exe /quiet /norestart KIOSKUSER=player INSTALLSHELL=1. НЕ передаёт SERVERURL и
         CLUBAPIKEY (переписали бы agent.json через powershell -Command, ключ — в командной строке процесса) и ничего из
         INSTALLDISKLESS / DISKLESS* (в релизном MSI помощника нет; наш 1.6.0 не трогаем). KIOSKUSER нужен всё равно:
         MSI запоминает его в HKLM\SOFTWARE\ClubShell\KioskUser, по нему работает удаление;
      6. проверяет: служба агента работает; ключ ушёл из agent.json в secure\club.key; киоск «player» создан и не в
         привилегированных группах; автовход переключён на player; Club — по-прежнему администратор; папок ClubShell на
         G:/H: нет (перенаправления не было); помощник тот же и G:/H: на месте; DNS не изменился; журнал агента
         (регистрация: ждёт одобрения / ключ не принят / помощник признан владельцем G:);
      7. предлагает задать пароль Club (set-club-password.ps1) и PIN панели администратора киоска (set-shell-pin.ps1);
      8. печатает, что сделать в кассе, и по желанию ждёт, пока владелец одобрит ПК.
    Ключ ПК, пароль и PIN не печатаются и не пишутся в файлы (ключ — только в agent.json до первого старта агента).
    Установка не удалась — заготовленный agent.json удаляется (ключ не остаётся на диске).
    Журнал этого скрипта — C:\ClubShellKit\logs\install-<ПК>-<время>.txt, установщика — setup-<время>*.log там же.
    Строки вывода — только ASCII, без BOM и без #Requires (как у остальных скриптов набора).
.EXAMPLE
    shell-install-run-as-admin.cmd
#>
param(
    [string]$KitDir = 'C:\ClubShellKit',
    [string]$ServerUrl = 'https://clubshell-server-production.up.railway.app',
    [string]$KioskUser = 'player',
    [string]$AdminUser = 'Club'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы

# Релиз v1.0.18 (тег -> 04ff8f2, 2026-10-27): бандл Burn = WebView2 (если нет) + ClubShell-1.0.18.msi (агент + оболочка).
# Размер и SHA256 — из ответа API релиза и SHA256SUMS.txt (совпадают). ClubShell-Shell-1.0.18.* — только оболочка, без
# агента и в другую папку: не ставить.
$Version = '1.0.18'
$SetupName = 'ClubShellSetup-1.0.18.exe'
$SetupSize = 75490102
$SetupSha256 = '1D23DEE18E17BE03A19801CD2D7CD1F8C8FDE7B8D4E83A2B5BA6785EAF46A1CF'
$SetupUrl = 'https://github.com/Nightcall7442/club-shell/releases/download/v1.0.18/ClubShellSetup-1.0.18.exe'
$HelperSha256 = '26635A5F7AAB4D77441D0B0138064B9C7F64BB1DAF4B0BCAF09052439DDD0148'   # ClubDisklessHelper 1.6.0+13609a5
# Корни библиотек и Steam (agent.json -> games). Массив из файла заменяет массив по умолчанию (D:\Games, G:\Games) целиком.
$LibraryRoots = @('G:\Steam', 'H:\')
$SteamExe = 'G:\Steam\steam.exe'
$DataDir = Join-Path $env:ProgramData 'ClubShell'
$AgentJson = Join-Path $DataDir 'agent.json'
$ClubKeyFile = Join-Path $DataDir 'secure\club.key'
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$PrivilegedGroupSids = @('S-1-5-32-544', 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551', 'S-1-5-32-547', 'S-1-5-32-556', 'S-1-5-32-578')
$Count = @{ Fail = 0; Warn = 0 }

function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "      $Text" }
function Write-Warn([string]$Text, [string]$Fix) {
    $Count.Warn++
    Write-Host "WARN  $Text" -ForegroundColor Yellow
    if ($Fix) { Write-Host "      -> $Fix" -ForegroundColor Yellow }
}
function Write-Fail([string]$Text, [string]$Fix) {
    $Count.Fail++
    Write-Host "FAIL  $Text" -ForegroundColor Red
    if ($Fix) { Write-Host "      -> $Fix" -ForegroundColor Red }
}

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

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 3
    }
    [bool](& $Condition)
}

function Get-GroupMemberSids([string]$GroupSid) {
    try {
        return @(Get-LocalGroupMember -SID $GroupSid -ErrorAction Stop | ForEach-Object { $_.SID.Value })
    } catch { }
    $sids = @()
    try {
        $name = ((New-Object Security.Principal.SecurityIdentifier($GroupSid)).Translate([Security.Principal.NTAccount]).Value -split '\\')[-1]
        $group = [ADSI]"WinNT://./$name,group"
        foreach ($member in @($group.Invoke('Members'))) {
            $bytes = $member.GetType().InvokeMember('objectSid', 'GetProperty', $null, $member, $null)
            $sids += (New-Object Security.Principal.SecurityIdentifier($bytes, 0)).Value
        }
    } catch { }
    $sids
}

function Get-PrivilegedGroupsOf([string]$UserSid) {
    $names = @()
    foreach ($groupSid in $PrivilegedGroupSids) {
        if ((Get-GroupMemberSids $groupSid) -contains $UserSid) { $names += $groupSid }
    }
    $names
}

function Get-DnsSnapshot {
    $map = @{}
    Get-DnsClientServerAddress -ErrorAction SilentlyContinue | ForEach-Object {
        $map["$($_.InterfaceAlias) ($($_.AddressFamily))"] = (@($_.ServerAddresses) -join ', ')
    }
    $map
}

function Get-HelperState {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='ClubDisklessHelper'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    $exe = ($service.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim()
    $hash = if (Test-Path -LiteralPath $exe) { (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash } else { $null }
    [pscustomobject]@{ State = $service.State; Exe = $exe; Hash = $hash }
}

# Ключ в agent.json уже стёрт агентом и лежит в secure\club.key. Значение ключа не читается в переменные и не печатается:
# проверяется только, что поле пустое.
function Test-KeyMoved {
    if (-not (Test-Path -LiteralPath $ClubKeyFile)) { return $false }
    try { $config = [IO.File]::ReadAllText($AgentJson) | ConvertFrom-Json } catch { return $false }
    [string]::IsNullOrEmpty([string]$config.server.clubApiKey)
}

# Состояние агента по его журналу (Serilog compact JSON: шаблон сообщения в "@mt", исключение в "@x"). Печатается только
# вывод, не строки журнала.
function Get-AgentLogState {
    $state = @{ Registration = 'none'; Detail = ''; HelperStandDown = $false; KioskRefused = $false }
    $log = Get-ChildItem -LiteralPath (Join-Path $DataDir 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return $state }
    foreach ($line in @(Get-Content -LiteralPath $log.FullName -Tail 3000 -ErrorAction SilentlyContinue)) {
        if ($line -notmatch '"@mt"') { continue }
        if ($line -match 'KioskAccountRefused|privilegedAccount') { $state.KioskRefused = $true }
        try { $entry = $line | ConvertFrom-Json } catch { continue }
        $template = [string]$entry.'@mt'
        if ($template -like 'Games library volume is managed by*') { $state.HelperStandDown = $true }
        elseif ($template -like 'Registered as PC*') { $state.Registration = 'registered'; $state.Detail = "name $($entry.PcName), zone $($entry.Zone)" }
        elseif ($template -like 'Registration attempt*failed*') {
            $exception = [string]$entry.'@x'
            if ($exception -match 'waiting for approval|pendingApproval') { $state.Registration = 'waiting'; $state.Detail = '' }
            elseif ($exception -match 'X-Club-Key') { $state.Registration = 'badKey'; $state.Detail = '' }
            else {
                $first = (($exception -split "`n")[0]).Trim()
                if ($first.Length -gt 160) { $first = $first.Substring(0, 160) }
                $state.Registration = 'failing'; $state.Detail = $first
            }
        }
    }
    $state
}

function Show-Registration($State) {
    switch ($State.Registration) {
        'registered' { Write-Pass "agent registered and approved ($($State.Detail))" }
        'waiting' { Write-Pass 'agent reached the server: the PC waits for approval in the kassa' }
        'badKey' { Write-Fail 'the server rejected the PC key (X-Club-Key)' 'shell-uninstall (remove data), then install again with the right key' }
        'failing' { Write-Warn "registration fails, the agent keeps retrying: $($State.Detail)" }
        default { Write-Warn "no registration in the agent log yet ($DataDir\logs)" }
    }
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

# Набор — в C:\ClubShellKit (лаунчер копирует его сам; это — если скрипт запустили руками с флешки).
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path $KitDir | Out-Null
# Только SYSTEM и администраторы: игрок (киоск) не должен подменять скрипты, которые потом запускает администратор.
$kitAcl = New-Object System.Security.AccessControl.DirectorySecurity
$kitAcl.SetSecurityDescriptorSddlForm('D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
Set-Acl -LiteralPath $KitDir -AclObject $kitAcl
# Файлы внутри — только наследуемые права от папки (старый лаунчер мог оставить их с пустым DACL).
& icacls.exe "$KitDir\*" /reset /T /C /Q 2>&1 | Out-Null
if ($here -and $here.TrimEnd('\') -ne $KitDir.TrimEnd('\')) {
    Get-ChildItem -LiteralPath $here -File | Where-Object { $_.Extension -in @('.ps1', '.cmd') -or $_.Name -eq $SetupName } |
        Copy-Item -Destination $KitDir -Force
}
$LogDir = Join-Path $KitDir 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
try { Start-Transcript -LiteralPath (Join-Path $LogDir "install-$env:COMPUTERNAME-$stamp.txt") | Out-Null } catch { }

$wroteAgentJson = $false
$createdDataDir = $false
$installed = $false
try {
    Write-Host "ClubShell $Version install on $env:COMPUTERNAME (kiosk account '$KioskUser', server $ServerUrl)" -ForegroundColor Cyan
    if ($KioskUser -ieq $AdminUser) { throw "Kiosk account '$KioskUser' is the admin account '$AdminUser'. Nothing was changed." }

    # --- 1. Проверка ----------------------------------------------------------------------------------------------
    Write-Title 'Step 1: preflight check'
    $preflight = Join-Path $KitDir 'shell-preflight.ps1'
    if (-not (Test-Path -LiteralPath $preflight)) { throw "$preflight is missing: copy the whole kit folder. Nothing was changed." }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $preflight -ServerUrl $ServerUrl -KioskUser $KioskUser -AdminUser $AdminUser -KitDir $KitDir
    $preflightCode = $LASTEXITCODE
    if ($preflightCode -notin @(0, 1, 2)) { throw "The preflight check itself failed (exit code $preflightCode). Nothing was changed." }
    if ($preflightCode -ge 2) { throw 'The preflight check has FAIL lines (above). Fix them and run again. Nothing was changed.' }
    if ($preflightCode -eq 1 -and -not (Read-YesNo 'The check has WARN lines (above). Install anyway?' $false)) {
        throw 'Stopped at the WARN lines. Nothing was changed.'
    }

    # --- 2. Установщик ----------------------------------------------------------------------------------------------
    Write-Title "Step 2: installer $SetupName"
    $setup = Join-Path $KitDir $SetupName
    if (-not (Test-Path -LiteralPath $setup)) {
        $candidate = Join-Path $env:USERPROFILE "Downloads\$SetupName"
        if (Test-Path -LiteralPath $candidate) { Copy-Item -LiteralPath $candidate -Destination $setup -Force }
    }
    if (-not (Test-Path -LiteralPath $setup)) {
        Write-Info "$SetupName is not in $KitDir or Downloads."
        if (-not (Read-YesNo "Download it from GitHub (release v$Version, 72 MB)?" $true)) {
            throw "Put $SetupName (release v$Version) into $KitDir and run again. Nothing was changed."
        }
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        Write-Info "downloading $SetupUrl"
        Invoke-WebRequest -UseBasicParsing -Uri $SetupUrl -OutFile "$setup.part"
        Move-Item -LiteralPath "$setup.part" -Destination $setup -Force
    }
    $setupLength = (Get-Item -LiteralPath $setup).Length
    $setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
    if ($setupLength -ne $SetupSize -or $setupHash -ne $SetupSha256) {
        throw "$setup is not the official v$Version file (size $setupLength, SHA256 $setupHash). Delete it and download again. Nothing was changed."
    }
    # Скачанный браузером файл несёт метку зоны: без неё SmartScreen не спрашивает про неподписанный установщик.
    Unblock-File -LiteralPath $setup
    Write-Pass "installer verified: $setupLength bytes, SHA256 $SetupSha256 (release v$Version, unsigned)"

    # --- 3. Снимок для сверки -------------------------------------------------------------------------------------------
    $dnsBefore = Get-DnsSnapshot
    $helperBefore = Get-HelperState
    $dnsBefore.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name): $($_.Value)" } |
        Set-Content -LiteralPath (Join-Path $LogDir "dns-before-$stamp.txt") -Encoding UTF8

    # --- 4. agent.json с ключом ПК ----------------------------------------------------------------------------------------
    Write-Title 'Step 3: agent.json (before the installer runs)'
    if (Test-Path -LiteralPath $AgentJson) { throw "$AgentJson already exists (earlier install?): run shell-uninstall first. Nothing was changed." }
    Write-Info 'The PC key: the same key the kassa page "Install on PC" asks for (from the ClubShell superadmin / Club__EnrollmentKey).'
    Write-Info 'Type or paste it (right click pastes); nothing is shown while you type.'
    $secureKey = Read-Host 'PC key' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $secureKey.Dispose() }
    # Ключ могли вставить из буфера обмена — не оставлять его там.
    try { Set-Clipboard -Value ' ' } catch { }
    # Формат ключа — как проверяет страница кассы «Установка на ПК»; сам ключ в сообщения не попадает.
    if ($key -notmatch '^[A-Za-z0-9._-]{8,128}$') {
        $key = $null
        throw 'The key does not look right (8-128 characters: letters, digits, dot, dash, underscore). Nothing was changed.'
    }

    $origin = $ServerUrl.Trim().TrimEnd('/') -replace '/api/v\d+$', ''
    $config = [ordered]@{
        server  = [ordered]@{
            baseUrl    = "$origin/api/v1"
            wsUrl      = ($origin -replace '^http', 'ws') + '/ws/agent'
            clubApiKey = $key
        }
        shell   = [ordered]@{
            kioskUser = [ordered]@{
                name                       = $KioskUser
                removeFromPrivilegedGroups = $false
                folderRedirect             = [ordered]@{ enabled = $false; root = $null; clearOnReset = $true }
            }
        }
        games   = [ordered]@{
            libraryRoots = $LibraryRoots
            launchers    = [ordered]@{ steam = [ordered]@{ exePath = $SteamExe } }
        }
        storage = [ordered]@{ gamesShare = [ordered]@{ enabled = $false } }
        updates = [ordered]@{ enabled = $false }
    }
    $json = $config | ConvertTo-Json -Depth 8
    $key = $null
    $config.server.clubApiKey = $null

    if (-not (Test-Path -LiteralPath $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir | Out-Null
        $createdDataDir = $true
    }
    # Файл создаётся сразу с DACL «SYSTEM и Administrators» (без наследования): ключ ни на миг не читается группой Users.
    # Агент потом перезаписывает файл атомарно (новый файл наследует права папки — ключа в нём уже нет).
    $security = New-Object System.Security.AccessControl.FileSecurity
    $security.SetSecurityDescriptorSddlForm('D:PAI(A;;FA;;;SY)(A;;FA;;;BA)')
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
    $json = $null
    try { $stream = [IO.File]::Create($AgentJson, 4096, [IO.FileOptions]::None, $security) }
    catch {
        # Такой перегрузки нет в .NET Core (PowerShell 7): пустой файл, его права, потом содержимое.
        [IO.File]::WriteAllBytes($AgentJson, [byte[]]@())
        Set-Acl -LiteralPath $AgentJson -AclObject $security
        $stream = [IO.File]::Open($AgentJson, [IO.FileMode]::Truncate, [IO.FileAccess]::Write)
    }
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
    $wroteAgentJson = $true
    Write-Pass "$AgentJson written (SYSTEM + Administrators only): kiosk '$KioskUser', folder redirection off, library roots $($LibraryRoots -join ' '), Steam $SteamExe, updates off"

    # --- 5. Установка ---------------------------------------------------------------------------------------------------
    Write-Title "Step 4: $SetupName /quiet"
    $setupLog = Join-Path $LogDir "setup-$stamp.log"
    $arguments = '/quiet /norestart /log "{0}" KIOSKUSER={1} INSTALLSHELL=1' -f $setupLog, $KioskUser
    Write-Info "running: $SetupName $arguments"
    Write-Info 'a few minutes, no windows; the agent starts at the end and turns USB storage off on this PC'
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($exitCode -eq 0) { Write-Pass 'setup finished (exit code 0)' }
    elseif ($exitCode -eq 3010) { Write-Pass 'setup finished (exit code 3010: Windows wants a reboot)' }
    else {
        $hint = switch ($exitCode) { 1602 { 'cancelled' } 1618 { 'another install is running: wait or reboot' } 1638 { 'another version is installed: run shell-uninstall' } default { 'see the setup log' } }
        throw "Setup failed with exit code $exitCode ($hint). Logs: $setupLog and the *_ClubShellMsi*.log next to it."
    }
    $installed = $true

    # --- 6. Проверки после установки ------------------------------------------------------------------------------------
    Write-Title 'Step 5: checks after the install'
    $running = Wait-Until { (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue).Status -eq 'Running' } 60
    if (-not $running) {
        try { Start-Service -Name ClubShellAgent -ErrorAction Stop } catch { }
        $running = Wait-Until { (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue).Status -eq 'Running' } 30
    }
    if ($running) { Write-Pass 'service ClubShellAgent is running' }
    else { Write-Fail 'service ClubShellAgent is not running' "Event Viewer -> Application, source ClubShellAgent; $DataDir\logs" }

    if (Wait-Until { Test-KeyMoved } 90) { Write-Pass 'PC key moved into secure\club.key (DPAPI); agent.json has no key now' }
    else { Write-Warn 'the PC key is still in agent.json (readable only by SYSTEM and Administrators)' "agent log $DataDir\logs; the agent moves it at its next start" }

    $kioskReady = Wait-Until {
        (Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue) -and
        (Get-RegValue $WinlogonKey 'DefaultUserName') -eq $KioskUser -and
        (Get-RegValue $WinlogonKey 'AutoAdminLogon') -eq '1'
    } 120
    $kiosk = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if ($kiosk) { Write-Pass "kiosk account '$KioskUser' exists" } else { Write-Fail "kiosk account '$KioskUser' was not created" "$DataDir\logs" }
    if ($kioskReady) { Write-Pass "auto-logon now goes to '$KioskUser' (the kiosk starts at the next boot)" }
    else { Write-Warn ("auto-logon is AutoAdminLogon={0}, DefaultUserName={1}: not switched to '{2}' yet" -f (Get-RegValue $WinlogonKey 'AutoAdminLogon'), (Get-RegValue $WinlogonKey 'DefaultUserName'), $KioskUser) "$DataDir\logs" }
    if ($kiosk) {
        $groups = @(Get-PrivilegedGroupsOf $kiosk.SID.Value)
        if ($groups.Count -eq 0) { Write-Pass "'$KioskUser' is a standard user (no admin or operator groups)" }
        else { Write-Fail "'$KioskUser' is in privileged groups ($($groups -join ', '))" 'shell-uninstall-run-as-admin.cmd' }
    }
    $adminAccount = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
    if ($adminAccount -and (Get-GroupMemberSids 'S-1-5-32-544') -contains $adminAccount.SID.Value) { Write-Pass "$AdminUser is still an administrator" }
    else { Write-Fail "$AdminUser is no longer an administrator" 'Computer Management -> Local Users and Groups -> Administrators: add Club back' }

    # Перенаправление папок (выключено в agent.json) создало бы <диск>:\ClubShell\Users\<киоск>.
    $redirected = @('G:\ClubShell', 'H:\ClubShell') | Where-Object { Test-Path -LiteralPath $_ }
    if ($redirected) { Write-Fail "folder redirection happened: $($redirected -join ', ')" 'shell-uninstall (remove the player profile), then install again' }
    else { Write-Pass "no folder redirection to G:/H: ($KioskUser's folders stay in C:\Users\$KioskUser)" }

    $helperAfter = Get-HelperState
    if ($helperAfter -and $helperAfter.State -eq 'Running' -and $helperAfter.Hash -eq $HelperSha256 -and $helperBefore -and $helperAfter.Hash -eq $helperBefore.Hash) {
        Write-Pass 'helper ClubDisklessHelper 1.6.0+13609a5 untouched and running'
    } else {
        Write-Fail 'helper ClubDisklessHelper changed or stopped' 'irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex'
    }
    foreach ($letter in @('G', 'H')) {
        if (Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue) { Write-Pass "${letter}: is still mounted" }
        else { Write-Fail "${letter}: is gone" 'check the helper (panel "Diskless")' }
    }

    $dnsAfter = Get-DnsSnapshot
    $dnsChanged = @($dnsBefore.Keys | Where-Object { $dnsAfter[$_] -ne $dnsBefore[$_] }) + @($dnsAfter.Keys | Where-Object { -not $dnsBefore.ContainsKey($_) -and $dnsAfter[$_] })
    if ($dnsChanged.Count -eq 0) { Write-Pass 'DNS servers of every adapter unchanged' }
    else {
        foreach ($name in ($dnsChanged | Select-Object -Unique)) { Write-Fail "DNS of $name changed: '$($dnsBefore[$name])' -> '$($dnsAfter[$name])'" 'kassa policy: webFilter.dnsServers must be [] (the agent sets DNS on every adapter)' }
    }

    $usbStart = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start'
    if ($usbStart -eq 4) { Write-Info 'USBSTOR Start=4: USB storage is off for the whole PC (policy usb.allowStorage=false); expected' }
    else { Write-Info "USBSTOR Start=$usbStart" }

    Start-Sleep -Seconds 5
    $logState = Get-AgentLogState
    if ($logState.HelperStandDown) { Write-Pass 'agent log: "Games library volume is managed by ClubDisklessHelper" (the agent leaves G:/H: alone)' }
    else { Write-Warn 'agent log has no "managed by ClubDisklessHelper" line yet' }
    if ($logState.KioskRefused) { Write-Fail "agent log: the kiosk account was refused as privileged" 'shell-uninstall, fix the account, install again' }
    Show-Registration $logState

    # --- 7. Пароль Club и PIN --------------------------------------------------------------------------------------------
    Write-Title "Step 6: $AdminUser password and the kiosk admin PIN"
    Write-Info "With the kiosk on, holding Shift at boot skips auto-logon and shows the sign-in screen: $AdminUser must have a password before players arrive."
    $passwordScript = Join-Path $KitDir 'set-club-password.ps1'
    if ((Test-Path -LiteralPath $passwordScript) -and (Read-YesNo "Set (or change) the $AdminUser password now?" $true)) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $passwordScript -AdminUser $AdminUser
        if ($LASTEXITCODE) { Write-Warn "$AdminUser password not set" "run set-club-password-run-as-admin.cmd later, BEFORE players" }
    } else {
        Write-Warn "$AdminUser password not changed here" 'set-club-password-run-as-admin.cmd, BEFORE players'
    }
    Write-Info 'The admin PIN opens the kiosk admin panel (Ctrl+Alt+Shift+A): exit to maintenance, reboot. Without it the panel answers "not configured".'
    $pinScript = Join-Path $KitDir 'set-shell-pin.ps1'
    if ((Test-Path -LiteralPath $pinScript) -and (Read-YesNo 'Set the admin PIN now (6-12 digits)?' $true)) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pinScript
        if ($LASTEXITCODE) { Write-Warn 'admin PIN not set' 'set-shell-pin-run-as-admin.cmd later' }
    } else {
        Write-Warn 'admin PIN not set here' 'set-shell-pin-run-as-admin.cmd'
    }

    # --- 8. Касса ----------------------------------------------------------------------------------------------------
    Write-Title 'Step 7: in the kassa (owner)'
    $number = if ($env:COMPUTERNAME -match '^PC-(\d{2})$') { [int]$Matches[1] } else { '?' }
    Write-Host "  1. Open the kassa -> 'Zal i ustroystva' (Hall and devices): the new PC $env:COMPUTERNAME is there, maintenance ON."
    Write-Host "  2. Check its number is $number (taken from the Windows name). Do not add PCs by hand."
    Write-Host '  3. Choose its zone (spelled exactly as in the catalog game zones).'
    Write-Host "  4. Switch 'Obsluzhivanie' (maintenance) OFF: that approves the PC."
    Write-Host "  5. 'Politika PK' (PC policy): webFilter.dnsServers stays [] and blockResolvedIps false; usb.allowStorage as decided."
    Write-Host '  6. Then reboot this PC: it signs in as the kiosk; check the games as a player (docs/shell-install.md, step 6).'
    if ($logState.Registration -ne 'registered' -and (Read-YesNo 'Wait here until the PC is approved in the kassa (up to 10 minutes)?' $true)) {
        $approved = Wait-Until { (Get-AgentLogState).Registration -eq 'registered' } 600
        Show-Registration (Get-AgentLogState)
        if (-not $approved) { Write-Info 'not approved yet: the agent keeps retrying by itself after the approval' }
        else {
            # Политика кассы (DNS, USB) приходит только после одобрения — проверить DNS ещё раз.
            Write-Info 'waiting 60 s for the kassa policy, then checking DNS again'
            Start-Sleep -Seconds 60
            $dnsLate = Get-DnsSnapshot
            $lateChanged = @($dnsBefore.Keys | Where-Object { $dnsLate[$_] -ne $dnsBefore[$_] })
            if ($lateChanged.Count -eq 0) { Write-Pass 'DNS servers unchanged after the kassa policy' }
            else { foreach ($name in $lateChanged) { Write-Fail "DNS of $name changed after the policy: '$($dnsBefore[$name])' -> '$($dnsLate[$name])'" 'kassa policy: webFilter.dnsServers must be []' } }
        }
    }
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
    $Count.Fail++
} finally {
    # Установка не дошла до конца — заготовленный agent.json с ключом не остаётся на диске.
    if ($wroteAgentJson -and -not $installed -and -not (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $AgentJson -Force -ErrorAction SilentlyContinue
        if ($createdDataDir -and @(Get-ChildItem -LiteralPath $DataDir -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $DataDir -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $AgentJson) { Write-Host "Delete $AgentJson by hand: it holds the PC key." -ForegroundColor Red }
        else { Write-Host 'The prepared agent.json (with the PC key) was removed.' -ForegroundColor Yellow }
    }
    Write-Host ''
    $color = if ($Count.Fail -gt 0) { 'Red' } elseif ($Count.Warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ("Install result for {0}: {1} FAIL, {2} WARN. Log: {3}" -f $env:COMPUTERNAME, $Count.Fail, $Count.Warn, $LogDir) -ForegroundColor $color
    if ($installed) { Write-Host 'Rollback: shell-uninstall-run-as-admin.cmd (from C:\ClubShellKit; USB sticks no longer work on this PC).' }
    try { Stop-Transcript | Out-Null } catch { }
}
if ($PSCommandPath) { if ($Count.Fail -gt 0) { exit 2 } else { exit 0 } }
