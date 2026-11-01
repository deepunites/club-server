<#
.SYNOPSIS
    Меняет ключ ПК (X-Club-Key) у уже установленного ClubShell без переустановки.
.DESCRIPTION
    Ключ решает, в какой клуб на сервере шелла попадёт ПК: с ключом другого клуба агент регистрируется и ждёт одобрения
    там, а в кассе нашего клуба ПК не видно. Агент читает ключ сначала из agent.json (server.clubApiKey), потом из
    secure\club.key, и при старте сам переносит ключ из agent.json в secure\club.key (SHELL_CHANGES 41).
    Что делает (PowerShell от администратора; по сети:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/shell-set-key.ps1 | iex):
      1. спрашивает новый ключ (ввод скрыт), чистит буфер обмена;
      2. останавливает службу ClubShellAgent, пишет ключ в agent.json, запускает службу;
      3. ждёт, пока агент перенесёт ключ в secure\club.key (agent.json снова без ключа).
    Ключ на экран и в журналы не выводится. Строки вывода — только ASCII.
#>
$ErrorActionPreference = 'Stop'
$AgentJson = 'C:\ProgramData\ClubShell\agent.json'
$ClubKey = 'C:\ProgramData\ClubShell\secure\club.key'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}
if (-not (Test-Path -LiteralPath $AgentJson)) { throw 'ClubShell is not installed on this PC (no agent.json). Nothing was changed.' }
if (-not (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)) { throw 'Service ClubShellAgent is missing. Nothing was changed.' }

Write-Host 'Type or paste the PC key of YOUR club (right click pastes); nothing is shown while you type.'
$secure = Read-Host 'PC key' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $secure.Dispose() }
try { Set-Clipboard -Value ' ' } catch { }
if ($key -notmatch '^[A-Za-z0-9._-]{8,128}$') { $key = $null; throw 'The key does not look right (8-128 characters: letters, digits, dot, dash, underscore). Nothing was changed.' }

$keyTimeBefore = if (Test-Path -LiteralPath $ClubKey) { (Get-Item -LiteralPath $ClubKey).LastWriteTimeUtc } else { [datetime]::MinValue }
Write-Host 'Stopping ClubShellAgent'
Stop-Service -Name ClubShellAgent -Force
try {
    $config = Get-Content -LiteralPath $AgentJson -Raw | ConvertFrom-Json
    if (-not $config.server) { throw 'agent.json has no "server" section. Nothing was changed.' }
    if ($config.server.PSObject.Properties.Name -contains 'clubApiKey') { $config.server.clubApiKey = $key }
    else { $config.server | Add-Member -NotePropertyName clubApiKey -NotePropertyValue $key }
    [IO.File]::WriteAllText($AgentJson, ($config | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding $false))
    $key = $null
    $config = $null
    Write-Host 'New key written to agent.json'
} finally {
    Write-Host 'Starting ClubShellAgent'
    Start-Service -Name ClubShellAgent
}

$deadline = (Get-Date).AddSeconds(60)
$moved = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $now = Get-Content -LiteralPath $AgentJson -Raw | ConvertFrom-Json
    if (-not $now.server.clubApiKey -and (Test-Path -LiteralPath $ClubKey) -and (Get-Item -LiteralPath $ClubKey).LastWriteTimeUtc -gt $keyTimeBefore) { $moved = $true; break }
}
if ($moved) {
    Write-Host 'Done: the agent moved the new key into secure\club.key and registers again.' -ForegroundColor Green
    Write-Host 'The PC should appear in the kassa ("Hall and devices") as waiting for approval within a minute.'
} else {
    Write-Host 'The agent has not moved the key yet: check the agent log (shell-agent-diag) in a minute.' -ForegroundColor Yellow
}
