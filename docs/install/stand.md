# Стенд: установка по этапам

Цель первого дня — пройти путь до рабочей библиотеки игр: сервер клуба → TrueNAS → помощник на ПК → игра на
мастер-томе → опубликованная версия, которую ПК видит диском `G:` только для чтения. Потом DHCP (Kea). Образы Windows
и перезаливка — отдельным днём.

Каждый этап заканчивается проверкой «этап пройден, если…». Не идите дальше, пока проверка не прошла, — пришлите
мне то, что просит раздел «Если не получилось» этого этапа.

Все файлы — в папке `ClubStand` (в Документах на машине разработки): сборка сервера, помощник, скрипты, эта
инструкция. Перенесите её на флешку.

---

## Этап 0. Подготовка (до выезда)

**Сеть стенда — отдельная.** Свой коммутатор и роутер, не рабочая сеть клуба: на этапе 7 DHCP переключается на
наш сервер, в живой сети это отключит игроков.

**Железо:**

| Что | Минимум для стенда | Заметки |
|---|---|---|
| Сервер клуба (Ubuntu) | 4 ядра, 8 ГБ ОЗУ, SSD 120 ГБ, сеть 1 Гбит | здесь PostgreSQL, наш сервер, позже Kea и TFTP |
| TrueNAS | 16 ГБ ОЗУ, отдельный SSD под систему, диски под пул | RAIDZ1 — от 3 дисков; для стенда хватит 1–2 (без отказоустойчивости) |
| ПК клуба | Windows 11 Pro, 1–2 шт. | один будет суперклиентом (правка игр) |
| Роутер | с интернетом и DHCP, который можно выключить | шлюз стенда |
| Ноутбук администратора | браузер, SSH | с флешкой `ClubStand` |

Если сервер и TrueNAS — одна машина, напишите мне до начала: тогда TrueNAS ставится на железо, а Ubuntu — в
виртуальную машину внутри TrueNAS, этап 1 будет другим.

**Адреса стенда** (впишите свои, если сеть другая; дальше в тексте — эти):

| Кто | Адрес |
|---|---|
| Роутер (шлюз, интернет) | `192.168.77.1` |
| Сервер клуба | `192.168.77.2` |
| TrueNAS | `192.168.77.3` |
| ПК клуба (после этапа 7) | `192.168.77.101` и дальше — по номеру места |
| Пул для новых ПК и гостей | `192.168.77.200`–`192.168.77.250` |

До этапа 7 адреса раздаёт роутер: настройте его пул так, чтобы `.2` и `.3` в него не входили (например, пул роутера
`.100`–`.199`).

**Скачать заранее:**

- Ubuntu Server 26.04 LTS — https://ubuntu.com/download/server (ISO, amd64).
- TrueNAS Community Edition 25.10 (последний 25.10.x) — https://www.truenas.com/download-truenas-community-edition/
  Версия важна: сервер проверен на API 25.10; на 26.x подключение будет отклонено (см. этап 4).
- Программа записи ISO на флешку: balenaEtcher или Rufus.
- Две флешки по 8 ГБ+ (Ubuntu и TrueNAS).

---

## Этап 1. Ubuntu Server на сервер клуба (~40 мин)

1. Записать ISO Ubuntu на флешку, загрузиться с неё. В BIOS — режим UEFI (Secure Boot можно не выключать).
2. Установщик:
   - Язык — English (сообщения об ошибках проще искать), раскладка — своя.
   - Тип — **Ubuntu Server** (не minimized).
   - **Сеть**: выбрать сетевую карту → Edit IPv4 → Manual:
     - Subnet `192.168.77.0/24`, Address `192.168.77.2`, Gateway `192.168.77.1`,
     - Name servers `192.168.77.1, 1.1.1.1`.
   - Proxy — пусто; mirror — по умолчанию.
   - Диск — «Use an entire disk» на системный SSD, LVM по умолчанию.
   - Profile: name — ваше имя, server name `club-server`, username `admin` (не `clubsrv` — это имя займёт служба).
   - Ubuntu Pro — Skip. **Install OpenSSH server — да.** Snaps — ничего.
3. После перезагрузки войти и обновить:

```bash
sudo apt update && sudo apt full-upgrade -y
sudo timedatectl set-timezone Asia/Tashkent
sudo reboot
```

4. С ноутбука: `ssh admin@192.168.77.2` — дальше всё по SSH.

**Этап пройден, если:**

```bash
ip -br a                 # у карты адрес 192.168.77.2/24
ping -c3 192.168.77.1    # роутер отвечает
ping -c3 ubuntu.com      # интернет есть
timedatectl              # System clock synchronized: yes, Time zone: Asia/Tashkent
lsb_release -d           # Ubuntu 26.04 LTS
```

**Если не получилось:** адрес не тот — поправить файл в `/etc/netplan/` (пример ниже) и `sudo netplan apply`.
Имя карты — из `ip -br link` (например, `enp3s0`).

```yaml
network:
  version: 2
  ethernets:
    enp3s0:
      addresses: [192.168.77.2/24]
      routes: [{ to: default, via: 192.168.77.1 }]
      nameservers: { addresses: [192.168.77.1, 1.1.1.1] }
```

---

## Этап 2. Сервер клуба (~30 мин)

Скопировать на сервер файлы из `ClubStand` (с ноутбука, из папки `ClubStand`):

```bash
scp club-server-*-linux-x64.tar.gz deploy/club-server.service deploy/club-server.env.example scripts/make-club-ca.sh admin@192.168.77.2:~
```

(Или флешкой: `lsblk`, `sudo mount /dev/sdX1 /mnt`, `cp /mnt/ClubStand/... ~`.)

На сервере:

```bash
# База и служебный пользователь
sudo apt install -y postgresql
sudo useradd --system --home /var/lib/club-server --shell /usr/sbin/nologin clubsrv
sudo -u postgres createuser clubsrv
sudo -u postgres createdb -O clubsrv club

# Программа (самодостаточная сборка: .NET ставить не нужно)
sudo mkdir -p /opt/club-server /etc/club-server
sudo tar -xzf ~/club-server-*-linux-x64.tar.gz -C /opt/club-server

# Внутренний CA клуба и сертификаты (сервер .2, TrueNAS .3)
sudo sh ~/make-club-ca.sh 192.168.77.2 192.168.77.3

# Настройки
sudo cp ~/club-server.env.example /etc/club-server/club-server.env
openssl rand -hex 24     # → Panel__AdminToken (токен входа в панель)
openssl rand -hex 16     # → Auth__ClubApiKey (ключ для помощников)
sudo nano /etc/club-server/club-server.env     # вписать оба значения вместо ЗАМЕНИТЬ
sudo chown root:clubsrv /etc/club-server/club-server.env
sudo chmod 640 /etc/club-server/club-server.env

# Служба
sudo cp ~/club-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now club-server
```

Токен панели и ключ клуба запишите — понадобятся на этапах 2 и 5.

**Этап пройден, если:**

```bash
systemctl status club-server --no-pager          # active (running)
curl -s http://127.0.0.1:5080/health             # {"status":"ok"}
curl -s --cacert /etc/club-server/tls/club-ca.crt https://192.168.77.2:5443/health   # {"status":"ok"}
```

и с ноутбука открывается `https://192.168.77.2:5443/panel/` (браузер предупредит о неизвестном сертификате —
«Всё равно перейти», или установите `club-ca.crt` на ноутбук как доверенный корневой), вход — токен панели,
видна вкладка «Рабочие станции».

**Если не получилось:** `sudo journalctl -u club-server -n 100 --no-pager` — пришлите вывод.

---

## Этап 3. TrueNAS (~1 ч)

1. Записать ISO TrueNAS на флешку, установить **на отдельный системный SSD** (не на диски будущего пула). Пароль
   администратора (`truenas_admin`) — запишите.
2. В консоли после загрузки: «Configure network interfaces» → статический адрес `192.168.77.3/24`, шлюз
   `192.168.77.1`, DNS `192.168.77.1`. Веб-интерфейс: `http://192.168.77.3`.
3. **Пул**: Storage → Create Pool → имя `tank`, Data VDEV — RAIDZ1 на 3+ дисках (для стенда можно Stripe на одном).
4. **Датасеты**: Datasets → `tank` → Add Dataset → имя `club` (Generic). Затем внутри `club` → Add Dataset →
   `published` (Generic).
5. **Мастер-том**: Datasets → `tank/club` → Add Zvol → имя `lib`, размер — для стенда 300–500 GiB (не больше
   ~80 % пула), **Sparse** — да, Advanced → Block size **64K**.
6. **iSCSI**: Shares → iSCSI (без мастера настройки):
   - Portals → Add → IP `0.0.0.0`, порт `3260`. Запомните его **ID** (обычно 1).
   - Initiators Groups → Add → «Allow all initiators». Запомните **ID** (обычно 1). Опубликованные тома — только для
     чтения, поэтому группа открыта всем. Если позже сузите её списком IQN ПК — добавьте в список и имя, от которого
     сервер клуба проверяет публикацию: `iqn.2026-10.local.clubsrv:probe` (или своё из `Library__ProbeInitiatorIqn`).
     TrueNAS применяет список инициаторов и к discovery: без этого имени сервер не увидит таргет новой версии, и
     публикация остановится на шаге «проверка доступа» с ошибкой `does not include the server's probe name`.
   - Target Global Configuration — базовое имя не менять.
   - System → Services → iSCSI → Start и **Start Automatically**.
7. **Сертификат** (чтобы сервер клуба проверял, что говорит именно с этим TrueNAS). На сервере клуба показать
   сертификат и ключ:

   ```bash
   sudo cat /etc/club-server/tls/truenas.crt
   sudo cat /etc/club-server/tls/truenas.key
   ```

   В TrueNAS: Credentials → Certificates → Certificates → Import → имя `club-truenas`, вставить сертификат и ключ.
   System → General Settings → GUI → Settings → GUI SSL Certificate → `club-truenas` → Save (интерфейс
   перезапустится; дальше — `https://192.168.77.3`).
8. **Служебный пользователь и ключ API** (сервер клуба работает от него, с минимальными правами):
   - Credentials → Groups → Add → `clubsrv`.
   - Credentials → Users → Add → `clubsrv`, пароль отключить, shell `nologin`, основная группа `clubsrv`.
   - Credentials → Groups → Privileges → Add → имя `club-server`, Local Groups `clubsrv`, Web Shell — нет, Roles:
     `DATASET_WRITE`, `DATASET_DELETE`, `SNAPSHOT_WRITE`, `SNAPSHOT_DELETE`, `SHARING_ISCSI_WRITE`, `READONLY_ADMIN`.
   - API Keys (Credentials → API Keys или меню пользователя справа вверху) → Add → Name `club-server`,
     Username `clubsrv` → **скопировать ключ сразу** (показывается один раз).
   Точные названия пунктов меню в 25.10 могут немного отличаться — ищите «Privileges» и «API Keys».

**Этап пройден, если** на сервере клуба:

```bash
curl -s --cacert /etc/club-server/tls/club-ca.crt https://192.168.77.3/api/versions
```

выводит список вроде `["v25.10.0", …]` без ошибки сертификата. Пришлите мне этот список — сверю с белым списком
версий сервера.

**Если не получилось:** ошибка сертификата — не выбран `club-truenas` в GUI SSL Certificate или адрес TrueNAS не
`.3` (сертификат выписан на `.3`: `sudo sh ~/make-club-ca.sh` заново с нужным адресом, удалив `truenas.crt`).

---

## Этап 4. Сервер клуба ↔ TrueNAS (~15 мин)

```bash
sudo nano /etc/club-server/club-server.env
#   TrueNas__ApiKey=<ключ из этапа 3>
#   Library__Enabled=true
#   Library__PortalId / Library__InitiatorGroupId — ID из этапа 3, если не 1
#   Library__PortalAddress — портал iSCSI для ПК (192.168.77.3:3260)
sudo systemctl restart club-server
sudo journalctl -u club-server -n 50 --no-pager | grep -i -E "truenas|library|error"
nc -zv 192.168.77.3 3260                          # «succeeded»: портал iSCSI доступен серверу клуба
```

Серверу клуба, как и ПК, нужен доступ к порталу iSCSI (TCP 3260): перед тем как сделать версию текущей, он сам
спрашивает портал (iSCSI SendTargets), виден ли её таргет. Если с сервера портал доступен по другому адресу, чем с
ПК, — `Library__DiscoveryAddress=host:port`; пусто — тот же `Library__PortalAddress`. Пустой `Library__PortalAddress`
или порт не из 1..65535 — сервер не запустится (причина в журнале). Если портал с сервера недоступен (нет связи,
отказ в соединении, discovery с CHAP), версии всё равно публикуются, но без проверки: сервер повторно применяет
конфигурацию iSCSI вслепую, а сверка (раз в 5 минут) показывает в панели «Сервер не может проверить iSCSI-портал».

**Этап пройден, если** в панели на вкладке «Библиотека игр» нет плашки «Хранилище выключено», а в журнале нет
ошибок подключения к TrueNAS. Через 5 минут (сверка) предупреждений о TrueNAS нет.

**Если не получилось:** пришлите журнал. Типичное:

- `api version not allowed` — версия TrueNAS не из белого списка (пришлите вывод `/api/versions`);
- `EXPIRED`/`not authorized` — ключ API неверный или у группы нет нужных ролей;
- ошибка сертификата — см. этап 3.

---

## Этап 5. Первый ПК: помощник (~20 мин на ПК)

На ПК (Windows 11 Pro, в сети стенда) — из `ClubStand` скопировать `ClubDisklessHelper.exe`, и с сервера клуба —
`/etc/club-server/tls/club-ca.crt` (сохранить как `club-ca.pem`). PowerShell **от администратора**:

```powershell
New-Item -ItemType Directory -Force 'C:\Program Files\ClubDiskless' | Out-Null
Copy-Item .\ClubDisklessHelper.exe 'C:\Program Files\ClubDiskless\'
New-Item -ItemType Directory -Force "$env:ProgramData\ClubDiskless" | Out-Null
Copy-Item .\club-ca.pem "$env:ProgramData\ClubDiskless\club-ca.pem"
@'
{ "Helper": { "ServerUrl": "https://192.168.77.2:5443", "ClubKey": "<Auth__ClubApiKey с сервера>", "CaCertificatePath": "C:\\ProgramData\\ClubDiskless\\club-ca.pem" } }
'@ | Set-Content "$env:ProgramData\ClubDiskless\helper.json" -Encoding UTF8
New-Service -Name ClubDisklessHelper -BinaryPathName '"C:\Program Files\ClubDiskless\ClubDisklessHelper.exe"' -StartupType Automatic -DisplayName 'Club diskless helper'
Start-Service ClubDisklessHelper
```

В панели → «Рабочие станции» → «Ждут одобрения» → **Одобрить**. Через ~30 с ПК «В сети», версия помощника 1.4.1.

**Этап пройден, если** ПК в панели «В сети», и в его строке есть значок Secure Boot (помощник прислал данные о ПК).

**Если не получилось:** журнал Windows → «Приложение», источник `ClubDisklessHelper` — пришлите ошибки. Частое:
неверный `ClubKey`, `ServerUrl` с `http` вместо `https`, не тот файл CA.

---

## Этап 6. Мастер-том и первая версия библиотеки (~40 мин)

1. Панель → «Библиотека игр» → карточка «Мастер-том» → выбрать этот ПК → **Открыть для правки**. Через 30–60 с
   состояние «открыт на запись».
2. **Первый раз** мастер-том пустой: на ПК помощник подключит новый диск, но букву поставить не сможет (в панели
   «ошибка на ПК» — это ожидаемо). На ПК: Win+X → «Управление дисками» → новый диск → инициализировать **GPT** →
   «Создать простой том» → NTFS, метка `GAMES`, букву **не назначать**. Через ~30 с помощник поставит `M:`.
3. Положить на `M:` игру (для стенда — любая папка с файлами или небольшая игра через её лаунчер, путь установки —
   `M:\...`).
4. Панель → **Закончить правку**. Помощник сбросит данные и отключит `M:`; через 30–60 с — «закрыт».
5. Панель → «Новая версия» → **Опубликовать** (метка — сегодняшняя дата). Шаги: снапшот → клон → таргет → extent →
   LUN → проверка доступа (сервер сам спрашивает портал iSCSI, виден ли таргет, и при необходимости заново применяет
   конфигурацию iSCSI; если портал с сервера недоступен — переключение без проверки, см. этап 4) → переключение; итог —
   «опубликована, текущая».
6. Через ~30 с на ПК появляется диск `G:` с игрой.

**Этап пройден, если:**

- на ПК `G:` открывается, а запись на него запрещена (создать файл на `G:` — отказ «диск защищён от записи»);
- в PowerShell: `Get-Disk | ? BusType -eq iSCSI | fl Number,IsReadOnly,IsOffline` → `IsReadOnly : True`;
- в панели «ПК на связи: на текущей 1», в строке версии — «Состав: N папок» с вашей игрой.

**Если не получилось:** публикация упала на шаге «проверка доступа» с `not visible to PCs` — TrueNAS не включила таргет
даже после повторного применения конфигурации. Если в тексте ошибки есть `admits only listed initiators` — сначала
проверить, что в списке группы есть `iqn.2026-10.local.clubsrv:probe` (или своё `Library__ProbeInitiatorIqn`), добавить
и «Повторить». Если имя в списке есть или группа открыта всем: System → Services → iSCSI → Stop → Start (рвёт сессии
всех ПК), затем в журнале операций «Повторить». С `does not include the server's probe name` — группа инициаторов сужена без имени проверки сервера
(этап 3, п. 6): добавить его в группу и «Повторить»; службу iSCSI перезапускать не нужно. Ошибка `drive letter M is in use` — буква `M:` на ПК уже занята (флешка, сетевой диск):
освободить её. `no data partition on the library disk` после разметки — том создан не NTFS или не «простой». Иначе —
снимок панели (карточка мастер-тома и «Выполняется»), журнал сервера
(`journalctl -u club-server -n 200`), на ПК — журнал «Приложение» и вывод `Get-IscsiSession; Get-Disk`.

---

## Этап 7. DHCP: Kea (~40 мин) — сеть стенда переключается на наш сервер

До этого шага ничего не меняйте на роутере. Подробности — `docs/network.md`.

```bash
sudo systemctl mask kea-dhcp4-server            # не дать Kea стартовать рядом с DHCP роутера
sudo apt install -y kea-dhcp4-server kea-admin
cd /tmp
sudo -u postgres createuser _kea
sudo -u postgres createdb -O _kea kea
# без -h/-u kea-admin идёт на localhost как keatest (умолчания пакета) — указать сокет и роль явно
sudo -u _kea kea-admin db-init pgsql -h /var/run/postgresql -u _kea -n kea     # «Schema version … 29.0»
sudo -u _kea psql -d kea <<'SQL'
GRANT CONNECT ON DATABASE kea TO clubsrv;
GRANT SELECT ON schema_version TO clubsrv;
GRANT SELECT, INSERT, UPDATE, DELETE ON hosts TO clubsrv;
GRANT USAGE ON SEQUENCE hosts_host_id_seq TO clubsrv;
SQL
sudo nano /etc/club-server/club-server.env      # Kea__Enabled=true
sudo systemctl restart club-server
ip -br link                                      # имя сетевой карты — для поля «Интерфейс»
```

Панель → «Сеть»: подсеть `192.168.77.0/24`, интерфейс — имя карты, адрес DHCP-сервера `192.168.77.2`, шлюз
`192.168.77.1`, DNS `192.168.77.1`, пул `.200`–`.250`, IP места №1 `.101` → **Сохранить**. Затем «Скачать
kea-dhcp4.conf» и скопировать на сервер:

```bash
sudo install -m 640 -o root -g _kea ~/kea-dhcp4.conf /etc/kea/kea-dhcp4.conf
sudo kea-dhcp4 -t /etc/kea/kea-dhcp4.conf        # должно пройти без ошибок
# профиль AppArmor kea-dhcp4 в Ubuntu пускает только к MySQL: разрешить сокет PostgreSQL
echo '/run/postgresql/.s.PGSQL.* rw,' | sudo tee -a /etc/apparmor.d/local/usr.sbin.kea-dhcp4
sudo apparmor_parser -r /etc/apparmor.d/usr.sbin.kea-dhcp4
sudo systemctl unmask kea-dhcp4-server
sudo systemctl enable --now kea-dhcp4-server
sudo journalctl -u kea-dhcp4-server -n 20 --no-pager | grep -E "ERROR|DHCP4_STARTED"   # только DHCP4_STARTED
```

`kea-dhcp4 -t` к базе не подключается, поэтому ошибку AppArmor не ловит: без правила Kea падает при старте с
`Unable to open database … .s.PGSQL.5432 failed: Permission denied`, в `journalctl -k` — `apparmor="DENIED"
profile="kea-dhcp4"`.

**Только теперь** выключить DHCP на роутере. На ПК: `ipconfig /release` и `ipconfig /renew`.

**Этап пройден, если** ПК получил адрес `192.168.77.101` (место №1), в панели «Сеть» — резервация ПК и нет красной
плашки «Чужой DHCP».

**Если не получилось — сразу вернуть сеть:** включить DHCP на роутере и `sudo systemctl disable --now kea-dhcp4-server`.
Так же — после пробного переключения, если Kea пока не должен оставаться DHCP сети (конфиг и база остаются).
Пришлите `sudo journalctl -u kea-dhcp4-server -n 100 --no-pager`.

---

## Дальше (другим днём)

- Второй ПК, публикация второй версии, переключение без выхода из игры, откат — `docs/helper.md`, раздел «Проверка
  на стенде».
- Образы Windows и перезаливка по сети, Secure Boot — `docs/imaging.md`.

## Полезное

| Что | Команда |
|---|---|
| Журнал сервера | `sudo journalctl -u club-server -f` |
| Перезапуск сервера | `sudo systemctl restart club-server` |
| Остановить всё наше | `sudo systemctl stop club-server kea-dhcp4-server` |
| Обновить сервер | `sudo systemctl stop club-server; sudo tar -xzf club-server-…tar.gz -C /opt/club-server; sudo systemctl start club-server` |
| Настройки | `/etc/club-server/club-server.env` |
