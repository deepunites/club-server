# Adds network card drivers to both images of Windows Setup's boot.wim (1 = WinPE, 2 = Windows Setup), so that WinPE
# brings the NIC up at boot and connects the diskless system image from iBFT (iPXE sanhook). A driver loaded later with
# drvload gives WinPE a network, but the iSCSI boot session stays at "Number of connections: 0" and Setup sees no disk.
# Run on any Windows 10/11 PC in the club network, in PowerShell as Administrator:
#   iwr http://192.168.1.201:5080/pxe/v1/files/tools/add-winpe-driver.ps1 -OutFile $env:TEMP\add-winpe-driver.ps1
#   powershell -ExecutionPolicy Bypass -File $env:TEMP\add-winpe-driver.ps1
# Input on the server (share "incoming", user clubimg): winpe-fix\boot.wim and winpe-fix\drv\*.inf (+ their files).
# Output: winpe-fix\boot-drivers.wim; the server side then replaces /srv/club/pxe/winsetup/sources/boot.wim with it.
param(
    [string]$Share = '\\192.168.1.201\incoming',
    [string]$Work = "$env:SystemDrive\winpe-fix"
)
$ErrorActionPreference = 'Stop'

function Invoke-Dism([string[]]$Arguments) {
    & dism.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dism $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator (DISM needs it).'
}

if (-not (Test-Path "$Share\winpe-fix\boot.wim")) {
    Write-Host "Connecting to $Share as clubimg (type the password you set for clubimg; it is not shown)..."
    net use $Share /user:clubimg *
    if ($LASTEXITCODE -ne 0) { throw "Could not connect to $Share" }
}

$mount = Join-Path $Work 'mount'
if (Test-Path $mount) { & dism.exe /Unmount-Image /MountDir:$mount /Discard *> $null }
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work, $mount, (Join-Path $Work 'drv') | Out-Null

Write-Host 'Copying boot.wim and drivers from the server...'
Copy-Item "$Share\winpe-fix\boot.wim" (Join-Path $Work 'boot.wim')
Copy-Item "$Share\winpe-fix\drv\*" (Join-Path $Work 'drv')
$wim = Join-Path $Work 'boot.wim'

foreach ($index in 1, 2) {
    Write-Host "Image ${index}: adding drivers..."
    Invoke-Dism @('/Mount-Image', "/ImageFile:$wim", "/Index:$index", "/MountDir:$mount")
    try {
        Invoke-Dism @("/Image:$mount", '/Add-Driver', "/Driver:$(Join-Path $Work 'drv')", '/Recurse')
        & dism.exe "/Image:$mount" /Get-Drivers | Select-String -Pattern 'Published Name|Original File Name|Опубликованное имя|Исходное имя файла' | ForEach-Object { $_.Line }
    } catch {
        & dism.exe /Unmount-Image /MountDir:$mount /Discard *> $null
        throw
    }
    Invoke-Dism @('/Unmount-Image', "/MountDir:$mount", '/Commit')
}

Write-Host 'Copying the result back to the server...'
Copy-Item $wim "$Share\winpe-fix\boot-drivers.wim" -Force
$hash = (Get-FileHash $wim -Algorithm SHA256).Hash
Write-Host "Done: $Share\winpe-fix\boot-drivers.wim  SHA256 $hash"
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
