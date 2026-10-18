#Requires -Version 5.1
<#
.SYNOPSIS
    Последний шаг сеанса мастера перед публикацией эталона: убирает из образа регистрацию этого ПК у помощника
    бездиска и выключает ПК.

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора, когда всё остальное готово
    (prepare-golden.ps1 без WARN, перезагрузка прошла). После выключения — панель «Бездиск» → «Опубликовать».

    Зачем: помощник на мастере регистрируется на сервере и хранит в %ProgramData%\ClubDiskless токены этого ПК
    (credentials.bin, DPAPI машины — его расшифрует любое место с этим образом) и назначение с CHAP-секретом личного
    диска (assignment.json). В опубликованном образе они достались бы всем местам, а сам ПК мастера при каждой загрузке
    предъявлял бы уже использованный токен и терял регистрацию. Места с другим HWID эти файлы не используют, но читать их
    могут. Настройки (helper.json, club-ca.pem) остаются: по ним каждое место регистрируется само.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\seal-golden.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoShutdown,
    [switch]$Force
)

$ErrorActionPreference = 'Continue'
$ServiceName = 'ClubDisklessHelper'
$StateDir = "$env:ProgramData\ClubDiskless"
$StateFiles = 'credentials.bin', 'credentials.bin.tmp', 'assignment.json', 'assignment.json.tmp'

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

Write-Title 'Это сеанс мастера?'
$master = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':diskless-master$' })
if ($master) { Write-Pass "загрузка с мастер-тома ($($master[0].TargetNodeAddress))" }
elseif ($Force) { Write-Warn 'сессии diskless-master нет — продолжаю из-за -Force' }
else { throw 'Этот ПК загружен не с мастер-тома (нет iSCSI-сессии …:diskless-master). Скрипт — только для сеанса мастера (обойти: -Force).' }

if (Get-Volume -DriveLetter M -ErrorAction SilentlyContinue) {
    Write-Warn 'открыта правка библиотеки (M:): в панели «Закончить правку» библиотеки, потом снова этот скрипт'
}

Write-Title 'Помощник: остановка и удаление регистрации этого ПК'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Write-Warn 'службы ClubDisklessHelper нет — помощник не установлен (install-helper.ps1)' }
else {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'ClubDisklessHelper' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    }
    if ((Get-Service -Name $ServiceName).Status -eq 'Stopped') { Write-Pass 'служба остановлена' } else { Write-Warn 'служба не остановилась' }
    if ((Get-Service -Name $ServiceName).StartType -ne 'Automatic') { Write-Warn 'запуск службы не «Автоматически»: на местах помощник не запустится' }
}
foreach ($f in $StateFiles) {
    $p = Join-Path $StateDir $f
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) { Write-Warn "не удалён $p" } else { Write-Pass "удалён $f" }
    }
}
foreach ($f in 'helper.json', 'club-ca.pem') {
    if (Test-Path -LiteralPath (Join-Path $StateDir $f)) { Write-Pass "на месте $f" } else { Write-Warn "нет $StateDir\$f — места не зарегистрируются" }
}

Write-Host ''
if ($script:WarnCount -ne 0) {
    Write-Host ('Предупреждений: {0} (строки WARN выше). ПК не выключаю — разберите их и запустите снова.' -f $script:WarnCount) -ForegroundColor Yellow
    return
}
if ($NoShutdown) { Write-Host 'Готово. Выключите ПК (не перезагружайте), потом панель «Бездиск» → «Опубликовать».' -ForegroundColor Green; return }
Write-Host 'Готово. ПК выключится через 15 секунд; потом панель «Бездиск» → «Опубликовать».' -ForegroundColor Green
Write-Host 'Не включайте его до публикации: помощник снова зарегистрируется и попадёт в образ.' -ForegroundColor Green
& shutdown.exe /s /t 15 /c 'Эталон готов к публикации'
