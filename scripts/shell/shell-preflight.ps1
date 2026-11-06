<#
.SYNOPSIS
    Проверка гибрида перед установкой ClubShell 1.0.18 (касса клуба Deagle). Только читает: ничего не меняет.
.DESCRIPTION
    Запускать на гибриде (Windows на локальном SSD, имя PC-NN) под «Club», PowerShell от администратора:
    shell-preflight-run-as-admin.cmd — с флешки или из C:\ClubShellKit. shell-install.ps1 запускает эту проверку сам и
    не ставит шелл, пока есть FAIL. Порядок работ и что делать с каждым FAIL/WARN — docs/shell-install.md.
    Что проверяет:
      1. ПК: имя PC-NN и переименование не ждёт перезагрузки; системный диск локальный (бездисковый ПК — отказ: там
         шелл ставится в эталон в режиме мастера, отдельно); место на C:; ожидание перезагрузки; Smart App Control
         (сборка 1.0.18 не подписана — он её заблокирует);
      2. помощник бездиска: служба ClubDisklessHelper, exe в %ProgramFiles%\ClubDiskless, версия не ниже 1.6.0 (по
         SHA256 exe: helper-build.json службы 1.8.0+ или таблица известных сборок); G: «Online Games» и H: «Offline
         Games» по iSCSI; номер личных дисков (таргеты games-seat-NN, solo-seat-NN) = номер в имени Windows (ClubShell
         берёт номер ПК из имени Windows при первой регистрации); G:\Steam\steam.exe; право записи группы Users в папки
         игр (игрок в киоске — обычный пользователь, не администратор);
      3. учётные записи: Club — администратор и включён; пароль Club (пустой — WARN: до прихода игроков задать,
         set-club-password: Shift при загрузке пропускает автовход и даёт экран входа); срок пароля (образы h1b/h1c —
         42 дня); киоск «player» — ещё нет (агент создаст обычного пользователя со случайным паролем) или есть, но не в
         привилегированных группах; имя киоска не совпадает с Club (SAM не различает регистр: KIOSKUSER=club по
         умолчанию — это наш Club); другие включённые администраторы с пустым паролем; задачи планировщика с
         сохранённым паролем Club (смена пароля их сломает);
      4. ClubShell ещё не стоит: нет службы ClubShellAgent, записей «ClubShell*» в «Программах» (бандл, MSI, в том
         числе старые пилотные сборки CI), HKLM\SOFTWARE\ClubShell, C:\ProgramData\ClubShell (остатки прошлой установки —
         сначала shell-uninstall: заготовленный agent.json и старая регистрация мешают);
      5. сеть и прочее: сервер ClubShell отвечает (GET /health), часы не расходятся с сервером, WebView2, DNS адаптеров
         (для сверки после установки), USBSTOR, брандмауэр для игр по локалке (install-lan-firewall.ps1), ClubMaxRefresh,
         установщик ClubShellSetup-1.0.18.exe рядом (размер и SHA256).
    Пустой ли пароль, проверяется входом с пустым паролем (LogonUser, сетевой вход): «пустые пароли запрещены» —
    пароль пустой, «неверный пароль» — пароль есть. Неверная попытка идёт в счётчик блокировки Windows (порог — 10 за
    10 минут): проверку не гонять десятки раз подряд.
    -Golden — эталон бездиска в режиме мастера (его передаёт shell-install.ps1 -Golden, тот — get-shell-kit, когда
    видит сеанс …:diskless-master). Отличия: имя USER, а не PC-NN (FAIL иначе); системный диск — iSCSI с сеансом
    мастера (место или локальный диск — FAIL); открытая правка библиотек — WARN; ожидание перезагрузки — FAIL
    (перезагружаться — до установки: после kiosk-on мастер загрузится в киоск); номер личных дисков — только для
    сведения (они того ПК, на котором сейчас мастер); G:/H: не подключены — WARN; администратор — -AdminUser
    (администратор эталона; Club есть только в образах гибридов): встроенный Administrator — FAIL, учётка Microsoft —
    FAIL (её вход и токены ушли бы в образ, снимки и резервные копии), пустой пароль — FAIL (места копируют эталон:
    Shift при загрузке на любом месте дал бы рабочий стол администратора без пароля). Другие включённые учётки — на
    экране входа всех мест: включённый встроенный Administrator, второй администратор и учётка с пустым паролем — FAIL
    (seal-golden.ps1 перед каждой публикацией их тоже не пропустит; лучше до установки, чем после).
    Код выхода при запуске файлом: 0 — всё PASS, 1 — есть WARN, 2 — есть FAIL.
    По сети одной строкой — если файл выложен на сервер клуба (/srv/club/pxe/tools; пока не выложен):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/shell-preflight.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
param(
    [string]$ServerUrl = 'https://clubshell-server-production.up.railway.app',
    [string]$KioskUser = 'player',
    [string]$AdminUser = 'Club',
    [string]$KitDir = 'C:\ClubShellKit',
    [switch]$Golden
)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# Сверяемые значения — те же, что в shell-install.ps1 (менять вместе).
# Помощник: exe службы ClubDisklessHelper и его версия не ниже 1.6.0. Версия — по SHA256 exe (Get-HelperVersion):
# helper-build.json службы 1.8.0+ или таблица известных сборок ниже (SHA256 exe -> версия).
$HelperExePath = Join-Path $env:ProgramFiles 'ClubDiskless\ClubDisklessHelper.exe'
$HelperBuildFile = Join-Path $env:ProgramData 'ClubDiskless\helper-build.json'
$HelperMinVersion = [version]'1.6.0'
$HelperBuilds = @{
    '26635a5f7aab4d77441d0b0138064b9c7f64bb1daf4b0bcaf09052439ddd0148' = '1.6.0'   # 1.6.0+13609a5
    '41da96a0b0721b22bb5f5e1898c927f08c4856968ee059a0c40b3d2b54a30e23' = '1.7.0'   # 1.7.0+f79e0d5
    '05a73a8f0cb1717040c7f64fe1c94104fbdf501b9b196f90659c56d0c33b31e1' = '1.8.0'   # 1.8.0+e77f0ad
}
$SetupName = 'ClubShellSetup-1.0.18.exe'
$SetupSize = 75490102
$SetupSha256 = '1D23DEE18E17BE03A19801CD2D7CD1F8C8FDE7B8D4E83A2B5BA6785EAF46A1CF'   # SHA256SUMS.txt релиза v1.0.18
$UpdateHelperCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex'
$LanFirewallCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/install-lan-firewall.ps1 | iex'
$LocalBusTypes = @('NVMe', 'SATA', 'SAS', 'RAID', 'ATA', 'SCSI')
# BusType бывает числом (MSFT_Disk): 1 SCSI, 3 ATA, 7 USB, 8 RAID, 9 iSCSI, 10 SAS, 11 SATA, 14 Virtual, 17 NVMe.
$BusTypeNames = @{ '1' = 'SCSI'; '3' = 'ATA'; '7' = 'USB'; '8' = 'RAID'; '9' = 'iSCSI'; '10' = 'SAS'; '11' = 'SATA'; '14' = 'Virtual'; '17' = 'NVMe' }
# Группы, членов которых агент не берёт в киоск (LocalUserManager.PrivilegedGroupSids в 1.0.18): Administrators, Account,
# Server, Print, Backup Operators, Power Users, Network Configuration Operators, Hyper-V Administrators.
$PrivilegedGroupSids = @('S-1-5-32-544', 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551', 'S-1-5-32-547', 'S-1-5-32-556', 'S-1-5-32-578')
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
# Хэш-таблица, а не $script:-переменные: так счётчики одинаково работают и файлом, и через irm | iex.
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

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { $null }
}

function Get-BusName($BusType) {
    $text = "$BusType"
    if ($BusTypeNames.ContainsKey($text)) { return $BusTypeNames[$text] }
    $text
}

function Get-UserSid([string]$Name) {
    try { (New-Object Security.Principal.NTAccount($Name)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $null }
}

# SID членов локальной группы. Get-LocalGroupMember падает на «осиротевших» SID (удалённые учётки в группе) — тогда ADSI.
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

# Имена привилегированных групп, в которых состоит учётка (группы, которых нет в этой редакции Windows, пропускаются).
function Get-PrivilegedGroupsOf([string]$UserSid) {
    $names = @()
    foreach ($groupSid in $PrivilegedGroupSids) {
        if ((Get-GroupMemberSids $groupSid) -contains $UserSid) {
            try { $names += ((New-Object Security.Principal.SecurityIdentifier($groupSid)).Translate([Security.Principal.NTAccount]).Value -split '\\')[-1] }
            catch { $names += $groupSid }
        }
    }
    $names
}

# Может ли группа Users (или Authenticated Users, Everyone, Interactive) писать в папку: разрешающая запись ACE на самой
# папке (не «только для дочерних»). Приближение — запрещающие ACE не учитываются, поэтому только WARN.
function Test-UsersCanWrite([string]$Path) {
    try { $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop } catch { return $null }
    $sids = @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4')
    foreach ($ace in $acl.Access) {
        if ("$($ace.AccessControlType)" -ne 'Allow') { continue }
        if (($ace.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        $sid = $null
        try { $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if ($sids -notcontains $sid) { continue }
        $rights = [int64]$ace.FileSystemRights
        # 0x2 = WriteData/CreateFiles; 0x40000000 GENERIC_WRITE, 0x10000000 GENERIC_ALL.
        if (($rights -band 0x2) -ne 0 -or ($rights -band 0x50000000) -ne 0) { return $true }
    }
    $false
}

# Записи «Программы и компоненты» с именем ClubShell*: бандл ClubShellSetup (есть QuietUninstallString), скрытый MSI из
# бандла, MSI без бандла (пилотные сборки CI) и Tauri-установщик одной оболочки (productName тоже «ClubShell»).
function Get-ClubShellEntries {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    @(Get-ChildItem -Path $keys -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { "$($_.DisplayName)" -like 'ClubShell*' })
}

# LogonUser с пустым паролем в C#: GetLastWin32Error из PowerShell после P/Invoke ненадёжен (между вызовами PowerShell
# сам делает системные вызовы и затирает код ошибки).
if (-not ('ClubKitPreflightNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClubKitPreflightNative
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // 0 when the empty password logs on (network logon, LOGON32_LOGON_NETWORK = 3), else the Win32 error of LogonUser.
    public static int TryEmptyPassword(string user)
    {
        IntPtr token;
        if (LogonUser(user, ".", "", 3, 0, out token)) { CloseHandle(token); return 0; }
        return Marshal.GetLastWin32Error();
    }
}
'@
}

# 1326 — неверный пароль (значит, он не пустой); 1327 — пустые пароли запрещены для входа не с консоли (пароль пустой);
# 1385, 1330, 1907 — пароль подошёл, но вход по сети запрещён / пароль истёк / его надо сменить (тоже пустой).
function Get-PasswordState([string]$User) {
    $code = [ClubKitPreflightNative]::TryEmptyPassword($User)
    switch ($code) {
        0 { return 'blank' }
        1326 { return 'set' }
        1327 { return 'blank' }
        1385 { return 'blank' }
        1330 { return 'blank' }
        1907 { return 'blank' }
        1909 { return 'locked' }
        default { return "unknown (Win32 error $code)" }
    }
}

$pcNumber = $null

# ======================================================================================================================
Write-Title 'This PC'
$build = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'CurrentBuildNumber'
$display = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'DisplayVersion'
if ([int]$build -ge 22000) { Write-Pass "Windows 11 $display, build $build" }
else { Write-Warn "Windows build $build ($display): the club PCs run Windows 11" }

if ($Golden) {
    Write-Info 'mode: golden (diskless master): ClubShell goes into the golden image, the seats get it with the publish'
    if ($env:COMPUTERNAME -eq 'USER') { Write-Pass 'Windows name USER (golden: every seat boots with it; numbers are set by hand in the kassa)' }
    else { Write-Fail "Windows name '$env:COMPUTERNAME' is not USER" 'the golden keeps the name USER: Rename-Computer USER, reboot, then run again' }
} elseif ($env:COMPUTERNAME -match '^PC-(\d{2})$') {
    $pcNumber = [int]$Matches[1]
    Write-Pass "Windows name $env:COMPUTERNAME (ClubShell takes number $pcNumber from it at the first registration)"
} else {
    Write-Fail "Windows name '$env:COMPUTERNAME' is not PC-NN" 'renumber first: number in the club-server panel, then Rename-Computer PC-NN, reboot (docs/shell-install.md)'
}
# Переименование без перезагрузки (Rename-Computer без -Restart): имя и личные диски ещё старого места, сверка номера
# ниже прошла бы, а ClubShell взял бы старый номер — один раз и навсегда. Та же проверка, что в get-shell-kit.ps1.
$pendingName = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
if ($pendingName -and $pendingName -ne $env:COMPUTERNAME) {
    Write-Fail "this PC was renamed to '$pendingName' but not rebooted yet (the Windows name is still $env:COMPUTERNAME)" 'reboot first, then run this check again'
}

$systemDisk = $null
try { $systemDisk = Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch { }
$bus = if ($systemDisk) { Get-BusName $systemDisk.BusType } else { '?' }
# Таргет загрузки бездиска (мастер или место). У гибрида сессии только к дискам игр (games-…, solo-…) и правке библиотек.
$bootSessions = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match ':(diskless-master|seat-\d+)$' })
if ($Golden) {
    # Эталон: только сеанс мастера. Обычное место (…:seat-NN) сбрасывает C: при каждой загрузке.
    $masterSessions = @($bootSessions | Where-Object { "$($_.TargetNodeAddress)" -match ':diskless-master$' })
    if (@($bootSessions | Where-Object { "$($_.TargetNodeAddress)" -match ':seat-\d+$' }).Count -gt 0) {
        Write-Fail "this PC boots as a normal diskless seat: its C: resets at every boot" 'panel "Diskless": master mode for this PC, reboot, run again'
    } elseif ($bus -eq 'iSCSI' -and $masterSessions.Count -gt 0) {
        Write-Pass "system disk is the golden image in master mode ($($masterSessions[0].TargetNodeAddress))"
    } else {
        Write-Fail "golden mode, but the system disk is '$bus' without a ':diskless-master' session" 'run it on the diskless master (panel "Diskless" -> master mode)'
    }
    # Правка библиотеки G: (club-master) или H: (club-master-solo) — та же проверка, что в seal-golden.ps1.
    if (@(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match ':club-master(-[a-z0-9]+)?$' -and $_.IsConnected }).Count -gt 0) {
        Write-Warn 'library editing is open (club-master / club-master-solo session)' 'panel: finish the library editing first (seal-golden refuses it too)'
    }
} elseif ($bus -eq 'iSCSI' -or $bootSessions.Count -gt 0) {
    Write-Fail "system disk $env:SystemDrive is diskless (iSCSI): this kit is for hybrids only" 'diskless: master mode in the panel, then get-shell-kit on the master (golden mode, docs/shell-install.md "Diskless")'
} elseif ($LocalBusTypes -contains $bus) {
    Write-Pass "system disk is local ($bus): hybrid"
} else {
    Write-Warn "system disk bus is '$bus': not a known internal bus"
}

$cVolume = Get-Volume -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction SilentlyContinue
if ($cVolume) {
    $freeGb = [math]::Round($cVolume.SizeRemaining / 1GB, 1)
    if ($freeGb -ge 5) { Write-Pass "free space on ${env:SystemDrive}: $freeGb GB" }
    elseif ($freeGb -ge 1) { Write-Warn "free space on ${env:SystemDrive}: $freeGb GB (ClubShell needs about 0.5 GB, keep 5 GB)" }
    else { Write-Fail "free space on ${env:SystemDrive}: $freeGb GB" 'free at least 1 GB' }
}

# Ожидающая перезагрузка: MSI тогда может ответить 3010 или 1618 (идёт другая установка).
$rebootPending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
    (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
# Эталон — FAIL: перезагрузка после kiosk-on загрузила бы мастер в киоск player, поэтому только до установки.
if ($rebootPending -and $Golden) { Write-Fail 'Windows waits for a reboot (updates/servicing)' 'reboot the master now (before the shell), sign in as the golden admin, run again' }
elseif ($rebootPending) { Write-Warn 'Windows waits for a reboot (updates/servicing)' 'reboot, then run this check again' }
else { Write-Pass 'no reboot pending' }
if (Get-Process -Name msiexec -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }) {
    Write-Warn 'an msiexec window is open (another install may be running)' 'finish or close it first (error 1618 otherwise)'
}

# Smart App Control: 1 — включён (не даст запустить неподписанные ClubShellAgent.exe и clubshell-shell.exe), 2 — оценка.
$sac = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' 'VerifiedAndReputablePolicyState'
if ($sac -eq 1) { Write-Fail 'Smart App Control is ON: the unsigned ClubShell 1.0.18 would be blocked' 'Windows Security -> App & browser control -> Smart App Control -> Off' }
elseif ($sac -eq 2) { Write-Warn 'Smart App Control is in evaluation mode: it may switch on and block the unsigned ClubShell' 'Windows Security -> App & browser control -> Smart App Control -> Off' }
else { Write-Pass 'Smart App Control is off' }

$here = $PSScriptRoot
if ($here) {
    $hereDrive = $null
    try { $hereDrive = Get-Volume -DriveLetter $here.Substring(0, 1) -ErrorAction Stop } catch { }
    if ($hereDrive -and "$($hereDrive.DriveType)" -eq 'Removable') {
        Write-Info "running from a removable drive ($($here.Substring(0, 2))): shell-install-run-as-admin.cmd copies the kit to $KitDir first"
    }
}

# Версия помощника по SHA256 его exe ($Hash): helper-build.json ({"v":1,"version":"x.y.z","build":"<sha256>"}, пишет сама
# служба 1.8.0+ при каждом старте) — только если build = SHA256 exe; иначе таблица $HelperBuilds; иначе $null (неизвестная
# сборка). Версию файла exe не читаем: exe собирается на Linux, и ресурс версии в нём — от среды .NET (10.0.x), а не от
# помощника. Та же функция — в shell-install.ps1 и shell-uninstall.ps1 (менять вместе).
function Get-HelperVersion([string]$Hash) {
    if (-not $Hash) { return $null }
    $sha = $Hash.ToLowerInvariant()
    try {
        $info = [IO.File]::ReadAllText($HelperBuildFile) | ConvertFrom-Json
        if ($info.v -eq 1 -and "$($info.build)".ToLowerInvariant() -eq $sha -and "$($info.version)" -match '^\d+\.\d+\.\d+$') { return "$($info.version)" }
    } catch { }
    if ($HelperBuilds.ContainsKey($sha)) { return $HelperBuilds[$sha] }
    $null
}

# ======================================================================================================================
Write-Title 'Club diskless helper, G: and H:'
$helper = Get-CimInstance -ClassName Win32_Service -Filter "Name='ClubDisklessHelper'" -ErrorAction SilentlyContinue
if (-not $helper) {
    Write-Fail 'service ClubDisklessHelper is missing: no G:/H: games' $UpdateHelperCmd
} else {
    if ($helper.State -eq 'Running' -and $helper.StartMode -eq 'Auto') { Write-Pass 'service ClubDisklessHelper: running, automatic' }
    else { Write-Fail "service ClubDisklessHelper: $($helper.State), start $($helper.StartMode)" $UpdateHelperCmd }
    $helperExe = [Environment]::ExpandEnvironmentVariables(($helper.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim())
    if ($helperExe -ne $HelperExePath) {
        Write-Fail "service ClubDisklessHelper runs $helperExe, not $HelperExePath" $UpdateHelperCmd
    } elseif (Test-Path -LiteralPath $helperExe) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $helperExe).Hash
        $helperVersion = Get-HelperVersion $hash
        if (-not $helperVersion) { Write-Fail "unknown helper build: SHA256 $hash ($helperExe)" $UpdateHelperCmd }
        elseif ([version]$helperVersion -lt $HelperMinVersion) { Write-Fail "helper $helperVersion is older than $HelperMinVersion ($helperExe)" $UpdateHelperCmd }
        else { Write-Pass "helper exe is $helperVersion ($helperExe)" }
    } else {
        Write-Fail "helper exe not found: $helperExe" $UpdateHelperCmd
    }
}

function Get-LibraryVolume([string]$Letter) {
    $volume = Get-Volume -DriveLetter $Letter -ErrorAction SilentlyContinue
    if (-not $volume) { return $null }
    $disk = $null
    try { $disk = Get-Partition -DriveLetter $Letter -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch { }
    $diskBus = if ($disk) { Get-BusName $disk.BusType } else { '?' }
    [pscustomobject]@{ Label = "$($volume.FileSystemLabel)"; Bus = $diskBus; FreeGb = [math]::Round($volume.SizeRemaining / 1GB) }
}

# G: — «Online Games» (раньше ClubGames), H: — «Offline Games» (помощник 1.6.0); оба — личные диски места по iSCSI.
foreach ($lib in @(@{ Letter = 'G'; Labels = @('Online Games', 'ClubGames') }, @{ Letter = 'H'; Labels = @('Offline Games') })) {
    $volume = Get-LibraryVolume $lib.Letter
    if (-not $volume -and $Golden) {
        # Мастер: личные диски — того ПК, на котором он сейчас; шелл их не трогает, места подключают свои.
        Write-Warn "$($lib.Letter): is not mounted in this master session" 'wait 1-2 minutes after boot (the helper mounts it); the seats mount their own disks'
    } elseif (-not $volume) {
        Write-Fail "$($lib.Letter): is not mounted" 'wait 1-2 minutes after boot (the helper mounts it), check the panel "Diskless", run again'
    } elseif ($lib.Labels -contains $volume.Label -and $volume.Bus -eq 'iSCSI') {
        Write-Pass "$($lib.Letter): '$($volume.Label)' over iSCSI, $($volume.FreeGb) GB free"
    } else {
        Write-Fail "$($lib.Letter): is '$($volume.Label)' on bus $($volume.Bus), not the club library" 'Disk Management: move that volume to another letter (not G: or H:)'
    }
}

# Номер личных дисков — по имени таргета (games-seat-NN для G:, solo-seat-NN для H:). Не совпал с PC-NN — ПК
# перенумерован наполовину: касса взяла бы номер из имени Windows, а диски — другого места.
$seatTargets = @(Get-IscsiSession -ErrorAction SilentlyContinue | ForEach-Object { "$($_.TargetNodeAddress)" } | Where-Object { $_ -match ':(games|solo)-seat-(\d+)$' })
if ($seatTargets.Count -eq 0 -and $Golden) {
    Write-Info 'no personal disk session (games-seat-NN / solo-seat-NN) in this master session'
} elseif ($seatTargets.Count -eq 0) {
    Write-Warn 'no personal disk session (games-seat-NN / solo-seat-NN) found' 'check the panel "Diskless" for this PC'
}
foreach ($target in $seatTargets) {
    $null = $target -match ':(games|solo)-seat-(\d+)$'
    $seat = [int]$Matches[2]
    # Эталон: номер в имени Windows не нужен (USER), диски — того ПК, на котором сейчас мастер.
    if ($Golden) { Write-Info "personal $($Matches[1]) disk of seat $seat ($target): the PC that runs the master now" }
    elseif ($pcNumber -and $seat -eq $pcNumber) { Write-Pass "personal $($Matches[1]) disk is seat $seat ($target)" }
    else { Write-Fail "personal $($Matches[1]) disk is seat $seat, Windows name is $env:COMPUTERNAME" 'renumber in the panel and rename Windows to the same PC-NN first' }
}

if (Test-Path -LiteralPath 'G:\Steam\steam.exe') { Write-Pass 'Steam: G:\Steam\steam.exe' }
elseif ($Golden) { Write-Warn 'G:\Steam\steam.exe not found in this master session (the kassa catalog and agent.json use it)' 'check G: on a seat after the publish' }
else { Write-Fail 'G:\Steam\steam.exe not found (the kassa catalog and agent.json use it)' 'check G: (Online Games library)' }
if (Get-Service -Name 'Steam Client Service' -ErrorAction SilentlyContinue) { Write-Pass 'service "Steam Client Service" exists (Steam for a standard user)' }
else { Write-Warn 'service "Steam Client Service" is missing: Steam as a standard user cannot update games' 'start G:\Steam\steam.exe once as Club (it installs the service)' }

# Игрок в киоске — обычный пользователь: игре, которая пишет в свою папку (конфиги, обновления), нужна запись.
foreach ($path in @('G:\Steam', 'G:\Steam\steamapps')) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $canWrite = Test-UsersCanWrite $path
    if ($canWrite) { Write-Pass "Users can write to $path" }
    elseif ($null -eq $canWrite) { Write-Warn "ACL of $path is unreadable" }
    else { Write-Warn "Users cannot write to ${path}: Steam as player may fail to update" 'give BUILTIN\Users Modify in the library master (master mode), not on the seat' }
}
$hDirs = @(Get-ChildItem -LiteralPath 'H:\' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^(\$RECYCLE\.BIN|System Volume Information|ClubShell)$' } | Select-Object -First 60)
if ($hDirs.Count -gt 0) {
    $readOnly = @($hDirs | Where-Object { -not (Test-UsersCanWrite $_.FullName) } | ForEach-Object { $_.Name })
    if ($readOnly.Count -eq 0) { Write-Pass "Users can write to all $($hDirs.Count) game folders on H:" }
    else { Write-Warn ("Users cannot write to {0} of {1} folders on H: ({2})" -f $readOnly.Count, $hDirs.Count, (($readOnly | Select-Object -First 8) -join ', ')) 'games that save next to their exe fail as player: give BUILTIN\Users Modify in the H: master (master mode)' }
}
if (Test-Path -LiteralPath 'C:\Riot Games') {
    if (Test-UsersCanWrite 'C:\Riot Games') { Write-Pass 'Users can write to C:\Riot Games' }
    else { Write-Warn 'Users cannot write to C:\Riot Games: the Riot client may fail to update as player' 'icacls "C:\Riot Games" /grant *S-1-5-32-545:(OI)(CI)M' }
}

# ======================================================================================================================
Write-Title 'Accounts'
$adminSids = Get-GroupMemberSids 'S-1-5-32-544'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($me -like "*\$AdminUser") { Write-Pass "signed in as $me" }
else { Write-Warn "signed in as $me, not $AdminUser" "sign in as $AdminUser (hold Shift at boot to skip auto-logon)" }

$admin = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
# Пароль администратора эталона — из набора, -AdminUser с его именем (у лаунчера .cmd имя всегда Club).
$goldenPasswordCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\set-club-password.ps1 -AdminUser '$AdminUser'"
if (-not $admin) {
    if ($Golden) { Write-Fail "local account $AdminUser is missing" 'golden mode takes the signed-in admin: sign in as the golden admin and run get-shell-kit again' }
    else { Write-Fail "local account $AdminUser is missing" 'this kit expects the hybrid images h1d/h1e (admin "Club")' }
} else {
    if ($admin.Enabled -and ($adminSids -contains $admin.SID.Value)) { Write-Pass "$AdminUser is an enabled administrator (our maintenance account: keep it)" }
    else { Write-Fail "$AdminUser is not an enabled member of Administrators" }
    if ($Golden -and $admin.SID.Value -match '-500$') {
        Write-Fail "$AdminUser is the built-in Administrator" 'sign in as the golden''s own admin account (the built-in one stays disabled)'
    }
    # Учётка Microsoft: пароль — от аккаунта в интернете, на экране входа мест (Shift) нужен он; set-club-password ей не подходит.
    # Эталон: её вход и токены ушли бы в образ, снимки и резервные копии; seal-golden такой эталон не закроет.
    if ($Golden -and "$($admin.PrincipalSource)" -eq 'MicrosoftAccount') {
        Write-Fail "$AdminUser is a Microsoft account: its sign-in and tokens would go into the image, snapshots and backups; Shift at every seat asks for its online password" ('create a local admin with its own password (Settings -> Accounts -> Other users), sign in as it, ' +
            "delete $AdminUser with its data (same place -> Remove), run get-shell-kit again")
    }
    $state = Get-PasswordState $AdminUser
    if ($state -eq 'set') { Write-Pass "$AdminUser has a password" }
    elseif ($state -eq 'blank' -and $Golden) { Write-Fail "$AdminUser has an EMPTY password: every seat copies the golden, Shift at boot would give anyone an admin desktop" "set it first: $goldenPasswordCmd" }
    elseif ($state -eq 'blank') { Write-Warn "$AdminUser has an EMPTY password: with the kiosk, Shift at boot gives anyone an admin desktop" 'set it before players arrive: set-club-password-run-as-admin.cmd (shell-install offers it)' }
    else { Write-Warn "$AdminUser password state: $state" }
    if ($null -eq $admin.PasswordExpires) { Write-Pass "$AdminUser password never expires" }
    elseif ($Golden) { Write-Warn ("{0} password expires {1:yyyy-MM-dd}: then Shift at the seats stops working" -f $AdminUser, $admin.PasswordExpires) "$goldenPasswordCmd (it also sets `"never expires`")" }
    else { Write-Warn ("{0} password expires {1:yyyy-MM-dd}: image older than h1d (h1b/h1c)?" -f $AdminUser, $admin.PasswordExpires) 'reimage to h1e BEFORE the shell (a reimage wipes the shell); set-club-password also sets "never expires"' }
}

# Задачи планировщика под Club с сохранённым паролем перестанут запускаться после смены пароля.
$storedTasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { "$($_.Principal.UserId)" -match "(^|\\)$([regex]::Escape($AdminUser))$" -and "$($_.Principal.LogonType)" -eq 'Password' })
if ($storedTasks.Count -gt 0) {
    Write-Warn ("tasks with a stored $AdminUser password: {0}" -f (($storedTasks | ForEach-Object { "$($_.TaskPath)$($_.TaskName)" }) -join ', ')) 'after set-club-password re-enter the password in those tasks'
}

if ($KioskUser -ieq $AdminUser) {
    Write-Fail "kiosk account '$KioskUser' is our admin '$AdminUser' (Windows ignores case)" 'use KioskUser player (never club)'
} else {
    $kiosk = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if (-not $kiosk) {
        Write-Pass "no local account '$KioskUser' yet: the agent creates it (standard user, random password)"
    } else {
        $groups = @(Get-PrivilegedGroupsOf $kiosk.SID.Value)
        if ($groups.Count -gt 0) { Write-Fail "'$KioskUser' is in privileged groups ($($groups -join ', ')): the agent refuses it and the kiosk never starts" "remove '$KioskUser' from those groups or delete the account" }
        else { Write-Warn "local account '$KioskUser' exists (standard user): the agent takes it over (new random password, enabled, added to Users)" }
    }
}

foreach ($user in @(Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -and $_.Name -ne $AdminUser -and $_.Name -ne $KioskUser })) {
    $isAdmin = $adminSids -contains $user.SID.Value
    # Эталон: каждая включённая учётка — на экране входа всех мест (Shift при загрузке); seal-golden.ps1 перед каждой
    # публикацией не пропустит встроенную, второго администратора и пустой пароль — исправить до установки.
    if ($Golden) {
        $userName = $user.Name
        if ($user.SID.Value -match '-500$') { Write-Fail "built-in $userName is enabled: every seat would get it" "net user `"$userName`" /active:no" }
        elseif ($isAdmin) { Write-Fail "second administrator '$userName': it and its password would be on every seat" "Disable-LocalUser -Name '$userName' (the golden has one admin: $AdminUser)" }
        else {
            $userState = Get-PasswordState $userName
            if ($userState -eq 'blank') { Write-Fail "enabled account '$userName' has an EMPTY password: Shift at every seat would give a desktop outside the kiosk" "Disable-LocalUser -Name '$userName' (or give it a password)" }
            elseif ($userState -eq 'set') { Write-Info "other account: $userName (standard user with a password; on the sign-in screen of every seat)" }
            else { Write-Warn "enabled account '$userName': password state $userState" "Disable-LocalUser -Name '$userName' if it is not needed (seal-golden stops on it)" }
        }
        continue
    }
    if ($user.SID.Value -match '-500$') { Write-Warn "built-in Administrator is enabled" 'net user Administrator /active:no (or give it a password)' }
    elseif ($isAdmin -and (Get-PasswordState $user.Name) -eq 'blank') { Write-Warn "administrator '$($user.Name)' has an EMPTY password" "set a password or disable '$($user.Name)'" }
    elseif ($isAdmin) { Write-Info "other administrator: $($user.Name)" }
}

$autoLogon = Get-RegValue $WinlogonKey 'AutoAdminLogon'
$defaultUser = Get-RegValue $WinlogonKey 'DefaultUserName'
Write-Info "auto-logon now: AutoAdminLogon=$autoLogon, DefaultUserName=$defaultUser (ClubShell switches it to $KioskUser)"

# ======================================================================================================================
Write-Title 'ClubShell must not be installed yet'
$found = $false
if (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue) {
    $found = $true
    Write-Fail 'service ClubShellAgent exists' 'run shell-uninstall-run-as-admin.cmd first'
}
foreach ($entry in Get-ClubShellEntries) {
    $found = $true
    $kind = if ($entry.QuietUninstallString -or $entry.BundleUpgradeCode) { 'bundle' } elseif ($entry.WindowsInstaller -eq 1) { "MSI $($entry.PSChildName)" } else { 'other' }
    Write-Fail ("installed: {0} {1} ({2}, {3})" -f $entry.DisplayName, $entry.DisplayVersion, $kind, $entry.Publisher) 'run shell-uninstall-run-as-admin.cmd first (it also removes MSI-only pilot builds)'
}
if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\ClubShell') {
    $found = $true
    Write-Fail ("HKLM\SOFTWARE\ClubShell exists (version {0}, kiosk {1})" -f (Get-RegValue 'HKLM:\SOFTWARE\ClubShell' 'Version'), (Get-RegValue 'HKLM:\SOFTWARE\ClubShell' 'KioskUser')) 'run shell-uninstall-run-as-admin.cmd first'
}
$dataDir = Join-Path $env:ProgramData 'ClubShell'
if (Test-Path -LiteralPath $dataDir) {
    $found = $true
    if (@(Get-ChildItem -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Fail "$dataDir has files of an earlier install (settings, registration)" 'shell-uninstall-run-as-admin.cmd and answer "y" to removing ClubShell data'
    } else {
        Write-Warn "$dataDir exists (empty)" "Remove-Item '$dataDir'"
    }
}
if (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'ClubShell')) {
    $found = $true
    Write-Warn "$env:ProgramFiles\ClubShell exists (leftovers)" 'shell-uninstall-run-as-admin.cmd'
}
if (Get-ScheduledTask -TaskPath '\ClubShell\' -ErrorAction SilentlyContinue) {
    $found = $true
    Write-Warn 'scheduled tasks under \ClubShell\ exist (old pilot: enable-kiosk-autologon.ps1)' 'shell-uninstall-run-as-admin.cmd removes them'
}
if (-not $found) { Write-Pass 'no ClubShell on this PC' }

# ======================================================================================================================
Write-Title 'Server, network, prerequisites'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri "$($ServerUrl.TrimEnd('/'))/health" -TimeoutSec 20
    if ($response.StatusCode -eq 200 -and "$($response.Content)" -match '"ok"') { Write-Pass "ClubShell server answers: $ServerUrl/health" }
    else { Write-Fail "ClubShell server answered $($response.StatusCode) on /health" }
    # Часы: агент и сервер сверяют время (clockSkewToleranceSec 300); заголовок Date — время сервера.
    $date = $response.Headers['Date']
    if ($date) {
        $serverUtc = [DateTime]::Parse($date, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        $skew = [math]::Round([math]::Abs(((Get-Date).ToUniversalTime() - $serverUtc).TotalSeconds))
        if ($skew -le 120) { Write-Pass "clock differs from the server by $skew s" }
        else { Write-Warn "clock differs from the server by $skew s" 'Settings -> Time -> Sync now (time zone Tashkent)' }
    }
} catch {
    Write-Fail "ClubShell server $ServerUrl does not answer: $($_.Exception.Message)" 'check internet on this PC: the agent needs the server all the time'
}

$webView = Get-RegValue 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' 'pv'
# Свой загрузчик WebView2 у бандла 1.0.18 падает (0x80091007): shell-install ставит WebView2 заранее install-webview2.ps1
# из набора — тогда это не WARN.
$webViewScripts = @($KitDir, $here) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'install-webview2.ps1' } | Where-Object { Test-Path -LiteralPath $_ }
if ($webView -and $webView -ne '0.0.0.0') { Write-Pass "WebView2 runtime $webView" }
elseif ($webViewScripts) { Write-Info 'WebView2 runtime not registered: shell-install installs it first (install-webview2.ps1, needs internet)' }
else { Write-Warn 'WebView2 runtime not registered and install-webview2.ps1 is not in the kit: the 1.0.18 setup fails with 0x80091007' 'put the whole kit into C:\ClubShellKit (get-shell-kit), shell-install installs WebView2 first' }

# DNS: политика кассы с пустым dnsServers адаптеры не трогает; shell-install сверит этот список после установки.
Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.ServerAddresses } | ForEach-Object {
    Write-Info ("DNS {0}: {1}" -f $_.InterfaceAlias, ($_.ServerAddresses -join ', '))
}

$usbStart = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start'
Write-Info "USBSTOR Start=$usbStart now (3 = USB sticks work); right after the install the agent sets 4 for the whole PC"

$profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
$notifyOff = $profiles.Count -gt 0 -and @($profiles | Where-Object { "$($_.NotifyOnListen)" -ne 'False' }).Count -eq 0
$lanNames = @(Get-NetFirewallRule -Group 'Club LAN games' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
# Правила текущего install-lan-firewall.ps1 ($Rules): без Warcraft III — набор старый, скрипт запускается снова.
$lanMissing = @(@('Club-LAN-Games-UDP', 'Club-LAN-WC3-UDP', 'Club-LAN-WC3-TCP') | Where-Object { $lanNames -notcontains $_ })
if ($notifyOff -and $lanMissing.Count -eq 0) { Write-Pass 'firewall for LAN games is set (install-lan-firewall.ps1)' }
elseif ($notifyOff -and $lanNames.Count -gt 0) { Write-Warn "firewall for LAN games is old (missing $($lanMissing -join ', ')): Warcraft III LAN games are blocked" $LanFirewallCmd }
else { Write-Warn 'firewall for LAN games is not set: as a standard user every "allow access" prompt becomes a block rule' $LanFirewallCmd }

if (Get-ScheduledTask -TaskName 'ClubMaxRefresh' -ErrorAction SilentlyContinue) { Write-Pass 'ClubMaxRefresh task exists (monitor refresh rate at every logon, player too)' }
else { Write-Warn 'ClubMaxRefresh task is missing (monitors stay at 60 Hz for the player)' 'irm http://192.168.1.201:5080/pxe/v1/files/tools/install-max-refresh.ps1 | iex' }

$setupFound = $null
foreach ($dir in @($KitDir, $here, (Join-Path $env:USERPROFILE 'Downloads'))) {
    if (-not $dir) { continue }
    $candidate = Join-Path $dir $SetupName
    if (Test-Path -LiteralPath $candidate) { $setupFound = $candidate; break }
}
if (-not $setupFound) {
    Write-Warn "$SetupName not found in $KitDir, next to this script or in Downloads" 'put it into the kit folder (shell-install can also download it from GitHub)'
} elseif ((Get-Item -LiteralPath $setupFound).Length -eq $SetupSize -and (Get-FileHash -Algorithm SHA256 -LiteralPath $setupFound).Hash -eq $SetupSha256) {
    Write-Pass "installer ${setupFound}: size and SHA256 of release v1.0.18"
} else {
    Write-Fail "installer $setupFound is not the official v1.0.18 file (size or SHA256 differ)" 'download ClubShellSetup-1.0.18.exe again from the v1.0.18 release'
}

# ======================================================================================================================
Write-Host ''
$color = if ($Count.Fail -gt 0) { 'Red' } elseif ($Count.Warn -gt 0) { 'Yellow' } else { 'Green' }
Write-Host ("Result for {0}: {1} FAIL, {2} WARN" -f $env:COMPUTERNAME, $Count.Fail, $Count.Warn) -ForegroundColor $color
if ($Count.Fail -gt 0) { Write-Host 'Fix every FAIL before installing ClubShell (docs/shell-install.md).' -ForegroundColor Red }
$code = if ($Count.Fail -gt 0) { 2 } elseif ($Count.Warn -gt 0) { 1 } else { 0 }
# exit только при запуске файлом: в irm | iex он закрыл бы окно PowerShell.
if ($PSCommandPath) { exit $code }
