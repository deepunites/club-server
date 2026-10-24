#Requires -Version 5.1
<#
.SYNOPSIS
    Видеодрайверы для бездискового эталона карт Turing и новее: NVIDIA 617.42 (GTX 16xx, RTX 20/30/40/50) и AMD 26.5.2
    (Polaris) для RX 470.

.DESCRIPTION
    Запускать ТОЛЬКО внутри эталона в режиме мастера, PowerShell от администратора, после обновлений Windows.

    Почему отдельные образы по поколениям NVIDIA: с декабря 2025 NVIDIA делит драйверы — 59x+ (сейчас 617.42) только для
    Turing и новее, 582.xx — для Maxwell/Pascal/Volta (GTX 1050 Ti). Оба ставят один и тот же драйвер ядра nvlddmkm и одни
    и те же 46 файлов в System32 — в одной Windows уживается только один. Общий для всех 581.80 (ноябрь 2025) теряет FPS на
    обновлениях Windows с октября 2025 (признано NVIDIA). Клуб выбрал максимум FPS: этот образ — 617.42; для GTX 1050 Ti —
    свой образ с 582.78 (2026-10-21). AMD с NVIDIA не пересекается (свои службы; общие только загрузчики Vulkan/OpenCL).

    Что делает (повторный запуск безопасен):
      1. скачивает с сервера клуба два архива (только нужные INF: nv_dispi.inf, nvhda.inf, AMD WT6A_INF и HDMI-звук), сверяет SHA256;
      2. проверяет, что Windows Update не ставит драйверы; добавляет 617.42 в хранилище и только потом удаляет из него другие
         версии видеодрайверов NVIDIA/AMD (например, пришедшие из Windows Update при установке Windows);
      3. NVIDIA: pnputil /add-driver /install — на мастере (RTX 3050) ставится сразу, для GTX 1650 и других RTX лежит в хранилище;
         без NVIDIA App, телеметрии, FrameView и ShadowPlay; Панель управления NVIDIA — из пакета (appx);
      4. AMD: pnputil /add-driver без /install — карты AMD на мастере нет, драйвер ждёт RX 470 в хранилище;
      5. проверяет результат (PASS/WARN), удаляет временные файлы.

    Первая загрузка места с другой видеокартой ставит драйвер из хранилища (до минуты). Чтобы это не повторялось на каждой
    загрузке (место сбрасывается), каждую конфигурацию (плата + видеокарта) нужно один раз загрузить в режиме мастера.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File $env:TEMP\gpu-drivers.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools/drivers',
    [string]$Work = "$env:SystemDrive\ClubDrivers"
)

$ErrorActionPreference = 'Continue'

$NvVersion = '32.0.16.1742'     # NVIDIA 617.42
$AmdVersion = '31.0.21925.1001' # AMD Adrenalin 26.5.2 for Polaris & Vega
$Bundles = @(
    @{ File = 'gpu-nv-617.42.zip'; Sha256 = 'd340b365e9161ffda3aa2e4487dccb99abb045f459611609fc313f2ad6aa7ebe'; Dir = 'nv' },
    @{ File = 'gpu-amd-26.5.2.zip'; Sha256 = '17e92b66219a9c47b331ddb71e164c717a028550f437040db8c10dbc66413399'; Dir = 'amd' }
)
$MinFreeGb = 12   # архивы 2 ГБ + распаковка 4,3 ГБ + копии в хранилище драйверов

# --- Вывод ------------------------------------------------------------------------------------------------------------
$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

# pnputil: 0 — готово, 259 — пакет добавлен, но ни одно устройство не обновлено, 3010/1641 — нужна перезагрузка.
function Invoke-PnpUtil([string[]]$Arguments) {
    $out = & pnputil.exe @Arguments 2>&1
    $code = $LASTEXITCODE
    $out | Where-Object { "$_".Trim() } | Select-Object -Last 6 | ForEach-Object { Write-Line "$_" }
    if ($code -notin 0, 259, 1641, 3010) { Write-Warn ("pnputil {0} — код {1}" -f ($Arguments -join ' '), $code) }
    return $code
}

# Видеодрайверы в хранилище (DISM): Driver = oemNN.inf, OriginalFileName — исходный INF, Version — версия пакета.
function Get-StoreDisplayDrivers {
    # Только NVIDIA и AMD: «ATI» без якоря совпало бы с «Corporation» (Intel Corporation).
    Get-WindowsDriver -Online -ErrorAction Stop | Where-Object { $_.ClassName -eq 'Display' -and $_.ProviderName -match '^(NVIDIA|Advanced Micro Devices|AMD\b|ATI Technologies)' }
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

$freeGb = [math]::Round((Get-PSDrive -Name $env:SystemDrive.TrimEnd(':')).Free / 1GB, 1)
if ($freeGb -lt $MinFreeGb) { throw "На диске $env:SystemDrive свободно $freeGb ГБ, нужно не меньше $MinFreeGb ГБ." }

try {
    Write-Title 'Скачивание драйверов с сервера клуба'
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    foreach ($b in $Bundles) {
        $zip = Join-Path $Work $b.File
        $dir = Join-Path $Work $b.Dir
        $ok = (Test-Path -LiteralPath $zip) -and ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -eq $b.Sha256)
        if (-not $ok) {
            Write-Line "$($b.File) ..."
            & "$env:SystemRoot\System32\curl.exe" -f --retry 3 --progress-bar -o $zip "$BaseUrl/$($b.File)"
            if ($LASTEXITCODE -ne 0) { throw "не скачан $($b.File) (curl, код $LASTEXITCODE)" }
            if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $b.Sha256) { throw "SHA256 не совпадает: $($b.File)" }
        }
        Write-Pass "$($b.File): SHA256 совпадает"
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $dir
        if ($LASTEXITCODE -ne 0) { throw "не распакован $($b.File) (tar, код $LASTEXITCODE)" }
    }
    $nvInf = Join-Path $Work 'nv\Display.Driver\nv_dispi.inf'
    $nvHda = Join-Path $Work 'nv\HDAudio\nvhda.inf'
    $amdDisplay = Join-Path $Work 'amd\Display'
    $amdHdmi = Join-Path $Work 'amd\HDMI\AtihdWT6.inf'
    foreach ($p in $nvInf, $nvHda, (Join-Path $amdDisplay 'u0201039.inf'), $amdHdmi) {
        if (-not (Test-Path -LiteralPath $p)) { throw "в архиве нет $p" }
    }

    # Без этих политик Windows Update вернёт на RTX 3050 драйвер 59x+ (только Turing и новее) — те же значения ставит prepare-golden.ps1.
    Write-Title 'Windows Update не ставит драйверы'
    foreach ($v in @(@('HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate', 'ExcludeWUDriversInQualityUpdate'),
                     @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching', 'SearchOrderConfig'))) {
        $want = if ($v[1] -eq 'SearchOrderConfig') { 0 } else { 1 }
        if ((Get-ItemProperty -LiteralPath $v[0] -Name $v[1] -ErrorAction SilentlyContinue).($v[1]) -ne $want) {
            if (-not (Test-Path -LiteralPath $v[0])) { New-Item -Path $v[0] -Force | Out-Null }
            New-ItemProperty -LiteralPath $v[0] -Name $v[1] -Value $want -PropertyType DWord -Force | Out-Null
            Write-Warn "$($v[0]) : $($v[1]) не был $want — поставлен (запускали prepare-golden.ps1?)"
        } else { Write-Pass "$($v[1]) = $want" }
    }

    # Сначала 617.42 в хранилище: при удалении другой версии карта сразу переходит на него, а не на «Базовый видеоадаптер»,
    # и если пакет не добавился — ничего не удаляем.
    Write-Title "NVIDIA $NvVersion (617.42): добавление в хранилище"
    $code = Invoke-PnpUtil @('/add-driver', $nvInf)
    if ($code -notin 0, 259, 3010) { throw "NVIDIA 617.42 не добавлен в хранилище (pnputil, код $code) — другие версии не удалялись" }

    Write-Title 'Другие версии видеодрайверов NVIDIA/AMD: удаление из хранилища'
    $foreign = @(Get-StoreDisplayDrivers | Where-Object {
        -not (($_.ProviderName -match '^NVIDIA' -and $_.Version -eq $NvVersion) -or ($_.ProviderName -notmatch '^NVIDIA' -and $_.Version -eq $AmdVersion)) })
    if (-not $foreign) { Write-Pass 'других версий нет' }
    foreach ($d in $foreign) {
        Write-Line ("{0} ({1}, {2} {3})" -f $d.Driver, (Split-Path $d.OriginalFileName -Leaf), $d.ProviderName, $d.Version)
        Invoke-PnpUtil @('/delete-driver', $d.Driver, '/uninstall', '/force') | Out-Null
    }

    Write-Title "NVIDIA $NvVersion (617.42): установка на эту карту и в хранилище"
    Invoke-PnpUtil @('/add-driver', $nvInf, '/install') | Out-Null
    Invoke-PnpUtil @('/add-driver', $nvHda, '/install') | Out-Null
    $cplAppx = Get-ChildItem -LiteralPath (Join-Path $Work 'nv\Display.Driver\NVCPL') -Filter '*.appx' -ErrorAction SilentlyContinue | Select-Object -First 1
    $cplLicense = Get-ChildItem -LiteralPath (Join-Path $Work 'nv\Display.Driver\NVCPL') -Filter '*License*.xml' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cplAppx -and $cplLicense) {
        # -Regions all: без Regions предустановленный пакет, не закреплённый в Пуске, Windows убирает при первом входе
        # (гибриды PC-11/PC-21, 2026-10-24); лицензию Store sysprep всё равно стирает — после заливки панель заново
        # ставит C:\Windows\Setup\Scripts\nvcp\Install-NVCP.ps1 (scripts/server/patch-hybrid-wim.sh).
        try { Add-AppxProvisionedPackage -Online -PackagePath $cplAppx.FullName -LicensePath $cplLicense.FullName -Regions 'all' -ErrorAction Stop | Out-Null; Write-Pass 'Панель управления NVIDIA добавлена в образ' }
        catch { Write-Warn "Панель управления NVIDIA не добавлена: $($_.Exception.Message)" }
    } else { Write-Warn 'в архиве нет Панели управления NVIDIA (NVCPL)' }

    Write-Title "AMD $AmdVersion (26.5.2 Polaris): только в хранилище (для RX 470)"
    Invoke-PnpUtil @('/add-driver', (Join-Path $amdDisplay '*.inf'), '/subdirs') | Out-Null
    Invoke-PnpUtil @('/add-driver', $amdHdmi) | Out-Null

    Write-Title 'Проверка'
    $store = @(Get-StoreDisplayDrivers)
    $nv = @($store | Where-Object { $_.ProviderName -match '^NVIDIA' })
    $amd = @($store | Where-Object { $_.ProviderName -notmatch '^NVIDIA' })
    if ($nv.Count -eq 1 -and $nv[0].Version -eq $NvVersion) { Write-Pass "в хранилище один драйвер NVIDIA: $NvVersion ($($nv[0].Driver))" }
    else { Write-Warn ("драйверы NVIDIA в хранилище: {0}" -f (($nv | ForEach-Object { "$($_.Driver) $($_.Version)" }) -join ', ')) }
    if (@($amd | Where-Object { $_.Version -eq $AmdVersion }).Count -ge 1 -and -not @($amd | Where-Object { $_.Version -ne $AmdVersion })) { Write-Pass "в хранилище драйвер AMD: $AmdVersion" }
    else { Write-Warn ("драйверы AMD в хранилище: {0}" -f (($amd | ForEach-Object { "$($_.Driver) $($_.Version)" }) -join ', ')) }
    foreach ($gpu in @(Get-CimInstance Win32_VideoController)) {
        $text = "{0}: драйвер {1}, состояние {2}" -f $gpu.Name, $gpu.DriverVersion, $gpu.Status
        if ($gpu.PNPDeviceID -match 'VEN_10DE') { if ($gpu.DriverVersion -eq $NvVersion -and $gpu.Status -eq 'OK') { Write-Pass $text } else { Write-Warn $text } }
        else { Write-Line $text }
    }
    $svc = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm' -Name ImagePath -ErrorAction SilentlyContinue
    if ($svc -and $svc.ImagePath -match 'nv_dispi\.inf_amd64') { Write-Line "nvlddmkm: $($svc.ImagePath)" }
} catch {
    Write-Warn $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений. Перезагрузите ПК.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше). Перезагрузите ПК.' -f $script:WarnCount) -ForegroundColor Yellow }
