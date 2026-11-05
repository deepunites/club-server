# Player saves test (helper 1.7.0) before the shell does it: sends one command to the helper pipe
# \\.\pipe\club-diskless-helper, the same JSON line the shell agent sends (club-contracts docs/SHELL_CHANGES.md, item 23),
# and prints the answer line as is. Spec: docs/superpowers/specs/2026-11-05-player-saves-design.md (4.1, 7).
# PowerShell as Administrator (the pipe is SYSTEM and Administrators only). By network:
#   irm http://192.168.1.201:5080/pxe/v1/files/tools/saves-test.ps1 | iex                         (ping)
#   & ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/saves-test.ps1))) attach <playerId>
#   & ([scriptblock]::Create((irm http://192.168.1.201:5080/pxe/v1/files/tools/saves-test.ps1))) detach <playerId>
# From a file: powershell -NoProfile -ExecutionPolicy Bypass -File saves-test.ps1 -Op attach -PlayerId <id> [-Sid <sid>]
# Keys: -Op ping|attach|detach (or the first bare word; default ping), -PlayerId <id> (or the second bare word; needed
# for attach and detach), -Sid <sid> (attach only; default: SID of the local user "player", the kiosk account).
# The answer is awaited as long as the shell waits for it: ping 10 s, attach 60 s, detach 120 s. On a timeout the helper
# still finishes the command (see its log: Application, source ClubDisklessHelper).
# Runs in the console scope under irm | iex: no param() (it does not work under iex), keys come from $args, exit only
# when run as a file (-File). Output is ASCII only, no BOM, no #Requires (irm in PowerShell 5.1 reads octet-stream as
# Latin-1). Compatible with Windows PowerShell 5.1.
$scriptArgs = @($args)
$savedErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Stop'
$exitCode = 1
try {
    $PipeName = 'club-diskless-helper'
    $ConnectTimeoutMs = 5000
    $Ops = @{ 'ping' = 'ping'; 'attach' = 'profile.attach'; 'detach' = 'profile.detach' }
    $AnswerTimeoutSec = @{ 'ping' = 10; 'attach' = 60; 'detach' = 120 }
    $Usage = 'Usage: saves-test.ps1 [-Op] ping|attach|detach [-PlayerId] <playerId> [-Sid <sid>]'

    $named = @{}
    $words = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $scriptArgs.Count; $i++) {
        $arg = "$($scriptArgs[$i])"
        if ($arg -match '^-(Op|PlayerId|Sid)$') {
            if ($i + 1 -ge $scriptArgs.Count) { throw "No value after $arg. $Usage" }
            $named[$Matches[1]] = "$($scriptArgs[$i + 1])"
            $i++
        } elseif ($arg -like '-*') {
            throw "Unknown key $arg. $Usage"
        } else {
            [void]$words.Add($arg)
        }
    }
    # Bare words fill Op, then PlayerId, whichever is not given by its key.
    $positional = @(@('Op', 'PlayerId') | Where-Object { -not $named.ContainsKey($_) })
    if ($words.Count -gt $positional.Count) { throw "Too many arguments: $($words -join ' '). $Usage" }
    for ($i = 0; $i -lt $words.Count; $i++) { $named[$positional[$i]] = $words[$i] }
    $op = if ($named.ContainsKey('Op')) { $named['Op'].ToLowerInvariant() } else { 'ping' }
    $playerId = $named['PlayerId']
    if (-not $Ops.ContainsKey($op)) { throw "Unknown op '$op'. $Usage" }

    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run PowerShell as Administrator (the helper pipe is SYSTEM and Administrators only).'
    }

    $request = [ordered]@{ op = $Ops[$op] }
    if ($op -ne 'ping') {
        if ([string]::IsNullOrWhiteSpace($playerId)) { throw "$op needs a player ID. $Usage" }
        $request['playerId'] = $playerId
    }
    if ($op -eq 'attach') {
        $sid = $named['Sid']
        if ([string]::IsNullOrWhiteSpace($sid)) {
            try { $sid = (Get-LocalUser -Name 'player').SID.Value }
            catch { throw "No local user 'player' to take the SID from ($($_.Exception.Message)). Pass -Sid <sid>." }
        }
        $request['windowsUserSid'] = $sid
    }
    $line = $request | ConvertTo-Json -Compress
    Write-Host "Request: $line"

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        try { $pipe.Connect($ConnectTimeoutMs) }
        catch {
            $base = $_.Exception.GetBaseException()
            if ($base -is [TimeoutException]) {
                throw "No pipe \\.\pipe\$PipeName within $($ConnectTimeoutMs / 1000) s: is ClubDisklessHelper 1.7.0 or newer running? (Get-Service ClubDisklessHelper)"
            }
            throw "Pipe \\.\pipe\$PipeName not opened: $($base.GetType().Name): $($base.Message)"
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($line + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $reader = New-Object System.IO.StreamReader($pipe, (New-Object System.Text.UTF8Encoding($false)))
        $read = $reader.ReadLineAsync()
        $timeoutSec = $AnswerTimeoutSec[$op]
        if (-not $read.Wait($timeoutSec * 1000)) {
            Write-Host ("TIMEOUT: no answer within {0} s (the shell gives up here); the helper still finishes the command, see its log" -f $timeoutSec) -ForegroundColor Red
        } else {
            $answer = $read.Result
            $elapsed = $watch.Elapsed.TotalSeconds
            if ($null -eq $answer) {
                Write-Host ('FAIL: the helper closed the pipe without an answer after {0:N1} s' -f $elapsed) -ForegroundColor Red
            } else {
                Write-Host 'Answer:'
                Write-Host $answer
                $parsed = $null
                try { $parsed = $answer | ConvertFrom-Json } catch { $parsed = $null }
                if ($null -ne $parsed -and $parsed.ok -eq $true) {
                    $exitCode = 0
                    Write-Host ('OK in {0:N1} s' -f $elapsed) -ForegroundColor Green
                } elseif ($null -ne $parsed) {
                    Write-Host ('FAIL {0} in {1:N1} s' -f $parsed.error, $elapsed) -ForegroundColor Red
                } else {
                    Write-Host ('FAIL: the answer is not JSON ({0:N1} s)' -f $elapsed) -ForegroundColor Red
                }
            }
        }
    } finally {
        $pipe.Dispose()
    }
} finally {
    $ErrorActionPreference = $savedErrorAction
}
if ($PSCommandPath) { exit $exitCode }
