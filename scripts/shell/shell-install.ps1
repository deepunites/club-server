<#
.SYNOPSIS
    Ставит ClubShell 1.0.20 (касса клуба Deagle) на один гибрид: WebView2, свой agent.json до установки, установщик без
    ключа в командной строке, проверки после установки, пароль Club и PIN администратора киоска, шаги в кассе, после
    одобрения — киоск (kiosk-on.ps1) и перезагрузка в него.
.DESCRIPTION
    Запускать на гибриде под «Club», от администратора. Обычно его запускает get-shell-kit.ps1: одна команда в
    PowerShell от администратора приносит набор по сети клуба в C:\ClubShellKit и запускает этот скрипт оттуда
    (docs/shell-install.md). Без сети клуба — shell-install-run-as-admin.cmd с флешки: лаунчер сначала копирует папку
    набора в C:\ClubShellKit и работает оттуда. Набор должен быть на C: в обоих случаях: сразу после установки агент
    применяет политику, вшитую в MSI (касса ещё не одобрила ПК), — накопители USB выключаются на всей машине, вставленная
    флешка извлекается.
    Сам по сети (irm | iex) этот скрипт не запускается: ему нужны установщик и соседние скрипты набора.
    Только для ПК без ClubShell: где он уже стоит, новую версию ставит shell-upgrade.ps1 (команда get-shell-kit
    предлагает его сама), а здесь — стоп, ничего не изменено.
    Что делает, по порядку:
      1. shell-preflight.ps1: есть FAIL — стоп, ничего не изменено; есть WARN — спрашивает, продолжать ли;
      2. находит ClubShellSetup-1.0.20.exe (C:\ClubShellKit, рядом со скриптом, «Загрузки»; нет — предлагает скачать из
         релиза v1.0.20 на GitHub), сверяет размер и SHA256 из SHA256SUMS.txt релиза, снимает метку «из интернета»;
      3. WebView2 Runtime не зарегистрирован — ставит его install-webview2.ps1 из набора (нужен интернет; не вышло —
         стоп, ClubShell не ставится). Бандл качает загрузчик WebView2 сам и сверяет его с хэшем, зашитым при сборке;
         Microsoft файл обновила, и установка 1.0.18 падала с 0x80091007 (PC-01, 2026-11-01). В бандле 1.0.20 хэш
         загрузчика тот же — на ПК без WebView2 он падает так же. Если WebView2 уже стоит, бандл этот шаг пропускает;
      4. запоминает DNS всех адаптеров, SHA256 и версию помощника — для сверки после установки;
         спрашивает ключ ПК (ввод скрыт) и пишет C:\ProgramData\ClubShell\agent.json ДО установки, с правами только
         SYSTEM и Administrators. MSI видит готовый файл и оставляет его как есть (свой agent.json он пишет только на
         чистый ПК, KIOSKUSER в готовый файл не пишет), поэтому ключ не попадает ни в командную строку, ни в журналы
         установки (SHELL_CHANGES 70). При первом старте агент переносит ключ в secure\club.key (DPAPI) и стирает его в
         agent.json. В файле — только то, что касса не задаёт (настройки агента локальные, сервер их не присылает):
           server             — API ClubShell на Railway (clubshell-server-production), а не адрес кассы
                                (clubshell-admin-production — отдельное приложение, агенту не отвечает);
           shell.kioskUser    — киоск «player» (по умолчанию «club» = наш Club: агент отказался бы, и игрок остался бы
                                на рабочем столе администратора); removeFromPrivilegedGroups=false — иначе агент мог бы
                                вынуть совпавшую учётку из администраторов; перенаправление папок ВЫКЛ: иначе при первом
                                старте агент уводит Рабочий стол, Документы, Загрузки игрока на диск с наибольшим
                                свободным местом — наш H: или G: (личные диски, сбрасываются при загрузке), и выключение
                                потом этого не отменяет;
           games              — корни библиотек G:\Steam (библиотека Steam, даже без libraryfolders.vdf) и H:\ (игры
                                владельца лежат прямо в H:\<Игра>; записи каталога с полным путём H:\<Игра>\<exe>
                                находятся и без корней); Steam — G:\Steam\steam.exe (по умолчанию C:\Program Files (x86));
           storage.gamesShare — выкл: G: и H: подключает наш помощник (при нём агент и так ничего не монтирует);
           updates            — выкл: автообновление не работает (манифест релиза 1.0.20 по-прежнему не подписан —
                                агент его отвергнет, скачав 72 МБ); обновляем запуском нового установщика
                                (shell-upgrade.ps1 через get-shell-kit);
      5. ставит ClubShellSetup-1.0.20.exe /quiet /norestart KIOSKUSER=player INSTALLSHELL=1. НЕ передаёт SERVERURL и
         CLUBAPIKEY (SERVERURL переписал бы адреса в agent.json; ключ попал бы в командную строку процесса бандла, а
         1.0.20 кладёт его ещё и в HKLM\SOFTWARE\ClubShell\Enrollment) и ничего из INSTALLDISKLESS / DISKLESS* (в
         релизном MSI помощника нет; наш помощник не трогаем). KIOSKUSER нужен всё равно: MSI запоминает его в
         HKLM\SOFTWARE\ClubShell\KioskUser, по нему работает удаление;
      6. проверяет: служба агента работает; ключ ушёл из agent.json в secure\club.key; киоск «player» создан и не в
         привилегированных группах; Club — по-прежнему администратор; папок ClubShell на G:/H: нет (перенаправления не
         было); помощник работает, версия не ниже, чем до установки (самообновление 1.8.0+ — не ошибка), G:/H: на
         месте; DNS не изменился; журнал агента (регистрация: ждёт одобрения / ключ не принят / помощник признан
         владельцем G:). Автовход здесь — только строка для сведения, не WARN: агент пишет его
         вместе с шеллом в профиль player, а профиля нет, пока в киоск никто не входил (PC-01: ни до, ни после
         одобрения), — профиль создаёт шаг 9;
      7. предлагает задать пароль Club (set-club-password.ps1) и PIN панели администратора киоска (set-shell-pin.ps1);
      8. печатает, что сделать в кассе, и по желанию ждёт, пока владелец одобрит ПК;
      9. одобрен — kiosk-on.ps1 -NoReboot (Club — включённый администратор и с паролем, иначе киоск не включается;
         AutoPlay выключен, шелл в шаблоне профиля Default, профиль player и автовход в него — enable-kiosk-autologon.ps1)
         и в самом конце, когда журнал уже закрыт, спрашивает, перезагрузить ли ПК в киоск (есть FAIL — по умолчанию нет).
         Не одобрен за время ожидания — WARN: после одобрения C:\ClubShellKit\kiosk-on-run-as-admin.cmd; до этого
         автовход идёт в Club, и после нового пароля Club перезагрузка останавливается на экране входа.
    -Golden — эталон бездиска в режиме мастера (передаёт get-shell-kit, когда видит сеанс …:diskless-master; вместе с
    -AdminUser <администратор эталона>). Шелл ставится в эталон один раз, места получают его с публикацией. Отличия:
    проверка — shell-preflight -Golden (имя USER, сеанс мастера, пароль администратора эталона уже задан); agent.json
    и установщик — те же (paths.persistent не пишется: постоянного тома у мест нет); G:/H: после установки сверяются с
    тем, что было до неё; пароль — по умолчанию не менять (задан до установки); шаг 8 — мастер одобряется как ЭТОТ
    физический ПК Primer (тот же HWID, что у него как у места): его номер, зона Primer, «Без постоянного диска», и эту
    запись в кассе не удалять; шаг 9 — kiosk-on -Golden; перезагрузки нет совсем (следующая загрузка мастера — уже
    киоск player в эталоне): в конце — что дальше (seal-golden.ps1 из набора, потом «Опубликовать»), но только без
    FAIL: есть FAIL — красным «seal-golden и публикацию не запускать» (seal-golden большую часть проверок шага 6 не
    повторяет: DNS, помощник, перенаправление папок, G:/H:). Установщик ответил 3010 — киоск не включается:
    перезагрузить мастер один раз сейчас (до киоска), войти администратором, снова команду get-shell-kit (предложит
    kiosk-on); метка C:\ClubShellKit\reboot-before-kiosk — kiosk-on -Golden откажет, пока загрузка старше неё (3010 от
    MSI не виден в ключах RebootPending, которые смотрит проверка). Код выхода при запуске файлом: 0 — без FAIL,
    2 — есть FAIL, 3 — без FAIL, но эталону до киоска нужна перезагрузка (3010).
    Ключ ПК, пароль и PIN не печатаются и не пишутся в файлы (ключ — только в agent.json до первого старта агента).
    Установка не удалась — заготовленный agent.json удаляется (ключ не остаётся на диске).
    Журнал этого скрипта — C:\ClubShellKit\logs\install-<ПК>-<время>.txt, установщика — setup-<время>*.log там же.
    Строки вывода — только ASCII, без BOM и без #Requires (как у остальных скриптов набора).
.EXAMPLE
    shell-install-run-as-admin.cmd
#>
param(
    [string]$KitDir = 'C:\ClubShellKit',
    [string]$ServerUrl = 'https://clubshell-server-production.up.railway.app',
    [string]$KioskUser = 'player',
    [string]$AdminUser = 'Club',
    [switch]$Golden
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # прогресс-бар Invoke-WebRequest в 5.1 замедляет скачивание в разы

# Релиз v1.0.20 (тег -> dbe2091, 2026-11-08): бандл Burn = WebView2 (если нет) + ClubShell-1.0.20.msi (агент, оболочка).
# Размер и SHA256 — из SHA256SUMS.txt релиза (совпадает с загруженной копией). ClubShell-Shell-1.0.20.* — только
# оболочка, без агента и в другую папку: не ставить. 1.0.19 не ставить никогда (события агента не доходили до экрана).
# Те же значения — в shell-preflight.ps1 и shell-upgrade.ps1 (менять вместе).
$Version = '1.0.20'
$SetupName = 'ClubShellSetup-1.0.20.exe'
$SetupSize = 75550872
$SetupSha256 = '0AC2A30BB47EFE3E00D3E4FF3907520CB21B88E2711ECD3D90BB428D0C4C0B94'
$SetupUrl = 'https://github.com/Nightcall7442/club-shell/releases/download/v1.0.20/ClubShellSetup-1.0.20.exe'
$GetKitCmd = 'irm http://192.168.1.201:5080/pxe/v1/files/tools/get-shell-kit.ps1 | iex'
# Версия помощника — по SHA256 exe (Get-HelperVersion): helper-build.json службы 1.8.0+ или таблица известных сборок
# (SHA256 exe -> версия) — та же, что в shell-preflight.ps1 (менять вместе).
$HelperBuildFile = Join-Path $env:ProgramData 'ClubDiskless\helper-build.json'
$HelperBuilds = @{
    '26635a5f7aab4d77441d0b0138064b9c7f64bb1daf4b0bcaf09052439ddd0148' = '1.6.0'   # 1.6.0+13609a5
    '41da96a0b0721b22bb5f5e1898c927f08c4856968ee059a0c40b3d2b54a30e23' = '1.7.0'   # 1.7.0+f79e0d5
    '05a73a8f0cb1717040c7f64fe1c94104fbdf501b9b196f90659c56d0c33b31e1' = '1.8.0'   # 1.8.0+e77f0ad
    '1af4465530dd2d17fa287f9d90f11f0250262940193d1dfc88931e66e4c9ac56' = '1.9.0'   # 1.9.0+184aabf
}
# Корни библиотек и Steam (agent.json -> games). Массив из файла заменяет массив по умолчанию (D:\Games, G:\Games) целиком.
$LibraryRoots = @('G:\Steam', 'H:\')
$SteamExe = 'G:\Steam\steam.exe'
$DataDir = Join-Path $env:ProgramData 'ClubShell'
$AgentJson = Join-Path $DataDir 'agent.json'
$ClubKeyFile = Join-Path $DataDir 'secure\club.key'
$WinlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$WebView2Key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$PrivilegedGroupSids = @('S-1-5-32-544', 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551', 'S-1-5-32-547', 'S-1-5-32-556', 'S-1-5-32-578')
$Count = @{ Fail = 0; Warn = 0 }

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

# Файлы набора — только наследуемые права от папки (старый лаунчер мог оставить их с пустым DACL). icacls — с
# 'Continue': в 5.1 перенаправленный stderr внешней программы при 'Stop' стал бы исключением; пустую папку не трогаем
# (свежая C:\ClubShellKit при запуске руками с флешки — icacls ответил бы ошибкой на «*»). Как в get-shell-kit.ps1.
function Reset-KitFileAcl {
    $ErrorActionPreference = 'Continue'
    if (@(Get-ChildItem -LiteralPath $KitDir -Force -ErrorAction SilentlyContinue).Count -eq 0) { return }
    & icacls.exe "$KitDir\*" /reset /T /C /Q 2>&1 | Out-Null
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 3
    }
    [bool](& $Condition)
}

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

function Get-PrivilegedGroupsOf([string]$UserSid) {
    $names = @()
    foreach ($groupSid in $PrivilegedGroupSids) {
        if ((Get-GroupMemberSids $groupSid) -contains $UserSid) { $names += $groupSid }
    }
    $names
}

function Get-DnsSnapshot {
    $map = @{}
    Get-DnsClientServerAddress -ErrorAction SilentlyContinue | ForEach-Object {
        $map["$($_.InterfaceAlias) ($($_.AddressFamily))"] = (@($_.ServerAddresses) -join ', ')
    }
    $map
}

# Версия помощника по SHA256 его exe ($Hash): helper-build.json ({"v":1,"version":"x.y.z","build":"<sha256>"}, пишет сама
# служба 1.8.0+ при каждом старте) — только если build = SHA256 exe; иначе таблица $HelperBuilds; иначе $null (неизвестная
# сборка). Версию файла exe не читаем: exe собирается на Linux, и ресурс версии в нём — от среды .NET (10.0.x), а не от
# помощника. Та же функция — в shell-preflight.ps1 и shell-uninstall.ps1 (менять вместе).
function Get-HelperVersion([string]$Hash) {
    if (-not $Hash) { return $null }
    $sha = $Hash.ToLowerInvariant()
    try {
        $info = [IO.File]::ReadAllText($HelperBuildFile) | ConvertFrom-Json
        if ($info.v -eq 1 -and "$($info.build)".ToLowerInvariant() -eq $sha -and "$($info.version)" -match '^\d+\.\d+\.\d+$') { return "$($info.version)" }
    } catch { }
    if ($HelperBuilds.ContainsKey($sha)) { return $HelperBuilds[$sha] }
    $null
}

function Get-HelperState {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='ClubDisklessHelper'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    $exe = [Environment]::ExpandEnvironmentVariables(($service.PathName -replace '^\s*"([^"]+)".*$', '$1').Trim())
    $hash = if (Test-Path -LiteralPath $exe) { (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash } else { $null }
    [pscustomobject]@{ State = $service.State; Exe = $exe; Hash = $hash; Version = (Get-HelperVersion $hash) }
}

# Ключ в agent.json уже стёрт агентом и лежит в secure\club.key. Значение ключа не читается в переменные и не печатается:
# проверяется только, что поле пустое.
function Test-KeyMoved {
    if (-not (Test-Path -LiteralPath $ClubKeyFile)) { return $false }
    try { $config = [IO.File]::ReadAllText($AgentJson) | ConvertFrom-Json } catch { return $false }
    [string]::IsNullOrEmpty([string]$config.server.clubApiKey)
}

# Строка журнала разбирается JavaScriptSerializer, а не ConvertFrom-Json: после первой регистрации агент добавляет к
# каждому событию "pcId", и строки с {PcId} в шаблоне несут оба ключа — ConvertFrom-Json в 5.1 такие отвергает (ключи
# различаются только регистром). Словарь сериализатора различает регистр. Как в kiosk-on.ps1. В PowerShell 7 сборки
# нет — там ConvertFrom-Json -AsHashtable (он такие строки принимает).
$JsonReader = $null
try {
    Add-Type -AssemblyName System.Web.Extensions
    $JsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer
} catch { }
# Грубый фильтр строк: только шаблоны, которые разбирает Get-AgentLogState.
$AgentLogFilter = '"@mt"\s*:\s*"(Games library volume is managed by|Registered as PC|Stored agent tokens refreshed|' +
    'Registration attempt)'

# Состояние агента по его журналам (три последних agent-*.json, Serilog compact JSON: шаблон сообщения в "@mt",
# исключение в "@x"); регистрацию решает последняя подходящая строка, как Get-Registration в kiosk-on.ps1: «Registered
# as PC» — одобрен; «Stored agent tokens refreshed» — тоже (зарегистрированный агент при старте только обновляет
# токены, «Registered» в новых журналах уже нет); «Registration attempt failed» — ждёт одобрения / ключ не принят /
# ошибка; строк нет, но есть secure\agent.tokens — одобрен (токены выдаются только одобренному ПК). Сначала грубый
# фильтр по тексту: разбор тысяч строк в 5.1 медленный. Печатается только вывод, не строки журнала.
function Get-AgentLogState {
    $state = @{ Registration = 'none'; Detail = ''; HelperStandDown = $false; KioskRefused = $false }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $DataDir 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 3)
    foreach ($log in $logs) {
        foreach ($line in @(Get-Content -LiteralPath $log.FullName -Tail 3000 -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            if ($line -notmatch '"@mt"') { continue }
            if ($line -match 'KioskAccountRefused|privilegedAccount') { $state.KioskRefused = $true }
            if ($line -notmatch $AgentLogFilter) { continue }
            try {
                if ($JsonReader) { $entry = $JsonReader.DeserializeObject($line) }
                else { $entry = $line | ConvertFrom-Json -AsHashtable }
            } catch { continue }
            if ($entry -isnot [System.Collections.IDictionary]) { continue }
            $template = [string]$entry['@mt']
            if ($template -like 'Games library volume is managed by*') { $state.HelperStandDown = $true }
            elseif ($template -like 'Registered as PC*') { $state.Registration = 'registered'; $state.Detail = "name $($entry['PcName']), zone $($entry['Zone'])" }
            elseif ($template -like 'Stored agent tokens refreshed*') { $state.Registration = 'registered'; $state.Detail = 'agent tokens refreshed' }
            elseif ($template -like 'Registration attempt*failed*') {
                $exception = [string]$entry['@x']
                if ($exception -match 'waiting for approval|pendingApproval') { $state.Registration = 'waiting'; $state.Detail = '' }
                elseif ($exception -match 'X-Club-Key') { $state.Registration = 'badKey'; $state.Detail = '' }
                else {
                    $first = (($exception -split "`n")[0]).Trim()
                    if ($first.Length -gt 160) { $first = $first.Substring(0, 160) }
                    $state.Registration = 'failing'; $state.Detail = $first
                }
            }
        }
    }
    if ($state.Registration -eq 'none' -and (Test-Path -LiteralPath (Join-Path $DataDir 'secure\agent.tokens'))) {
        $state.Registration = 'registered'; $state.Detail = 'agent tokens present'
    }
    $state
}

function Show-Registration($State) {
    switch ($State.Registration) {
        'registered' { Write-Pass "agent registered and approved ($($State.Detail))" }
        'waiting' { Write-Pass 'agent reached the server: the PC waits for approval in the kassa' }
        'badKey' { Write-Fail 'the server rejected the PC key (X-Club-Key)' 'shell-uninstall (remove data), then install again with the right key' }
        'failing' { Write-Warn "registration fails, the agent keeps retrying: $($State.Detail)" }
        default { Write-Warn "no registration in the agent log yet ($DataDir\logs)" }
    }
}

# ======================================================================================================================
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}

# Набор — в C:\ClubShellKit (лаунчер копирует его сам; это — если скрипт запустили руками с флешки).
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path $KitDir | Out-Null
# Только SYSTEM и администраторы: игрок (киоск) не должен подменять скрипты, которые потом запускает администратор.
$kitAcl = New-Object System.Security.AccessControl.DirectorySecurity
$kitAcl.SetSecurityDescriptorSddlForm('D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
Set-Acl -LiteralPath $KitDir -AclObject $kitAcl
# До копирования: файл с пустым DACL не перезаписался бы.
Reset-KitFileAcl
if ($here -and $here.TrimEnd('\') -ne $KitDir.TrimEnd('\')) {
    Get-ChildItem -LiteralPath $here -File | Where-Object { $_.Extension -in @('.ps1', '.cmd') -or $_.Name -eq $SetupName } |
        Copy-Item -Destination $KitDir -Force
}
$LogDir = Join-Path $KitDir 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
try { Start-Transcript -LiteralPath (Join-Path $LogDir "install-$env:COMPUTERNAME-$stamp.txt") | Out-Null } catch { }

$wroteAgentJson = $false
$createdDataDir = $false
$installed = $false
$rebootIntoKiosk = $false
$goldenKioskOn = $false
$rebootBeforeKiosk = $false
# Режим эталона — дальше в проверку и kiosk-on.
$modeArgs = @()
if ($Golden) { $modeArgs = @('-Golden') }
$kioskOnCmd = Join-Path $KitDir 'kiosk-on-run-as-admin.cmd'
# Эталон, установщик ответил 3010: метка для kiosk-on -Golden (он не включит киоск, пока загрузка старше неё).
$RebootMarker = Join-Path $KitDir 'reboot-before-kiosk'
if ($Golden) { $kioskOnCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\kiosk-on.ps1 -Golden -AdminUser '$AdminUser' (or the get-shell-kit command again)" }
try {
    Write-Host "ClubShell $Version install on $env:COMPUTERNAME (kiosk account '$KioskUser', server $ServerUrl)" -ForegroundColor Cyan
    if ($Golden) { Write-Host "GOLDEN MODE: into the diskless golden image (master session), admin '$AdminUser'; no reboot at the end" -ForegroundColor Cyan }
    if ($KioskUser -ieq $AdminUser) { throw "Kiosk account '$KioskUser' is the admin account '$AdminUser'. Nothing was changed." }

    # --- 1. Проверка ----------------------------------------------------------------------------------------------
    Write-Title 'Step 1: preflight check'
    $preflight = Join-Path $KitDir 'shell-preflight.ps1'
    if (-not (Test-Path -LiteralPath $preflight)) { throw "$preflight is missing: copy the whole kit folder. Nothing was changed." }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $preflight -ServerUrl $ServerUrl -KioskUser $KioskUser -AdminUser $AdminUser -KitDir $KitDir @modeArgs
    $preflightCode = $LASTEXITCODE
    if ($preflightCode -notin @(0, 1, 2)) { throw "The preflight check itself failed (exit code $preflightCode). Nothing was changed." }
    if ($preflightCode -ge 2) { throw 'The preflight check has FAIL lines (above). Fix them and run again. Nothing was changed.' }
    if ($preflightCode -eq 1 -and -not (Read-YesNo 'The check has WARN lines (above). Install anyway?' $false)) {
        throw 'Stopped at the WARN lines. Nothing was changed.'
    }

    # --- 2. Установщик ----------------------------------------------------------------------------------------------
    Write-Title "Step 2: installer $SetupName"
    $setup = Join-Path $KitDir $SetupName
    if (-not (Test-Path -LiteralPath $setup)) {
        $candidate = Join-Path $env:USERPROFILE "Downloads\$SetupName"
        if (Test-Path -LiteralPath $candidate) { Copy-Item -LiteralPath $candidate -Destination $setup -Force }
    }
    if (-not (Test-Path -LiteralPath $setup)) {
        Write-Info "$SetupName is not in $KitDir or Downloads."
        if (-not (Read-YesNo "Download it from GitHub (release v$Version, 72 MB)?" $true)) {
            throw "Put $SetupName (release v$Version) into $KitDir and run again. Nothing was changed."
        }
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        Write-Info "downloading $SetupUrl"
        Invoke-WebRequest -UseBasicParsing -Uri $SetupUrl -OutFile "$setup.part"
        Move-Item -LiteralPath "$setup.part" -Destination $setup -Force
    }
    $setupLength = (Get-Item -LiteralPath $setup).Length
    $setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
    if ($setupLength -ne $SetupSize -or $setupHash -ne $SetupSha256) {
        throw "$setup is not the official v$Version file (size $setupLength, SHA256 $setupHash). Delete it and download again. Nothing was changed."
    }
    # Скачанный браузером файл несёт метку зоны: без неё SmartScreen не спрашивает про неподписанный установщик.
    Unblock-File -LiteralPath $setup
    Write-Pass "installer verified: $setupLength bytes, SHA256 $SetupSha256 (release v$Version, unsigned)"

    # --- 3. WebView2 Runtime ------------------------------------------------------------------------------------------
    # Та же проверка, что в install-webview2.ps1. Свой загрузчик WebView2 у бандла устарел (0x80091007; хэш в 1.0.20 тот
    # же, что в 1.0.18): ставим заранее, тогда бандл этот шаг пропускает.
    Write-Title 'Step 3: WebView2 Runtime'
    $webView = Get-RegValue $WebView2Key 'pv'
    if ($webView -and $webView -ne '0.0.0.0') {
        Write-Pass "WebView2 Runtime $webView is installed"
    } else {
        $webViewScript = Join-Path $KitDir 'install-webview2.ps1'
        if (-not (Test-Path -LiteralPath $webViewScript)) { throw "$webViewScript is missing: copy the whole kit folder. Nothing was changed." }
        Write-Info 'not installed: installing it first (install-webview2.ps1, needs internet)'
        # -FromInstall: без подсказки «теперь запустите shell-install» — он уже идёт.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $webViewScript -FromInstall
        if ($LASTEXITCODE -ne 0) { throw "WebView2 Runtime install failed (exit code $LASTEXITCODE, see above; it needs internet). Nothing was changed." }
        $webView = Get-RegValue $WebView2Key 'pv'
        if (-not $webView -or $webView -eq '0.0.0.0') { throw 'WebView2 Runtime is still not registered. Nothing was changed.' }
        Write-Pass "WebView2 Runtime $webView installed"
    }

    # --- 4. Снимок для сверки, agent.json с ключом ПК ---------------------------------------------------------------------
    $dnsBefore = Get-DnsSnapshot
    $helperBefore = Get-HelperState
    # Эталон: G:/H: в сеансе мастера бывают не подключены (WARN проверки) — после установки сверяем с этим.
    $libsBefore = @(@('G', 'H') | Where-Object { Get-Volume -DriveLetter $_ -ErrorAction SilentlyContinue })
    $dnsBefore.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name): $($_.Value)" } |
        Set-Content -LiteralPath (Join-Path $LogDir "dns-before-$stamp.txt") -Encoding UTF8

    Write-Title 'Step 4: agent.json (before the installer runs)'
    # ClubShell уже стоит — новая версия ставится обновлением (shell-upgrade.ps1 из get-shell-kit), а не установкой.
    if (Test-Path -LiteralPath $AgentJson) {
        if (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue) {
            throw "ClubShell is already installed ($AgentJson exists): to update it run the get-shell-kit command ($GetKitCmd), it offers the upgrade. Nothing was changed."
        }
        throw "$AgentJson is left over from an earlier install: run shell-uninstall first. Nothing was changed."
    }
    # Ключ клуба Deagle, а не Club__EnrollmentKey с Railway: тот принадлежит первому клубу «ClubShell», и PC-01 с ним
    # зарегистрировался там (в кассе Deagle ПК не было видно).
    Write-Info 'The PC key of club Deagle: ClubShell superadmin -> "Novyi klyuch dlya PK" (New PC key).'
    Write-Info 'NOT Club__EnrollmentKey from Railway: that is the key of the first club "ClubShell" (PC-01 landed there).'
    Write-Info 'Type or paste it (right click pastes); nothing is shown while you type.'
    $secureKey = Read-Host 'PC key' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $secureKey.Dispose() }
    # Ключ могли вставить из буфера обмена — не оставлять его там.
    try { Set-Clipboard -Value ' ' } catch { }
    # Формат ключа — как проверяет страница кассы «Установка на ПК»; сам ключ в сообщения не попадает.
    if ($key -notmatch '^[A-Za-z0-9._-]{8,128}$') {
        $key = $null
        throw 'The key does not look right (8-128 characters: letters, digits, dot, dash, underscore). Nothing was changed.'
    }

    $origin = $ServerUrl.Trim().TrimEnd('/') -replace '/api/v\d+$', ''
    $config = [ordered]@{
        server  = [ordered]@{
            baseUrl    = "$origin/api/v1"
            wsUrl      = ($origin -replace '^http', 'ws') + '/ws/agent'
            clubApiKey = $key
        }
        shell   = [ordered]@{
            kioskUser = [ordered]@{
                name                       = $KioskUser
                removeFromPrivilegedGroups = $false
                folderRedirect             = [ordered]@{ enabled = $false; root = $null; clearOnReset = $true }
            }
        }
        games   = [ordered]@{
            libraryRoots = $LibraryRoots
            launchers    = [ordered]@{ steam = [ordered]@{ exePath = $SteamExe } }
        }
        storage = [ordered]@{ gamesShare = [ordered]@{ enabled = $false } }
        updates = [ordered]@{ enabled = $false }
    }
    $json = $config | ConvertTo-Json -Depth 8
    $key = $null
    $config.server.clubApiKey = $null

    if (-not (Test-Path -LiteralPath $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir | Out-Null
        $createdDataDir = $true
    }
    # Файл создаётся сразу с DACL «SYSTEM и Administrators» (без наследования): ключ ни на миг не читается группой Users.
    # Агент потом перезаписывает файл атомарно (новый файл наследует права папки — ключа в нём уже нет).
    $security = New-Object System.Security.AccessControl.FileSecurity
    $security.SetSecurityDescriptorSddlForm('D:PAI(A;;FA;;;SY)(A;;FA;;;BA)')
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
    $json = $null
    try { $stream = [IO.File]::Create($AgentJson, 4096, [IO.FileOptions]::None, $security) }
    catch {
        # Такой перегрузки нет в .NET Core (PowerShell 7): пустой файл, его права, потом содержимое.
        [IO.File]::WriteAllBytes($AgentJson, [byte[]]@())
        Set-Acl -LiteralPath $AgentJson -AclObject $security
        $stream = [IO.File]::Open($AgentJson, [IO.FileMode]::Truncate, [IO.FileAccess]::Write)
    }
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
    $wroteAgentJson = $true
    Write-Pass "$AgentJson written (SYSTEM + Administrators only): kiosk '$KioskUser', folder redirection off, library roots $($LibraryRoots -join ' '), Steam $SteamExe, updates off"

    # --- 5. Установка ---------------------------------------------------------------------------------------------------
    Write-Title "Step 5: $SetupName /quiet"
    $setupLog = Join-Path $LogDir "setup-$stamp.log"
    $arguments = '/quiet /norestart /log "{0}" KIOSKUSER={1} INSTALLSHELL=1' -f $setupLog, $KioskUser
    Write-Info "running: $SetupName $arguments"
    Write-Info 'a few minutes, no windows; the agent starts at the end and turns USB storage off on this PC'
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($exitCode -eq 0) { Write-Pass 'setup finished (exit code 0)' }
    elseif ($exitCode -eq 3010 -and $Golden) {
        # Эталон: перезагрузка — только до kiosk-on (после него мастер загрузился бы в киоск player).
        Write-Pass 'setup finished (exit code 3010: Windows wants a reboot; golden: once, before the kiosk)'
        $rebootBeforeKiosk = $true
        # MSI 3010 — обычно PendingFileRenameOperations, не ключи RebootPending: без метки второй запуск get-shell-kit
        # предложил бы kiosk-on, и эталон ушёл бы в seal-golden с несделанной перезагрузкой.
        try { Set-Content -LiteralPath $RebootMarker -Value "ClubShell setup exit code 3010 at $(Get-Date -Format s): reboot the master once before kiosk-on" -Encoding ASCII -ErrorAction Stop }
        catch { Write-Warn "cannot write $RebootMarker ($($_.Exception.Message))" 'reboot the master once before kiosk-on anyway' }
    }
    elseif ($exitCode -eq 3010) { Write-Pass 'setup finished (exit code 3010: Windows wants a reboot)' }
    else {
        $hint = switch ($exitCode) {
            1602 { 'cancelled' }
            1618 { 'another install is running: wait or reboot' }
            1638 { "another version of ClubShell is already installed: the get-shell-kit command ($GetKitCmd) offers the upgrade of an older one" }
            default { 'see the setup log' }
        }
        throw "Setup failed with exit code $exitCode ($hint). Logs: $setupLog and the *_ClubShellMsi*.log next to it."
    }
    $installed = $true

    # --- 6. Проверки после установки ------------------------------------------------------------------------------------
    Write-Title 'Step 6: checks after the install'
    $running = Wait-Until { (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue).Status -eq 'Running' } 60
    if (-not $running) {
        try { Start-Service -Name ClubShellAgent -ErrorAction Stop } catch { }
        $running = Wait-Until { (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue).Status -eq 'Running' } 30
    }
    if ($running) { Write-Pass 'service ClubShellAgent is running' }
    else { Write-Fail 'service ClubShellAgent is not running' "Event Viewer -> Application, source ClubShellAgent; $DataDir\logs" }

    if (Wait-Until { Test-KeyMoved } 90) { Write-Pass 'PC key moved into secure\club.key (DPAPI); agent.json has no key now' }
    else { Write-Warn 'the PC key is still in agent.json (readable only by SYSTEM and Administrators)' "agent log $DataDir\logs; the agent moves it at its next start" }

    # Учётку киоска агент создаёт при старте, без кассы. Автовход — часть политики shellReplacement: она включена и в
    # политике из MSI (Agent\config\policies.example.json — её агент применяет до одобрения), и в политике кассы, но
    # агент сначала пишет шелл в профиль player и падает, пока профиля нет (в киоск никто не входил; PC-01 — и до, и
    # после одобрения). Поэтому ждём только учётку, а автовход — строка для сведения: профиль создаёт шаг 9 (kiosk-on),
    # и тогда автовход включает сам агент.
    $null = Wait-Until { [bool](Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue) } 120
    $kiosk = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if ($kiosk) { Write-Pass "kiosk account '$KioskUser' exists" } else { Write-Fail "kiosk account '$KioskUser' was not created" "$DataDir\logs" }
    $autoLogon = Get-RegValue $WinlogonKey 'AutoAdminLogon'
    $autoLogonUser = Get-RegValue $WinlogonKey 'DefaultUserName'
    if ($autoLogon -eq '1' -and $autoLogonUser -eq $KioskUser) { Write-Pass "auto-logon already goes to '$KioskUser'" }
    else { Write-Info ("auto-logon is AutoAdminLogon={0}, DefaultUserName={1}: step 9 (kiosk on) switches it to '{2}' after the approval" -f $autoLogon, $autoLogonUser, $KioskUser) }
    if ($kiosk) {
        $groups = @(Get-PrivilegedGroupsOf $kiosk.SID.Value)
        if ($groups.Count -eq 0) { Write-Pass "'$KioskUser' is a standard user (no admin or operator groups)" }
        else { Write-Fail "'$KioskUser' is in privileged groups ($($groups -join ', '))" 'shell-uninstall-run-as-admin.cmd' }
    }
    $adminAccount = Get-LocalUser -Name $AdminUser -ErrorAction SilentlyContinue
    if ($adminAccount -and (Get-GroupMemberSids 'S-1-5-32-544') -contains $adminAccount.SID.Value) { Write-Pass "$AdminUser is still an administrator" }
    else { Write-Fail "$AdminUser is no longer an administrator" "Computer Management -> Local Users and Groups -> Administrators: add $AdminUser back" }

    # Перенаправление папок (выключено в agent.json) создало бы <диск>:\ClubShell\Users\<киоск>.
    $redirected = @('G:\ClubShell', 'H:\ClubShell') | Where-Object { Test-Path -LiteralPath $_ }
    if ($redirected) { Write-Fail "folder redirection happened: $($redirected -join ', ')" 'shell-uninstall (remove the player profile), then install again' }
    else { Write-Pass "no folder redirection to G:/H: ($KioskUser's folders stay in C:\Users\$KioskUser)" }

    # Сборка помощника может смениться и во время установки — самообновлением (1.8.0+, кнопка в панели): это не ошибка,
    # если служба работает и версия не ниже, чем до установки.
    $helperAfter = Get-HelperState
    if ($helperAfter -and $helperAfter.State -eq 'Running' -and $helperAfter.Version -and $helperBefore -and $helperBefore.Version -and
        [version]$helperAfter.Version -ge [version]$helperBefore.Version) {
        if ($helperAfter.Hash -eq $helperBefore.Hash) { Write-Pass "helper ClubDisklessHelper $($helperAfter.Version) untouched and running" }
        else { Write-Pass "helper ClubDisklessHelper running, updated $($helperBefore.Version) -> $($helperAfter.Version) during the install (self-update)" }
    } else {
        Write-Fail 'helper ClubDisklessHelper stopped, unknown or older than before the install' 'irm http://192.168.1.201:5080/pxe/v1/files/tools/update-helper-net.ps1 | iex'
    }
    foreach ($letter in @('G', 'H')) {
        if (Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue) { Write-Pass "${letter}: is still mounted" }
        elseif ($Golden -and $libsBefore -notcontains $letter) { Write-Info "${letter}: was not mounted before the install either (master session)" }
        else { Write-Fail "${letter}: is gone" 'check the helper (panel "Diskless")' }
    }

    $dnsAfter = Get-DnsSnapshot
    $dnsChanged = @($dnsBefore.Keys | Where-Object { $dnsAfter[$_] -ne $dnsBefore[$_] }) + @($dnsAfter.Keys | Where-Object { -not $dnsBefore.ContainsKey($_) -and $dnsAfter[$_] })
    if ($dnsChanged.Count -eq 0) { Write-Pass 'DNS servers of every adapter unchanged' }
    else {
        foreach ($name in ($dnsChanged | Select-Object -Unique)) { Write-Fail "DNS of $name changed: '$($dnsBefore[$name])' -> '$($dnsAfter[$name])'" 'kassa policy: webFilter.dnsServers must be [] (the agent sets DNS on every adapter)' }
    }

    $usbStart = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' 'Start'
    if ($usbStart -eq 4) { Write-Info 'USBSTOR Start=4: USB storage is off for the whole PC (policy usb.allowStorage=false); expected' }
    else { Write-Info "USBSTOR Start=$usbStart" }

    Start-Sleep -Seconds 5
    $logState = Get-AgentLogState
    if ($logState.HelperStandDown) { Write-Pass 'agent log: "Games library volume is managed by ClubDisklessHelper" (the agent leaves G:/H: alone)' }
    else { Write-Warn 'agent log has no "managed by ClubDisklessHelper" line yet' }
    if ($logState.KioskRefused) { Write-Fail "agent log: the kiosk account was refused as privileged" 'shell-uninstall, fix the account, install again' }
    Show-Registration $logState

    # --- 7. Пароль Club и PIN --------------------------------------------------------------------------------------------
    Write-Title "Step 7: $AdminUser password and the kiosk admin PIN"
    Write-Info "With the kiosk on, holding Shift at boot skips auto-logon and shows the sign-in screen: $AdminUser must have a password before players arrive."
    $passwordScript = Join-Path $KitDir 'set-club-password.ps1'
    # Эталон: пароль администратора уже задан (пустой — FAIL проверки), менять не по умолчанию; PIN — один на все места.
    if ($Golden) { Write-Info 'Golden: the admin password and the PIN go to every seat with the publish (seal-golden keeps secure\admin-pin).' }
    if ((Test-Path -LiteralPath $passwordScript) -and (Read-YesNo "Set (or change) the $AdminUser password now?" (-not $Golden))) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $passwordScript -AdminUser $AdminUser
        if ($LASTEXITCODE -and $Golden) { Write-Info "$AdminUser password not changed (it was set before the install: the preflight checked it)" }
        elseif ($LASTEXITCODE) { Write-Warn "$AdminUser password not set" "run set-club-password-run-as-admin.cmd later, BEFORE players" }
    } elseif ($Golden) {
        Write-Info "$AdminUser keeps its password (set before the install: the preflight checked it)"
    } else {
        Write-Warn "$AdminUser password not changed here" 'set-club-password-run-as-admin.cmd, BEFORE players'
    }
    Write-Info 'The admin PIN opens the kiosk admin panel (Ctrl+Alt+Shift+A): exit to maintenance, reboot. Without it the panel answers "not configured".'
    $pinScript = Join-Path $KitDir 'set-shell-pin.ps1'
    if ((Test-Path -LiteralPath $pinScript) -and (Read-YesNo 'Set the admin PIN now (6-12 digits)?' $true)) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pinScript
        if ($LASTEXITCODE) { Write-Warn 'admin PIN not set' 'set-shell-pin-run-as-admin.cmd later' }
    } else {
        Write-Warn 'admin PIN not set here' 'set-shell-pin-run-as-admin.cmd'
    }

    # --- 8. Касса ----------------------------------------------------------------------------------------------------
    Write-Title 'Step 8: in the kassa (owner)'
    if ($Golden) {
        # Мастер — тот же физический ПК, что и место (HWID без iSCSI-диска): его запись и есть это место. Номер по
        # IP .1NN (план адресов клуба) — подсказка; сверить с панелью club-server (место по IP/MAC).
        $seatHint = 'the number of this seat (club-server panel: seat by IP/MAC)'
        foreach ($ip in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { "$($_.IPAddress)" })) {
            if ($ip -match '^192\.168\.1\.1(\d{2})$' -and [int]$Matches[1] -ge 1) { $seatHint = "{0} (IP {1}; check in the club-server panel)" -f [int]$Matches[1], $ip }
        }
        Write-Host "  1. Open the kassa -> 'Zal i ustroystva' (Hall and devices): a new pending PC named USER (number max+1), maintenance ON."
        Write-Host '     It is the master = THIS physical Primer seat (same hardware, same HWID as when it boots as a seat).'
        Write-Host "  2. Set its number to $seatHint; zone Primer (spelled as in the catalog game zones)."
        Write-Host "  3. Turn 'Bez postoyannogo diska' (no persistent disk) ON: C: resets at every boot (owner only)."
        Write-Host "  4. Switch 'Obsluzhivanie' (maintenance) OFF: that approves it. NEVER delete this row: it is that seat."
        Write-Host "  5. 'Politika PK' (PC policy): webFilter.dnsServers stays [] and blockResolvedIps false; usb.allowStorage as decided."
        Write-Host '  6. Then this script turns the kiosk on in the golden (step 9). No reboot: next seal-golden, then Publish.'
    } else {
    $number = if ($env:COMPUTERNAME -match '^PC-(\d{2})$') { [int]$Matches[1] } else { '?' }
    Write-Host "  1. Open the kassa -> 'Zal i ustroystva' (Hall and devices): the new PC $env:COMPUTERNAME is there, maintenance ON."
    Write-Host "  2. Check its number is $number (taken from the Windows name). Do not add PCs by hand."
    Write-Host '  3. Choose its zone (spelled exactly as in the catalog game zones).'
    Write-Host "  4. Switch 'Obsluzhivanie' (maintenance) OFF: that approves the PC."
    Write-Host "  5. 'Politika PK' (PC policy): webFilter.dnsServers stays [] and blockResolvedIps false; usb.allowStorage as decided."
    Write-Host '  6. Then this script turns the kiosk on (step 9) and offers the reboot; check the games as a player (docs/shell-install.md).'
    }
    $approved = $logState.Registration -eq 'registered'
    if (-not $approved -and (Read-YesNo 'Wait here until the PC is approved in the kassa (up to 10 minutes)?' $true)) {
        $approved = Wait-Until { (Get-AgentLogState).Registration -eq 'registered' } 600
        Show-Registration (Get-AgentLogState)
        if (-not $approved) { Write-Info 'not approved yet: the agent keeps retrying by itself after the approval' }
        else {
            # Политика кассы (DNS, USB) приходит только после одобрения — проверить DNS ещё раз.
            Write-Info 'waiting 60 s for the kassa policy, then checking DNS again'
            Start-Sleep -Seconds 60
            $dnsLate = Get-DnsSnapshot
            $lateChanged = @($dnsBefore.Keys | Where-Object { $dnsLate[$_] -ne $dnsBefore[$_] })
            if ($lateChanged.Count -eq 0) { Write-Pass 'DNS servers unchanged after the kassa policy' }
            else { foreach ($name in $lateChanged) { Write-Fail "DNS of $name changed after the policy: '$($dnsBefore[$name])' -> '$($dnsLate[$name])'" 'kassa policy: webFilter.dnsServers must be []' } }
        }
    }

    # --- 9. Киоск ----------------------------------------------------------------------------------------------------
    # kiosk-on.ps1 отдельным процессом, без перезагрузки: о ней спрашиваем в самом конце, когда журнал закрыт.
    Write-Title 'Step 9: kiosk on'
    if ($rebootBeforeKiosk) {
        # Эталон, установщик ответил 3010: до киоска — одна перезагрузка (автовход ещё не в player).
        Write-Warn 'kiosk not on yet: the installer wants a reboot first (exit code 3010)' "reboot the master ONCE now (before the kiosk), sign in as $AdminUser, run the get-shell-kit command again: it offers kiosk-on"
    } elseif ($approved) {
        $kioskOnScript = Join-Path $KitDir 'kiosk-on.ps1'
        if (-not (Test-Path -LiteralPath $kioskOnScript)) {
            Write-Fail "$kioskOnScript is missing" "get the kit again, then $kioskOnCmd"
        } else {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $kioskOnScript -KitDir $KitDir -KioskUser $KioskUser -AdminUser $AdminUser -NoReboot @modeArgs
            $kioskOnCode = $LASTEXITCODE
            if ($kioskOnCode -eq 0 -and $Golden) {
                Write-Pass "kiosk on in the golden: auto-logon to '$KioskUser' with the ClubShell shell; the seats start it at their next boot"
                $goldenKioskOn = $true
            } elseif ($kioskOnCode -eq 0) {
                Write-Pass "kiosk on: auto-logon to '$KioskUser' with the ClubShell shell from the next boot"
                $rebootIntoKiosk = $true
            } else {
                Write-Fail "kiosk not turned on (kiosk-on exit code $kioskOnCode, see above)" $kioskOnCmd
            }
        }
    } elseif ($Golden) {
        Write-Warn 'kiosk not on yet: the master is not approved in the kassa' "after the approval: $kioskOnCmd"
        Write-Info 'Do not publish the golden without the kiosk: the seats would boot to the admin auto-logon (or the sign-in screen).'
    } else {
        Write-Warn 'kiosk not on yet: the PC is not approved in the kassa' "after the approval: $kioskOnCmd"
        Write-Info "Until then auto-logon still goes to $AdminUser with its old password: after a new $AdminUser password a reboot stops"
        Write-Info "at the sign-in screen (sign in as $AdminUser with the new password, then run $kioskOnCmd)."
    }
} catch {
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
    $Count.Fail++
} finally {
    # Установка не дошла до конца — заготовленный agent.json с ключом не остаётся на диске.
    if ($wroteAgentJson -and -not $installed -and -not (Get-Service -Name ClubShellAgent -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $AgentJson -Force -ErrorAction SilentlyContinue
        if ($createdDataDir -and @(Get-ChildItem -LiteralPath $DataDir -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $DataDir -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $AgentJson) { Write-Host "Delete $AgentJson by hand: it holds the PC key." -ForegroundColor Red }
        else { Write-Host 'The prepared agent.json (with the PC key) was removed.' -ForegroundColor Yellow }
    }
    Write-Host ''
    $color = if ($Count.Fail -gt 0) { 'Red' } elseif ($Count.Warn -gt 0) { 'Yellow' } else { 'Green' }
    Write-Host ("Install result for {0}: {1} FAIL, {2} WARN. Log: {3}" -f $env:COMPUTERNAME, $Count.Fail, $Count.Warn, $LogDir) -ForegroundColor $color
    if ($installed -and $Golden) { Write-Host 'Rollback before the publish: shell-uninstall-run-as-admin.cmd in this master session, or Claude rolls the golden zvol back to the pre-shell snapshot.' }
    elseif ($installed) { Write-Host 'Rollback: shell-uninstall-run-as-admin.cmd (from C:\ClubShellKit; USB sticks no longer work on this PC).' }
    # Эталон: перезагрузки нет — что дальше. Журнал закрывается после этих строк, набор (и журнал) удалит seal-golden.
    # Только без FAIL: seal-golden большую часть проверок шага 6 не повторяет, и FAIL ушёл бы на все места.
    if ($goldenKioskOn -and $Count.Fail -eq 0) {
        Write-Host ''
        Write-Host 'GOLDEN: ClubShell and the kiosk are in the master. Do NOT reboot it (the next boot logs on to the player kiosk).' -ForegroundColor Yellow
        Write-Host '  1. In this admin window (current folder anything but C:\ClubShellKit):' -ForegroundColor Yellow
        Write-Host "       powershell -NoProfile -ExecutionPolicy Bypass -File $KitDir\seal-golden.ps1" -ForegroundColor Yellow
        Write-Host "     It stops and cleans ClubShell and the helper, checks the golden, deletes $KitDir and shuts the PC down." -ForegroundColor Yellow
        Write-Host '     Any WARN: fix it and run it again, no reboot in between.' -ForegroundColor Yellow
        Write-Host "  2. Panel 'Diskless' -> Publish (the PC is off), then one seat first (docs/shell-install.md, 'Diskless')." -ForegroundColor Yellow
    } elseif ($goldenKioskOn) {
        Write-Host ''
        Write-Host 'GOLDEN: FAIL lines above - do NOT run seal-golden or publish; fix them or roll back first.' -ForegroundColor Red
        Write-Host '  Do NOT reboot the master either (the next boot logs on to the player kiosk); take a photo of this window.' -ForegroundColor Red
    }
    try { Stop-Transcript | Out-Null } catch { }
}
# Эталон, установщик ответил 3010: одна перезагрузка до киоска. После журнала: Restart-Computer закрывает окно.
if ($rebootBeforeKiosk -and $installed -and (Read-YesNo 'Reboot the master now (once, before the kiosk)?' $true)) { Restart-Computer -Force }
# Киоск включён — перезагрузка в него. После журнала: Restart-Computer закрывает окно, итог уже записан. Есть FAIL — по
# умолчанию нет: после перезагрузки в киоск исправлять их можно только через Shift -> Club.
if ($rebootIntoKiosk) {
    $clean = $Count.Fail -eq 0
    if (-not $clean) { Write-Host 'There are FAIL lines above: take a photo of this window; reboot only once they are sorted out.' -ForegroundColor Red }
    if (Read-YesNo 'Reboot now into the kiosk?' $clean) { Restart-Computer -Force }
}
# 3 — эталону до киоска нужна перезагрузка (3010): get-shell-kit отличает это от «ещё не одобрен» (оба без FAIL).
if ($PSCommandPath) { if ($Count.Fail -gt 0) { exit 2 } elseif ($rebootBeforeKiosk) { exit 3 } else { exit 0 } }
