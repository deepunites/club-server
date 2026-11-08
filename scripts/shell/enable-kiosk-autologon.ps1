<#
.SYNOPSIS
    Turns the kiosk on after ClubShell is installed (pilot support): after the next reboot Windows logs on to the kiosk
    account by itself and starts the ClubShell shell.
.DESCRIPTION
    ClubShell 1.0.6 runs its service without SeTakeOwnershipPrivilege, so CreateProfile fails with 0x80070522: the kiosk
    account gets no profile, the shell cannot be written into its registry hive and auto-logon is never turned on.
    1.0.7 fixes that in the installer and the agent; this script works with both. Run as administrator, it:
      1. adds SeTakeOwnershipPrivilege to the service when it is missing;
      2. creates the kiosk account's profile (an administrator holds that privilege);
      3. turns off what blocks or interrupts auto-logon on Windows 11: "only Windows Hello sign-in"
         (DevicePasswordLessBuildVersion), a logon message (legal notice), IgnoreShiftOverride, the first-logon animation
         and the privacy questions for a new account; on an agent older than 1.0.15 it also turns off the kiosk
         profile reset after each session (shell.kioskUser.resetProfileOnLogout), which there recreates the profile
         without the shell (1.0.15 writes the shell into the recreated profile; set-profile-reset.ps1 turns it back on);
      4. sets the service to plain auto start (installers up to 1.0.7 use delayed-auto, so the Shell showed "agent
         disconnected" for about two minutes after every boot) and restarts the agent: on start it sets its own
         password for the account, writes the shell into the profile and
         only then turns auto-logon on (LSA secret), so an interrupted run never leaves auto-logon without the shell;
      5. waits for the agent log to confirm it. When the agent wrote "." as the auto-logon domain (1.0.6), the computer
         name is written instead and the scheduled task \ClubShell\AutoLogonDomain keeps it so after the agent's next
         password rotation.
    The kiosk password stays with the agent; this script never sees it. Holding Shift while Windows starts skips the
    auto-logon (to sign in as an administrator).
#>
[CmdletBinding()]
param(
    [string] $KioskUser = '',
    [int] $WaitSeconds = 180,
    [string] $LogDir = (Join-Path $env:ProgramData 'ClubShell\logs'),
    [string] $AgentConfig = (Join-Path $env:ProgramData 'ClubShell\agent.json'),
    [switch] $NoReboot
)

# The kiosk this PC already has (SHELL_CHANGES 64): the name the installer stored, then agent.json; 'player' on a new PC.
if (-not $KioskUser) {
    try { $KioskUser = [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\ClubShell' -Name KioskUser -ErrorAction Stop).KioskUser } catch { }
}
if (-not $KioskUser) {
    try { $KioskUser = [string](Get-Content -LiteralPath $AgentConfig -Raw -ErrorAction Stop | ConvertFrom-Json).shell.kioskUser.name } catch { }
}
if (-not $KioskUser) { $KioskUser = 'player' }

$ErrorActionPreference = 'Stop'
$serviceName = 'ClubShellAgent'
$winlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$systemPolicyKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$passwordLessKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device'
$diagnoseHint = 'Запустите DIAGNOSE.cmd и пришлите файл clubshell-diagnose.txt с рабочего стола.'

Add-Type -AssemblyName System.Web.Extensions
$jsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer

function Write-Step([string] $Text) { Write-Host ''; Write-Host $Text -ForegroundColor Cyan }
function Write-Ok([string] $Text) { Write-Host "  OK  $Text" -ForegroundColor Green }
function Write-Note([string] $Text) { Write-Host "  !   $Text" -ForegroundColor Yellow }

function Exit-WithError([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Red
    Write-Host $diagnoseHint -ForegroundColor Red
    exit 1
}

function Set-RegistryValue([string] $Key, [string] $Name, $Value, [string] $Type) {
    # New-Item -Force on an existing registry key would recreate it empty, so only for a missing key.
    if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }
    New-ItemProperty -LiteralPath $Key -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

# Turns off an auto-logon that points at the kiosk account (an operator's own auto-logon is left alone).
function Disable-KioskAutoLogon {
    $winlogon = Get-ItemProperty -LiteralPath $winlogonKey
    if ($winlogon.AutoAdminLogon -eq '1' -and $winlogon.DefaultUserName -eq $KioskUser) {
        Set-RegistryValue $winlogonKey 'AutoAdminLogon' '0' 'String'
        Remove-ItemProperty -LiteralPath $winlogonKey -Name 'ForceAutoLogon' -ErrorAction SilentlyContinue
        Write-Host 'Автовход выключен, чтобы клиент не попал на обычный рабочий стол Windows.' -ForegroundColor Red
    }
}

function Get-KioskProfilePath([string] $Sid) {
    $value = (Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid" -ErrorAction SilentlyContinue).ProfileImagePath
    if ($value) { return [Environment]::ExpandEnvironmentVariables($value) }
    return $null
}

# One agent log line (Serilog compact JSON) as an object. ConvertFrom-Json in Windows PowerShell 5.1 rejects keys that
# differ only in case, and agent events carry "Version" (a message property) next to "version" (the agent version):
# the first of such keys is kept.
function ConvertFrom-LogLine([string] $Line) {
    try { $map = $jsonReader.DeserializeObject($Line) } catch { return $null }
    if ($map -isnot [System.Collections.IDictionary]) { return $null }
    $entry = New-Object psobject
    foreach ($key in $map.Keys) {
        if (-not $entry.PSObject.Properties[$key]) { Add-Member -InputObject $entry -NotePropertyName $key -NotePropertyValue $map[$key] }
    }
    return $entry
}

# Agent log events written at or after $Since whose raw text matches $Pattern.
function Get-AgentEvents([DateTimeOffset] $Since, [string] $Pattern) {
    $files = @(Get-ChildItem -LiteralPath $LogDir -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $Since.LocalDateTime.AddMinutes(-1) } | Sort-Object LastWriteTime)
    foreach ($file in $files) {
        foreach ($line in @(Get-Content -LiteralPath $file.FullName -Encoding UTF8 -ErrorAction SilentlyContinue) -match $Pattern) {
            $e = ConvertFrom-LogLine $line
            if (-not $e) { continue }
            try { $time = [DateTimeOffset]::Parse([string] $e.'@t', [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
            if ($time -ge $Since) { $e }
        }
    }
}

function Format-AgentEvent($e) {
    $text = [string] $e.'@mt'
    foreach ($p in $e.PSObject.Properties) {
        if ($p.Name -notlike '@*') { $text = $text.Replace('{' + $p.Name + '}', [string] $p.Value) }
    }
    if ($e.'@x') { $text += ' | ' + (([string] $e.'@x') -split "`n")[0].Trim() }
    return $text
}

# Rewrites DefaultDomainName "." (what agent 1.0.6 writes on every start) to the computer name every 5 minutes;
# a no-op once the agent writes the computer name itself.
function Register-DomainNameTask {
    $command = '$k = ''HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon''; ' +
        'if ((Get-ItemProperty -LiteralPath $k).DefaultDomainName -eq ''.'') { Set-ItemProperty -LiteralPath $k -Name DefaultDomainName -Value $env:COMPUTERNAME }'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded"
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskPath '\ClubShell\' -TaskName 'AutoLogonDomain' -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'ClubShell pilot: keeps the computer name as the auto-logon domain of the kiosk account (agent 1.0.6 writes ".").' -Force | Out-Null
}

# Version of the installed agent (ClubShellAgent.exe), or $null when it cannot be read.
function Get-AgentVersion {
    $exe = Join-Path $env:ProgramFiles 'ClubShell\Agent\ClubShellAgent.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    $text = [string] (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $parsed = $null
    if ([Version]::TryParse(($text -split '[+-]')[0], [ref] $parsed)) { return $parsed }
    return $null
}

# Sets shell.kioskUser.resetProfileOnLogout = false in agent.json (written to a temp file and checked first).
function Disable-ProfileReset {
    if (-not (Test-Path -LiteralPath $AgentConfig)) { return 'нет agent.json' }
    $config = Get-Content -LiteralPath $AgentConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $config.shell -or -not $config.shell.kioskUser) { return 'в agent.json нет shell.kioskUser' }
    $kiosk = $config.shell.kioskUser
    if ($kiosk.PSObject.Properties['resetProfileOnLogout'] -and $kiosk.resetProfileOnLogout -eq $false) { return $null }
    if ($kiosk.PSObject.Properties['resetProfileOnLogout']) { $kiosk.resetProfileOnLogout = $false }
    else { Add-Member -InputObject $kiosk -NotePropertyName 'resetProfileOnLogout' -NotePropertyValue $false }
    $text = $config | ConvertTo-Json -Depth 30
    $check = $text | ConvertFrom-Json
    if ($check.shell.kioskUser.resetProfileOnLogout -ne $false) { return 'проверка записи не прошла' }
    $temp = $AgentConfig + '.tmp'
    [IO.File]::WriteAllText($temp, $text, (New-Object System.Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temp -Destination $AgentConfig -Force
    return $null
}

$principalCheck = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}

try {
    Write-Host "=== ClubShell: включение киоска на $env:COMPUTERNAME" -ForegroundColor Cyan
    if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
        Write-Host 'ClubShell не установлен: сначала установите ClubShell-Setup.exe.' -ForegroundColor Red
        exit 1
    }

    # --- 1. Service privileges
    Write-Step '1/5  Права службы агента'
    $privileges = @(& sc.exe qprivs $serviceName 8192 | Select-String -Pattern 'Se\w+Privilege' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Value })
    if ($privileges.Count -eq 0) {
        Write-Ok 'у службы нет ограничения прав'
    } elseif ($privileges -contains 'SeTakeOwnershipPrivilege') {
        Write-Ok 'право SeTakeOwnershipPrivilege уже есть'
    } else {
        $output = & sc.exe privs $serviceName (($privileges + 'SeTakeOwnershipPrivilege') -join '/')
        if ($LASTEXITCODE -ne 0) { Exit-WithError "Не удалось изменить права службы: $output" }
        Write-Ok 'добавлено право SeTakeOwnershipPrivilege (без него агент не может создать профиль киоска)'
    }

    # --- 2. Kiosk account and its profile
    Write-Step "2/5  Пользователь киоска '$KioskUser' и его профиль"
    $user = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if (-not $user) {
        Write-Note 'пользователя ещё нет, его создаёт агент: запускаю агента и жду до 2 минут'
        try { Start-Service $serviceName -ErrorAction Stop } catch { }
        $deadline = (Get-Date).AddMinutes(2)
        while (-not $user -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $user = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
        }
        if (-not $user) { Exit-WithError "Агент не создал пользователя '$KioskUser'." }
    }
    if (-not $user.Enabled) { Enable-LocalUser -Name $KioskUser }
    $sid = $user.SID.Value

    $profilePath = Get-KioskProfilePath $sid
    if ($profilePath -and -not (Test-Path -LiteralPath (Join-Path $profilePath 'NTUSER.DAT'))) {
        $broken = Get-CimInstance Win32_UserProfile -Filter "SID='$sid'" -ErrorAction SilentlyContinue
        if ($broken -and -not $broken.Loaded) {
            Remove-CimInstance -InputObject $broken
            Write-Note 'профиль был повреждён (нет NTUSER.DAT) и удалён, создаю заново'
        }
        $profilePath = Get-KioskProfilePath $sid
    }
    if (-not $profilePath) {
        Add-Type -Namespace ClubShellFix -Name Userenv -MemberDefinition @'
[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
public static extern int CreateProfile(string pszUserSid, string pszUserName, System.Text.StringBuilder pszProfilePath, uint cchProfilePath);
'@
        $buffer = New-Object System.Text.StringBuilder 260
        $code = '{0:X8}' -f [ClubShellFix.Userenv]::CreateProfile($sid, $KioskUser, $buffer, 260)
        if ($code -ne '00000000' -and $code -ne '800700B7') { Exit-WithError "Windows не создал профиль '$KioskUser' (ошибка 0x$code)." }
        $profilePath = Get-KioskProfilePath $sid
    }
    if (-not $profilePath -or -not (Test-Path -LiteralPath (Join-Path $profilePath 'NTUSER.DAT'))) {
        Exit-WithError "Профиль '$KioskUser' не появился."
    }
    Write-Ok "профиль: $profilePath"

    # --- 3. Windows settings that block or interrupt auto-logon; profile reset off on agents before 1.0.15
    Write-Step '3/5  Настройки Windows, которые мешают автовходу'
    $before = (Get-ItemProperty -LiteralPath $passwordLessKey -ErrorAction SilentlyContinue).DevicePasswordLessBuildVersion
    Set-RegistryValue $passwordLessKey 'DevicePasswordLessBuildVersion' 0 'DWord'
    if ($before) { Write-Ok "вход «только через Windows Hello» выключен (было $before)" } else { Write-Ok 'вход «только через Windows Hello» не включён' }

    $noticeRemoved = $false
    foreach ($key in $systemPolicyKey, $winlogonKey) {
        $values = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        foreach ($name in 'LegalNoticeCaption', 'LegalNoticeText') {
            if ($values -and $values.$name) {
                Set-RegistryValue $key $name '' 'String'
                $noticeRemoved = $true
            }
        }
    }
    if ($noticeRemoved) { Write-Ok 'убрано сообщение перед входом (оно останавливает автовход до нажатия OK)' }

    if ((Get-ItemProperty -LiteralPath $winlogonKey -ErrorAction SilentlyContinue).IgnoreShiftOverride) {
        Remove-ItemProperty -LiteralPath $winlogonKey -Name 'IgnoreShiftOverride'
        Write-Ok 'снова работает Shift при загрузке (вход под администратором)'
    }
    Remove-ItemProperty -LiteralPath $winlogonKey -Name 'AutoLogonCount' -ErrorAction SilentlyContinue

    Set-RegistryValue $systemPolicyKey 'EnableFirstLogonAnimation' 0 'DWord'
    Set-RegistryValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE' 'DisablePrivacyExperience' 1 'DWord'
    Write-Ok 'отключены анимация первого входа и вопросы о конфиденциальности для нового пользователя'

    # 1.0.15 writes the shell into the profile Windows recreates after a reset; older agents leave a Windows desktop there.
    $agentVersion = Get-AgentVersion
    if ($agentVersion -and $agentVersion -ge [Version]'1.0.15') {
        Write-Ok "очистка профиля между клиентами остаётся как в agent.json (агент $agentVersion восстанавливает шелл в новом профиле)"
    } else {
        $resetProblem = Disable-ProfileReset
        if ($resetProblem) { Write-Note "очистку профиля между клиентами выключить не удалось ($resetProblem)" }
        else { Write-Ok 'очистка профиля между клиентами выключена: агент старше 1.0.15 (обновите его и запустите set-profile-reset.ps1)' }
    }

    # --- 4. Agent restart: provisioning, shell into the profile, then auto-logon
    Write-Step '4/5  Перезапуск агента'
    # Plain auto start (1.0.6/1.0.7 install delayed-auto): the kiosk logs on at boot and its Shell shows "Служба клуба
    # отключена" until the agent is up, about two minutes later with delayed-auto.
    & sc.exe config $serviceName start= auto | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Ok 'агент запускается сразу при старте Windows (без задержки в 2 минуты)' }
    else { Write-Note 'не удалось убрать задержку запуска агента' }
    if ((Get-Service $serviceName).Status -ne 'Stopped') {
        & sc.exe stop $serviceName | Out-Null
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Service $serviceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        if ((Get-Service $serviceName).Status -ne 'Stopped') {
            Get-Process ClubShellAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
        }
    }
    # Only what the restarted agent logs counts (the old one may have logged a failure just before it stopped).
    $restartedAt = [DateTimeOffset]::Now
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Service $serviceName).Status -ne 'Running' -and (Get-Date) -lt $deadline) {
        try { Start-Service $serviceName -ErrorAction Stop } catch { Start-Sleep -Seconds 2 }
    }
    if ((Get-Service $serviceName).Status -ne 'Running') { Exit-WithError 'Агент ClubShell не запускается.' }
    Write-Ok 'агент перезапущен'

    # --- 5. Wait for the agent to write the shell and turn auto-logon on
    Write-Step "5/5  Жду, пока агент запишет шелл и включит автовход (до $([math]::Ceiling($WaitSeconds / 60)) мин)"
    $result = $null
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while (-not $result -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        foreach ($e in Get-AgentEvents -Since $restartedAt -Pattern 'shellReplacement') {
            if ([string] $e.Section -ne 'shellReplacement') { continue }
            $template = [string] $e.'@mt'
            if ($template -like 'policy*' -and ([string] $e.Note) -like 'shell replacement disabled*') { $result = 'disabled' }
            elseif ($template -like 'Policy section*applied*' -and $result -ne 'disabled') { $result = 'applied' }
            elseif ($template -like 'Policy section*failed*') { $result = 'failed: ' + (Format-AgentEvent $e) }
        }
    }

    if ($result -ne 'applied') {
        Write-Host ''
        if ($result -eq 'disabled') {
            Disable-KioskAutoLogon
            Write-Host 'Замена оболочки выключена в настройках клуба, поэтому киоск не включён.' -ForegroundColor Red
            Write-Host 'Включите в кассе замену оболочки (shellReplacement) и запустите этот файл ещё раз.' -ForegroundColor Red
            exit 1
        }
        if ($result) {
            Disable-KioskAutoLogon
            Write-Host "Агент не смог записать шелл: $($result.Substring(8))" -ForegroundColor Red
        } else {
            Write-Host "Агент ничего не сообщил про шелл за $WaitSeconds с." -ForegroundColor Red
        }
        Write-Host 'Последние предупреждения агента:' -ForegroundColor Red
        foreach ($e in @(Get-AgentEvents -Since $restartedAt -Pattern '"@l":"(Warning|Error|Fatal)"') | Select-Object -Last 8) {
            Write-Host ('  ' + (Format-AgentEvent $e))
        }
        Exit-WithError 'Киоск не включён.'
    }

    $winlogon = Get-ItemProperty -LiteralPath $winlogonKey
    if ($winlogon.AutoAdminLogon -ne '1' -or $winlogon.DefaultUserName -ne $KioskUser) {
        Exit-WithError "Агент записал шелл, но автовход не включился: AutoAdminLogon=$($winlogon.AutoAdminLogon) DefaultUserName=$($winlogon.DefaultUserName)."
    }
    if ($winlogon.DefaultDomainName -eq '.' -or -not $winlogon.DefaultDomainName) {
        # Agent 1.0.6: Windows documents the computer name as the auto-logon domain of a local account.
        Set-RegistryValue $winlogonKey 'DefaultDomainName' $env:COMPUTERNAME 'String'
        try { Register-DomainNameTask } catch { Write-Note "задача AutoLogonDomain не создана: $($_.Exception.Message)" }
    }
    Write-Ok "шелл ClubShell записан в профиль '$KioskUser', автовход включён ($env:COMPUTERNAME\$KioskUser)"
} catch {
    Exit-WithError "Ошибка: $($_.Exception.Message)"
}

Write-Host ''
Write-Host 'ГОТОВО. После перезагрузки ПК сам войдёт в киоск и откроет ClubShell.' -ForegroundColor Green
Write-Host 'Чтобы войти под администратором: держите Shift, пока Windows загружается.' -ForegroundColor Green
if (-not $NoReboot) {
    Write-Host ''
    $answer = Read-Host 'Перезагрузить ПК сейчас? Введите Y и нажмите Enter (или просто Enter — позже)'
    if ($answer -match '^\s*(y|yes|д|да)\s*$') { Restart-Computer -Force }
}
