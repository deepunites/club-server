<#
.SYNOPSIS
    Обновляет помощник бездиска на гибриде по сети, без флешки: скачивает с сервера клуба exe, install-helper.ps1 и
    club-ca.pem, сверяет SHA256 и запускает install-helper.ps1 (docs/helper.md, «Гибриды — на месте»).
.DESCRIPTION
    PowerShell от администратора (на гибриде — вход «Club admin»), одной строкой:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex
    Файлы лежат на сервере в /srv/club/pxe/tools: ClubDisklessHelper-<версия>.exe, install-helper-<версия>.ps1,
    club-ca.pem. Сеть клуба — HTTP без TLS: exe сверяется по SHA256 здесь и ещё раз в install-helper.ps1, CA — по
    отпечатку. helper.json не нужен — остаётся установленный.
    Строки вывода — только ASCII: сервер отдаёт файл как application/octet-stream, и irm в PowerShell 5.1 читает его
    как Latin-1 (кириллица в строках превратилась бы в мусор; в комментариях это не мешает). По той же причине без BOM
    и без #Requires.
    После install-helper.ps1 — служба «Steam Client Service» для Steam из библиотеки G: (G:\Steam): игрок — обычный
    пользователь, без службы Steam при обновлении игры просит пароль администратора (UAC). Ставит её
    G:\Steam\bin\SteamService.exe /repair — способ Valve (help.steampowered.com, FAQ 22C0-03D0-AE4B-04E8, «Repair the
    Steam Service»: сначала выйти из Steam, нужны права администратора, идёт несколько минут); exe — только с
    действующей подписью Valve (G: игрок может менять). Этот шаг только
    предупреждает (WARN) и никогда не роняет скрипт: помощник от него не зависит.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы
$Version = '1.9.0'
$ExeSha256 = '1af4465530dd2d17fa287f9d90f11f0250262940193d1dfc88931e66e4c9ac56'
$CaSha256 = '69E23A1433F5A1ECF23FDC5D5B6B21657385DAF24A043B0A21EE891F34648830'
$Base = 'http://192.168.1.201:5080/pxe/v1/files/tools'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$dir = Join-Path $env:TEMP "club-helper-$Version"
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$files = [ordered]@{
    "ClubDisklessHelper-$Version.exe" = 'ClubDisklessHelper.exe'
    "install-helper-$Version.ps1"     = 'install-helper.ps1'
    'club-ca.pem'                     = 'club-ca.pem'
}
foreach ($name in $files.Keys) {
    Write-Host "Downloading $name"
    Invoke-WebRequest -UseBasicParsing -Uri "$Base/$name" -OutFile (Join-Path $dir $files[$name])
}

$exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dir 'ClubDisklessHelper.exe')).Hash
if ($exeHash -ne $ExeSha256.ToUpperInvariant()) { throw "Helper exe SHA256 mismatch ($exeHash): wrong file on the server" }
$ca = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $dir 'club-ca.pem')
$sha = [System.Security.Cryptography.SHA256]::Create()
$caHash = ([BitConverter]::ToString($sha.ComputeHash($ca.RawData))).Replace('-', '')
if ($caHash -ne $CaSha256) { throw "club-ca.pem fingerprint mismatch ($caHash)" }
Write-Host "Files verified: helper $Version, club CA" -ForegroundColor Green

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'install-helper.ps1') -Source $dir
if ($LASTEXITCODE) { Write-Host "install-helper.ps1 exited with code $LASTEXITCODE" -ForegroundColor Yellow }

# Служба Steam: G:\Steam\bin\SteamService.exe /repair (Valve). Только WARN, без исключений наружу.
function Install-SteamClientService {
    $svcExe = 'G:\Steam\bin\SteamService.exe'   # строкой, не Join-Path: в 5.1 Join-Path падает, если диска G: нет
    $timeoutSec = 300
    if (-not (Test-Path -LiteralPath 'G:\')) {
        Write-Host 'WARN Steam Client Service: G: is not mounted (no Online Games library session), skipped' -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path -LiteralPath 'G:\Steam\steam.exe')) {
        Write-Host 'WARN Steam Client Service: G:\Steam\steam.exe not found (no Steam in the G: library), skipped' -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path -LiteralPath $svcExe)) {
        Write-Host "WARN Steam Client Service: $svcExe not found, skipped" -ForegroundColor Yellow
        return
    }
    # Valve: перед /repair выйти из Steam. steam.exe в сеансе игрока виден и отсюда (администратор видит все процессы).
    $running = @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host ("WARN Steam Client Service: steam.exe is running (PID {0}): Valve's repair needs Steam closed - close Steam and run again" -f (($running | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Yellow
        return
    }
    # G: — диск места, игрок может в него писать: запускать от администратора только exe с действующей подписью Valve
    # (подписант сейчас «Valve Corp.», у прежних сборок «Valve Corporation»). Проверка — прямо перед запуском.
    $sig = Get-AuthenticodeSignature -LiteralPath $svcExe
    $signer = ''
    if ($sig.SignerCertificate) {
        $signer = $sig.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    }
    if ([string]$sig.Status -ne 'Valid' -or @('Valve Corp.', 'Valve Corporation') -notcontains $signer) {
        Write-Host "WARN Steam Client Service: $svcExe signature is not a valid Valve signature (Status=$($sig.Status), Signer='$signer'), not run as admin; restore Steam in the G: library" -ForegroundColor Yellow
        return
    }
    Write-Host "Steam Client Service: $svcExe signed by $signer"
    Write-Host "Steam Client Service: $svcExe /repair (up to $timeoutSec s)"
    $proc = Start-Process -FilePath $svcExe -ArgumentList '/repair' -PassThru -WindowStyle Hidden
    $null = $proc.Handle   # без этого в 5.1 ExitCode после WaitForExit бывает пустым
    if (-not $proc.WaitForExit($timeoutSec * 1000)) {
        Write-Host "WARN Steam Client Service: SteamService.exe /repair still running after $timeoutSec s (PID $($proc.Id)), left running; run this line again later" -ForegroundColor Yellow
    } else {
        Write-Host "SteamService.exe /repair exited with code $($proc.ExitCode)"
    }
    $svc = @(Get-CimInstance -ClassName Win32_Service -Filter "Name='Steam Client Service' OR DisplayName='Steam Client Service'" -ErrorAction SilentlyContinue | Where-Object { $_ })
    if ($svc.Count -gt 0) {
        Write-Host ("PASS service ""Steam Client Service"" exists: Status={0}, StartType={1}, Path={2}" -f $svc[0].State, $svc[0].StartMode, $svc[0].PathName) -ForegroundColor Green
    } else {
        Write-Host 'WARN service "Steam Client Service" is still missing: Steam as a standard user will ask for admin rights on updates' -ForegroundColor Yellow
    }
}
try { Install-SteamClientService }
catch { Write-Host "WARN Steam Client Service step failed: $($_.Exception.Message) (the helper install above is not affected)" -ForegroundColor Yellow }
