<#
.SYNOPSIS
    Состояние агента ClubShell на этом ПК: служба, версия ClubShell и автовход (Winlogon), адрес сервера, ответ сервера,
    последние строки журнала агента.
.DESCRIPTION
    Только читает, ничего не меняет, ключ ПК не показывает. Результат — на экран и в
    C:\ClubShellKit\logs\agent-diag-<ПК>-<время>.txt (флешки после установки шелла на ПК не читаются).
    Строки вывода — только ASCII.
#>
$ErrorActionPreference = 'Continue'
$out = Join-Path 'C:\ClubShellKit\logs' ('agent-diag-' + $env:COMPUTERNAME + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
& {
    "== $env:COMPUTERNAME $(Get-Date -Format s)"
    '== service'
    Get-Service ClubShellAgent -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize | Out-String
    # Версию пишет MSI; автовход в киоск пишет агент (1.0.20 — с ForceAutoLogon=1: после выхода сразу снова вход).
    '== ClubShell version, auto-logon (never the password)'
    $cs = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\ClubShell' -ErrorAction SilentlyContinue
    'HKLM\SOFTWARE\ClubShell Version: ' + $cs.Version + ', KioskUser: ' + $cs.KioskUser
    $wl = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue
    'Winlogon AutoAdminLogon: ' + $wl.AutoAdminLogon + ', ForceAutoLogon: ' + $wl.ForceAutoLogon
    'Winlogon DefaultUserName: ' + $wl.DefaultUserName
    '== agent.json (server part, never the key)'
    $j = $null
    try { $j = Get-Content -LiteralPath 'C:\ProgramData\ClubShell\agent.json' -Raw -ErrorAction Stop | ConvertFrom-Json } catch { 'agent.json: ' + $_.Exception.Message }
    if ($j) {
        'baseUrl: ' + $j.server.baseUrl
        'wsUrl: ' + $j.server.wsUrl
        'key still in agent.json: ' + [bool]$j.server.clubApiKey
        'kiosk account: ' + $j.shell.kioskUser.name
    }
    'key moved to secure\club.key: ' + (Test-Path -LiteralPath 'C:\ProgramData\ClubShell\secure\club.key')
    '== server answer'
    $origin = if ($j -and $j.server.baseUrl) { $j.server.baseUrl -replace '/api/v\d+/?$', '' } else { 'https://clubshell-server-production.up.railway.app' }
    try { 'health ' + $origin + ': ' + (Invoke-WebRequest -UseBasicParsing -TimeoutSec 15 -Uri ($origin + '/health')).StatusCode } catch { 'health ' + $origin + ': ' + $_.Exception.Message }
    '== last 40 lines of the newest agent log'
    $log = Get-ChildItem -LiteralPath 'C:\ProgramData\ClubShell\logs' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { 'file: ' + $log.FullName; Get-Content -LiteralPath $log.FullName -Tail 40 } else { 'no log files in C:\ProgramData\ClubShell\logs' }
} *>&1 | Out-String -Width 220 | Tee-Object -FilePath $out
Write-Host "Saved to $out" -ForegroundColor Green
