# Заливка Windows в WinPE (сервер клуба, docs/imaging.md). Скрипт ничего не решает: план (диск, образ, имя)
# даёт сервер, скрипт исполняет и докладывает шаги. Любой сбой — сообщение на экране и ожидание: PXE-флаг
# снимается сервером только после успешного bcdboot, поэтому перезагрузка вернёт машину сюда же.
# Файл подкладывает wimboot в X:\Windows\System32 вместе с clubdeploy.json (адрес сервера).
# Тексты на экране — английские: в WinPE без языкового пакета кириллица в консоли не отображается.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Net.Http

$Config = Get-Content -Raw -Path "$env:SystemRoot\System32\clubdeploy.json" | ConvertFrom-Json
$Server = ([string]$Config.server).TrimEnd('/')
$script:JobBase = $null
$script:Step = 'start'

class DeployError : System.Exception {
    [string]$Reason
    DeployError([string]$reason, [string]$message) : base($message) { $this.Reason = $reason }
}

function Say([string]$Text) {
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text)
}

# Запрос к серверу. 4xx — решение сервера (DeployError с причиной), сеть и 5xx — повтор.
function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [int]$Tries = 6) {
    for ($i = 1; ; $i++) {
        try {
            $params = @{ Method = $Method; Uri = "$Server$Path"; UseBasicParsing = $true; TimeoutSec = 120 }
            if ($null -ne $Body) {
                $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6 -Compress))
                $params.ContentType = 'application/json; charset=utf-8'
            }
            $response = Invoke-WebRequest @params
            if ($response.Content) { return $response.Content | ConvertFrom-Json }
            return $null
        }
        catch {
            # PowerShell 5.1 (WinPE): WebException с HttpWebResponse; 7.x: HttpResponseException. Тело — в ErrorDetails.
            $http = $_.Exception.Response
            $status = if ($http) { [int]$http.StatusCode } else { 0 }
            if ($status -ge 400 -and $status -lt 500) {
                $text = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message }
                        elseif ($http -is [System.Net.WebResponse]) { (New-Object System.IO.StreamReader($http.GetResponseStream())).ReadToEnd() }
                        else { '' }
                $apiError = try { ($text | ConvertFrom-Json).error } catch { $null }
                $reason = if ($apiError.details.reason) { [string]$apiError.details.reason } else { "http$status" }
                $message = if ($apiError.message) { [string]$apiError.message } else { $text }
                throw [DeployError]::new($reason, $message)
            }
            if ($i -ge $Tries) { throw }
            Say "Server unreachable ($($_.Exception.Message)), retry in $([Math]::Min(30, 3 * $i)) s"
            Start-Sleep -Seconds ([Math]::Min(30, 3 * $i))
        }
    }
}

function Report([string]$Step, $Percent = $null, [string]$Message = $null) {
    $script:Step = $Step
    try {
        Invoke-Api PUT "$script:JobBase/progress" @{ step = $Step; percent = $Percent; message = $Message } -Tries 2 | Out-Null
    }
    catch [DeployError] { throw }
    catch { Say "Could not report step ${Step}: $($_.Exception.Message)" }
}

# Путь PowerShell → путь для .NET (в WinPE совпадают; нужен, чтобы cmdlet'ы и [IO.File] видели один файл).
function ProviderPath([string]$Path) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-FreeLetter {
    $used = (Get-Volume | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter }) + (Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
    foreach ($letter in 'W', 'S', 'T', 'R', 'Q', 'P', 'O', 'N', 'M') {
        if ($used -notcontains $letter) { return $letter }
    }
    throw [DeployError]::new('noDriveLetter', 'No free drive letter')
}

function Invoke-Tool([string]$File, [string[]]$Arguments, [string]$Reason) {
    Say "$File $($Arguments -join ' ')"
    & $File @Arguments | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw [DeployError]::new($Reason, "$File exited with code $LASTEXITCODE") }
}

# Скачивание образа на временный раздел с докачкой (Range) и отчётом о процентах.
function Save-Image([string]$Url, [string]$Path, [long]$Size) {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
    $buffer = New-Object byte[] (4MB)
    $failures = 0
    $lastReported = -1
    while ($true) {
        $have = if (Test-Path $Path) { (Get-Item $Path).Length } else { 0 }
        if ($have -gt $Size) { Remove-Item $Path; $have = 0 }
        if ($have -eq $Size) { break }
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$Server$Url")
        if ($have -gt 0) { $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new($have, $null) }
        $response = $null; $in = $null; $out = $null
        try {
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            if ($status -ge 400 -and $status -lt 500) { throw [DeployError]::new("download$status", "Server refused the image (HTTP $status)") }
            $response.EnsureSuccessStatusCode() | Out-Null
            if ($have -gt 0 -and $status -ne 206) { $have = 0 }
            $mode = if ($have -gt 0) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::Create }
            $out = [System.IO.File]::Open((ProviderPath $Path), $mode, [System.IO.FileAccess]::Write)
            $in = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            while (($read = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $read)
                $have += $read
                $percent = [int][Math]::Floor(100 * $have / $Size)
                if ($percent -ge $lastReported + 5) {
                    $lastReported = $percent
                    Say ('Downloaded {0:N1} of {1:N1} GiB ({2}%)' -f ($have / 1GB), ($Size / 1GB), $percent)
                    Report 'download' $percent
                }
            }
            $failures = 0
        }
        catch [DeployError] { throw }
        catch {
            $failures++
            if ($failures -ge 20) { throw [DeployError]::new('download', "Download keeps failing: $($_.Exception.Message)") }
            Say "Download interrupted ($($_.Exception.Message)), resuming in 10 s"
            Start-Sleep -Seconds 10
        }
        finally {
            if ($out) { $out.Dispose() }
            if ($in) { $in.Dispose() }
            if ($response) { $response.Dispose() }
        }
    }
    $client.Dispose()
}

# Генерализован ли образ: SysprepStatus\GeneralizationState = 7 после sysprep /generalize.
# [ГИПОТЕЗА: значение 7 — по опыту развёртываний, проверить на стенде на образе до и после sysprep.]
function Test-Generalized([string]$WindowsDrive) {
    $hive = "${WindowsDrive}:\Windows\System32\config\SYSTEM"
    & reg.exe load 'HKLM\ClubOffline' $hive | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    try {
        $state = (Get-ItemProperty -Path 'HKLM:\ClubOffline\Setup\Status\SysprepStatus' -ErrorAction SilentlyContinue).GeneralizationState
        if ($null -eq $state) { return $null }
        return [int]$state -eq 7
    }
    finally {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload 'HKLM\ClubOffline' | Out-Null
    }
}

try {
    Say "Club server: $Server"
    $macs = @(Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.PhysicalAdapter -and $_.MACAddress } | ForEach-Object { $_.MACAddress })
    $firmware = if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').PEFirmwareType -eq 2) { 'uefi' } else { 'bios' }
    $disks = @(Get-Disk | ForEach-Object {
        @{ number = $_.Number; serial = ([string]$_.SerialNumber).Trim(); model = [string]$_.FriendlyName; sizeBytes = [long]$_.Size; busType = [string]$_.BusType }
    })
    Say "MAC: $($macs -join ', '); firmware: $firmware; disks: $($disks.Count)"

    # Ждём сеть и сервер сколько угодно: без плана ничего не делаем.
    $plan = Invoke-Api POST '/deploy/v1/start' @{ macs = $macs; firmware = $firmware; disks = $disks } -Tries 1000
    $script:JobBase = "/deploy/v1/jobs/$($plan.jobId)"
    $number = [int]$plan.targetDisk.number
    Say "Seat $($plan.seat), name $($plan.computerName), image $($plan.image.label), attempt $($plan.attempt)"
    Say "Target disk $number : $($plan.targetDisk.model) $($plan.targetDisk.serial) $([Math]::Round($plan.targetDisk.sizeBytes / 1GB)) GiB"

    # 1. Разметка. Перед стиранием — ещё раз сверяем диск с планом сервера.
    $disk = Get-Disk -Number $number
    if (([string]$disk.SerialNumber).Trim() -ne [string]$plan.targetDisk.serial -or [long]$disk.Size -ne [long]$plan.targetDisk.sizeBytes) {
        throw [DeployError]::new('diskChanged', "Disk $number does not match the server plan: serial '$($disk.SerialNumber)' vs '$($plan.targetDisk.serial)', size $($disk.Size) vs $($plan.targetDisk.sizeBytes)")
    }
    Report 'partition'
    if ($disk.IsOffline) { Set-Disk -Number $number -IsOffline $false }
    if ($disk.IsReadOnly) { Set-Disk -Number $number -IsReadOnly $false }
    if ($disk.PartitionStyle -ne 'RAW') { Clear-Disk -Number $number -RemoveData -RemoveOEM -Confirm:$false }
    Initialize-Disk -Number $number -PartitionStyle GPT
    $system = New-Partition -DiskNumber $number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    Format-Volume -Partition $system -FileSystem FAT32 -NewFileSystemLabel 'System' -Confirm:$false | Out-Null
    $S = Get-FreeLetter; $system | Set-Partition -NewDriveLetter $S
    New-Partition -DiskNumber $number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
    $free = (Get-Disk -Number $number).LargestFreeExtent
    $windows = New-Partition -DiskNumber $number -Size ($free - [long]$plan.tempPartitionBytes - 16MB)
    Format-Volume -Partition $windows -FileSystem NTFS -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
    $W = Get-FreeLetter; $windows | Set-Partition -NewDriveLetter $W
    $temp = New-Partition -DiskNumber $number -UseMaximumSize
    Format-Volume -Partition $temp -FileSystem NTFS -NewFileSystemLabel 'ClubDeploy' -Confirm:$false | Out-Null
    $T = Get-FreeLetter; $temp | Set-Partition -NewDriveLetter $T

    # 2–3. Образ целиком на диск и проверка sha256 до применения.
    $wim = "${T}:\install.wim"
    Report 'download' 0
    Save-Image $plan.image.url $wim ([long]$plan.image.sizeBytes)
    Report 'verify'
    Say 'Verifying sha256...'
    $hash = (Get-FileHash -Path $wim -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne [string]$plan.image.sha256) {
        Remove-Item $wim
        throw [DeployError]::new('checksum', "Image sha256 mismatch: $hash")
    }

    # 4. Применение. Временный раздел потом удаляется, Windows занимает весь диск.
    Report 'apply'
    Invoke-Tool 'dism.exe' @('/Apply-Image', "/ImageFile:$wim", "/Index:$($plan.image.index)", "/ApplyDir:${W}:\") 'apply'
    Remove-Partition -DriveLetter $T -Confirm:$false
    Resize-Partition -DriveLetter $W -Size (Get-PartitionSupportedSize -DriveLetter $W).SizeMax

    # 5. Личность: имя компьютера в unattend (поверх unattend от sysprep), метка версии образа для помощника.
    Report 'identity'
    $unattendPath = "${W}:\Windows\Panther\unattend.xml"
    $existing = if (Test-Path $unattendPath) { Get-Content -Raw -Path $unattendPath -Encoding UTF8 } else { $null }
    $identity = Invoke-Api POST "$script:JobBase/unattend" @{ existing = $existing; generalized = (Test-Generalized $W) }
    New-Item -ItemType Directory -Force -Path "${W}:\Windows\Panther" | Out-Null
    [System.IO.File]::WriteAllText((ProviderPath $unattendPath), [string]$identity.xml, (New-Object System.Text.UTF8Encoding($false)))
    New-Item -ItemType Directory -Force -Path "${W}:\ProgramData\ClubDiskless" | Out-Null
    [System.IO.File]::WriteAllText((ProviderPath "${W}:\ProgramData\ClubDiskless\image.json"), [string]$plan.marker, (New-Object System.Text.UTF8Encoding($false)))

    # 6. Загрузчик. Только после него сервер снимает PXE-флаг.
    Report 'bcdboot'
    Invoke-Tool 'bcdboot.exe' @("${W}:\Windows", '/s', "${S}:", '/f', 'UEFI') 'bcdboot'

    # 7. Сервер снимает флаг и убирает класс из Kea; перезагружаемся только после его «да».
    Invoke-Api POST "$script:JobBase/complete" -Tries 100000 | Out-Null
    Say 'Done. Rebooting into Windows.'
    Start-Sleep -Seconds 5
    & wpeutil.exe reboot
}
catch {
    $reason = if ($_.Exception -is [DeployError]) { $_.Exception.Reason } else { 'error' }
    $message = $_.Exception.Message
    Write-Host ''
    Write-Host "FAILED at step $script:Step ($reason): $message" -ForegroundColor Red
    if ($script:JobBase -and $reason -ne 'notDeploying') {
        try { Invoke-Api POST "$script:JobBase/fail" @{ step = $script:Step; message = "${reason}: $message" } -Tries 3 | Out-Null } catch { }
    }
    Write-Host 'The PC stays in reinstall mode: after a reboot the next attempt starts from scratch.'
    Write-Host 'Details are in the club panel. Press Enter to reboot, or power the PC off.'
    [void](Read-Host)
    & wpeutil.exe reboot
}
