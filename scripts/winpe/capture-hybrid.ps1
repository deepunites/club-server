# Снятие генерализованного образа гибрида с эталонного ПК (PC-11), docs/imaging.md («Снятие гибридного образа»).
# Запуск — capture-hybrid.cmd в WinPE с флешки (build-winpe.cmd /usb кладёт оба файла в её корень) после того, как
# seal-hybrid.ps1 (sysprep /generalize /oobe /shutdown) выключил ПК, а Windows после sysprep не загружалась.
#   1. Раздел Windows: буква с Windows\System32\config\SYSTEM, кроме X: (сам WinPE). Несколько — выбирает человек.
#   2. Проверка как у club-deploy.ps1 (Test-Generalized): ImageState = IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE, иначе
#      отказ; GeneralizationState — только для записи. Токены помощника этого ПК в образе — удаляются после «Y».
#   3. net use \\192.168.1.201\incoming /user:clubimg: пароль вводится в net use и нигде не сохраняется.
#   4. dism /Capture-Image в подпапку incoming\capture-tmp: импорт в панели видит только *.wim/*.esd в корне incoming
#      (ImageLibrary.IncomingFiles), недописанный образ импортировать нельзя. После успешного dism и dism /Get-WimInfo —
#      перенос в incoming\<метка>.wim (переименование на сервере).
# Тексты на экране — английские: в WinPE без языкового пакета кириллица в консоли не отображается.
param(
    [string]$Share = '\\192.168.1.201\incoming',
    [string]$Drive,
    [string]$Label,
    [string]$ScratchDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Generalized = 'IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE'
# ImageLibrary.LabelPattern на сервере: метка — имя файла, имя образа в WIM и метка версии при импорте.
$LabelPattern = '^[a-z0-9][a-z0-9._-]{0,39}$'
$TempFolder = 'capture-tmp'

function Say([string]$Text) {
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text)
}

function Warn([string]$Text) {
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text) -ForegroundColor Yellow
}

# Значение из куста реестра (SOFTWARE, SYSTEM) офлайн-Windows; null — значения нет. Как Get-OfflineValue в
# club-deploy.ps1, но куст, который не загрузился или не выгрузился, — ошибка: открытый куст сорвал бы снятие образа.
function Get-OfflineValue([string]$WindowsDrive, [string]$Hive, [string]$Key, [string]$Name) {
    $mount = "ClubCapture$Hive"
    # Остаток прерванного запуска.
    if (Test-Path "HKLM:\$mount") {
        & reg.exe unload "HKLM\$mount" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "HKLM\$mount from an earlier run is still loaded and reg unload failed (exit code $LASTEXITCODE). Reboot into WinPE and run again." }
    }
    & reg.exe load "HKLM\$mount" "${WindowsDrive}:\Windows\System32\config\$Hive" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "reg load of ${WindowsDrive}:\Windows\System32\config\$Hive failed (exit code $LASTEXITCODE)" }
    try {
        return (Get-ItemProperty -Path "HKLM:\$mount\$Key" -ErrorAction SilentlyContinue).$Name
    }
    finally {
        $unloaded = $false
        for ($i = 1; $i -le 5 -and -not $unloaded; $i++) {
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
            & reg.exe unload "HKLM\$mount" | Out-Null
            $unloaded = $LASTEXITCODE -eq 0
            if (-not $unloaded) { Start-Sleep -Seconds 1 }
        }
        if (-not $unloaded) { throw "reg unload HKLM\$mount failed (exit code $LASTEXITCODE), the hive stays open. Reboot into WinPE and run again." }
    }
}

# Состояние sysprep — как Test-Generalized в club-deploy.ps1: генерализован только IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE,
# другое значение или его нет — нет. GeneralizationState — только для диагностики.
function Get-SysprepState([string]$WindowsDrive) {
    $imageState = Get-OfflineValue $WindowsDrive 'SOFTWARE' 'Microsoft\Windows\CurrentVersion\Setup\State' 'ImageState'
    $generalizationState = Get-OfflineValue $WindowsDrive 'SYSTEM' 'Setup\Status\SysprepStatus' 'GeneralizationState'
    if ($null -ne $imageState) { $imageState = [string]$imageState }
    if ($null -ne $generalizationState) { $generalizationState = $generalizationState -as [int] }
    [pscustomobject]@{
        Generalized = $imageState -eq $Generalized
        ImageState = $imageState
        GeneralizationState = $generalizationState
        Text = 'ImageState={0}, GeneralizationState={1}' -f $(if ($null -ne $imageState) { $imageState } else { 'none' }), $(if ($null -ne $generalizationState) { $generalizationState } else { 'none' })
    }
}

# Буквы разделов с офлайн-Windows, кроме X: (в самом WinPE тоже есть config\SYSTEM).
function Find-OfflineWindows {
    foreach ($volume in [System.IO.DriveInfo]::GetDrives()) {
        $letter = $volume.Name.Substring(0, 1).ToUpperInvariant()
        if ("${letter}:" -eq $env:SystemDrive -or [string]$volume.DriveType -eq 'Network' -or -not $volume.IsReady) { continue }
        if (Test-Path -LiteralPath "${letter}:\Windows\System32\config\SYSTEM") { $letter }
    }
}

function Get-VolumeText([string]$Letter) {
    $volume = [System.IO.DriveInfo]::new("${Letter}:\")
    '{0}: label "{1}", {2}, {3:N1} GB, {4:N1} GB used' -f $Letter, $volume.VolumeLabel, $volume.DriveFormat, ($volume.TotalSize / 1GB), (($volume.TotalSize - $volume.TotalFreeSpace) / 1GB)
}

function Test-Port([string]$HostName, [int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { return $client.ConnectAsync($HostName, $Port).Wait(3000) }
    catch { return $false }
    finally { $client.Close() }
}

$code = 1
$connected = $false
$temp = $null
$captured = $false
$scratchCreated = $false
$started = Get-Date
# Журналы — на флешке рядом со скриптом (X: — RAM-диск, пропадёт при перезагрузке).
$logDir = if ($PSScriptRoot -and (Split-Path -Qualifier $PSScriptRoot) -ne $env:SystemDrive) { $PSScriptRoot } else { $null }
$transcript = $false
if ($logDir) {
    try { Start-Transcript -Path (Join-Path $logDir 'capture-hybrid.log') -Append | Out-Null; $transcript = $true } catch { }
}

try {
    Say 'Club: capture of the generalized hybrid image (reference PC after sysprep)'
    if (-not (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT')) {
        throw 'This is not WinPE. Boot the PC from the WinPE USB stick (F12, UEFI: the stick) and run it there.'
    }
    $shareUri = [uri]$Share
    if (-not $shareUri.IsUnc -or -not $shareUri.Host) { throw "Share must look like \\server\share, not '$Share'" }
    $server = $shareUri.Host

    # 1. Раздел Windows.
    $found = @(Find-OfflineWindows)
    if ($Drive) {
        $Drive = $Drive.Trim().TrimEnd('\').TrimEnd(':').ToUpperInvariant()
        if ($found -notcontains $Drive) { throw "No offline Windows on ${Drive}: (found: $(if ($found.Count) { $found -join ', ' } else { 'none' }))" }
    }
    elseif ($found.Count -eq 0) {
        try { Get-Volume | Format-Table -AutoSize | Out-Host } catch { }
        throw 'No offline Windows found (a drive with Windows\System32\config\SYSTEM). If its partition has no letter: diskpart, list volume, select volume N, assign letter=W, exit; then run again.'
    }
    elseif ($found.Count -eq 1) {
        $Drive = $found[0]
    }
    else {
        Warn 'More than one offline Windows found:'
        foreach ($letter in $found) {
            $state = try { (Get-SysprepState $letter).Text } catch { "hives not readable: $($_.Exception.Message)" }
            Write-Host "  $(Get-VolumeText $letter); $state"
        }
        $answer = ([string](Read-Host 'Type the drive letter to capture (Enter cancels)')).Trim().TrimEnd(':').ToUpperInvariant()
        if (-not $answer) { throw 'Cancelled.' }
        if ($found -notcontains $answer) { throw "${answer}: is not one of the listed drives" }
        $Drive = $answer
    }
    if ($logDir -and (Split-Path -Qualifier $logDir) -eq "${Drive}:") { throw "Run the script from the USB stick, not from the captured drive ${Drive}:" }
    Say "Windows partition $(Get-VolumeText $Drive)"
    try { Say "Windows version (ntoskrnl.exe): $((Get-Item -LiteralPath "${Drive}:\Windows\System32\ntoskrnl.exe").VersionInfo.ProductVersion)" } catch { }

    # 2. Генерализован ли: та же проверка, что у WinPE при заливке (club-deploy.ps1).
    $sysprep = Get-SysprepState $Drive
    Say "Sysprep check: $($sysprep.Text)"
    if (-not $sysprep.Generalized) {
        throw "Windows on ${Drive}: is not generalized ($($sysprep.Text)), ImageState must be $Generalized. Run seal-hybrid.ps1 on this PC (sysprep /generalize /oobe /shutdown) and boot this stick right after it, without starting that Windows."
    }
    # Токены помощника и назначение личного диска этого ПК достались бы всем гибридам (их удаляет seal-hybrid.ps1).
    $leftovers = @(Get-ChildItem -LiteralPath "${Drive}:\ProgramData\ClubDiskless" -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'credentials.bin*' -or $_.Name -like 'assignment.json*' })
    if ($leftovers.Count -gt 0) {
        Warn "Helper state of this PC is still in the image: $($leftovers.Name -join ', ') in ${Drive}:\ProgramData\ClubDiskless"
        if (([string](Read-Host 'Type Y to delete these files from the offline Windows and continue')).Trim() -ne 'y') { throw 'Cancelled: helper state left in the image.' }
        $leftovers | Remove-Item -Force
        Say 'Deleted.'
    }

    # 3. Сервер. Сеть в WinPE поднимает wpeinit с драйвером из boot.wim (build-winpe.cmd /drivers).
    if (-not (Test-Port $server 445)) {
        Say "Waiting up to 2 minutes for the network and $server (SMB, port 445)..."
        & ipconfig.exe | Select-String 'IPv4' | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        $deadline = (Get-Date).AddMinutes(2)
        while (-not (Test-Port $server 445)) {
            if ((Get-Date) -gt $deadline) {
                & ipconfig.exe /all | Out-Host
                throw "$server is not reachable on port 445. No IPv4 address above means this WinPE has no driver for the NIC: build it with build-winpe.cmd /drivers."
            }
            Start-Sleep -Seconds 3
        }
    }
    # Без подключения Samba отвечает «Access is denied», и при ErrorActionPreference Stop Test-Path бросал бы его
    # (PC-11 2026-10-24) — поэтому SilentlyContinue: нет доступа = ещё не подключено.
    if (Test-Path -LiteralPath "$Share\" -ErrorAction SilentlyContinue) {
        Say "$Share is already connected"
    }
    else {
        # Пароль вводится в net use (не виден, не сохраняется). Вызов не в функции и без перенаправления вывода:
        # иначе PowerShell перехватит вывод net и приглашение ввести пароль не появится на экране.
        for ($try = 1; ; $try++) {
            Write-Host "Connecting to $Share as clubimg. Type the clubimg password (it is not shown and not saved)."
            & net.exe use $Share '*' '/user:clubimg' '/persistent:no'
            if ($LASTEXITCODE -eq 0) { $connected = $true; break }
            if ($try -ge 3) { throw "net use $Share failed (exit code $LASTEXITCODE)" }
            Warn "net use failed (exit code $LASTEXITCODE), try again"
        }
    }

    # 4. Метка и пути. Занятое имя в incoming — спросить другую.
    while ($true) {
        if (-not $Label) {
            $Label = ([string](Read-Host ('Image label: lowercase letters, digits, dot, dash, underscore, up to 40 chars, e.g. h1-{0:yyyy-MM-dd} (Enter cancels)' -f (Get-Date)))).Trim()
            if (-not $Label) { throw 'Cancelled.' }
        }
        if ($Label -cnotmatch $LabelPattern) { Warn "Bad label '$Label'"; $Label = $null; continue }
        $final = Join-Path $Share "$Label.wim"
        if (Test-Path -LiteralPath $final) { Warn "$final already exists, choose another label"; $Label = $null; continue }
        break
    }
    $tempDir = Join-Path $Share $TempFolder
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    $tempPath = Join-Path $tempDir "$Label.wim"
    if (Test-Path -LiteralPath $tempPath) { Warn "Removing an unfinished capture $tempPath"; Remove-Item -LiteralPath $tempPath -Force }

    # Свободное место в incoming: WIM меньше занятого на разделе (сжатие max), но больше занятого — точно не влезет.
    $source = [System.IO.DriveInfo]::new("${Drive}:\")
    $used = $source.TotalSize - $source.TotalFreeSpace
    $freeLine = & cmd.exe /c dir /-c $Share '2>nul' | Select-String 'bytes free' | Select-Object -Last 1
    if ($freeLine -and $freeLine.Line -match '(\d+) bytes free') {
        $free = [long]$Matches[1]
        Say ('Free in {0}: {1:N1} GB; used on {2}: {3:N1} GB (the WIM is smaller)' -f $Share, ($free / 1GB), "${Drive}:", ($used / 1GB))
        if ($free -lt $used -and ([string](Read-Host 'Less free space than used on the drive. Type Y to capture anyway')).Trim() -ne 'y') { throw 'Cancelled: not enough free space.' }
    }
    else {
        Warn "Could not read the free space of $Share"
    }

    # Scratch DISM — на диске: не на X: (RAM-диск 512 МБ, build-winpe.cmd) и не на снимаемом разделе. По умолчанию — флешка.
    if (-not $ScratchDir) {
        if (-not $logDir) { throw 'Run the script from the USB stick or give -ScratchDir on a local disk other than X:' }
        $ScratchDir = Join-Path $logDir 'capture-scratch'
    }
    $scratchDrive = Split-Path -Qualifier $ScratchDir
    if ($scratchDrive -eq $env:SystemDrive -or $scratchDrive -eq "${Drive}:") { throw "ScratchDir $ScratchDir must be neither on $env:SystemDrive (RAM) nor on the captured drive ${Drive}:" }
    if (-not (Test-Path -LiteralPath $ScratchDir)) { New-Item -ItemType Directory -Force -Path $ScratchDir | Out-Null; $scratchCreated = $true }

    # 5. Снятие. Вызов не в функции: на экране остаётся индикатор хода dism. /CheckIntegrity — таблица целостности в WIM.
    $capture = @('/Capture-Image', "/ImageFile:$tempPath", "/CaptureDir:${Drive}:\", "/Name:$Label", '/Compress:max', '/CheckIntegrity', "/ScratchDir:$ScratchDir")
    if ($logDir) { $capture += "/LogPath:$(Join-Path $logDir "capture-hybrid-$Label-dism.log")" }
    Say "Capturing ${Drive}:\ to $tempPath (max compression, this takes a while)..."
    $temp = $tempPath
    & dism.exe @capture
    if ($LASTEXITCODE -ne 0) { throw "dism /Capture-Image failed with exit code $LASTEXITCODE" }
    $captured = $true
    & dism.exe /Get-WimInfo "/WimFile:$tempPath" /Index:1
    if ($LASTEXITCODE -ne 0) { throw "dism /Get-WimInfo failed with exit code $LASTEXITCODE, the captured file is not a readable WIM" }

    # 6. В корень incoming — только теперь образ виден импорту в панели.
    if (Test-Path -LiteralPath $final) { throw "$final appeared during the capture" }
    Move-Item -LiteralPath $tempPath -Destination $final
    $temp = $null
    $size = (Get-Item -LiteralPath $final).Length
    Write-Host ''
    Write-Host ('Done: {0}, {1:N2} GiB, in {2:hh\:mm\:ss}. {3}' -f $final, ($size / 1GB), ((Get-Date) - $started), $sysprep.Text) -ForegroundColor Green
    Write-Host "Next: club panel, Windows images, Import: file $Label.wim, label $Label, index 1 (docs/imaging.md)."
    Write-Host 'Remove the stick and power the PC off. Starting this Windows runs OOBE: to capture again, sysprep it again.'
    $code = 0
}
catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($temp -and -not $captured -and (Test-Path -LiteralPath $temp)) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        Write-Host "Removed the unfinished $temp"
    }
    elseif ($temp -and $captured) {
        Write-Host "The captured image stays in $temp, the panel import does not see it there."
    }
}
finally {
    if ($scratchCreated) { Remove-Item -LiteralPath $ScratchDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($connected) { & net.exe use $Share /delete /y | Out-Null }
    if ($transcript) { try { Stop-Transcript | Out-Null } catch { } }
}
exit $code
