# Стенд на одной машине: TrueNAS + сервер клуба в виртуальной машине

Инструкция записана по реальной установке 1–2 октября 2026 года. Все шаги и ловушки ниже проверены на этом
железе. Вариант для двух машин — [stand.md](stand.md).

**Схема.** TrueNAS ставится прямо на машину, на отдельный SSD. Сервер клуба (Ubuntu) работает в виртуальной
машине внутри TrueNAS. Обе системы работают одновременно: TrueNAS отдаёт игры по iSCSI, сервер клуба ими управляет
через API TrueNAS.

| Что | Стенд |
|---|---|
| Машина | Intel Core i3-10105F, 16 ГБ ОЗУ, два SSD по 128 ГБ |
| `sda` | система TrueNAS (`boot-pool`) |
| `sdb` | пул `tank`: игры и диск виртуальной машины |
| Роутер | `192.168.1.1`: шлюз, интернет, DHCP для остальных устройств |
| TrueNAS | `192.168.1.50` (мост `br0`) |
| Сервер клуба (ВМ Ubuntu) | `192.168.1.51` |
| Свои постоянные адреса | `192.168.1.50`–`192.168.1.100`. Диапазон DHCP роутера не должен их пересекать |

---

## 0. Что скачать

| Что | Ссылка | SHA256 |
|---|---|---|
| TrueNAS 25.10.7 (ISO, ~2,3 ГБ) | https://download.truenas.com/TrueNAS-SCALE-Goldeye/25.10.7/TrueNAS-SCALE-25.10.7.iso | `54ce9441ce66966a392e28f63604ca3c2c083d0bec4db7bb5af2f74f7a007c8e` |
| Ubuntu Server 26.04.1 LTS (ISO, ~2,9 ГБ) — скачивается сразу в TrueNAS, шаг 6 | https://releases.ubuntu.com/26.04/ubuntu-26.04.1-live-server-amd64.iso | `cc8a95cde20f6ced61a322420de00f10cc3c90ced545daa46cb9c1a117f1d927` |
| Запись ISO на флешку | Rufus https://rufus.ie/ (режим **DD**) или balenaEtcher https://etcher.balena.io/ | — |
| VNC-клиент (экран ВМ) | Linux: Remmina (есть в Ubuntu). Windows: TigerVNC https://tigervnc.org/ или RealVNC Viewer https://www.realvnc.com/en/connect/download/viewer/ | — |
| Пакет стенда | папка `ClubStand`: сборка сервера `club-server-*-linux-x64.tar.gz`, `deploy/`, `scripts/`, помощник для ПК | `SHA256SUMS` в папке |

Проверка образа в Windows: `Get-FileHash .\файл.iso -Algorithm SHA256`, в Linux: `sha256sum файл.iso`.

TrueNAS нужна именно 25.10.x: сервер клуба разговаривает с её API версий v25.10.0–v25.10.5, а в 25.10.7 API
`v25.10.5`.

---

## 1. BIOS

- Включить виртуализацию: Intel VT-x или AMD SVM. Без неё ВМ не создать.
- Режим загрузки UEFI.
- Secure Boot выключить (так ставили на стенде).

## 2. Установка TrueNAS

1. Записать ISO TrueNAS на флешку, загрузиться с неё → **Install/Upgrade**.
2. Выбрать **только** SSD под систему (`sda`), весь диск уйдёт под TrueNAS.
3. Вариант **Administrative user (truenas_admin)**, пароль — простой, латиницей.
4. Перезагрузка, флешку вынуть. На экране появится меню консоли и временный адрес от роутера
   (у нас был `192.168.1.121`).

**Если не пускает в веб-интерфейс по паролю:** в меню консоли пункт `4) Change local administrator password`. Цифру
набирать на верхнем ряду клавиатуры: на цифровом блоке без NumLock меню пишет «invalid choice». Пароль при вводе не
отображается.

## 3. Сеть TrueNAS: постоянный адрес и мост

Мост `br0` нужен виртуальной машине: без него ВМ не достучится до самой TrueNAS. Делать по временному адресу
`http://192.168.1.121` (свой — с экрана консоли).

1. **System → Network → Global Configuration → Settings:** шлюз `192.168.1.1`, DNS — те, что сейчас пришли по
   DHCP (у нас `8.8.8.8`, `1.1.1.1`) → **Save**. Они становятся постоянными.
2. **Interfaces → `enp3s0` → Edit:** **Define Static IP Addresses** без адресов, снять **Autoconfigure IPv6** → Save.
3. **Interfaces → Add:** Type **Bridge**, имя `br0`, Static IP `192.168.1.50` / `24`, Bridge Members `enp3s0` →
   Save.
4. В плашке «Test network interface changes for 60 seconds» **поставить 300** и сразу нажать **Test Changes** →
   Confirm. Поле сбрасывается на 60 при перезагрузке страницы — менять прямо перед нажатием.
5. Открыть `http://192.168.1.50`, **войти заново** (это новый адрес — новая сессия), System → Network →
   **Save Changes**. Не успели — TrueNAS сама вернёт старые настройки, повторить шаги 2–5.

Проверка: `ping 192.168.1.50` отвечает, старый адрес нет.

TrueNAS разлогинивает примерно через 5 минут бездействия — это нормально, просто войти снова.

## 4. Хранилище

1. **Storage → Create Pool:** имя `tank`, Layout **Stripe**, диск `sdb` → Create. Предупреждение про Stripe
   нормально: один диск, без резервирования.
2. **Datasets → `tank` → Add Dataset:** `club` (Generic). Внутри `club` → Add Dataset: `published`.
3. **Datasets → `tank/club` → Add Zvol:** имя `lib`, размер `60 GiB`, **Sparse**, Block size **64 KiB**.
   Это мастер-том библиотеки игр; блок после создания не меняется.
4. **Datasets → `tank` → Add Dataset:** `iso`. Затем у `tank/iso`: Permissions → Edit → User `truenas_admin`,
   **Apply User** → Save. Иначе в Shell не записать файл: `sudo` в TrueNAS просит пароль.

## 5. iSCSI

1. **Shares → iSCSI → Portals → Add:** Description `club-lib`, IP `0.0.0.0` (порт 3260) → Save. Запомнить
   **Portal Group ID** (у нас 1).
2. **Initiators → Add:** **Allow All Initiators** → Save. **Group ID** (у нас 1). Опубликованные тома только для
   чтения; мастер-том сервер закроет отдельной группой и CHAP сам. Если эту группу когда-нибудь сузят списком IQN ПК,
   в нём должно быть и имя проверки сервера `iqn.2026-10.local.clubsrv:probe` (`Library__ProbeInitiatorIqn`): список
   инициаторов действует и на discovery, без имени публикация остановится на «проверке доступа».
3. **System → Services → iSCSI:** включить **Start Automatically** и запустить (Running).

Проверка с любого компьютера сети: порт `192.168.1.50:3260` открыт.

## 6. Образ Ubuntu — прямо в TrueNAS

**System → Shell:**

```bash
cd /mnt/tank/iso && curl -fL --progress-bar -o ubuntu-26.04.1-live-server-amd64.iso https://releases.ubuntu.com/26.04/ubuntu-26.04.1-live-server-amd64.iso
sha256sum ubuntu-26.04.1-live-server-amd64.iso
```

Сумма должна совпасть с таблицей шага 0. Скачивание ~3–5 минут.

## 7. Виртуальная машина

**Virtual Machines → Add:**

| Шаг мастера | Значение |
|---|---|
| Operating System | Linux; имя `server`; Boot **UEFI**; Start on Boot — да; Display (VNC) — да, **пароль VNC до 8 символов** (запомнить) |
| CPU and Memory | 1 CPU × **4 cores** × 1 thread; память **6 GiB** |
| Disks | Create new disk image, тип **VirtIO**, Zvol Location `tank`, **32 GiB** |
| Network | VirtIO, Attach NIC **`br0`** |
| Installation Media | `/mnt/tank/iso/ubuntu-26.04.1-live-server-amd64.iso` |
| GPU | по умолчанию |

Save → Start. Экран ВМ — по VNC на `192.168.1.50:5900` (в браузере TrueNAS его не открыть):

```bash
remmina -c vnc://192.168.1.50:5900
```

Если Remmina пишет «VNC connection timed out» сразу после старта ВМ — закрыть окно и подключиться ещё раз через
10–20 секунд.

## 8. Установка Ubuntu в ВМ — ловушки

| Экран | Значение | Почему |
|---|---|---|
| Language | English | |
| **Keyboard** | **English (US) / English (US)** | С русской раскладкой в ВМ печатается кириллица, логин и пароль не ввести |
| Type | Ubuntu Server (не minimized) | |
| **Network** | `ens3` → Edit IPv4 → **Manual**: подсеть `192.168.1.0/24`, адрес `192.168.1.51`, шлюз `192.168.1.1`, DNS `1.1.1.1`. **Create bond не трогать** (лишний `bond0` — удалить: Enter на нём → Delete) | |
| Proxy | пусто | |
| **Mirror** | **`http://archive.ubuntu.com/ubuntu`** вместо `http://uz.archive.ubuntu.com/ubuntu` | Узбекское зеркало было посреди синхронизации, установка падала: `File has unexpected size … Mirror sync in progress?` → `cmd-in-target: FAIL` |
| Storage | Use an entire disk (32 GiB), LVM по умолчанию, подтвердить стирание | |
| **Profile** | server name `clubserver`, username **`clubadmin`**, пароль **только строчными латинскими буквами** | `admin` занят системой. Пароль, набранный через VNC с Shift/цифрами, у нас записался искажённым — войти было невозможно, пришлось переустанавливать. Сменить на сложный — потом по SSH (`passwd`) |
| Ubuntu Pro | Skip | |
| SSH | **Install OpenSSH server** (пробел) | |
| Snaps | ничего | |

В конце **Reboot Now**. Если висит на «Rebooting…» больше минуты или ВМ снова загружает установщик:
TrueNAS → Virtual Machines → `server` → **Power Off** → **Devices** → у **CD-ROM** ⋮ → **Delete** → **Start**.
CD-ROM стоит в порядке загрузки первым (1000, диск — 1001), поэтому без удаления ВМ всегда грузит установщик.

Проверка: `ssh clubadmin@192.168.1.51` спрашивает пароль, на консоли ВМ — `clubserver login:`.

## 9. Доступ к серверу с компьютера администратора

На компьютере администратора (Linux, Windows 10+ с OpenSSH):

```bash
ssh-keygen -t ed25519 -f ~/.ssh/club_stand -N ""
ssh-copy-id -i ~/.ssh/club_stand.pub clubadmin@192.168.1.51
```

`ssh-copy-id` спросит пароль `clubadmin` — у SSH на ввод 2 минуты и 3 попытки. Если адрес уже был у другой
установки: `ssh-keygen -R 192.168.1.51`.

Только для стенда — `sudo` без пароля (пароль спросят один раз):

```bash
ssh -t -i ~/.ssh/club_stand clubadmin@192.168.1.51 "echo 'clubadmin ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/90-stand >/dev/null && sudo chmod 440 /etc/sudoers.d/90-stand && echo SUDO_OK"
```

Убрать потом: `sudo rm /etc/sudoers.d/90-stand`.

## 10. Сервер клуба

Дальше всё по SSH (`ssh -i ~/.ssh/club_stand clubadmin@192.168.1.51`).

**Система:**

```bash
sudo lvextend -r -l +100%FREE /dev/ubuntu-vg/ubuntu-lv      # установщик занял 15 из 32 ГБ
sudo timedatectl set-timezone Asia/Tashkent
sudo apt-get update && sudo DEBIAN_FRONTEND=noninteractive apt-get -y full-upgrade
```

**База:**

```bash
sudo apt-get install -y postgresql
sudo useradd --system --home /var/lib/club-server --shell /usr/sbin/nologin clubsrv
sudo -u postgres createuser clubsrv
sudo -u postgres createdb -O clubsrv club
```

**Программа и сертификаты** (с компьютера администратора, из папки `ClubStand`):

```bash
scp -i ~/.ssh/club_stand club-server-*-linux-x64.tar.gz deploy/club-server.service deploy/club-server.env.example scripts/make-club-ca.sh clubadmin@192.168.1.51:~
```

На сервере:

```bash
sudo mkdir -p /opt/club-server /etc/club-server
sudo tar -xzf ~/club-server-*-linux-x64.tar.gz -C /opt/club-server
sudo sh ~/make-club-ca.sh 192.168.1.51 192.168.1.50          # CA клуба + сертификат сервера
```

**Настройки** — токены генерируются на месте, адреса меняются на наши:

```bash
f=/etc/club-server/club-server.env
sudo cp ~/club-server.env.example $f
sudo sed -i -e "s|^Panel__AdminToken=.*|Panel__AdminToken=$(openssl rand -hex 24)|" \
  -e "s|^Auth__ClubApiKey=.*|Auth__ClubApiKey=$(openssl rand -hex 16)|" \
  -e "s|192\.168\.77\.3|192.168.1.50|g" -e "s|192\.168\.77\.2|192.168.1.51|g" $f
sudo chown root:clubsrv $f && sudo chmod 640 $f
```

**Служба:**

```bash
sudo cp ~/club-server.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now club-server
curl -s http://127.0.0.1:5080/health                          # {"status":"ok"}
sudo journalctl -u club-server -n 50 --no-pager               # 8 миграций, Now listening on :5080 и :5443
```

**Панель:** `https://192.168.1.51:5443/panel/` с любого устройства сети (предупреждение о сертификате — потому что
CA клуба браузеру не знаком; файл CA — `/etc/club-server/tls/club-ca.crt`, можно установить как доверенный).
Токен входа:

```bash
ssh -i ~/.ssh/club_stand clubadmin@192.168.1.51 sudo grep AdminToken /etc/club-server/club-server.env
```

## 11. Связь сервера с TrueNAS

> Пройдено на стенде 2 октября: сервер подключился к TrueNAS 25.10.7 через API v25.10.5.

**Пользователь и права в TrueNAS:**

1. **Credentials → Users → Add:** `clubsrv`, снять SMB Access, **Disable Password**, группа — новая `clubsrv`.
2. **Credentials → Groups → Privileges → Add:** имя `server`, Local Groups `clubsrv`, Roles: **Readonly Admin,
   Dataset Write, Dataset Delete, Snapshot Write, Snapshot Delete, Sharing iSCSI Write**. Других ролей не нужно, в том
   числе Service Write: повторное применение конфигурации iSCSI при публикации идёт через `iscsi.target.update`
   (Sharing iSCSI Write). Проверка публикации — запрос списка таргетов к порталу `Library__PortalAddress`
   (`192.168.1.50:3260`; другой адрес — `Library__DiscoveryAddress`) с сервера клуба от имени
   `Library__ProbeInitiatorIqn`: порт 3260 должен быть доступен серверу так же, как ПК. Недоступен — версии
   публикуются без проверки (reload-ы вслепую), сверка показывает «Сервер не может проверить iSCSI-портал».

**Сертификат веб-интерфейса TrueNAS** (чтобы сервер проверял, что говорит именно с ней). Проще всего — пара, которую
уже выпустил `make-club-ca.sh`: `/etc/club-server/tls/truenas.crt` и `truenas.key`. **Certificates → Import:** имя
`club-truenas-gui`, Certificate — содержимое `truenas.crt`, Private Key — содержимое `truenas.key` (скопировать
целиком, вместе со строками `-----BEGIN/END …-----`), затем шаг 4 ниже. Вариант через запрос (ключ не покидает
TrueNAS):

1. **Credentials → Certificates → Certificate Signing Requests → Add:** имя `club-truenas`, профиль
   **HTTPS RSA Certificate**, Common Name и SAN — `192.168.1.50`, email — любой с обычным доменом (домен `.local`
   TrueNAS отвергает).
2. У запроса ⋮ → Edit → **View/Download CSR** → скопировать текст в файл `truenas.csr` на сервере клуба и
   подписать:

   ```bash
   cd /etc/club-server/tls
   printf 'subjectAltName=IP:192.168.1.50\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nbasicConstraints=CA:FALSE\n' > /tmp/gui.ext
   sudo openssl x509 -req -in ~/truenas.csr -CA club-ca.crt -CAkey club-ca.key -CAserial club-ca.srl -days 825 -sha256 -extfile /tmp/gui.ext -out truenas-gui.crt
   cat truenas-gui.crt
   ```

3. **Certificates → Import:** имя `club-truenas-gui`, Certificate — подписанный `truenas-gui.crt`, **Private Key —
   из запроса** (у `club-truenas` ⋮ → Edit → **View/Download Key**). Без ключа TrueNAS импортирует сертификат, но
   выбрать его для интерфейса не даст: «Selected certificate does not have a private key».
4. **System → General Settings → GUI → Settings → GUI SSL Certificate:** `club-truenas-gui` → Save. Интерфейс
   перезапустится.

Проверка на сервере клуба:

```bash
curl -s --cacert /etc/club-server/tls/club-ca.crt https://192.168.1.50/api/versions
```

**API-ключ** для `clubsrv` (Credentials → Users → `clubsrv` → API Keys, или меню пользователя → API Keys → Add,
User `clubsrv`) — показывается один раз. Вписать в настройки сервера, не показывая на экране:

```bash
ssh -t -i ~/.ssh/club_stand clubadmin@192.168.1.51 'read -rsp "API key: " K; echo; echo "Dlina: ${#K}"; [ ${#K} -ge 20 ] && sudo sed -i "s|^TrueNas__ApiKey=.*|TrueNas__ApiKey=$K|" /etc/club-server/club-server.env && echo OK'
# вставка через «правая кнопка → Вставить»; длина ключа TrueNAS ~66. Если вставился символ CR:
ssh -i ~/.ssh/club_stand clubadmin@192.168.1.51 "sudo sed -i '/^TrueNas__ApiKey=/ s/\r//g' /etc/club-server/club-server.env"
```

Включить библиотеку и перезапустить:

```bash
sudo sed -i 's/^Library__Enabled=false/Library__Enabled=true/' /etc/club-server/club-server.env
sudo systemctl restart club-server
sudo journalctl -u club-server -n 50 --no-pager | grep -i -E "truenas|library|error"
```

## 12. ПК клуба и первая версия библиотеки (пройдено)

**Помощник на ПК** (Windows 11, PowerShell от администратора). Папка `ClubStand/pc` раздаётся с компьютера
администратора (`python3 -m http.server 8000` в этой папке; `clubkey.txt` с ключом клуба кладётся туда командой
`ssh … "sudo sed -n 's/^Auth__ClubApiKey=//p' /etc/club-server/club-server.env" > pc/clubkey.txt` и удаляется после
установки):

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force; iwr http://192.168.1.133:8000/install-helper.ps1 -UseBasicParsing -OutFile $env:TEMP\ih.ps1; & $env:TEMP\ih.ps1
```

Панель → «Рабочие станции» → «Ждут одобрения» → Одобрить.

**Помощник 1.4.0 — обход (в 1.4.1 исправлено):** без запущенной службы «Инициатор iSCSI Майкрософт» 1.4.0 не
сообщает IQN, и ПК не появляется в выборе для правки мастер-тома. Помощник 1.4.1 сам включает и запускает службу и
перечитывает IQN без перезапуска — обход не нужен. На 1.4.0 один раз на ПК суперклиента:

```powershell
Set-Service MSiSCSI -StartupType Automatic; Start-Service MSiSCSI; Restart-Service ClubDisklessHelper
```

**Мастер-том и публикация:** «Библиотека игр» → Мастер-том → ПК → «Открыть для правки» → на ПК в «Управлении
дисками» новый диск: GPT, простой том NTFS **без буквы** → помощник ставит `M:` → скопировать игру → «Закончить
правку» → «Опубликовать».

**«Target not found or hidden from login» — исправлено в сервере.** 2 октября TrueNAS 25.10.7 записала таргет новой
версии в конфиг, но не включила его (в SCST `enabled 0`, без LUN), хотя API ответил успехом. Теперь сервер создаёт
таргет раньше экстента, после LUN сам спрашивает портал, виден ли таргет (iSCSI SendTargets), при необходимости заново
применяет конфигурацию iSCSI и делает версию текущей, когда таргет виден (`docs/research/truenas-api.md` §8.4). Если
портал с сервера проверить нельзя (нет связи, discovery с CHAP), версия становится текущей без проверки — после
повторных применений конфигурации вслепую и с предупреждением сверки «Сервер не может проверить iSCSI-портал».
Ручной обход остаётся на случай, если не помогло и это (публикация падает на шаге «проверка доступа» с
`not visible to PCs`; в сверке — предупреждение «Таргет … не виден ПК»). Если в ошибке есть `admits only listed initiators`
(в сверке — «Таргет … не виден имени проверки сервера»), сначала добавить в группу имя проверки сервера
`iqn.2026-10.local.clubsrv:probe` и «Повторить». Иначе — System → Services → iSCSI → Stop → Start,
затем «Повторить» в журнале операций. Через ~30 с ПК подключит `G:`. Перезапуск службы рвёт сессии всех ПК —
сервер сам его не делает.

Проверка на ПК:

```powershell
Get-Disk | ? BusType -eq iSCSI | ft Number,IsReadOnly,IsOffline     # IsReadOnly True, IsOffline False
New-Item G:\probe.txt                                              # «Носитель защищён от записи»
```

## 13. DHCP: Kea на ВМ сервера (пройдено; с 2026-10-02 — DHCP всей сети стенда)

Команды — этап 7 в [stand.md](stand.md) (с `kea-admin -h /var/run/postgresql -u _kea` и правилом AppArmor). Сеть
стенда — домашняя: сначала DHCP роутера выключали на время проверки, затем оставили выключенным — адреса всей сети
раздаёт Kea (`systemctl is-enabled kea-dhcp4-server` → `enabled`). Пока TrueNAS (и с ней ВМ) выключена, новые
устройства адрес не получат; ВМ в TrueNAS должна стартовать сама (Virtualization → ВМ → Autostart).

Панель → «Сеть» с нашими адресами (всё — в выделенном нам диапазоне .50–.100):

| Поле | Значение |
|---|---|
| Подсеть | `192.168.1.0/24` |
| Интерфейс сервера | `ens3` (`ip -br link` на ВМ) |
| Адрес этого DHCP-сервера | `192.168.1.51` |
| Шлюз, DNS | `192.168.1.1` |
| Пул гостей | `192.168.1.80` – `192.168.1.100` |
| IP места №1 | `192.168.1.60` → мест 20 (.60–.79), место 19 → `.78` |

Порядок: перед запуском проверить с ВМ, что .52–.100 свободны (`ping` по диапазону и `ip neigh`), запустить Kea,
убедиться в `DHCP4_STARTED`, **потом** выключить DHCP на роутере, на ПК `ipconfig /release; ipconfig /renew`.

Итог: ПК получил `192.168.1.78`, помощник сообщил DHCP-сервер `192.168.1.51`, `G:` осталась подключена. Остальные
устройства сети при продлении аренды переходят в пул .80–.100 (21 адрес); устройства со статическим адресом (машина
администратора `.133`) не затрагиваются. Вернуть DHCP роутеру: включить его на роутере и
`sudo systemctl disable --now kea-dhcp4-server` на ВМ; конфиг Kea и база остаются.

## 14. Дальше

Второй ПК, публикация во время игры, откат — `docs/helper.md` («Проверка на стенде»); перезаливка Windows по PXE —
`docs/imaging.md` (нужен Kea как DHCP сети). Адреса: сервер `192.168.1.51`, TrueNAS `192.168.1.50`, `ServerUrl`
помощника `https://192.168.1.51:5443`.

## Неполадки, которые встретились

| Симптом | Причина | Что сделать |
|---|---|---|
| Меню консоли TrueNAS: «invalid choice» | Цифра с цифрового блока без NumLock | Цифры верхнего ряда |
| Сеть TrueNAS откатилась сама | Не успели подтвердить за 60 с | Поставить 300 с перед Test Changes, войти на новый адрес, Save Changes |
| `sudo: a password is required` в Shell TrueNAS | Shell работает под `truenas_admin` | Права на датасет через интерфейс (шаг 4.4) |
| В ВМ печатается кириллица | В установщике выбрана русская раскладка | Keyboard: English (US) |
| В окне ВМ не печатает на английской раскладке (Wayland, us+ru) | Особенность Remmina | Раскладка гостя — English (US); если окно зависло — переподключиться |
| Установка падает в конце, `Mirror sync in progress?` | Зеркало `uz.archive.ubuntu.com` синхронизируется | Mirror: `http://archive.ubuntu.com/ubuntu` |
| `The username "admin" is reserved` | Имя занято системой | `clubadmin` |
| «Login incorrect» с верным паролем | Пароль через VNC записался искажённым | Переустановка с паролем из строчных букв |
| Висит «Rebooting…» / снова установщик | CD-ROM первым в загрузке | Power Off → удалить CD-ROM → Start |
| `Connection closed by … port 22` при `ssh-copy-id` | 2 минуты на ввод пароля истекли | Запустить заново и сразу ввести |
| `REMOTE HOST IDENTIFICATION HAS CHANGED` | ВМ переустановлена, ключ хоста новый | `ssh-keygen -R 192.168.1.51` |
| ПК нет в выборе «Открыть для правки», в панели «Нет ПК, чей помощник сообщил имя iSCSI-инициатора» | Служба MSiSCSI на ПК остановлена; помощник 1.4.0 запоминает пустой IQN (в 1.4.1 исправлено) | Обновить помощник до 1.4.1; на 1.4.0 — `Set-Service MSiSCSI -StartupType Automatic; Start-Service MSiSCSI; Restart-Service ClubDisklessHelper`. На 1.4.1 — журнал Windows, источник `ClubDisklessHelper`: предупреждение «iSCSI initiator IQN is not available» с причиной |
| После публикации на ПК «The target name is not found or is marked as hidden from login» | TrueNAS 25.10.7 не применила LUN к новому таргету (SCST: target enabled=0), а API ответил успехом | Исправлено в сервере: порядок «таргет → экстент → LUN», проверка SendTargets и повторное применение конфигурации до переключения версии (шаг 12). Если публикация всё же упала на «проверке доступа» — System → Services → iSCSI → Stop → Start, затем «Повторить» |
| Публикация упала на «проверке доступа» с `does not include the server's probe name` | Группа инициаторов версий сужена списком IQN без имени проверки сервера: discovery подчиняется тому же списку | Добавить `iqn.2026-10.local.clubsrv:probe` (или своё `Library__ProbeInitiatorIqn`) в группу (шаг 5.2) или вернуть «Allow All Initiators», затем «Повторить». Перезапуск службы iSCSI не поможет |
| API-ключ записался с лишним символом | При вставке в скрытый ввод попал CR | `sed -i '/^TrueNas__ApiKey=/ s/\r//g'` (шаг 11) |
| Панель не открывается в браузере приложения по HTTPS | CA клуба не доверен | `http://192.168.1.51:5080/panel/` в сети стенда или установить `club-ca.crt` |
| `kea-admin db-init` → `password authentication failed for user "keatest"` | Без `-h`/`-u` kea-admin 3.0.3 идёт на `localhost` как `keatest` (умолчания пакета) | `sudo -u _kea kea-admin db-init pgsql -h /var/run/postgresql -u _kea -n kea` (из `cd /tmp`) |
| Kea не стартует: `Unable to open database … .s.PGSQL.5432 failed: Permission denied` | Профиль AppArmor `kea-dhcp4` в Ubuntu не пускает к сокету PostgreSQL (`journalctl -k`: `apparmor="DENIED"`); `kea-dhcp4 -t` этого не ловит | `/run/postgresql/.s.PGSQL.* rw,` в `/etc/apparmor.d/local/usr.sbin.kea-dhcp4`, `apparmor_parser -r /etc/apparmor.d/usr.sbin.kea-dhcp4`, перезапуск Kea |
| После публикации, хотя с G: запущены игра и Steam: на помощнике 1.4.1 ПК в панели выглядит выключенным (отчётов нет, пока том не освободится); на 1.4.2 — долго «ждёт освобождения тома» (`switchPending`), причина `volume in use (open files on G:): …` (или `old version not disconnected: …`) | Помощник (1.4.1, возможно и 1.4.2) в службе не нашёл процессы с G: (стенд 2026-10-02, причина не установлена) и пробовал отключить том — Windows отказала | Закрыть игру и выйти из Steam — через ~30 с новая версия. Помощник 1.4.2 ищет процессы и по пути NT; если на нём причина та же — в журнале «Приложение» (источник `ClubDisklessHelper`) предупреждение `… is not released: … Disk of the target: …; process scan of G: …` сохранить для разбора (`docs/helper.md`, «Диагностика отказа») |
| Консоль TrueNAS (или `dmesg`): `dev_vdisk: … FLUSH bio failed: -5` при подключении ПК; в журнале System Windows возможно Event ID 7 (disk, «has a bad block») | Известный шум read-only томов: SCST объявляет кэш записи, Windows шлёт SYNCHRONIZE CACHE, read-only zvol отвечает на сброс ошибкой (`docs/research/truenas-api.md` §8.6.1). Данные не под угрозой | Ничего; через наш API не исправить. Тикет в TrueNAS |
