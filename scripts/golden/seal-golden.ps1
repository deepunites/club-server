#Requires -Version 5.1
<#
.SYNOPSIS
    Последний шаг сеанса мастера перед публикацией эталона: убирает из образа регистрацию этого ПК у помощника
    бездиска и у ClubShell, проверяет эталон с шеллом и выключает ПК.

.DESCRIPTION
    Запускать в эталоне в режиме мастера, PowerShell от администратора, когда всё остальное готово
    (prepare-golden.ps1 без WARN, перезагрузка прошла; с шеллом — ещё get-shell-kit в режиме эталона: ClubShell 1.0.20,
    помощник 1.9.0 или новее, киоск включён). После выключения — панель «Бездиск» → «Опубликовать».
    С ClubShell 1.0.20 из учётки администратора на мастере не выходить: агент ставит в Winlogon ForceAutoLogon=1,
    и после выхода Windows сразу входит в player и запускает оболочку (её данные в профиле — стоп ниже). Закончить
    сеанс — этим скриптом (он выключает ПК) или перезагрузкой с Shift при загрузке.

    Зачем: помощник на мастере регистрируется на сервере и хранит в %ProgramData%\ClubDiskless токены этого ПК
    (credentials.bin, DPAPI машины — его расшифрует любое место с этим образом) и назначения с CHAP-секретами личных
    дисков (assignment.json, у H: — assignment.json.solo). В опубликованном образе они достались бы всем местам, а сам ПК
    мастера при каждой загрузке предъявлял бы уже использованный токен и терял регистрацию. Места с другим HWID эти файлы
    не используют, но читать их могут. Также удаляются папки сохранений игрока (1.7.0: saves-baseline, saves-tmp,
    saves-carry) — исходное содержимое папок G:/H: и файлы игроков этого ПК.
    Настройки (helper.json, club-ca.pem) остаются: по ним каждое место регистрируется само.
    Самообновление помощника (1.8.0) должно быть закончено: после остановки службы есть update.json,
    ClubDisklessHelper.exe.new или .failed — стоп, регистрация не удаляется, ПК не выключается (дождаться «готово» в
    панели или поставить помощник вручную); .old без update.json и update-failed.json удаляются. Скрипт печатает версию
    и SHA256 помощника и проверяет, что служба после остановки не запустилась снова (сбой при остановке — Windows
    перезапустила бы её по действиям при сбое).

    ClubShell — только если есть служба ClubShellAgent (без неё раздел пропускается, как раньше). Агент мастера тоже
    зарегистрирован, и места унаследовали бы: agent-identity.json, secure\agent.tokens (токены и секрет подписи), hwid.fallback,
    kiosk.cred, офлайн-базу (игроки, неотправленные сеансы), кэши сервера, журналы, а в agent.json — pcId, pcName и
    zone мастера (PolicyStore, SessionManager и SessionLock берут pcId из agent.json раньше, чем из токена: место до
    своей регистрации, а без интернета всегда, было бы мастером). Очистка — та же, что у prepare-image.ps1 из club-shell
    v1.0.20 (его здесь не запускаем; как и он, стоп на cache\profile-dirty.marker — SHELL_CHANGES 50), но:
      - ключ ПК остаётся в secure\club.key (DPAPI машины). Эталон без sysprep: ключи DPAPI машины у мест те же, что у
        мастера (как у credentials.bin помощника), и ключ расшифруется на каждом месте. prepare-image вернул бы его
        открытым текстом в agent.json, а этот файл после перезаписи агентом читает группа Users, — в образ, снимки img-*
        и резервные копии. -PlainKey — запасной путь, если на местах club.key не расшифруется (в журнале агента «No club
        key»): ключ — в agent.json, но с правами только SYSTEM и Administrators, secure\club.key удаляется; агент места
        при первом старте сам спрячет ключ в свой club.key;
      - pcId, pcName и zone убираются из agent.json (prepare-image их оставляет);
      - есть cache\profile-reset-reboot.txt (агент 1.0.20) — стоп: prepare-image его оставляет (SHELL_CHANGES 89), а
        места унаследовали бы «перезагрузка ради долга сброса профиля уже была»;
      - USBSTOR Start — по политике кассы в кэше (policies.json, usb.allowStorage: false — 4, true — 3), а не 3, как у
        prepare-image: у клона окно до агента бывает только при первой загрузке (дальше 4 остаётся на диске), а у мест
        C: сбрасывается при каждой загрузке — со Start=3 USB-диск, вставленный до загрузки, подключался бы раньше
        агента на каждом сеансе (агент извлекает только съёмные тома, USB HDD/SSD — «несъёмные»). Запрет HID из
        политики снимается, как у prepare-image: клавиатура и мышь места ставятся при каждой загрузке.
    Порядок: сначала проверки без изменений — имя USER, версия 1.0.20, помощник 1.9.0 или новее, player не вошёл,
    нет profile-dirty.marker и profile-reset-reboot.txt, оболочка на мастере не запускалась, мастер одобрен в кассе,
    ключ ПК на месте. Любой WARN здесь — стоп: ничего не изменено, ПК не выключается. Потом очистка помощника (как
    раньше) и ClubShell и проверки эталона с шеллом: agent.json (поля
    shell-install.ps1), admin-pin, ключ обновлений, политика кассы в кэше, учётка player и её профиль, оболочка в
    профиле player и в шаблоне Default, автовход в player без пароля в реестре, пароль администратора эталона и что он
    не учётка Microsoft, других включённых администраторов и учёток с пустым паролем нет (каждая включённая учётка —
    на экране входа всех мест), встроенный Administrator выключен, автозапуск выключен, USBSTOR по политике кассы,
    схемы питания. С шеллом и без: обновление Edge/WebView2 (службы edgeupdate, edgeupdatem, задания
    MicrosoftEdgeUpdate*) выключается снова, как в fixes-v2.ps1 — install-webview2.ps1 обновляет клиент EdgeUpdate, и
    тот заново включает свои задания; места качали бы обновления при каждой загрузке. Агент в начале каждого
    сеанса включает «Высокую производительность»: у неё (и у «Максимальной») сон «никогда» и PCIe ASPM выкл. ставятся
    здесь же, у текущей схемы — только проверяются (их ставит prepare-golden.ps1). В самом конце, если WARN нет,
    удаляется C:\ClubShellKit (установщик 72 МБ, журналы, снимки DNS, копия шаблона профиля): с WARN он нужен, чтобы
    запустить этот скрипт снова.

    Мастер между этим скриптом и публикацией не загружать: помощник и агент снова зарегистрируются и попадут в образ,
    а с шеллом мастер ещё и войдёт в киоск player. Пришлось загрузить — Shift при загрузке, вход администратором
    эталона, команда get-shell-kit (вернёт набор в C:\ClubShellKit; вопрос про киоск — n), потом этот скрипт снова.

.PARAMETER AdminUser
    Администратор эталона (по умолчанию — тот, кто запустил скрипт): включён, в Administrators, пароль не пустой.

.PARAMETER PlainKey
    Ключ ПК — в agent.json (только SYSTEM и Administrators), secure\club.key удаляется. Только если на местах
    secure\club.key не расшифровался.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\ClubShellKit\seal-golden.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File E:\club\seal-golden.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoShutdown,
    [switch]$Force,
    [string]$AdminUser = $env:USERNAME,
    [switch]$PlainKey
)

$ErrorActionPreference = 'Continue'
$ServiceName = 'ClubDisklessHelper'
$StateDir = "$env:ProgramData\ClubDiskless"
$StateFiles = 'credentials.bin', 'credentials.bin.tmp', 'assignment.json', 'assignment.json.tmp', 'assignment.json.solo', 'assignment.json.solo.tmp'
# Сохранения игрока (помощник 1.7.0): исходное содержимое папок G:/H:, архив detach, файлы игрока для пропущенных
# папок — у каждого места свои.
$StateDirs = 'saves-baseline', 'saves-tmp', 'saves-carry'
# Самообновление (помощник 1.8.0, docs/helper.md, «Самообновление»): .new, .old, .failed — рядом с exe; update.json,
# update-failed.json, helper-build.json — в $StateDir.
$HelperExe = "$env:ProgramFiles\ClubDiskless\ClubDisklessHelper.exe"
# Сборки, известные без helper-build.json: SHA256 exe -> версия (как в scripts/shell/shell-preflight.ps1, менять вместе).
$HelperBuilds = @{
    '26635a5f7aab4d77441d0b0138064b9c7f64bb1daf4b0bcaf09052439ddd0148' = '1.6.0'   # 1.6.0+13609a5
    '41da96a0b0721b22bb5f5e1898c927f08c4856968ee059a0c40b3d2b54a30e23' = '1.7.0'   # 1.7.0+f79e0d5
    '05a73a8f0cb1717040c7f64fe1c94104fbdf501b9b196f90659c56d0c33b31e1' = '1.8.0'   # 1.8.0+e77f0ad
    '1af4465530dd2d17fa287f9d90f11f0250262940193d1dfc88931e66e4c9ac56' = '1.9.0'   # 1.9.0+184aabf
}
# Помощник эталона с ClubShell — не старше этой версии: шелл 1.0.20 узнаёт готовность ПК (диски игр) из ping помощника
# 1.9.0 (SHELL_CHANGES 86); со старым — догадка по корням библиотек, а с 1.6.0 (канала нет) ещё и ожидание сохранений
# по 60 с при каждом входе. Тот же минимум — у shell-preflight.ps1 и shell-upgrade.ps1.
$HelperMinVersion = [version]'1.9.0'

# ClubShell. Адрес сервера и поля agent.json — те же, что пишет shell-install.ps1 (менять вместе).
$ShellService = 'ClubShellAgent'
$ShellVersion = '1.0.20'
$ShellVersionPattern = '^1\.0\.20(\D|$)'
$ShellData = "$env:ProgramData\ClubShell"
$ShellAgentDir = "$env:ProgramFiles\ClubShell\Agent"
# Ровно та строка, что агент и seal-default-profile.ps1 пишут в профиль (Shell).
$ShellExe = 'C:\Program Files\ClubShell\Shell\clubshell-shell.exe'
$ShellOrigin = 'https://clubshell-server-production.up.railway.app'
$KioskUser = 'player'
$KitDir = 'C:\ClubShellKit'
$AgentJson = Join-Path $ShellData 'agent.json'
$ClubKeyFile = Join-Path $ShellData 'secure\club.key'
$KeyEntropy = 'ClubShell.Agent.tokens.v1'   # DPAPI LocalMachine агента (TokenStore), как в prepare-image.ps1
$KeyPattern = '^[A-Za-z0-9._-]{8,128}$'
# Списки prepare-image.ps1 v1.0.20 (строки 110-114, 128, 135), без secure\club.key. Новая версия ClubShell — сначала
# сверить с её prepare-image.ps1.
$ShellStateFiles = @('agent-identity.json', 'secure\agent.tokens', 'secure\hwid.fallback', 'secure\shell.token', 'secure\kiosk.cred',
    'cache\offline.db', 'cache\offline.db-wal', 'cache\offline.db-shm', 'crash-history.json', 'cache\power-acks.json')
$ShellEmptyDirs = @('pending-update', 'cache\updates', 'cache\saves', 'cache\player-settings', 'cache\profile-keep', 'logs')
$ShellHiveKeys = [ordered]@{ Winlogon = 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon'; Policies = 'Software\Microsoft\Windows\CurrentVersion\Policies\System' }
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
# Группы, членов которых агент не берёт в киоск (как в shell-preflight.ps1).
$PrivilegedGroupSids = @('S-1-5-32-544', 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551', 'S-1-5-32-547', 'S-1-5-32-556', 'S-1-5-32-578')
# Users, Authenticated Users, Everyone, Interactive.
$UserishSids = @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4')
# Агент ClubShell в начале каждого сеанса включает «Высокую производительность», если сейчас не она и не «Максимальная».
$HighPlans = [ordered]@{ '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' = '«Высокая производительность»'; 'e9a42b02-d5df-448d-aa00-03f14749eb61' = '«Максимальная производительность»' }

$script:WarnCount = 0
function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Line([string]$Text) { Write-Host "    $Text" }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text, [string]$Hint) {
    $script:WarnCount++
    Write-Host "WARN  $Text" -ForegroundColor Yellow
    if ($Hint) { Write-Host "      -> $Hint" -ForegroundColor Yellow }
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { $null }
}

# Версия помощника по SHA256 его exe: helper-build.json ({"v":1,"version":"x.y.z","build":"<sha256>"}, пишет сама служба
# 1.8.0+ при каждом старте) — только если build = SHA256 exe; иначе таблица $HelperBuilds; иначе $null. Версию файла exe
# не читаем: exe собирается на Linux, и ресурс версии в нём — от среды .NET (10.0.x), а не от помощника.
function Get-HelperVersion([string]$Hash) {
    if (-not $Hash) { return $null }
    $sha = $Hash.ToLowerInvariant()
    try {
        $info = [IO.File]::ReadAllText((Join-Path $StateDir 'helper-build.json')) | ConvertFrom-Json
        if ($info.v -eq 1 -and "$($info.build)".ToLowerInvariant() -eq $sha -and "$($info.version)" -match '^\d+\.\d+\.\d+$') { return "$($info.version)" }
    } catch { }
    if ($HelperBuilds.ContainsKey($sha)) { return $HelperBuilds[$sha] }
    $null
}

function Get-NativeOutput([string]$File, [string[]]$Arguments) {
    ((& $File @Arguments 2>&1) | ForEach-Object { "$_" }) -join "`n"
}

# SID членов локальной группы. Get-LocalGroupMember падает на «осиротевших» SID — тогда ADSI (как в наборе шелла).
function Get-GroupMemberSids([string]$GroupSid) {
    try {
        return @(Get-LocalGroupMember -SID $GroupSid -ErrorAction Stop | ForEach-Object { $_.SID.Value })
    } catch { }
    $sids = @()
    try {
        $name = ((New-Object Security.Principal.SecurityIdentifier($GroupSid)).Translate([Security.Principal.NTAccount]).Value -split '\\')[-1]
        $group = [ADSI]"WinNT://./$name,group"
        foreach ($member in @($group.Invoke('Members'))) {
            $bytes = $member.GetType().InvokeMember('objectSid', 'GetProperty', $null, $member, $null)
            $sids += (New-Object Security.Principal.SecurityIdentifier($bytes, 0)).Value
        }
    } catch { }
    $sids
}

# Пустой ли пароль — входом с пустым паролем (LogonUser, сетевой вход), как в shell-preflight.ps1: 1326 — пароль есть.
if (-not ('ClubSealNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClubSealNative
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // 0 when the empty password logs on (network logon, LOGON32_LOGON_NETWORK = 3), else the Win32 error of LogonUser.
    public static int TryEmptyPassword(string user)
    {
        IntPtr token;
        if (LogonUser(user, ".", "", 3, 0, out token)) { CloseHandle(token); return 0; }
        return Marshal.GetLastWin32Error();
    }
}
'@
}

function Get-PasswordState([string]$User) {
    $code = [ClubSealNative]::TryEmptyPassword($User)
    if ($code -eq 1326) { return 'set' }
    if (@(0, 1327, 1385, 1330, 1907) -contains $code) { return 'blank' }
    if ($code -eq 1909) { return 'locked' }
    "unknown (Win32 error $code)"
}

# agent.json, policies.json и строки журнала — сериализатором, а не ConvertFrom-Json: ключи, различающиеся только
# регистром, ConvertFrom-Json в 5.1 отвергает, а словарь сериализатора различает регистр (как в kiosk-on.ps1).
Add-Type -AssemblyName System.Web.Extensions
$Json = New-Object System.Web.Script.Serialization.JavaScriptSerializer

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { $data = $Json.DeserializeObject([IO.File]::ReadAllText($Path)) } catch { return $null }
    if ($data -is [System.Collections.IDictionary]) { return $data }
    $null
}

# Ключ словаря без учёта регистра (как SettingsJson.FindKey у агента).
function Find-JsonKey($Map, [string]$Name) {
    if ($Map -isnot [System.Collections.IDictionary]) { return $null }
    foreach ($key in @($Map.Keys)) {
        if ([string]::Equals([string]$key, $Name, [StringComparison]::OrdinalIgnoreCase)) { return [string]$key }
    }
    $null
}

# Значение по пути ключей; массив возвращается целиком (запятая — PowerShell не разворачивает его в конвейер).
function Get-JsonValue($Map, [string[]]$Keys) {
    $node = $Map
    foreach ($name in $Keys) {
        $key = Find-JsonKey $node $name
        if ($null -eq $key) { return $null }
        $node = $node[$key]
    }
    , $node
}

# USBSTOR Start эталона — по политике кассы в кэше (policies.json, usb.allowStorage): true — 3; false, нет политики или
# поля — 4 (как у политики установщика). C: мест сбрасывается при каждой загрузке: значение из образа действует с самого
# старта Windows, до агента.
function Get-GoldenUsbStart {
    $allow = Get-JsonValue (Read-JsonFile (Join-Path $ShellData 'policies.json')) 'usb', 'allowStorage'
    if ($allow -is [bool] -and $allow) { return @{ Start = 3; Why = 'usb.allowStorage=true в политике кассы' } }
    if ($allow -is [bool]) { return @{ Start = 4; Why = 'usb.allowStorage=false в политике кассы' } }
    @{ Start = 4; Why = 'usb.allowStorage в кэше политики нет — как у политики установщика' }
}

# agent.json — как пишет его агент (SettingsLoader.WriteAtomicAsync): UTF-8 без BOM во временный файл рядом, потом на
# место. $AdminOnly — файл сразу с правами только SYSTEM и Administrators (ключ ни на миг не читается Users), как в
# shell-install.ps1; иначе права наследуются от папки, как у файла, который пишет агент.
function Write-JsonAtomic([string]$Path, $Data, [bool]$AdminOnly) {
    $bytes = $null
    $temp = "$Path.seal.tmp"
    try {
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($Json.Serialize($Data))
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        if ($AdminOnly) {
            $security = New-Object System.Security.AccessControl.FileSecurity
            $security.SetSecurityDescriptorSddlForm('D:PAI(A;;FA;;;SY)(A;;FA;;;BA)')
            try { $stream = [IO.File]::Create($temp, 4096, [IO.FileOptions]::None, $security) }
            catch {
                # Такой перегрузки нет в .NET Core (PowerShell 7): пустой файл, его права, потом содержимое (как в shell-install.ps1).
                [IO.File]::WriteAllBytes($temp, [byte[]]@())
                Set-Acl -LiteralPath $temp -AclObject $security -ErrorAction Stop
                $stream = [IO.File]::Open($temp, [IO.FileMode]::Truncate, [IO.FileAccess]::Write)
            }
        } else {
            $stream = [IO.File]::Create($temp)
        }
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        Move-Item -LiteralPath $temp -Destination $Path -Force -ErrorAction Stop
        return $true
    } catch {
        Write-Warn "не записан ${Path}: $($_.Exception.Message)"
        return $false
    } finally {
        if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

# Есть ли у Users (Authenticated Users, Everyone, Interactive) разрешающая запись в ACL; ACL не читается — «есть».
function Test-UserishAccess([string]$Path) {
    try { $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop } catch { return $true }
    foreach ($ace in $acl.Access) {
        if ("$($ace.AccessControlType)" -ne 'Allow') { continue }
        $sid = $null
        try { $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if ($UserishSids -contains $sid) { return $true }
    }
    $false
}

# Ключ ПК из secure\club.key (DPAPI машины, энтропия агента). Ключ не печатается; State: ok, missing, broken.
function Read-ClubKeyFile {
    if (-not (Test-Path -LiteralPath $ClubKeyFile)) { return @{ State = 'missing'; Key = $null } }
    $key = $null
    try {
        Add-Type -AssemblyName System.Security
        $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($ClubKeyFile),
            [Text.Encoding]::UTF8.GetBytes($KeyEntropy), [Security.Cryptography.DataProtectionScope]::LocalMachine)
        try { $key = [Text.Encoding]::UTF8.GetString($plain) } finally { [Array]::Clear($plain, 0, $plain.Length) }
    } catch { return @{ State = 'broken'; Key = $null } }
    if ($key -notmatch $KeyPattern) { return @{ State = 'broken'; Key = $null } }
    @{ State = 'ok'; Key = $key }
}

# Регистрация по журналам агента, как в kiosk-on.ps1 (три последних agent-*.json, Serilog compact JSON: шаблон — "@mt",
# исключение — "@x"); решает последняя подходящая строка. Строки журнала не печатаются.
function Get-ShellRegistration {
    $state = @{ State = 'none'; Detail = '' }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $ShellData 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 3)
    foreach ($log in $logs) {
        foreach ($line in @(Get-Content -LiteralPath $log.FullName -Tail 3000 -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            if ($line -notmatch '"@mt"\s*:\s*"(Registered as PC|Stored agent tokens refreshed|Registration attempt)') { continue }
            try { $entry = $Json.DeserializeObject($line) } catch { continue }
            if ($entry -isnot [System.Collections.IDictionary]) { continue }
            $template = [string]$entry['@mt']
            if ($template -like 'Registered as PC*' -or $template -like 'Stored agent tokens refreshed*') { $state.State = 'registered'; $state.Detail = '' }
            elseif ($template -like 'Registration attempt*failed*') {
                $exception = [string]$entry['@x']
                if ($exception -match 'waiting for approval|pendingApproval') { $state.State = 'waiting'; $state.Detail = '' }
                elseif ($exception -match 'X-Club-Key') { $state.State = 'badKey'; $state.Detail = '' }
                else {
                    $first = (($exception -split "`n")[0]).Trim()
                    if ($first.Length -gt 160) { $first = $first.Substring(0, 160) }
                    $state.State = 'failing'; $state.Detail = $first
                }
            }
        }
    }
    $state
}

# Значения Shell в кусте профиля (NTUSER.DAT): загрузить под своим именем, прочитать, выгрузить с повторами (как
# seal-default-profile.ps1: куст держат и чужие процессы). Не загрузился — $null.
function Get-HiveShellValues([string]$HiveFile, [string]$Mount) {
    $users = [Microsoft.Win32.Registry]::Users
    $probe = $users.OpenSubKey($Mount)
    if ($probe) { $probe.Dispose(); & reg.exe unload "HKU\$Mount" 2>&1 | Out-Null }
    & reg.exe load "HKU\$Mount" $HiveFile 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    $values = @{}
    try {
        foreach ($name in @($ShellHiveKeys.Keys)) {
            $values[$name] = $null
            $key = $users.OpenSubKey("$Mount\$($ShellHiveKeys[$name])")
            if ($key) { try { $values[$name] = $key.GetValue('Shell') } finally { $key.Dispose() } }
        }
    } finally {
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        $unloaded = $false
        for ($i = 0; $i -lt 10 -and -not $unloaded; $i++) {
            & reg.exe unload "HKU\$Mount" 2>&1 | Out-Null
            $code = $LASTEXITCODE
            $probe = $users.OpenSubKey($Mount)
            if ($probe) { $probe.Dispose() }
            if ($code -eq 0 -or -not $probe) { $unloaded = $true } else { Start-Sleep -Seconds 1 }
        }
        if (-not $unloaded) { Write-Warn "куст HKU\$Mount не выгрузился" 'запустить этот скрипт снова (он сначала выгрузит его)' }
    }
    $values
}

# Индексы настройки схемы питания: последние два числа 0x… в выводе powercfg /query — от сети и от батареи (как в
# prepare-golden.ps1; вывод локализован, числа — нет).
function Get-PowerIndex([string]$Scheme, [string]$SubGroup, [string]$Setting) {
    $text = Get-NativeOutput 'powercfg.exe' @('/query', $Scheme, $SubGroup, $Setting)
    $values = @([regex]::Matches($text, '0x([0-9a-fA-F]{8})') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value, 16) })
    if ($values.Count -lt 2) { return $null }
    @{ AC = $values[$values.Count - 2]; DC = $values[$values.Count - 1] }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

Write-Title 'Это сеанс мастера?'
$master = @(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':diskless-master$' })
if ($master) { Write-Pass "загрузка с мастер-тома ($($master[0].TargetNodeAddress))" }
elseif ($Force) { Write-Warn 'сессии diskless-master нет — продолжаю из-за -Force' }
else { throw 'Этот ПК загружен не с мастер-тома (нет iSCSI-сессии …:diskless-master). Скрипт — только для сеанса мастера (обойти: -Force).' }

# Мастер-том G: — club-master, H: — club-master-solo (у каждой библиотеки свой).
if (@(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object { $_.TargetNodeAddress -match ':club-master(-[a-z0-9]+)?$' -and $_.IsConnected }).Count -gt 0) {
    Write-Warn 'открыта правка библиотеки (таргет club-master или club-master-solo): в панели «Закончить правку» этой библиотеки, потом снова этот скрипт'
}
# Вход в лаунчеры под аккаунтами клуба остался бы в образе и библиотеке — игроки получили бы эти аккаунты.
Write-Line 'Перед публикацией: выйдите из аккаунтов клуба в Steam, Epic, Battle.net, Ubisoft, Rockstar, Lesta (и закройте их).'

# ======================================================================================================================
# ClubShell: проверки до изменений. Любой WARN здесь — стоп до очистки помощника и ClubShell.
# ======================================================================================================================
$shellSvc = Get-Service -Name $ShellService -ErrorAction SilentlyContinue
$playerAccount = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
$playerProfile = $null
if ($playerAccount) { $playerProfile = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$($playerAccount.SID.Value)'" -ErrorAction SilentlyContinue }
if ($shellSvc) {
    Write-Title 'ClubShell: проверки до очистки (WARN здесь — стоп, ничего не изменено)'
    $gateStart = $script:WarnCount

    $pendingName = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
    if ($env:COMPUTERNAME -ne 'USER') {
        Write-Warn "имя Windows $env:COMPUTERNAME, а у эталона — USER (с ним грузится каждое место; номера мест — в кассе)" 'Rename-Computer USER, перезагрузка (Shift при загрузке), снова этот скрипт'
    } elseif ($pendingName -and $pendingName -ne $env:COMPUTERNAME) {
        Write-Warn "переименование в $pendingName ждёт перезагрузки" 'вернуть имя: Rename-Computer USER, перезагрузка (Shift при загрузке), снова этот скрипт'
    } else {
        Write-Pass 'имя Windows USER'
    }

    $agentExe = $null
    $agentVersion = $null
    $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ShellService'" -ErrorAction SilentlyContinue
    if ($serviceInfo) { $agentExe = ($serviceInfo.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim() }
    if ($agentExe -and (Test-Path -LiteralPath $agentExe)) { $agentVersion = "$((Get-Item -LiteralPath $agentExe).VersionInfo.ProductVersion)" }
    if ($agentVersion -match $ShellVersionPattern) { Write-Pass "ClubShellAgent $agentVersion" }
    elseif ($agentVersion -match '^(\d+\.\d+\.\d+)' -and [version]$Matches[1] -lt [version]$ShellVersion) {
        Write-Warn "ClubShellAgent $agentVersion старше $ShellVersion ($agentExe)" ('обновить ClubShell в этом ' +
            "сеансе мастера: команда get-shell-kit (предложит обновление до $ShellVersion), потом снова этот скрипт")
    }
    else { Write-Warn "ClubShellAgent версии '$agentVersion' ($agentExe), а очистка ниже — по prepare-image.ps1 v$ShellVersion" 'сначала сверить списки этого скрипта с tools/scripts/prepare-image.ps1 новой версии (Claude)' }

    # Помощник — до остановки службы и удаления регистрации: старше 1.9.0 (или неизвестная сборка) — стоп, обновить его
    # можно сразу, в этом же сеансе мастера.
    if (Test-Path -LiteralPath $HelperExe) {
        $gateHelperSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $HelperExe).Hash.ToLowerInvariant()
        $gateHelperVersion = Get-HelperVersion $gateHelperSha
        if ($gateHelperVersion -and [version]$gateHelperVersion -ge $HelperMinVersion) {
            Write-Pass "помощник $gateHelperVersion (для ClubShell $ShellVersion нужен $HelperMinVersion или новее)"
        } else {
            $shownVersion = "неизвестной сборки (SHA256 $gateHelperSha)"
            if ($gateHelperVersion) { $shownVersion = $gateHelperVersion }
            $helperHint = ('обновить помощник до 1.9.0 в этом сеансе мастера: кнопкой в панели (карточка ' +
                '«Обновление помощника», с 1.8.x) или ' +
                'irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex; потом снова этот скрипт')
            Write-Warn "помощник $shownVersion, а нужен $HelperMinVersion или новее (ClubShell $ShellVersion)" $helperHint
        }
    } else {
        Write-Warn "нет $HelperExe — на местах помощника не будет" 'install-helper.ps1 (1.9.0), потом снова этот скрипт'
    }

    # Вошедший player — его профиль и данные оболочки ушли бы в образ.
    $playerProcs = @(Get-Process -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { "$($_.UserName)" -like "*\$KioskUser" })
    $shellProcs = @(Get-Process -Name 'clubshell-shell' -ErrorAction SilentlyContinue)
    if ($playerProcs.Count -gt 0) { Write-Warn ("{0} вошёл в Windows (процессов: {1})" -f $KioskUser, $playerProcs.Count) "Диспетчер задач → «Пользователи» → $KioskUser → «Выйти», потом снова этот скрипт" }
    elseif ($shellProcs.Count -gt 0) { Write-Warn 'запущена оболочка clubshell-shell.exe' 'выйти из сеанса, где она идёт, потом снова этот скрипт' }
    else { Write-Pass "$KioskUser не вошёл, оболочка не запущена" }

    # profile-dirty.marker: профиль прошлого игрока ещё не сброшен. prepare-image v1.0.20 на нём тоже останавливается
    # (SHELL_CHANGES 50; v1.0.18 стирал молча), а в образе он заставил бы каждое место сбрасывать профиль при каждой
    # загрузке.
    if (Test-Path -LiteralPath (Join-Path $ShellData 'cache\profile-dirty.marker')) {
        Write-Warn 'есть cache\profile-dirty.marker: на мастере шёл сеанс игрока, профиль player ещё не сброшен' 'Start-Service ClubShellAgent; подождать, пока агент сбросит профиль player и файл исчезнет; снова этот скрипт'
    }
    # profile-reset-reboot.txt (агент 1.0.20): профиль player на мастере дважды подряд не удалился, и агент уже
    # перезагружал ПК ради этого долга. prepare-image v1.0.20 файл оставляет (SHELL_CHANGES 89); в образе каждое место
    # считало бы свою единственную перезагрузку ради долга уже потраченной. Агент удаляет файл сам, когда профиль
    # удалится.
    if (Test-Path -LiteralPath (Join-Path $ShellData 'cache\profile-reset-reboot.txt')) {
        Write-Warn ('есть cache\profile-reset-reboot.txt: профиль player на мастере дважды подряд не удалился, ' +
            'агент уже перезагружал ПК ради этого') ("разобраться со сбросом профиля player на мастере: журнал " +
            "агента ($ShellData\logs, «not fully deleted»), что держит профиль; когда профиль удалится, агент удалит " +
            'и файл; потом снова этот скрипт')
    }
    # Данные WebView2 оболочки (Tauri: %LOCALAPPDATA%\<identifier>) — признак, что в киоск на мастере входили.
    $playerDir = if ($playerProfile -and $playerProfile.LocalPath) { "$($playerProfile.LocalPath)" } else { "C:\Users\$KioskUser" }
    $shellWebData = Join-Path $playerDir 'AppData\Local\uz.clubshell.shell'
    if (Test-Path -LiteralPath $shellWebData) {
        Write-Warn "есть ${shellWebData}: оболочка уже работала на мастере (вход в киоск), её данные ушли бы в образ" ("удалить профиль ${KioskUser}: sysdm.cpl → «Дополнительно» → «Профили пользователей» → $KioskUser → «Удалить»; " +
            "потом powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\kiosk-on.ps1 -Golden (создаст профиль заново) и снова этот скрипт")
    }

    # Одобрение: «Registered as PC» / «Stored agent tokens refreshed» в журнале или secure\agent.tokens (токены выдаются
    # только одобренному ПК) — ключ ПК принят кассой Deagle, политика кассы в кэше. Журналы и токены удаляет очистка
    # ниже: после неё (агент остановлен, регистрации нет) — уже проверено прошлым запуском.
    $registration = Get-ShellRegistration
    if ($registration.State -eq 'registered' -or (Test-Path -LiteralPath (Join-Path $ShellData 'secure\agent.tokens'))) {
        Write-Pass 'мастер одобрен в кассе Deagle (ключ ПК принят, политика кассы в кэше)'
    } elseif ($registration.State -eq 'none' -and "$($shellSvc.Status)" -eq 'Stopped' -and -not (Test-Path -LiteralPath (Join-Path $ShellData 'agent-identity.json'))) {
        Write-Line 'регистрации нет — её уже убрал прошлый запуск этого скрипта (агент остановлен); одобрение проверялось тогда'
    } elseif ($registration.State -eq 'waiting') {
        Write-Warn 'мастер ждёт одобрения в кассе: ключ ПК не проверен, политика — из установщика' ("касса «Зал и устройства»: новый ПК USER = этот ПК Primer — его номер, зона Primer, «Без постоянного диска» вкл., " +
            '«Обслуживание» выкл.; через минуту снова этот скрипт')
    } elseif ($registration.State -eq 'badKey') {
        Write-Warn 'сервер ClubShell отверг ключ ПК (X-Club-Key): с ним не зарегистрируется ни одно место' "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\shell-set-key.ps1 (ключ Deagle), одобрить в кассе, снова этот скрипт"
    } else {
        $detail = if ($registration.Detail) { " ($($registration.Detail))" } else { '' }
        Write-Warn "в журнале агента нет одобрения${detail}: ключ ПК не проверен" "служба ClubShellAgent и интернет ($KitDir\shell-agent-diag-run-as-admin.cmd), одобрение в кассе, снова этот скрипт"
    }

    # Ключ ПК. Значение не печатается и сразу забывается.
    $config = Read-JsonFile $AgentJson
    if (-not $config) {
        Write-Warn "$AgentJson нет или он не читается как JSON" 'shell-uninstall и установка заново (get-shell-kit)'
    } else {
        $persistent = "$(Get-JsonValue $config 'paths', 'persistent')"
        if ($persistent) { Write-Warn "в agent.json задан paths.persistent ($persistent): у мест бездиска постоянного тома нет" 'убрать paths.persistent из agent.json (shell-install его не пишет)' }
        $hasJsonKey = "$(Get-JsonValue $config 'server', 'clubApiKey')" -match $KeyPattern
        $jsonKeyAny = [bool]"$(Get-JsonValue $config 'server', 'clubApiKey')"
        $fileKeyState = (Read-ClubKeyFile).State
        $setKeyCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\shell-set-key.ps1 (ключ Deagle), снова этот скрипт"
        if ($PlainKey -and $fileKeyState -eq 'ok') { Write-Pass 'ключ ПК в secure\club.key расшифровывается: -PlainKey перенесёт его в agent.json (только SYSTEM и Administrators)' }
        elseif ($PlainKey -and $fileKeyState -eq 'missing' -and $hasJsonKey) { Write-Pass 'ключ ПК уже в agent.json (-PlainKey), secure\club.key нет' }
        elseif (-not $PlainKey -and $fileKeyState -eq 'ok' -and -not $jsonKeyAny) { Write-Pass 'ключ ПК — в secure\club.key (DPAPI машины: расшифровывается, места с этим образом тоже расшифруют), в agent.json пусто' }
        elseif ($jsonKeyAny) { Write-Warn 'ключ ПК лежит в agent.json открытым текстом' 'Start-Service ClubShellAgent; через минуту агент перенесёт его в secure\club.key; снова этот скрипт (или запуск с -PlainKey)' }
        elseif ($fileKeyState -eq 'missing') { Write-Warn 'ключа ПК нет ни в secure\club.key, ни в agent.json: места не зарегистрируются' $setKeyCmd }
        else { Write-Warn 'secure\club.key не расшифровывается или в нём не ключ ПК' $setKeyCmd }
    }
    $secureDir = Join-Path $ShellData 'secure'
    if ((Test-Path -LiteralPath $secureDir) -and (Test-UserishAccess $secureDir)) {
        Write-Warn "$secureDir доступна группе Users (или Everyone)" 'права ставит MSI: только SYSTEM и Administrators (shell-uninstall и установка заново)'
    }

    if ($script:WarnCount -gt $gateStart) {
        Write-Host ''
        Write-Host ('ClubShell: предупреждений {0} (строки WARN выше). Ничего не изменено, ПК не выключаю — разберите их и запустите снова.' -f ($script:WarnCount - $gateStart)) -ForegroundColor Yellow
        return
    }
} else {
    Write-Title 'ClubShell'
    if (Test-Path -LiteralPath $ShellData) {
        Write-Warn "службы ClubShellAgent нет, но есть $ShellData (остатки установки: в agent.json мог остаться ключ ПК)" 'shell-uninstall-run-as-admin.cmd (данные — удалить), потом снова этот скрипт'
    } else {
        Write-Line 'службы ClubShellAgent нет — ClubShell в эталоне не стоит, раздел пропущен'
    }
}

Write-Title 'Помощник: остановка и удаление регистрации этого ПК'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Write-Warn 'службы ClubDisklessHelper нет — помощник не установлен (install-helper.ps1)' }
else {
    $stopAt = Get-Date
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'ClubDisklessHelper' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    }
    if ((Get-Service -Name $ServiceName).Status -eq 'Stopped') { Write-Pass 'служба остановлена' } else { Write-Warn 'служба не остановилась' }
    if ((Get-Service -Name $ServiceName).StartType -ne 'Automatic') { Write-Warn 'запуск службы не «Автоматически»: на местах помощник не запустится' }
    # Действия при сбое (перезапуск через 5/5/30 с: install-helper.ps1, помощник 1.8.0 ставит их сам) Windows выполняет,
    # только если процесс завершился, не сообщив «остановлена»: падение или Kill при ненормальном конце хоста (1.8.0) —
    # и во время остановки тоже. Обычная остановка (Stop-Service, помощник отмечает «чистый выход») — не сбой, её Windows
    # не перезапускает. Запустись служба снова — помощник опять зарегистрировался бы, и регистрация ушла бы в образ.
    Start-Sleep -Seconds 6
    $crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031, 7034; StartTime = $stopAt } -ErrorAction SilentlyContinue |
        Where-Object { @($svc.DisplayName, $ServiceName) -contains "$($_.Properties[0].Value)" })
    $statusNow = "$((Get-Service -Name $ServiceName).Status)"
    if ($crashes.Count -gt 0 -or $statusNow -ne 'Stopped') {
        Write-Warn ('служба не осталась остановленной: сейчас {0}, сбоев при остановке (события 7031/7034): {1} — Windows запускает её снова' -f $statusNow, $crashes.Count) 'подождать 30 с (служба запустится), потом снова этот скрипт'
    } else {
        Write-Pass 'служба не запустилась снова (остановка без сбоя)'
    }
}
if (Test-Path -LiteralPath $HelperExe) {
    $helperSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $HelperExe).Hash.ToLowerInvariant()
    $helperVersion = Get-HelperVersion $helperSha
    if ($helperVersion) { Write-Line "помощник $helperVersion, SHA256 $helperSha" } else { Write-Line "помощник — неизвестная сборка, SHA256 $helperSha" }
} else {
    Write-Warn "нет $HelperExe — на местах помощника не будет" 'install-helper.ps1'
}
# Самообновление не закончено: update.json — замена или проверка новой сборки, .new — скачанная, ещё не поставленная,
# .failed — откат. Не стирать: в образ ушла бы непроверенная сборка или прерванная замена.
$updateLeft = @((Join-Path $StateDir 'update.json'), "$HelperExe.new", "$HelperExe.failed" | Where-Object { Test-Path -LiteralPath $_ })
if ($updateLeft.Count -gt 0) {
    Write-Warn ('обновление помощника не подтверждено: есть {0}' -f ($updateLeft -join ', ')) ('Start-Service ClubDisklessHelper; дождаться «готово» в панели (карточка «Обновление помощника») или поставить ' +
        'помощник вручную (install-helper.ps1); потом снова этот скрипт')
    Write-Host ''
    Write-Host 'Помощник: обновление не подтверждено. Служба остановлена, регистрация этого ПК не удалена, ПК не выключаю — разберите WARN и запустите снова.' -ForegroundColor Yellow
    return
}
# .old без update.json — хвост подтверждённой замены (помощник стёр бы его при старте); update-failed.json — поручения с
# ошибкой на этом ПК: местам не нужны.
foreach ($p in "$HelperExe.old", (Join-Path $StateDir 'update-failed.json')) {
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) { Write-Warn "не удалён $p" } else { Write-Pass "удалён $p" }
    }
}
foreach ($f in $StateFiles) {
    $p = Join-Path $StateDir $f
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) { Write-Warn "не удалён $p" } else { Write-Pass "удалён $f" }
    }
}
foreach ($d in $StateDirs) {
    $p = Join-Path $StateDir $d
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $p) { Write-Warn "не удалена $p" } else { Write-Pass "удалена папка $d" }
    }
}

if ($shellSvc) {
    # ==================================================================================================================
    # ClubShell: остановка и очистка (prepare-image.ps1 v1.0.20 без переноса ключа), потом проверки эталона с шеллом
    # ==================================================================================================================
    Write-Title 'ClubShell: остановка агента и удаление регистрации этого ПК'
    $shellSvc.Refresh()
    if ($shellSvc.Status -ne 'Stopped') {
        Stop-Service -Name $ShellService -Force -ErrorAction SilentlyContinue
        try { $shellSvc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
    }
    $shellSvc.Refresh()
    $agentStopped = "$($shellSvc.Status)" -eq 'Stopped'
    # Действие службы «перезапуск при сбое» на остановку не срабатывает.
    if ($agentStopped) { Write-Pass 'агент остановлен' }
    else { Write-Warn "агент не остановился ($($shellSvc.Status)): очистка пропущена" 'Stop-Service ClubShellAgent -Force (не перезагружать), потом снова этот скрипт' }
    if ("$($shellSvc.StartType)" -ne 'Automatic') {
        Write-Warn "запуск агента «$($shellSvc.StartType)», а не «Автоматически»: на местах шелл не запустится" 'Set-Service ClubShellAgent -StartupType Automatic'
    }
    $leftProcs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ClubShell*' -or $_.Name -like 'clubshell-*' })
    if ($leftProcs.Count -gt 0) {
        Write-Warn ("работают процессы ClubShell: {0} — очистка пропущена" -f (($leftProcs | ForEach-Object { $_.Name } | Select-Object -Unique) -join ', ')) 'закрыть их (Диспетчер задач), потом снова этот скрипт'
    }

    if ($agentStopped -and $leftProcs.Count -eq 0) {
        $removed = 0
        foreach ($rel in $ShellStateFiles) {
            $path = Join-Path $ShellData $rel
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue; $removed++ }
        }
        # Кэши сервера (server-config.json, games.json, …) и метка последнего сброса профиля (profile-reset.marker).
        # Не cache\dns-filter.state: в нём DNS адаптеров до политики, агент их возвращает; и не
        # cache\profile-dirty.marker — долг сброса, проверен выше (как в prepare-image.ps1 v1.0.20).
        foreach ($item in @(Get-ChildItem -LiteralPath (Join-Path $ShellData 'cache') -File -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '*.json' -or $_.Name -eq 'profile-reset.marker' })) {
            Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
            $removed++
        }
        # Папки — очистить, а не удалить: их права ставил установщик.
        foreach ($rel in $ShellEmptyDirs) {
            foreach ($item in @(Get-ChildItem -LiteralPath (Join-Path $ShellData $rel) -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $removed++
            }
        }
        Write-Line "удалено файлов и папок: $removed (регистрация, токены, kiosk.cred, офлайн-база, кэши сервера, журналы)"

        # agent.json заново: агент мог переписать его до остановки. pcId, pcName, zone мастера — убрать; -PlainKey — ключ
        # из secure\club.key в server.clubApiKey, файл только для SYSTEM и Administrators, club.key удалить.
        $config = Read-JsonFile $AgentJson
        $plainValue = $null
        if ($PlainKey) { $plainValue = (Read-ClubKeyFile).Key }
        if (-not $config) {
            Write-Warn "$AgentJson не читается как JSON: pcId мастера не убран" 'shell-uninstall и установка заново (get-shell-kit)'
        } else {
            $stripped = @()
            foreach ($name in 'pcId', 'pcName', 'zone') {
                $key = Find-JsonKey $config $name
                while ($null -ne $key) { [void]$config.Remove($key); $stripped += $key; $key = Find-JsonKey $config $name }
            }
            $server = Get-JsonValue $config 'server'
            $keyName = $null
            if ($plainValue -and $server -is [System.Collections.IDictionary]) {
                $keyName = Find-JsonKey $server 'clubApiKey'
                if (-not $keyName) { $keyName = 'clubApiKey' }
                $server[$keyName] = $plainValue
            }
            if ($stripped.Count -gt 0 -or $keyName -or ($PlainKey -and (Test-UserishAccess $AgentJson))) {
                if (Write-JsonAtomic $AgentJson $config ([bool]$PlainKey)) {
                    if ($stripped.Count -gt 0) { Write-Pass ("из agent.json убраны {0} мастера" -f ($stripped -join ', ')) }
                    if ($keyName) {
                        Remove-Item -LiteralPath $ClubKeyFile -Force -ErrorAction SilentlyContinue
                        Write-Pass 'ключ ПК — в agent.json (только SYSTEM и Administrators), secure\club.key удалён (-PlainKey)'
                    }
                }
            } else {
                Write-Pass 'в agent.json нет pcId, pcName и zone мастера'
            }
            if ($keyName) { $server[$keyName] = $null }
        }
        $plainValue = $null

        # USB — по политике кассы в кэше, а не «как у Windows» (prepare-image.ps1 ставит 3): C: мест сбрасывается при
        # каждой загрузке, и со Start=3 USB-диск, вставленный до загрузки, подключался бы раньше агента на каждом сеансе
        # (агент извлекает только съёмные тома, USB HDD/SSD — DriveType Fixed). Запрет HID снимается: клавиатура и мышь
        # каждого места ставятся при каждой загрузке.
        $usb = Get-GoldenUsbStart
        $usbStor = 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR'
        if (Test-Path -LiteralPath $usbStor) {
            Set-ItemProperty -LiteralPath $usbStor -Name 'Start' -Value $usb.Start -Type DWord -ErrorAction SilentlyContinue
            Write-Line "USBSTOR Start=$($usb.Start) ($($usb.Why))"
        }
        $deviceInstall = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
        $hidClass = '{745a17a0-74d3-11d0-b6fe-00a0c9f57da2}'
        if ((Get-RegValue "$deviceInstall\DenyDeviceClasses" '1') -eq $hidClass) {
            Remove-ItemProperty -LiteralPath "$deviceInstall\DenyDeviceClasses" -Name '1' -ErrorAction SilentlyContinue
            Set-ItemProperty -LiteralPath $deviceInstall -Name 'DenyDeviceClasses' -Value 0 -Type DWord -ErrorAction SilentlyContinue
            Write-Pass 'снят запрет устройств HID из политики агента'
        }
    }

    Write-Title 'ClubShell: проверка эталона'
    $left = @($ShellStateFiles | Where-Object { Test-Path -LiteralPath (Join-Path $ShellData $_) })
    $left += @(Get-ChildItem -LiteralPath (Join-Path $ShellData 'cache') -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*.json' -or $_.Name -eq 'profile-reset.marker' } | ForEach-Object { "cache\$($_.Name)" })
    $left += @($ShellEmptyDirs | Where-Object { @(Get-ChildItem -LiteralPath (Join-Path $ShellData $_) -Force -ErrorAction SilentlyContinue).Count -gt 0 } | ForEach-Object { "$_\*" })
    if ($left.Count -eq 0) { Write-Pass 'регистрации мастера нет: agent-identity.json, токены, kiosk.cred, офлайн-база, кэши сервера и журналы удалены' }
    else { foreach ($rel in $left) { Write-Warn "остался $ShellData\$rel" 'закрыть то, что его держит, потом снова этот скрипт' } }

    # agent.json — поля shell-install.ps1, без pcId мастера, ключ — по выбранному пути.
    $config = Read-JsonFile $AgentJson
    if (-not $config) {
        Write-Warn "$AgentJson нет или он не читается как JSON" 'shell-uninstall и установка заново (get-shell-kit)'
    } else {
        $expected = [ordered]@{
            'server.baseUrl'                              = "$ShellOrigin/api/v1"
            'server.wsUrl'                                = ($ShellOrigin -replace '^http', 'ws') + '/ws/agent'
            'shell.kioskUser.name'                        = $KioskUser
            'shell.kioskUser.removeFromPrivilegedGroups'  = 'False'
            'shell.kioskUser.folderRedirect.enabled'      = 'False'
            'storage.gamesShare.enabled'                  = 'False'
            'updates.enabled'                             = 'False'
            'games.libraryRoots'                          = 'G:\Steam|H:\'
            'games.launchers.steam.exePath'               = 'G:\Steam\steam.exe'
        }
        $bad = @()
        foreach ($field in $expected.Keys) {
            $value = Get-JsonValue $config ($field -split '\.')
            $text = (@($value) | ForEach-Object { "$_" }) -join '|'
            if ($text -ne $expected[$field]) { $bad += "$field = '$text', нужно '$($expected[$field])'" }
        }
        foreach ($name in 'pcId', 'pcName', 'zone') { if (Find-JsonKey $config $name) { $bad += "есть $name (мастера)" } }
        if ("$(Get-JsonValue $config 'paths', 'persistent')") { $bad += 'задан paths.persistent' }
        $jsonKeyText = "$(Get-JsonValue $config 'server', 'clubApiKey')"
        if ($PlainKey) {
            if ($jsonKeyText -notmatch $KeyPattern) { $bad += 'нет ключа ПК в server.clubApiKey (-PlainKey)' }
            if (Test-UserishAccess $AgentJson) { $bad += 'файл с ключом ПК доступен группе Users' }
            if (Test-Path -LiteralPath $ClubKeyFile) { $bad += 'остался secure\club.key (-PlainKey)' }
        } else {
            if ($jsonKeyText) { $bad += 'ключ ПК в server.clubApiKey открытым текстом' }
            if ((Read-ClubKeyFile).State -ne 'ok') { $bad += 'secure\club.key нет или он не расшифровывается' }
        }
        $jsonKeyText = $null
        if ($bad.Count -eq 0) { Write-Pass "agent.json: сервер $ShellOrigin, киоск $KioskUser, без перенаправления папок, библиотеки G:\Steam и H:\, без автообновления, без pcId мастера" }
        else { foreach ($line in $bad) { Write-Warn "agent.json: $line" } }
    }

    if (Test-Path -LiteralPath (Join-Path $ShellData 'secure\admin-pin')) { Write-Pass 'PIN панели администратора киоска (secure\admin-pin) на месте — один на все места' }
    else { Write-Warn 'нет secure\admin-pin: панель администратора киоска на местах ответит «не настроено»' "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\set-shell-pin.ps1, потом снова этот скрипт" }
    if (Test-Path -LiteralPath (Join-Path $ShellAgentDir 'update-public.pem')) { Write-Pass 'ключ обновлений Agent\update-public.pem на месте' }
    else { Write-Warn "нет $ShellAgentDir\update-public.pem: места отвергнут любое обновление" "переустановить ClubShell $ShellVersion (shell-uninstall, потом get-shell-kit)" }
    $kioskRegistry = Get-RegValue 'HKLM:\SOFTWARE\ClubShell' 'KioskUser'
    if ("$kioskRegistry" -ieq $KioskUser) { Write-Pass "HKLM\SOFTWARE\ClubShell: KioskUser=$KioskUser (по нему работает удаление)" }
    else { Write-Warn "HKLM\SOFTWARE\ClubShell: KioskUser='$kioskRegistry', нужно $KioskUser" 'установщик ставился без KIOSKUSER=player: shell-uninstall и get-shell-kit заново' }
    $policy = Read-JsonFile (Join-Path $ShellData 'policies.json')
    if (-not $policy) {
        Write-Warn 'нет policies.json (кэш политики кассы): места до регистрации возьмут политику установщика' 'Start-Service ClubShellAgent, через минуту (политика кассы придёт) снова этот скрипт'
    } else {
        $dnsValue = Get-JsonValue $policy 'webFilter', 'dnsServers'
        $dnsServers = @(@($dnsValue) | Where-Object { "$_" })
        $blockIps = "$(Get-JsonValue $policy 'webFilter', 'blockResolvedIps')"
        if ($dnsServers.Count -eq 0 -and $blockIps -ne 'True') { Write-Pass 'политика в кэше: webFilter.dnsServers пусто, blockResolvedIps выкл. (DNS адаптеров агент не трогает)' }
        else { Write-Warn ("политика в кэше: dnsServers [{0}], blockResolvedIps {1}" -f ($dnsServers -join ', '), $blockIps) 'касса «Политика ПК»: dnsServers [] и blockResolvedIps false; Start-Service ClubShellAgent, минута, снова этот скрипт' }
    }

    # Учётка и профиль player; оболочка в его кусте и в шаблоне Default (из него Windows пересоздаёт профиль после сброса).
    $kioskOnCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\kiosk-on.ps1 -Golden"
    $playerAccount = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if (-not $playerAccount) {
        Write-Warn "нет учётки $KioskUser" "$kioskOnCmd, потом снова этот скрипт"
    } else {
        $groups = @($PrivilegedGroupSids | Where-Object { (Get-GroupMemberSids $_) -contains $playerAccount.SID.Value })
        if ($playerAccount.Enabled -and $groups.Count -eq 0) { Write-Pass "$KioskUser — включённый обычный пользователь" }
        else { Write-Warn ("{0}: включён {1}, привилегированные группы: {2} — агент откажется от такого киоска" -f $KioskUser, $playerAccount.Enabled, ($groups -join ', ')) 'убрать player из этих групп (или удалить учётку), потом kiosk-on -Golden' }
        $playerProfile = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID='$($playerAccount.SID.Value)'" -ErrorAction SilentlyContinue
        if (-not $playerProfile) {
            Write-Warn "нет профиля ${KioskUser}: на местах автовход создаст его из шаблона" "$kioskOnCmd (создаёт профиль), потом снова этот скрипт"
        } elseif ($playerProfile.Loaded) {
            Write-Warn "профиль $KioskUser загружен (вход или чужой процесс держит куст)" "выйти из сеанса $KioskUser, подождать минуту, снова этот скрипт"
        } else {
            $values = Get-HiveShellValues (Join-Path $playerProfile.LocalPath 'NTUSER.DAT') 'ClubSeal-Player'
            if ($null -eq $values) { Write-Warn "куст профиля $KioskUser ($($playerProfile.LocalPath)\NTUSER.DAT) не загрузился" 'снова этот скрипт' }
            elseif ($values.Winlogon -eq $ShellExe -or $values.Policies -eq $ShellExe) { Write-Pass "в профиле $KioskUser — оболочка ClubShell (Shell)" }
            else { Write-Warn "в профиле $KioskUser нет оболочки ClubShell (Shell): на местах — рабочий стол игрока" "$kioskOnCmd, потом снова этот скрипт" }
        }
    }
    $defaultDir = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList').Default)
    $values = Get-HiveShellValues (Join-Path $defaultDir 'NTUSER.DAT') 'ClubSeal-Default'
    if ($null -eq $values) { Write-Warn "куст шаблона $defaultDir\NTUSER.DAT не загрузился (занят?)" 'снова этот скрипт' }
    elseif ($values.Winlogon -eq $ShellExe -and $values.Policies -eq $ShellExe) { Write-Pass 'шаблон профиля Default: оболочка ClubShell (оба значения Shell)' }
    else { Write-Warn 'в шаблоне профиля Default нет оболочки ClubShell: после первого сброса профиля у игрока — Пуск и explorer' "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\seal-default-profile.ps1, потом снова этот скрипт" }

    # Автовход: пароль player — в секрете LSA (его пишет агент); у мест kiosk.cred нет, агент берёт пароль автовхода.
    $autoLogon = "$(Get-RegValue $WinlogonKey 'AutoAdminLogon')"
    $defaultUser = "$(Get-RegValue $WinlogonKey 'DefaultUserName')"
    $defaultDomain = "$(Get-RegValue $WinlogonKey 'DefaultDomainName')"
    if ($autoLogon -eq '1' -and $defaultUser -ieq $KioskUser -and ($defaultDomain -ieq 'USER' -or $defaultDomain -eq '.')) { Write-Pass "автовход: $defaultDomain\$defaultUser" }
    else { Write-Warn ("автовход AutoAdminLogon={0}, {1}\{2}: нужно 1 и USER\{3}" -f $autoLogon, $defaultDomain, $defaultUser, $KioskUser) "$kioskOnCmd, потом снова этот скрипт" }
    if ($null -ne (Get-RegValue $WinlogonKey 'DefaultPassword')) { Write-Warn 'в Winlogon есть DefaultPassword (пароль автовхода открытым текстом) — ушёл бы в образ' "Remove-ItemProperty '$WinlogonKey' DefaultPassword; потом снова этот скрипт" }
    if ($null -ne (Get-RegValue $WinlogonKey 'AutoLogonCount')) { Write-Warn 'в Winlogon есть AutoLogonCount: автовход кончится через несколько загрузок' "Remove-ItemProperty '$WinlogonKey' AutoLogonCount" }
    if ("$(Get-RegValue $WinlogonKey 'IgnoreShiftOverride')" -eq '1') { Write-Warn 'IgnoreShiftOverride=1: Shift при загрузке места не даст экрана входа администратора' "Remove-ItemProperty '$WinlogonKey' IgnoreShiftOverride" }
    # ForceAutoLogon=1 агент 1.0.20 пишет вместе с автовходом и ставит снова при каждом применении политики шелла — не
    # сбрасывать. После любого выхода из Windows сразу входит player (Shift при загрузке работает по-прежнему).
    $forceAutoLogon = "$(Get-RegValue $WinlogonKey 'ForceAutoLogon')"
    if ($forceAutoLogon -eq '1') {
        Write-Line 'ForceAutoLogon=1 (агент 1.0.20): после выхода из любой учётки сразу входит player'
    } else {
        Write-Line "ForceAutoLogon='$forceAutoLogon': агент 1.0.20 поставит 1 сам, применив политику шелла"
    }

    # Администратор эталона: Shift при загрузке любого места → он. Встроенный Administrator — выключен.
    $admin = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
    if (-not $admin) {
        Write-Warn "нет учётки $AdminUser (администратор эталона)" 'запустить скрипт под администратором эталона или с -AdminUser <имя>'
    } else {
        $isAdmin = (Get-GroupMemberSids 'S-1-5-32-544') -contains $admin.SID.Value
        $passwordState = Get-PasswordState $AdminUser
        if ($admin.Enabled -and $isAdmin) { Write-Pass "$AdminUser — включённый администратор" }
        else { Write-Warn "$AdminUser выключен или не в Administrators: на местах не будет администратора" 'Управление компьютером → Локальные пользователи и группы' }
        if ($passwordState -eq 'set') { Write-Pass "у $AdminUser есть пароль (Shift при загрузке места → $AdminUser → пароль)" }
        elseif ($passwordState -eq 'blank') { Write-Warn "у $AdminUser ПУСТОЙ пароль: Shift при загрузке любого места даст рабочий стол администратора" "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\set-club-password.ps1 -AdminUser '$AdminUser'" }
        else { Write-Warn "пароль ${AdminUser}: $passwordState" }
    }
    foreach ($builtin in @(Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.SID.Value -match '-500$' -and $_.Enabled })) {
        Write-Warn "включена встроенная учётка $($builtin.Name)" "net user `"$($builtin.Name)`" /active:no (администратор эталона — своя учётка, не встроенная)"
    }
    # Учётка Microsoft: её вход и токены ушли бы в образ, снимки img-* и резервные копии, а Shift на местах спрашивал бы
    # пароль от аккаунта в интернете (shell-preflight -Golden — FAIL).
    if ($admin -and "$($admin.PrincipalSource)" -eq 'MicrosoftAccount') {
        Write-Warn "$AdminUser — учётка Microsoft: её вход и токены — в образе, снимках и резервных копиях; Shift на местах спрашивает пароль от аккаунта" ("локальный администратор со своим паролем (Параметры → Учётные записи → Другие пользователи), вход им, " +
            "учётку $AdminUser удалить вместе с её данными (там же → «Удалить»), снова этот скрипт (уже под новым администратором)")
    }
    # Остальные включённые учётки (кроме player и встроенной — она выше) — тоже на экране входа каждого места (Shift при
    # загрузке): второй администратор или пустой пароль — вход мимо киоска на всех местах.
    $adminSids = Get-GroupMemberSids 'S-1-5-32-544'
    foreach ($other in @(Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -and $_.Name -ne $KioskUser -and $_.Name -ne $AdminUser -and $_.SID.Value -notmatch '-500$' })) {
        $otherName = $other.Name
        if ($adminSids -contains $other.SID.Value) {
            Write-Warn "включён второй администратор ${otherName}: он и его пароль — на каждом месте (Shift при загрузке)" "Disable-LocalUser -Name '$otherName' (администратор эталона — один: $AdminUser), снова этот скрипт"
            continue
        }
        $otherState = Get-PasswordState $otherName
        if ($otherState -eq 'set') { Write-Line "включена учётка $otherName (обычный пользователь, пароль есть): она на экране входа мест (Shift)" }
        elseif ($otherState -eq 'blank') { Write-Warn "у включённой учётки $otherName ПУСТОЙ пароль: Shift при загрузке любого места — рабочий стол мимо киоска" "Disable-LocalUser -Name '$otherName' (или задать ей пароль), снова этот скрипт" }
        else { Write-Warn "пароль включённой учётки ${otherName}: $otherState (не проверен)" "Disable-LocalUser -Name '$otherName', если она не нужна, снова этот скрипт" }
    }

    if ("$(Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoDriveTypeAutoRun')" -eq '255') { Write-Pass 'автозапуск выключен (NoDriveTypeAutoRun=255): окна «Автозапуск» при подключении G:/H: нет' }
    else { Write-Warn 'автозапуск не выключен: у игрока появится окно «Автозапуск» при подключении G:/H:' "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\disable-autoplay.ps1" }
    $usbExpected = Get-GoldenUsbStart
    $usbStart = "$(Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start')"
    if ($usbStart -eq "$($usbExpected.Start)") { Write-Pass "USBSTOR Start=$usbStart ($($usbExpected.Why)): с ним каждое место грузится ещё до агента" }
    else { Write-Warn "USBSTOR Start=$usbStart, нужно $($usbExpected.Start) ($($usbExpected.Why))" "Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' Start $($usbExpected.Start)" }

    # Схемы питания. «Высокая»/«Максимальная», если они есть в списке и не текущие: сон «никогда» и PCIe ASPM выкл. —
    # здесь же (повторяемо, перезагрузка не нужна). Текущую настраивает prepare-golden.ps1 — её только проверяем.
    $guidPattern = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
    $activeMatch = [regex]::Match((Get-NativeOutput 'powercfg.exe' @('/getactivescheme')), $guidPattern)
    $activeScheme = if ($activeMatch.Success) { $activeMatch.Value.ToLowerInvariant() } else { $null }
    $listedSchemes = @([regex]::Matches((Get-NativeOutput 'powercfg.exe' @('/list')), $guidPattern) | ForEach-Object { $_.Value.ToLowerInvariant() })
    foreach ($guid in @($HighPlans.Keys)) {
        if ($listedSchemes -notcontains $guid -or $guid -eq $activeScheme) { continue }
        $standby = Get-PowerIndex $guid 'SUB_SLEEP' 'STANDBYIDLE'
        if (-not $standby -or $standby.AC -ne 0 -or $standby.DC -ne 0) {
            & powercfg.exe /setacvalueindex $guid SUB_SLEEP STANDBYIDLE 0 2>&1 | Out-Null
            & powercfg.exe /setdcvalueindex $guid SUB_SLEEP STANDBYIDLE 0 2>&1 | Out-Null
            Write-Line "$($HighPlans[$guid]): сон — никогда"
        }
        $aspm = Get-PowerIndex $guid 'SUB_PCIEXPRESS' 'ASPM'
        if (-not $aspm -or $aspm.AC -ne 0 -or $aspm.DC -ne 0) {
            & powercfg.exe /setacvalueindex $guid SUB_PCIEXPRESS ASPM 0 2>&1 | Out-Null
            & powercfg.exe /setdcvalueindex $guid SUB_PCIEXPRESS ASPM 0 2>&1 | Out-Null
            Write-Line "$($HighPlans[$guid]): PCIe ASPM — выкл."
        }
    }
    $checkSchemes = @(@($activeScheme) + @($HighPlans.Keys | Where-Object { $listedSchemes -contains $_ }) | Where-Object { $_ } | Select-Object -Unique)
    foreach ($guid in $checkSchemes) {
        $name = if ($HighPlans.Contains($guid)) { $HighPlans[$guid] } else { $guid }
        if ($guid -eq $activeScheme) { $name = "$name (текущая)" }
        $standby = Get-PowerIndex $guid 'SUB_SLEEP' 'STANDBYIDLE'
        $aspm = Get-PowerIndex $guid 'SUB_PCIEXPRESS' 'ASPM'
        if ($standby -and $standby.AC -eq 0 -and $aspm -and $aspm.AC -eq 0) { Write-Pass "схема питания ${name}: сон — никогда, PCIe ASPM — выкл." }
        else {
            $hint = if ($guid -eq $activeScheme) { 'powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 0; powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0; powercfg /setactive SCHEME_CURRENT' }
                    else { "powercfg /setacvalueindex $guid SUB_SLEEP STANDBYIDLE 0; powercfg /setacvalueindex $guid SUB_PCIEXPRESS ASPM 0" }
            Write-Warn ("схема питания {0}: сон {1} с, ASPM {2} (от сети) — сон или ASPM рвут iSCSI и сетевую карту" -f $name, $(if ($standby) { $standby.AC } else { '?' }), $(if ($aspm) { $aspm.AC } else { '?' })) "$hint; потом снова этот скрипт"
        }
    }
}

# Обновление Edge и WebView2 в эталоне выключено (fixes-v2.ps1). install-webview2.ps1 (шаг шелла) ставит WebView2 через
# службы обновления, а загрузчик обновляет клиент EdgeUpdate, и тот заново регистрирует задания MicrosoftEdgeUpdate*
# включёнными (они идут от SYSTEM и без служб). Места с C:, сброшенным при каждой загрузке, качали бы обновления при
# каждой загрузке и меняли WebView2 под работающим шеллом. Выключить снова (повторяемо), потом проверить.
Write-Title 'Обновление Edge и WebView2: выключено'
$edgeFound = 0
foreach ($svc in @(Get-Service -Name 'edgeupdate', 'edgeupdatem' -ErrorAction SilentlyContinue)) {
    $edgeFound++
    if ("$($svc.StartType)" -ne 'Disabled') {
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        Set-Service -Name $svc.Name -StartupType Disabled -ErrorAction SilentlyContinue
        Write-Line "служба $($svc.Name): запуск «$($svc.StartType)» → «Отключена»"
    }
    $svcStart = "$((Get-Service -Name $svc.Name -ErrorAction SilentlyContinue).StartType)"
    if ($svcStart -eq 'Disabled') { Write-Pass "служба $($svc.Name) отключена" }
    else { Write-Warn "служба $($svc.Name): запуск «$svcStart», не отключена" "Stop-Service $($svc.Name) -Force; Set-Service $($svc.Name) -StartupType Disabled; снова этот скрипт" }
}
foreach ($task in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'MicrosoftEdgeUpdate*' })) {
    $edgeFound++
    if ("$($task.State)" -ne 'Disabled') {
        $task | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
        Write-Line "задание $($task.TaskName): «$($task.State)» → выключено"
    }
    $taskState = "$((Get-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue).State)"
    if ($taskState -eq 'Disabled') { Write-Pass "задание $($task.TaskName) выключено" }
    else { Write-Warn "задание $($task.TaskName): «$taskState», не выключено" 'Get-ScheduledTask MicrosoftEdgeUpdate* | Disable-ScheduledTask; снова этот скрипт' }
}
if ($edgeFound -eq 0) { Write-Line 'служб edgeupdate/edgeupdatem и заданий MicrosoftEdgeUpdate* нет' }

# Образы NGEN: после обновлений и новых программ .NET Framework их надо достроить здесь — на местах (сброс при каждой
# загрузке) иначе каждый запуск PowerShell компилирует всё заново (помощник бездиска грузил ЦП до 100%, 2026-10-20).
Write-Title 'Образы NGEN (.NET Framework): достройка'
foreach ($ngen in "$env:windir\Microsoft.NET\Framework64\v4.0.30319\ngen.exe", "$env:windir\Microsoft.NET\Framework\v4.0.30319\ngen.exe",
                  "$env:windir\Microsoft.NET\Framework64\v2.0.50727\ngen.exe", "$env:windir\Microsoft.NET\Framework\v2.0.50727\ngen.exe") {
    if (-not (Test-Path -LiteralPath $ngen)) { continue }
    & $ngen executeQueuedItems /nologo /silent | Out-Null
    Write-Line ("{0}: код {1}" -f ($ngen -replace '^.*\\Microsoft\.NET\\', ''), $LASTEXITCODE)
}
$psStart = @(1..3 | ForEach-Object { (Measure-Command { & powershell.exe -NoProfile -NonInteractive -Command 'exit' }).TotalSeconds } | Measure-Object -Minimum).Minimum
if ($psStart -le 1.5) { Write-Pass ('запуск PowerShell {0:N1} с' -f $psStart) } else { Write-Warn ('запуск PowerShell {0:N1} с — NGEN не достроен? (должно быть меньше 1,5 с)' -f $psStart) }

foreach ($f in 'helper.json', 'club-ca.pem') {
    if (Test-Path -LiteralPath (Join-Path $StateDir $f)) { Write-Pass "на месте $f" } else { Write-Warn "нет $StateDir\$f — места не зарегистрируются" }
}

# Набор шелла — последним и только без WARN: с WARN этот скрипт из набора нужен, чтобы запустить его снова.
if ($script:WarnCount -eq 0 -and (Test-Path -LiteralPath $KitDir)) {
    Write-Title "Набор шелла ${KitDir}: удаление"
    # Не из самой папки: этот скрипт PowerShell уже прочитал, а текущая папка держала бы её.
    Set-Location -LiteralPath $env:SystemRoot
    Remove-Item -LiteralPath $KitDir -Recurse -Force -ErrorAction SilentlyContinue
    $leftFiles = @(Get-ChildItem -LiteralPath $KitDir -Recurse -Force -File -ErrorAction SilentlyContinue)
    if (-not (Test-Path -LiteralPath $KitDir)) { Write-Pass "удалён $KitDir (установщик, журналы, снимки DNS, копия шаблона профиля)" }
    elseif ($leftFiles.Count -eq 0) { Write-Line "осталась пустая папка $KitDir (открыта в другом окне) — безвредно" }
    else {
        Write-Warn ("{0} удалён не весь (файлов: {1}): папка открыта в другом окне или программе" -f $KitDir, $leftFiles.Count) ("закрыть их, потом Remove-Item $KitDir -Recurse -Force " +
            'и выключить ПК: shutdown /s /t 0 (НЕ перезагружать)')
    }
}

Write-Host ''
if ($script:WarnCount -ne 0) {
    Write-Host ('Предупреждений: {0} (строки WARN выше). ПК не выключаю — разберите их и запустите снова.' -f $script:WarnCount) -ForegroundColor Yellow
    return
}
if ($NoShutdown) { Write-Host 'Готово. Выключите ПК (не перезагружайте), потом панель «Бездиск» → «Опубликовать».' -ForegroundColor Green; return }
Write-Host 'Готово. ПК выключится через 15 секунд; потом панель «Бездиск» → «Опубликовать».' -ForegroundColor Green
Write-Host 'Не включайте его до публикации: помощник снова зарегистрируется и попадёт в образ.' -ForegroundColor Green
if ($shellSvc) {
    Write-Host 'С ClubShell — и агент, а мастер войдёт в киоск player. Пришлось включить: Shift при загрузке, вход администратором' -ForegroundColor Green
    Write-Host 'эталона, команда get-shell-kit (вернёт набор; вопрос про киоск — n), потом этот скрипт снова.' -ForegroundColor Green
    Write-Host 'Из учётки администратора не выходить: с ForceAutoLogon=1 сразу войдёт player.' -ForegroundColor Green
}
& shutdown.exe /s /t 15 /c 'Эталон готов к публикации'
