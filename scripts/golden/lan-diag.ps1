<#
.SYNOPSIS
    Диагностика сетевой игры по локалке (CS 1.6, CS2): профиль сети, брандмауэр Windows, правила для игр, кто слушает
    игровые порты 27000-27050.
.DESCRIPTION
    Только читает, ничего не меняет. Запускать на ПК, где создан сервер (игра с сервером запущена), PowerShell от
    администратора, одной строкой:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/lan-diag.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
$ErrorActionPreference = 'Continue'
$games = @('hl.exe', 'cs2.exe', 'csgo.exe', 'hlds.exe', 'srcds.exe')

Write-Host "== $env:COMPUTERNAME"
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '192.168.*' } |
    ForEach-Object { Write-Host "IP $($_.IPAddress)/$($_.PrefixLength) on $($_.InterfaceAlias)" }
Get-NetConnectionProfile -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "Network '$($_.Name)' on $($_.InterfaceAlias): $($_.NetworkCategory)" }
Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("Firewall {0}: enabled={1} inbound={2} allowInboundRules={3} notify={4}" -f $_.Name, $_.Enabled, $_.DefaultInboundAction, $_.AllowInboundRules, $_.NotifyOnListen) }

Write-Host ''
Write-Host '== Firewall rules for games (programs on G:, H: or named like a game)'
$found = 0
# Имя файла — через -split, а не Path.GetFileName: тот бросает исключение на кавычках в пути чужого правила.
Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object {
    $_.Program -and $_.Program -ne 'Any' -and (
        ($games -contains (($_.Program -split '\\')[-1]).ToLowerInvariant()) -or
        $_.Program -like 'G:\*' -or $_.Program -like 'H:\*')
} | ForEach-Object {
    $program = $_.Program
    $_ | Get-NetFirewallRule -ErrorAction SilentlyContinue | ForEach-Object {
        $found++
        Write-Host ("{0} {1} enabled={2} profile={3} store={4} '{5}' {6}" -f $_.Direction, $_.Action, $_.Enabled, $_.Profile, $_.PolicyStoreSourceType, $_.DisplayName, $program)
    }
}
if ($found -eq 0) { Write-Host 'none' }

Write-Host ''
Write-Host '== Inbound rules for any program that cover UDP 27015'
$found = 0
function Test-Covers27015($ports) {
    foreach ($p in @($ports)) {
        if ($p -eq 'Any') { return $true }
        if ($p -match '^(\d+)-(\d+)$') { if ([int]$Matches[1] -le 27015 -and [int]$Matches[2] -ge 27015) { return $true } }
        elseif ($p -match '^\d+$' -and [int]$p -eq 27015) { return $true }
    }
    $false
}
Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Where-Object {
    ($_.Protocol -eq 'UDP' -or $_.Protocol -eq 'Any') -and (Test-Covers27015 $_.LocalPort)
} | ForEach-Object {
    $_ | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
        $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' -and (($_ | Get-NetFirewallApplicationFilter).Program -eq 'Any') -and
        (($_ | Get-NetFirewallServiceFilter).Service -eq 'Any')
    } | ForEach-Object {
        $found++
        $addr = ($_ | Get-NetFirewallAddressFilter).RemoteAddress -join ','
        Write-Host ("{0} profile={1} store={2} from={3} '{4}'" -f $_.Action, $_.Profile, $_.PolicyStoreSourceType, $addr, $_.DisplayName)
    }
}
if ($found -eq 0) { Write-Host 'none' }

Write-Host ''
Write-Host '== Programs listening on UDP 27000-27050'
$listeners = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -ge 27000 -and $_.LocalPort -le 27050 })
foreach ($l in $listeners) {
    $p = Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue
    Write-Host ("UDP {0}:{1} {2} {3}" -f $l.LocalAddress, $l.LocalPort, $p.ProcessName, $p.Path)
}
if ($listeners.Count -eq 0) { Write-Host 'none: no game server is running on this PC right now' }
Write-Host ''
Write-Host 'Done. Take a photo of this window.' -ForegroundColor Green
