#Requires -Version 5.1
<#
.SYNOPSIS
    Диагностика: сколько стоит запуск PowerShell и достроены ли образы NGEN (.NET Framework). Можно запускать на месте —
    всё, что скрипт меняет, сбросится при перезагрузке. Лог — рядом со скриптом (ngen-test.log).
#>
$ErrorActionPreference = 'Continue'
$log = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'ngen-test.log'
Start-Transcript -LiteralPath $log -Force | Out-Null

function Measure-PsStart([string]$Title) {
    Write-Host ''
    Write-Host "==> $Title" -ForegroundColor Cyan
    foreach ($i in 1..3) {
        $s = (Measure-Command { & powershell.exe -NoProfile -NonInteractive -Command 'Get-IscsiSession | Out-Null' }).TotalSeconds
        Write-Host ('    запуск PowerShell + Get-IscsiSession: {0:N1} с' -f $s)
    }
}

$ngen64 = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
$ngen32 = "$env:windir\Microsoft.NET\Framework\v4.0.30319\ngen.exe"

Measure-PsStart 'До: время запуска PowerShell'

Write-Host ''
Write-Host '==> Очередь NGEN (64-бит)' -ForegroundColor Cyan
& $ngen64 queue status

Write-Host ''
Write-Host '==> Достраиваю образы NGEN (несколько минут, процессор будет загружен)' -ForegroundColor Cyan
& $ngen64 executeQueuedItems | Select-Object -Last 3
& $ngen32 executeQueuedItems | Select-Object -Last 3

Measure-PsStart 'После: время запуска PowerShell'

Stop-Transcript | Out-Null
Write-Host ''
Write-Host "Готово. Лог: $log" -ForegroundColor Green
