# Diskless golden, master mode, PowerShell as Administrator: removes StartOverride from the boot NIC driver services.
# Windows demotes boot-start drivers whose device is absent on the board it just booted (StartOverride\0 = 3): after a
# master session on a Realtek board (H510M K) it demotes e1dexpress, on an Intel board rt640x64. Published like that,
# every seat with the other card stops with 0x7B. prepare-golden.ps1 checks only the card of the board it runs on.
# Run right before seal-golden.ps1 (no reboot in between). By network: irm http://192.168.1.201:5080/pxe/v1/files/tools/clear-nic-startoverride.ps1 | iex
# Output is ASCII only, no BOM, no #Requires (irm in PowerShell 5.1 reads octet-stream as Latin-1).
$bad = 0
foreach ($name in 'e1dexpress', 'e1iexpress', 'e2fexpress', 'rt640x64', 'rtcx21x64') {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
    if (-not (Test-Path -LiteralPath $key)) { continue }
    if (Test-Path -LiteralPath "$key\StartOverride") {
        Remove-Item -LiteralPath "$key\StartOverride" -Recurse -Force
        Write-Host "removed: $name\StartOverride" -ForegroundColor Yellow
    }
    $p = Get-ItemProperty -LiteralPath $key
    $override = Test-Path -LiteralPath "$key\StartOverride"
    $line = '{0}: Start={1} BootFlags={2} StartOverride={3}' -f $name, $p.Start, $p.BootFlags, $override
    # rtcx21x64 (NetAdapterCx) is not a boot driver of the golden: only its override matters.
    if ($override -or ($name -ne 'rtcx21x64' -and $null -ne $p.ImagePath -and ($p.Start -ne 0 -or $p.BootFlags -ne 1))) {
        $bad++
        Write-Host "WARN $line" -ForegroundColor Red
    } else {
        Write-Host "PASS $line" -ForegroundColor Green
    }
}
if ($bad) { Write-Host "$bad WARN: take a photo, do NOT run seal-golden" -ForegroundColor Red }
else { Write-Host 'OK: now run seal-golden (no reboot in between)' -ForegroundColor Green }
