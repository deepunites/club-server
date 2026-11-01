<#
.SYNOPSIS
    Задаёт пароль администратора гибрида «Club» (вводит владелец, ввод скрыт). Обязательно до прихода игроков.
.DESCRIPTION
    Зачем: в образах гибридов h1d/h1e у Club пустой пароль и автовход. ClubShell переключает автовход на киоск «player»,
    но Shift при загрузке (или при выходе из сеанса) пропускает автовход и показывает экран входа — и любой выбирает
    Club без пароля: полный администратор. ClubShell этот путь не закрывает (IgnoreShiftOverride он не ставит), и
    закрывать его не нужно: Shift -> Club -> пароль — это наш путь на рабочий стол администратора.
    Что делает (PowerShell от администратора, на гибриде):
      1. спрашивает новый пароль дважды (Read-Host -AsSecureString: не видно на экране, не попадает в журнал);
      2. ставит его Club (Set-LocalUser) и «срок действия пароля не ограничен» — у образов h1b/h1c срок 42 дня;
      3. предупреждает, если автовход сейчас идёт в Club: со следующей загрузки он остановится на экране входа (с
         ClubShell автовход и так идёт в киоск).
    Пароль нигде не сохраняется и не печатается. Записать его туда, где игроки не увидят; один на все гибриды — решение
    владельца. Club из администраторов не убирать: это наша учётка обслуживания (update-helper-net.ps1, install-*.ps1).
    Задачи планировщика с сохранённым паролем Club (preflight их показывает) после смены пароля перестанут запускаться.
    По сети одной строкой — если файл выложен на сервер клуба (/srv/club/pxe/tools; пока не выложен):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/set-club-password.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
param(
    [string]$AdminUser = 'Club',
    [int]$MinLength = 8
)
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

# Две SecureString сравниваются через BSTR в неуправляемой памяти, которая сразу обнуляется.
function Test-SameSecret([Security.SecureString]$A, [Security.SecureString]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    $pa = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($A)
    $pb = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($B)
    try {
        for ($i = 0; $i -lt $A.Length * 2; $i++) {
            if ([Runtime.InteropServices.Marshal]::ReadByte($pa, $i) -ne [Runtime.InteropServices.Marshal]::ReadByte($pb, $i)) { return $false }
        }
        return $true
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pa)
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pb)
    }
}

$code = 1
$account = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
if (-not $account) {
    Write-Host "Local account $AdminUser not found. Nothing was changed." -ForegroundColor Red
} else {
    Write-Host "New password for the local administrator '$AdminUser' (at least $MinLength characters; nothing is shown while typing)."
    $first = Read-Host 'New password' -AsSecureString
    $second = Read-Host 'Repeat it' -AsSecureString
    if ($first.Length -lt $MinLength) {
        Write-Host "Too short: at least $MinLength characters. Nothing was changed." -ForegroundColor Red
    } elseif (-not (Test-SameSecret $first $second)) {
        Write-Host 'The two passwords differ. Nothing was changed.' -ForegroundColor Red
    } else {
        Set-LocalUser -Name $AdminUser -Password $first -PasswordNeverExpires $true
        Write-Host "Password of '$AdminUser' set; it never expires." -ForegroundColor Green
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        $autoUser = (Get-ItemProperty -LiteralPath $winlogon -Name DefaultUserName -ErrorAction SilentlyContinue).DefaultUserName
        $autoOn = (Get-ItemProperty -LiteralPath $winlogon -Name AutoAdminLogon -ErrorAction SilentlyContinue).AutoAdminLogon
        if ($autoOn -eq '1' -and $autoUser -ieq $AdminUser) {
            Write-Host "Auto-logon still goes to '$AdminUser' with the old empty password: from the next boot this PC stops at the sign-in screen." -ForegroundColor Yellow
            Write-Host 'With ClubShell installed the agent moves auto-logon to the kiosk account, so this is expected only without ClubShell.' -ForegroundColor Yellow
        }
        Write-Host "To reach the admin desktop: hold Shift at boot -> '$AdminUser' -> this password. Keep it away from players." -ForegroundColor Green
        $code = 0
    }
    $first.Dispose()
    $second.Dispose()
}
if ($PSCommandPath) { exit $code }
