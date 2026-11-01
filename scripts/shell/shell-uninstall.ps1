<#
.SYNOPSIS
    Откат ClubShell с гибрида: удаляет агента и оболочку (бандл 1.0.18, MSI-сборки пилота, установщик одной оболочки),
    то, что удаление ClubShell оставляет, по желанию — киоск «player» с профилем и данные ClubShell, и возвращает
    автовход Club. Помощник бездиска (ClubDisklessHelper) не трогает и проверяет, что он цел.
.DESCRIPTION
    Запускать на гибриде под «Club» (Shift при загрузке -> Club -> пароль), от администратора:
    C:\ClubShellKit\shell-uninstall-run-as-admin.cmd (флешки при установленном ClubShell не работают — набор уже на C:).
    Что делает:
      1. показывает, что стоит: служба ClubShellAgent, записи «ClubShell*» в «Программах», имя киоска из
         HKLM\SOFTWARE\ClubShell\KioskUser; спрашивает подтверждение и что удалять ещё (-Yes — без вопросов: ClubShell,
         киоск, если его создал ClubShell, и данные; автовход не трогать);
      2. копирует exe помощника в C:\ClubShellKit\helper-backup: MSI старых пилотных сборок (до PR #58) могли нести
         помощник в ту же папку и удалить его вместе с собой (SHELL_CHANGES 58) — тогда exe возвращается на место;
      3. выключает и останавливает агента (иначе удаление ждёт службу, которая перезапускается), закрывает оболочку и
         сеансы киоска;
      4. запускает удаление: бандл — его QuietUninstallString (копия в Package Cache, иначе ClubShellSetup-1.0.18.exe из
         набора с /uninstall), потом оставшиеся MSI — msiexec /x {код продукта} (uninstall-clubshell.ps1 из club-shell
         MSI-установки не удаляет, SHELL_CHANGES 63), прочие — их QuietUninstallString;
      5. убирает то, что удаление ClubShell оставляет или может оставить (SHELL_CHANGES 67, 28): USBSTOR Start=3, запрет
         установки HID (если его ставила политика), LSA-секрет DefaultPassword (пароль киоска для автовхода), автовход в
         киоск, задачи \ClubShell\ (старый пилот), правила брандмауэра ClubShell-*, блок ClubShell в hosts; шелл в
         шаблоне профиля C:\Users\Default (seal-default-profile.ps1 -Undo из набора) — при каждом запуске, который дошёл
         до остатков: и если удаление MSI упало, и если ClubShell уже удалён раньше (через «Программы» или прошлым
         запуском); не вышло — WARN с командой по сети. Автозапуск остаётся выключенным (disable-autoplay.ps1 из
         kiosk-on): это безвредно, окно «Автозапуск» не нужно и администратору;
      6. по желанию удаляет киоск (профиль через Win32_UserProfile и учётку) — никогда не трогает Club и любого
         администратора; учётку, созданную не ClubShell (описание не «ClubShell kiosk account»), — только по отдельному
         «да»; и данные: C:\ProgramData\ClubShell (настройки, регистрация, журналы), остатки C:\Program Files\ClubShell,
         HKLM\SOFTWARE\ClubShell;
      7. по желанию возвращает автовход Club, как было до шелла: удаление ClubShell ставит AutoAdminLogon=0, и гибрид
         останавливается на экране входа. Пароль Club вводит владелец (Enter — если пароль пустой); он проверяется
         входом (LogonUser) и хранится, как его хранит Windows, — в LSA-секрете DefaultPassword, не в реестре;
      8. проверяет: ClubShell нет, помощник тот же и работает (иначе возвращает exe из копии), G:/H: на месте, USB
         включён; печатает, что сделать в кассе.
    Альтернатива откату — перезаливка гибрида из панели club-server (образ h1e): шелла после неё нет.
    Журнал — C:\ClubShellKit\logs\uninstall-<ПК>-<время>.txt и журналы удаления рядом.
    По сети одной строкой — если файл выложен на сервер клуба (/srv/club/pxe/tools; пока не выложен):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/shell-uninstall.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
param(
    [string]$AdminUser = 'Club',
    [string]$KitDir = 'C:\ClubShellKit',
    [switch]$Yes,
    [switch]$RestoreAdminAutologon
)
$ErrorActionPreference = 'Continue'
$SetupName = 'ClubShellSetup-1.0.18.exe'
$HelperSha256 = '26635A5F7AAB4D77441D0B0138064B9C7F64BB1DAF4B0BCAF09052439DDD0148'   # ClubDisklessHelper 1.6.0+13609a5
$UpdateHelperCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex'
$SealUndoCmd = '& ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/seal-default-profile.ps1))) -Undo'
$DataDir = Join-Path $env:ProgramData 'ClubShell'
$InstallDir = Join-Path $env:ProgramFiles 'ClubShell'
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$KioskDescription = 'ClubShell kiosk account'
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

# Да/нет; в русской раскладке клавиши Y и N дают «н» и «т». С -Yes вопрос не задаётся: ответ — $YesAnswer.
function Read-YesNo([string]$Question, [bool]$Default, [bool]$YesAnswer) {
    if ($Yes) { return $YesAnswer }
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

function Get-ClubShellEntries {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    @(Get-ChildItem -Path $keys -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { "$($_.DisplayName)" -like 'ClubShell*' })
}

function Get-HelperState {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='ClubDisklessHelper'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    $exe = ($service.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim()
    $hash = if (Test-Path -LiteralPath $exe) { (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash } else { $null }
    [pscustomobject]@{ State = $service.State; Exe = $exe; Hash = $hash }
}

function Invoke-Uninstaller([string]$Exe, [string]$Arguments, [string]$What) {
    Write-Info "running: $What"
    try {
        $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -ErrorAction Stop
        $code = $process.ExitCode
    } catch {
        Write-Fail "$What could not start: $($_.Exception.Message)"
        return
    }
    # 1605 — продукта уже нет (удалён предыдущим шагом, например MSI внутри бандла).
    if ($code -in @(0, 1605)) { Write-Pass "$What finished (exit code $code)" }
    elseif ($code -eq 3010) { Write-Pass "$What finished (exit code 3010: reboot needed)" }
    else { Write-Fail "$What failed with exit code $code" 'reboot and run this script again; logs in C:\ClubShellKit\logs' }
}

# LSA-секрет DefaultPassword (пароль автовхода) и проверка пароля — в C#: секрет пишется прямо из SecureString, без
# управляемой строки, и коды ошибок не затираются вызовами PowerShell.
if (-not ('ClubKitUninstallNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security;

public static class ClubKitUninstallNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes;
        public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, int access, out IntPtr policy);
    [DllImport("advapi32.dll")]
    private static extern uint LsaStorePrivateData(IntPtr policy, ref LSA_UNICODE_STRING key, IntPtr data);
    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, IntPtr password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const int PolicyCreateSecret = 0x20;
    private const int PolicyGetPrivateInformation = 0x4;

    // Stores value as the LSA secret key (null deletes it). Returns 0 or a Win32 error; deleting a missing secret is 0.
    public static int StoreSecret(string key, SecureString value)
    {
        LSA_OBJECT_ATTRIBUTES attributes = new LSA_OBJECT_ATTRIBUTES();
        attributes.Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));
        IntPtr policy;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateSecret | PolicyGetPrivateInformation, out policy);
        if (status != 0) { return LsaNtStatusToWinError(status); }
        IntPtr keyBuffer = Marshal.StringToHGlobalUni(key);
        IntPtr valueBuffer = IntPtr.Zero;
        IntPtr valueStruct = IntPtr.Zero;
        try
        {
            LSA_UNICODE_STRING keyString = new LSA_UNICODE_STRING();
            keyString.Buffer = keyBuffer;
            keyString.Length = (ushort)(key.Length * 2);
            keyString.MaximumLength = (ushort)(key.Length * 2 + 2);
            if (value != null)
            {
                valueBuffer = Marshal.SecureStringToGlobalAllocUnicode(value);
                LSA_UNICODE_STRING valueString = new LSA_UNICODE_STRING();
                valueString.Buffer = valueBuffer;
                valueString.Length = (ushort)(value.Length * 2);
                valueString.MaximumLength = (ushort)(value.Length * 2 + 2);
                valueStruct = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LSA_UNICODE_STRING)));
                Marshal.StructureToPtr(valueString, valueStruct, false);
            }
            status = LsaStorePrivateData(policy, ref keyString, valueStruct);
            if (status == 0) { return 0; }
            int error = LsaNtStatusToWinError(status);
            return (value == null && error == 2) ? 0 : error;
        }
        finally
        {
            Marshal.FreeHGlobal(keyBuffer);
            if (valueBuffer != IntPtr.Zero) { Marshal.ZeroFreeGlobalAllocUnicode(valueBuffer); }
            if (valueStruct != IntPtr.Zero) { Marshal.FreeHGlobal(valueStruct); }
            LsaClose(policy);
        }
    }

    // Network logon (LOGON32_LOGON_NETWORK = 3) with the given password: 0 = right, else the Win32 error
    // (1326 wrong password; 1327 empty password, refused off the console; 1385 right, but network logon not granted).
    public static int CheckPassword(string user, SecureString password)
    {
        IntPtr buffer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            IntPtr token;
            if (LogonUser(user, ".", buffer, 3, 0, out token)) { CloseHandle(token); return 0; }
            return Marshal.GetLastWin32Error();
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(buffer); }
    }
}
'@
}

New-Item -ItemType Directory -Force -Path (Join-Path $KitDir 'logs') | Out-Null
$LogDir = Join-Path $KitDir 'logs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
try { Start-Transcript -LiteralPath (Join-Path $LogDir "uninstall-$env:COMPUTERNAME-$stamp.txt") | Out-Null } catch { }

try {
    # --- 1. Что стоит --------------------------------------------------------------------------------------------------
    Write-Title "ClubShell on $env:COMPUTERNAME"
    $entries = Get-ClubShellEntries
    $service = Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue
    $registryKiosk = Get-RegValue 'HKLM:\SOFTWARE\ClubShell' 'KioskUser'
    $kioskUser = if ($registryKiosk) { $registryKiosk } else { 'player' }
    $kioskAccount = Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
    $tasks = @(Get-ScheduledTask -TaskPath '\ClubShell\' -ErrorAction SilentlyContinue)
    foreach ($entry in $entries) { Write-Info ("installed: {0} {1} ({2})" -f $entry.DisplayName, $entry.DisplayVersion, $entry.PSChildName) }
    if ($service) { Write-Info "service ClubShellAgent: $($service.Status)" }
    Write-Info ("kiosk account: {0} ({1})" -f $kioskUser, $(if ($kioskAccount) { "exists, '$($kioskAccount.Description)'" } else { 'missing' }))
    if (Test-Path -LiteralPath $DataDir) { Write-Info "data: $DataDir" }
    $anything = $entries.Count -gt 0 -or $service -or (Test-Path -LiteralPath $DataDir) -or (Test-Path -LiteralPath $InstallDir) -or
        (Test-Path -LiteralPath 'HKLM:\SOFTWARE\ClubShell') -or $tasks.Count -gt 0 -or ($kioskAccount -and $kioskAccount.Description -eq $KioskDescription)
    if (-not $anything -and -not $RestoreAdminAutologon) {
        Write-Pass 'ClubShell is not on this PC: nothing to do'
        return
    }

    # Киоск не удаляется никогда, если это Club или любой администратор (KIOSKUSER=club на гибриде = наш Club).
    $kioskProtected = ($kioskUser -ieq $AdminUser) -or ($kioskAccount -and (Get-GroupMemberSids 'S-1-5-32-544') -contains $kioskAccount.SID.Value)
    if ($kioskProtected) { Write-Warn "kiosk account '$kioskUser' is '$AdminUser' or an administrator: it is kept as it is" }

    $removeShell = $false
    if ($entries.Count -gt 0 -or $service) {
        $removeShell = Read-YesNo 'Remove ClubShell (agent and kiosk shell) from this PC?' $false $true
        if (-not $removeShell) { Write-Host 'Nothing was changed.'; return }
    }
    $removeKiosk = $false
    if ($kioskAccount -and -not $kioskProtected) {
        if ($kioskAccount.Description -eq $KioskDescription) {
            $removeKiosk = Read-YesNo "Delete the kiosk account '$kioskUser' and its profile (created by ClubShell)?" $true $true
        } else {
            $removeKiosk = Read-YesNo "Account '$kioskUser' was NOT created by ClubShell (it existed before). Delete it and its profile anyway?" $false $false
        }
    }
    $removeData = $false
    if ((Test-Path -LiteralPath $DataDir) -or (Test-Path -LiteralPath $InstallDir) -or (Test-Path -LiteralPath 'HKLM:\SOFTWARE\ClubShell')) {
        $removeData = Read-YesNo "Delete ClubShell data (${DataDir}: settings, registration, logs)? Needed before a clean reinstall." $true $true
    }
    $restoreAutologon = $RestoreAdminAutologon
    if (-not $restoreAutologon -and -not $Yes) {
        $restoreAutologon = Read-YesNo "Turn auto-logon to '$AdminUser' back on (as before the shell)? Without it the PC stops at the sign-in screen." $false $false
    }

    $winlogonUserBefore = Get-RegValue $WinlogonKey 'DefaultUserName'

    # --- 2. Копия помощника ----------------------------------------------------------------------------------------------
    $helperBefore = Get-HelperState
    $helperBackup = $null
    if ($helperBefore -and $helperBefore.Hash) {
        $backupDir = Join-Path $KitDir 'helper-backup'
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
        $helperBackup = Join-Path $backupDir 'ClubDisklessHelper.exe'
        Copy-Item -LiteralPath $helperBefore.Exe -Destination $helperBackup -Force
        Write-Info "helper exe copied to $helperBackup (SHA256 $($helperBefore.Hash))"
    } else {
        Write-Warn 'helper ClubDisklessHelper not found before the removal' $UpdateHelperCmd
    }

    # --- 3-4. Агент, оболочка, удаление ----------------------------------------------------------------------------------
    if ($removeShell) {
        Write-Title 'Removing ClubShell'
        if ($service) {
            # Сначала выключить автозапуск: иначе служба с перезапуском при сбое поднимется снова.
            & sc.exe config ClubShellAgent start= disabled | Out-Null
            & sc.exe stop ClubShellAgent | Out-Null
            Start-Sleep -Seconds 3
            Get-Process -Name ClubShellAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Write-Info 'agent stopped and disabled'
        }
        Get-Process -Name 'clubshell-shell' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        # Сеансы киоска (быстрое переключение пользователей): выход, чтобы освободить профиль.
        $kioskSessions = @(Get-Process -IncludeUserName -ErrorAction SilentlyContinue |
            Where-Object { $_.UserName -and (($_.UserName -split '\\')[-1]) -ieq $kioskUser -and $_.SessionId -ne 0 } |
            ForEach-Object { $_.SessionId } | Sort-Object -Unique)
        foreach ($sessionId in $kioskSessions) {
            Write-Info "signing out the kiosk session $sessionId"
            & logoff.exe $sessionId 2>$null
        }

        foreach ($entry in @($entries | Where-Object { $_.QuietUninstallString })) {
            $isBundle = [bool]$entry.BundleUpgradeCode
            if ("$($entry.QuietUninstallString)" -match '^\s*"(?<exe>[^"]+)"\s*(?<args>.*)$') {
                $exe = $Matches['exe']
                $arguments = $Matches['args']
                if ($isBundle) { $arguments = "$arguments /norestart /log `"$(Join-Path $LogDir "uninstall-bundle-$stamp.log")`"" }
                if (-not (Test-Path -LiteralPath $exe) -and $isBundle -and $entry.DisplayVersion -like '1.0.18*' -and (Test-Path -LiteralPath (Join-Path $KitDir $SetupName))) {
                    # Копии бандла в Package Cache нет — тот же релиз из набора удаляет себя сам.
                    $exe = Join-Path $KitDir $SetupName
                    $arguments = "/uninstall /quiet /norestart /log `"$(Join-Path $LogDir "uninstall-bundle-$stamp.log")`""
                }
                if (Test-Path -LiteralPath $exe) { Invoke-Uninstaller $exe $arguments "$($entry.DisplayName) $($entry.DisplayVersion) uninstall" }
                else { Write-Fail "uninstaller of $($entry.DisplayName) $($entry.DisplayVersion) not found: $exe" "put $SetupName into $KitDir and run again" }
            }
        }
        # Оставшиеся MSI (MSI из бандла уходит вместе с ним; MSI пилотных сборок ставились без бандла).
        foreach ($entry in @(Get-ClubShellEntries | Where-Object { $_.WindowsInstaller -eq 1 -and $_.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$' })) {
            $msiLog = Join-Path $LogDir "uninstall-msi-$($entry.PSChildName.Trim('{}'))-$stamp.log"
            Invoke-Uninstaller 'msiexec.exe' "/x $($entry.PSChildName) /qn /norestart /l*v `"$msiLog`"" "msiexec /x $($entry.PSChildName) ($($entry.DisplayName) $($entry.DisplayVersion))"
        }
    }

    # --- 5. Остатки ------------------------------------------------------------------------------------------------------
    Write-Title 'Leftovers'
    Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name Start -Value 3 -Type DWord -ErrorAction SilentlyContinue
    $restrictions = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
    if ((Get-RegValue "$restrictions\DenyDeviceClasses" '1') -eq '{745a17a0-74d3-11d0-b6fe-00a0c9f57da2}') {
        Remove-ItemProperty -LiteralPath "$restrictions\DenyDeviceClasses" -Name '1' -ErrorAction SilentlyContinue
        Set-ItemProperty -LiteralPath $restrictions -Name DenyDeviceClasses -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Write-Info 'HID install block of the policy removed'
    }
    foreach ($task in @(Get-ScheduledTask -TaskPath '\ClubShell\' -ErrorAction SilentlyContinue)) {
        Unregister-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-Info "task $($task.TaskPath)$($task.TaskName) removed"
    }
    $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'ClubShell-*' -or $_.Name -like 'ClubShell-*' })
    foreach ($rule in $rules) { $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue }
    if ($rules.Count -gt 0) { Write-Info "$($rules.Count) ClubShell firewall rule(s) removed" }
    $hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
    $lines = @()
    try { $lines = [IO.File]::ReadAllLines($hosts) } catch { }
    if ($lines | Where-Object { $_ -match '^\s*#\s*ClubShell-BEGIN' }) {
        $kept = New-Object System.Collections.Generic.List[string]
        $skip = $false
        foreach ($line in $lines) {
            if ($line -match '^\s*#\s*ClubShell-BEGIN') { $skip = $true; continue }
            if ($line -match '^\s*#\s*ClubShell-END') { $skip = $false; continue }
            if (-not $skip) { $kept.Add($line) }
        }
        [IO.File]::WriteAllLines($hosts, $kept)
        Write-Info 'ClubShell block removed from the hosts file'
    }
    if ($removeShell) {
        # Автовход в киоск выключается удалением ClubShell, но LSA-секрет с паролем киоска остаётся (SHELL_CHANGES 67).
        $lsa = [ClubKitUninstallNative]::StoreSecret('DefaultPassword', $null)
        if ($lsa -eq 0) { Write-Info 'auto-logon secret (kiosk password) removed' } else { Write-Warn "auto-logon secret not removed (Win32 error $lsa)" }
        if ((Get-RegValue $WinlogonKey 'DefaultUserName') -ieq $kioskUser -or $winlogonUserBefore -ieq $kioskUser) {
            Set-ItemProperty -LiteralPath $WinlogonKey -Name AutoAdminLogon -Value '0' -ErrorAction SilentlyContinue
            Remove-ItemProperty -LiteralPath $WinlogonKey -Name DefaultUserName -ErrorAction SilentlyContinue
        }
    }
    # Шелл ClubShell в шаблоне профиля (seal-default-profile.ps1, шаг kiosk-on): без -Undo каждая новая учётная запись
    # стартовала бы с удалённой оболочкой вместо рабочего стола. Не только при $removeShell: сюда доходим, только если
    # ClubShell удаляется сейчас или его уже нет (удалён раньше через «Программы» или прошлым запуском, у которого -Undo
    # не вышел) — в обоих случаях значение в шаблоне лишнее, а -Undo убирает только своё. Среди остатков: выполняется и
    # тогда, когда удаление MSI упало (его ошибки скрипт не прерывают). Отдельным процессом — у скрипта свой
    # $ErrorActionPreference = 'Stop'.
    $sealScript = Join-Path $KitDir 'seal-default-profile.ps1'
    if (Test-Path -LiteralPath $sealScript) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sealScript -Undo
        if ($LASTEXITCODE -eq 0) { Write-Pass 'default profile template: no ClubShell shell (new profiles start the Windows desktop)' }
        else { Write-Warn "seal-default-profile.ps1 -Undo failed (exit code $LASTEXITCODE): new profiles may still try to start ClubShell" $SealUndoCmd }
    } else {
        Write-Warn "$sealScript is missing: new profiles may still try to start ClubShell" $SealUndoCmd
    }

    # --- 6. Киоск и данные -------------------------------------------------------------------------------------------------
    if ($removeKiosk) {
        Write-Title "Kiosk account '$kioskUser'"
        $sid = $kioskAccount.SID.Value
        $userProfile = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$sid'" -ErrorAction SilentlyContinue
        $profileGone = $true
        if ($userProfile) {
            if ($userProfile.Loaded) {
                $profileGone = $false
                Write-Warn "profile $($userProfile.LocalPath) is in use" 'reboot, sign in as Club and run this script again'
            } else {
                try { $userProfile | Remove-CimInstance -ErrorAction Stop; Write-Pass "profile $($userProfile.LocalPath) deleted" }
                catch { $profileGone = $false; Write-Warn "profile $($userProfile.LocalPath) not deleted: $($_.Exception.Message)" 'reboot and run again' }
            }
        }
        if ($profileGone) {
            try { Remove-LocalUser -SID $sid -ErrorAction Stop; Write-Pass "account '$kioskUser' deleted" }
            catch { Write-Warn "account '$kioskUser' not deleted: $($_.Exception.Message)" }
        }
    }
    if ($removeData) {
        Write-Title 'ClubShell data'
        foreach ($path in @($DataDir, $InstallDir)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $path) { Write-Warn "$path not fully deleted (files in use)" 'reboot and run again' } else { Write-Pass "$path deleted" }
            }
        }
        if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\ClubShell') {
            Remove-Item -LiteralPath 'HKLM:\SOFTWARE\ClubShell' -Recurse -Force -ErrorAction SilentlyContinue
            Write-Pass 'HKLM\SOFTWARE\ClubShell deleted'
        }
    }

    # --- 7. Автовход Club ------------------------------------------------------------------------------------------------
    if ($restoreAutologon) {
        Write-Title "Auto-logon to '$AdminUser'"
        $stored = $false
        for ($try = 1; $try -le 3 -and -not $stored; $try++) {
            $password = Read-Host "Password of '$AdminUser' (hidden; just Enter if it is empty)" -AsSecureString
            $check = [ClubKitUninstallNative]::CheckPassword($AdminUser, $password)
            $right = ($check -eq 0) -or ($check -eq 1385) -or ($check -eq 1327 -and $password.Length -eq 0)
            if (-not $right) {
                if ($check -eq 1326) { Write-Host '      wrong password, try again' -ForegroundColor Yellow }
                else { Write-Host "      the password could not be checked (Win32 error $check), try again" -ForegroundColor Yellow }
                $password.Dispose()
                continue
            }
            $lsa = [ClubKitUninstallNative]::StoreSecret('DefaultPassword', $password)
            $empty = $password.Length -eq 0
            $password.Dispose()
            if ($lsa -ne 0) { Write-Fail "auto-logon password not stored (Win32 error $lsa)"; break }
            Set-ItemProperty -LiteralPath $WinlogonKey -Name AutoAdminLogon -Value '1'
            Set-ItemProperty -LiteralPath $WinlogonKey -Name DefaultUserName -Value $AdminUser
            Set-ItemProperty -LiteralPath $WinlogonKey -Name DefaultDomainName -Value $env:COMPUTERNAME
            Remove-ItemProperty -LiteralPath $WinlogonKey -Name AutoLogonCount -ErrorAction SilentlyContinue
            # Пустой пароль: пустое значение DefaultPassword в реестре (секрета в нём нет), как при автовходе из unattend;
            # непустой — только в LSA, открытого значения в реестре нет.
            if ($empty) { Set-ItemProperty -LiteralPath $WinlogonKey -Name DefaultPassword -Value '' }
            else { Remove-ItemProperty -LiteralPath $WinlogonKey -Name DefaultPassword -ErrorAction SilentlyContinue }
            $stored = $true
            Write-Pass "auto-logon to '$AdminUser' is on again (from the next boot)"
            if ($empty) { Write-Warn "'$AdminUser' has an EMPTY password: anyone at this PC gets an admin desktop" 'only until the club opens' }
        }
        if (-not $stored) { Write-Warn "auto-logon to '$AdminUser' not restored: the PC stops at the sign-in screen" }
    }

    # --- 8. Проверки ---------------------------------------------------------------------------------------------------
    Write-Title 'Checks'
    $left = @(Get-ClubShellEntries)
    if (-not (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue) -and $left.Count -eq 0) { Write-Pass 'ClubShell is gone (no service, no "Programs" entries)' }
    elseif ($removeShell) { Write-Fail ("ClubShell is still here: {0}" -f (($left | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join ', ')) 'reboot, then run this script again' }

    $helperAfter = Get-HelperState
    if ($helperBefore -and $helperBefore.Hash) {
        if (-not $helperAfter) {
            Write-Fail 'service ClubDisklessHelper was removed' $UpdateHelperCmd
        } elseif ($helperAfter.Hash -ne $helperBefore.Hash -and $helperBackup) {
            # exe удалён или подменён удалением старой сборки — вернуть копию и запустить службу.
            Stop-Service -Name ClubDisklessHelper -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $helperAfter.Exe) | Out-Null
            Copy-Item -LiteralPath $helperBackup -Destination $helperAfter.Exe -Force
            Start-Service -Name ClubDisklessHelper -ErrorAction SilentlyContinue
            $helperAfter = Get-HelperState
            if ($helperAfter.Hash -eq $helperBefore.Hash -and $helperAfter.State -eq 'Running') { Write-Pass 'helper exe was changed by the removal: restored from the copy, running' }
            else { Write-Fail 'helper exe changed and could not be restored' $UpdateHelperCmd }
        } elseif ($helperAfter.State -ne 'Running') {
            Start-Service -Name ClubDisklessHelper -ErrorAction SilentlyContinue
            if ((Get-Service -Name ClubDisklessHelper).Status -eq 'Running') { Write-Pass 'helper was stopped: started again' } else { Write-Fail 'helper does not run' $UpdateHelperCmd }
        } else {
            $label = if ($helperAfter.Hash -eq $HelperSha256) { '1.6.0+13609a5' } else { "SHA256 $($helperAfter.Hash)" }
            Write-Pass "helper ClubDisklessHelper untouched and running ($label)"
        }
    }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not ((Get-Volume -DriveLetter G -ErrorAction SilentlyContinue) -and (Get-Volume -DriveLetter H -ErrorAction SilentlyContinue))) { Start-Sleep -Seconds 5 }
    foreach ($letter in @('G', 'H')) {
        if (Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue) { Write-Pass "${letter}: is mounted" }
        else { Write-Warn "${letter}: is not mounted" 'reboot; the helper mounts it within a minute' }
    }
    if ((Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start') -eq 3) { Write-Pass 'USB storage allowed again (USBSTOR Start=3; a stick plugged in now works, an ejected one: re-plug)' }
    Write-Info ("auto-logon now: AutoAdminLogon={0}, DefaultUserName={1}" -f (Get-RegValue $WinlogonKey 'AutoAdminLogon'), (Get-RegValue $WinlogonKey 'DefaultUserName'))

    Write-Title 'In the kassa (owner)'
    Write-Host "  Reinstalling soon: keep $env:COMPUTERNAME in 'Zal i ustroystva' (same hardware = same seat, number and zone)."
    Write-Host "  ClubShell gone from this PC for good: delete $env:COMPUTERNAME in 'Zal i ustroystva'."
    Write-Host '  Reboot this PC now.'
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
    $Count.Fail++
} finally {
    Write-Host ''
    $color = if ($Count.Fail -gt 0) { 'Red' } elseif ($Count.Warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ("Uninstall result for {0}: {1} FAIL, {2} WARN. Log: {3}" -f $env:COMPUTERNAME, $Count.Fail, $Count.Warn, $LogDir) -ForegroundColor $color
    try { Stop-Transcript | Out-Null } catch { }
}
if ($PSCommandPath) { if ($Count.Fail -gt 0) { exit 2 } else { exit 0 } }
