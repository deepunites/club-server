<#
.SYNOPSIS
    Ставит Microsoft Edge WebView2 Runtime (Evergreen) перед установкой ClubShell.
.DESCRIPTION
    Без WebView2 оболочка ClubShell не работает. Установщик ClubShell 1.0.18 качает загрузчик WebView2 сам, но сверяет
    его с контрольной суммой, зашитой при сборке релиза; Microsoft с тех пор обновила файл, и установка шелла падает с
    кодом 0x80091007 (CRYPT_E_HASH_VALUE). Если WebView2 уже стоит, установщик шелла этот шаг пропускает.
    Что делает (PowerShell от администратора, нужен интернет):
      1. если WebView2 Runtime уже зарегистрирован (HKLM ...\EdgeUpdate\Clients\{F3017226-...}, pv) — ничего;
      2. скачивает официальный загрузчик Evergreen с go.microsoft.com, проверяет подпись Microsoft;
      3. ставит тихо (/silent /install) и проверяет, что версия зарегистрирована.
    -FromInstall — запуск из shell-install.ps1 (его шаг 3): без подсказки «теперь запустите shell-install», он уже идёт.
    Строки вывода — только ASCII.
#>
param([switch]$FromInstall)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
function Get-WebView2Version {
    try { (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).pv } catch { $null }
}

$version = Get-WebView2Version
if ($version -and $version -ne '0.0.0.0') {
    Write-Host "WebView2 Runtime $version is already installed: nothing to do" -ForegroundColor Green
    return
}

$exe = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
Write-Host 'Downloading the WebView2 Evergreen bootstrapper from Microsoft (go.microsoft.com)'
Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $exe
$sig = Get-AuthenticodeSignature -LiteralPath $exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    throw "The downloaded file is not signed by Microsoft (signature $($sig.Status), signer '$($sig.SignerCertificate.Subject)')."
}
Write-Host 'Microsoft signature OK; installing WebView2 Runtime (1-2 minutes, no windows)'
$process = Start-Process -FilePath $exe -ArgumentList '/silent', '/install' -Wait -PassThru
Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue

$version = Get-WebView2Version
if ($version -and $version -ne '0.0.0.0') {
    Write-Host "Done: WebView2 Runtime $version installed (exit code $($process.ExitCode))." -ForegroundColor Green
    if (-not $FromInstall) { Write-Host 'Now run shell-install-run-as-admin.cmd.' -ForegroundColor Green }
} else {
    throw "WebView2 Runtime is not registered after the install (exit code $($process.ExitCode))."
}
