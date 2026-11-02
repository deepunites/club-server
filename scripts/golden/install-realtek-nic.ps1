<#
.SYNOPSIS
    Гибриды зоны Mirage (Realtek RTL8111H, PCI\VEN_10EC&DEV_8168): замена встроенного драйвера Windows rtcx21x64
    (NetAdapterCx) на NDIS-драйвер Realtek rt640x64, выключение энергосбережения карты и сторож при загрузке.
.DESCRIPTION
    Почему: на 5 ПК Mirage (Ryzen 5600X, MSI B450 TOMAHAWK MS-7C02 и X470 GAMING PLUS MAX MS-7B79) со встроенным
    драйвером «Realtek PCIe GbE Family Controller» 1.0.0.14 (rtcx21x64.sys, 2017) карта время от времени перестаёт
    ПРИНИМАТЬ пакеты, а линк остаётся 1 Гбит/с (события 27 нет): иногда оживает сама через ~3 минуты, иногда — только
    после переподключения кабеля. Лечение — драйвер Realtek NDIS rt640x64.sys (пакет MSI realtek_pcielan_w11.zip) и
    выключенные EEE / Green Ethernet / энергосбережение карты и PCIe ASPM. Подробно — docs/realtek-nic.md.
    Что делает (PowerShell от администратора, вход «Club»; Win+X -> «Терминал (администратор)»):
      1. находит карты PCI\VEN_10EC&DEV_8168, печатает драйвер, ИД оборудования; нет карт — ничего не делает;
      2. печатает события System за 7 дней (только чтение): HAL 15 (DMA remapping / IOMMU), NDIS 10400/10401 (сброс
         карты), Tcpip 4202, 27/32 (линк) от Realtek, NCSI;
      3. скачивает набор с сервера клуба в C:\ClubDrivers\realtek-nic (права — только SYSTEM и Administrators, владелец —
         Administrators, у C:\ClubDrivers тоже: из этой папки задача-сторож запускает скрипт от SYSTEM; ссылка
         junction/symlink вместо папки или внутри неё — отказ): SHA256SUMS, rt640x64.inf/.sys/.cat и этот же скрипт.
         Изменившиеся файлы сначала скачиваются в <имя>.part и сверяются по SHA256; на место (SHA256SUMS — последним)
         они встают, только когда сверены ВСЕ — иначе .part удаляются, старый набор не тронут. Верный файл не качается
         снова. Всё скачивается ДО замены драйвера (при замене сеть пропадает на секунды). Версия — из DriverVer INF;
      4. карта уже на rt640x64.sys не старше набора — замены нет; иначе предупреждает про игры с G:/H:, спрашивает Y/n,
         pnputil /add-driver <inf> /install и ждёт до 60 с, пока у карты не станет DriverFileName = rt640x64.sys; не
         стало — принудительно UpdateDriverForPlugAndPlayDevices (newdev.dll, INSTALLFLAG_FORCE) по самому точному ИД
         оборудования карты, который есть в INF, и снова ждёт. Итог по карте:
           - на rt640x64.sys — OK;
           - устройство уже настроено на rt640x64 (служба из INF), но pnputil ответил 3010 / «нужна перезагрузка» или
             устройство ждёт перезагрузки (код 14) — WARN: замена закончится при перезагрузке, сторож ставится (настройки
             карты он выставит при загрузке), вопрос о перезагрузке — по умолчанию «да»;
           - настроено на rt640x64, но сетевой карты нет или у устройства код ошибки — FAIL, сети может не быть;
           - не настроено — FAIL; «старый драйвер работает» — только если это проверено (карта Up на прежнем файле).
         На FAIL больше ничего не меняется, а откат печатается локальный (из C:\ClubDrivers, без сети);
      5. ждёт линк и IPv4 (до 60 с) и проверяет http://192.168.1.201:5080/health;
      6. ключевые слова драйвера (только если они у карты есть): *EEE, AdvancedEEE, EnableGreenEthernet, GigaLite,
         PowerSavingMode = 0 (с -NoRestart, вступят в силу после перезагрузки); *FlowControl, *SpeedDuplex,
         *JumboPacket и WoL только печатаются. «Разрешить отключение устройства для экономии энергии» — выкл. через
         Set-NetAdapterPowerManagement (не Disable-NetAdapterPowerManagement: тот выключил бы и WoL). PCIe ASPM активной
         схемы питания — выкл. (powercfg ... SUB_PCIEXPRESS ASPM 0, от сети и от батареи);
      7. задача-сторож ClubRealtekNicGuard (SYSTEM, при загрузке, наивысшие права):
           powershell -NoProfile -ExecutionPolicy Bypass -File C:\ClubDrivers\realtek-nic\install-realtek-nic.ps1 -Guard
         Обновление Windows (feature update) может вернуть встроенный драйвер — сторож ставит rt640x64 обратно из
         локальных файлов. Локальная копия скрипта — тот же файл из набора на сервере (он в SHA256SUMS), а не текст,
         выполняемый через iex: так сторож запускает ровно то, что сверено по SHA256. Перед регистрацией владелец папок
         и файлов снова ставится Administrators и проверяется: не так — сторож не ставится (FAIL). После регистрации —
         пробный запуск;
      8. итог (OK / WARN / FAIL) и вопрос о перезагрузке (есть FAIL — по умолчанию нет).
    Встроенный драйвер (компонент Microsoft.Windows.Ethernet.Client.Realtek.Rtcx21x64) НЕ удаляется: если rt640x64
    откажет, карта останется с драйвером. На бездиске (системный диск по iSCSI) скрипт отказывается работать: драйвер
    живой загрузочной карты менять нельзя (синий экран).
    Одной строкой:
      irm http://192.168.1.201:5080/pxe/v1/files/tools/install-realtek-nic.ps1 | iex
    Без вопроса о перезагрузке:
      & ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/install-realtek-nic.ps1))) -NoReboot
    Откат (задача удаляется, пакеты rt640x64.inf — pnputil /delete-driver ... /uninstall /force, карта возвращается на
    встроенный драйвер; перед вопросом проверяется, что встроенный драйвер есть — компонент Installed или rtcx21x64.sys в
    хранилище драйверов; нет — WARN и по умолчанию «нет», иначе карта осталась бы без драйвера и без сети; ASPM и
    питание карты не возвращаются — скрипт печатает, как это сделать):
      & ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/install-realtek-nic.ps1))) -Undo
    Без сети (карта мертва) — то же из локальной копии:
      powershell -NoProfile -ExecutionPolicy Bypass -File C:\ClubDrivers\realtek-nic\install-realtek-nic.ps1 -Undo
    Режим -Guard (задача при загрузке): без сети и без вопросов; сверяет локальные файлы с локальным SHA256SUMS; карта
    не на rt640x64.sys — pnputil (и принудительная установка) из локальных файлов; rt640x64 уже стоит и ждёт перезагрузки
    — ничего не делает; настройки — только у карты, работающей на rt640x64, и только если значение отличается и
    поменялось, тогда карта перезапускается один раз; журнал C:\ClubDrivers\realtek-nic\logs\guard.log (ограничен по
    размеру). Исключение из задачи не выходит никогда. Без сети его же можно запустить руками — он ставит rt640x64 из
    локального набора:
      powershell -NoProfile -ExecutionPolicy Bypass -File C:\ClubDrivers\realtek-nic\install-realtek-nic.ps1 -Guard
    Журнал запуска — C:\ClubDrivers\realtek-nic\logs\<ПК>-<время>.txt.
    Перезаливка гибрида образом всё это убирает (драйвер, настройки, задачу, папку): rt640x64 и настройки нужно потом
    добавить в образ гибрида.
    Выполняется в области консоли (irm | iex): без param и без exit на пути iex — ключи (-Guard, -Undo, -NoReboot)
    берутся из $args, exit — только при запуске файлом (-File). Строки вывода — только ASCII, без BOM и без #Requires
    (irm в PowerShell 5.1 читает octet-stream как Latin-1). Совместим с Windows PowerShell 5.1.
#>
# Ключи — из $args (под iex param() не работает): -Guard, -Undo, -NoReboot. Захват — до определения функций, в них
# $args свой.
$scriptArgs = @($args)
# irm | iex выполняет скрипт в области консоли: настройки возвращаются в finally, чтобы не остаться в консоли.
$savedErrorAction = $ErrorActionPreference
$savedProgress = $ProgressPreference
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы

$ServerUrl = 'http://192.168.1.201:5080'
$ToolsUrl = "$ServerUrl/pxe/v1/files/tools"
$SetUrl = "$ToolsUrl/realtek-nic"
# Запуск файлом из копии набора (флешка: club\realtek-nic-run-as-admin.cmd) — набор берётся из этой папки, а не с сервера:
# у ПК Mirage сеть как раз и пропадает. Сверка с SHA256SUMS та же. Под irm | iex пути нет — набор с сервера; сторож
# запускается из $LocalDir и набор не перекачивает.
$SourceDir = $null
if ($PSCommandPath) {
    $scriptDir = Split-Path -Parent $PSCommandPath
    if ($scriptDir -and ($scriptDir.TrimEnd('\') -ne 'C:\ClubDrivers\realtek-nic') -and (Test-Path -LiteralPath (Join-Path $scriptDir 'SHA256SUMS'))) {
        $SourceDir = $scriptDir
    }
}
$HealthUrl = "$ServerUrl/health"
$ClubDriversDir = 'C:\ClubDrivers'
$LocalDir = 'C:\ClubDrivers\realtek-nic'
$LogDir = 'C:\ClubDrivers\realtek-nic\logs'
$GuardLog = 'C:\ClubDrivers\realtek-nic\logs\guard.log'
$ScriptName = 'install-realtek-nic.ps1'
$LocalScript = 'C:\ClubDrivers\realtek-nic\install-realtek-nic.ps1'
$InfName = 'rt640x64.inf'
$InboxFile = 'rtcx21x64.sys'
$TaskName = 'ClubRealtekNicGuard'
$DeviceMask = 'PCI\VEN_10EC&DEV_8168*'
$InboxCapability = 'Microsoft.Windows.Ethernet.Client.Realtek.Rtcx21x64~~~~0.0.1.0'
$Pnputil = Join-Path $env:windir 'System32\pnputil.exe'
$Powercfg = Join-Path $env:windir 'System32\powercfg.exe'
$WindowsPowerShell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
$RunCmd = "irm $ToolsUrl/$ScriptName | iex"
$UndoCmd = "& ([scriptblock]::Create((irm $ToolsUrl/$ScriptName))) -Undo"
$LocalUndoCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $LocalScript -Undo"
# Без сети: сторож руками ставит rt640x64 из локального набора (журнал — guard.log), или pnputil напрямую.
$LocalGuardCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $LocalScript -Guard"
$LocalPnputilCmd = "pnputil /add-driver $LocalDir\$InfName /install"
$FolderSddl = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'
# Владелец папки сторожа и её файлов — только эти SID: владелец всегда может переписать себе права (WRITE_DAC).
$AdminsSid = 'S-1-5-32-544'
$SystemSid = 'S-1-5-18'
# INSTALLFLAG_FORCE (1) | INSTALLFLAG_NONINTERACTIVE (4): без окон — сторож работает от SYSTEM в сеансе 0, окно ждало бы вечно.
$InstallFlags = [uint32]5

# Выключить (REG_SZ, только если ключевое слово у карты есть). Остальное только печатается.
$Settings = [ordered]@{ '*EEE' = '0'; 'AdvancedEEE' = '0'; 'EnableGreenEthernet' = '0'; 'GigaLite' = '0'; 'PowerSavingMode' = '0' }
$ShowOnly = @('*FlowControl', '*SpeedDuplex', '*JumboPacket', '*WakeOnMagicPacket', '*WakeOnPattern', 'WakeOnLink', 'S5WakeOnLan', 'WolShutdownLinkSpeed')

$State = @{ Mode = 'Install'; Guard = $false; Changed = $false; RebootPending = $false; NothingToDo = $false; LogFile = ''; BadArgs = @() }
$Count = @{ Fail = 0; Warn = 0 }
$Results = New-Object System.Collections.ArrayList
$NoReboot = $false
foreach ($scriptArg in $scriptArgs) {
    switch -Regex ("$scriptArg") {
        '^-Guard$' { $State.Mode = 'Guard' }
        '^-Undo$' { $State.Mode = 'Undo' }
        '^-NoReboot$' { $NoReboot = $true }
        default { $State.BadArgs += "$scriptArg" }
    }
}
if (@($scriptArgs | Where-Object { "$_" -match '^-(Guard|Undo)$' }).Count -gt 1) { $State.BadArgs += '-Guard/-Undo together' }

# P/Invoke для принудительной установки: pnputil ставит драйвер, только если он лучше текущего по рангу.
$NewDevSource = @'
using System;
using System.Runtime.InteropServices;

namespace ClubRealtekNic
{
    public static class NewDev
    {
        [DllImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath,
            uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    }
}
'@

# --- Вывод ---------------------------------------------------------------------------------------------------------------
# В режиме -Guard консоли нет: всё идёт в guard.log. Итог (OK/WARN/FAIL) копится в $Results для сводки в конце.
function Add-GuardLog([string]$Text) {
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
        $line = '{0} {1}{2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Text, "`r`n"
        [IO.File]::AppendAllText($GuardLog, $line, (New-Object System.Text.UTF8Encoding $false))
    } catch { }
}
function Write-Out([string]$Text, [string]$Color) {
    if ($State.Guard) { Add-GuardLog $Text; return }
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}
function Write-Title([string]$Text) { if (-not $State.Guard) { Write-Host '' }; Write-Out "==> $Text" 'Cyan' }
function Write-Info([string]$Text) { Write-Out "      $Text" '' }
function Write-Ok([string]$Text) { [void]$Results.Add("OK    $Text"); Write-Out "OK    $Text" 'Green' }
function Write-Warn([string]$Text, [string]$Fix) {
    $Count.Warn++
    [void]$Results.Add("WARN  $Text")
    Write-Out "WARN  $Text" 'Yellow'
    if ($Fix) { Write-Out "      -> $Fix" 'Yellow' }
}
function Write-Fail([string]$Text, [string]$Fix) {
    $Count.Fail++
    [void]$Results.Add("FAIL  $Text")
    Write-Out "FAIL  $Text" 'Red'
    if ($Fix) { Write-Out "      -> $Fix" 'Red' }
}

# Да/нет. В русской раскладке клавиши Y и N дают «н» и «т» — их тоже понимаем, чтобы не переключать раскладку.
function Read-YesNo([string]$Question, [bool]$Default) {
    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $yesRu = [string][char]0x043D
    $noRu = [string][char]0x0442
    while ($true) {
        $answer = "$(Read-Host "$Question $suffix")".Trim().ToLowerInvariant()
        if (-not $answer) { return $Default }
        if ($answer -in @('y', 'yes', $yesRu)) { return $true }
        if ($answer -in @('n', 'no', $noRu)) { return $false }
        Write-Host '      type y or n'
    }
}

# Внешняя программа с выводом в журнал. 'Continue': в 5.1 перенаправленный stderr при 'Stop' стал бы исключением.
# Вывод pnputil/powercfg на русской Windows — по-русски: его только печатаем, решения — по коду выхода и состоянию карты.
function Invoke-Native([string]$Exe, [string[]]$Arguments, [switch]$Quiet) {
    $ErrorActionPreference = 'Continue'
    $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    if (-not $Quiet) { foreach ($l in $lines) { if ($l.Trim()) { Write-Info "  $($l.TrimEnd())" } } }
    [pscustomobject]@{ Code = $code; Lines = $lines }
}

function Test-FileSha256([string]$Path, [string]$Sha256) {
    # -eq без учёта регистра: hex из SHA256SUMS (строчные) и Get-FileHash (прописные) совпадают.
    (Test-Path -LiteralPath $Path) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -eq $Sha256
}

# --- Папка и права ---------------------------------------------------------------------------------------------------------
# Только SYSTEM и администраторы — и у C:\ClubDrivers: по умолчанию «Прошедшие проверку» могут создать папку в корне C:
# (и становятся её владельцем) или переименовать свою. Поэтому владелец — тоже Administrators: SDDL 'D:...' меняет
# только DACL, а владелец всегда может переписать себе права (WRITE_DAC). Иначе пользователь (например, player), заранее
# создавший C:\ClubDrivers\realtek-nic, вернул бы себе доступ и подложил install-realtek-nic.ps1, который сторож запускает
# от SYSTEM (самопроверка SHA256 — внутри подложенного же файла, она не спасает). Ссылки (junction/symlink) не
# допускаются: icacls /T и запись файлов пошли бы по ним в чужую папку.

# Содержимое realtek-nic без захода в ссылки: сами ссылки возвращаются, внутрь — нет.
function Get-LocalDirItems {
    $items = New-Object System.Collections.ArrayList
    $dirs = New-Object System.Collections.Queue
    if (Test-Path -LiteralPath $LocalDir) { $dirs.Enqueue($LocalDir) }
    while ($dirs.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $dirs.Dequeue() -Force -ErrorAction SilentlyContinue)) {
            [void]$items.Add($item)
            if ($item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $dirs.Enqueue($item.FullName) }
        }
    }
    $items
}

# Что не так с папкой сторожа: ссылки и владелец не Administrators/SYSTEM (C:\ClubDrivers — только сама папка: в ней
# лежат и другие наборы драйверов). Пусто — всё в порядке.
function Get-UnsafeLocalPaths {
    $bad = @()
    $all = @(Get-Item -LiteralPath $ClubDriversDir, $LocalDir -Force -ErrorAction SilentlyContinue) + @(Get-LocalDirItems)
    foreach ($item in $all) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $bad += [pscustomobject]@{ Path = $item.FullName; Why = 'a link (junction/symlink)' }; continue }
        $owner = ''
        try { $owner = (Get-Acl -LiteralPath $item.FullName).GetOwner([System.Security.Principal.SecurityIdentifier]).Value } catch { }
        if ($owner -notin @($AdminsSid, $SystemSid)) { $bad += [pscustomobject]@{ Path = $item.FullName; Why = "owner '$owner'" } }
    }
    $bad
}

# Владелец — Administrators (по SID: имя группы зависит от языка Windows). icacls /setowner; что не вышло (права у
# администраторов отняты) — takeown /A, он берёт владение привилегией SeTakeOwnership. Есть ссылки — ничего рекурсивно.
function Set-LocalDirOwner {
    if (@(Get-UnsafeLocalPaths | Where-Object { $_.Why -like 'a link*' }).Count) { return }
    [void](Invoke-Native 'icacls.exe' @($ClubDriversDir, '/setowner', "*$AdminsSid", '/C', '/Q') -Quiet)
    if (Test-Path -LiteralPath $LocalDir) { [void](Invoke-Native 'icacls.exe' @($LocalDir, '/setowner', "*$AdminsSid", '/T', '/C', '/Q') -Quiet) }
    foreach ($bad in @(Get-UnsafeLocalPaths)) { [void](Invoke-Native 'takeown.exe' @('/F', $bad.Path, '/A') -Quiet) }
}

# Возвращает строки с тем, что осталось небезопасным (пусто — всё в порядке); решает вызывающий.
function Initialize-LocalDir {
    foreach ($dir in @($ClubDriversDir, $LocalDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        if ((Get-Item -LiteralPath $dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            return @("$dir is a link (junction/symlink), not a folder")
        }
    }
    $links = @(Get-LocalDirItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object { $_.FullName })
    if ($links.Count) { return @($links | ForEach-Object { "$_ is a link (junction/symlink)" }) }
    # Сначала владелец: тогда WRITE_DAC у администраторов есть, даже если пользователь убрал их из DACL.
    Set-LocalDirOwner
    foreach ($dir in @($ClubDriversDir, $LocalDir)) {
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetSecurityDescriptorSddlForm($FolderSddl)
        Set-Acl -LiteralPath $dir -AclObject $acl
    }
    # Файлы внутри — только наследуемые права от папки (как в get-shell-kit.ps1); пустую папку не трогаем.
    if (@(Get-ChildItem -LiteralPath $LocalDir -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        [void](Invoke-Native 'icacls.exe' @("$LocalDir\*", '/reset', '/T', '/C', '/Q') -Quiet)
    }
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    @(Get-UnsafeLocalPaths | ForEach-Object { '{0}: {1}' -f $_.Path, $_.Why })
}

# --- SHA256SUMS ------------------------------------------------------------------------------------------------------------
function Read-Sums([string]$Path, [string]$What) {
    $entries = @()
    $seen = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (-not $line.Trim()) { continue }
        # Только простые имена файлов: имя из списка становится путём в C:\ClubDrivers\realtek-nic и частью URL.
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64}) [ *]([A-Za-z0-9._-]+)$')
        if (-not $match.Success) { throw ("{0} has a bad line: '{1}'. Nothing was changed." -f $What, $line) }
        $name = $match.Groups[2].Value
        if ($name -match '^\.+$' -or $name -ieq 'SHA256SUMS' -or $name -ieq 'logs' -or $name -like '*.part') {
            throw ("{0} has a bad file name: '{1}'. Nothing was changed." -f $What, $name)
        }
        if ($seen.ContainsKey($name.ToLowerInvariant())) { throw ("{0} lists {1} twice. Nothing was changed." -f $What, $name) }
        $seen[$name.ToLowerInvariant()] = $true
        $entries += [pscustomobject]@{ Sha256 = $match.Groups[1].Value; Name = $name }
    }
    if ($entries.Count -eq 0) { throw ("{0} is empty. Nothing was changed." -f $What) }
    $entries
}

# Скачивание набора без смешения версий: SHA256SUMS и каждый изменившийся файл — в <имя>.part со сверкой SHA256, и только
# когда сверены ВСЕ, .part встают на место, SHA256SUMS — последним (его читает сторож). Ошибка до этого — удаляются
# только .part: старый набор и его SHA256SUMS остаются согласованными, сторож по-прежнему может из них ставить драйвер.
# Несовпадение — стоп до любой замены драйвера.
# Один файл набора в $Destination: копия из $SourceDir (флешка) или загрузка с сервера.
function Get-SetFile([string]$Name, [string]$Destination, [int]$TimeoutSec) {
    if ($SourceDir) { Copy-Item -LiteralPath (Join-Path $SourceDir $Name) -Destination $Destination -Force }
    else { Invoke-WebRequest -UseBasicParsing -TimeoutSec $TimeoutSec -Uri "$SetUrl/$Name" -OutFile $Destination }
}

function Save-ServedSet {
    $sumsPath = Join-Path $LocalDir 'SHA256SUMS'
    $sumsPart = "$sumsPath.part"
    $parts = New-Object System.Collections.ArrayList
    [void]$parts.Add($sumsPart)
    $fetched = @()
    $moving = $false
    $done = $false
    try {
        if ($SourceDir) { Write-Info "driver set from $SourceDir (not from the server)" }
        try { Get-SetFile 'SHA256SUMS' $sumsPart 60 }
        catch { throw ("Cannot get {0}/SHA256SUMS ({1}): is the driver set published on the club server (publish-realtek-nic.sh)? Nothing was changed." -f $(if ($SourceDir) { $SourceDir } else { $SetUrl }), $_.Exception.Message) }
        $entries = @(Read-Sums $sumsPart 'SHA256SUMS on the server')
        foreach ($required in @($InfName, $ScriptName)) {
            if (-not ($entries | Where-Object { $_.Name -ieq $required })) {
                throw ("SHA256SUMS on the server lists no {0}: the set there is incomplete. Nothing was changed." -f $required)
            }
        }
        foreach ($entry in $entries) {
            $target = Join-Path $LocalDir $entry.Name
            if (Test-FileSha256 $target $entry.Sha256) { continue }
            $part = "$target.part"
            [void]$parts.Add($part)
            Write-Info "getting $($entry.Name)"
            try { Get-SetFile $entry.Name $part 120 }
            catch { throw ("Download of {0} failed: {1}. The local set in {2} was left as it was; nothing was changed." -f $entry.Name, $_.Exception.Message, $LocalDir) }
            if (-not (Test-FileSha256 $part $entry.Sha256)) {
                $got = (Get-FileHash -Algorithm SHA256 -LiteralPath $part).Hash
                throw ("{0} from the server has SHA256 {1}, SHA256SUMS says {2}: the set must be published again. The local set was left as it was; nothing was changed." -f $entry.Name, $got, $entry.Sha256)
            }
            $fetched += $entry
        }
        # Всё скачано и сверено — на место; SHA256SUMS последним.
        $moving = $true
        foreach ($entry in $fetched) {
            $target = Join-Path $LocalDir $entry.Name
            Move-Item -LiteralPath "$target.part" -Destination $target -Force
        }
        Move-Item -LiteralPath $sumsPart -Destination $sumsPath -Force
        $done = $true
    } catch {
        if ($moving) {
            throw ("Putting the checked files in place in {0} failed: {1}. The local set is now mixed (the startup guard cannot use it): run the same command again. The driver was not changed." -f $LocalDir, $_.Exception.Message)
        }
        throw
    } finally {
        if (-not $done) { foreach ($p in $parts) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue } }
    }
    Write-Ok ('driver set in {0}: {1} files, SHA256 checked ({2} downloaded, {3} already up to date)' -f $LocalDir, $entries.Count, $fetched.Count, ($entries.Count - $fetched.Count))
    $entries
}

# Сторож: локальные файлы против локального SHA256SUMS (без сети). Ошибка — $null и FAIL в журнале.
function Get-LocalSet {
    $sumsPath = Join-Path $LocalDir 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sumsPath)) { Write-Fail "$sumsPath is missing: cannot reinstall the driver" "run $RunCmd"; return $null }
    try {
        $entries = @(Read-Sums $sumsPath 'local SHA256SUMS')
        $bad = @($entries | Where-Object { -not (Test-FileSha256 (Join-Path $LocalDir $_.Name) $_.Sha256) } | ForEach-Object { $_.Name })
        if ($bad.Count) { Write-Fail ('local files differ from SHA256SUMS: {0}' -f ($bad -join ', ')) "run $RunCmd"; return $null }
        $infEntry = @($entries | Where-Object { $_.Name -ieq $InfName })[0]
        if (-not $infEntry) { Write-Fail "local SHA256SUMS lists no $InfName" "run $RunCmd"; return $null }
        $inf = Get-InfData (Join-Path $LocalDir $infEntry.Name)
        Test-InfFiles $inf
        Write-Info ('local set OK: {0} files, {1} DriverVer {2}' -f $entries.Count, $infEntry.Name, $inf.DriverVer)
        return $inf
    } catch {
        Write-Fail ('local driver set is not usable: {0}' -f $_.Exception.Message) "run $RunCmd"
        return $null
    }
}

# --- INF -------------------------------------------------------------------------------------------------------------------
function Get-InfSection([string]$Text, [string]$Name) {
    $inside = $false
    foreach ($raw in ($Text -split "`r?`n")) {
        $line = ($raw -replace ';.*$', '').Trim()
        if ($line -match '^\[(.+)\]$') { $inside = ($Matches[1].Trim() -ieq $Name); continue }
        if ($inside -and $line) { $line }
    }
}

function Get-InfData([string]$Path) {
    # ReadAllText сам узнаёт UTF-16 по BOM (INF бывают в UTF-16 LE).
    $text = [IO.File]::ReadAllText($Path)
    $version = @(Get-InfSection $text 'Version')
    $driverVer = ''
    foreach ($l in $version) { if ($l -match '^DriverVer\s*=\s*(.+)$') { $driverVer = $Matches[1].Trim(); break } }
    $m = [regex]::Match($driverVer, '^(\d{1,2}/\d{1,2}/\d{4})\s*,\s*(\d+(?:\.\d+){1,3})')
    if (-not $m.Success) { throw ("{0}: no readable DriverVer in [Version] ('{1}')" -f $Path, $driverVer) }
    $catalogs = @(foreach ($l in $version) { if ($l -match '^CatalogFile(?:\.NTamd64)?\s*=\s*(.+)$') { $Matches[1].Trim().Trim('"') } })
    $files = @()
    foreach ($section in @('SourceDisksFiles', 'SourceDisksFiles.amd64')) {
        foreach ($l in @(Get-InfSection $text $section)) {
            if ($l -match '^"?([^"=]+?)"?\s*=') { $files += $Matches[1].Trim() }
        }
    }
    # Службы драйвера (AddService = <имя>, ...): по ним видно, что устройство уже настроено на этот пакет, даже если
    # работает ещё старый драйвер (ждёт перезагрузки) или новый не запустился.
    $services = @([regex]::Matches($text, '(?im)^[ \t]*AddService[ \t]*=[ \t]*"?([A-Za-z0-9_.-]+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    [pscustomobject]@{
        Path      = $Path
        Text      = $text
        DriverVer = '{0},{1}' -f $m.Groups[1].Value, $m.Groups[2].Value
        Version   = [version]$m.Groups[2].Value
        Catalogs  = $catalogs
        Files     = $files
        Services  = $services
    }
}

# Всё, что INF берёт с «диска» (каталог и [SourceDisksFiles*]), должно лежать рядом с ним.
function Test-InfFiles($Inf) {
    if ($Inf.Text -notmatch 'VEN_10EC&DEV_8168') { throw ("{0} does not list PCI\VEN_10EC&DEV_8168: wrong driver in the set" -f $Inf.Path) }
    $dir = Split-Path -Parent $Inf.Path
    foreach ($name in @($Inf.Catalogs) + @($Inf.Files)) {
        if ($name -notmatch '^[A-Za-z0-9._-]+$') { throw ("{0} names a file '{1}' that is not a plain file name" -f $Inf.Path, $name) }
        if (-not (Test-Path -LiteralPath (Join-Path $dir $name))) { throw ("{0} needs {1}, which is not in {2}" -f $Inf.Path, $name, $dir) }
    }
}

# --- Карты -----------------------------------------------------------------------------------------------------------------
function Get-NicDevices {
    @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { "$($_.InstanceId)" -like $DeviceMask })
}

function Get-NicInfo([string]$InstanceId) {
    $keys = @('DEVPKEY_Device_DriverVersion', 'DEVPKEY_Device_DriverProvider', 'DEVPKEY_Device_DriverDate', 'DEVPKEY_Device_DriverInfPath',
        'DEVPKEY_Device_Service', 'DEVPKEY_Device_HardwareIds', 'DEVPKEY_Device_CompatibleIds', 'DEVPKEY_Device_ProblemCode',
        'DEVPKEY_Device_DevNodeStatus')
    $p = @{}
    try { foreach ($item in @(Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $keys -ErrorAction SilentlyContinue)) { $p[$item.KeyName] = $item.Data } } catch { }
    $pnp = $null
    try { $pnp = Get-PnpDevice -InstanceId $InstanceId -ErrorAction Stop } catch { }
    $adapter = $null
    try { $adapter = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { "$($_.PnPDeviceID)" -eq $InstanceId })[0] } catch { }
    $version = "$($p['DEVPKEY_Device_DriverVersion'])"
    if (-not $version -and $adapter) { $version = "$($adapter.DriverVersionString)" }
    $date = $p['DEVPKEY_Device_DriverDate']
    if ($date -is [datetime]) { $date = $date.ToString('yyyy-MM-dd') } elseif ($adapter) { $date = "$($adapter.DriverDate)" } else { $date = "$date" }
    $provider = "$($p['DEVPKEY_Device_DriverProvider'])"
    if (-not $provider -and $adapter) { $provider = "$($adapter.DriverProvider)" }
    $name = ''; $status = ''; $speed = ''; $file = ''
    if ($adapter) { $name = "$($adapter.Name)"; $status = "$($adapter.Status)"; $speed = "$($adapter.LinkSpeed)"; $file = "$($adapter.DriverFileName)" }
    # Код проблемы устройства: 14 = CM_PROB_NEED_RESTART (драйвер сменится при перезагрузке), остальные > 0 — ошибка
    # (10 — не запустился, 28 — нет драйвера). DN_NEED_RESTART (0x100) в состоянии узла — тоже «ждёт перезагрузки».
    $problemCode = -1
    foreach ($v in @($p['DEVPKEY_Device_ProblemCode'], $pnp.ConfigManagerErrorCode)) {
        if ($null -eq $v -or "$v" -eq '') { continue }
        try { $problemCode = [int]$v; break } catch { }
    }
    $nodeStatus = [long]0
    try { $nodeStatus = [long]$p['DEVPKEY_Device_DevNodeStatus'] } catch { }
    [pscustomobject]@{
        InstanceId     = $InstanceId
        FriendlyName   = "$($pnp.FriendlyName)"
        PnpStatus      = "$($pnp.Status)"
        Problem        = "$($pnp.Problem)"
        ProblemCode    = $problemCode
        NeedRestart    = ($problemCode -eq 14) -or (($nodeStatus -band 0x100) -ne 0)
        HasProblem     = ($problemCode -gt 0) -and ($problemCode -ne 14)
        Adapter        = $adapter
        Name           = $name
        Status         = $status
        LinkSpeed      = $speed
        DriverFileName = $file
        DriverVersion  = $version
        DriverProvider = $provider
        DriverDate     = $date
        InfPath        = "$($p['DEVPKEY_Device_DriverInfPath'])"
        Service        = "$($p['DEVPKEY_Device_Service'])"
        HardwareIds    = @($p['DEVPKEY_Device_HardwareIds'] | Where-Object { $_ })
        CompatibleIds  = @($p['DEVPKEY_Device_CompatibleIds'] | Where-Object { $_ })
    }
}

function Test-OnRealtekNdis($Info) { "$($Info.DriverFileName)" -match '(^|\\)rt640x64\.sys$' }

function ConvertTo-Version([string]$Text) { try { [version]$Text.Trim() } catch { $null } }

# Уже на rt640x64 и не старше набора — замена не нужна.
function Test-Current($Info, [version]$Wanted) {
    if (-not (Test-OnRealtekNdis $Info)) { return $false }
    $have = ConvertTo-Version $Info.DriverVersion
    ($null -ne $have) -and $have -ge $Wanted
}

# Устройство настроено на пакет rt640x64: его служба — из AddService INF набора (или rt640x64), либо карта уже на файле.
function Test-ConfiguredRealtek($Info, $Inf) {
    if (Test-OnRealtekNdis $Info) { return $true }
    $services = @('rt640x64')
    if ($Inf) { $services += @($Inf.Services) }
    [bool]($Info.Service -and @($services | Where-Object { $_ -ieq $Info.Service }).Count)
}

# Состояние карты относительно rt640x64 (решения — только по нему, не по одному DriverFileName):
#   Bound         — сетевая карта работает на rt640x64.sys;
#   PendingReboot — устройство уже настроено на rt640x64, но до перезагрузки работает прежний драйвер (код 14 /
#                   DN_NEED_RESTART, или pnputil 3010 / rebootRequired, или карта Up на прежнем файле);
#   NotStarted    — настроено на rt640x64, но сетевой карты нет или у устройства код ошибки: сети, скорее всего, нет;
#   NotBound      — устройство не на rt640x64.
function Get-BindState($Info, $Inf, [bool]$RebootSignal) {
    if (-not (Test-ConfiguredRealtek $Info $Inf)) { return 'NotBound' }
    if ($Info.NeedRestart) { return 'PendingReboot' }
    if (Test-OnRealtekNdis $Info) { return 'Bound' }
    if ($Info.HasProblem) { return 'NotStarted' }
    if ($RebootSignal -or $Info.Adapter) { return 'PendingReboot' }
    'NotStarted'
}

# Карта не на rt640x64: работает ли прежний драйвер — только если проверено (Up на прежнем файле), а не по умолчанию.
function Format-OldDriverState($Info) {
    if ($Info.Adapter -and $Info.Status -eq 'Up' -and $Info.DriverFileName -and -not (Test-OnRealtekNdis $Info)) {
        return ('the previous driver {0} keeps working (adapter Up)' -f $Info.DriverFileName)
    }
    'the adapter is NOT Up on its previous driver either: the network may be down'
}

function Format-NicLine($Info) {
    $label = $Info.Name
    if (-not $label) { $label = '(no network adapter)' }
    $file = $Info.DriverFileName
    if (-not $file) { $file = "service '$($Info.Service)'" }
    "'{0}': {1} {2}, status {3} {4}, device {5} {6}" -f $label, $file, $Info.DriverVersion, $Info.Status, $Info.LinkSpeed, $Info.PnpStatus, $Info.Problem
}

function Show-NicInfo($Info) {
    $label = $Info.Name
    if (-not $label) { $label = '(no network adapter)' }
    Write-Info ("adapter {0}: {1}" -f $label, $Info.FriendlyName)
    Write-Info ("  status {0}, link {1}, device {2} {3}" -f $Info.Status, $Info.LinkSpeed, $Info.PnpStatus, $Info.Problem)
    Write-Info ("  driver {0} version {1}, provider {2}, date {3}" -f $Info.DriverFileName, $Info.DriverVersion, $Info.DriverProvider, $Info.DriverDate)
    Write-Info ("  INF {0}, service {1}" -f $Info.InfPath, $Info.Service)
    Write-Info ("  instance {0}" -f $Info.InstanceId)
    foreach ($id in $Info.HardwareIds) { Write-Info ("  hardware ID {0}" -f $id) }
}

# Бездиск: системный диск по iSCSI через эту же карту — драйвер живой загрузочной карты менять нельзя.
function Test-NetworkBoot {
    try {
        $letter = $env:SystemDrive.TrimEnd(':')
        $disk = Get-Partition -DriveLetter $letter -ErrorAction Stop | Get-Disk -ErrorAction Stop
        return ("$($disk.BusType)" -eq 'iSCSI')
    } catch { return $false }
}

function Show-InboxCapability {
    try {
        $cap = Get-WindowsCapability -Online -Name $InboxCapability -ErrorAction Stop
        Write-Info ("inbox driver capability {0}: {1} (kept: the fallback driver if rt640x64 fails)" -f $InboxCapability, $cap.State)
    } catch {
        Write-Info ("inbox driver capability {0}: not readable ({1})" -f $InboxCapability, $_.Exception.Message)
    }
}

# --- События за 7 дней (только чтение) ------------------------------------------------------------------------------------
function Show-NicEvents {
    $since = (Get-Date).AddDays(-7)
    $events = @()
    try { $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $since; Id = 15, 27, 32, 4202, 10400, 10401 } -ErrorAction Stop) }
    catch { if ("$($_.FullyQualifiedErrorId)" -notlike 'NoMatchingEventsFound*') { Write-Info "System log not readable: $($_.Exception.Message)" } }
    $labels = [ordered]@{
        'HAL 15'       = 'DMA remapping / IOMMU fault'
        'NDIS 10400'   = 'adapter reset started (hang detected)'
        'NDIS 10401'   = 'adapter reset finished'
        'Tcpip 4202'   = 'adapter disconnected from the network'
        'rtcx21x64 27' = 'link down (inbox driver)'
        'rtcx21x64 32' = 'link up (inbox driver)'
        'rt640x64 27'  = 'link down (Realtek NDIS driver)'
        'rt640x64 32'  = 'link up (Realtek NDIS driver)'
    }
    $found = @()
    foreach ($e in $events) {
        $provider = "$($e.ProviderName)"
        $key = $null
        if ($e.Id -eq 15 -and $provider -eq 'Microsoft-Windows-HAL') { $key = 'HAL 15' }
        elseif ($e.Id -in @(10400, 10401) -and $provider -match '^(Microsoft-Windows-)?NDIS$') { $key = "NDIS $($e.Id)" }
        elseif ($e.Id -eq 4202 -and $provider -match '^(Microsoft-Windows-)?Tcpip$') { $key = 'Tcpip 4202' }
        elseif ($e.Id -in @(27, 32) -and $provider -match '^rt[0-9a-z]*x64$') { $key = '{0} {1}' -f $provider.ToLowerInvariant(), $e.Id }
        if ($key) { $found += [pscustomobject]@{ Key = $key; Time = $e.TimeCreated } }
    }
    foreach ($ncsi in @(
            @{ Key = 'NCSI (System)'; Text = 'events of Microsoft-Windows-NCSI'; Filter = @{ LogName = 'System'; StartTime = $since; ProviderName = 'Microsoft-Windows-NCSI' } },
            @{ Key = 'NCSI/Operational 4042'; Text = 'connectivity changes'; Filter = @{ LogName = 'Microsoft-Windows-NCSI/Operational'; StartTime = $since; Id = 4042 } })) {
        $labels[$ncsi.Key] = $ncsi.Text
        try { foreach ($e in @(Get-WinEvent -FilterHashtable $ncsi.Filter -ErrorAction Stop)) { $found += [pscustomobject]@{ Key = $ncsi.Key; Time = $e.TimeCreated } } }
        catch { }
    }
    foreach ($k in @($found | ForEach-Object { $_.Key } | Select-Object -Unique)) { if (-not $labels.Contains($k)) { $labels[$k] = 'link event of a Realtek driver' } }
    Write-Info ("since {0:yyyy-MM-dd HH:mm}:" -f $since)
    foreach ($k in @($labels.Keys)) {
        $hits = @($found | Where-Object { $_.Key -eq $k })
        $last = 'never'
        if ($hits.Count) { $last = 'last {0:yyyy-MM-dd HH:mm:ss}' -f (@($hits | Sort-Object Time)[-1].Time) }
        Write-Info ('{0,-22} {1,5}  {2,-25} {3}' -f $k, $hits.Count, $last, $labels[$k])
    }
    $timeline = @($found | Where-Object { $_.Key -notlike 'NCSI*' } | Sort-Object Time | Select-Object -Last 15)
    if ($timeline.Count) {
        Write-Info 'latest adapter events:'
        foreach ($t in $timeline) { Write-Info ('  {0:yyyy-MM-dd HH:mm:ss}  {1}' -f $t.Time, $t.Key) }
    }
}

# --- Установка драйвера ----------------------------------------------------------------------------------------------------
function Get-NewDevType {
    $type = 'ClubRealtekNic.NewDev' -as [type]
    if (-not $type) {
        Add-Type -TypeDefinition $NewDevSource -Language CSharp
        $type = 'ClubRealtekNic.NewDev' -as [type]
    }
    $type
}

# Самый точный ИД карты (ИД оборудования, потом совместимые), который INF перечисляет целиком; иначе первый ИД оборудования.
function Select-HardwareId($Info, [string]$InfText) {
    foreach ($id in @($Info.HardwareIds) + @($Info.CompatibleIds)) {
        if ($id -and $InfText -match ('(?im)[=,]\s*' + [regex]::Escape($id) + '\s*(?:[,;]|$)')) { return $id }
    }
    if (@($Info.HardwareIds).Count) { return @($Info.HardwareIds)[0] }
    $null
}

# Ждёт, пока каждая карта не станет Bound — или устройство не попросит перезагрузку (код 14): дальше ждать бессмысленно.
function Wait-RealtekBound([string[]]$InstanceIds, $Inf, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        $left = @($InstanceIds | Where-Object {
                $i = Get-NicInfo $_
                $s = Get-BindState $i $Inf $false
                -not ($s -eq 'Bound' -or ($s -eq 'PendingReboot' -and $i.NeedRestart))
            })
        if ($left.Count -eq 0 -or (Get-Date) -ge $deadline) { return }
        Start-Sleep -Seconds 3
    }
}

# pnputil, ожидание до 60 с, при неудаче — UpdateDriverForPlugAndPlayDevices с INSTALLFLAG_FORCE и снова ожидание.
# Принудительно — только карты, которые не настроены на rt640x64 (или на rt640x64 старше набора): «ждёт перезагрузки»
# повторной установкой не лечится. Возвращает [ordered]@{ InstanceId = состояние из Get-BindState }.
function Install-RealtekDriver($Inf, [string[]]$InstanceIds) {
    $rebootSignal = $false
    Write-Info ("pnputil /add-driver {0} /install" -f $Inf.Path)
    $r = Invoke-Native $Pnputil @('/add-driver', $Inf.Path, '/install')
    if ($r.Code -eq 3010) { $rebootSignal = $true; Write-Info 'pnputil exit code 3010: installed, a reboot is required to finish' }
    elseif ($r.Code -eq 0) { Write-Info 'pnputil exit code 0' }
    else { Write-Info ("pnputil exit code {0} (not 0/3010): the driver check below decides" -f $r.Code) }
    Write-Info 'waiting up to 60 s for DriverFileName = rt640x64.sys'
    Wait-RealtekBound $InstanceIds $Inf 60

    $force = @($InstanceIds | Where-Object {
            $i = Get-NicInfo $_
            $s = Get-BindState $i $Inf $rebootSignal
            $s -eq 'NotBound' -or ($s -eq 'Bound' -and -not (Test-Current $i $Inf.Version))
        })
    if ($force.Count) {
        Write-Info 'pnputil did not bind rt640x64: forcing it with UpdateDriverForPlugAndPlayDevices (INSTALLFLAG_FORCE)'
        $type = $null
        try { $type = Get-NewDevType } catch { Write-Info ("cannot load newdev.dll P/Invoke: {0}" -f $_.Exception.Message) }
        if ($type) {
            foreach ($id in $force) {
                $info = Get-NicInfo $id
                $hwid = Select-HardwareId $info $Inf.Text
                if (-not $hwid) { Write-Info ("no hardware ID readable for {0}" -f $id); continue }
                $reboot = $false
                $ok = $type::UpdateDriverForPlugAndPlayDevices([IntPtr]::Zero, $hwid, $Inf.Path, $InstallFlags, [ref]$reboot)
                $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                if ($ok) {
                    Write-Info ("forced on {0} (reboot required: {1})" -f $hwid, $reboot)
                    if ($reboot) { $rebootSignal = $true }
                } else {
                    Write-Info ("UpdateDriverForPlugAndPlayDevices on {0} failed: 0x{1:X8} {2}" -f $hwid, $err, (New-Object ComponentModel.Win32Exception $err).Message)
                }
            }
        }
        Write-Info 'waiting up to 60 s for DriverFileName = rt640x64.sys'
        Wait-RealtekBound $InstanceIds $Inf 60
    }
    $states = [ordered]@{}
    foreach ($id in $InstanceIds) {
        $states[$id] = Get-BindState (Get-NicInfo $id) $Inf $rebootSignal
        Write-Info ('{0}: {1}' -f $id, $states[$id])
    }
    $states
}

# --- Сеть ------------------------------------------------------------------------------------------------------------------
function Test-Network([string[]]$InstanceIds) {
    Write-Info 'waiting up to 60 s for link Up and an IPv4 address'
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        $ready = @()
        foreach ($id in $InstanceIds) {
            $info = Get-NicInfo $id
            if (-not $info.Adapter -or $info.Status -ne 'Up') { continue }
            $ip = @(Get-NetIPAddress -InterfaceIndex $info.Adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -notlike '169.254.*' -and "$($_.AddressState)" -eq 'Preferred' })
            if ($ip.Count) { $ready += [pscustomobject]@{ Info = $info; Ip = $ip[0].IPAddress } }
        }
        if ($ready.Count -eq $InstanceIds.Count -or (Get-Date) -ge $deadline) { break }
        Start-Sleep -Seconds 3
    }
    foreach ($id in $InstanceIds) {
        $hit = @($ready | Where-Object { $_.Info.InstanceId -eq $id })[0]
        if ($hit) { Write-Ok ("adapter '{0}': Up {1}, IPv4 {2}, {3} {4}" -f $hit.Info.Name, $hit.Info.LinkSpeed, $hit.Ip, $hit.Info.DriverFileName, $hit.Info.DriverVersion) }
        elseif ($ready.Count) { Write-Info ("other Realtek device {0}: no link / IPv4 ({1})" -f $id, (Format-NicLine (Get-NicInfo $id))) }
    }
    if ($ready.Count -eq 0) {
        $now = @($InstanceIds | ForEach-Object { Format-NicLine (Get-NicInfo $_) }) -join '; '
        Write-Fail ("no Realtek adapter has link and an IPv4 address after 60 s ({0})" -f $now) "check the cable, reboot; if the network is still dead: $LocalUndoCmd"
        return
    }
    $answer = ''
    for ($i = 1; $i -le 3; $i++) {
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $HealthUrl -TimeoutSec 10
            if ($resp.StatusCode -eq 200) { Write-Ok ("club server answers: {0} -> {1} {2}" -f $HealthUrl, $resp.StatusCode, "$($resp.Content)".Trim()); return }
            $answer = "HTTP $($resp.StatusCode)"
        } catch { $answer = $_.Exception.Message }
        Start-Sleep -Seconds 3
    }
    Write-Warn ("club server does not answer {0}: {1}" -f $HealthUrl, $answer) 'the adapter has an address; check the server, then G:/H: in Explorer'
}

# --- Настройки ---------------------------------------------------------------------------------------------------------
function Get-AdvancedProperties([string]$Name) {
    # -Name понимает шаблоны: оставляем только точное имя. RegistryKeyword — тоже точным сравнением: шаблон '*EEE'
    # совпал бы и с AdvancedEEE.
    @(Get-NetAdapterAdvancedProperty -Name $Name -AllProperties -ErrorAction Stop | Where-Object { $_.Name -eq $Name })
}

# Возвращает $true, только если значение реально поменялось (прочитано обратно). Всегда с -NoRestart: перезапуск
# карты решает вызывающий.
function Set-NicSettings($Info) {
    $name = $Info.Name
    $changed = $false
    $props = @(Get-AdvancedProperties $name)
    foreach ($kw in $ShowOnly) {
        if ($State.Guard) { break }
        $p = @($props | Where-Object { $_.RegistryKeyword -eq $kw })[0]
        if ($p) { Write-Info ("{0} ({1}) = {2} [{3}]: left as is" -f $kw, $p.DisplayName, ($p.RegistryValue -join ','), $p.DisplayValue) }
    }
    foreach ($kw in @($Settings.Keys)) {
        $want = $Settings[$kw]
        $p = @($props | Where-Object { $_.RegistryKeyword -eq $kw })[0]
        if (-not $p) { Write-Info ("{0}: not on this driver, skipped" -f $kw); continue }
        $label = '{0} ({1})' -f $kw, $p.DisplayName
        $before = ($p.RegistryValue -join ',')
        if ($before -eq $want) { Write-Info ("{0} = {1}: already off" -f $label, $before); continue }
        try {
            $p | Set-NetAdapterAdvancedProperty -RegistryValue $want -NoRestart -ErrorAction Stop
            $after = (@(Get-AdvancedProperties $name | Where-Object { $_.RegistryKeyword -eq $kw })[0].RegistryValue -join ',')
            if ($after -eq $want) { Write-Ok ("adapter '{0}' {1}: {2} -> {3}" -f $name, $label, $before, $after); $changed = $true }
            else { Write-Warn ("adapter '{0}' {1}: set to {2}, reads back {3}" -f $name, $label, $want, $after) }
        } catch {
            Write-Warn ("adapter '{0}' {1}: cannot set {2} ({3})" -f $name, $label, $want, $_.Exception.Message)
        }
    }
    $changed
}

# «Разрешить отключение устройства для экономии энергии» — выкл. Через объект Get-/Set-NetAdapterPowerManagement:
# Disable-NetAdapterPowerManagement выключил бы и пробуждение по сети (WoL).
function Set-DevicePower($Info) {
    $name = $Info.Name
    try {
        $pm = @(Get-NetAdapterPowerManagement -Name $name -ErrorAction Stop | Where-Object { $_.Name -eq $name })[0]
        if (-not $pm) { Write-Warn ("adapter '{0}': power management not readable" -f $name); return $false }
        if (-not $State.Guard) { Write-Info ("WoL: WakeOnMagicPacket {0}, WakeOnPattern {1}: left as is" -f $pm.WakeOnMagicPacket, $pm.WakeOnPattern) }
        $before = "$($pm.AllowComputerToTurnOffDevice)"
        if ($before -eq 'Disabled') { Write-Info 'AllowComputerToTurnOffDevice = Disabled: already off'; return $false }
        if ($before -eq 'Unsupported') { Write-Info 'AllowComputerToTurnOffDevice = Unsupported: nothing to change'; return $false }
        $pm.AllowComputerToTurnOffDevice = 'Disabled'
        $pm | Set-NetAdapterPowerManagement -NoRestart -ErrorAction Stop
        $after = "$(@(Get-NetAdapterPowerManagement -Name $name -ErrorAction Stop | Where-Object { $_.Name -eq $name })[0].AllowComputerToTurnOffDevice)"
        if ($after -eq 'Disabled') { Write-Ok ("adapter '{0}' AllowComputerToTurnOffDevice: {1} -> {2}" -f $name, $before, $after); return $true }
        # Не встало — $false, как в Set-NicSettings: иначе сторож перезапускал бы карту на каждой загрузке (как раз когда
        # помощник подключает G:/H:), а значение всё равно не держится.
        Write-Warn ("adapter '{0}' AllowComputerToTurnOffDevice: set to Disabled, reads back {1}" -f $name, $after)
        return $false
    } catch {
        Write-Warn ("adapter '{0}': device power setting failed ({1})" -f $name, $_.Exception.Message)
        return $false
    }
}

# Индекс ASPM активной схемы: последние два значения 0x........ в выводе powercfg — от сети и от батареи (подписи
# на русской Windows по-русски, числа — нет). Скрытый параметр — через /qh.
function Get-AspmIndex {
    foreach ($q in @('/query', '/qh')) {
        $r = Invoke-Native $Powercfg @($q, 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM') -Quiet
        $vals = @($r.Lines | ForEach-Object { if ($_ -match '0x([0-9A-Fa-f]{8})\s*$') { [Convert]::ToInt32($Matches[1], 16) } })
        if ($vals.Count -ge 2) { return [pscustomobject]@{ AC = $vals[-2]; DC = $vals[-1] } }
    }
    $null
}

function Format-Aspm($Index) {
    if (-not $Index) { return 'unknown' }
    $names = @{ 0 = 'off'; 1 = 'moderate'; 2 = 'maximum' }
    'AC {0} ({1}), DC {2} ({3})' -f $Index.AC, $names[$Index.AC], $Index.DC, $names[$Index.DC]
}

function Set-Aspm {
    $before = Get-AspmIndex
    if ($before -and $before.AC -eq 0 -and $before.DC -eq 0) { Write-Info ("PCIe ASPM of the active power plan: {0}: already off" -f (Format-Aspm $before)); return $false }
    $codes = @()
    $codes += (Invoke-Native $Powercfg @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0')).Code
    $codes += (Invoke-Native $Powercfg @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0')).Code
    $codes += (Invoke-Native $Powercfg @('/setactive', 'SCHEME_CURRENT')).Code
    $after = Get-AspmIndex
    if ($after -and $after.AC -eq 0 -and $after.DC -eq 0) { Write-Ok ("PCIe ASPM (Link State Power Management) of the active power plan: {0} -> off" -f (Format-Aspm $before)); return $true }
    Write-Warn ("PCIe ASPM: powercfg exit codes {0}, now {1}" -f ($codes -join ','), (Format-Aspm $after)) 'powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0; powercfg /setactive SCHEME_CURRENT'
    $true
}

# --- Сторож ----------------------------------------------------------------------------------------------------------------
function Register-Guard {
    if (-not (Test-Path -LiteralPath $LocalScript)) { Write-Fail "$LocalScript is missing: guard not installed" "run $RunCmd again"; return }
    # Задача запускает файл из этой папки от SYSTEM: владелец — ещё раз после скачивания (новые файлы) и проверка по SID.
    # Не Administrators/SYSTEM или ссылка — сторож не ставится.
    Set-LocalDirOwner
    $unsafe = @(Get-UnsafeLocalPaths)
    if ($unsafe.Count) {
        Write-Fail ('guard not installed: {0}' -f (($unsafe | ForEach-Object { '{0} is {1}' -f $_.Path, $_.Why }) -join '; ')) `
            "delete $LocalDir by hand (as Administrator; a link: only the link itself) and run $RunCmd again"
        return
    }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Guard' -f $LocalScript
    $action = New-ScheduledTaskAction -Execute $WindowsPowerShell -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT30S'
    # SYSTEM по SID: имя учётки зависит от языка Windows.
    $system = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18').Translate([System.Security.Principal.NTAccount]).Value
    $principal = New-ScheduledTaskPrincipal -UserId $system -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'Club: puts the Realtek rt640x64 NDIS driver back on the RTL8111H if Windows swapped it, keeps its power saving off' -Force | Out-Null
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    $act = @($task.Actions)[0]
    if ($act.Execute -ne $WindowsPowerShell -or $act.Arguments -ne $arguments) { Write-Fail ("task {0} registered with a different action: {1} {2}" -f $TaskName, $act.Execute, $act.Arguments); return }
    Write-Ok ("guard task {0}: at startup as SYSTEM -> {1} -Guard" -f $TaskName, $LocalScript)

    # Пробный запуск: проверяет копию, права и запуск от SYSTEM. Изменений не ждём — всё уже выставлено выше.
    Write-Info 'test run of the guard task (as SYSTEM, no changes expected)'
    $before = (Get-ScheduledTaskInfo -TaskName $TaskName).LastRunTime
    Start-ScheduledTask -TaskName $TaskName
    $deadline = (Get-Date).AddSeconds(120)
    $info = $null
    do {
        Start-Sleep -Seconds 3
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        $running = "$((Get-ScheduledTask -TaskName $TaskName).State)" -eq 'Running'
    } while (((-not $running -and $info.LastRunTime -eq $before) -or $running) -and (Get-Date) -lt $deadline)
    if (Test-Path -LiteralPath $GuardLog) { Get-Content -LiteralPath $GuardLog -Tail 8 | ForEach-Object { Write-Info "  guard.log: $_" } }
    if ($info.LastRunTime -ne $before -and $info.LastTaskResult -eq 0) { Write-Ok 'guard test run finished with code 0' }
    else { Write-Warn ("guard test run: last result {0} (0x{0:X})" -f $info.LastTaskResult) "see $GuardLog; the task stays registered" }
}

function Invoke-Guard {
    $State.Guard = $true
    Add-GuardLog ('---- guard start on {0} ({1})' -f $env:COMPUTERNAME, $LocalScript)
    try {
        if (Test-NetworkBoot) { Write-Info 'system disk is on iSCSI (network boot): the guard does nothing here'; return }
        $ids = @(Get-NicDevices | ForEach-Object { $_.InstanceId })
        if ($ids.Count -eq 0) { Write-Info 'no Realtek PCI\VEN_10EC&DEV_8168 adapter: nothing to do'; return }
        $infos = @($ids | ForEach-Object { Get-NicInfo $_ })
        foreach ($i in $infos) { Write-Info (Format-NicLine $i) }
        # Сверка локального набора — каждый запуск: испорченный набор виден в журнале раньше, чем понадобится.
        $inf = Get-LocalSet
        # Переустановка — только карт не на rt640x64. Уже настроена и ждёт перезагрузки — ждём; настроена, но не
        # запустилась — повторная установка того же пакета не поможет: FAIL с локальным откатом.
        $off = @()
        foreach ($i in $infos) {
            switch (Get-BindState $i $inf $false) {
                'NotBound' { $off += $i }
                'PendingReboot' { Write-Info ("{0}: rt640x64 is installed and becomes active at the next reboot: nothing to do now" -f $i.InstanceId) }
                'NotStarted' { Write-Fail ('rt640x64 is set up but did not start: {0}' -f (Format-NicLine $i)) "reboot; still no network -> $LocalUndoCmd" }
            }
        }
        if ($off.Count) {
            Write-Info ('{0} adapter(s) not on rt640x64.sys: reinstalling from {1}' -f $off.Count, $LocalDir)
            if (-not $inf) { Write-Info 'no usable local driver set (above): the driver is not reinstalled' }
            else {
                $states = Install-RealtekDriver $inf @($off | ForEach-Object { $_.InstanceId })
                foreach ($id in @($states.Keys)) {
                    $now = Get-NicInfo $id
                    switch ($states[$id]) {
                        'Bound' { Write-Ok ('{0} back on rt640x64.sys (DriverVer {1})' -f $id, $inf.DriverVer) }
                        'PendingReboot' { Write-Warn ('{0}: rt640x64 reinstalled, becomes active at the next reboot (the previous driver runs until then)' -f $id) }
                        'NotStarted' { Write-Fail ('rt640x64 reinstalled but did not start: {0}' -f (Format-NicLine $now)) "reboot; still no network -> $LocalUndoCmd" }
                        default { Write-Fail ('still not on rt640x64.sys after pnputil and the forced install: {0}; {1}' -f (Format-NicLine $now), (Format-OldDriverState $now)) "run $RunCmd and take a photo" }
                    }
                }
            }
        } elseif (-not @($infos | Where-Object { (Get-BindState $_ $inf $false) -ne 'Bound' }).Count) {
            Write-Info 'all Realtek adapters are on rt640x64.sys'
        }
        # Настройки — каждую загрузку: обновление драйвера (и через Windows Update) сбрасывает их. Только у карты, которая
        # работает на rt640x64 (не ждёт перезагрузки). Отличается значение и поменялось — -NoRestart и один перезапуск карты.
        foreach ($id in $ids) {
            $info = Get-NicInfo $id
            if ((Get-BindState $info $inf $false) -ne 'Bound' -or -not $info.Adapter) { continue }
            $c1 = Set-NicSettings $info
            $c2 = Set-DevicePower $info
            if ($c1 -or $c2) {
                Write-Info ("restarting adapter '{0}' so the changed settings apply" -f $info.Name)
                try { $info.Adapter | Restart-NetAdapter -Confirm:$false -ErrorAction Stop }
                catch { Write-Warn ("restart of adapter '{0}' failed: {1}" -f $info.Name, $_.Exception.Message) }
            }
        }
        [void](Set-Aspm)
    } catch {
        Write-Fail ('guard error: {0}' -f $_.Exception.Message)
    } finally {
        Add-GuardLog ('---- guard end: {0} FAIL, {1} WARN' -f $Count.Fail, $Count.Warn)
        # Журнал ограничен: больше 256 КБ — остаются последние 1500 строк.
        try {
            if ((Get-Item -LiteralPath $GuardLog -ErrorAction Stop).Length -gt 262144) {
                $keep = @([IO.File]::ReadAllLines($GuardLog) | Select-Object -Last 1500)
                [IO.File]::WriteAllLines($GuardLog, [string[]]$keep, (New-Object System.Text.UTF8Encoding $false))
            }
        } catch { }
    }
}

# --- Установка (без ключей) -------------------------------------------------------------------------------------------------
function Invoke-Install {
    Write-Host ("Realtek RTL8111H driver fix for {0} (driver set from {1})" -f $env:COMPUTERNAME, $SetUrl) -ForegroundColor Cyan

    Write-Title 'Step 1: Realtek PCI\VEN_10EC&DEV_8168 network adapters'
    if (Test-NetworkBoot) {
        throw 'The system disk of this PC is on iSCSI (network boot, diskless): the driver of the boot network card must never be switched live (blue screen). Nothing was changed.'
    }
    $ids = @(Get-NicDevices | ForEach-Object { $_.InstanceId })
    if ($ids.Count -eq 0) {
        Write-Host 'No Realtek PCI\VEN_10EC&DEV_8168 adapter in this PC: nothing to do.' -ForegroundColor Green
        $State.NothingToDo = $true
        return
    }
    foreach ($id in $ids) { Show-NicInfo (Get-NicInfo $id) }
    Show-InboxCapability

    Write-Title 'Step 2: network events of the last 7 days (System log, read only)'
    Show-NicEvents

    # Сначала всё скачать: при замене драйвера сеть пропадает. Набор нужен и при уже стоящем rt640x64 — для сторожа.
    Write-Title "Step 3: driver set from the club server into $LocalDir"
    $entries = @(Save-ServedSet)
    $infEntry = @($entries | Where-Object { $_.Name -ieq $InfName })[0]
    $inf = Get-InfData (Join-Path $LocalDir $infEntry.Name)
    Test-InfFiles $inf
    Write-Ok ('served driver {0}: DriverVer {1}, lists PCI\VEN_10EC&DEV_8168; files {2}' -f $infEntry.Name, $inf.DriverVer, ((@($inf.Catalogs) + @($inf.Files)) -join ', '))
    foreach ($id in $ids) {
        if ($id -match 'SUBSYS_([0-9A-Fa-f]{8})') {
            # Подсистему — в переменную до второго -match: он перезаписывает $Matches.
            $subsys = $Matches[1].ToUpperInvariant()
            $exact = 'no, generic match only (the forced install may be needed)'
            if ($inf.Text -match ('DEV_8168&SUBSYS_' + $subsys)) { $exact = 'yes' }
            Write-Info ('INF lists this board (SUBSYS_{0}): {1}' -f $subsys, $exact)
        }
    }
    foreach ($cat in $inf.Catalogs) {
        $sig = Get-AuthenticodeSignature -LiteralPath (Join-Path $LocalDir $cat)
        $signer = ''
        if ($sig.SignerCertificate -and $sig.SignerCertificate.Subject -match 'CN=([^,]+)') { $signer = $Matches[1] }
        if ("$($sig.Status)" -eq 'Valid') { Write-Ok ('catalog {0}: signature Valid ({1})' -f $cat, $signer) }
        else { Write-Warn ('catalog {0}: signature {1} ({2})' -f $cat, $sig.Status, $signer) 'pnputil checks the signature itself and refuses a badly signed driver' }
    }

    Write-Title 'Step 4: driver switch to Realtek rt640x64'
    $infos = @($ids | ForEach-Object { Get-NicInfo $_ })
    $pending = @($infos | Where-Object { -not (Test-Current $_ $inf.Version) })
    if ($pending.Count -eq 0) {
        foreach ($i in $infos) {
            if ((Get-BindState $i $inf $false) -eq 'PendingReboot') {
                $State.RebootPending = $true
                Write-Warn ("adapter '{0}': rt640x64.sys {1} is installed but waits for a reboot" -f $i.Name, $i.DriverVersion) 'reboot'
            } else {
                Write-Ok ("adapter '{0}' already on rt640x64.sys {1} (served {2}): no switch needed" -f $i.Name, $i.DriverVersion, $inf.Version)
            }
        }
    } else {
        foreach ($p in $pending) { Write-Info ("adapter '{0}': {1} {2} -> rt640x64.sys {3}" -f $p.Name, $p.DriverFileName, $p.DriverVersion, $inf.Version) }
        Write-Host ''
        Write-Host 'The switch restarts the network adapter: the network drops for 5-30 seconds.' -ForegroundColor Yellow
        Write-Host 'Close games running from G: or H: first (the helper mounts the drives again by itself afterwards).' -ForegroundColor Yellow
        if (-not (Read-YesNo ('Switch {0} adapter(s) to Realtek rt640x64 {1} now?' -f $pending.Count, $inf.Version) $true)) {
            Write-Warn 'driver not switched (answered No): nothing was changed on this PC' "close the games and run the same command again: $RunCmd"
            return
        }
        $State.Changed = $true
        $pendingIds = @($pending | ForEach-Object { $_.InstanceId })
        $states = Install-RealtekDriver $inf $pendingIds
        # На FAIL дальше ничего не меняется. Откат — локальный: при сбое сети irm не сработает, а файлы уже скачаны.
        $failed = $false
        foreach ($id in $pendingIds) {
            $now = Get-NicInfo $id
            Write-Info ('now: {0}' -f (Format-NicLine $now))
            switch ($states[$id]) {
                'Bound' {
                    if (Test-Current $now $inf.Version) { Write-Ok ("adapter '{0}' now on {1} {2}" -f $now.Name, $now.DriverFileName, $now.DriverVersion) }
                    else { Write-Warn ("adapter '{0}' is on {1} {2}, older than the served {3}" -f $now.Name, $now.DriverFileName, $now.DriverVersion, $inf.Version) 'take a photo of this window' }
                }
                'PendingReboot' {
                    $State.RebootPending = $true
                    Write-Warn ("rt640x64 {0} is installed on {1}, the switch completes at the reboot (the previous driver runs until then)" -f $inf.Version, $id) `
                        'reboot; the startup guard applies the adapter settings at the next startup'
                }
                'NotStarted' {
                    $failed = $true
                    Write-Fail ('rt640x64 is set up on the device but did not start (no network adapter or a device error): the network may be down') `
                        "reboot once; still no network -> $LocalUndoCmd (no network needed: puts the inbox driver back)"
                }
                default {
                    $failed = $true
                    Write-Fail ('the adapter is still NOT on rt640x64.sys after pnputil and the forced install; {0}' -f (Format-OldDriverState $now)) `
                        "take a photo of this window; the rt640x64 package stays in the driver store (remove it, no network needed: $LocalUndoCmd)"
                }
            }
        }
        if ($failed) { Write-Info 'nothing else was changed (no adapter settings, no guard)'; return }
    }

    Write-Title 'Step 5: network check'
    Test-Network $ids

    Write-Title 'Step 6: adapter power saving off (applies after the reboot), PCIe ASPM off'
    foreach ($id in $ids) {
        $info = Get-NicInfo $id
        $bind = Get-BindState $info $inf $false
        if ($bind -eq 'PendingReboot') {
            # Ключевые слова прежнего (ещё работающего) драйвера менять бессмысленно: rt640x64 их выставит сторож.
            Write-Info ("{0}: rt640x64 becomes active at the reboot; its settings are applied by the startup guard then" -f $id)
            continue
        }
        if ($bind -ne 'Bound' -or -not $info.Adapter) { Write-Warn ('{0}: not on rt640x64.sys, settings skipped' -f (Format-NicLine $info)); continue }
        Write-Info ("adapter '{0}':" -f $info.Name)
        if (Set-NicSettings $info) { $State.Changed = $true }
        if (Set-DevicePower $info) { $State.Changed = $true }
    }
    if (Set-Aspm) { $State.Changed = $true }

    Write-Title "Step 7: startup guard (task $TaskName)"
    Write-Info 'At every startup it checks the adapters, puts rt640x64 back from the local files if Windows swapped the driver,'
    Write-Info 'and turns the power saving off again if a driver update reset it. No network, no windows.'
    if ($State.RebootPending) { Write-Info 'rt640x64 becomes active at the reboot: the guard then sets its power saving off (one adapter restart).' }
    if (Read-YesNo 'Install the startup guard?' $true) { Register-Guard }
    elseif ($State.RebootPending) { Write-Warn 'startup guard not installed (answered No): after the reboot the rt640x64 power saving stays ON' "after the reboot run $RunCmd again" }
    else { Write-Warn 'startup guard not installed (answered No)' "a Windows feature update may bring the inbox driver back; run $RunCmd again to add the guard" }
}

# --- Откат (-Undo) -----------------------------------------------------------------------------------------------------------
# Пакеты rt640x64.inf в хранилище драйверов: Get-WindowsDriver (не разбор вывода pnputil — он на языке Windows), при его
# ошибке — база драйверов в реестре; плюс INF, на котором карта сейчас.
function Get-RealtekPackages([object[]]$Infos) {
    $found = [ordered]@{}
    try {
        foreach ($d in @(Get-WindowsDriver -Online -ErrorAction Stop)) {
            if ("$($d.OriginalFileName)" -match '(^|\\)rt640x64\.inf$' -and "$($d.Driver)" -match '^oem\d+\.inf$') {
                $found[$d.Driver.ToLowerInvariant()] = '{0} {1} {2:yyyy-MM-dd}' -f $d.ProviderName, $d.Version, $d.Date
            }
        }
    } catch {
        Write-Info ("Get-WindowsDriver failed ({0}): reading the driver database in the registry" -f $_.Exception.Message)
        foreach ($root in @('HKLM:\DRIVERS\DriverDatabase\DriverInfFiles', 'HKLM:\SYSTEM\DriverDatabase\DriverInfFiles')) {
            if (-not (Test-Path -LiteralPath $root)) { continue }
            foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                if ($key.PSChildName -notmatch '^oem\d+\.inf$') { continue }
                $packages = @($key.GetValue('')) + @($key.GetValue('Active')) | Where-Object { "$_" -match '^rt640x64\.inf_' }
                if (@($packages).Count) { $found[$key.PSChildName.ToLowerInvariant()] = "$(@($packages)[0])" }
            }
            break
        }
    }
    foreach ($i in $Infos) {
        if ((Test-OnRealtekNdis $i) -and $i.InfPath -match '^oem\d+\.inf$' -and -not $found.Contains($i.InfPath.ToLowerInvariant())) {
            $found[$i.InfPath.ToLowerInvariant()] = "in use by '$($i.Name)'"
        }
    }
    $found
}

# Запасной драйвер для отката: компонент встроенного драйвера установлен или rtcx21x64.sys есть в хранилище драйверов
# (по файлу, а не по имени папки пакета). Нет ни того, ни другого — после удаления rt640x64 карта осталась бы без драйвера.
function Test-InboxFallback {
    $capState = ''
    try { $capState = "$((Get-WindowsCapability -Online -Name $InboxCapability -ErrorAction Stop).State)" }
    catch { $capState = "not readable ($($_.Exception.Message))" }
    Write-Info ("inbox driver capability {0}: {1}" -f $InboxCapability, $capState)
    $repo = Join-Path $env:windir 'System32\DriverStore\FileRepository'
    $store = @(Get-ChildItem -LiteralPath $repo -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $InboxFile) } | ForEach-Object { $_.Name })
    if ($store.Count) { Write-Info ("inbox driver package in the driver store: {0}" -f ($store -join ', ')) }
    else { Write-Info ("inbox driver package in the driver store: no {0} found in {1}" -f $InboxFile, $repo) }
    ($capState -eq 'Installed') -or ($store.Count -gt 0)
}

function Invoke-Undo {
    Write-Host ("Undo of the Realtek rt640x64 driver on {0}" -f $env:COMPUTERNAME) -ForegroundColor Cyan

    Write-Title "Step 1: startup guard task $TaskName"
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Write-Fail "task $TaskName is still registered" }
        else { Write-Ok "guard task $TaskName removed" }
    } else {
        Write-Info "no task $TaskName"
    }

    Write-Title 'Step 2: Realtek adapters and rt640x64.inf packages in the driver store'
    $ids = @(Get-NicDevices | ForEach-Object { $_.InstanceId })
    $infos = @($ids | ForEach-Object { Get-NicInfo $_ })
    foreach ($i in $infos) { Write-Info (Format-NicLine $i) }
    $packages = Get-RealtekPackages $infos
    if ($packages.Count -eq 0) {
        Write-Ok 'no rt640x64.inf package in the driver store: the adapter already uses the Windows driver'
    } else {
        foreach ($k in @($packages.Keys)) { Write-Info ('{0}  rt640x64.inf  {1}' -f $k, $packages[$k]) }
        if ($ids.Count -and (Test-NetworkBoot)) { throw 'The system disk of this PC is on iSCSI (network boot): the driver of the boot network card must never be removed live. Nothing was removed.' }
        # Есть карта — сначала убедиться, что ей будет на что вернуться; нет запасного драйвера — по умолчанию «нет».
        $fallback = $true
        if ($ids.Count) { $fallback = Test-InboxFallback }
        if (-not $fallback) {
            Write-Warn 'no inbox Realtek driver found (capability not Installed, no rtcx21x64.sys in the driver store): after the removal the adapter would have NO driver and NO network' `
                'answer No; if the inbox driver is needed: Settings -> Optional features, or Windows Update'
        }
        Write-Host ''
        Write-Host 'Removing them restarts the network adapter on the Windows inbox driver: the network drops for 5-30 seconds.' -ForegroundColor Yellow
        Write-Host 'Close games running from G: or H: first.' -ForegroundColor Yellow
        if (-not (Read-YesNo ('Remove {0} rt640x64 package(s) (pnputil /delete-driver ... /uninstall /force)?' -f $packages.Count) $fallback)) {
            Write-Warn 'driver packages not removed (answered No)'
        } else {
            $State.Changed = $true
            foreach ($k in @($packages.Keys)) {
                $r = Invoke-Native $Pnputil @('/delete-driver', $k, '/uninstall', '/force')
                if ($r.Code -in @(0, 3010)) { Write-Ok ("{0} removed (pnputil exit code {1})" -f $k, $r.Code) }
                else { Write-Fail ("pnputil /delete-driver {0} /uninstall /force: exit code {1}" -f $k, $r.Code) }
            }
            if ($ids.Count) {
                # Карта должна вернуться на встроенный драйвер сама; нет за 20 с — pnputil /scan-devices.
                Write-Info 'waiting up to 60 s for the adapter on the Windows inbox driver'
                $deadline = (Get-Date).AddSeconds(60)
                $scanned = $false
                $start = Get-Date
                while ($true) {
                    $now = @($ids | ForEach-Object { Get-NicInfo $_ })
                    $left = @($now | Where-Object { (Test-OnRealtekNdis $_) -or -not $_.Adapter })
                    if ($left.Count -eq 0 -or (Get-Date) -ge $deadline) { break }
                    if (-not $scanned -and ((Get-Date) - $start).TotalSeconds -ge 20) {
                        Write-Info 'pnputil /scan-devices'
                        [void](Invoke-Native $Pnputil @('/scan-devices'))
                        $scanned = $true
                    }
                    Start-Sleep -Seconds 3
                }
                foreach ($i in $now) {
                    if ((Test-OnRealtekNdis $i) -or -not $i.Adapter) {
                        # Без сети: irm не сработает, а задачи-сторожа уже нет — печатаем, как вернуть rt640x64 из локальных файлов.
                        Write-Fail ('adapter not back on the inbox driver: {0}' -f (Format-NicLine $i)) `
                            "reboot; still no network -> rt640x64 back from the local files (no network needed): $LocalGuardCmd (log: $GuardLog), or: $LocalPnputilCmd"
                    }
                    else { Write-Ok ("adapter '{0}' back on {1} {2} ({3})" -f $i.Name, $i.DriverFileName, $i.DriverVersion, $i.DriverProvider) }
                }
                Write-Title 'Step 3: network check'
                Test-Network $ids
            }
        }
    }

    Write-Title 'Left as they are'
    Write-Info 'PCIe ASPM of the power plan and the adapter power setting were NOT reverted. To revert them:'
    Write-Info '  powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 1   (0 off, 1 moderate, 2 maximum; old values: install log)'
    Write-Info '  powercfg /setdcvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 2'
    Write-Info '  powercfg /setactive SCHEME_CURRENT'
    Write-Info '  Get-NetAdapterPowerManagement -Name <adapter> | ForEach-Object { $_.AllowComputerToTurnOffDevice = ''Enabled''; $_ } | Set-NetAdapterPowerManagement'
    Write-Info ('Files stay in {0} (local -Undo: {1}); delete the folder by hand if it is not needed.' -f $LocalDir, $LocalUndoCmd)
    Write-Info ('rt640x64 back from these files without network: {0} (or: {1})' -f $LocalGuardCmd, $LocalPnputilCmd)
}

# ======================================================================================================================
$exitCode = 0
try {
    if ($State.BadArgs.Count) { throw ("Unknown or conflicting arguments: {0}. Use none, -Guard, -Undo, -NoReboot." -f ($State.BadArgs -join ' ')) }
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run PowerShell as Administrator (Win+X -> Terminal (Admin)).'
    }
    # 32-битный PowerShell: pnputil из SysWOW64 нет, а UpdateDriverForPlugAndPlayDevices отвечает ERROR_IN_WOW64.
    if (-not [Environment]::Is64BitProcess) { throw 'Run the 64-bit PowerShell (C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe).' }

    if ($State.Mode -eq 'Guard') {
        Invoke-Guard
    } else {
        $unsafe = @(Initialize-LocalDir)
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $suffix = ''
        if ($State.Mode -eq 'Undo') { $suffix = '-undo' }
        $State.LogFile = Join-Path $LogDir ('{0}-{1}{2}.txt' -f $env:COMPUTERNAME, $stamp, $suffix)
        try { Start-Transcript -LiteralPath $State.LogFile | Out-Null } catch { }
        try {
            # Папка сторожа небезопасна (ссылка, чужой владелец) — установка не идёт; откату папка не нужна.
            if ($unsafe.Count) {
                $text = 'folder {0} is not safe for a SYSTEM task: {1}' -f $LocalDir, ($unsafe -join '; ')
                $fix = "delete $LocalDir by hand (as Administrator; a link: only the link itself) and run the command again"
                if ($State.Mode -eq 'Undo') { Write-Warn $text $fix }
                else { throw ('{0}. Fix: {1}. Nothing was changed.' -f $text, $fix) }
            }
            if ($State.Mode -eq 'Undo') { Invoke-Undo } else { Invoke-Install }
        } catch {
            Write-Host ''
            Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
            $Count.Fail++
            [void]$Results.Add("FAIL  stopped: $($_.Exception.Message)")
        } finally {
            if (-not $State.NothingToDo) {
                Write-Host ''
                Write-Host ("==> Summary for {0}" -f $env:COMPUTERNAME) -ForegroundColor Cyan
                foreach ($line in $Results) {
                    $color = 'Green'
                    if ($line -like 'WARN*') { $color = 'Yellow' } elseif ($line -like 'FAIL*') { $color = 'Red' }
                    Write-Host $line -ForegroundColor $color
                }
                $color = 'Green'
                if ($Count.Fail -gt 0) { $color = 'Red' } elseif ($Count.Warn -gt 0) { $color = 'Yellow' }
                Write-Host ("Result: {0} FAIL, {1} WARN. Log: {2}" -f $Count.Fail, $Count.Warn, $State.LogFile) -ForegroundColor $color
                if ($State.Mode -ne 'Undo' -and $State.Changed) { Write-Host "Undo: $UndoCmd" }
            }
            try { Stop-Transcript | Out-Null } catch { }
        }
        # После журнала: Restart-Computer закрывает окно, итог уже записан. Есть FAIL — по умолчанию нет. Замена драйвера
        # ждёт перезагрузки — это WARN, не FAIL: по умолчанию да.
        if ($State.Changed -or $State.RebootPending) {
            if ($State.RebootPending) { Write-Host 'The switch to rt640x64 completes only at the reboot.' -ForegroundColor Yellow }
            if ($NoReboot) {
                Write-Host 'Reboot skipped (-NoReboot): reboot this PC before players use it (the driver switch and the adapter settings apply after the reboot).' -ForegroundColor Yellow
            } else {
                $clean = $Count.Fail -eq 0
                if (-not $clean) { Write-Host 'There are FAIL lines above: take a photo of this window before rebooting.' -ForegroundColor Red }
                if (Read-YesNo 'Reboot now?' $clean) { Restart-Computer -Force }
            }
        } elseif (-not $State.NothingToDo) {
            Write-Host 'No driver or setting was changed: no reboot needed.'
        }
    }
} catch {
    if ($State.Mode -eq 'Guard') { Add-GuardLog ('guard stopped: {0}' -f $_.Exception.Message) }
    else { Write-Host ''; Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red }
    $Count.Fail++
} finally {
    $ErrorActionPreference = $savedErrorAction
    $ProgressPreference = $savedProgress
}
if ($Count.Fail -gt 0) { $exitCode = 1 }
# exit — только при запуске файлом (-File, задача-сторож): под irm | iex он закрыл бы окно консоли.
if ($PSCommandPath) { exit $exitCode }
