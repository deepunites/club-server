<#
.SYNOPSIS
    Включает киоск ClubShell на гибриде, когда ClubShell стоит и ПК одобрен в кассе: автовход в «player» и шелл вместо
    рабочего стола — и в каждом профиле игрока, который Windows пересоздаёт после сброса.
.DESCRIPTION
    Запускать на гибриде под «Club», от администратора: C:\ClubShellKit\kiosk-on-run-as-admin.cmd. shell-install.ps1
    запускает его сам, когда дождался одобрения (с -NoReboot: о перезагрузке спрашивает уже он).
    Зачем: агент 1.0.18 после одобрения не создал профиль киоска и не переключил автовход (PC-01, 2026-11-01), а после
    пароля Club ПК вставал на экране входа — автовход по-прежнему шёл в Club со старым пустым паролем. Агент удаляет
    профиль player после каждой сессии (resetProfileOnLogout), Windows создаёт его заново из C:\Users\Default, где шелла
    нет, — у игрока появлялись Пуск и окно «Автозапуск» поверх киоска.
    Что делает, по порядку:
      0. ClubShell стоит: служба ClubShellAgent и оболочка clubshell-shell.exe; три скрипта шагов 3-5 лежат в наборе;
         Club — включённый администратор (иначе после перезагрузки в киоск администратора на ПК не осталось бы:
         встроенный Administrator выключен). Чего-то нет — стоп, ничего не изменено;
      1. одобрение — по журналам агента (три последних agent-*.json, Serilog compact JSON, как Get-AgentLogState в
         shell-install.ps1): «Registered as PC» — одобрен; «Stored agent tokens refreshed» — тоже (после первой
         регистрации агент при старте только обновляет токены, «Registered» в новых журналах уже нет); «Registration
         attempt failed» с pendingApproval — ждёт одобрения; строк нет, но есть secure\agent.tokens — одобрен (токены
         выдаются только одобренному ПК). Не одобрен — что сделать в кассе и вопрос, продолжать ли (по умолчанию нет:
         до одобрения агент применяет политику из MSI — Agent\config\policies.example.json, замена оболочки в ней
         включена, — и киоск включился бы без кассы: ПК в ней ещё ждёт одобрения, сеанс на нём не начать);
      2. пароль Club: пустой (вход с пустым паролем, как в shell-preflight.ps1) — предлагает задать его
         (set-club-password.ps1); остался пустым — стоп: с киоском Shift при загрузке даёт экран входа, и Club без
         пароля — рабочий стол администратора для любого;
      3. disable-autoplay.ps1 — окно «Автозапуск» при подключении G:/H: не появляется ни у кого;
      4. seal-default-profile.ps1 — шелл ClubShell в шаблоне C:\Users\Default. ДО создания профиля player: и первый
         профиль киоска, и каждый пересозданный после сброса сразу стартуют с шеллом, а не с explorer;
      5. enable-kiosk-autologon.ps1 -KioskUser player -NoReboot — скрипт разработчика шелла (club-shell
         tools/scripts, копия без правок, вывод по-русски; по умолчанию у него киоск «club» = наш Club, поэтому
         -KioskUser всегда передаётся): создаёт профиль player (CreateProfile), убирает то, что мешает автовходу,
         перезапускает агента и ждёт в его журнале «Policy section shellReplacement applied» — шелл в профиль и автовход
         пишет сам агент (без профиля он падал: PC-01);
      6. проверяет Winlogon: AutoAdminLogon=1, DefaultUserName=player;
      7. без -NoReboot спрашивает, перезагрузить ли ПК в киоск (по умолчанию да).
    Шаги 3-5 — отдельные powershell -File; ошибка шага — стоп с подсказкой, следующие шаги не выполняются. Повторный
    запуск безопасен: все три скрипта повторяемы (enable-kiosk-autologon после включённого киоска только перезапускает
    агента).
    Журнал — C:\ClubShellKit\logs\kiosk-on-<ПК>-<время>.txt; вывод дочерних скриптов в журнал не попадает (идёт прямо в
    консоль) — при ошибке сфотографировать окно. Код выхода при запуске файлом: 0 — киоск включён, 2 — нет.
    Строки вывода — только ASCII, без BOM и без #Requires (как у остальных скриптов набора).
.EXAMPLE
    kiosk-on-run-as-admin.cmd
#>
param(
    [string]$KitDir = 'C:\ClubShellKit',
    [string]$KioskUser = 'player',
    [switch]$NoReboot
)
$ErrorActionPreference = 'Stop'

$AdminUser = 'Club'
# Путь из политики кассы (shellReplacement.shellExe) — тот же, что в seal-default-profile.ps1.
$ShellExe = 'C:\Program Files\ClubShell\Shell\clubshell-shell.exe'
$DataDir = Join-Path $env:ProgramData 'ClubShell'
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$DiagCmd = Join-Path $KitDir 'shell-agent-diag-run-as-admin.cmd'
$SelfCmd = Join-Path $KitDir 'kiosk-on-run-as-admin.cmd'
$GetKitCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/get-shell-kit.ps1 | iex'
$StepScripts = @('disable-autoplay.ps1', 'seal-default-profile.ps1', 'enable-kiosk-autologon.ps1')

function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "      $Text" }
function Write-Note([string]$Text) { Write-Host "WARN  $Text" -ForegroundColor Yellow }

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

# Пустой ли пароль — входом с пустым паролем (LogonUser, сетевой вход), как в shell-preflight.ps1. В C#:
# GetLastWin32Error из PowerShell после P/Invoke ненадёжен. Пароль есть — неверная попытка идёт в счётчик блокировки
# Windows (порог — 10 за 10 минут): одна на запуск.
if (-not ('ClubKitKioskNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClubKitKioskNative
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
    switch ([ClubKitKioskNative]::TryEmptyPassword($User)) {
        0 { return 'blank' }
        1326 { return 'set' }
        1327 { return 'blank' }
        1385 { return 'blank' }
        1330 { return 'blank' }
        1907 { return 'blank' }
        1909 { return 'locked' }
        default { return "unknown (Win32 error $_)" }
    }
}

# Строка журнала разбирается JavaScriptSerializer, а не ConvertFrom-Json: после первой регистрации агент добавляет к
# каждому событию "pcId", и строки с {PcId} в шаблоне несут оба ключа — ConvertFrom-Json в 5.1 такие отвергает (ключи
# различаются только регистром). Словарь сериализатора различает регистр.
Add-Type -AssemblyName System.Web.Extensions
$JsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer

# Регистрация по журналам агента (шаблон сообщения — "@mt", исключение — "@x"); решает последняя подходящая строка.
# Сначала грубый фильтр по тексту: разбор тысяч строк в 5.1 медленный. Строки журнала не печатаются.
function Get-Registration {
    $state = @{ State = 'none'; Detail = '' }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $DataDir 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 3)
    foreach ($log in $logs) {
        foreach ($line in @(Get-Content -LiteralPath $log.FullName -Tail 3000 -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            if ($line -notmatch '"@mt"\s*:\s*"(Registered as PC|Stored agent tokens refreshed|Registration attempt)') { continue }
            try { $entry = $JsonReader.DeserializeObject($line) } catch { continue }
            if ($entry -isnot [System.Collections.IDictionary]) { continue }
            $template = [string]$entry['@mt']
            if ($template -like 'Registered as PC*') { $state.State = 'registered'; $state.Detail = "name $($entry['PcName']), zone $($entry['Zone'])" }
            elseif ($template -like 'Stored agent tokens refreshed*') { $state.State = 'registered'; $state.Detail = 'agent tokens refreshed' }
            elseif ($template -like 'Registration attempt*failed*') {
                $exception = [string]$entry['@x']
                if ($exception -match 'waiting for approval|pendingApproval') { $state.State = 'waiting'; $state.Detail = '' }
                elseif ($exception -match 'X-Club-Key') { $state.State = 'badKey'; $state.Detail = '' }
                else {
                    $first = (($exception -split "`n")[0]).Trim()
                    if ($first.Length -gt 160) { $first = $first.Substring(0, 160) }
                    $state.State = 'failing'; $state.Detail = $first
                }
            }
        }
    }
    if ($state.State -eq 'none' -and (Test-Path -LiteralPath (Join-Path $DataDir 'secure\agent.tokens'))) {
        $state.State = 'registered'; $state.Detail = 'agent tokens present'
    }
    $state
}

# Скрипт набора отдельным процессом: его $ErrorActionPreference и exit не задевают этот скрипт; код выхода 0 — успех
# (throw в скрипте при -File даёт 1).
function Invoke-KitScript([string]$Name, [string[]]$Arguments, [string]$Fix) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $KitDir $Name) @Arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "$Name failed (exit code $code, see the lines above). $Fix" }
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$LogDir = Join-Path $KitDir 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
try { Start-Transcript -LiteralPath (Join-Path $LogDir "kiosk-on-$env:COMPUTERNAME-$stamp.txt") | Out-Null } catch { }

$kioskOn = $false
try {
    Write-Host "ClubShell kiosk on $env:COMPUTERNAME (kiosk account '$KioskUser')" -ForegroundColor Cyan
    if ($KioskUser -ieq $AdminUser) { throw "Kiosk account '$KioskUser' is the admin account '$AdminUser'. Nothing was changed." }

    # --- 0. ClubShell и набор ----------------------------------------------------------------------------------------
    Write-Title "Step 0: ClubShell, the kit and the $AdminUser account"
    if (-not (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)) {
        throw 'Service ClubShellAgent is missing: install ClubShell first (shell-install). Nothing was changed.'
    }
    if (-not (Test-Path -LiteralPath $ShellExe)) { throw "$ShellExe is missing: install ClubShell first (shell-install). Nothing was changed." }
    Write-Pass 'ClubShell is installed (service ClubShellAgent, kiosk shell)'
    $missing = @($StepScripts | Where-Object { -not (Test-Path -LiteralPath (Join-Path $KitDir $_)) })
    if ($missing.Count -gt 0) {
        throw "Missing in ${KitDir}: $($missing -join ', '). Get the kit again ($GetKitCmd). Nothing was changed."
    }
    Write-Pass "kit scripts found in $KitDir"
    # Club — единственный администратор гибрида (встроенный выключен): не включать киоск, если он им быть перестал.
    $adminAccount = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
    if (-not $adminAccount -or -not $adminAccount.Enabled -or (Get-GroupMemberSids 'S-1-5-32-544') -notcontains $adminAccount.SID.Value) {
        throw ("$AdminUser is not an enabled administrator: after the reboot into the kiosk no admin would be left on this PC. " +
            "Computer Management -> Local Users and Groups: enable $AdminUser and add it to Administrators. Nothing was changed.")
    }
    Write-Pass "$AdminUser is an enabled administrator"

    # --- 1. Одобрение в кассе ----------------------------------------------------------------------------------------
    Write-Title 'Step 1: approval in the kassa'
    $registration = Get-Registration
    switch ($registration.State) {
        'registered' { Write-Pass "agent log: the PC is registered and approved ($($registration.Detail))" }
        'waiting' { Write-Note 'agent log: the PC waits for approval in the kassa' }
        'badKey' {
            Write-Note 'agent log: the server rejected the PC key (X-Club-Key): the key of another club?'
            Write-Info "fix: powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\shell-set-key.ps1 (the key of club Deagle), then approve"
        }
        'failing' { Write-Note "agent log: registration fails, the agent keeps retrying: $($registration.Detail)" }
        default { Write-Note "agent log: no registration yet ($DataDir\logs)" }
    }
    if ($registration.State -ne 'registered') {
        $number = if ($env:COMPUTERNAME -match '^PC-(\d{2})$') { [int]$Matches[1] } else { '?' }
        Write-Info "Approve it in the kassa: 'Zal i ustroystva' -> $env:COMPUTERNAME (number $number) -> its zone, then 'Obsluzhivanie' (maintenance) OFF."
        # До одобрения агент живёт на политике из MSI (замена оболочки в ней включена): киоск включился бы и без кассы.
        Write-Info 'Without the approval the kiosk would start on the default policy of the installer, and the kassa could not'
        Write-Info 'start a session on this PC (it still waits for approval there): approve it first.'
        if (-not (Read-YesNo 'Turn the kiosk on anyway?' $false)) {
            throw "The PC is not approved yet. Nothing was changed. After the approval run $SelfCmd"
        }
    }

    # --- 2. Пароль Club ----------------------------------------------------------------------------------------------
    # С киоском Shift при загрузке даёт экран входа (enable-kiosk-autologon даже снимает IgnoreShiftOverride): Club без
    # пароля — рабочий стол администратора для любого.
    Write-Title "Step 2: $AdminUser password"
    $passwordState = Get-PasswordState $AdminUser
    if ($passwordState -eq 'blank') {
        Write-Note "$AdminUser has an EMPTY password: with the kiosk on, Shift at boot gives anyone an admin desktop"
        $passwordScript = Join-Path $KitDir 'set-club-password.ps1'
        if ((Test-Path -LiteralPath $passwordScript) -and (Read-YesNo "Set the $AdminUser password now?" $true)) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $passwordScript -AdminUser $AdminUser
            $passwordState = Get-PasswordState $AdminUser
        }
        if ($passwordState -eq 'blank') {
            throw "$AdminUser still has an empty password: the kiosk was not turned on. Run $KitDir\set-club-password-run-as-admin.cmd, then $SelfCmd again."
        }
    }
    if ($passwordState -eq 'set') { Write-Pass "$AdminUser has a password" }
    else { Write-Note "the $AdminUser password could not be checked ($passwordState): make sure it has one before players arrive" }

    # --- 3. Автозапуск -----------------------------------------------------------------------------------------------
    Write-Title 'Step 3: AutoPlay off (disable-autoplay.ps1)'
    Invoke-KitScript 'disable-autoplay.ps1' @() "Take a photo of this window and run $SelfCmd again."
    Write-Pass 'AutoPlay is off for every drive and every user'

    # --- 4. Шаблон профиля -------------------------------------------------------------------------------------------
    Write-Title 'Step 4: ClubShell shell in the default profile template (seal-default-profile.ps1)'
    Invoke-KitScript 'seal-default-profile.ps1' @() "If it says 'still loaded': reboot, sign in as $AdminUser and run $SelfCmd again; otherwise send a photo of this window."
    Write-Pass "profiles created from now on (also '$KioskUser' after every profile reset) start ClubShell, not explorer"

    # --- 5. Профиль киоска и автовход ---------------------------------------------------------------------------------
    Write-Title "Step 5: kiosk profile and auto-logon (enable-kiosk-autologon.ps1 -KioskUser $KioskUser)"
    Write-Info 'the shell developer script: its lines are in Russian; it restarts the agent and waits up to 3 minutes'
    Invoke-KitScript 'enable-kiosk-autologon.ps1' @('-KioskUser', $KioskUser, '-NoReboot') ("Its DIAGNOSE.cmd hint does not apply here: take a photo of this window, run $DiagCmd and send both photos. " +
        "Kassa: the PC approved (maintenance off), 'Politika PK' shellReplacement on. Then run $SelfCmd again.")

    # --- 6. Проверка автовхода ---------------------------------------------------------------------------------------
    Write-Title 'Step 6: auto-logon'
    $autoLogon = Get-RegValue $WinlogonKey 'AutoAdminLogon'
    $defaultUser = Get-RegValue $WinlogonKey 'DefaultUserName'
    if ("$autoLogon" -eq '1' -and "$defaultUser" -ieq $KioskUser) {
        Write-Pass "auto-logon goes to '$KioskUser' (AutoAdminLogon=1): the kiosk starts at the next boot"
    } else {
        throw "Auto-logon is AutoAdminLogon=$autoLogon, DefaultUserName=${defaultUser}: not '$KioskUser'. Take a photo of this window, run $DiagCmd and send both photos."
    }
    Write-Info "Admin desktop later: hold Shift while Windows starts, until the sign-in screen -> $AdminUser -> password."
    $kioskOn = $true
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Write-Host ''
    if ($kioskOn) { Write-Host "${env:COMPUTERNAME}: the kiosk is ON. Log: $LogDir" -ForegroundColor Green }
    else { Write-Host "${env:COMPUTERNAME}: the kiosk is NOT on. Log: $LogDir" -ForegroundColor Red }
    try { Stop-Transcript | Out-Null } catch { }
}
# После журнала: перезагрузка закрывает окно, итог уже записан.
if ($kioskOn -and -not $NoReboot -and (Read-YesNo 'Reboot now into the kiosk?' $true)) { Restart-Computer -Force }
if ($PSCommandPath) { if ($kioskOn) { exit 0 } else { exit 2 } }
