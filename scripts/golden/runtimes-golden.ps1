#Requires -Version 5.1
<#
.SYNOPSIS
    Библиотеки для игр в эталон: Visual C++ 2005–2022 (x86 и x64), DirectX 9–11 (июнь 2010), .NET Framework 3.5.

.DESCRIPTION
    Запускать ТОЛЬКО внутри эталона в режиме мастера, PowerShell от администратора, после обновлений Windows.
    Без этих библиотек многие игры не запускаются («нет MSVCP140.dll», «нет d3dx9_43.dll», XInput, XAudio2).
    Установщики — с download.microsoft.com (подписи Microsoft проверены на сервере); скрипт скачивает их одним архивом
    с сервера клуба и сверяет SHA256. Повторный запуск безопасен: уже стоящие библиотеки пропускаются (код 1638).
    .NET Framework 3.5 скачивается из Windows Update (компонент Windows), это может занять несколько минут.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File $env:TEMP\runtimes-golden.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools',
    [string]$Work = "$env:SystemDrive\ClubRuntimes"
)

$ErrorActionPreference = 'Continue'

$Bundle = @{ File = 'runtimes-2026-10-18.zip'; Sha256 = '77fe6d959dfc934a75dd1e2139c9efee5939d632341ebc8cc035257b92662174' }

# Тихие ключи установщиков: 2005 — IExpress (/q), 2008 — самораспаковщик VS (/q), 2010 — /q /norestart, 2012+ — WiX Burn
# (/install /quiet /norestart).
$Installers = @(
    @{ File = 'vc2005_x86.exe'; Args = '/q'; Name = 'Visual C++ 2005 x86' },
    @{ File = 'vc2005_x64.exe'; Args = '/q'; Name = 'Visual C++ 2005 x64' },
    @{ File = 'vc2008_x86.exe'; Args = '/q'; Name = 'Visual C++ 2008 x86' },
    @{ File = 'vc2008_x64.exe'; Args = '/q'; Name = 'Visual C++ 2008 x64' },
    @{ File = 'vc2010_x86.exe'; Args = '/q /norestart'; Name = 'Visual C++ 2010 x86' },
    @{ File = 'vc2010_x64.exe'; Args = '/q /norestart'; Name = 'Visual C++ 2010 x64' },
    @{ File = 'vc2012_x86.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2012 x86' },
    @{ File = 'vc2012_x64.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2012 x64' },
    @{ File = 'vc2013_x86.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2013 x86' },
    @{ File = 'vc2013_x64.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2013 x64' },
    @{ File = 'vc2015-2022_x86.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2015–2022 x86' },
    @{ File = 'vc2015-2022_x64.exe'; Args = '/install /quiet /norestart'; Name = 'Visual C++ 2015–2022 x64' }
)
# 0 — установлено, 3010 — нужна перезагрузка, 1638 — эта или более новая версия уже стоит.
$OkCodes = @(0, 1638, 3010)

# --- Вывод ------------------------------------------------------------------------------------------------------------
$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

function Invoke-Installer([string]$Path, [string]$Arguments) {
    $p = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

try {
    Write-Title 'Скачивание библиотек с сервера клуба'
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    $zip = Join-Path $Work $Bundle.File
    & "$env:SystemRoot\System32\curl.exe" -f --retry 3 --progress-bar -o $zip "$BaseUrl/$($Bundle.File)"
    if ($LASTEXITCODE -ne 0) { throw "не скачан $($Bundle.File) (curl, код $LASTEXITCODE)" }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $Bundle.Sha256) { throw "SHA256 не совпадает: $($Bundle.File)" }
    Write-Pass "$($Bundle.File): SHA256 совпадает"
    & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $Work
    if ($LASTEXITCODE -ne 0) { throw "не распакован $($Bundle.File) (tar, код $LASTEXITCODE)" }

    Write-Title 'Visual C++ 2005–2022'
    foreach ($i in $Installers) {
        $path = Join-Path $Work $i.File
        if (-not (Test-Path -LiteralPath $path)) { Write-Warn "$($i.Name): нет файла $($i.File) в архиве"; continue }
        $code = Invoke-Installer $path $i.Args
        if ($code -in $OkCodes) { Write-Pass ("{0}: код {1}" -f $i.Name, $code) } else { Write-Warn ("{0}: код {1}" -f $i.Name, $code) }
    }

    # .NET 3.5 — до DirectX: управляемой части DirectX (Managed DirectX 1.1) нужна среда CLR 2.0.
    Write-Title '.NET Framework 3.5 (из Windows Update, может занять несколько минут)'
    $fx = Get-WindowsOptionalFeature -Online -FeatureName NetFx3 -ErrorAction SilentlyContinue
    if ($fx -and $fx.State -eq 'Enabled') { Write-Pass '.NET Framework 3.5 уже включён' }
    else {
        & dism.exe /Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart
        if ($LASTEXITCODE -in 0, 3010) { Write-Pass ".NET Framework 3.5 включён (код $LASTEXITCODE)" }
        else { Write-Warn ".NET Framework 3.5 не включён (dism, код $LASTEXITCODE): нужен доступ к Windows Update" }
    }

    Write-Title 'DirectX 9–11 (июнь 2010): d3dx9/10/11, XInput 1.3, XAudio2 2.7'
    $dx = Join-Path $Work 'directx'
    New-Item -ItemType Directory -Force -Path $dx | Out-Null
    $code = Invoke-Installer (Join-Path $Work 'directx_Jun2010_redist.exe') "/Q /T:`"$dx`""
    if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $dx 'DXSETUP.exe'))) { Write-Warn "DirectX: распаковка не удалась (код $code)" }
    else {
        $code = Invoke-Installer (Join-Path $dx 'DXSETUP.exe') '/silent'
        if ($code -eq 0) { Write-Pass 'DirectX: установлено' } else { Write-Warn "DirectX: DXSETUP код $code" }
    }

    Write-Title 'Проверка'
    $vc = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Microsoft Visual C++*Redistributable*' } | Sort-Object DisplayName -Unique)
    $vc | ForEach-Object { Write-Line $_.DisplayName }
    # x86 узнаём по отсутствию «x64»: у 2005 x86 архитектура в названии не указана. С 14.50 (ноябрь 2025) 2015–2022
    # называется «Microsoft Visual C++ v14 Redistributable».
    foreach ($year in '2005', '2008', '2010', '2012', '2013', '2015|2017|2019|2022|v14') {
        foreach ($arch in 'x86', 'x64') {
            $found = @($vc | Where-Object { $_.DisplayName -match $year -and (($arch -eq 'x64') -eq ($_.DisplayName -match 'x64')) })
            $label = 'Visual C++ {0} {1}' -f ($year -replace '\|.*', '–2022'), $arch
            if ($found.Count -ge 1) { Write-Pass $label } else { Write-Warn "$label не найден в установленных программах" }
        }
    }
    foreach ($dll in "$env:SystemRoot\System32\d3dx9_43.dll", "$env:SystemRoot\SysWOW64\d3dx9_43.dll", "$env:SystemRoot\System32\xinput1_3.dll", "$env:SystemRoot\SysWOW64\xinput1_3.dll") {
        if (Test-Path -LiteralPath $dll) { Write-Pass "есть $dll" } else { Write-Warn "нет $dll" }
    }
    $fx = Get-WindowsOptionalFeature -Online -FeatureName NetFx3 -ErrorAction SilentlyContinue
    if ($fx -and $fx.State -in 'Enabled', 'EnablePending') { Write-Pass ".NET Framework 3.5: $($fx.State)" } else { Write-Warn ".NET Framework 3.5: $($fx.State)" }
} catch {
    Write-Warn $_.Exception.Message
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений. Перезагрузите ПК.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше). Перезагрузите ПК.' -f $script:WarnCount) -ForegroundColor Yellow }
