<#
.SYNOPSIS
    Ставит Microsoft Edge WebView2 Runtime (Evergreen) перед установкой ClubShell.
.DESCRIPTION
    Без WebView2 оболочка ClubShell не работает. Установщик ClubShell качает загрузчик WebView2 сам, но сверяет его с
    контрольной суммой, зашитой при сборке релиза; Microsoft с тех пор обновила файл, и установка шелла падает с кодом
    0x80091007 (CRYPT_E_HASH_VALUE) — у 1.0.18 и у 1.0.20 (сумма в сборке та же). Если WebView2 уже стоит, установщик
    шелла этот шаг пропускает.
    Что делает (PowerShell от администратора, нужен интернет):
      1. если WebView2 Runtime уже зарегистрирован (HKLM ...\EdgeUpdate\Clients\{F3017226-...}, pv) — ничего;
      2. скачивает официальный загрузчик Evergreen с go.microsoft.com, проверяет подпись Microsoft;
      3. ставит тихо (/silent /install) и проверяет, что версия зарегистрирована. Эталон бездиска: fixes-v2.ps1
         выключает службы обновления Edge (edgeupdate, edgeupdatem), а загрузчик ставит WebView2 через них — на время
         установки выключенные из них становятся «Вручную», в конце (и при ошибке) снова «Отключена»; задания
         MicrosoftEdgeUpdate* — тоже снова выключены, как в fixes-v2.ps1: загрузчик обновляет старый клиент EdgeUpdate
         эталона, и тот заново регистрирует их включёнными (без служб они идут от SYSTEM), а места с C:, сброшенным при
         каждой загрузке, качали бы обновления Edge/WebView2 при каждой загрузке и меняли WebView2 под работающим шеллом.
    -FromInstall — запуск из shell-install.ps1 (его шаг 3): без подсказки «теперь запустите shell-install», он уже идёт.
    Строки вывода — только ASCII.
#>
param([switch]$FromInstall)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

$key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
function Get-WebView2Version {
    try { (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).pv } catch { $null }
}

$version = Get-WebView2Version
if ($version -and $version -ne '0.0.0.0') {
    Write-Host "WebView2 Runtime $version is already installed: nothing to do" -ForegroundColor Green
    return
}

$exe = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
Write-Host 'Downloading the WebView2 Evergreen bootstrapper from Microsoft (go.microsoft.com)'
Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $exe
$sig = Get-AuthenticodeSignature -LiteralPath $exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    throw "The downloaded file is not signed by Microsoft (signature $($sig.Status), signer '$($sig.SignerCertificate.Subject)')."
}
Write-Host 'Microsoft signature OK; installing WebView2 Runtime (1-2 minutes, no windows)'
# Только выключенные службы и только на время установки (эталон: fixes-v2.ps1); на гибриде они не выключены — не трогаем.
$disabledUpdate = @(Get-Service -Name 'edgeupdate', 'edgeupdatem' -ErrorAction SilentlyContinue | Where-Object { "$($_.StartType)" -eq 'Disabled' } | ForEach-Object { $_.Name })
try {
    foreach ($name in $disabledUpdate) {
        Set-Service -Name $name -StartupType Manual
        Write-Host "  service ${name}: Disabled -> Manual for the install"
    }
    $process = Start-Process -FilePath $exe -ArgumentList '/silent', '/install' -Wait -PassThru
} finally {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    foreach ($name in $disabledUpdate) {
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        Set-Service -Name $name -StartupType Disabled -ErrorAction SilentlyContinue
        if ("$((Get-Service -Name $name -ErrorAction SilentlyContinue).StartType)" -eq 'Disabled') { Write-Host "  service ${name}: Disabled again" }
        else { Write-Host "  service ${name}: could NOT be set back to Disabled (Set-Service $name -StartupType Disabled)" -ForegroundColor Yellow }
    }
    # Эталон (службы были выключены fixes-v2.ps1): задания MicrosoftEdgeUpdate* обновлённый клиент регистрирует заново
    # включёнными — выключить их снова, как fixes-v2.ps1. На гибриде ($disabledUpdate пуст) не трогаем.
    if ($disabledUpdate.Count -gt 0) {
        try {
            foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'MicrosoftEdgeUpdate*' })) {
                $task | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
                $taskState = "$((Get-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue).State)"
                if ($taskState -eq 'Disabled') { Write-Host "  task $($task.TaskName): Disabled" }
                else { Write-Host "  task $($task.TaskName): could NOT be disabled ($taskState): Get-ScheduledTask MicrosoftEdgeUpdate* | Disable-ScheduledTask" -ForegroundColor Yellow }
            }
        } catch {
            Write-Host "  tasks MicrosoftEdgeUpdate*: not checked ($($_.Exception.Message)): Get-ScheduledTask MicrosoftEdgeUpdate* | Disable-ScheduledTask" -ForegroundColor Yellow
        }
    }
}

$version = Get-WebView2Version
if ($version -and $version -ne '0.0.0.0') {
    Write-Host "Done: WebView2 Runtime $version installed (exit code $($process.ExitCode))." -ForegroundColor Green
    if (-not $FromInstall) { Write-Host 'Now run shell-install-run-as-admin.cmd.' -ForegroundColor Green }
} else {
    throw "WebView2 Runtime is not registered after the install (exit code $($process.ExitCode))."
}
