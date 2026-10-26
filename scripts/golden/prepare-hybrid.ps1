#Requires -Version 5.1
<#
.SYNOPSIS
    Подготовка эталона гибрида (Windows на локальном SSD) на эталонном ПК PC-11 перед seal-hybrid.ps1 и sysprep.

.DESCRIPTION
    Эталон гибрида — опубликованный эталон бездиска (img-v2), залитый на PC-11 перезаливкой по PXE без sysprep
    (docs/imaging.md). Запускать на PC-11 после того, как он загрузился С ЛОКАЛЬНОГО ДИСКА, PowerShell от администратора.
    Повторный запуск безопасен: каждый шаг ставит значение, а не добавляет его; ошибка одного шага не останавливает
    остальные. В конце — проверка (строки PASS/WARN). Программы скрипт не ставит.

    Что делает:
      - возвращает то, что prepare-golden.ps1 выключил для бездиска, а локальному SSD нужно: TRIM в NTFS, TRIM C: раз
        в неделю (своя задача \Club\RetrimSystemDrive — только системный диск), файл подкачки на C: по выбору системы,
        VBS и целостность памяти (HVCI);
      - оставляет как в бездиске: Windows Update без автообновлений и драйверов, запрет шифрования устройства,
        гибернация и быстрый запуск выключены, автоматическое обслуживание и ScheduledDefrag выключены (оптимизировали
        бы и G: — клон библиотеки по iSCSI);
      - загрузка с локального диска: storahci и stornvme грузятся при старте (без StartOverride); службы сетевых карт и
        iScsiPrt — НЕ при старте (Start=3, без BootFlags: так их ставил эталон бездиска для загрузки по iSCSI);
      - образы NGEN (.NET Framework) — достройка, как в seal-golden.ps1;
      - последним шагом — очистка хранилища компонентов DISM /StartComponentCleanup /ResetBase (долго).
    Проверяет: Secure Boot и TPM, VBS и HVCI работают, BitLocker выключен, IQN инициатора iSCSI — по умолчанию
    (iqn.1991-05.com.microsoft:<имя ПК>, а не IQN места из iBFT), помощник бездиска (служба, диск игр G:), буква H:
    свободна для диска офлайн-игр (или он подключён).

.PARAMETER SkipCleanup
    Не запускать DISM /StartComponentCleanup /ResetBase (повторный запуск для проверки после перезагрузки).

.PARAMETER TargetRelease
    Версия Windows, на которой держит Windows Update (TargetReleaseVersionInfo), как в prepare-golden.ps1.

.PARAMETER Force
    Продолжить, даже если шина системного диска не из внутренних (NVMe, SATA, SAS, RAID, ATA, SCSI) или не прочиталась.
    Отказ всегда (и с -Force): системный диск по iSCSI или есть сессия iSCSI таргета бездиска (diskless-master, seat-NN).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\prepare-hybrid.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\prepare-hybrid.ps1 -SkipCleanup
#>
[CmdletBinding()]
param(
    [switch]$SkipCleanup,
    [string]$TargetRelease = '26H2',
    [switch]$Force
)

$ErrorActionPreference = 'Continue'

# --- Постоянные -------------------------------------------------------------------------------------------------------
$Services    = 'HKLM:\SYSTEM\CurrentControlSet\Services'
$WuPolicy    = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$DeviceGuard = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
$HvciPath    = "$DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"
$MemoryMgmt  = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
$Maintenance = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'
$PageFileSpec = '{0}\pagefile.sys 0 0' -f $env:SystemDrive   # размер 0 0 — по выбору системы
$HelperService = 'ClubDisklessHelper'
$HelperStateDir = "$env:ProgramData\ClubDiskless"
$StorageServices = @('storahci', 'stornvme')
# Службы сетевых карт, которым эталон бездиска ставил загрузку при старте (install.cmd в extract-winsetup.sh,
# prepare-golden.ps1); к ним добавляются службы всех физических карт этого ПК и iScsiPrt.
$NetBootServices = @('rt640x64', 'rtcx21x64', 'e1dexpress', 'e1iexpress', 'e2fexpress')
$LocalBusTypes = @('NVMe', 'SATA', 'SAS', 'RAID', 'ATA', 'SCSI')
# BusType бывает числом (MSFT_Disk): 1 SCSI, 3 ATA, 8 RAID, 9 iSCSI, 10 SAS, 11 SATA, 17 NVMe.
$BusTypeNames = @{ '1' = 'SCSI'; '3' = 'ATA'; '7' = 'USB'; '8' = 'RAID'; '9' = 'iSCSI'; '10' = 'SAS'; '11' = 'SATA'; '14' = 'Virtual'; '17' = 'NVMe' }
$DefaultIqnPrefix = 'iqn.1991-05.com.microsoft:'
$DisklessTargetPattern = ':(diskless-master|seat-\d+)$'
# TRIM локального SSD: своя задача только для системного диска (ScheduledDefrag оптимизирует все тома, и G:).
$RetrimTaskPath = '\Club\'
$RetrimTaskName = 'RetrimSystemDrive'
$RetrimArguments = '{0} /L' -f $env:SystemDrive   # defrag /L — только TRIM (Retrim), без дефрагментации

$script:FailedSteps = New-Object 'System.Collections.Generic.List[string]'
$script:WarnCount = 0

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

function Format-Value($Value) { if ($null -eq $Value) { '(нет)' } else { "$Value" } }
function Format-Bool($Value) { if ($Value) { 'да' } else { 'нет' } }

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
    Write-Line ('{0} : {1} = {2}' -f (Format-RegPath $Path), $Name, ($Value -join '; '))
}

function Test-RegValue([string]$Path, [string]$Name, $Expected, [string]$Hint) {
    $actual = Get-RegValue $Path $Name
    $text = '{0} : {1} = {2}' -f (Format-RegPath $Path), $Name, (Format-Value $actual)
    if ($null -ne $actual -and "$actual" -eq "$Expected") { Write-Pass $text } else { Write-Warn "$text, нужно $Expected" $Hint }
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

# TRIM в NTFS: fsutil behavior query DisableDeleteNotify — строка «NTFS DisableDeleteNotify = 0» (0 — TRIM включён).
function Get-NtfsTrimDisabled {
    $text = Get-NativeOutput 'fsutil.exe' @('behavior', 'query', 'DisableDeleteNotify')
    $m = [regex]::Match($text, '(?im)^\s*(?:NTFS\s+)?DisableDeleteNotify\s*=\s*(\d+)')
    if (-not $m.Success) { return $null }
    return [int]$m.Groups[1].Value
}

function Get-DefragTask {
    Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction SilentlyContinue
}

function Get-RetrimTask {
    Get-ScheduledTask -TaskPath $RetrimTaskPath -TaskName $RetrimTaskName -ErrorAction SilentlyContinue
}

# --- Диски и сеть -----------------------------------------------------------------------------------------------------
function Get-SystemDisk {
    Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop | Get-Disk -ErrorAction Stop
}

function Get-BusName($BusType) {
    $text = "$BusType"
    if ($BusTypeNames.ContainsKey($text)) { return $BusTypeNames[$text] }
    return $text
}

# Сессии iSCSI загрузочного диска бездиска (таргет мастера или места, DisklessOptions): есть — это бездисковый ПК, даже
# если шина системного диска не прочиталась. У гибрида сессии только к дискам игр (games-…) и правке библиотеки.
function Get-DisklessBootSessions {
    @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match $DisklessTargetPattern })
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

# Службы, которым на локальном диске НЕ нужна загрузка при старте: сетевые карты (известные и этого ПК) и iScsiPrt.
function Get-NetBootServiceNames {
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $NetBootServices) { [void]$names.Add($n) }
    foreach ($adapter in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
        $service = Find-NicService $adapter
        if ($service) { [void]$names.Add($service) }
    }
    [void]$names.Add('iScsiPrt')
    return @($names | Where-Object { Test-Path -LiteralPath "$Services\$_" } | Sort-Object)
}

# IQN инициатора iSCSI (как его читает помощник): Get-InitiatorPort отдаёт iSCSI-порт, только пока MSiSCSI запущена.
function Get-InitiatorIqn {
    try { if ((Get-Service -Name MSiSCSI -ErrorAction Stop).Status -ne 'Running') { Start-Service -Name MSiSCSI -ErrorAction Stop } } catch { }
    try {
        $port = Get-InitiatorPort -ErrorAction Stop | Where-Object { $_.ConnectionType -eq 'iSCSI' } | Select-Object -First 1
        if ($port) { return "$($port.NodeAddress)" }
    } catch { }
    return $null
}

# IQN по умолчанию Windows строит из имени компьютера (строчными; с DNS-суффиксом, если он задан).
function Test-DefaultIqn([string]$Iqn) {
    foreach ($name in @(@($env:COMPUTERNAME, [System.Net.Dns]::GetHostName()) | Where-Object { $_ })) {
        if ($Iqn -match ('^{0}{1}(\..+)?$' -f [regex]::Escape($DefaultIqnPrefix), [regex]::Escape("$name".ToLowerInvariant()))) { return $true }
    }
    return $false
}

# ======================================================================================================================
# 0. Предупреждение и проверки перед началом
# ======================================================================================================================
Write-Host ''
Write-Host '############################################################################' -ForegroundColor Red
Write-Host '##                                                                        ##' -ForegroundColor Red
Write-Host '##   ВНИМАНИЕ! Подготовка ЭТАЛОНА ГИБРИДА (Windows на локальном SSD).' -ForegroundColor Red
Write-Host '##   Запускать ТОЛЬКО на эталонном ПК гибрида (PC-11) после ЛОКАЛЬНОЙ' -ForegroundColor Red
Write-Host '##   загрузки эталона, перед seal-hybrid.ps1.' -ForegroundColor Red
Write-Host '##   НЕ запускать в эталоне бездиска (мастер), на местах, сервере:' -ForegroundColor Red
Write-Host '##   включает VBS/HVCI, файл подкачки, TRIM по расписанию, меняет загрузку' -ForegroundColor Red
Write-Host '##   драйверов сетевых карт и iSCSI.' -ForegroundColor Red
Write-Host '##                                                                        ##' -ForegroundColor Red
Write-Host '############################################################################' -ForegroundColor Red
Write-Host ''

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Нужны права администратора: запустите PowerShell «от имени администратора». Ничего не изменено.' -ForegroundColor Red
    exit 1
}

# Системный диск должен быть локальным: по iSCSI грузятся эталон бездиска и места — там этот скрипт ломает загрузку
# (службы карты и iScsiPrt -> Start=3 — синий экран 0x7B). Отказ — и с -Force: шина iSCSI или сессия таргета бездиска.
$systemDisk = $null
try { $systemDisk = Get-SystemDisk } catch { }
$busType = if ($systemDisk) { Get-BusName $systemDisk.BusType } else { '?' }
$bootSessions = Get-DisklessBootSessions
if ($busType -eq 'iSCSI' -or $bootSessions) {
    if ($busType -eq 'iSCSI') { $why = 'подключён по iSCSI' }
    else { $why = 'с сессией iSCSI таргета бездиска ({0})' -f (($bootSessions | ForEach-Object { "$($_.TargetNodeAddress)" }) -join ', ') }
    Write-Host ('Системный диск {0} {1}: это бездисковый ПК (эталон в режиме мастера или место), а не эталон гибрида.' -f $env:SystemDrive, $why) -ForegroundColor Red
    Write-Host 'Остановлено, ничего не изменено (-Force этого не обходит). Для эталона бездиска — prepare-golden.ps1.' -ForegroundColor Red
    exit 1
}
if ($LocalBusTypes -notcontains $busType) {
    Write-Host ('Шина системного диска {0}: {1} — не внутренний диск (NVMe, SATA, SAS, RAID, ATA, SCSI).' -f $env:SystemDrive, $busType) -ForegroundColor Red
    if (-not $Force) {
        Write-Host 'Остановлено, ничего не изменено. Если это точно локальный SSD эталона гибрида — запустите с -Force.' -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host ('Системный диск: {0}, шина {1}, {2:N0} ГБ, разметка {3} — локальный.' -f $systemDisk.FriendlyName, $busType, ($systemDisk.Size / 1GB), $systemDisk.PartitionStyle) -ForegroundColor Green
}

# ======================================================================================================================
# 1. Электропитание и Windows Update — как в бездиске
# ======================================================================================================================
Invoke-Step 'Гибернация выключена (powercfg /hibernate off)' {
    Invoke-Native 'powercfg.exe' @('/hibernate', 'off')
}

Invoke-Step 'Быстрый запуск (Fast Startup) выключен' {
    Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
}

Invoke-Step ('Windows Update: без автообновлений, без драйверов, версия {0}' -f $TargetRelease) {
    Set-RegValue "$WuPolicy\AU" 'NoAutoUpdate' 1
    # Драйверы из Windows Update не меняют видеодрайвер и драйвер сетевой карты образа.
    Set-RegValue $WuPolicy 'ExcludeWUDriversInQualityUpdate' 1
    Set-RegValue $WuPolicy 'TargetReleaseVersion' 1
    Set-RegValue $WuPolicy 'ProductVersion' 'Windows 11' 'String'
    Set-RegValue $WuPolicy 'TargetReleaseVersionInfo' $TargetRelease 'String'
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching' 'SearchOrderConfig' 0
}

Invoke-Step 'Шифрование устройства запрещено (PreventDeviceEncryption)' {
    Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 1
}

# ======================================================================================================================
# 2. Локальный SSD: TRIM только системного диска, файл подкачки
# ======================================================================================================================
# Обслуживание и ScheduledDefrag остаются выключенными, как в бездиске (там тот же G:): ScheduledDefrag (defrag -c)
# оптимизирует ВСЕ тома, и G:/H: — личные клоны библиотеки по iSCSI. Тонкий диск SAN Windows оптимизирует со слиянием
# слэбов (SlabConsolidate), диск, принятый за HDD, — дефрагментацией: это запись в клон каждого места и нагрузка на общий
# пул TrueNAS (вместе с проверками томов chkdsk из обслуживания). Поэтому TRIM локального SSD — своя задача только для C:.
Invoke-Step 'Автоматическое обслуживание выключено (как в бездиске: не трогает диски игр)' {
    Set-RegValue $Maintenance 'MaintenanceDisabled' 1
}

Invoke-Step 'Оптимизация дисков по расписанию (ScheduledDefrag: все тома, и G:) выключена (как в бездиске)' {
    $task = Get-DefragTask
    if (-not $task) { Write-Line 'задачи ScheduledDefrag нет — пропуск'; return }
    if ("$($task.State)" -ne 'Disabled') { $task | Disable-ScheduledTask | Out-Null; Write-Line 'задача ScheduledDefrag выключена' }
    else { Write-Line 'задача ScheduledDefrag уже выключена' }
}

Invoke-Step ('TRIM {0} раз в неделю: задача {1}{2} (defrag {3}, только системный диск)' -f $env:SystemDrive, $RetrimTaskPath, $RetrimTaskName, $RetrimArguments) {
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\defrag.exe" -Argument $RetrimArguments
    $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Wednesday -At '13:00'
    # ПК был выключен в это время — запуск при следующем включении; TRIM SSD на 240 ГБ — секунды.
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)
    # S-1-5-18 — SYSTEM: имя учётной записи на ru-RU локализовано, SID — нет.
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskPath $RetrimTaskPath -TaskName $RetrimTaskName -Action $action -Trigger $trigger -Settings $settings -Principal $taskPrincipal `
        -Description ('Клуб: TRIM только системного диска (defrag {0}); диски игр по iSCSI не трогает.' -f $RetrimArguments) -Force | Out-Null
    Write-Line ('задача {0}{1}: по средам 13:00 (или при включении после пропуска), SYSTEM' -f $RetrimTaskPath, $RetrimTaskName)
}

Invoke-Step 'TRIM в NTFS включён (DisableDeleteNotify = 0)' {
    $state = Get-NtfsTrimDisabled
    if ($state -eq 0) { Write-Line 'TRIM уже включён'; return }
    Invoke-Native 'fsutil.exe' @('behavior', 'set', 'DisableDeleteNotify', 'NTFS', '0')
}

Invoke-Step ('Файл подкачки: {0} по выбору системы (после перезагрузки)' -f $env:SystemDrive) {
    # Только на системном диске: на G: (диск игр по iSCSI) файла подкачки не должно быть никогда.
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    if ($cs.AutomaticManagedPagefile) {
        Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $false } -ErrorAction Stop
        Write-Line 'автоматический выбор для всех дисков выключен (файл — только на системном диске)'
    }
    Set-RegValue $MemoryMgmt 'PagingFiles' @($PageFileSpec) 'MultiString'
}

# ======================================================================================================================
# 3. VBS и целостность памяти (FACEIT и Vanguard на гибридах)
# ======================================================================================================================
Invoke-Step 'VBS и целостность памяти (HVCI) включены (после перезагрузки)' {
    Set-RegValue $DeviceGuard 'EnableVirtualizationBasedSecurity' 1
    Set-RegValue $DeviceGuard 'RequirePlatformSecurityFeatures' 1   # 1 — Secure Boot (3 — ещё и защита DMA)
    Set-RegValue $HvciPath 'Enabled' 1
    # Гипервизор нужен VBS. При заливке bcdboot создаёт BCD заново — это только для проверки на этом ПК.
    $bcd = Get-NativeOutput 'bcdedit.exe' @('/enum', '{current}')
    if ($bcd -match '(?im)^hypervisorlaunchtype\s+Off\s*$') { Invoke-Native 'bcdedit.exe' @('/set', '{current}', 'hypervisorlaunchtype', 'Auto') }
    else { Write-Line 'BCD {current}: hypervisorlaunchtype не Off' }
}

# ======================================================================================================================
# 4. Загрузочные драйверы: контроллеры диска — при старте; сетевые карты и iSCSI — нет (их включал эталон бездиска)
# ======================================================================================================================
Invoke-Step 'storahci и stornvme грузятся при старте (Start=0, без StartOverride)' {
    foreach ($name in $StorageServices) {
        $key = "$Services\$name"
        if (-not (Test-Path -LiteralPath $key)) { Write-Line ('нет службы {0} — ключ не создаём' -f $name); continue }
        if ("$(Get-RegValue $key 'Start')" -ne '0') { Write-Line ('{0}: Start = {1} — ИСПРАВЛЯЮ' -f $name, (Format-Value (Get-RegValue $key 'Start'))); Set-RegValue $key 'Start' 0 }
        else { Write-Line ('{0}: Start = 0' -f $name) }
        # StartOverride\0 = 3 отключает драйвер при загрузке (так Windows делает, если ставилась не в режиме AHCI) — 0x7B.
        if (Test-Path -LiteralPath "$key\StartOverride") {
            Remove-Item -LiteralPath "$key\StartOverride" -Recurse -Force -ErrorAction Stop
            Write-Line ('{0}: подключ StartOverride удалён — ИСПРАВЛЕНО' -f $name)
        }
    }
}

Invoke-Step 'Сетевые карты и iScsiPrt НЕ грузятся при старте (Start=3, без BootFlags)' {
    foreach ($name in Get-NetBootServiceNames) {
        $key = "$Services\$name"
        $start = Get-RegValue $key 'Start'
        $flags = Get-RegValue $key 'BootFlags'
        if ("$start" -eq '0' -or "$start" -eq '1') { Write-Line ('{0}: Start = {1} — ИСПРАВЛЯЮ' -f $name, $start); Set-RegValue $key 'Start' 3 }
        else { Write-Line ('{0}: Start = {1}' -f $name, (Format-Value $start)) }
        if ($null -ne $flags) {
            Remove-ItemProperty -LiteralPath $key -Name 'BootFlags' -Force -ErrorAction Stop
            Write-Line ('{0}: BootFlags = {1} удалён — ИСПРАВЛЕНО' -f $name, $flags)
        }
    }
}

# ======================================================================================================================
# 5. Образы NGEN и очистка хранилища компонентов
# ======================================================================================================================
# Как в seal-golden.ps1: после обновлений и новых программ .NET Framework образы NGEN надо достроить, иначе каждый запуск
# PowerShell компилирует всё заново.
Invoke-Step 'Образы NGEN (.NET Framework): достройка (несколько минут, процессор загружен)' {
    foreach ($ngen in "$env:windir\Microsoft.NET\Framework64\v4.0.30319\ngen.exe", "$env:windir\Microsoft.NET\Framework\v4.0.30319\ngen.exe",
                      "$env:windir\Microsoft.NET\Framework64\v2.0.50727\ngen.exe", "$env:windir\Microsoft.NET\Framework\v2.0.50727\ngen.exe") {
        if (-not (Test-Path -LiteralPath $ngen)) { continue }
        & $ngen executeQueuedItems /nologo /silent | Out-Null
        Write-Line ("{0}: код {1}" -f ($ngen -replace '^.*\\Microsoft\.NET\\', ''), $LASTEXITCODE)
    }
}

if ($SkipCleanup) {
    Write-Title 'Очистка хранилища компонентов (DISM /StartComponentCleanup /ResetBase)'
    Write-Line 'пропущено (-SkipCleanup)'
} else {
    Invoke-Step 'Очистка хранилища компонентов (DISM /StartComponentCleanup /ResetBase)' {
        Write-Host '    ДОЛГО: от 10 минут до часа и больше, проценты подолгу стоят на месте. Не закрывайте окно и не выключайте ПК.' -ForegroundColor Yellow
        Write-Host '    После /ResetBase установленные обновления удалить нельзя — так и задумано для образа.' -ForegroundColor Yellow
        $dism = Join-Path $env:SystemRoot 'System32\dism.exe'
        Write-Line "> $dism /Online /Cleanup-Image /StartComponentCleanup /ResetBase"
        # Вывод DISM — прямо в консоль (проценты), код — из процесса.
        $p = Start-Process -FilePath $dism -ArgumentList '/Online', '/Cleanup-Image', '/StartComponentCleanup', '/ResetBase' -NoNewWindow -PassThru
        $null = $p.Handle
        $p.WaitForExit()
        if ($p.ExitCode -eq 3010) { Write-Line 'DISM: готово, нужна перезагрузка (код 3010)' }
        elseif ($p.ExitCode -ne 0) { throw ('dism завершился с кодом {0}; журнал — {1}\Logs\DISM\dism.log; ожидает перезагрузки — перезагрузить и запустить снова' -f $p.ExitCode, $env:SystemRoot) }
    }
}

# ======================================================================================================================
# 6. Проверка
# ======================================================================================================================
Write-Host ''
Write-Host '============================== ПРОВЕРКА ==============================' -ForegroundColor Cyan

# Windows и диск
$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Write-Info ('Windows: {0}, {1}, сборка {2}.{3}; имя ПК {4}' -f (Get-RegValue $cv 'EditionID'), (Get-RegValue $cv 'DisplayVersion'),
    (Get-RegValue $cv 'CurrentBuild'), (Get-RegValue $cv 'UBR'), $env:COMPUTERNAME)
Test-Check ('системный диск локальный (шина {0})' -f $busType) { $LocalBusTypes -contains $busType } 'скрипт — только для Windows на локальном SSD'
if ($systemDisk) {
    Test-Check ('разметка системного диска GPT ({0})' -f $systemDisk.PartitionStyle) { "$($systemDisk.PartitionStyle)" -eq 'GPT' } 'перезаливка — только UEFI/GPT'
}

# Secure Boot и TPM
$secureBoot = $null
try { $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop } catch { }
if ($secureBoot -eq $true) { Write-Pass 'Secure Boot включён' }
elseif ($secureBoot -eq $false) { Write-Warn 'Secure Boot выключен' 'BIOS: Boot -> Secure Boot: Enabled, Standard; статус «Not Active» — восстановить заводские ключи' }
else { Write-Warn 'Secure Boot не прочитался (загрузка не UEFI?)' 'BIOS: Boot -> CSM Support: Disabled, Secure Boot: Enabled' }
try {
    $tpm = Get-Tpm -ErrorAction Stop
    Test-Check ('TPM: есть {0}, готов {1}' -f (Format-Bool $tpm.TpmPresent), (Format-Bool $tpm.TpmReady)) { $tpm.TpmPresent -and $tpm.TpmReady } 'BIOS: Settings -> Miscellaneous -> Intel PTT: Enabled (AMD: fTPM); затем tpm.msc'
} catch {
    Write-Warn ('TPM не прочитался: {0}' -f $_.Exception.Message) 'BIOS: Intel PTT (AMD: fTPM) — Enabled'
}

# VBS и целостность памяти
Test-RegValue $DeviceGuard 'EnableVirtualizationBasedSecurity' 1
Test-RegValue $HvciPath 'Enabled' 1
try {
    $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName 'Win32_DeviceGuard' -ErrorAction Stop
    $vbsRunning = ([int]$dg.VirtualizationBasedSecurityStatus -eq 2)
    $hvciRunning = (@($dg.SecurityServicesRunning | ForEach-Object { [int]$_ }) -contains 2)
    $properties = @($dg.AvailableSecurityProperties | ForEach-Object { [int]$_ })
    Test-Check 'VBS работает' { $vbsRunning } 'сначала перезагрузить ПК после этого скрипта; если и после неё нет — BIOS: Intel Virtualization Technology (VT-x) и VT-d — Enabled'
    Test-Check 'целостность памяти (HVCI) работает' { $hvciRunning } 'сначала перезагрузить ПК; если не включается — «Безопасность Windows» -> «Безопасность устройства» -> «Изоляция ядра»: там несовместимые драйверы'
    Write-Info ('поддержка гипервизора: {0}; защита DMA (IOMMU) для ядра: {1}' -f (Format-Bool ($properties -contains 1)), (Format-Bool ($properties -contains 3)))
} catch {
    Write-Warn 'состояние VBS/HVCI не прочиталось' $_.Exception.Message
}

# Электропитание, Windows Update, шифрование
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled' 0 'powercfg /hibernate off'
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
Test-RegValue "$WuPolicy\AU" 'NoAutoUpdate' 1
Test-RegValue $WuPolicy 'ExcludeWUDriversInQualityUpdate' 1
Test-RegValue $WuPolicy 'TargetReleaseVersion' 1
Test-RegValue $WuPolicy 'ProductVersion' 'Windows 11'
Test-RegValue $WuPolicy 'TargetReleaseVersionInfo' $TargetRelease
Test-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching' 'SearchOrderConfig' 0
Test-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 1

# BitLocker / шифрование устройства: все тома — Fully Decrypted (sysprep с включённым BitLocker не проходит).
$volumes = $null
try { $volumes = @(Get-BitLockerVolume -ErrorAction Stop) } catch { }
if ($volumes) {
    foreach ($v in $volumes) {
        $text = 'BitLocker {0}: {1}, защита {2}' -f $v.MountPoint, $v.VolumeStatus, $v.ProtectionStatus
        if ("$($v.VolumeStatus)" -eq 'FullyDecrypted') { Write-Pass $text }
        else { Write-Warn $text ('manage-bde -off {0} и дождаться «Fully Decrypted» (manage-bde -status)' -f $v.MountPoint) }
    }
} else {
    $status = Get-NativeOutput 'manage-bde.exe' @('-status', $env:SystemDrive)
    Test-Check ('BitLocker {0}: полностью расшифрован (manage-bde)' -f $env:SystemDrive) { $status -match 'Fully Decrypted|Полностью расшифрован' } ('manage-bde -status {0}; если шифруется — manage-bde -off {0}' -f $env:SystemDrive)
}

# Обслуживание, TRIM, файл подкачки
Test-RegValue $Maintenance 'MaintenanceDisabled' 1 'этот скрипт ещё раз'
Test-Check 'задача ScheduledDefrag выключена (оптимизировала бы и диски игр)' { $task = Get-DefragTask; -not $task -or "$($task.State)" -eq 'Disabled' } 'Disable-ScheduledTask -TaskPath \Microsoft\Windows\Defrag\ -TaskName ScheduledDefrag'
Test-Check ('задача {0}{1} включена: defrag {2}' -f $RetrimTaskPath, $RetrimTaskName, $RetrimArguments) {
    $task = Get-RetrimTask
    $task -and "$($task.State)" -ne 'Disabled' -and @($task.Actions).Count -eq 1 -and "$(@($task.Actions)[0].Arguments)".Trim() -eq $RetrimArguments
} 'этот скрипт ещё раз'
# Как Windows видит диски игр (для ScheduledDefrag: тонкий SAN — SlabConsolidate, HDD — дефрагментация).
foreach ($d in @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { (Get-BusName $_.BusType) -eq 'iSCSI' })) {
    $media = (Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { "$($_.DeviceId)" -eq "$($d.Number)" } | Select-Object -First 1).MediaType
    Write-Info ('диск iSCSI {0}: {1}, {2:N0} ГБ, носитель {3}, выделение {4}' -f $d.Number, $d.FriendlyName, ($d.Size / 1GB), (Format-Value $media), (Format-Value $d.ProvisioningType))
}
$trim = Get-NtfsTrimDisabled
Test-Check ('TRIM в NTFS включён (DisableDeleteNotify = {0})' -f (Format-Value $trim)) { $trim -eq 0 } 'fsutil behavior set DisableDeleteNotify NTFS 0'
$paging = @(Get-RegValue $MemoryMgmt 'PagingFiles' | Where-Object { "$_".Trim() })
Test-Check ('файл подкачки по выбору системы на {0} ({1})' -f $env:SystemDrive, (Format-Value ($paging -join '; '))) {
    $paging.Count -eq 1 -and ("$($paging[0])" -eq $PageFileSpec -or "$($paging[0])" -eq '?:\pagefile.sys')
} ('этот скрипт; или «Система -> Дополнительные параметры -> Быстродействие -> Виртуальная память»: {0} — размер по выбору системы' -f $env:SystemDrive)
try {
    $usage = @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop)
    if ($usage) { foreach ($u in $usage) { Write-Info ('файл подкачки сейчас: {0}, {1} МБ' -f $u.Name, $u.AllocatedBaseSize) } }
    else { Write-Info 'файла подкачки сейчас нет (появится после перезагрузки)' }
} catch { }

# Загрузочные драйверы
foreach ($name in $StorageServices) {
    Test-RegValue "$Services\$name" 'Start' 0 'этот скрипт ещё раз; без загрузки контроллера при старте — синий экран 0x7B'
    Test-Check ('{0}: нет подключа StartOverride' -f $name) { -not (Test-Path -LiteralPath "$Services\$name\StartOverride") } 'StartOverride подменяет Start при загрузке — удалить подключ'
}
foreach ($name in Get-NetBootServiceNames) {
    $start = Get-RegValue "$Services\$name" 'Start'
    $flags = Get-RegValue "$Services\$name" 'BootFlags'
    $text = '{0}: Start = {1}, BootFlags = {2}' -f $name, (Format-Value $start), (Format-Value $flags)
    if ("$start" -ne '0' -and "$start" -ne '1' -and $null -eq $flags) { Write-Pass "$text — не грузится при старте" }
    else { Write-Warn "$text — нужно Start 3, без BootFlags" 'этот скрипт ещё раз' }
}

# IQN инициатора iSCSI: по нему сервер пускает ПК к личному диску игр (SeatGames). Заданный вручную или взятый из iBFT места
# IQN попал бы в образ — у всех гибридов был бы один IQN.
$iqn = Get-InitiatorIqn
if (-not $iqn) {
    Write-Warn 'IQN инициатора iSCSI не прочитался (Get-InitiatorPort)' 'служба MSiSCSI: Start-Service MSiSCSI; затем этот скрипт ещё раз'
} elseif (Test-DefaultIqn $iqn) {
    Write-Pass ('IQN инициатора по умолчанию: {0}' -f $iqn)
} else {
    # Эталон из бездиска несёт IQN мастера из iBFT (iqn.2026-10.local.club:master, PC-11 2026-10-24): Windows записала его
    # при загрузке по iSCSI. «*» возвращает имя по умолчанию (из имени компьютера; после sysprep — из нового имени).
    # Помощник читает IQN один раз за запуск — перезапуск, чтобы сервер получил новый (личный диск G: пересоздастся).
    Write-Line ('IQN инициатора не по умолчанию: {0} — возвращаю по умолчанию' -f $iqn)
    & iscsicli.exe NodeName '*' | Out-Null
    $iqn = Get-InitiatorIqn
    if ($iqn -and (Test-DefaultIqn $iqn)) {
        Write-Pass ('IQN инициатора теперь по умолчанию: {0}' -f $iqn)
        Restart-Service -Name $HelperService -ErrorAction SilentlyContinue
    } else {
        Write-Warn ('IQN инициатора не вернулся к умолчанию: {0} (ожидается {1}{2})' -f $iqn, $DefaultIqnPrefix, "$env:COMPUTERNAME".ToLowerInvariant()) 'iscsicpl -> «Конфигурация» -> «Изменить» -> «Сбросить», затем этот скрипт ещё раз'
    }
}

# Помощник бездиска и диск игр G:
$helper = Get-Service -Name $HelperService -ErrorAction SilentlyContinue
Test-Check 'служба ClubDisklessHelper установлена, запуск — автоматически' { $helper -and "$($helper.StartType)" -eq 'Automatic' } 'install-helper.ps1'
Test-Check 'служба ClubDisklessHelper работает' { $helper -and "$($helper.Status)" -eq 'Running' } 'Start-Service ClubDisklessHelper; сообщения — журнал «Приложение»'
if (Test-Path -LiteralPath (Join-Path $HelperStateDir 'credentials.bin')) { Write-Info 'помощник зарегистрирован на сервере (credentials.bin; seal-hybrid.ps1 его удалит)' }
Test-Check 'диск игр G: подключён и читается' {
    $null = Get-Volume -DriveLetter G -ErrorAction Stop
    $null = Get-ChildItem -LiteralPath 'G:\' -ErrorAction Stop | Select-Object -First 1
    $true
} 'панель: ПК с режимом загрузки local, версия библиотеки опубликована; помощник проверяет каждые 30 с — подождать 1–2 мин'
try {
    $gVolume = Get-Volume -DriveLetter G -ErrorAction Stop
    Write-Info ('G: метка {0}, свободно {1:N0} ГБ из {2:N0} ГБ' -f $gVolume.FileSystemLabel, ($gVolume.SizeRemaining / 1GB), ($gVolume.Size / 1GB))
} catch { }
Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match ':games-' } |
    ForEach-Object { Write-Info ('сессия игр: {0}, подключена: {1}' -f $_.TargetNodeAddress, (Format-Bool $_.IsConnected)) }

# Диск офлайн-игр H: (помощник 1.6.0, библиотека H: на сервере) — сведения: его нет, пока библиотека H: не включена или
# не опубликована. Буква H: у другого тома (второй диск, картридер, DVD, флешка) — WARN: помощник не подключит H: (в
# панели у ПК H: «ошибка», drive letter H is in use).
$hVolume = Get-Volume -DriveLetter H -ErrorAction SilentlyContinue
if (-not $hVolume) {
    Write-Info 'буква H: свободна (диск офлайн-игр H: подключится, когда библиотека H: опубликована)'
} else {
    $hDisk = $null
    try { $hDisk = Get-Partition -DriveLetter H -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch { }
    if ($hDisk -and (Get-BusName $hDisk.BusType) -eq 'iSCSI') {
        Write-Pass ('диск офлайн-игр H: подключён: метка {0}, свободно {1:N0} ГБ из {2:N0} ГБ' -f $hVolume.FileSystemLabel, ($hVolume.SizeRemaining / 1GB), ($hVolume.Size / 1GB))
    } else {
        $bus = if ($hDisk) { Get-BusName $hDisk.BusType } else { '?' }
        Write-Warn ('буква H: занята другим томом: {0}, шина {1}, метка «{2}»' -f $hVolume.DriveType, $bus, $hVolume.FileSystemLabel) '«Управление дисками» -> «Изменить букву диска» на другую (не G: и не H:): иначе помощник не подключит диск офлайн-игр H:'
    }
}
Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { "$($_.TargetNodeAddress)" -match ':solo-' } |
    ForEach-Object { Write-Info ('сессия H: {0}, подключена: {1}' -f $_.TargetNodeAddress, (Format-Bool $_.IsConnected)) }

# NGEN: запуск PowerShell
$psStart = @(1..3 | ForEach-Object { (Measure-Command { & powershell.exe -NoProfile -NonInteractive -Command 'exit' }).TotalSeconds } | Measure-Object -Minimum).Minimum
Test-Check ('запуск PowerShell {0:N1} с' -f $psStart) { $psStart -le 1.5 } 'NGEN не достроен? Должно быть меньше 1,5 с — этот скрипт ещё раз'

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
if ($script:WarnCount -gt 0) { Write-Host ('Предупреждений (WARN): {0} — разобрать до seal-hybrid.ps1.' -f $script:WarnCount) -ForegroundColor Yellow }
else { Write-Host 'Предупреждений нет.' -ForegroundColor Green }
Write-Host ''
Write-Host 'Дальше: перезагрузить ПК (VBS, HVCI и файл подкачки вступят в силу) и запустить этот скрипт ещё раз с -SkipCleanup —'
Write-Host 'нужно 0 WARN. Проверить: G: подключён, лаунчеры видят игры, CS2 запускается. Затем seal-hybrid.ps1 (sysprep).'

if ($script:FailedSteps.Count -gt 0) { exit 2 }
exit 0
