#Requires -Version 5.1
<#
.SYNOPSIS
    Диагностика: ошибки и предупреждения журналов Windows за последние N часов (видеодрайвер, iSCSI, диски, зависания
    приложений) — в файл events.log рядом со скриптом. Ничего не меняет.
#>
param([int]$Hours = 2)
$ErrorActionPreference = 'Continue'
$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'events.log'
$since = (Get-Date).AddHours(-$Hours)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Компьютер: $env:COMPUTERNAME  время: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  за последние $Hours ч")
$gpu = Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) драйвер $($_.DriverVersion) состояние $($_.Status)" }
$lines.Add("Видео: $($gpu -join '; ')")
$events = @(Get-WinEvent -FilterHashtable @{ LogName = 'System', 'Application'; StartTime = $since; Level = 1, 2, 3 } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated)
$lines.Add("Событий (критические, ошибки, предупреждения): $($events.Count)")
$lines.Add('')
foreach ($e in $events) {
    $msg = (($e.Message -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' | '
    if ($msg.Length -gt 400) { $msg = $msg.Substring(0, 400) + '…' }
    $lines.Add(('{0:HH:mm:ss} {1,-8} {2} #{3}: {4}' -f $e.TimeCreated, $e.LevelDisplayName, $e.ProviderName, $e.Id, $msg))
}
$lines | Set-Content -LiteralPath $out -Encoding UTF8
$lines | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Полный список — в $out" -ForegroundColor Green
