<#
.SYNOPSIS
    Брандмауэр Windows для сетевой игры по локалке клуба (CS 1.6, CS2 и другие игры Valve): впускает игровой UDP от ПК
    клуба и убирает правила-блокировки, которые Windows создала для игр.
.DESCRIPTION
    Почему не работает без него: входящие по умолчанию запрещены. Когда игра поднимает сервер, Windows показывает окно
    «Разрешить доступ»; оно прячется за полноэкранной игрой, а «Отмена» (у не-администратора — любой ответ) создаёт
    правила Block для этой программы. Block сильнее любого Allow, поэтому ни вкладка LAN, ни connect до сервера не доходят.
    Что делает (PowerShell от администратора; на эталоне бездиска — в режиме мастера, до seal-golden.ps1):
      1. выключает это окно на всех профилях (NotifyOnListen = False): правила-блокировки больше не появляются;
      2. удаляет входящие правила Block, созданные этим окном («Query User», для любой программы — без них остаётся
         обычный запрет по умолчанию), и любые входящие Block для игр hl.exe, cs2.exe, csgo.exe, hlds.exe, srcds.exe;
      3. создаёт правило группы «Club LAN games»: входящий UDP 27000-27050 только с адресов ПК клуба
         (192.168.1.101-199 — места, .201 — сервер клуба для проверки), на всех профилях (Public/Private не важен).
         Игровой порт 27015, вкладка LAN ищет на 27015-27020, запас — второй сервер на одном ПК.
    Окно больше не спрашивает ни про одну программу: сетевой игре не из Valve (другие порты) нужно своё правило —
    добавить строку в $Rules и запустить скрипт снова.
    Сеть остаётся Public (ПК не видят друг друга в «Сети», общих папок нет), брандмауэр остаётся включённым.
    Повторный запуск безопасен (правила группы пересоздаются). Удалить: Remove-NetFirewallRule -Group 'Club LAN games'.
    По сети одной строкой: irm http://192.168.1.201:5080/pxe/v1/files/tools/install-lan-firewall.ps1 | iex
    Строки вывода — только ASCII, без BOM и без #Requires (irm в PowerShell 5.1 читает octet-stream как Latin-1).
#>
$ErrorActionPreference = 'Stop'
$Group = 'Club LAN games'
# Места: 192.168.1.(100+N), N = 1..99 (docs/network.md); пул .20-.99 (телефоны, Wi-Fi) сюда не входит.
$Remote = @('192.168.1.101-192.168.1.199', '192.168.1.201')
$Rules = @(
    @{ Name = 'Club-LAN-Games-UDP'; DisplayName = 'Club LAN games UDP 27000-27050'; Protocol = 'UDP'; LocalPort = '27000-27050' }
)
$games = @('hl.exe', 'cs2.exe', 'csgo.exe', 'hlds.exe', 'srcds.exe')

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

function Get-GameBlockRules {
    # Правила окна «Разрешить доступ» называются «TCP/UDP Query User{GUID}<путь>». Имя файла — через -split, а не
    # Path.GetFileName: тот бросает исключение на кавычках в пути чужого правила.
    $found = @{}
    Get-NetFirewallRule -PolicyStore PersistentStore -Direction Inbound -Action Block -ErrorAction SilentlyContinue | ForEach-Object {
        $program = ($_ | Get-NetFirewallApplicationFilter).Program
        $leaf = if ($program) { (($program -split '\\')[-1]).ToLowerInvariant() } else { '' }
        if ($_.Name -like '*Query User*' -or $games -contains $leaf) { $found[$_.Name] = @{ Rule = $_; Program = $program } }
    }
    $found.Values
}

Write-Host 'Turning off the "allow access" prompt on all firewall profiles'
Set-NetFirewallProfile -All -NotifyOnListen False

$block = @(Get-GameBlockRules)
foreach ($b in $block) {
    Write-Host "Removing block rule '$($b.Rule.DisplayName)' $($b.Program)"
    # Удаляем сам объект: -Name понимает [ ] как шаблон, и правило с «[Rus]» в пути молча осталось бы.
    $b.Rule | Remove-NetFirewallRule
}
if ($block.Count -eq 0) { Write-Host 'No block rules to remove' }
$left = @(Get-GameBlockRules)
if ($left.Count -gt 0) { throw "Block rules still present: $(($left | ForEach-Object { $_.Rule.DisplayName }) -join ', ')" }

Remove-NetFirewallRule -Group $Group -PolicyStore PersistentStore -ErrorAction SilentlyContinue
foreach ($r in $Rules) {
    Write-Host "Adding rule '$($r.DisplayName)' from club PCs, all profiles"
    New-NetFirewallRule -Name $r.Name -DisplayName $r.DisplayName -Group $Group -Direction Inbound -Action Allow `
        -Protocol $r.Protocol -LocalPort $r.LocalPort -RemoteAddress $Remote -Profile Any -PolicyStore PersistentStore | Out-Null
}

Write-Host ''
$shieldsUp = $false
Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object {
    Write-Host ("Firewall {0}: enabled={1} notify={2} allowInboundRules={3}" -f $_.Name, $_.Enabled, $_.NotifyOnListen, $_.AllowInboundRules)
    if ($_.AllowInboundRules -eq 'False') { $shieldsUp = $true }
}
Get-NetFirewallRule -Group $Group | ForEach-Object {
    $port = $_ | Get-NetFirewallPortFilter
    $addr = $_ | Get-NetFirewallAddressFilter
    Write-Host ("{0} {1} {2} {3} from {4} enabled={5} profile={6}" -f $_.DisplayName, $_.Action, $port.Protocol, $port.LocalPort, ($addr.RemoteAddress -join ','), $_.Enabled, $_.Profile)
}
if ($shieldsUp) {
    Write-Host 'WARNING: a profile has "block all incoming connections" on (allowInboundRules=False): allow rules are ignored there.' -ForegroundColor Yellow
}
Write-Host 'Done: LAN games allowed through Windows Firewall' -ForegroundColor Green
