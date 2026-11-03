# Diskless golden, master mode, PowerShell as Administrator, after AMD Software (Adrenalin) is installed and the
# display settings (GPU scaling, Full panel) are set: turns off the autostart of the AMD Software app.
# The golden is shared with NVIDIA seats (Primer): there the app would start at every logon and complain that no AMD
# driver is present. The display settings live in the driver's registry and work without the app; the services
# (AMD External Events Utility etc.) are not touched. The app still opens by hand from Start.
# By network: irm http://192.168.1.201:5080/pxe/v1/files/tools/amd-no-autostart.ps1 | iex
# Output is ASCII only, no BOM, no #Requires (irm in PowerShell 5.1 reads octet-stream as Latin-1).
$pattern = '\\AMD\\|Radeon|CNext|AMD Software'
$changed = 0
foreach ($task in Get-ScheduledTask) {
    $exec = ($task.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' '
    if ($exec -notmatch $pattern) { continue }
    if ($task.State -eq 'Disabled') { Write-Host "already off: task $($task.TaskPath)$($task.TaskName)"; continue }
    try {
        $task | Disable-ScheduledTask -ErrorAction Stop | Out-Null
        $changed++
        Write-Host "turned off: task $($task.TaskPath)$($task.TaskName) ($exec)" -ForegroundColor Yellow
    } catch { Write-Host "WARN cannot turn off task $($task.TaskName): $($_.Exception.Message)" -ForegroundColor Red }
}
foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
    if (-not (Test-Path -LiteralPath $key)) { continue }
    $props = Get-ItemProperty -LiteralPath $key
    foreach ($p in $props.PSObject.Properties) {
        if ($p.Name -like 'PS*') { continue }
        if ("$($p.Value)" -match $pattern) {
            Remove-ItemProperty -LiteralPath $key -Name $p.Name
            $changed++
            Write-Host "removed: $key\$($p.Name) ($($p.Value))" -ForegroundColor Yellow
        }
    }
}
$svc = Get-Service | Where-Object { $_.DisplayName -match 'AMD' } | ForEach-Object { "$($_.DisplayName) [$($_.Status)]" }
Write-Host ('AMD services left as they are: ' + ($svc -join '; '))
Write-Host "Done: $changed autostart entr(ies) turned off. The display settings stay in the driver." -ForegroundColor Green
