#Requires -Version 5.1
<#
.SYNOPSIS
    Диагностика производительности: видеокарта (состояние, код ошибки, драйвер, DirectX), процессор (частоты, Turbo),
    электропитание, устройства с ошибками, ошибки видеодрайвера с загрузки — в gpu-diag.log рядом со скриптом. Ничего не меняет.
#>
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $dir 'gpu-diag.log'
$L = New-Object System.Collections.Generic.List[string]
function Add([string]$s) { $L.Add($s) }
function Section([string]$s) { Add ''; Add "== $s" }

Add "Компьютер: $env:COMPUTERNAME  время: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  загрузка: $((Get-CimInstance Win32_OperatingSystem).LastBootUpTime)"

Section 'Видеоадаптеры (Win32_VideoController)'
foreach ($g in Get-CimInstance Win32_VideoController) {
    Add ("  {0} | драйвер {1} | состояние {2} | код ошибки {3} | {4} | {5}x{6} {7} Гц" -f $g.Name, $g.DriverVersion, $g.Status,
        $g.ConfigManagerErrorCode, $g.PNPDeviceID, $g.CurrentHorizontalResolution, $g.CurrentVerticalResolution, $g.CurrentRefreshRate)
}

Section 'Устройства с ошибками (Get-PnpDevice, не OK)'
foreach ($d in @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Status -ne 'OK' })) {
    $code = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data
    Add ("  {0} | {1} | {2} | код {3} | {4}" -f $d.Class, $d.FriendlyName, $d.Status, $code, $d.InstanceId)
}

Section 'Видеокарта NVIDIA: драйвер устройства'
foreach ($d in @(Get-PnpDevice -PresentOnly -Class Display -ErrorAction SilentlyContinue)) {
    foreach ($k in 'DEVPKEY_Device_DriverInfPath', 'DEVPKEY_Device_DriverVersion', 'DEVPKEY_Device_DriverProvider', 'DEVPKEY_Device_ProblemCode', 'DEVPKEY_Device_ProblemStatus') {
        $v = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
        Add ("  {0}: {1} = {2}" -f $d.FriendlyName, ($k -replace 'DEVPKEY_Device_', ''), $v)
    }
}
$svc = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm' -ErrorAction SilentlyContinue
Add "  nvlddmkm: Start=$($svc.Start) ImagePath=$($svc.ImagePath)"
if ($svc.ImagePath) { $sys = $svc.ImagePath -replace '^\\SystemRoot', $env:SystemRoot; Add "  nvlddmkm.sys есть: $(Test-Path -LiteralPath $sys)" }

Section 'Процессор'
foreach ($c in Get-CimInstance Win32_Processor) { Add ("  {0} | сейчас {1} МГц | макс {2} МГц | ядер {3} | потоков {4}" -f $c.Name.Trim(), $c.CurrentClockSpeed, $c.MaxClockSpeed, $c.NumberOfCores, $c.NumberOfLogicalProcessors) }
try {
    $perf = Get-Counter '\Processor Information(_Total)\% Processor Performance', '\Processor Information(_Total)\Processor Frequency' -SampleInterval 1 -MaxSamples 3 -ErrorAction Stop
    foreach ($s in $perf.CounterSamples) { Add ("  {0}: {1:N0}" -f ($s.Path -replace '^.*\\', ''), $s.CookedValue) }
} catch { Add "  счётчики производительности: $($_.Exception.Message)" }

Section 'Электропитание'
Add ("  " + ((& powercfg.exe /getactivescheme) -join ' '))
foreach ($q in @('SUB_PROCESSOR PROCTHROTTLEMIN', 'SUB_PROCESSOR PROCTHROTTLEMAX', 'SUB_PROCESSOR PERFBOOSTMODE')) {
    $a = $q -split ' '
    $txt = (& powercfg.exe /query SCHEME_CURRENT $a[0] $a[1]) -join ' '
    $ac = [regex]::Match($txt, '0x[0-9a-fA-F]+(?=\s*[^0-9a-fA-Fx]*0x[0-9a-fA-F]+\s*$)').Value
    Add ("  {0}: {1}" -f $a[1], (($txt -split '\s{2,}') | Select-Object -Last 2) -join ' ')
}

Section 'DirectX (dxdiag)'
$dx = Join-Path $env:TEMP 'club-dxdiag.txt'
Remove-Item -LiteralPath $dx -ErrorAction SilentlyContinue
Start-Process dxdiag.exe -ArgumentList "/whql:off /t `"$dx`"" -Wait
for ($i = 0; $i -lt 30 -and -not (Test-Path -LiteralPath $dx); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path -LiteralPath $dx) {
    Get-Content -LiteralPath $dx | Where-Object { $_ -match '^\s*(Card name|Manufacturer|Chip type|Display Memory|Dedicated Memory|Driver Version|Driver Model|Feature Levels|Hardware Scheduling|Current Mode|DDI Version|Driver File Version|Имя устройства|Изготовитель|Тип микросхем|Версия драйвера|Модель драйвера|Уровни функций|Текущий режим)\s*:' } |
        Select-Object -First 30 | ForEach-Object { Add "  $($_.Trim())" }
} else { Add '  dxdiag не создал отчёт за 30 с' }

Section 'Ошибки видеодрайвера и устройств с последней загрузки'
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
foreach ($e in @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $boot } -ErrorAction SilentlyContinue |
        Where-Object { $_.LevelDisplayName -and $_.Level -le 3 -and $_.ProviderName -match 'nvlddmkm|Display|Kernel-PnP|DriverFrameworks|UserPnp|Dxgkrnl|DeviceSetupManager' } | Select-Object -First 30)) {
    $m = (($e.Message -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' | '
    Add ("  {0:HH:mm:ss} {1} #{2}: {3}" -f $e.TimeCreated, $e.ProviderName, $e.Id, ($m.Substring(0, [Math]::Min(300, $m.Length))))
}

$L | Set-Content -LiteralPath $out -Encoding UTF8
$L | Select-Object -First 30 | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Полный отчёт — в $out" -ForegroundColor Green
