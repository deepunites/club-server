#Requires -Version 5.1
<#
.SYNOPSIS
    Панель управления NVIDIA на гибриде после перезаливки: ставит её заново для всех пользователей, с лицензией Store.

.DESCRIPTION
    Лежит в образе гибрида в C:\Windows\Setup\Scripts\nvcp вместе с пакетом панели и его лицензией (драйвер 617.42,
    Display.Driver\NVCPL; NVIDIA с 610.47 панель в драйвер GeForce больше не кладёт — пакет и лицензия замороженной
    парой). Без него после перезаливки панели нет (PC-11 и PC-21, 2026-10-24): в образе она предустановлена
    (provisioned), но при первом входе Club Windows её не регистрирует (AppXDeploymentServer 809, «ограничения политики
    загрузки неопубликованных приложений»), AppReadiness снимает её с предустановленных, а с удалением профиля
    defaultuser0 пропадает и сам пакет. Чем панель отличается от уцелевших пакетов образа: sysprep /generalize стёр
    лицензии Store (ClipSVC, копия осталась только в ClipSVC\Archive), а в её записи provisioned нет Regions (без них DISM
    оставляет пакет, только если он закреплён в Пуске; у Realtek Audio Control Regions=all, у кодеков нет плитки в Пуске).
    Скрипт снимает обе причины.

    -Mode System — из SetupComplete.cmd (SYSTEM, конец установки Windows, до первого входа): Add-AppxProvisionedPackage
      с лицензией и -Regions all; если запись provisioned из образа осталась без Regions — дописывает Regions=all.
    -Mode User — из FirstLogonCommands при первом входе Club (отдельным процессом, вход его не ждёт): страховка, если
      SetupComplete.cmd не выполнился (Windows пропускает его при OEM-ключе). 25 минут раз в минуту: если у текущего
      пользователя панели нет — то же, что в System, и Add-AppxPackage (так панель вернули вручную на PC-11 и PC-21).
    Каждый шаг пишется в nvcp.log рядом со скриптом (UTF-8).
#>
param([ValidateSet('System', 'User')][string]$Mode = 'System')

$ErrorActionPreference = 'Stop'
$name = 'NVIDIACorp.NVIDIAControlPanel'
$appx = Join-Path $PSScriptRoot '7e010d7e2de54a439dbd340dd1f61212.appx'
$license = Join-Path $PSScriptRoot '7e010d7e2de54a439dbd340dd1f61212_License1.xml'
$store = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore'
$log = Join-Path $PSScriptRoot 'nvcp.log'

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1} {2}] {3}' -f (Get-Date), $Mode, $env:USERNAME, $Message
    try { [System.IO.File]::AppendAllText($log, "$line`r`n", [System.Text.Encoding]::UTF8) }
    catch { try { [System.IO.File]::AppendAllText((Join-Path $env:TEMP 'nvcp.log'), "$line`r`n", [System.Text.Encoding]::UTF8) } catch { } }
}

# Записи provisioned панели (ключи AppxAllUserStore\Applications) с их Regions.
function Get-ProvisionedKeys {
    @(Get-ChildItem -LiteralPath "$store\Applications" | Where-Object PSChildName -like "${name}_*")
}

function Write-State([string]$When) {
    $parts = @()
    try {
        $parts += 'provisioned=' + (@(Get-ProvisionedKeys | ForEach-Object {
            '{0} (Regions={1})' -f $_.PSChildName, (Get-ItemProperty -LiteralPath $_.PSPath).Regions }) -join ', ')
    } catch { $parts += "provisioned=? ($($_.Exception.Message))" }
    try {
        $parts += 'users=' + (@(Get-AppxPackage -AllUsers -Name $name | ForEach-Object { $_.PackageUserInformation } |
            ForEach-Object { '{0}:{1}' -f $_.UserSecurityId.Username, $_.InstallState }) -join ', ')
    } catch { $parts += "users=? ($($_.Exception.Message))" }
    foreach ($dir in 'Install', 'Archive') {
        try { $parts += "ClipSVC\$dir\Apps=" + @(Get-ChildItem -LiteralPath "$env:ProgramData\Microsoft\Windows\ClipSVC\$dir\Apps").Count }
        catch { $parts += "ClipSVC\$dir\Apps=?" }
    }
    Write-Log "${When}: $($parts -join '; ')"
}

function Install-Provisioned {
    try {
        Add-AppxProvisionedPackage -Online -PackagePath $appx -LicensePath $license -Regions 'all' | Out-Null
        Write-Log 'Add-AppxProvisionedPackage (лицензия, Regions all): OK'
    } catch { Write-Log "Add-AppxProvisionedPackage: ОШИБКА $($_.Exception.Message)" }
    # Повторный provisioned поверх записи из образа может оставить её без Regions — дописать, как у Realtek Audio Control.
    try {
        foreach ($key in Get-ProvisionedKeys) {
            if ((Get-ItemProperty -LiteralPath $key.PSPath).Regions -ne 'all') {
                New-ItemProperty -LiteralPath $key.PSPath -Name 'Regions' -Value 'all' -PropertyType String -Force | Out-Null
                Write-Log "Regions=all дописан: $($key.PSChildName)"
            }
        }
    } catch { Write-Log "Regions не дописан: $($_.Exception.Message)" }
}

try {
    Write-Log "== запуск, PowerShell $($PSVersionTable.PSVersion)"
    # Видеокарта NVIDIA (PCI VEN_10DE): из WMI, а если WMI не ответил — из перечня устройств PCI в реестре.
    try {
        $gpu = @(Get-CimInstance Win32_VideoController | Where-Object { "$($_.PNPDeviceID)" -match 'VEN_10DE' } |
            ForEach-Object { "$($_.Name), драйвер $($_.DriverVersion)" })
    } catch {
        Write-Log "WMI: ОШИБКА $($_.Exception.Message) — видеокарту ищу в реестре"
        $gpu = @(Get-ChildItem -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI' -ErrorAction SilentlyContinue |
            Where-Object PSChildName -match 'VEN_10DE&DEV_' | ForEach-Object PSChildName)
    }
    if (-not $gpu) { Write-Log 'видеокарты NVIDIA нет — панель не нужна'; exit 0 }
    Write-Log "видеокарта: $($gpu[0])"
    if (-not (Test-Path -LiteralPath $appx) -or -not (Test-Path -LiteralPath $license)) { Write-Log "нет пакета или лицензии в $PSScriptRoot"; exit 0 }

    if ($Mode -eq 'System') {
        Write-State 'до'
        Install-Provisioned
        Write-State 'после'
        exit 0
    }

    # Предустановленные пакеты регистрируются в первые секунды после входа (809 у Club было через секунду) — дать пройти.
    Start-Sleep -Seconds 90
    Write-State 'вход'
    $repairs = 0
    $end = (Get-Date).AddMinutes(25)
    while ((Get-Date) -lt $end) {
        # Служба развёртывания пакетов при входе бывает занята — ошибка проверки не прерывает страховку.
        try { $present = @(Get-AppxPackage -Name $name) }
        catch { Write-Log "Get-AppxPackage: ОШИБКА $($_.Exception.Message)"; Start-Sleep -Seconds 60; continue }
        if (-not $present) {
            if (++$repairs -gt 3) { Write-Log 'панели нет и после 3 попыток — разобрать nvcp.log'; break }
            Write-Log "панели у $env:USERNAME нет — ставлю (попытка $repairs)"
            Install-Provisioned
            try { Add-AppxPackage -Path $appx; Write-Log 'Add-AppxPackage: OK' } catch { Write-Log "Add-AppxPackage: ОШИБКА $($_.Exception.Message)" }
        }
        Start-Sleep -Seconds 60
    }
    Write-State 'конец'
} catch {
    Write-Log "ОШИБКА: $($_.Exception.Message)"
}
