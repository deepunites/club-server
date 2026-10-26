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
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы
$Version = '1.6.0'
$ExeSha256 = '26635a5f7aab4d77441d0b0138064b9c7f64bb1daf4b0bcaf09052439ddd0148'
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
