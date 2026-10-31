<#
.SYNOPSIS
    Топ-зона (Gigabyte B650 EAGLE AX): драйвер чипсета AMD на всех ПК и драйвер видеокарты AMD (Adrenalin, только
    драйвер) на ПК с Radeon RX 9070 — после заливки образа h1e.
.DESCRIPTION
    PowerShell от администратора (вход «Club admin»), одной строкой:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/install-amd-top.ps1 | iex
    Скачивает с сервера клуба (/srv/club/pxe/tools, скачаны с drivers.amd.com 2026-10-31):
      amd-chipset-8.09.28.1124.exe            — AMD Chipset Software 8.09.28.1124 (все ПК);
      amd-adrenalin-26.10.2-win11.exe         — AMD Software: Adrenalin Edition 26.10.2 WHQL (только если в ПК есть
                                                 видеокарта AMD, PCI\VEN_1002).
    Сверяет SHA256 и подпись Authenticode (Advanced Micro Devices), запускает установщики по очереди и ждёт их.
    Установщики оконные: в чипсете — «Install» с компонентами по умолчанию; в Adrenalin — Additional Options →
    Software Type = «Driver Only», «Factory Reset» не отмечать. После обоих — перезагрузка.
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы
$Base = 'http://192.168.1.201:5080/pxe/v1/files/tools'
$Chipset = @{ File = 'amd-chipset-8.09.28.1124.exe'; Sha256 = '57ed1aa9ac842d8ad0064fced2524e9aab66c5ba74ed90c0135b34f53278ac7f' }
$Adrenalin = @{ File = 'amd-adrenalin-26.10.2-win11.exe'; Sha256 = 'b003a0caa9f9d53f957539530171f6be467dbd90bbb3d8964c43d6de68da0d2b' }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$dir = 'C:\ClubDrivers'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

function Get-Verified($item) {
    $path = Join-Path $dir $item.File
    $ok = $false
    if (Test-Path -LiteralPath $path) { $ok = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -eq $item.Sha256.ToUpperInvariant() }
    if (-not $ok) {
        Write-Host "Downloading $($item.File)"
        Invoke-WebRequest -UseBasicParsing -Uri "$Base/$($item.File)" -OutFile $path
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
        if ($hash -ne $item.Sha256.ToUpperInvariant()) { throw "$($item.File): SHA256 mismatch ($hash)" }
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Advanced Micro Devices') {
        throw "$($item.File): signature is $($sig.Status), signer '$($sig.SignerCertificate.Subject)'"
    }
    Write-Host "Verified $($item.File): SHA256 and AMD signature OK" -ForegroundColor Green
    $path
}

$amdGpu = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'PCI\VEN_1002*' })
$gpuNames = (Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }) -join ', '
Write-Host "Video: $gpuNames"

$chipsetExe = Get-Verified $Chipset
Write-Host ''
Write-Host 'AMD Chipset Software: click Install (default components), wait for it to finish. Do NOT reboot yet.' -ForegroundColor Cyan
Start-Process -FilePath $chipsetExe -Wait

if ($amdGpu.Count -gt 0) {
    $adrenalinExe = Get-Verified $Adrenalin
    Write-Host ''
    Write-Host 'AMD Adrenalin: Additional Options -> Software Type = Driver Only. Do NOT tick Factory Reset. Then Install.' -ForegroundColor Cyan
    Start-Process -FilePath $adrenalinExe -Wait
    # Распаковка установщика AMD (может весить больше 1 ГБ) на 120 ГБ SSD не нужна после установки.
    Remove-Item -LiteralPath 'C:\AMD' -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host 'No AMD video card here (NVIDIA PC): only the chipset driver is installed.'
}

Write-Host ''
Write-Host 'Done. Reboot the PC now.' -ForegroundColor Green
