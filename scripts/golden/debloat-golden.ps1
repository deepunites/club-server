#Requires -Version 5.1
<#
.SYNOPSIS
    Убирает из эталона Windows 11 (полный бездиск) лишнее для игрового клуба: рекламу, телеметрию, Copilot, виджеты,
    OneDrive, предустановленные приложения; ставит игровые настройки.

.DESCRIPTION
    Запускать ТОЛЬКО внутри эталона в режиме мастера, PowerShell от администратора, ПОСЛЕ prepare-golden.ps1.
    Повторный запуск безопасен; после крупного обновления Windows (оно возвращает часть приложений) — запустить ещё раз.
    Ошибка одного шага или значения не останавливает остальные. Настройки пользователя пишутся и текущему пользователю,
    и в профиль по умолчанию (C:\Users\Default) — их получат новые учётные записи (игроки, шелл).

    НЕ трогает (иначе бездиск или игры ломаются):
      - сетевую карту, её драйвер и привязки; Hyper-V/WSL/Sandbox/VPN не включает — фильтры NDIS на загрузочной карте
        дают синий экран (docs/diskless-pilot.md);
      - Microsoft Store, App Installer (winget), Xbox, Gaming Services, Game Bar, VCLibs/.NET/UI.Xaml, WebView2, Edge,
        кодеки, калькулятор, блокнот, фото, терминал, ножницы, Paint;
      - Защитник Windows и Центр обновления (их настраивает prepare-golden.ps1).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File $env:TEMP\debloat-golden.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

# Предустановленные приложения (Appx): удаляются у всех пользователей и из образа (provisioned), чтобы не вернулись
# новым учётным записям. Шаблоны — по имени пакета.
$RemoveApps = @(
    'Clipchamp.Clipchamp', 'Microsoft.549981C3F5F10', 'Microsoft.BingNews', 'Microsoft.BingSearch', 'Microsoft.BingWeather',
    'Microsoft.BingFinance', 'Microsoft.BingSports', 'Microsoft.BingTranslator', 'Microsoft.News', 'Microsoft.Copilot',
    'Microsoft.GetHelp', 'Microsoft.Getstarted', 'Microsoft.MicrosoftJournal', 'Microsoft.MicrosoftOfficeHub',
    'Microsoft.MicrosoftSolitaireCollection', 'Microsoft.MicrosoftStickyNotes', 'Microsoft.MixedReality.Portal',
    'Microsoft.Office.OneNote', 'Microsoft.OutlookForWindows', 'microsoft.windowscommunicationsapps', 'Microsoft.People',
    'Microsoft.PowerAutomateDesktop', 'Microsoft.SkypeApp', 'Microsoft.Messaging', 'Microsoft.OneConnect', 'Microsoft.Todos',
    'Microsoft.Wallet', 'Microsoft.Windows.DevHome', 'Microsoft.WindowsAlarms', 'Microsoft.WindowsCamera',
    'Microsoft.WindowsFeedbackHub', 'Microsoft.WindowsMaps', 'Microsoft.WindowsSoundRecorder', 'Microsoft.YourPhone',
    'Microsoft.ZuneMusic', 'Microsoft.ZuneVideo', 'Microsoft.Print3D', 'Microsoft.3DBuilder', 'Microsoft.Microsoft3DViewer',
    'MicrosoftCorporationII.MicrosoftFamily', 'MicrosoftCorporationII.QuickAssist', 'MicrosoftTeams', 'MSTeams',
    'MicrosoftWindows.Client.WebExperience', 'MicrosoftWindows.CrossDevice',
    '*Disney*', '*Spotify*', '*Facebook*', '*Instagram*', '*TikTok*', '*CandyCrush*', '*LinkedIn*', '*Netflix*', '*Twitter*',
    '*AmazonPrimeVideo*', '*king.com*', '*Duolingo*', '*PicsArt*', '*Hulu*'
)

# Защита от ошибки в шаблонах: эти пакеты не удаляются никогда.
$KeepApps = @(
    'Microsoft.WindowsStore', 'Microsoft.StorePurchaseApp', 'Microsoft.DesktopAppInstaller', 'Microsoft.Xbox*', 'Microsoft.GamingApp',
    'Microsoft.GamingServices', 'Microsoft.Edge*', 'Microsoft.VCLibs*', 'Microsoft.UI.Xaml*', 'Microsoft.NET.*',
    'Microsoft.WindowsAppRuntime*', 'Microsoft.WebView2*', 'Microsoft.WindowsCalculator', 'Microsoft.WindowsNotepad',
    'Microsoft.Windows.Photos', 'Microsoft.WindowsTerminal', 'Microsoft.ScreenSketch', 'Microsoft.Paint', 'Microsoft.SecHealthUI',
    'Microsoft.*Extension', 'Microsoft.Windows.ShellExperienceHost', 'Microsoft.Windows.StartMenuExperienceHost',
    'MicrosoftWindows.Client.CBS', 'MicrosoftWindows.Client.Core'
)

$DisableServices = @(
    'DiagTrack',          # телеметрия
    'dmwappushservice',   # доставка WAP-сообщений телеметрии
    'SysMain',            # предвыборка: на сетевом диске лишняя запись, а кэш всё равно пропадает при сбросе места
    'WerSvc',             # отчёты об ошибках
    'MapsBroker',         # карты
    'RetailDemo',         # демонстрационный режим
    'PcaSvc',             # помощник совместимости программ
    'lfsvc',              # геолокация
    'Fax'
)

$DisableTasks = @(
    '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser',
    '\Microsoft\Windows\Application Experience\ProgramDataUpdater',
    '\Microsoft\Windows\Application Experience\StartupAppTask',
    '\Microsoft\Windows\Application Experience\PcaPatchDbTask',
    '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator',
    '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip',
    '\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector',
    '\Microsoft\Windows\Feedback\Siuf\DmClient',
    '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload',
    '\Microsoft\Windows\Windows Error Reporting\QueueReporting',
    '\Microsoft\Windows\Maps\MapsUpdateTask',
    '\Microsoft\Windows\Maps\MapsToastTask'
    # \Microsoft\Windows\CloudExperienceHost\CreateObjectTask НЕ отключать: без него OOBE после sysprep не создаёт
    # учётную запись («Возникла проблема», гибрид PC-11 2026-10-24); seal-hybrid.ps1 включает его обратно.
)

# --- Вывод ------------------------------------------------------------------------------------------------------------
$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

# Шаг: свой try/catch — ошибка одного шага не останавливает остальные.
function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Title $Title
    try { & $Action } catch { Write-Warn ('{0}: {1}' -f $Title, $_.Exception.Message) }
}

# --- Реестр -----------------------------------------------------------------------------------------------------------
# Создаёт только отсутствующие ключи (по цепочке родителей): New-Item -Force на существующем ключе может стереть его значения.
function New-RegKey([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return }
    New-RegKey (Split-Path -Path $Path -Parent)
    New-Item -Path $Path -ErrorAction Stop | Out-Null
}

# Ошибка одного значения — WARN, остальные значения шага пишутся дальше (часть ключей Explorer Windows защищает от записи).
function Set-RegValue([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord') {
    try {
        New-RegKey $Path
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force -ErrorAction Stop | Out-Null
    } catch { Write-Warn ('{0} : {1} — {2}' -f $Path, $Name, $_.Exception.Message) }
}

# Настройки пользователя: текущий пользователь (HKCU) и профиль по умолчанию (для новых учётных записей).
$UserRoots = @('HKCU:')
function Set-UserValue([string]$SubPath, [string]$Name, $Value, [string]$Type = 'DWord') {
    foreach ($root in $UserRoots) { Set-RegValue "$root\$SubPath" $Name $Value $Type }
}

function Test-Pattern([string]$Name, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Name -like $p) { return $true } }
    return $false
}
function Test-Removable([string]$Name) { (Test-Pattern $Name $RemoveApps) -and -not (Test-Pattern $Name $KeepApps) }

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

$DefaultHiveFile = "$env:SystemDrive\Users\Default\NTUSER.DAT"
$defaultLoaded = $false
if (Test-Path -LiteralPath $DefaultHiveFile) {
    & reg.exe load 'HKU\ClubDefault' $DefaultHiveFile | Out-Null
    if ($LASTEXITCODE -eq 0) { $UserRoots += 'Registry::HKEY_USERS\ClubDefault'; $defaultLoaded = $true }
    else { Write-Warn 'профиль по умолчанию не загружен: новые учётные записи получат настройки Windows по умолчанию' }
}

try {

Invoke-Step 'Предустановленные приложения: удаление (у всех пользователей и из образа)' {
    foreach ($pkg in @(Get-AppxPackage -AllUsers | Where-Object { (Test-Removable $_.Name) -and -not $_.NonRemovable })) {
        try { Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop; Write-Pass "удалено: $($pkg.Name)" }
        catch { Write-Warn "не удалено: $($pkg.Name) ($($_.Exception.Message))" }
    }
    foreach ($prov in @(Get-AppxProvisionedPackage -Online | Where-Object { Test-Removable $_.DisplayName })) {
        try { Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction Stop | Out-Null; Write-Pass "из образа: $($prov.DisplayName)" }
        catch { Write-Warn "из образа не удалено: $($prov.DisplayName) ($($_.Exception.Message))" }
    }
}

Invoke-Step 'OneDrive: удаление и запрет' {
    Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $setups = @("$env:SystemRoot\System32\OneDriveSetup.exe", "$env:SystemRoot\SysWOW64\OneDriveSetup.exe") +
        @(Get-ChildItem "$env:ProgramFiles\Microsoft OneDrive\*\OneDriveSetup.exe", "${env:ProgramFiles(x86)}\Microsoft OneDrive\*\OneDriveSetup.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName)
    foreach ($setup in $setups | Where-Object { Test-Path -LiteralPath $_ }) {
        $setupArgs = if ($setup -like "$env:SystemRoot\*") { '/uninstall' } else { '/uninstall /allusers' }
        $proc = Start-Process $setup -ArgumentList $setupArgs -PassThru -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.WaitForExit(180000)) { Write-Warn "$setup $setupArgs не завершился за 3 минуты" }
    }
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive' 'DisableFileSyncNGSC' 1
    foreach ($root in $UserRoots) { Remove-ItemProperty -LiteralPath "$root\Software\Microsoft\Windows\CurrentVersion\Run" -Name 'OneDriveSetup' -ErrorAction SilentlyContinue }
    Write-Pass 'OneDrive удалён и запрещён'
}

Invoke-Step 'Телеметрия, реклама, Copilot, виджеты, веб-поиск (политики компьютера)' {
    $p = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
    Set-RegValue "$p\DataCollection" 'AllowTelemetry' 1                 # минимум, допустимый для Pro
    Set-RegValue "$p\DataCollection" 'DoNotShowFeedbackNotifications' 1
    Set-RegValue "$p\DataCollection" 'AllowDeviceNameInTelemetry' 0
    Set-RegValue "$p\CloudContent" 'DisableWindowsConsumerFeatures' 1   # не ставить «рекомендованные» игры и приложения
    Set-RegValue "$p\CloudContent" 'DisableSoftLanding' 1
    Set-RegValue "$p\CloudContent" 'DisableCloudOptimizedContent' 1
    Set-RegValue "$p\CloudContent" 'DisableConsumerAccountStateContent' 1
    Set-RegValue "$p\AdvertisingInfo" 'DisabledByGroupPolicy' 1
    Set-RegValue "$p\System" 'EnableActivityFeed' 0
    Set-RegValue "$p\System" 'PublishUserActivities' 0
    Set-RegValue "$p\System" 'UploadUserActivities' 0
    Set-RegValue "$p\Windows Error Reporting" 'Disabled' 1
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'Disabled' 1
    Set-RegValue "$p\Windows Search" 'AllowCortana' 0
    Set-RegValue "$p\Windows Search" 'DisableWebSearch' 1
    Set-RegValue "$p\Windows Search" 'ConnectedSearchUseWeb' 0
    Set-RegValue "$p\Explorer" 'DisableSearchBoxSuggestions' 1
    Set-RegValue "$p\WindowsCopilot" 'TurnOffWindowsCopilot' 1
    Set-RegValue "$p\WindowsAI" 'DisableAIDataAnalysis' 1               # Recall
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0   # виджеты
    Set-RegValue "$p\Windows Chat" 'ChatIcon' 3
    Set-RegValue "$p\GameDVR" 'AllowGameDVR' 0                          # фоновая запись игр (сам Game Bar остаётся)
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload' 2   # без автообновления приложений Store на местах
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'StartupBoostEnabled' 0
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'BackgroundModeEnabled' 0
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'HideFirstRunExperience' 1
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' 'HubsSidebarEnabled' 0
    Write-Pass 'политики записаны'
}

Invoke-Step 'Службы: телеметрия, предвыборка, отчёты, карты — отключены' {
    foreach ($name in $DisableServices) {
        if (-not (Get-Service -Name $name -ErrorAction SilentlyContinue)) { continue }
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        Set-Service -Name $name -StartupType Disabled -ErrorAction SilentlyContinue
        if ((Get-Service -Name $name).StartType -eq 'Disabled') { Write-Pass "служба $name отключена" } else { Write-Warn "служба $name не отключена" }
    }
}

Invoke-Step 'Задания планировщика: телеметрия и совместимость — отключены' {
    foreach ($task in $DisableTasks) {
        $cut = $task.LastIndexOf('\') + 1
        $t = Get-ScheduledTask -TaskPath $task.Substring(0, $cut) -TaskName $task.Substring($cut) -ErrorAction SilentlyContinue
        if (-not $t) { continue }
        try { $t | Disable-ScheduledTask -ErrorAction Stop | Out-Null; Write-Pass "задание отключено: $task" }
        catch { Write-Warn "задание не отключено: $task ($($_.Exception.Message))" }
    }
}

Invoke-Step 'Пользователь: без рекламы и подсказок, панель задач, проводник, мышь, игровой режим' {
    $cdm = 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
    foreach ($v in 'ContentDeliveryAllowed', 'FeatureManagementEnabled', 'OemPreInstalledAppsEnabled', 'PreInstalledAppsEnabled',
        'PreInstalledAppsEverEnabled', 'SilentInstalledAppsEnabled', 'SoftLandingEnabled', 'SystemPaneSuggestionsEnabled',
        'RotatingLockScreenEnabled', 'RotatingLockScreenOverlayEnabled', 'SubscribedContentEnabled',
        'SubscribedContent-310093Enabled', 'SubscribedContent-338387Enabled', 'SubscribedContent-338388Enabled',
        'SubscribedContent-338389Enabled', 'SubscribedContent-338393Enabled', 'SubscribedContent-353694Enabled',
        'SubscribedContent-353696Enabled', 'SubscribedContent-353698Enabled') {
        Set-UserValue $cdm $v 0
    }
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo' 'Enabled' 0
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\Privacy' 'TailoredExperiencesWithDiagnosticDataEnabled' 0
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0   # «завершите настройку»
    Set-UserValue 'Software\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1
    Set-UserValue 'Software\Policies\Microsoft\Windows\Explorer' 'DisableSearchBoxSuggestions' 1
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\Search' 'SearchboxTaskbarMode' 1   # поиск — значок
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\Search' 'BingSearchEnabled' 0
    $adv = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    Set-UserValue $adv 'ShowCopilotButton' 0
    Set-UserValue $adv 'TaskbarMn' 0                 # чат
    Set-UserValue $adv 'Start_IrisRecommendations' 0
    Set-UserValue $adv 'Start_AccountNotifications' 0
    Set-UserValue $adv 'HideFileExt' 0               # расширения файлов видны
    Set-UserValue $adv 'LaunchTo' 1                  # проводник открывается на «Этот компьютер»
    Set-UserValue $adv 'TaskbarDa' 0                 # виджеты (Windows может запретить запись — их и так выключает политика)
    Set-UserValue 'Control Panel\Mouse' 'MouseSpeed' '0' 'String'      # без «повышенной точности указателя»
    Set-UserValue 'Control Panel\Mouse' 'MouseThreshold1' '0' 'String'
    Set-UserValue 'Control Panel\Mouse' 'MouseThreshold2' '0' 'String'
    Set-UserValue 'Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1
    Set-UserValue 'Software\Microsoft\GameBar' 'AllowAutoGameMode' 1
    Set-UserValue 'System\GameConfigStore' 'GameDVR_Enabled' 0
    Set-UserValue 'Software\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
    Write-Pass ('пользовательские настройки: {0}' -f ($UserRoots -join ', '))
}

# Новая схема не наследует настройки старой — сон и ASPM (prepare-golden.ps1) повторяются в ней.
Invoke-Step 'Электропитание: высокая производительность (сон — никогда, PCIe ASPM выключен)' {
    $ok = $true
    foreach ($a in @('/setactive', 'SCHEME_MIN'), @('/change', 'standby-timeout-ac', '0'), @('/change', 'standby-timeout-dc', '0'),
        @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0'), @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_PCIEXPRESS', 'ASPM', '0'),
        @('/setactive', 'SCHEME_CURRENT')) {
        & powercfg.exe @a
        if ($LASTEXITCODE -ne 0) { $ok = $false; Write-Warn "powercfg $($a -join ' ') — код $LASTEXITCODE" }
    }
    if ($ok) { Write-Pass 'схема «Высокая производительность» активна' }
}

} finally {
    if ($defaultLoaded) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload 'HKU\ClubDefault' | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warn 'профиль по умолчанию не выгружен (выгрузится при перезагрузке)' }
    }
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений. Перезагрузите ПК.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше). Перезагрузите ПК.' -f $script:WarnCount) -ForegroundColor Yellow }
