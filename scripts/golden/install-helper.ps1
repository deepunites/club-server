#Requires -Version 5.1
<#
.SYNOPSIS
    Ставит помощник бездиска (ClubDisklessHelper 1.6.0) в эталон или обновляет его на гибриде: служба Windows, которая
    подключает диски игр G: («Online Games») и H: («Offline Games», с 1.6.0), правку библиотек и сообщает серверу клуба
    состояние ПК (docs/helper.md).

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора. Рядом со скриптом (флешка) — три файла:
      ClubDisklessHelper.exe — сборка 1.6.0, SHA256 сверяется;
      helper.json            — адрес сервера и ключ клуба (секрет: после установки удаляется с флешки);
      club-ca.pem            — CA клуба, которым подписан сертификат сервера.
    Помощник 1.5.0 привязывает регистрацию к HWID платы: запуск на мастере безопасен — места с другим HWID
    регистрируются сами, регистрацию мастера не наследуют. Повторный запуск обновляет файлы и перезапускает службу.

    Гибрид — обновление на месте: та же флешка (helper.json не нужен — остаётся установленный), вход под администратором
    клуба (Shift при входе — мимо киоска). G: и H: при перезапуске службы не сбрасываются и не отключаются. Только G: на
    ПК — в helper.json "Libraries": []. Откат — install-helper.ps1 из git 1b748fa и exe 1.5.1: H: пропадёт после
    перезагрузки, G: как прежде.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\install-helper.ps1
#>
[CmdletBinding()]
param(
    [string]$Source
)

$ErrorActionPreference = 'Continue'
# $PSScriptRoot в значении параметра по умолчанию пуст при запуске powershell.exe -File (PS 5.1) — берём папку скрипта здесь.
if (-not $Source) { $Source = Split-Path -Parent $MyInvocation.MyCommand.Path }

# TODO-1.6.0-SHA: SHA256 сборки 1.6.0 вписывается, когда собран релиз (один exe — для эталона, гибридов и h1e;
# patch-hybrid-wim.sh сверяет exe с этой строкой). До этого скрипт отказывается ставить.
$ExeSha256 = 'TODO-1.6.0-SHA'   # 1.6.0+<коммит> (.NET 10.0.13)
$ExeVersion = '1.6.0'
$ServiceName = 'ClubDisklessHelper'
$InstallDir = "$env:ProgramFiles\ClubDiskless"
$StateDir = "$env:ProgramData\ClubDiskless"
$ServiceDescription = 'Диски игр G: и H:, правка библиотек и состояние ПК для сервера клуба'

# --- Вывод ------------------------------------------------------------------------------------------------------------
$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { $script:WarnCount++; Write-Host "WARN  $Text" -ForegroundColor Yellow }

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

$copied = $false
$cfg = $null
try {
    Write-Title 'Файлы установки'
    $exe = Join-Path $Source 'ClubDisklessHelper.exe'
    $json = Join-Path $Source 'helper.json'
    $ca = Join-Path $Source 'club-ca.pem'
    foreach ($p in $exe, $ca) { if (-not (Test-Path -LiteralPath $p)) { throw "нет файла $p" } }
    if ($ExeSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "в install-helper.ps1 не вписан SHA256 сборки $ExeVersion ($ExeSha256): скрипт ещё не готов" }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $ExeSha256) { throw 'SHA256 ClubDisklessHelper.exe не совпадает (не та сборка или файл повреждён)' }
    Write-Pass "ClubDisklessHelper.exe ${ExeVersion}: SHA256 совпадает"
    $haveJson = Test-Path -LiteralPath $json
    $stateJson = Join-Path $StateDir 'helper.json'
    $stateCa = Join-Path $StateDir 'club-ca.pem'
    if ($haveJson) {
        $config = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
        $cfg = $config.Helper
        if (-not $cfg.ServerUrl -or -not $cfg.ClubKey) { throw 'в helper.json нет ServerUrl или ClubKey' }
        # Путь к CA — в папке состояния: с путём на флешку места падали бы при старте службы (флешки там нет).
        if ("$($cfg.CaCertificatePath)" -ne $stateCa) { throw "в helper.json CaCertificatePath должен быть $stateCa (сейчас: $($cfg.CaCertificatePath))" }
        Write-Pass "helper.json: сервер $($cfg.ServerUrl), ключ клуба есть"
    } elseif (Test-Path -LiteralPath $stateJson) {
        Write-Line 'helper.json на флешке нет — остаётся уже установленный'
    } else { throw "нет файла $json" }

    # Папка состояния — новая ACL целиком (как StateDirectory.Ensure в службе): владелец — администраторы, только SYSTEM
    # и администраторы, без наследования. Ставится ДО копирования ключа клуба.
    Write-Title "Папка состояния $StateDir (доступ: SYSTEM и администраторы)"
    if ((Test-Path -LiteralPath $StateDir) -and ((Get-Item -LiteralPath $StateDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$StateDir — ссылка (reparse point), а не папка: удалите её и запустите снова"
    }
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $admins = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid),
            'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $StateDir -AclObject $acl -ErrorAction Stop
    Write-Pass 'права на папку выставлены'

    Write-Title 'Служба ClubDisklessHelper'
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        # «Остановлена» в SCM раньше, чем процесс выгрузил exe — ждём процесс, иначе копирование упадёт «файл занят».
        Get-Process -Name 'ClubDisklessHelper' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
        Write-Line 'служба остановлена для обновления'
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -LiteralPath $exe -Destination $InstallDir -Force -ErrorAction Stop
    Copy-Item -LiteralPath $ca -Destination $stateCa -Force -ErrorAction Stop
    if ($haveJson) {
        # Новый файл наследует ACL папки (Copy-Item поверх старого сохранил бы его ACL). Журнал Windows — с уровня
        # Information: иначе .NET пишет туда только предупреждения, и «mounted at G:» не видно.
        Remove-Item -LiteralPath $stateJson -Force -ErrorAction SilentlyContinue
        if (-not $config.PSObject.Properties['Logging']) {
            $config | Add-Member -NotePropertyName Logging -NotePropertyValue ([pscustomobject]@{ EventLog = [pscustomobject]@{ LogLevel = [pscustomobject]@{ Default = 'Information' } } })
        }
        $config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stateJson -Encoding UTF8 -ErrorAction Stop
        $copied = $true
    }
    $bin = '"{0}"' -f (Join-Path $InstallDir 'ClubDisklessHelper.exe')
    if (-not $svc) {
        New-Service -Name $ServiceName -BinaryPathName $bin -StartupType Automatic -DisplayName 'Club diskless helper' `
            -Description $ServiceDescription -ErrorAction Stop | Out-Null
        Write-Pass 'служба создана'
    } else {
        # Путь — через реестр: PowerShell 5.1 искажает кавычки внутри аргумента для sc.exe binPath=.
        Set-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $bin -Type ExpandString -ErrorAction Stop
        Set-Service -Name $ServiceName -StartupType Automatic -Description $ServiceDescription -ErrorAction Stop
        Write-Pass 'служба обновлена'
    }
    # Упала — перезапуск через 5 с (дважды), потом через 30 с; счётчик сбрасывается за сутки.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    $started = Get-Date
    try { Start-Service -Name $ServiceName -ErrorAction Stop; Write-Pass 'служба запущена' }
    catch { Write-Warn "служба не запустилась: $($_.Exception.Message)" }

    Write-Title 'Регистрация на сервере клуба и диск игр (до 90 с)'
    $creds = Join-Path $StateDir 'credentials.bin'
    for ($i = 0; $i -lt 45 -and -not ((Test-Path -LiteralPath $creds) -and (Get-Volume -DriveLetter G -ErrorAction SilentlyContinue)); $i++) { Start-Sleep -Seconds 2 }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started.AddSeconds(-5) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in $ServiceName, '.NET Runtime', 'Application Error' } | Sort-Object TimeCreated | Select-Object -Last 10)
    foreach ($e in $events) { Write-Line ('{0:HH:mm:ss} {1} {2}: {3}' -f $e.TimeCreated, $e.ProviderName, $e.LevelDisplayName, (($e.Message -split "`n")[0]).Trim()) }
    if ((Get-Service -Name $ServiceName).Status -eq 'Running') { Write-Pass 'служба работает' } else { Write-Warn 'служба не работает — сообщения выше' }
    $server = if ($cfg) { $cfg.ServerUrl } else { 'сервера клуба' }
    if (Test-Path -LiteralPath $creds) { Write-Pass 'помощник зарегистрирован на сервере (credentials.bin)' }
    else { Write-Warn "регистрации нет за 90 с: проверьте сеть до $server и сообщения выше" }
    # G: появляется, только когда опубликована версия библиотеки игр — до первой публикации его нет, это не ошибка.
    if (Get-Volume -DriveLetter G -ErrorAction SilentlyContinue) { Write-Pass 'диск игр G: подключён' }
    else {
        Write-Line 'диска игр G: нет: версия библиотеки не опубликована или сервер ещё готовит личный диск места (панель «Бездиск»)'
        Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':games-' } |
            ForEach-Object { Write-Line "сессия игр: $($_.TargetNodeAddress), подключена: $($_.IsConnected)" }
    }
    # H: («Offline Games», помощник 1.6.0) — только сведения: её нет, пока на сервере не включена библиотека H:
    # (Libraries__solo__*) или не опубликована её версия; цикл H: идёт после G:, первый диск H: сервер может готовить
    # дольше такта. Буква H: у другого тома (второй диск, картридер, DVD, флешка) — помощник не подключит H: никогда.
    $hVolume = Get-Volume -DriveLetter H -ErrorAction SilentlyContinue
    if ($hVolume) {
        $hDisk = $null
        try { $hDisk = Get-Partition -DriveLetter H -ErrorAction Stop | Get-Disk -ErrorAction Stop } catch { }
        if ($hDisk -and ("$($hDisk.BusType)" -eq 'iSCSI' -or "$($hDisk.BusType)" -eq '9')) { Write-Pass ('диск офлайн-игр H: подключён ({0})' -f $hVolume.FileSystemLabel) }
        else { Write-Warn ('буква H: занята другим томом ({0}, метка «{1}»): диск офлайн-игр H: не подключится — «Управление дисками» -> «Изменить букву диска» на другую (не G: и не H:)' -f $hVolume.DriveType, $hVolume.FileSystemLabel) }
    } else {
        Write-Line 'диска офлайн-игр H: нет: библиотека H: на сервере не включена, не опубликована или диск ещё готовится — это не ошибка'
        Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':solo-' } |
            ForEach-Object { Write-Line "сессия H: $($_.TargetNodeAddress), подключена: $($_.IsConnected)" }
    }
    Write-Line 'Перед публикацией эталона — seal-golden.ps1: он убирает регистрацию этого ПК из образа. На гибриде (обновление на месте) больше ничего не нужно.'
} catch {
    Write-Warn $_.Exception.Message
} finally {
    # Ключ клуба не остаётся на флешке, даже если установка прервалась после копирования.
    if ($copied -and (Test-Path -LiteralPath $json)) {
        Remove-Item -LiteralPath $json -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $json) { Write-Warn "удалите $json с флешки вручную: в нём ключ клуба" } else { Write-Pass 'helper.json с ключом клуба удалён с флешки' }
    }
}

Write-Host ''
if ($script:WarnCount -eq 0) { Write-Host 'Готово без предупреждений.' -ForegroundColor Green }
else { Write-Host ('Готово, предупреждений: {0} (строки WARN выше).' -f $script:WarnCount) -ForegroundColor Yellow }
