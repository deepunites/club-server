# Помощник бездиска (ClubDisklessHelper)

Служба Windows на каждом ПК клуба. Не зависит от шелла (ClubShell, Senet, iCafeCloud…): отвечает только за том
библиотеки игр. Сервер ничего не делает на ПК — помощник сам спрашивает, какой том нужен, и сообщает факты.

```
src/Club.Helper.Core   логика (собирается и тестируется на Linux)
src/Club.Helper        служба Windows: iSCSI и диски через командлеты PowerShell, процессы, DPAPI
tests/Club.Helper.Tests  тесты логики и сквозной тест против настоящего сервера клуба
```

## Что делает

Каждые 30 с: приводит том к назначенному сервером → отправляет `PUT /diskless/v1/machines/{id}/status` → в ответе
узнаёт актуальный том (новая версия или откат применяется сразу).

- **Монтирование fail-closed:** `Set-StorageSetting -NewDiskPolicy OfflineShared` → вход в таргет (без persistent)
  → `Set-Disk -IsReadOnly $true` и проверка, что атрибут встал → `Set-Disk -IsOffline $false` → буква раздела.
  Read-only не подтвердился → таргет отключается; том на запись не монтируется никогда.
- **Смена версии** не выдёргивает диск из-под игры: пока с тома запущен процесс (путь exe на букве тома, читается
  через `QueryFullProcessImageName` — работает и для процессов под античитом), состояние `switchPending`.
- **Сервер недоступен** — действует последнее назначение, сохранённое на ПК (`assignment.json`): после перезагрузки
  без сервера игры всё равно подключатся. Смонтированный том при потере связи не отключается.
- Чужие iSCSI-подключения ПК не трогаются: помощник управляет только таргетами `…:games-<версия>`.
- **Сеть** (`docs/network.md`): при регистрации первым идёт MAC основной карты (поднята, есть IPv4-шлюз) — по нему
  сервер резервирует IP места в Kea. В каждом отчёте — `dhcpServers`: кто выдал аренды поднятым Ethernet-картам
  (`DhcpServerAddresses`); чужой адрес — красная плашка в панели.
- **Перезаливка** (`docs/imaging.md`): в отчёте — системный диск (серийный номер, модель, размер, шина), чтобы
  WinPE стёр именно его, и версия образа из `image.json`, который пишет WinPE при заливке.

Состояние — `%ProgramData%\ClubDiskless` (доступ только SYSTEM и администраторам): `helper.json` (настройки),
`credentials.bin` (токены под DPAPI машины), `assignment.json` (последнее назначение).

## Установка на эталонный образ

Сборка (на любой машине с .NET 10 SDK):

```bash
dotnet publish src/Club.Helper -c Release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/helper
```

На ПК (PowerShell от администратора):

```powershell
New-Item -ItemType Directory -Force 'C:\Program Files\ClubDiskless' | Out-Null
Copy-Item .\ClubDisklessHelper.exe 'C:\Program Files\ClubDiskless\'
New-Item -ItemType Directory -Force "$env:ProgramData\ClubDiskless" | Out-Null
@'
{ "Helper": { "ServerUrl": "https://club-server", "ClubKey": "<ключ клуба>", "CaCertificatePath": "C:\\ProgramData\\ClubDiskless\\club-ca.pem" } }
'@ | Set-Content "$env:ProgramData\ClubDiskless\helper.json" -Encoding UTF8
New-Service -Name ClubDisklessHelper -BinaryPathName '"C:\Program Files\ClubDiskless\ClubDisklessHelper.exe"' -StartupType Automatic -DisplayName 'Club diskless helper'
Start-Service ClubDisklessHelper
```

Служба пишет в журнал «Приложение» с источником `ClubDisklessHelper`.

Установка до `sysprep /generalize`: токены не создаются, пока служба не зарегистрирует машину, а HWID читается
на каждом ПК свой — образ одинаков для всех. **До sysprep службу не запускать** (или удалить
`credentials.bin` перед захватом образа), иначе все ПК получат одну и ту же регистрацию.

## Проверка на стенде (обязательна: Windows-часть не запускалась)

Логика покрыта тестами на поддельной Windows; реальные командлеты и драйвер — гипотеза, пока не проверено на ПК.
Порядок для одного ПК стенда (Windows 11 Pro, TrueNAS 25.10 с опубликованной версией библиотеки):

1. Установить службу, дождаться строки в журнале `Library <версия> mounted read-only at G:`.
2. `Get-Disk | ? BusType -eq iSCSI | fl Number,IsReadOnly,IsOffline` → `IsReadOnly : True`, `IsOffline : False`.
3. Попробовать записать: `New-Item G:\probe.txt` → должно быть отказано (носитель защищён от записи).
4. `fsutil dirty query G:` → `NOT Dirty` после суток работы и нескольких перезагрузок.
5. `Get-StorageSetting | fl NewDiskPolicy` → `OfflineShared`.
6. Запустить игру с G:, опубликовать новую версию в панели → в панели у машины `switchPending`, игра не падает;
   закрыть игру → в течение 30 с `mounted` на новой версии, буква та же.
7. Выключить сервер клуба и перезагрузить ПК → G: подключается сам (последнее назначение), загрузка не зависает.
8. Выключить TrueNAS и перезагрузить ПК → рабочий стол без задержки, G: нет, в журнале ошибки подключения.
9. Античит (Q3): FACEIT и Vanguard с игрой на G: — отдельный протокол.
10. На ПК с двумя картами (встроенная + дискретная) в панели «Сеть» у места MAC той карты, что в сети клуба;
    `ipconfig /all` → «DHCP-сервер» совпадает с адресом из панели; включить DHCP на тестовом роутере в той же сети
    → плашка «Чужой DHCP» появляется у ПК, получивших от него аренду (`ipconfig /renew`).

Что проверить отдельно, если что-то не так: даёт ли `Set-Partition -NewDriveLetter` назначить букву на read-only
диске; видит ли `Get-Disk -iSCSISession` диск сразу после `Connect-IscsiTarget` (помощник ждёт до 30 с);
как ведёт себя `Set-StorageSetting` на Windows 11 Home (клубы ставят Pro).
