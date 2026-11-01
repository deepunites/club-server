<#
.SYNOPSIS
    Прописывает оболочку ClubShell в шаблон профиля Windows (C:\Users\Default): после сброса профиля игрок сразу
    получает шелл, а не рабочий стол с Пуском.
.DESCRIPTION
    После каждой сессии агент ClubShell удаляет профиль киоска «player» (shell.kioskUser.resetProfileOnLogout, по
    умолчанию включено), и при следующем входе Windows создаёт его заново из C:\Users\Default. В шаблоне нет замены
    оболочки, поэтому userinit запускает explorer.exe: Пуск, окно автозапуска, Проводник поверх киоска. Агент пишет
    шелл в новый профиль и закрывает explorer только 30 с после входа, а первый вход в новый профиль на Windows 11 длится
    дольше (PC-01, 2026-11-02: три сессии, после каждой — Пуск поверх киоска).
    Скрипт пишет в NTUSER.DAT шаблона те же два значения Shell, что агент пишет в профиль киоска (ShellRegistry:
    Winlogon\Shell и Policies\System\Shell), и новый профиль стартует сразу с шеллом ClubShell.
    Касается каждой учётной записи, которая ВПЕРВЫЕ войдёт на этот ПК: новый администратор тоже получит шелл (убрать —
    -Undo). Существующие профили (Club, текущий player) не меняются.
    Перед первой записью шаблон сохраняется в C:\ClubShellKit\backup\default-NTUSER.DAT (не в C:\Users\Default: всё
    оттуда копируется в каждый новый профиль).
    PowerShell от администратора; по сети:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/seal-default-profile.ps1 | iex
    Убрать значения из шаблона:
      & ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/seal-default-profile.ps1))) -Undo
    Запускать между сессиями (игрок не в киоске): шаблон загружен секунду-две. Каждый вход игрока теперь первый вход в
    новый профиль, поэтому скрипт выключает и анимацию первого входа (HKLM EnableFirstLogonAnimation=0).
    Повторный запуск безопасен. Строки вывода — только ASCII.
#>
param([switch] $Undo)

# irm | iex выполняет скрипт в области консоли: настройка ошибок возвращается в finally, чтобы не остаться в консоли.
$savedErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Stop'
try {
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

# Путь из политики кассы (shellReplacement.shellExe) — ровно та строка, которую агент пишет в профиль киоска.
$ShellExe = 'C:\Program Files\ClubShell\Shell\clubshell-shell.exe'
$ShellKeys = @('Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Software\Microsoft\Windows\CurrentVersion\Policies\System')
$Mount = 'ClubShell-DefaultProfile'
$BackupFile = 'C:\ClubShellKit\backup\default-NTUSER.DAT'
$Users = [Microsoft.Win32.Registry]::Users

# reg.exe с проверкой кода выхода. В Windows PowerShell 5.1 перенаправленный stderr внешней программы при 'Stop'
# превращается в исключение, поэтому внутри — 'Continue'.
function Invoke-Reg([string[]] $Arguments) {
    $ErrorActionPreference = 'Continue'
    $out = (& reg.exe @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "reg $($Arguments[0]) failed (exit $LASTEXITCODE): $out" }
}

function Test-Mounted {
    $key = $Users.OpenSubKey($Mount)
    if ($key) { $key.Dispose(); return $true }
    return $false
}

$defaultDir = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').Default)
$hiveFile = Join-Path $defaultDir 'NTUSER.DAT'
if (-not [IO.File]::Exists($hiveFile)) { throw "Default profile hive not found: $hiveFile" }
if (-not $Undo -and -not [IO.File]::Exists($ShellExe)) { throw "ClubShell shell not found: $ShellExe (install ClubShell first)" }

Write-Host "Default profile hive: $hiveFile"
# Остался от прерванного запуска: выгрузить, иначе load не пройдёт.
if (Test-Mounted) { Invoke-Reg @('unload', "HKU\$Mount") }
Invoke-Reg @('load', "HKU\$Mount", $hiveFile)
try {
    if (-not $Undo -and -not [IO.File]::Exists($BackupFile)) {
        New-Item -ItemType Directory -Path (Split-Path $BackupFile) -Force | Out-Null
        # Через временный файл: прерванный save не оставит обрезанную копию, которую следующий запуск примет за готовую.
        Invoke-Reg @('save', "HKU\$Mount", "$BackupFile.tmp", '/y')
        Move-Item -LiteralPath "$BackupFile.tmp" -Destination $BackupFile -Force
        Write-Host "  backup of the untouched template: $BackupFile"
    }
    foreach ($sub in $ShellKeys) {
        if ($Undo) {
            $key = $Users.OpenSubKey("$Mount\$sub", $true)
            if (-not $key) { Write-Host "  no key     $sub"; continue }
        } else {
            $key = $Users.CreateSubKey("$Mount\$sub")
        }
        try {
            $current = $key.GetValue('Shell')
            if ($Undo) {
                # Только наше значение: чужую оболочку в шаблоне не трогаем.
                if ($current -eq $ShellExe) { $key.DeleteValue('Shell'); Write-Host "  removed    $sub\Shell" }
                elseif ($null -eq $current) { Write-Host "  not set    $sub\Shell" }
                else { Write-Host "  kept       $sub\Shell = $current (not ClubShell)" }
            } else {
                $key.SetValue('Shell', $ShellExe, [Microsoft.Win32.RegistryValueKind]::String)
                if ($key.GetValue('Shell') -ne $ShellExe) { throw "Read-back of $sub\Shell does not match" }
                Write-Host "  set        $sub\Shell = $ShellExe"
            }
        } finally {
            $key.Dispose()
        }
    }
} finally {
    # Шаблон, оставшийся загруженным, Windows не скопирует в новый профиль (временный профиль у игрока), поэтому
    # выгрузка — с повторами (ключи держат и чужие процессы, например антивирус).
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    $unloaded = $false
    for ($i = 0; $i -lt 10 -and -not $unloaded; $i++) {
        try { Invoke-Reg @('unload', "HKU\$Mount"); $unloaded = $true }
        catch { if (-not (Test-Mounted)) { $unloaded = $true } else { Start-Sleep -Seconds 1 } }
    }
}
if (-not $unloaded) { throw "HKU\$Mount is still loaded: REBOOT the PC before a player logs on (new profiles cannot be created)." }

if (-not $Undo) {
    $systemPolicy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    New-ItemProperty -LiteralPath $systemPolicy -Name 'EnableFirstLogonAnimation' -Value 0 -PropertyType DWord -Force | Out-Null
    Write-Host '  set        HKLM ...\Policies\System\EnableFirstLogonAnimation = 0'
}

if ($Undo) { Write-Host 'Done: profiles created from now on start the Windows desktop again.' -ForegroundColor Green }
else { Write-Host 'Done: profiles created from now on start ClubShell, not explorer (no Start menu, no AutoPlay window).' -ForegroundColor Green }
} finally {
    $ErrorActionPreference = $savedErrorAction
}
