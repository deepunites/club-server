<#
.SYNOPSIS
    Задаёт (или убирает) PIN панели администратора киоска ClubShell на этом ПК: Ctrl+Alt+Shift+A на экране оболочки.
.DESCRIPTION
    В MSI 1.0.18 скрипта для PIN не было (SHELL_CHANGES 65); MSI 1.0.20 ставит его сам:
    C:\Program Files\ClubShell\Agent\Install\set-admin-pin.ps1 (тот же формат и тот же файл, но вывод по-русски). Касса
    PIN не задаёт; без PIN панель отвечает «не настроено». Это повтор tools/scripts/set-admin-pin.ps1 из club-shell
    v1.0.18 (MIT) с ASCII-выводом, чтобы работать и из набора, и через irm, без скачивания с GitHub. Формат файла —
    тот, что проверяет агент (OfflineSessionStore.VerifyPassword): pbkdf2$<итерации>$<соль base64>$<хэш base64>,
    PBKDF2-SHA256, соль 16 байт,
    хэш 32 байта, 600 000 итераций (как у set-admin-pin.ps1), ASCII без BOM, в C:\ProgramData\ClubShell\secure\admin-pin.
    Папка secure — только SYSTEM и Administrators (права ей ставит MSI), файл их наследует: игрок хэш не прочитает.
    Агент читает файл при каждой попытке — перезапуск не нужен. shell.json -> kiosk.adminPinHash агент не читает (1.0.18
    молча, 1.0.20 — с предупреждением в журнале).
    PIN — 6-12 цифр, вводит владелец (ввод скрыт), нигде не печатается. На эталоне бездиска PIN задаётся один раз в
    режиме мастера: prepare-image.ps1 файл admin-pin не удаляет.
    -Remove удаляет PIN: панель на этом ПК больше не откроется.
    По сети одной строкой — если файл выложен на сервер клуба (/srv/club/pxe/tools; пока не выложен):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/set-shell-pin.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
param(
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$SecureDir = Join-Path $env:ProgramData 'ClubShell\secure'
$PinFile = Join-Path $SecureDir 'admin-pin'
$Iterations = 600000

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

function Read-Pin([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $secure.Dispose() }
}

$code = 1
if (-not (Test-Path -LiteralPath $SecureDir)) {
    Write-Host "ClubShell is not installed on this PC ($SecureDir is missing). Nothing was changed." -ForegroundColor Red
} elseif ($Remove) {
    if (Test-Path -LiteralPath $PinFile) { Remove-Item -LiteralPath $PinFile -Force }
    Write-Host 'Admin PIN removed: the kiosk admin panel no longer opens on this PC.' -ForegroundColor Yellow
    $code = 0
} else {
    $pin = Read-Pin 'New admin PIN (6-12 digits, hidden)'
    if ($pin -notmatch '^[0-9]{6,12}$') {
        Write-Host 'The PIN must be 6 to 12 digits. Nothing was changed.' -ForegroundColor Red
    } elseif ((Read-Pin 'Repeat the PIN') -ne $pin) {
        Write-Host 'The two PINs differ. Nothing was changed.' -ForegroundColor Red
    } else {
        $salt = New-Object byte[] 16
        $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
        try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
        # Rfc2898DeriveBytes с HashAlgorithmName — .NET Framework 4.7.2+ (в Windows 11 — 4.8.1).
        $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes(([Text.Encoding]::UTF8.GetBytes($pin)), $salt, $Iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
        try { $hash = $kdf.GetBytes(32) } finally { $kdf.Dispose() }
        $encoded = 'pbkdf2$' + $Iterations + '$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($hash)
        [IO.File]::WriteAllText($PinFile, $encoded, (New-Object Text.ASCIIEncoding))
        [Array]::Clear($hash, 0, $hash.Length)
        Write-Host 'Admin PIN saved. On the kiosk screen: Ctrl+Alt+Shift+A -> PIN -> admin panel.' -ForegroundColor Green
        $code = 0
    }
    $pin = $null
}
if ($PSCommandPath) { exit $code }
