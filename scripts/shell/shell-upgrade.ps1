<#
.SYNOPSIS
    Обновляет ClubShell, который уже стоит, до 1.0.20 (касса клуба Deagle) — на гибриде и в эталоне бездиска (сеанс
    мастера): сначала помощник бездиска до 1.9.0, потом установщик 1.0.20 поверх старой версии, проверки, на гибриде —
    перезагрузка в киоск. agent.json, регистрация, ключ ПК, PIN и запись в кассе остаются.
.DESCRIPTION
    Обычно его запускает команда get-shell-kit — сама, когда на ПК стоит ClubShell старше, чем в наборе (PowerShell от
    администратора):
      irm http://192.168.1.201:5080/pxe/v1/files/tools/get-shell-kit.ps1 | iex
    Руками — C:\ClubShellKit\shell-upgrade-run-as-admin.cmd. На гибриде — под «Club»: Shift при загрузке, вход Club
    (автовход идёт в киоск player). Игрока в Windows быть не должно: установщик останавливает агента, оболочка игрока
    держала бы свои файлы, а его сеанс остался бы без агента.
    Что делает, по порядку:
      1. проверка — до неё ничего не меняется; есть FAIL — стоп «Nothing was changed», есть WARN — вопрос, продолжать
         ли (по умолчанию нет):
           - права администратора; служба ClubShellAgent; версия в HKLM\SOFTWARE\ClubShell\Version (пишет MSI): та же,
             что у набора, — «уже 1.0.20»: шаги 2-3 пропускаются, проверки шага 4 — всё равно; новее — FAIL (понижать
             версию установщик не даёт); версия продукта exe агента — для сведения;
           - HKLM\SOFTWARE\ClubShell\KioskUser — player; agent.json читается (сериализатором, как в kiosk-on.ps1), ключа
             ПК в нём нет, secure\club.key есть; Club (у эталона — его администратор) — включённый администратор;
           - player не вошёл в Windows, оболочка clubshell-shell не запущена; другой установки нет (мьютекс
             _MSIExecute кем-то занят или окно msiexec); ожидание перезагрузки — WARN (эталон — FAIL: перезагружаться
             — до обновления); Smart App Control: включён — FAIL, режим оценки — WARN (сборка не подписана); WebView2
             зарегистрирован;
           - ClubShellSetup-1.0.20.exe в наборе: размер и SHA256 релиза (нет или не тот — снова команда get-shell-kit);
           - системный диск: гибрид — локальный (не iSCSI), эталон — сеанс …:diskless-master и имя USER; место на C:;
             G: подключён (эталон — WARN), H: — WARN; на гибриде, где их нет, а помощник не работает или старше
             1.9.0, подсказка — update-helper-net руками, потом снова get-shell-kit;
      2. помощник бездиска — не ниже 1.9.0: с шеллом 1.0.20 только его ping несёт готовность G:/H: (SHELL_CHANGES 86);
         со старым шелл судит о готовности по корням библиотек, а у 1.6.0 канала нет совсем — ещё и ожидание
         сохранений по 60 с на каждый вход. Гибрид: вопрос (по умолчанию да) и update-helper-net с сервера клуба — в
         отдельном powershell (irm | iex в этой области переписал бы $Version и другие переменные); он же ставит службу
         Steam (там только WARN). Код выхода не решает, решает состояние (ждёт до 120 с): служба работает, SHA256 exe —
         сборка 1.9.0, версия по helper-build.json или таблице сборок — 1.9.0, G: снова подключён. Не дошёл — FAIL и
         стоп до установщика: шелл 1.0.20 на ПК со старым помощником не ставится. Эталон — FAIL с подсказкой (кнопка
         «Обновить помощник» в панели или update-helper-net в этом сеансе мастера, потом снова get-shell-kit): сам
         помощник эталона не обновляет;
      3. запоминает поля agent.json, которые пишет shell-install (server, киоск, перенаправление папок, библиотеки,
         Steam, gamesShare, updates), SHA256 файла, DNS адаптеров, помощник, есть ли secure\admin-pin, и запускает
         ClubShellSetup-1.0.20.exe /quiet /norestart /log ... KIOSKUSER=player INSTALLSHELL=1 — как установка, без
         SERVERURL и CLUBAPIKEY: agent.json уже есть, и MSI его не трогает, ключ ПК — в secure\club.key. MSI 1.0.20 —
         MajorUpgrade: старая версия удаляется внутри той же установки, без своей очистки (автовход, шелл в профиле,
         брандмауэр остаются). Код 0 — PASS; 3010 — PASS «нужна перезагрузка» (эталон — метка reboot-before-kiosk, как
         у shell-install, и код выхода 3); 1638 — уже стоит более новая; 1618 — идёт другая установка; другой — FAIL с
         журналами установщика. Ошибка установщика — стоп;
      4. проверки (и для «уже 1.0.20»): служба агента работает (MSI запускает её, не дожидаясь); версия в HKLM — 1.0.20,
         у exe агента — тоже (иначе WARN); в «Программах» одна запись бандла ClubShell 1.0.20 (старые — WARN); поля
         agent.json не изменились (WARN; включились updates или перенаправление папок — FAIL), ключа в нём нет,
         secure\club.key на месте; secure\admin-pin есть (нет — WARN: set-shell-pin); автовход — ждёт до 90 с, пока
         агент применит shellReplacement: AutoAdminLogon=1, DefaultUserName=player и ForceAutoLogon=1 (новое в 1.0.20:
         после любого выхода Windows сразу снова входит в киоск), без открытого DefaultPassword и без
         IgnoreShiftOverride=1 (Shift при загрузке должен пускать Club); не включился — WARN и вопрос, запустить ли
         kiosk-on.ps1 (потом снова сверка); регистрация в журнале агента (Get-Registration из kiosk-on.ps1); player —
         не в привилегированных группах; Club — включённый администратор; папок ClubShell на G:/H: нет; помощник
         работает и не старее, чем был; G:/H: — как до обновления; DNS не изменился (WARN); «Readiness: ready after» в
         журнале агента (нет — строка для сведения: бывает, что только после перезагрузки).
    В конце гибрид спрашивает, перезагрузить ли ПК (есть FAIL — по умолчанию нет): после перезагрузки ПК входит в
    киоск player уже с новым шеллом. Эталон не перезагружается никогда: в конце — что дальше (3010 — одна перезагрузка
    до seal-golden: киоск уже включён — Shift при загрузке, вход администратором, снова команда get-shell-kit; иначе —
    seal-golden.ps1 и «Опубликовать»). Выходить из сеанса администратора мастера нельзя: с ForceAutoLogon выход сразу
    входит в киоск player — только перезагрузка или выключение.
    -Golden — эталон в режиме мастера (передаёт get-shell-kit вместе с -AdminUser <администратор эталона>); сеанс
    …:diskless-master включает его и без флага (лаунчер .cmd флагов не передаёт), администратор тогда — тот, кто
    запустил. Ключ ПК, пароль Club и одобрение в кассе не спрашиваются: ПК остаётся той же записью кассы.
    Понизить версию установщик не даст (MajorUpgrade): откат — shell-uninstall, потом чистая установка набором старой
    версии.
    Журнал — C:\ClubShellKit\logs\upgrade-<ПК>-<время>.txt, установщика — setup-upgrade-<время>*.log там же.
    Код выхода при запуске файлом: 0 — без FAIL, 2 — есть FAIL (или стоп) после изменений (помощник, установщик или
    kiosk-on уже запускались), 4 — есть FAIL (или стоп), но ПК не менялся (чаще всего — стоп на проверке шага 1:
    исправить и запустить снова, откатывать нечего), 3 — эталон без FAIL, но установщик ответил 3010 (перезагрузить
    мастер один раз до seal-golden).
    Строки вывода — только ASCII, без BOM и без #Requires (как у остальных скриптов набора).
.EXAMPLE
    shell-upgrade-run-as-admin.cmd
#>
param(
    [string]$KitDir = 'C:\ClubShellKit',
    [string]$KioskUser = 'player',
    [string]$AdminUser = 'Club',
    [switch]$Golden
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Релиз v1.0.20 (тег -> dbe2091, 2026-11-08): те же значения, что в shell-install.ps1 и shell-preflight.ps1 (менять
# вместе). 1.0.19 не ставить никогда (события агента не доходили до экрана).
$Version = '1.0.20'
$TargetVersion = [version]$Version
$VersionPattern = '^1\.0\.20(\D|$)'
$SetupName = 'ClubShellSetup-1.0.20.exe'
$SetupSize = 75550872
$SetupSha256 = '0AC2A30BB47EFE3E00D3E4FF3907520CB21B88E2711ECD3D90BB428D0C4C0B94'
$GetKitCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/get-shell-kit.ps1 | iex'
# Помощник: не ниже 1.9.0 (SHELL_CHANGES 86). Сборка, до которой его доводит update-helper-net: 1.9.0+184aabf. Версия —
# по SHA256 exe (Get-HelperVersion): helper-build.json службы 1.8.0+ или таблица известных сборок — та же, что в
# shell-install.ps1, shell-preflight.ps1 и shell-uninstall.ps1 (менять вместе).
$HelperMinVersion = [version]'1.9.0'
$HelperTargetSha = '1af4465530dd2d17fa287f9d90f11f0250262940193d1dfc88931e66e4c9ac56'
$HelperRunnerUrl = 'http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1'
$UpdateHelperCmd = "irm $HelperRunnerUrl | iex"
$HelperBuildFile = Join-Path $env:ProgramData 'ClubDiskless\helper-build.json'
$HelperBuilds = @{
    '26635a5f7aab4d77441d0b0138064b9c7f64bb1daf4b0bcaf09052439ddd0148' = '1.6.0'   # 1.6.0+13609a5
    '41da96a0b0721b22bb5f5e1898c927f08c4856968ee059a0c40b3d2b54a30e23' = '1.7.0'   # 1.7.0+f79e0d5
    '05a73a8f0cb1717040c7f64fe1c94104fbdf501b9b196f90659c56d0c33b31e1' = '1.8.0'   # 1.8.0+e77f0ad
    '1af4465530dd2d17fa287f9d90f11f0250262940193d1dfc88931e66e4c9ac56' = '1.9.0'   # 1.9.0+184aabf
}
$DataDir = Join-Path $env:ProgramData 'ClubShell'
$AgentJson = Join-Path $DataDir 'agent.json'
$ClubKeyFile = Join-Path $DataDir 'secure\club.key'
$AdminPinFile = Join-Path $DataDir 'secure\admin-pin'
$ShellKey = 'HKLM:\SOFTWARE\ClubShell'
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$WebView2Key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
# Группы, членов которых агент не берёт в киоск (как в shell-install.ps1).
$PrivilegedGroupSids = @('S-1-5-32-544', 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551', 'S-1-5-32-547',
    'S-1-5-32-556', 'S-1-5-32-578')
# Поля agent.json, которые пишет shell-install.ps1 (шаг 4): обновление их не меняет (MSI agent.json не трогает).
$OurKeys = @('server.baseUrl', 'server.wsUrl', 'shell.kioskUser.name', 'shell.kioskUser.removeFromPrivilegedGroups',
    'shell.kioskUser.folderRedirect.enabled', 'games.libraryRoots', 'games.launchers.steam.exePath',
    'storage.gamesShare.enabled', 'updates.enabled')
# Включились — FAIL: перенаправление уводит папки игрока на G:/H: (сбрасываются при загрузке), автообновление качает
# 72 МБ, а неподписанный манифест агент отвергает. Нет поля — у агента по умолчанию true: тоже «включено».
$MustStayOff = @('shell.kioskUser.folderRedirect.enabled', 'updates.enabled')
# Хэш-таблица, а не $script:-переменные (как в shell-preflight.ps1).
$Count = @{ Fail = 0; Warn = 0 }

# Бездиск: таргет загрузки …:diskless-master — мастер эталона, …:seat-NN — обычное место (как в kiosk-on.ps1).
$bootTargets = @()
try {
    $bootTargets = @(Get-IscsiSession -ErrorAction SilentlyContinue | ForEach-Object { "$($_.TargetNodeAddress)" } |
        Where-Object { $_ -match ':(diskless-master|seat-\d+)$' })
} catch { }
$masterTargets = @($bootTargets | Where-Object { $_ -match ':diskless-master$' })
$seatTargets = @($bootTargets | Where-Object { $_ -match ':seat-\d+$' })
# Сеанс мастера включает режим эталона и без -Golden: лаунчер .cmd флагов не передаёт.
$goldenAuto = $false
if (-not $Golden -and $masterTargets.Count -gt 0) { $Golden = $true; $goldenAuto = $true }
# Эталон: администратор — тот, кто запустил (Club есть только в образах гибридов).
if ($Golden -and -not $PSBoundParameters.ContainsKey('AdminUser')) { $AdminUser = $env:USERNAME }
$modeArgs = @()
if ($Golden) { $modeArgs = @('-Golden') }
# Эталон, установщик ответил 3010: метка для kiosk-on -Golden (как у shell-install.ps1).
$RebootMarker = Join-Path $KitDir 'reboot-before-kiosk'
$KioskOnCmd = Join-Path $KitDir 'kiosk-on-run-as-admin.cmd'
if ($Golden) {
    $KioskOnCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\kiosk-on.ps1 -Golden -AdminUser " +
        "'$AdminUser'"
}
$DiagCmd = Join-Path $KitDir 'shell-agent-diag-run-as-admin.cmd'
$PinCmd = Join-Path $KitDir 'set-shell-pin-run-as-admin.cmd'
$SetKeyCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\shell-set-key.ps1 (the key of club Deagle)"
$AdminFix = "Computer Management -> Local Users and Groups: enable $AdminUser and add it to Administrators"

function Write-Title([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Pass([string]$Text) { Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "      $Text" }
function Write-Warn([string]$Text, [string]$Fix) {
    $Count.Warn++
    Write-Host "WARN  $Text" -ForegroundColor Yellow
    if ($Fix) { Write-Host "      -> $Fix" -ForegroundColor Yellow }
}
function Write-Fail([string]$Text, [string]$Fix) {
    $Count.Fail++
    Write-Host "FAIL  $Text" -ForegroundColor Red
    if ($Fix) { Write-Host "      -> $Fix" -ForegroundColor Red }
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { $null }
}

# Да/нет. В русской раскладке клавиши Y и N дают «н» и «т» — их тоже понимаем, чтобы не переключать раскладку.
function Read-YesNo([string]$Question, [bool]$Default) {
    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $yesRu = [string][char]0x043D
    $noRu = [string][char]0x0442
    while ($true) {
        $answer = "$(Read-Host "$Question $suffix")".Trim().ToLowerInvariant()
        if (-not $answer) { return $Default }
        if ($answer -in @('y', 'yes', $yesRu)) { return $true }
        if ($answer -in @('n', 'no', $noRu)) { return $false }
        Write-Host '      type y or n'
    }
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 3
    }
    [bool](& $Condition)
}

# SID членов локальной группы. Get-LocalGroupMember падает на «осиротевших» SID (удалённые учётки в группе) —
# тогда ADSI.
function Get-GroupMemberSids([string]$GroupSid) {
    try {
        return @(Get-LocalGroupMember -SID $GroupSid -ErrorAction Stop | ForEach-Object { $_.SID.Value })
    } catch { }
    $sids = @()
    try {
        $groupSidObject = New-Object Security.Principal.SecurityIdentifier($GroupSid)
        $name = ($groupSidObject.Translate([Security.Principal.NTAccount]).Value -split '\\')[-1]
        $group = [ADSI]"WinNT://./$name,group"
        foreach ($member in @($group.Invoke('Members'))) {
            $bytes = $member.GetType().InvokeMember('objectSid', 'GetProperty', $null, $member, $null)
            $sids += (New-Object Security.Principal.SecurityIdentifier($bytes, 0)).Value
        }
    } catch { }
    $sids
}

function Get-PrivilegedGroupsOf([string]$UserSid) {
    $names = @()
    foreach ($groupSid in $PrivilegedGroupSids) {
        if ((Get-GroupMemberSids $groupSid) -contains $UserSid) { $names += $groupSid }
    }
    $names
}

# Включённый член Administrators.
function Test-EnabledAdmin([string]$Name) {
    $account = Get-LocalUser -Name $Name -ErrorAction SilentlyContinue
    [bool]($account -and $account.Enabled -and (Get-GroupMemberSids 'S-1-5-32-544') -contains $account.SID.Value)
}

function Get-DnsSnapshot {
    $map = @{}
    Get-DnsClientServerAddress -ErrorAction SilentlyContinue | ForEach-Object {
        $map["$($_.InterfaceAlias) ($($_.AddressFamily))"] = (@($_.ServerAddresses) -join ', ')
    }
    $map
}

# Версия помощника по SHA256 его exe ($Hash): helper-build.json ({"v":1,"version":"x.y.z","build":"<sha256>"}, пишет
# сама служба 1.8.0+ при каждом старте) — только если build = SHA256 exe; иначе таблица $HelperBuilds; иначе $null
# (неизвестная сборка). Версию файла exe не читаем: exe собирается на Linux, и ресурс версии в нём — от среды .NET
# (10.0.x), а не от помощника. Та же функция — в shell-install.ps1, shell-preflight.ps1 и shell-uninstall.ps1 (менять
# вместе).
function Get-HelperVersion([string]$Hash) {
    if (-not $Hash) { return $null }
    $sha = $Hash.ToLowerInvariant()
    try {
        $info = [IO.File]::ReadAllText($HelperBuildFile) | ConvertFrom-Json
        if ($info.v -eq 1 -and "$($info.build)".ToLowerInvariant() -eq $sha -and
            "$($info.version)" -match '^\d+\.\d+\.\d+$') { return "$($info.version)" }
    } catch { }
    if ($HelperBuilds.ContainsKey($sha)) { return $HelperBuilds[$sha] }
    $null
}

function Get-HelperState {
    $filter = "Name='ClubDisklessHelper'"
    $service = Get-CimInstance -ClassName Win32_Service -Filter $filter -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    $exe = [Environment]::ExpandEnvironmentVariables(($service.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim())
    $hash = if (Test-Path -LiteralPath $exe) { (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash } else { $null }
    [pscustomobject]@{ State = $service.State; Exe = $exe; Hash = $hash; Version = (Get-HelperVersion $hash) }
}

# Помощник для строк вывода: версия и состояние службы.
function Get-HelperLabel($State) {
    if (-not $State) { return 'missing (no service ClubDisklessHelper)' }
    $label = if ($State.Version) { $State.Version } else { 'unknown build' }
    "$label ($($State.State))"
}

function Test-Volume([string]$Letter) { [bool](Get-Volume -DriveLetter $Letter -ErrorAction SilentlyContinue) }

# agent.json и строки журнала агента — JavaScriptSerializer, а не ConvertFrom-Json: после первой регистрации агент
# добавляет pcId, и строки журнала с {PcId} в шаблоне несут оба ключа — ConvertFrom-Json в 5.1 такие отвергает (ключи
# различаются только регистром). Словарь сериализатора различает регистр. Как в kiosk-on.ps1 и seal-golden.ps1. В
# PowerShell 7 сборки нет — там ConvertFrom-Json -AsHashtable (как в shell-install.ps1).
$JsonReader = $null
try {
    Add-Type -AssemblyName System.Web.Extensions
    $JsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer
} catch { }

function ConvertFrom-JsonText([string]$Text) {
    try {
        if ($JsonReader) { $data = $JsonReader.DeserializeObject($Text) }
        else { $data = $Text | ConvertFrom-Json -AsHashtable }
    } catch { return $null }
    if ($data -is [System.Collections.IDictionary]) { return $data }
    $null
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { $text = [IO.File]::ReadAllText($Path) } catch { return $null }
    ConvertFrom-JsonText $text
}

# Ключ словаря без учёта регистра (как SettingsJson.FindKey у агента; как в seal-golden.ps1).
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

# Наши поля agent.json текстом: массив — через «|», true/false — True/False, нет поля — пустая строка. Ключ ПК сюда не
# попадает (его поле только проверяется на пустоту).
function Get-AgentKeys($Config) {
    $map = [ordered]@{}
    foreach ($field in $OurKeys) {
        $value = Get-JsonValue $Config ($field -split '\.')
        $map[$field] = (@($value) | ForEach-Object { "$_" }) -join '|'
    }
    $map
}

# Три последних журнала агента (Serilog compact JSON, agent-*.json).
function Get-AgentLogs {
    @(Get-ChildItem -LiteralPath (Join-Path $DataDir 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 3)
}

# Регистрация по журналам агента (шаблон сообщения — "@mt", исключение — "@x"); решает последняя подходящая строка.
# Как Get-Registration в kiosk-on.ps1: «Registered as PC» — одобрен; «Stored agent tokens refreshed» — тоже
# (зарегистрированный агент при старте только обновляет токены); «Registration attempt failed» — ждёт одобрения / ключ
# не принят / ошибка; строк нет, но есть secure\agent.tokens — одобрен. Сначала грубый фильтр по тексту: разбор тысяч
# строк в 5.1 медленный. Строки журнала не печатаются.
function Get-Registration {
    $state = @{ State = 'none'; Detail = '' }
    $filter = '"@mt"\s*:\s*"(Registered as PC|Stored agent tokens refreshed|Registration attempt)'
    foreach ($log in (Get-AgentLogs)) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 3000 -Encoding UTF8 -ErrorAction SilentlyContinue)
        foreach ($line in $lines) {
            if ($line -notmatch $filter) { continue }
            $entry = ConvertFrom-JsonText $line
            if ($entry -isnot [System.Collections.IDictionary]) { continue }
            $template = [string]$entry['@mt']
            if ($template -like 'Registered as PC*') {
                $state.State = 'registered'; $state.Detail = "name $($entry['PcName']), zone $($entry['Zone'])"
            } elseif ($template -like 'Stored agent tokens refreshed*') {
                $state.State = 'registered'; $state.Detail = 'agent tokens refreshed'
            } elseif ($template -like 'Registration attempt*failed*') {
                $exception = [string]$entry['@x']
                if ($exception -match 'waiting for approval|pendingApproval') {
                    $state.State = 'waiting'; $state.Detail = ''
                } elseif ($exception -match 'X-Club-Key') {
                    $state.State = 'badKey'; $state.Detail = ''
                } else {
                    $first = (($exception -split "`n")[0]).Trim()
                    if ($first.Length -gt 160) { $first = $first.Substring(0, 160) }
                    $state.State = 'failing'; $state.Detail = $first
                }
            }
        }
    }
    if ($state.State -eq 'none' -and (Test-Path -LiteralPath (Join-Path $DataDir 'secure\agent.tokens'))) {
        $state.State = 'registered'; $state.Detail = 'agent tokens present'
    }
    $state
}

# Есть ли в журналах агента строка с таким началом шаблона ("@mt"). Только да/нет, строки не печатаются.
function Test-AgentLog([string]$Template) {
    $pattern = '"@mt"\s*:\s*"' + [regex]::Escape($Template)
    foreach ($log in (Get-AgentLogs)) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 3000 -Encoding UTF8 -ErrorAction SilentlyContinue)
        if (@($lines | Where-Object { $_ -match $pattern }).Count -gt 0) { return $true }
    }
    $false
}

# Записи «Программы и компоненты» с именем ClubShell*: бандл ClubShellSetup (BundleUpgradeCode), скрытый MSI из бандла,
# MSI без бандла (пилотные сборки CI), установщик одной оболочки (как в shell-preflight.ps1).
function Get-ClubShellEntries {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    @(Get-ChildItem -Path $keys -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { "$($_.DisplayName)" -like 'ClubShell*' })
}

# Версия из HKLM\SOFTWARE\ClubShell\Version (пишет MSI) — первые три числа; $null — значения нет или оно не такое.
function Get-InstalledVersion {
    if ("$(Get-RegValue $ShellKey 'Version')" -match '^(\d+\.\d+\.\d+)') { return [version]$Matches[1] }
    $null
}

# Exe агента — из пути службы; его версия продукта — для сведения (решает версия в HKLM).
function Get-AgentProduct {
    $exe = $null
    $product = ''
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='ClubShellAgent'" -ErrorAction SilentlyContinue
    if ($service) {
        $exe = [Environment]::ExpandEnvironmentVariables(($service.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim())
    }
    if ($exe -and (Test-Path -LiteralPath $exe)) {
        $product = "$((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion)"
    }
    [pscustomobject]@{ Exe = $exe; Product = $product }
}

# Автовход (Winlogon). Пароль не читается: только есть ли открытое значение DefaultPassword.
function Get-WinlogonState {
    $names = @()
    try { $names = @((Get-Item -LiteralPath $WinlogonKey -ErrorAction Stop).GetValueNames()) } catch { }
    [pscustomobject]@{
        AutoAdminLogon      = "$(Get-RegValue $WinlogonKey 'AutoAdminLogon')"
        DefaultUserName     = "$(Get-RegValue $WinlogonKey 'DefaultUserName')"
        ForceAutoLogon      = "$(Get-RegValue $WinlogonKey 'ForceAutoLogon')"
        IgnoreShiftOverride = "$(Get-RegValue $WinlogonKey 'IgnoreShiftOverride')"
        DefaultPassword     = ($names -contains 'DefaultPassword')
    }
}

# Киоск включён так, как его пишет агент 1.0.20 (ShellRegistry.SetAutoLogon): автовход в киоск и ForceAutoLogon=1.
function Test-KioskAutoLogon {
    $winlogon = Get-WinlogonState
    $winlogon.AutoAdminLogon -eq '1' -and $winlogon.DefaultUserName -ieq $KioskUser -and
        $winlogon.ForceAutoLogon -eq '1'
}

# Снимок до установщика (для «уже 1.0.20» — до проверок): с ним сверяется шаг 4.
function Get-Snapshot {
    $sha = $null
    try { $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $AgentJson).Hash } catch { }
    @{
        AgentSha  = $sha
        AgentKeys = (Get-AgentKeys (Read-JsonFile $AgentJson))
        Dns       = (Get-DnsSnapshot)
        Helper    = (Get-HelperState)
        AdminPin  = (Test-Path -LiteralPath $AdminPinFile)
    }
}

# ======================================================================================================================
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}
# Набор кладёт get-shell-kit (папка только для SYSTEM и администраторов): здесь папку не создаём — новая папка в C:\
# получила бы право записи для всех пользователей.
if (-not (Test-Path -LiteralPath $KitDir)) {
    throw "$KitDir is missing: run the get-shell-kit command ($GetKitCmd). Nothing was changed."
}
$LogDir = Join-Path $KitDir 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
try { Start-Transcript -LiteralPath (Join-Path $LogDir "upgrade-$env:COMPUTERNAME-$stamp.txt") | Out-Null } catch { }

$alreadyCurrent = $false
$upgraded = $false
$rebootBeforeKiosk = $false
# Что-то на ПК менялось (помощник, установщик, kiosk-on): только тогда вопрос о перезагрузке гибрида; FAIL без
# изменений — код выхода 4, а не 2.
$changed = $false
try {
    Write-Host ("ClubShell upgrade to {0} on {1} (kiosk account '{2}', admin '{3}')" -f $Version, $env:COMPUTERNAME,
        $KioskUser, $AdminUser) -ForegroundColor Cyan
    if ($goldenAuto) { Write-Info 'diskless master session (:diskless-master): golden mode on by itself (no reboot)' }
    if ($Golden) {
        Write-Host ("GOLDEN MODE: the diskless golden image (master session), admin '{0}'; no reboot at the end" -f
            $AdminUser) -ForegroundColor Cyan
    }
    if ($KioskUser -ieq $AdminUser) {
        throw "Kiosk account '$KioskUser' is the admin account '$AdminUser'. Nothing was changed."
    }

    # --- 1. Проверка: до неё ничего не меняется -------------------------------------------------------------------
    Write-Title 'Step 1: upgrade check (nothing is changed before it passes)'
    $agentService = Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue
    $installedText = "$(Get-RegValue $ShellKey 'Version')"
    $installedVersion = Get-InstalledVersion
    $agentBefore = Get-AgentProduct
    if (-not $agentService) {
        Write-Fail 'service ClubShellAgent is missing: ClubShell is not installed, nothing to upgrade' (
            "a new install: the get-shell-kit command ($GetKitCmd) runs shell-install")
    } elseif (-not $installedVersion) {
        Write-Fail "HKLM\SOFTWARE\ClubShell\Version is missing or not a version ('$installedText')" (
            "run $DiagCmd and send a photo to Claude")
    } elseif ($installedVersion -gt $TargetVersion) {
        Write-Fail "ClubShell $installedText is newer than $Version (this kit): no downgrade" (
            'the installer refuses it (MajorUpgrade): keep it, or shell-uninstall and a fresh install of the older kit')
    } elseif ($installedVersion -eq $TargetVersion) {
        $alreadyCurrent = $true
        Write-Pass "ClubShell $installedText is already $($Version): no setup, only the checks (step 4)"
    } else {
        Write-Pass ("ClubShell {0} is installed (service ClubShellAgent {1}): upgrade to {2}" -f $installedText,
            $agentService.Status, $Version)
    }
    if ($agentBefore.Exe) { Write-Info "agent exe: $($agentBefore.Exe), product version '$($agentBefore.Product)'" }

    $registryKiosk = "$(Get-RegValue $ShellKey 'KioskUser')"
    if ($registryKiosk -ieq $KioskUser) {
        Write-Pass "kiosk account of the install: '$registryKiosk' (HKLM\SOFTWARE\ClubShell\KioskUser)"
    } else {
        Write-Fail "HKLM\SOFTWARE\ClubShell\KioskUser is '$registryKiosk', not '$KioskUser'" (
            'this kit upgrades only a ClubShell installed by shell-install (kiosk player): send a photo to Claude')
    }

    # Ключ ПК не читается в переменные и не печатается: проверяется только, что поле пустое.
    $config = Read-JsonFile $AgentJson
    if (-not (Test-Path -LiteralPath $AgentJson)) {
        Write-Fail "$AgentJson is missing" "run $DiagCmd and send a photo to Claude"
    } elseif (-not $config) {
        Write-Fail "$AgentJson does not read as JSON" "run $DiagCmd and send a photo to Claude"
    } elseif ("$(Get-JsonValue $config 'server', 'clubApiKey')") {
        Write-Fail 'the PC key is still in agent.json (server.clubApiKey)' (
            'Start-Service ClubShellAgent; within a minute the agent moves it into secure\club.key; then run again')
    } else {
        Write-Pass "$AgentJson reads as JSON, no PC key in it (it stays as it is: the installer keeps it)"
    }
    $config = $null
    if (Test-Path -LiteralPath $ClubKeyFile) { Write-Pass 'secure\club.key is present (the PC key, DPAPI)' }
    else { Write-Fail 'secure\club.key is missing: the agent has no PC key' "$SetKeyCmd, then run again" }

    if (Test-EnabledAdmin $AdminUser) { Write-Pass "$AdminUser is an enabled administrator" }
    else { Write-Fail "$AdminUser is not an enabled administrator" $AdminFix }

    # Игрок в Windows: установщик останавливает агента, оболочка держит свои файлы (шелл игрока агент не закрывает).
    $playerProcs = @(Get-Process -IncludeUserName -ErrorAction SilentlyContinue |
        Where-Object { $_.UserName -and (($_.UserName -split '\\')[-1]) -ieq $KioskUser -and $_.SessionId -ne 0 })
    $shellProcs = @(Get-Process -Name 'clubshell-shell' -ErrorAction SilentlyContinue)
    if ($playerProcs.Count -gt 0 -or $shellProcs.Count -gt 0) {
        Write-Fail ("{0} is signed in to Windows ({1} processes) or the kiosk shell runs ({2} clubshell-shell)" -f
            $KioskUser, $playerProcs.Count, $shellProcs.Count) (
            "sign out the player: reboot, hold Shift at boot, sign in as $AdminUser, run again")
    } else {
        Write-Pass "$KioskUser is not signed in, the kiosk shell is not running"
    }

    # Другая установка: мьютекс Global\_MSIExecute Windows Installer держит (владеет им), пока идёт установка (и тихая,
    # без окна). Сам мьютекс есть и у простаивающей службы msiexec (она живёт ~10 минут после установки): решает
    # владение, а не то, что он открылся. WaitOne(0) — занят ли; свободен — сразу ReleaseMutex (как
    # Test-IsMutexAvailable в PSADT); брошенный (AbandonedMutexException) — тоже свободен, и тоже наш до ReleaseMutex.
    $msiBusy = $false
    $mutex = $null
    try {
        $rights = [System.Security.AccessControl.MutexRights]'Synchronize, Modify'
        if ([System.Threading.Mutex]::TryOpenExisting('Global\_MSIExecute', $rights, [ref]$mutex)) {
            $owned = $false
            try { $owned = $mutex.WaitOne(0) } catch {
                $abandoned = [System.Threading.AbandonedMutexException]
                if ($_.Exception -is $abandoned -or $_.Exception.InnerException -is $abandoned) { $owned = $true }
                else { throw }
            }
            if ($owned) { $mutex.ReleaseMutex() } else { $msiBusy = $true }
        }
    } catch {
        # Нет прав открыть — значит, мьютекс есть и не наш.
        $denied = [System.UnauthorizedAccessException]
        if ($_.Exception -is $denied -or $_.Exception.InnerException -is $denied) { $msiBusy = $true }
    } finally {
        if ($mutex) { $mutex.Dispose() }
    }
    $msiWindows = @(Get-Process -Name msiexec -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 })
    if ($msiBusy -or $msiWindows.Count -gt 0) {
        Write-Fail 'another installation is running (Windows Installer is busy or an msiexec window is open)' (
            'wait until it finishes or reboot (Shift at boot), then run again (error 1618 otherwise)')
    } else {
        Write-Pass 'no other installation is running'
    }

    # Ожидающая перезагрузка (как в shell-preflight.ps1): MSI тогда может ответить 3010 или 1618.
    $rebootPending =
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
    if ($rebootPending -and $Golden) {
        Write-Fail 'Windows waits for a reboot (updates/servicing)' ("reboot the master first (the kiosk is on: hold " +
            "Shift at boot, sign in as $AdminUser), then the get-shell-kit command again")
    } elseif ($rebootPending) {
        Write-Warn 'Windows waits for a reboot (updates/servicing): the installer may answer 3010 or 1618' (
            "reboot (hold Shift at boot, sign in as $AdminUser), then run again")
    } else {
        Write-Pass 'no reboot pending'
    }

    # Smart App Control: 1 — включён (не даст запустить неподписанные ClubShellAgent.exe и clubshell-shell.exe), 2 —
    # режим оценки.
    $sac = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' 'VerifiedAndReputablePolicyState'
    $sacFix = 'Windows Security -> App & browser control -> Smart App Control -> Off'
    if ($sac -eq 1) {
        Write-Fail "Smart App Control is ON: the unsigned ClubShell $Version would be blocked" $sacFix
    } elseif ($sac -eq 2) {
        Write-Warn 'Smart App Control is in evaluation mode: it may switch on and block the unsigned ClubShell' $sacFix
    } else {
        Write-Pass 'Smart App Control is off'
    }

    # Свой загрузчик WebView2 у бандла падает (0x80091007): на ПК с ClubShell WebView2 уже стоит, бандл его пропускает.
    $webView = Get-RegValue $WebView2Key 'pv'
    if ($webView -and $webView -ne '0.0.0.0') {
        Write-Pass "WebView2 Runtime $webView is installed"
    } else {
        Write-Fail 'WebView2 Runtime is not registered (the setup would fail with 0x80091007)' (
            "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\install-webview2.ps1 (needs internet); again")
    }

    $setup = Join-Path $KitDir $SetupName
    if ($alreadyCurrent) {
        Write-Info "installer not needed (ClubShell is already $Version)"
    } elseif (-not (Test-Path -LiteralPath $setup)) {
        Write-Fail "$SetupName is not in $KitDir" (
            "run the get-shell-kit command again ($GetKitCmd): it downloads the kit")
    } else {
        $setupLength = (Get-Item -LiteralPath $setup).Length
        $setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
        if ($setupLength -eq $SetupSize -and $setupHash -eq $SetupSha256) {
            Write-Pass "installer verified: $setupLength bytes, SHA256 $SetupSha256 (release v$Version, unsigned)"
        } else {
            Write-Fail "$setup is not the official v$Version file (size $setupLength, SHA256 $setupHash)" (
                "delete it, then run the get-shell-kit command again ($GetKitCmd)")
        }
    }

    # Системный диск: те же признаки, что в get-shell-kit.ps1 и shell-preflight.ps1 (BusType бывает и числом 9).
    $systemBus = '?'
    try {
        $systemPartition = Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop
        $systemDisk = $systemPartition | Get-Disk -ErrorAction Stop
        $systemBus = "$($systemDisk.BusType)"
    } catch { }
    $systemIscsi = $systemBus -eq 'iSCSI' -or $systemBus -eq '9'
    $renameFix = 'Rename-Computer USER, reboot (Shift at boot), run again'
    if ($seatTargets.Count -gt 0) {
        Write-Fail "this PC boots as a normal diskless seat ($($seatTargets -join ', ')): its C: resets at every boot" (
            'ClubShell goes into the golden: panel "Diskless" -> master mode, then the get-shell-kit command there')
    } elseif ($Golden) {
        if ($systemIscsi -and $masterTargets.Count -gt 0) {
            Write-Pass "system disk is the golden image in master mode ($($masterTargets[0]))"
        } else {
            Write-Fail "golden mode, but the system disk is '$systemBus' without a ':diskless-master' session" (
                'run it on the diskless master (panel "Diskless" -> master mode)')
        }
        $pendingName = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
        if ($env:COMPUTERNAME -ne 'USER') {
            Write-Fail "Windows name '$env:COMPUTERNAME' is not USER (every seat boots with this name)" $renameFix
        } elseif ($pendingName -and $pendingName -ne $env:COMPUTERNAME) {
            Write-Fail "the golden was renamed to '$pendingName' (not rebooted yet): it must stay USER" $renameFix
        } else {
            Write-Pass 'Windows name USER'
        }
    } elseif ($systemIscsi -or $bootTargets.Count -gt 0) {
        Write-Fail "system disk $env:SystemDrive is iSCSI without a ':diskless-master' session: not a hybrid" (
            'diskless: master mode in the panel, then the get-shell-kit command on the master')
    } else {
        Write-Pass "system disk is local ($systemBus): hybrid"
    }
    $cVolume = Get-Volume -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction SilentlyContinue
    if ($cVolume) {
        $freeGb = [math]::Round($cVolume.SizeRemaining / 1GB, 1)
        if ($freeGb -lt 1) { Write-Fail "free space on ${env:SystemDrive}: $freeGb GB" 'free at least 1 GB' }
        else { Write-Info "free space on ${env:SystemDrive}: $freeGb GB" }
    }

    # G:/H: — после обновления помощника и в конце сверяются с этим.
    $libsAtStart = @(@('G', 'H') | Where-Object { Test-Volume $_ })
    $mountFix = 'wait 1-2 minutes after boot (the helper mounts it), check the panel "Diskless", run again'
    # Гибрид: диски подключает помощник. Он не работает, сборка неизвестна или старше 1.9.0 — подсказка про него: шаг 2
    # (там update-helper-net) за FAIL по G: не начнётся, ожидание тут не поможет.
    if (-not $Golden -and $libsAtStart.Count -lt 2) {
        $helperAtStart = Get-HelperState
        if (-not ($helperAtStart -and $helperAtStart.State -eq 'Running' -and $helperAtStart.Version -and
                [version]$helperAtStart.Version -ge $HelperMinVersion)) {
            $mountFix = ("the helper is {0}: run {1}, then the get-shell-kit command again" -f
                (Get-HelperLabel $helperAtStart), $UpdateHelperCmd)
        }
    }
    foreach ($letter in @('G', 'H')) {
        if ($libsAtStart -contains $letter) {
            Write-Pass "${letter}: is mounted"
        } elseif ($letter -eq 'G' -and -not $Golden) {
            Write-Fail 'G: is not mounted' $mountFix
        } elseif ($Golden) {
            Write-Warn "${letter}: is not mounted in this master session" (
                'wait 1-2 minutes after boot (the helper mounts it); the seats mount their own disks')
        } else {
            Write-Warn 'H: is not mounted' $mountFix
        }
    }

    if ($Count.Fail -gt 0) {
        throw ("The upgrade check has FAIL lines (above): fix them, then run the get-shell-kit command again " +
            "($GetKitCmd). Nothing was changed.")
    }
    if (-not $alreadyCurrent -and $Count.Warn -gt 0 -and
        -not (Read-YesNo 'The check has WARN lines (above). Upgrade anyway?' $false)) {
        throw 'Stopped at the WARN lines. Nothing was changed.'
    }

    if ($alreadyCurrent) {
        Write-Title "Steps 2-3: skipped (ClubShell is already $Version)"
        $snapshot = Get-Snapshot
    } else {
        # --- 2. Помощник бездиска ---------------------------------------------------------------------------------
        Write-Title "Step 2: diskless helper $HelperMinVersion or newer"
        $helperNow = Get-HelperState
        $helperLabel = Get-HelperLabel $helperNow
        if ($helperNow -and $helperNow.State -eq 'Running' -and $helperNow.Version -and
            [version]$helperNow.Version -ge $HelperMinVersion) {
            Write-Pass "helper ClubDisklessHelper $($helperNow.Version) is running"
        } elseif ($Golden) {
            Write-Fail "helper $helperLabel is older than $HelperMinVersion or not running" (
                "update it in this master session with the panel button 'Obnovit pomoshchnik' (update the helper) " +
                "or $UpdateHelperCmd, then run the get-shell-kit command again")
            throw "The golden helper is not $HelperMinVersion yet: ClubShell was not upgraded. Nothing was changed."
        } else {
            Write-Info ("helper is {0}; ClubShell {1} needs {2} (it asks the helper whether G:/H: are ready)" -f
                $helperLabel, $Version, $HelperMinVersion)
            $question = "Update the helper $helperLabel -> $HelperMinVersion now (update-helper-net)?"
            if (-not (Read-YesNo $question $true)) {
                throw "The helper stays $($helperLabel): ClubShell was not upgraded. Nothing was changed."
            }
            # Отдельный powershell: irm | iex здесь переписал бы $Version, $ErrorActionPreference и другие переменные.
            Write-Info "running $HelperRunnerUrl in its own PowerShell"
            Write-Info 'it also sets up the Steam Client Service (Steam must be closed; up to 5 minutes; WARN only)'
            $changed = $true
            $runnerCommand = "irm '{0}' | iex" -f $HelperRunnerUrl
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $runnerCommand
            $runnerCode = $LASTEXITCODE
            Write-Info "update-helper-net ended (exit code $runnerCode; the state of the helper decides, not the code)"
            # Состояние: служба работает, exe — сборка 1.9.0 (SHA256), её версия по helper-build.json или таблице —
            # 1.9.0, G: снова подключён. H: (если был) — ждём заодно, но не требуем: шаг 4 сверит его ещё раз.
            $helperReady = {
                $state = Get-HelperState
                [bool]($state -and $state.State -eq 'Running' -and $state.Hash -eq $HelperTargetSha -and
                    $state.Version -and [version]$state.Version -eq $HelperMinVersion -and (Test-Volume 'G'))
            }
            $null = Wait-Until { (& $helperReady) -and ($libsAtStart -notcontains 'H' -or (Test-Volume 'H')) } 120
            $helperNow = Get-HelperState
            if (& $helperReady) {
                Write-Pass "helper updated: ClubDisklessHelper $($helperNow.Version) running (1.9.0 build), G: mounted"
                if ($libsAtStart -contains 'H' -and -not (Test-Volume 'H')) {
                    Write-Info 'H: is not back yet (checked again at the end)'
                }
            } else {
                $shaText = 'no exe'
                if ($helperNow -and $helperNow.Hash -eq $HelperTargetSha) { $shaText = 'the 1.9.0 build' }
                elseif ($helperNow -and $helperNow.Hash) { $shaText = "SHA256 $($helperNow.Hash)" }
                $gText = if (Test-Volume 'G') { 'mounted' } else { 'not mounted' }
                Write-Fail ("the helper did not reach {0}: {1}, exe {2}, G: {3}" -f $HelperMinVersion,
                    (Get-HelperLabel $helperNow), $shaText, $gText) ("read the update-helper-net lines above; run " +
                    "$UpdateHelperCmd again (or the panel button), then the get-shell-kit command")
                throw ("The helper is not $HelperMinVersion after update-helper-net: ClubShell was not upgraded " +
                    "(the shell $Version does not go on a PC with an older helper).")
            }
        }

        # --- 3. Установщик ------------------------------------------------------------------------------------------
        Write-Title "Step 3: $SetupName /quiet (upgrade over ClubShell $installedText)"
        $snapshot = Get-Snapshot
        $pinText = if ($snapshot.AdminPin) { 'set' } else { 'not set' }
        Write-Info ("before: agent.json SHA256 {0}, helper {1}, admin PIN {2}" -f $snapshot.AgentSha,
            (Get-HelperLabel $snapshot.Helper), $pinText)
        # Скачанный браузером файл несёт метку зоны: без неё SmartScreen не спрашивает про неподписанный установщик.
        Unblock-File -LiteralPath $setup
        $setupLog = Join-Path $LogDir "setup-upgrade-$stamp.log"
        # Те же параметры, что у установки: без SERVERURL (переписал бы адреса в agent.json) и без CLUBAPIKEY.
        $arguments = '/quiet /norestart /log "{0}" KIOSKUSER={1} INSTALLSHELL=1' -f $setupLog, $KioskUser
        Write-Info "running: $SetupName $arguments"
        Write-Info 'a few minutes, no windows: the old version is removed inside the same install, the agent restarts'
        $changed = $true
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
        $exitCode = $process.ExitCode
        if ($exitCode -eq 0) {
            Write-Pass 'setup finished (exit code 0)'
            $upgraded = $true
        } elseif ($exitCode -eq 3010 -and $Golden) {
            Write-Pass 'setup finished (exit code 3010: Windows wants a reboot; golden: once, before seal-golden)'
            $upgraded = $true
            $rebootBeforeKiosk = $true
            # MSI 3010 — обычно PendingFileRenameOperations, не ключи RebootPending: метка — как у shell-install.ps1.
            $markerText = ("ClubShell setup exit code 3010 at {0}: reboot the master once before kiosk-on" -f
                (Get-Date -Format s))
            try { Set-Content -LiteralPath $RebootMarker -Value $markerText -Encoding ASCII -ErrorAction Stop }
            catch {
                Write-Warn "cannot write $RebootMarker ($($_.Exception.Message))" (
                    'reboot the master once before seal-golden anyway')
            }
        } elseif ($exitCode -eq 3010) {
            Write-Pass 'setup finished (exit code 3010: Windows wants a reboot; the reboot at the end does it)'
            $upgraded = $true
        } else {
            $hint = switch ($exitCode) {
                1638 { 'a newer ClubShell is already installed' }
                1618 { 'another installation is running: wait or reboot, then run again' }
                1602 { 'cancelled' }
                default { 'see the setup log' }
            }
            Write-Fail "setup failed with exit code $exitCode ($hint)" (
                "logs: $setupLog and the *_ClubShellMsi*.log next to it")
            if ($exitCode -ne 1638) {
                Write-Info 'a failed MSI upgrade is rolled back: the old ClubShell should still be installed'
            }
            throw 'The ClubShell setup failed (FAIL line above): the checks after the upgrade were skipped.'
        }
    }

    # --- 4. Проверки --------------------------------------------------------------------------------------------------
    if ($upgraded) { Write-Title 'Step 4: checks after the upgrade' } else { Write-Title 'Step 4: checks' }
    # MSI запускает службу, не дожидаясь её (Wait=no): код 0 ещё не значит, что агент работает.
    $agentRunning = { (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue).Status -eq 'Running' }
    $running = Wait-Until $agentRunning 60
    if (-not $running) {
        try { Start-Service -Name ClubShellAgent -ErrorAction Stop } catch { }
        $running = Wait-Until $agentRunning 30
    }
    if ($running) { Write-Pass 'service ClubShellAgent is running' }
    else {
        Write-Fail 'service ClubShellAgent is not running' (
            "Event Viewer -> Application, source ClubShellAgent; $DataDir\logs")
    }

    $versionNow = "$(Get-RegValue $ShellKey 'Version')"
    if ($versionNow -match $VersionPattern) {
        Write-Pass "HKLM\SOFTWARE\ClubShell\Version = $versionNow"
    } else {
        Write-Fail "HKLM\SOFTWARE\ClubShell\Version is '$versionNow', not $Version" (
            "setup logs in $LogDir; send a photo to Claude")
    }
    $agentNow = Get-AgentProduct
    if ($agentNow.Product -match $VersionPattern) {
        Write-Pass "agent exe product version $($agentNow.Product)"
    } else {
        Write-Warn "agent exe product version is '$($agentNow.Product)' ($($agentNow.Exe)), not $Version" (
            "run $DiagCmd and send a photo to Claude")
    }

    # «Программы»: бандл 1.0.20 заменяет запись старого (тот же UpgradeCode); скрытый MSI той же версии — тоже
    # ClubShell.
    $arpEntries = @(Get-ClubShellEntries)
    $bundles = @($arpEntries | Where-Object { $_.BundleUpgradeCode -and "$($_.DisplayName)" -eq 'ClubShell' })
    $stale = @($arpEntries | Where-Object { "$($_.DisplayVersion)" -notmatch $VersionPattern })
    if ($bundles.Count -eq 1 -and "$($bundles[0].DisplayVersion)" -match $VersionPattern) {
        Write-Pass "Programs: ClubShell $($bundles[0].DisplayVersion) (one bundle entry)"
    } else {
        $bundleText = ($bundles | ForEach-Object { "$($_.DisplayVersion)" }) -join ', '
        Write-Warn ("Programs: {0} ClubShell bundle entries ({1}), expected one {2}" -f $bundles.Count, $bundleText,
            $Version) 'send a photo to Claude'
    }
    if ($stale.Count -gt 0) {
        $staleText = ($stale | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join ', '
        Write-Warn "Programs: older ClubShell entries remain: $staleText" (
            'send a photo to Claude (shell-uninstall removes them with ClubShell)')
    }

    # agent.json: MSI его не трогает, агент переписывает (добавляет pcId и т. п.) — сверяются только наши поля.
    $configAfter = Read-JsonFile $AgentJson
    if (-not $configAfter) {
        Write-Fail "$AgentJson is missing or does not read as JSON after the upgrade" (
            "run $DiagCmd and send a photo to Claude")
    } else {
        $keysBefore = $snapshot.AgentKeys
        $keysAfter = Get-AgentKeys $configAfter
        foreach ($field in $MustStayOff) {
            if ($keysAfter[$field] -eq 'False') { continue }
            if ($keysBefore[$field] -eq 'False') {
                Write-Fail "agent.json: $field is '$($keysAfter[$field])' now (it was False before the upgrade)" (
                    'send a photo to Claude: it must stay false')
            } else {
                Write-Warn "agent.json: $field is '$($keysAfter[$field])' (not False; so before the upgrade too)" (
                    'send a photo to Claude: shell-install writes false')
            }
        }
        # Включившееся поле из $MustStayOff — уже строкой выше, не второй раз в списке изменённых.
        $changedKeys = @($OurKeys | Where-Object {
                $keysAfter[$_] -ne $keysBefore[$_] -and
                -not ($MustStayOff -contains $_ -and $keysAfter[$_] -ne 'False') })
        foreach ($field in $changedKeys) {
            Write-Warn ("agent.json: {0} changed: '{1}' -> '{2}'" -f $field, $keysBefore[$field], $keysAfter[$field]) (
                'send a photo to Claude')
        }
        if ($changedKeys.Count -eq 0) {
            Write-Pass ('agent.json: the fields of shell-install unchanged (server, kiosk, no folder redirection, ' +
                'libraries, Steam, gamesShare off, updates off)')
        }
        if ("$(Get-JsonValue $configAfter 'server', 'clubApiKey')") {
            Write-Fail 'the PC key is in agent.json (server.clubApiKey)' (
                'Restart-Service ClubShellAgent: the agent moves it into secure\club.key; then run again')
        }
        $shaNow = $null
        try { $shaNow = (Get-FileHash -Algorithm SHA256 -LiteralPath $AgentJson).Hash } catch { }
        if ($shaNow -eq $snapshot.AgentSha) { Write-Info 'agent.json is byte for byte as before' }
        else { Write-Info 'agent.json was rewritten by the agent (its own fields; ours are checked above)' }
    }
    $configAfter = $null
    if (Test-Path -LiteralPath $ClubKeyFile) { Write-Pass 'secure\club.key is still present' }
    else { Write-Fail 'secure\club.key is gone: the agent has no PC key' $SetKeyCmd }
    if (Test-Path -LiteralPath $AdminPinFile) {
        Write-Pass 'secure\admin-pin is present (the kiosk admin panel PIN)'
    } elseif ($snapshot.AdminPin) {
        Write-Warn 'secure\admin-pin is gone (it was there before): the kiosk admin panel answers "not configured"' (
            "set the shell PIN: $PinCmd")
    } else {
        Write-Warn 'no secure\admin-pin: the kiosk admin panel answers "not configured"' "set the shell PIN: $PinCmd"
    }

    # Автовход пишет сам агент, когда применяет shellReplacement (при старте): AutoAdminLogon, DefaultUserName и, новое
    # в 1.0.20, ForceAutoLogon=1 — после любого выхода Windows сразу снова входит в киоск.
    $kioskOn = Wait-Until { Test-KioskAutoLogon } 90
    if ($kioskOn) {
        Write-Pass "auto-logon goes to '$KioskUser' (AutoAdminLogon=1, ForceAutoLogon=1: again after a sign-out)"
    } else {
        $winlogon = Get-WinlogonState
        $notOnText = 'auto-logon not the kiosk after 90 s: AutoAdminLogon={0}, DefaultUserName={1}, ForceAutoLogon={2}'
        Write-Warn ($notOnText -f $winlogon.AutoAdminLogon, $winlogon.DefaultUserName, $winlogon.ForceAutoLogon)
        if ($rebootBeforeKiosk) {
            # kiosk-on -Golden откажет, пока загрузка старше метки 3010.
            Write-Info 'golden, exit code 3010: kiosk-on waits for the reboot; then the get-shell-kit command offers it'
        } elseif (Read-YesNo 'Run kiosk-on now?' $true) {
            $kioskOnScript = Join-Path $KitDir 'kiosk-on.ps1'
            if (-not (Test-Path -LiteralPath $kioskOnScript)) {
                Write-Fail "$kioskOnScript is missing" "run the get-shell-kit command again ($GetKitCmd)"
            } else {
                # -NoReboot: о перезагрузке спрашивает этот скрипт в конце, когда журнал закрыт.
                $changed = $true
                $kioskOnArgs = @('-KitDir', $KitDir, '-KioskUser', $KioskUser, '-AdminUser', $AdminUser, '-NoReboot') +
                    $modeArgs
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $kioskOnScript @kioskOnArgs
                $kioskOnCode = $LASTEXITCODE
                Write-Info "kiosk-on exit code $kioskOnCode"
                $kioskOn = Wait-Until { Test-KioskAutoLogon } 30
            }
        }
        $winlogon = Get-WinlogonState
        if ($kioskOn) {
            Write-Pass "auto-logon goes to '$KioskUser' now (AutoAdminLogon=1, ForceAutoLogon=1)"
        } elseif ($winlogon.AutoAdminLogon -eq '1' -and $winlogon.DefaultUserName -ieq $KioskUser) {
            # Скобки вокруг «+»: -f связывает сильнее.
            Write-Info (("ForceAutoLogon is '{0}': the kiosk will not log on again after a sign-out until the agent " +
                'applies shellReplacement') -f $winlogon.ForceAutoLogon)
            Write-Info "if it stays so: $KioskOnCmd; then $DiagCmd and a photo to Claude"
        } else {
            Write-Fail ("auto-logon is AutoAdminLogon={0}, DefaultUserName={1}: the PC would not start the kiosk" -f
                $winlogon.AutoAdminLogon, $winlogon.DefaultUserName) (
                "$KioskOnCmd; if it fails: $DiagCmd and photos to Claude")
        }
    }
    $winlogon = Get-WinlogonState
    if ($winlogon.DefaultPassword) {
        Write-Warn 'Winlogon has a clear-text DefaultPassword value (readable by all users; auto-logon uses LSA)' (
            "Remove-ItemProperty -LiteralPath '$WinlogonKey' -Name DefaultPassword")
    }
    if ($winlogon.IgnoreShiftOverride -eq '1') {
        Write-Fail "Winlogon IgnoreShiftOverride=1: Shift at boot does not stop auto-logon, $AdminUser cannot sign in" (
            "Remove-ItemProperty -LiteralPath '$WinlogonKey' -Name IgnoreShiftOverride")
    }

    # Регистрация: агент после рестарта только обновляет токены («Stored agent tokens refreshed»).
    $null = Wait-Until { (Get-Registration).State -eq 'registered' } 30
    $registration = Get-Registration
    switch ($registration.State) {
        'registered' { Write-Pass "agent registered and approved ($($registration.Detail))" }
        'waiting' {
            Write-Warn 'agent log: the PC waits for approval in the kassa' (
                "kassa: 'Zal i ustroystva' -> $env:COMPUTERNAME -> 'Obsluzhivanie' (maintenance) OFF")
        }
        'badKey' { Write-Warn 'agent log: the server rejected the PC key (X-Club-Key)' $SetKeyCmd }
        'failing' { Write-Warn "agent log: registration fails, the agent keeps retrying: $($registration.Detail)" }
        default {
            Write-Warn "no registration in the agent log yet ($DataDir\logs)" "run $DiagCmd and send a photo to Claude"
        }
    }

    $kiosk = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if (-not $kiosk) {
        Write-Fail "kiosk account '$KioskUser' is missing" "$DataDir\logs; send a photo to Claude"
    } else {
        $groups = @(Get-PrivilegedGroupsOf $kiosk.SID.Value)
        if ($groups.Count -eq 0) {
            Write-Pass "'$KioskUser' is a standard user (no admin or operator groups)"
        } else {
            Write-Fail "'$KioskUser' is in privileged groups ($($groups -join ', '))" (
                'remove it from those groups (the agent refuses a privileged kiosk)')
        }
    }
    if (Test-EnabledAdmin $AdminUser) { Write-Pass "$AdminUser is still an enabled administrator" }
    else { Write-Fail "$AdminUser is no longer an enabled administrator" $AdminFix }

    # Перенаправление папок (выключено в agent.json) создало бы <диск>:\ClubShell\Users\<киоск>.
    $redirected = @(@('G:\ClubShell', 'H:\ClubShell') | Where-Object { Test-Path -LiteralPath $_ })
    if ($redirected.Count -gt 0) {
        Write-Fail "folder redirection happened: $($redirected -join ', ')" (
            'send a photo to Claude (agent.json folderRedirect must stay off)')
    } else {
        Write-Pass "no folder redirection to G:/H: ($KioskUser's folders stay in C:\Users\$KioskUser)"
    }

    # Сборка помощника может смениться и сейчас — самообновлением (1.8.0+, кнопка в панели): не ошибка, если служба
    # работает и версия не ниже, чем до установщика.
    $helperBefore = $snapshot.Helper
    $helperAfter = Get-HelperState
    if ($helperAfter -and $helperAfter.State -eq 'Running' -and $helperAfter.Version -and $helperBefore -and
        $helperBefore.Version -and [version]$helperAfter.Version -ge [version]$helperBefore.Version) {
        if ($helperAfter.Hash -eq $helperBefore.Hash) {
            Write-Pass "helper ClubDisklessHelper $($helperAfter.Version) untouched and running"
        } else {
            Write-Pass ("helper ClubDisklessHelper running, updated {0} -> {1} (self-update)" -f $helperBefore.Version,
                $helperAfter.Version)
        }
        if ([version]$helperAfter.Version -lt $HelperMinVersion) {
            Write-Warn ("helper {0} is older than {1}: ClubShell {2} only guesses whether G:/H: are ready" -f
                $helperAfter.Version, $HelperMinVersion, $Version) "update it: $UpdateHelperCmd"
        }
    } else {
        Write-Fail ("helper ClubDisklessHelper stopped, unknown or older than before: now {0}, before {1}" -f
            (Get-HelperLabel $helperAfter), (Get-HelperLabel $helperBefore)) $UpdateHelperCmd
    }
    foreach ($letter in @('G', 'H')) {
        if (Test-Volume $letter) { Write-Pass "${letter}: is still mounted" }
        elseif ($libsAtStart -notcontains $letter) { Write-Info "${letter}: was not mounted before the upgrade either" }
        else { Write-Fail "${letter}: is gone" 'check the helper (panel "Diskless")' }
    }

    $dnsBefore = $snapshot.Dns
    $dnsAfter = Get-DnsSnapshot
    $dnsChanged = @($dnsBefore.Keys | Where-Object { $dnsAfter[$_] -ne $dnsBefore[$_] }) +
        @($dnsAfter.Keys | Where-Object { -not $dnsBefore.ContainsKey($_) -and $dnsAfter[$_] })
    if ($dnsChanged.Count -eq 0) {
        Write-Pass 'DNS servers of every adapter unchanged'
    } else {
        foreach ($name in ($dnsChanged | Select-Object -Unique)) {
            Write-Warn "DNS of $name changed: '$($dnsBefore[$name])' -> '$($dnsAfter[$name])'" (
                'kassa policy: webFilter.dnsServers must be [] (the agent sets DNS on every adapter)')
        }
    }

    # Готовность ПК (SHELL_CHANGES 86): агент пишет «Readiness: ready after» один раз за запуск, когда диски игр готовы.
    if (Wait-Until { Test-AgentLog 'Readiness: ready after' } 60) {
        Write-Pass 'agent log: "Readiness: ready after" (the games disks are ready for sign-in)'
    } else {
        Write-Info ('agent log: no "Readiness: ready after" yet: sign-in opens once the games disks are ready ' +
            '(maybe only after the reboot)')
    }
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
    # Стоп — всегда FAIL (код выхода 2); строки FAIL перед ним уже посчитаны.
    if ($Count.Fail -eq 0) { $Count.Fail++ }
} finally {
    Write-Host ''
    $color = if ($Count.Fail -gt 0) { 'Red' } elseif ($Count.Warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ("Upgrade result for {0}: {1} FAIL, {2} WARN. Log: {3}" -f $env:COMPUTERNAME, $Count.Fail, $Count.Warn,
        $LogDir) -ForegroundColor $color
    if ($upgraded) {
        Write-Info 'No downgrade by the installer: going back = shell-uninstall, then a fresh install of the older kit.'
    }
    # Эталон: перезагрузки нет — что дальше. Выйти из сеанса администратора тоже нельзя: с ForceAutoLogon=1 Windows
    # сразу входит в киоск player (на мастере).
    if ($Golden -and $Count.Fail -gt 0) {
        Write-Host ''
        Write-Host 'GOLDEN: FAIL lines above - do NOT run seal-golden or publish; fix them first.' -ForegroundColor Red
        if ($changed) {
            Write-Host '  Do NOT sign out of the master (the kiosk auto-logon is on).' -ForegroundColor Red
            Write-Host '  Take a photo of this window.' -ForegroundColor Red
        }
    } elseif ($Golden -and $rebootBeforeKiosk) {
        Write-Host ''
        $lines = @('GOLDEN: the installer needs ONE reboot before seal-golden (exit code 3010): no seal/publish yet.',
            "  Reboot the master now; the kiosk is on: hold Shift at boot, sign in as $AdminUser,",
            "  run the get-shell-kit command again (ClubShell $Version is current then), then seal-golden.")
        foreach ($line in $lines) { Write-Host $line -ForegroundColor Yellow }
    } elseif ($Golden) {
        Write-Host ''
        $lines = @("GOLDEN: ClubShell $Version is in the master. Do NOT reboot it or sign out (next logon: the kiosk).",
            '  Next, in this admin window (current folder anything but C:\ClubShellKit):',
            "    powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\seal-golden.ps1",
            "  then panel 'Diskless' -> Publish (docs/shell-install.md, 'Diskless').")
        foreach ($line in $lines) { Write-Host $line -ForegroundColor Yellow }
    }
    try { Stop-Transcript | Out-Null } catch { }
}
# Гибрид: перезагрузка — после журнала (Restart-Computer закрывает окно, итог уже записан), только если что-то
# менялось. Есть FAIL — по умолчанию нет: после перезагрузки в киоск исправлять их можно только через Shift -> Club.
if (-not $Golden -and $changed) {
    $clean = $Count.Fail -eq 0
    if ($clean) {
        Write-Host 'After the reboot the PC logs on to the kiosk player with the new shell (Shift at boot: sign-in).'
    } else {
        $failText = 'There are FAIL lines above: take a photo of this window; reboot once they are sorted out.'
        Write-Host $failText -ForegroundColor Red
    }
    if (Read-YesNo 'Reboot now?' $clean) { Restart-Computer -Force }
}
# 3 — эталону нужна перезагрузка до seal-golden (3010): get-shell-kit отличает это от «без FAIL». 4 — FAIL или стоп,
# а ПК этот запуск не менял (ни помощника, ни установщика, ни kiosk-on): get-shell-kit тогда не советует откат.
if ($PSCommandPath) {
    if ($Count.Fail -gt 0 -and -not $changed) { exit 4 }
    elseif ($Count.Fail -gt 0) { exit 2 }
    elseif ($rebootBeforeKiosk) { exit 3 }
    else { exit 0 }
}
