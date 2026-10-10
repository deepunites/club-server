#Requires -Version 5.1
<#
.SYNOPSIS
    Подготовка эталона Windows 11 (полный бездиск) перед публикацией версии.

.DESCRIPTION
    Запускать ТОЛЬКО внутри эталона в режиме мастера (ПК грузится прямо с zvol эталона), PowerShell от администратора,
    перед КАЖДОЙ публикацией версии (docs/diskless-pilot.md). Повторный запуск безопасен: каждый шаг ставит значение,
    а не добавляет его; ошибка одного шага не останавливает остальные. В конце — проверка (строки PASS/WARN).

    Что делает:
      - электропитание: гибернация и быстрый запуск выключены, сон — никогда, PCIe ASPM выключен (экран не трогаем);
      - дампы памяти выключены, восстановление при загрузке и WinRE выключены (BCD, reagentc);
      - Windows Update: без автообновлений, без драйверов, версия 25H2; оптимизация доставки, контроль памяти,
        автоматическое обслуживание, дефрагментация по расписанию, индексатор (WSearch), точки восстановления — выключены;
        исключения Defender для G:\ и H:\; запрет шифрования устройства;
      - iSCSI: удержание запросов при обрыве 120 с (MaxRequestHoldTime), таймаут диска 120 с, служба MSiSCSI — автоматически
        (её ждёт помощник для G:), iScsiPrt грузится при старте;
      - загрузочная сетевая карта (по ней идёт системный диск): служба драйвера грузится при старте (Start=0, BootFlags=1),
        энергосбережение выключено (PnPCapabilities=24, EEE / Green Ethernet / Power Saving Mode).

    Загрузочную сетевую карту скрипт НИКОГДА не перезапускает и не отключает: вместе с ней пропал бы системный диск
    (синий экран). Её свойства меняются с -NoRestart и вступают в силу после перезагрузки ПК.

.PARAMETER StageDrivers
    Папка с драйверами (*.inf, с подпапками): pnputil /add-driver <папка>\*.inf /subdirs — только в хранилище драйверов
    Windows, без установки на устройства (/install не используется никогда).

.PARAMETER DisableMemoryIntegrity
    Выключить целостность памяти (HVCI): DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity Enabled=0
    (действует после перезагрузки; заметнее всего на Ryzen 5 1600).

.PARAMETER Force
    Продолжить, даже если системный диск не iSCSI или ПК загружен как обычное место (изменения места пропадут при
    следующей загрузке).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\ProgramData\ClubGolden\prepare-golden.ps1 -DisableMemoryIntegrity

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\ProgramData\ClubGolden\prepare-golden.ps1 -StageDrivers D:\Drivers\NIC
#>
[CmdletBinding()]
param(
    [string]$StageDrivers,
    [switch]$DisableMemoryIntegrity,
    [switch]$Force
)

$ErrorActionPreference = 'Continue'

# --- Постоянные -------------------------------------------------------------------------------------------------------
$NetClass  = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}'  # сетевые карты
$ScsiClass = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E97B-E325-11CE-BFC1-08002BE10318}'  # SCSI-адаптеры (iSCSI)
$Services  = 'HKLM:\SYSTEM\CurrentControlSet\Services'
$WuPolicy  = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$HoldTimeSec    = 120   # MaxRequestHoldTime: сколько iSCSI держит и повторяет запросы при обрыве (по умолчанию 60)
$DiskTimeoutSec = 120   # disk\TimeOutValue: не меньше удержания, иначе диск отказывает раньше (0x7A)
$ExclusionPaths = @('G:\', 'H:\')
$TargetRelease  = '25H2'
# Свойства энергосбережения сетевой карты: по отображаемому имени (англ. и рус.) и по ключевому слову реестра.
$PowerSavingNames    = '(?i)\bEEE\b|Energy.?Efficient|Green.?Ethernet|Power Saving|Ultra Low Power|Энергосбер|Зелён|Зелен'
$PowerSavingKeywords = '(?i)^\*?EEE$|^AdvancedEEE$|^EnableGreenEthernet$|^PowerSavingMode$|^ULPMode$'

$script:FailedSteps = New-Object 'System.Collections.Generic.List[string]'
$script:WarnCount = 0
$script:BootNic = $null          # Get-NetAdapter загрузочной карты
$script:BootNicService = $null   # имя службы драйвера (rt640x64, e1dexpress …)
$script:BootNicClassKey = $null  # ключ экземпляра в классе сетевых карт (…\{4D36E972…}\nnnn)

# --- Вывод ------------------------------------------------------------------------------------------------------------
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "INFO  $Text" }
function Write-Warn([string]$Text, [string]$Hint) {
    $script:WarnCount++
    Write-Host "WARN  $Text" -ForegroundColor Yellow
    if ($Hint) { Write-Host "      -> $Hint" -ForegroundColor Yellow }
}

# Шаг: свой try/catch — ошибка одного шага не останавливает остальные.
function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Title $Title
    try {
        $ErrorActionPreference = 'Stop'
        & $Action
        Write-Host '    OK' -ForegroundColor Green
    } catch {
        $script:FailedSteps.Add($Title)
        Write-Host ('    ОШИБКА: {0}' -f $_.Exception.Message) -ForegroundColor Red
    }
}

# Проверка: блок возвращает $true/$false; исключение — тоже WARN.
function Test-Check([string]$Text, [scriptblock]$Test, [string]$Hint) {
    try { $ok = [bool](& $Test) } catch { $ok = $false; $Hint = 'проверка не выполнилась: ' + $_.Exception.Message }
    if ($ok) { Write-Pass $Text } else { Write-Warn $Text $Hint }
}

# --- Реестр -----------------------------------------------------------------------------------------------------------
function Format-RegPath([string]$Path) { $Path -replace '^HKLM:\\', 'HKLM\' }

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

# Создаёт только отсутствующие ключи (по цепочке родителей): New-Item -Force на существующем ключе может стереть его значения.
function New-RegKey([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return }
    New-RegKey (Split-Path -Path $Path -Parent)
    New-Item -Path $Path -ErrorAction Stop | Out-Null
}

function Set-RegValue([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord') {
    New-RegKey $Path
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force -ErrorAction Stop | Out-Null
    Write-Line ('{0} : {1} = {2}' -f (Format-RegPath $Path), $Name, $Value)
}

# Ключ службы: значения ставим, только если служба уже есть. Ключ службы не создаём никогда (служба без драйвера — 0x7B).
function Set-ServiceBootStart([string]$Name) {
    $key = "$Services\$Name"
    if (-not (Test-Path -LiteralPath $key)) { throw ('нет службы {0} — ключ не создаём' -f (Format-RegPath $key)) }
    Set-RegValue $key 'Start' 0
    Set-RegValue $key 'BootFlags' 1
}

function Test-RegValue([string]$Path, [string]$Name, $Expected, [string]$Hint) {
    $actual = Get-RegValue $Path $Name
    $shown = if ($null -eq $actual) { '(нет)' } else { "$actual" }
    $text = '{0} : {1} = {2}' -f (Format-RegPath $Path), $Name, $shown
    if ($null -ne $actual -and "$actual" -eq "$Expected") { Write-Pass $text } else { Write-Warn "$text, нужно $Expected" $Hint }
}

# Экземпляры класса устройств: подключи 0000, 0001…; служебные (Properties, Configuration) бывают закрыты даже администратору.
function Get-ClassInstanceKeys([string]$ClassPath) {
    Get-ChildItem -LiteralPath $ClassPath -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^\d{4}$' } |
        ForEach-Object { "$ClassPath\$($_.PSChildName)" }
}

# Инициатор iSCSI Microsoft в классе SCSI-адаптеров. DriverDesc бывает локализован — поэтому ещё по INF и ИД устройства.
function Get-IscsiInitiatorKeys {
    foreach ($key in Get-ClassInstanceKeys $ScsiClass) {
        $desc = Get-RegValue $key 'DriverDesc'
        $inf = Get-RegValue $key 'InfPath'
        $match = Get-RegValue $key 'MatchingDeviceId'
        if ($desc -eq 'Microsoft iSCSI Initiator' -or $inf -eq 'iscsi.inf' -or $match -eq 'root\iscsiprt') { $key }
    }
}

# --- Внешние программы ------------------------------------------------------------------------------------------------
function Invoke-Native([string]$File, [string[]]$Arguments, [int[]]$OkCodes = @(0)) {
    Write-Line ('> {0} {1}' -f $File, ($Arguments -join ' '))
    $ErrorActionPreference = 'Continue'   # вывод в stderr внешней программы — не исключение PowerShell
    $output = & $File @Arguments 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $output) { if ("$line".Trim()) { Write-Line "  $line" } }
    if ($OkCodes -notcontains $code) { throw ('{0} завершился с кодом {1}' -f $File, $code) }
}

function Get-NativeOutput([string]$File, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    return ((& $File @Arguments 2>&1) | ForEach-Object { "$_" }) -join "`n"
}

# Текущие индексы настройки схемы питания: последние два числа 0x… в выводе powercfg /query — от сети и от батареи.
function Get-PowerIndex([string]$SubGroup, [string]$Setting) {
    $text = Get-NativeOutput 'powercfg.exe' @('/query', 'SCHEME_CURRENT', $SubGroup, $Setting)
    $values = @([regex]::Matches($text, '0x([0-9a-fA-F]{8})') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value, 16) })
    if ($values.Count -lt 2) { return $null }
    return @{ AC = $values[$values.Count - 2]; DC = $values[$values.Count - 1] }
}

# Состояние WinRE из ReAgent.xml (InstallState 1 — включена, 0 — выключена); вывод reagentc /info локализован.
function Get-WinReState {
    $path = Join-Path $env:SystemRoot 'System32\Recovery\ReAgent.xml'
    if (-not (Test-Path -LiteralPath $path)) { return 'unknown' }
    try { [xml]$xml = Get-Content -LiteralPath $path -Raw -ErrorAction Stop } catch { return 'unknown' }
    $state = "$($xml.WindowsRE.InstallState.state)"
    if ($state -eq '1') { return 'enabled' }
    if ($state -eq '0') { return 'disabled' }
    return 'unknown'
}

# --- Диски и сеть -----------------------------------------------------------------------------------------------------
function Get-SystemDisk {
    Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop | Get-Disk -ErrorAction Stop
}

# Сессии iSCSI системного диска; если связь с диском не прочиталась — все сессии, кроме томов игр (games-…).
function Get-BootSessions {
    $sessions = @()
    try { $sessions = @(Get-SystemDisk | Get-IscsiSession -ErrorAction Stop) } catch { }
    if (-not $sessions) {
        try { $sessions = @(Get-IscsiSession -ErrorAction Stop | Where-Object { "$($_.TargetNodeAddress)" -notmatch ':games-' }) } catch { }
    }
    return $sessions
}

# Карта, по которой идёт системный диск: адрес инициатора сессии iSCSI -> IP -> карта; адрес 0.0.0.0 — маршрут к порталу;
# запасной вариант — поднятая физическая карта со шлюзом по умолчанию.
function Find-BootNic {
    foreach ($session in Get-BootSessions) {
        $connections = @()
        try { $connections = @($session | Get-IscsiConnection -ErrorAction Stop) } catch { }
        foreach ($c in $connections) {
            if ($c.InitiatorAddress -and $c.InitiatorAddress -ne '0.0.0.0') {
                $ip = Get-NetIPAddress -IPAddress $c.InitiatorAddress -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($ip) {
                    $adapter = Get-NetAdapter -InterfaceIndex $ip.InterfaceIndex -ErrorAction SilentlyContinue
                    if ($adapter) {
                        return @{ Adapter = $adapter; How = ('сессия iSCSI {0}, адрес инициатора {1}' -f $session.TargetNodeAddress, $c.InitiatorAddress) }
                    }
                }
            }
            if ($c.TargetAddress) {
                $route = Find-NetRoute -RemoteIPAddress $c.TargetAddress -ErrorAction SilentlyContinue |
                    Where-Object { $_.InterfaceIndex } | Select-Object -First 1
                if ($route) {
                    $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
                    if ($adapter) {
                        return @{ Adapter = $adapter; How = ('сессия iSCSI {0}, маршрут к порталу {1}' -f $session.TargetNodeAddress, $c.TargetAddress) }
                    }
                }
            }
        }
    }
    $physical = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })
    foreach ($cfg in @(Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway })) {
        $adapter = $physical | Where-Object { $_.InterfaceIndex -eq $cfg.InterfaceIndex } | Select-Object -First 1
        if ($adapter) {
            return @{ Adapter = $adapter; How = 'ЗАПАСНОЙ ВАРИАНТ: поднятая физическая карта со шлюзом (сессия iSCSI не найдена)' }
        }
    }
    return $null
}

function Find-NetClassKey([string]$InterfaceGuid) {
    foreach ($key in Get-ClassInstanceKeys $NetClass) {
        if ("$(Get-RegValue $key 'NetCfgInstanceId')" -eq $InterfaceGuid) { return $key }
    }
    return $null
}

# Имя службы драйвера карты: значение Service в ключе устройства (Enum), иначе Win32_PnPEntity, иначе имя файла драйвера.
function Find-NicService($Adapter) {
    $service = Get-RegValue "HKLM:\SYSTEM\CurrentControlSet\Enum\$($Adapter.PnPDeviceID)" 'Service'
    if ($service) { return "$service" }
    try {
        $id = $Adapter.PnPDeviceID -replace '\\', '\\'
        $service = (Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID='$id'" -ErrorAction Stop).Service
        if ($service) { return "$service" }
    } catch { }
    if ($Adapter.DriverFileName) { return [IO.Path]::GetFileNameWithoutExtension($Adapter.DriverFileName) }
    return $null
}

function Assert-BootNic {
    if (-not $script:BootNic) { throw 'загрузочная сетевая карта не найдена (см. шаг «поиск»)' }
}

# Значение «выключено» для свойства карты — только по отображаемому значению (Disabled/Off/Выкл…). Нет такого значения
# (например, «EEE Max Support Speed» — выбор скорости) — свойство не трогаем.
function Get-DisabledValue($Property) {
    $display = @($Property.ValidDisplayValues)
    $registry = @($Property.ValidRegistryValues)
    for ($i = 0; $i -lt $display.Count -and $i -lt $registry.Count; $i++) {
        if ("$($display[$i])" -match '(?i)^\s*(Disabled?|Off|Выкл|Откл)') { return "$($registry[$i])" }
    }
    return $null
}

function Get-PowerSavingProperties([string]$AdapterName) {
    @(Get-NetAdapterAdvancedProperty -Name $AdapterName -ErrorAction Stop |
        Where-Object { "$($_.DisplayName)" -match $PowerSavingNames -or "$($_.RegistryKeyword)" -match $PowerSavingKeywords })
}

# ======================================================================================================================
# 0. Предупреждение и проверки перед началом
# ======================================================================================================================
Write-Host ''
Write-Host '############################################################################' -ForegroundColor Red
Write-Host '##                                                                        ##' -ForegroundColor Red
Write-Host '##   ВНИМАНИЕ! Подготовка ЭТАЛОНА ПОЛНОГО БЕЗДИСКА.' -ForegroundColor Red
Write-Host '##   Запускать ТОЛЬКО внутри эталона в РЕЖИМЕ МАСТЕРА' -ForegroundColor Red
Write-Host '##   (панель -> «Бездиск» -> «Режим мастера»), перед публикацией версии.' -ForegroundColor Red
Write-Host '##   НЕ запускать на гибридных ПК, сервере, ноутбуке администратора:' -ForegroundColor Red
Write-Host '##   выключает Windows Update, восстановление, гибернацию, точки' -ForegroundColor Red
Write-Host '##   восстановления и меняет загрузку сетевой карты.' -ForegroundColor Red
Write-Host '##                                                                        ##' -ForegroundColor Red
Write-Host '############################################################################' -ForegroundColor Red
Write-Host ''

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Нужны права администратора: запустите PowerShell «от имени администратора». Ничего не изменено.' -ForegroundColor Red
    exit 1
}

# Бездисковый ли это ПК и мастер ли: системный диск по iSCSI; таргет эталона — …:diskless-master, таргет обычного
# места — …:seat-NN (его диск сбрасывается при каждой загрузке, изменения пропадут).
$systemDisk = $null
try { $systemDisk = Get-SystemDisk } catch { }
$busType = if ($systemDisk) { "$($systemDisk.BusType)" } else { '?' }
if ($busType -ne 'iSCSI' -and $busType -ne '9') {
    Write-Host ('Системный диск {0} подключён не по iSCSI (BusType = {1}): это не бездисковый ПК.' -f $env:SystemDrive, $busType) -ForegroundColor Red
    if (-not $Force) {
        Write-Host 'Остановлено, ничего не изменено. Если это точно эталон в режиме мастера — запустите с -Force.' -ForegroundColor Red
        exit 1
    }
} else {
    # Командлетам iSCSI нужна служба MSiSCSI (её же ждёт помощник для G:).
    try { if ((Get-Service -Name MSiSCSI).Status -ne 'Running') { Start-Service -Name MSiSCSI -ErrorAction Stop } } catch { }
    $targets = @(Get-BootSessions | ForEach-Object { "$($_.TargetNodeAddress)" })
    if ($targets) {
        Write-Host ('Системный диск: таргет {0}' -f ($targets -join ', '))
        if (@($targets | Where-Object { $_ -match ':diskless-master$' }).Count -gt 0) {
            Write-Host 'Режим мастера: да.' -ForegroundColor Green
        } elseif (@($targets | Where-Object { $_ -match ':seat-\d+$' }).Count -gt 0) {
            Write-Host 'Этот ПК загружен как ОБЫЧНОЕ МЕСТО: его диск сбрасывается при каждой загрузке, изменения пропадут.' -ForegroundColor Red
            Write-Host 'Включите «Режим мастера» для этого ПК в панели и перезагрузите его.' -ForegroundColor Red
            if (-not $Force) { Write-Host 'Остановлено, ничего не изменено.' -ForegroundColor Red; exit 1 }
        } else {
            Write-Host 'По имени таргета не понять, мастер ли это, — продолжаю.' -ForegroundColor Yellow
        }
    } else {
        Write-Host 'Сессия iSCSI системного диска не прочиталась — продолжаю (убедитесь, что это режим мастера).' -ForegroundColor Yellow
    }
}

# ======================================================================================================================
# 1. Электропитание
# ======================================================================================================================
Invoke-Step 'Гибернация выключена (powercfg /hibernate off)' {
    Invoke-Native 'powercfg.exe' @('/hibernate', 'off')
}

Invoke-Step 'Быстрый запуск (Fast Startup) выключен' {
    Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
}

Invoke-Step 'Сон — никогда (отключение экрана не трогаем)' {
    Invoke-Native 'powercfg.exe' @('/change', 'standby-timeout-ac', '0')
    Invoke-Native 'powercfg.exe' @('/change', 'standby-timeout-dc', '0')
}

Invoke-Step 'PCIe ASPM выключен (энергосбережение шины PCIe — обрывы сетевой карты)' {
    Invoke-Native 'powercfg.exe' @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0')
    Invoke-Native 'powercfg.exe' @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0')
    Invoke-Native 'powercfg.exe' @('/setactive', 'SCHEME_CURRENT')
}

# ======================================================================================================================
# 2. Дампы и восстановление (дампа на iSCSI без драйвера дампа производителя не бывает; восстановление на месте бессмысленно)
# ======================================================================================================================
Invoke-Step 'Дампы памяти выключены' {
    Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' 'CrashDumpEnabled' 0
}

Invoke-Step 'Восстановление при загрузке выключено (BCD {default})' {
    Invoke-Native 'bcdedit.exe' @('/set', '{default}', 'recoveryenabled', 'No')
    Invoke-Native 'bcdedit.exe' @('/set', '{default}', 'bootstatuspolicy', 'IgnoreAllFailures')
}

Invoke-Step 'Среда восстановления WinRE выключена (reagentc /disable)' {
    try {
        Invoke-Native 'reagentc.exe' @('/disable')
    } catch {
        if ((Get-WinReState) -eq 'disabled') { Write-Line 'WinRE уже выключена' } else { throw }
    }
}

# ======================================================================================================================
# 3. Windows Update и фоновые задачи (каждое место грузится с отката — «пропущенное» обслуживание шло бы на каждой загрузке)
# ======================================================================================================================
Invoke-Step 'Windows Update: без автообновлений, без драйверов, версия 25H2' {
    Set-RegValue "$WuPolicy\AU" 'NoAutoUpdate' 1
    # Драйвер из Windows Update под другим именем службы теряет BootFlags сетевой карты -> 0x7B; заодно не меняет видеодрайверы.
    Set-RegValue $WuPolicy 'ExcludeWUDriversInQualityUpdate' 1
    Set-RegValue $WuPolicy 'TargetReleaseVersion' 1
    Set-RegValue $WuPolicy 'ProductVersion' 'Windows 11' 'String'
    Set-RegValue $WuPolicy 'TargetReleaseVersionInfo' $TargetRelease 'String'
    # И для новых устройств драйверы в Windows Update не искать.
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching' 'SearchOrderConfig' 0
}

Invoke-Step 'Оптимизация доставки (Delivery Optimization) — без раздачи между ПК' {
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode' 0
}

Invoke-Step 'Контроль памяти (Storage Sense) выключен' {
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\StorageSense' 'AllowStorageSenseGlobal' 0
}

Invoke-Step 'Автоматическое обслуживание выключено' {
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' 'MaintenanceDisabled' 1
}

Invoke-Step 'Дефрагментация по расписанию (ScheduledDefrag) выключена' {
    $task = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction SilentlyContinue
    if ($task) { $task | Disable-ScheduledTask | Out-Null; Write-Line 'задача ScheduledDefrag выключена' }
    else { Write-Line 'задачи ScheduledDefrag нет — пропуск' }
}

Invoke-Step 'Индексатор поиска (WSearch) выключен' {
    $service = Get-Service -Name 'WSearch' -ErrorAction SilentlyContinue
    if (-not $service) { Write-Line 'службы WSearch нет — пропуск'; return }
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'WSearch' -Force }
    Set-Service -Name 'WSearch' -StartupType Disabled
    Write-Line 'WSearch: остановлена, запуск — отключён'
}

Invoke-Step 'Точки восстановления (System Restore) выключены' {
    try { Disable-ComputerRestore -Drive ('{0}\' -f $env:SystemDrive) } catch { Write-Line ('Disable-ComputerRestore: {0}' -f $_.Exception.Message) }
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore' 'DisableSR' 1
}

Invoke-Step 'Defender: исключения для дисков игр G:\ и H:\' {
    Add-MpPreference -ExclusionPath $ExclusionPaths
    Write-Line ('исключения добавлены: {0}' -f ($ExclusionPaths -join ', '))
}

Invoke-Step 'Шифрование устройства запрещено (PreventDeviceEncryption)' {
    Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 1
}

if ($DisableMemoryIntegrity) {
    Invoke-Step 'Целостность памяти (HVCI) выключена (-DisableMemoryIntegrity, после перезагрузки)' {
        Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled' 0
    }
}

# ======================================================================================================================
# 4. iSCSI: переживать короткие обрывы сети
# ======================================================================================================================
Invoke-Step 'Служба MSiSCSI — автоматически (её ждёт помощник для диска игр G:)' {
    Set-Service -Name 'MSiSCSI' -StartupType Automatic
    if ((Get-Service -Name 'MSiSCSI').Status -ne 'Running') { Start-Service -Name 'MSiSCSI' }
    Write-Line 'MSiSCSI: автоматически, запущена'
}

Invoke-Step ('iSCSI: удержание запросов при обрыве {0} с (MaxRequestHoldTime)' -f $HoldTimeSec) {
    $keys = @(Get-IscsiInitiatorKeys)
    if (-not $keys) { throw 'в классе SCSI-адаптеров нет экземпляра «Microsoft iSCSI Initiator»' }
    foreach ($key in $keys) { Set-RegValue "$key\Parameters" 'MaxRequestHoldTime' $HoldTimeSec }
}

Invoke-Step ('Таймаут диска {0} с (disk\TimeOutValue)' -f $DiskTimeoutSec) {
    Set-RegValue "$Services\disk" 'TimeOutValue' $DiskTimeoutSec
}

Invoke-Step 'iScsiPrt грузится при старте (Start=0, BootFlags=1)' {
    Set-ServiceBootStart 'iScsiPrt'
}

# ======================================================================================================================
# 5. Загрузочная сетевая карта. НИКОГДА не перезапускать, не отключать, не менять ей драйвер: по ней идёт системный диск.
# ======================================================================================================================
Invoke-Step 'Загрузочная сетевая карта: поиск' {
    $found = Find-BootNic
    if (-not $found) { throw 'не удалось определить карту, по которой идёт системный диск' }
    $nic = $found.Adapter
    $script:BootNic = $nic
    $script:BootNicClassKey = Find-NetClassKey "$($nic.InterfaceGuid)"
    $script:BootNicService = Find-NicService $nic
    Write-Line ('как найдена          : {0}' -f $found.How)
    Write-Line ('Name                 : {0}' -f $nic.Name)
    Write-Line ('InterfaceDescription : {0}' -f $nic.InterfaceDescription)
    Write-Line ('DriverFileName       : {0}' -f $nic.DriverFileName)
    Write-Line ('DriverVersion        : {0}' -f $nic.DriverVersion)
    Write-Line ('PnPDeviceID          : {0}' -f $nic.PnPDeviceID)
    Write-Line ('MacAddress, скорость : {0}, {1}' -f $nic.MacAddress, $nic.LinkSpeed)
    Write-Line ('служба драйвера      : {0}' -f $script:BootNicService)
    if ($script:BootNicClassKey) {
        Write-Line ('ключ класса          : {0} (INF {1}, {2})' -f (Format-RegPath $script:BootNicClassKey),
            (Get-RegValue $script:BootNicClassKey 'InfPath'), (Get-RegValue $script:BootNicClassKey 'ProviderName'))
    } else {
        Write-Line 'ключ класса          : не найден'
    }
    # Сетевые карты PCI, которые эталон уже видел (введённые платы); у Realtek одной модели платы ИД экземпляра совпадает.
    Write-Line 'сетевые карты PCI в эталоне:'
    Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object { "$($_.InstanceId)" -like 'PCI\*' } | ForEach-Object {
        Write-Line ('  [{0}] {1} | {2}' -f $_.Status, $_.FriendlyName, $_.InstanceId)
    }
}

Invoke-Step 'Загрузочная сетевая карта: служба драйвера грузится при старте (Start=0, BootFlags=1)' {
    Assert-BootNic
    if (-not $script:BootNicService) { throw 'не определено имя службы драйвера' }
    Set-ServiceBootStart $script:BootNicService
}

Invoke-Step 'Загрузочная сетевая карта: энергосбережение выключено (карта НЕ перезапускается)' {
    Assert-BootNic
    $nic = $script:BootNic
    # «Разрешить отключение этого устройства для экономии энергии» и пробуждение — выкл.
    if ($script:BootNicClassKey) { Set-RegValue $script:BootNicClassKey 'PnPCapabilities' 24 }
    else { Write-Line 'ключ класса карты не найден — PnPCapabilities не выставлен' }
    $properties = Get-PowerSavingProperties $nic.Name
    if (-not $properties) { Write-Line 'свойств EEE/Green Ethernet/Power Saving у драйвера нет (так у Realtek «Not Support Power Saving»)' }
    foreach ($p in $properties) {
        $off = Get-DisabledValue $p
        $label = '{0} [{1}]' -f $p.DisplayName, $p.RegistryKeyword
        if ($null -eq $off) { Write-Line "$label`: нет значения «выключено» — пропуск"; continue }
        if ("$($p.RegistryValue)" -eq $off) { Write-Line "$label`: уже выключено"; continue }
        # -NoRestart обязателен: без него Windows перезапустит карту, и системный диск пропадёт.
        Set-NetAdapterAdvancedProperty -Name $nic.Name -RegistryKeyword $p.RegistryKeyword -RegistryValue $off -NoRestart -ErrorAction Stop
        Write-Line ('{0}: {1} -> выключено' -f $label, $p.DisplayValue)
    }
    Write-Host '    Карта не перезапускалась: изменения её свойств вступят в силу после перезагрузки ПК.' -ForegroundColor Yellow
}

Invoke-Step 'Сетевые карты других плат, уже введённых в эталон: PnPCapabilities=24' {
    # Экземпляры Ethernet (*IfType 6) на PCI; их дополнительные свойства — запуском этого скрипта на той плате в режиме мастера.
    $count = 0
    foreach ($key in Get-ClassInstanceKeys $NetClass) {
        if ($key -eq $script:BootNicClassKey) { continue }
        $match = "$(Get-RegValue $key 'MatchingDeviceId')"
        if ($match -like 'pci\*' -and (Get-RegValue $key '*IfType') -eq 6) {
            Set-RegValue $key 'PnPCapabilities' 24
            $count++
        }
    }
    if ($count -eq 0) { Write-Line 'других сетевых карт PCI нет' }
}

# ======================================================================================================================
# 6. Драйверы в хранилище (по желанию). Только /add-driver: /install на живой загрузочной карте сменил бы её драйвер — BSOD.
# ======================================================================================================================
if ($StageDrivers) {
    Invoke-Step ('Драйверы в хранилище Windows (без установки): {0}' -f $StageDrivers) {
        if (-not (Test-Path -LiteralPath $StageDrivers -PathType Container)) { throw ('нет папки {0}' -f $StageDrivers) }
        $infs = @(Get-ChildItem -LiteralPath $StageDrivers -Filter '*.inf' -Recurse -File -ErrorAction SilentlyContinue)
        if (-not $infs) { throw ('в {0} нет файлов *.inf' -f $StageDrivers) }
        Write-Line ('INF в папке: {0}' -f $infs.Count)
        Invoke-Native 'pnputil.exe' @('/add-driver', (Join-Path $StageDrivers '*.inf'), '/subdirs') -OkCodes @(0, 259, 3010)
    }
}

# ======================================================================================================================
# 7. Проверка
# ======================================================================================================================
Write-Host ''
Write-Host '============================== ПРОВЕРКА ==============================' -ForegroundColor Cyan

# Windows
$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$build = Get-RegValue $cv 'CurrentBuild'
Write-Info ('Windows: {0}, {1}, сборка {2}.{3}' -f (Get-RegValue $cv 'EditionID'), (Get-RegValue $cv 'DisplayVersion'), $build, (Get-RegValue $cv 'UBR'))
Test-Check ('версия Windows {0}' -f $TargetRelease) { (Get-RegValue $cv 'DisplayVersion') -eq $TargetRelease } 'эталон ставится с ISO 25H2; 26H1 (сборка 28000+) ломает передачу диска от iPXE'

# Электропитание
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled' 0 'powercfg /hibernate off'
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
$standby = Get-PowerIndex 'SUB_SLEEP' 'STANDBYIDLE'
Test-Check ('сон: никогда (от сети {0} с)' -f $(if ($standby) { $standby.AC } else { '?' })) { $standby -and $standby.AC -eq 0 } 'powercfg /change standby-timeout-ac 0'
$aspm = Get-PowerIndex 'SUB_PCIEXPRESS' 'ASPM'
Test-Check ('PCIe ASPM: выкл. (от сети {0})' -f $(if ($aspm) { $aspm.AC } else { '?' })) { $aspm -and $aspm.AC -eq 0 } 'powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0; ASPM выключить и в BIOS'

# Дампы и восстановление
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' 'CrashDumpEnabled' 0
$bcd = Get-NativeOutput 'bcdedit.exe' @('/enum', '{default}')
Test-Check 'BCD {default}: recoveryenabled No' { $bcd -match '(?im)^recoveryenabled\s+(No|Нет)\s*$' }
Test-Check 'BCD {default}: bootstatuspolicy IgnoreAllFailures' { $bcd -match '(?im)^bootstatuspolicy\s+IgnoreAllFailures\s*$' }
$winre = Get-WinReState
Test-Check ('WinRE выключена ({0})' -f $winre) { $winre -eq 'disabled' } 'reagentc /disable; состояние — reagentc /info'

# Windows Update и фоновые задачи
Test-RegValue "$WuPolicy\AU" 'NoAutoUpdate' 1
Test-RegValue $WuPolicy 'ExcludeWUDriversInQualityUpdate' 1
Test-RegValue $WuPolicy 'TargetReleaseVersion' 1
Test-RegValue $WuPolicy 'ProductVersion' 'Windows 11'
Test-RegValue $WuPolicy 'TargetReleaseVersionInfo' $TargetRelease
Test-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching' 'SearchOrderConfig' 0
Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode' 0
Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\StorageSense' 'AllowStorageSenseGlobal' 0
Test-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' 'MaintenanceDisabled' 1
Test-Check 'задача ScheduledDefrag выключена' {
    $task = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction SilentlyContinue
    (-not $task) -or ("$($task.State)" -eq 'Disabled')
}
Test-Check 'служба WSearch: запуск отключён' {
    $service = Get-Service -Name 'WSearch' -ErrorAction SilentlyContinue
    (-not $service) -or ("$($service.StartType)" -eq 'Disabled')
}
Test-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore' 'DisableSR' 1
Test-Check ('Defender: исключения {0}' -f ($ExclusionPaths -join ', ')) {
    $current = @((Get-MpPreference -ErrorAction Stop).ExclusionPath)
    @($ExclusionPaths | Where-Object { $current -notcontains $_ }).Count -eq 0
} 'Add-MpPreference -ExclusionPath G:\,H:\'
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 1

# BitLocker / шифрование устройства: перед публикацией все тома — Fully Decrypted.
$volumes = $null
try { $volumes = @(Get-BitLockerVolume -ErrorAction Stop) } catch { }
if ($volumes) {
    foreach ($v in $volumes) {
        $text = 'BitLocker {0}: {1}, защита {2}' -f $v.MountPoint, $v.VolumeStatus, $v.ProtectionStatus
        if ("$($v.VolumeStatus)" -eq 'FullyDecrypted') { Write-Pass $text }
        else { Write-Warn $text ('manage-bde -off {0} и дождаться «Fully Decrypted» (manage-bde -status) до публикации' -f $v.MountPoint) }
    }
} else {
    $status = Get-NativeOutput 'manage-bde.exe' @('-status', $env:SystemDrive)
    Test-Check ('BitLocker {0}: полностью расшифрован (manage-bde)' -f $env:SystemDrive) { $status -match 'Fully Decrypted|Полностью расшифрован' } ('manage-bde -status {0}; если шифруется — manage-bde -off {0}' -f $env:SystemDrive)
}

# Целостность памяти (HVCI)
$hvciPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
if ($DisableMemoryIntegrity) { Test-RegValue $hvciPath 'Enabled' 0 }
try {
    $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName 'Win32_DeviceGuard' -ErrorAction Stop
    $running = @($dg.SecurityServicesRunning) -contains 2
    Write-Info ('целостность памяти (HVCI) сейчас {0}; в реестре Enabled = {1} (действует после перезагрузки)' -f $(if ($running) { 'работает' } else { 'не работает' }), (Get-RegValue $hvciPath 'Enabled'))
} catch { }

# iSCSI
$iscsiKeys = @(Get-IscsiInitiatorKeys)
if ($iscsiKeys) { foreach ($key in $iscsiKeys) { Test-RegValue "$key\Parameters" 'MaxRequestHoldTime' $HoldTimeSec } }
else { Write-Warn 'экземпляр «Microsoft iSCSI Initiator» в классе SCSI-адаптеров не найден' }
Test-Check ('disk\TimeOutValue >= {0}' -f $DiskTimeoutSec) { [int](Get-RegValue "$Services\disk" 'TimeOutValue') -ge $DiskTimeoutSec }
Test-Check 'служба MSiSCSI: автоматически и запущена' {
    $service = Get-Service -Name 'MSiSCSI' -ErrorAction Stop
    ("$($service.StartType)" -eq 'Automatic') -and ("$($service.Status)" -eq 'Running')
}
Test-RegValue "$Services\iScsiPrt" 'Start' 0
Test-RegValue "$Services\iScsiPrt" 'BootFlags' 1
Test-Check 'iScsiPrt: нет подключа StartOverride' { -not (Test-Path -LiteralPath "$Services\iScsiPrt\StartOverride") } 'StartOverride подменяет Start при загрузке — удалить подключ'

# Загрузочная сетевая карта
if (-not $script:BootNic) {
    Write-Warn 'загрузочная сетевая карта не найдена: флаги загрузки и энергосбережение карты не проверены' 'iscsicli SessionList; Get-NetAdapter | fl Name,InterfaceDescription,DriverFileName,PnPDeviceID'
} else {
    $nic = $script:BootNic
    $service = $script:BootNicService
    $driver = "$($nic.DriverFileName)"
    Write-Info ('загрузочная карта: {0} | {1} | {2} | служба {3}' -f $nic.Name, $nic.InterfaceDescription, $driver, $service)
    if ($service) {
        Test-RegValue "$Services\$service" 'Start' 0
        Test-RegValue "$Services\$service" 'BootFlags' 1
        Test-Check ('{0}: нет подключа StartOverride' -f $service) { -not (Test-Path -LiteralPath "$Services\$service\StartOverride") } 'StartOverride подменяет Start при загрузке — удалить подключ'
    }

    # Драйвер: для Realtek нужен NDIS rt640x64; rtcx21x64 — встроенный NetAdapterCx-драйвер Windows 11.
    if ($driver -match '(?i)^rtcx21' -or $service -match '(?i)^rtcx21') {
        Write-Warn ('драйвер карты — встроенный NetAdapterCx ({0}), а не rt640x64' -f $driver) 'установите драйвер Realtek NDIS «Win10/Win11 Auto Installation Program (NDIS) - Not Support Power Saving» (rt640x64): положите его в /srv/club/pxe/winsetup/drv/ на сервере и переустановите эталон (docs/diskless-pilot.md). Драйвер живой загрузочной карты НЕ менять — синий экран'
    } elseif ($driver -match '(?i)^rt640') {
        Write-Pass ('драйвер карты: Realtek NDIS {0} ({1})' -f $driver, $nic.DriverVersion)
        try {
            $cap = Get-WindowsCapability -Online -Name 'Microsoft.Windows.Ethernet.Client.Realtek.Rtcx21x64~~~~0.0.1.0' -ErrorAction Stop
            if ("$($cap.State)" -eq 'Installed') {
                Write-Info 'встроенный драйвер Realtek NetAdapterCx (rtcx21x64) ещё в образе; по желанию: dism /online /Remove-Capability /CapabilityName:Microsoft.Windows.Ethernet.Client.Realtek.Rtcx21x64~~~~0.0.1.0'
            }
        } catch { }
    } else {
        Write-Info ('драйвер карты {0} (не Realtek): платы с этой картой вводить в эталон отдельно (docs/diskless-pilot.md)' -f $driver)
    }

    Test-Check ('скорость карты не ниже 1 Гбит/с ({0})' -f $nic.LinkSpeed) { [uint64]$nic.Speed -ge 1000000000 } 'кабель, порт коммутатора; 100 Мбит/с — загрузка мест будет очень медленной'

    # Сторонние компоненты (фильтры NDIS, протоколы) на загрузочной карте: новый экземпляр карты на другой плате -> 0x7B.
    try {
        $foreign = @(Get-NetAdapterBinding -Name $nic.Name -AllBindings -IncludeHidden -ErrorAction Stop |
            Where-Object { $_.Enabled -and "$($_.ComponentID)" -notlike 'ms_*' })
        if ($foreign) {
            foreach ($b in $foreign) {
                Write-Warn ('на загрузочной карте включён сторонний компонент {0} ({1})' -f $b.ComponentID, $b.DisplayName) 'удалить программу, которая его поставила (VPN, Npcap, сетевой фильтр антивируса), в режиме мастера и перезагрузить'
            }
        } else {
            Write-Pass 'на загрузочной карте нет сторонних компонентов (только ms_*)'
        }
    } catch {
        Write-Warn 'привязки карты не прочитались' $_.Exception.Message
    }

    if ($script:BootNicClassKey) { Test-RegValue $script:BootNicClassKey 'PnPCapabilities' 24 }
    else { Write-Warn 'ключ класса загрузочной карты не найден: PnPCapabilities не проверен' }
    try {
        foreach ($p in (Get-PowerSavingProperties $nic.Name)) {
            $off = Get-DisabledValue $p
            $text = '{0} [{1}] = {2}' -f $p.DisplayName, $p.RegistryKeyword, $p.DisplayValue
            if ($null -eq $off) { Write-Info "$text (значения «выключено» нет)" }
            elseif ("$($p.RegistryValue)" -eq $off) { Write-Pass $text }
            else { Write-Warn "$text, нужно «выключено»" 'Set-NetAdapterAdvancedProperty ... -NoRestart (этот скрипт) и перезагрузка' }
        }
    } catch {
        Write-Warn 'дополнительные свойства карты не прочитались' $_.Exception.Message
    }
}

# Файл подкачки — только для сведения (на пилоте: при DRIVER_IRQL_NOT_LESS_OR_EQUAL на первой загрузке — выключить).
$paging = @(Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'PagingFiles') -join '; '
Write-Info ('файл подкачки: {0}' -f $(if ($paging.Trim()) { $paging } else { '(нет)' }))

# ======================================================================================================================
# Итог
# ======================================================================================================================
Write-Host ''
Write-Host '================================ ИТОГ ================================' -ForegroundColor Cyan
if ($script:FailedSteps.Count -gt 0) {
    Write-Host ('Шагов с ошибкой: {0}' -f $script:FailedSteps.Count) -ForegroundColor Red
    foreach ($step in $script:FailedSteps) { Write-Host "  - $step" -ForegroundColor Red }
} else {
    Write-Host 'Все шаги выполнены.' -ForegroundColor Green
}
if ($script:WarnCount -gt 0) { Write-Host ('Предупреждений (WARN): {0} — разобрать до публикации.' -f $script:WarnCount) -ForegroundColor Yellow }
else { Write-Host 'Предупреждений нет.' -ForegroundColor Green }
Write-Host ''
Write-Host 'Дальше: перезагрузить ПК мастера один раз (свойства сетевой карты вступят в силу), убедиться, что Windows'
Write-Host 'загрузилась и диск игр G: подключился, затем выключить ПК и опубликовать версию в панели («Бездиск»).'

if ($script:FailedSteps.Count -gt 0) { exit 2 }
exit 0
