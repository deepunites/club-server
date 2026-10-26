# Установка в клубе: сервер Z590 (65 ПК)

План на день установки фундамента: сервер, хранилище, сеть. Полный бездиск (зоны 1–2, `docs/diskless-full.md`)
в сервере уже есть — личные диски мест, загрузка по iSCSI, эталон и режим мастера, вкладка «Бездиск» в панели;
его пилот (раздел 5) — после того как сервер и сеть готовы. Уроки стенда
(`docs/install/stand-one-machine.md`) учтены: лимит кэша ZFS, автозапуск ВМ, режим процессора, DHCP не переключаем
до пилота.

Кто что делает: **[руки]** — человек на месте (железо, BIOS, пароли); **[удалённо]** — через ноутбук администратора,
подключённый кабелем к коммутатору клуба.

## 0. Адреса (заполнить утром)

| | Адрес |
|---|---|
| Роутер (шлюз, DNS) | `192.168.1.1` |
| TrueNAS (физический сервер) | `192.168.1.200` (вне пула DHCP роутера — проверить) |
| Сервер клуба (ВМ) | `192.168.1.201` (вне пула DHCP роутера — проверить) |
| Ноутбук администратора | `____` |

С собой: флешка 8+ ГБ с `TrueNAS-SCALE-25.10.7.iso` (на ноутбуке с Linux — «Диски» → флешка → ⋮ →
«Восстановить образ диска», файл `~/Загрузки/TrueNAS-SCALE-25.10.7.iso`; на Windows — Rufus в режиме DD),
ноутбук с пакетом `~/Документы/ClubInstall` (сервер, ISO автоустановки ВМ под .201, скрипты, `SHA256SUMS`),
кабель до коммутатора, ИБП. Раздача пакета с ноутбука: `cd ~/Документы/ClubInstall && python3 -m http.server 8000`.

## 1. Железо сервера [руки]

- Диски: системный диск TrueNAS — отдельный (от 32 ГБ); 3 SSD и 6 HDD — на SATA/M.2; SATA в режиме **AHCI**
  (не RAID). Не хватает портов — HBA-контроллер в режиме IT/HBA.
- BIOS: виртуализация **VT-x** и **VT-d** включены; загрузка с флешки; восстановление питания после пропадания —
  «Power On» (сервер сам включится после отключения света).
- Сеть: SFP+ сервера — оптикой в коммутатор; связь между двумя коммутаторами Ruijie — тоже SFP+ (не медь 1G).
- Питание: сервер и оба коммутатора — через ИБП.

## 2. TrueNAS [руки → удалённо]

1. [руки] Загрузка с флешки → Install → системный диск (только он!) → пароль `truenas_admin` → перезагрузка без
   флешки. На консоли появится адрес веб-интерфейса.
2. [руки] На ноутбуке во встроенном браузере открыть `http://<адрес с консоли>`, войти под `truenas_admin`.
3. [удалённо] Сеть: статический адрес TrueNAS и мост `br0` на порту SFP+ (Network → Interfaces, Test Changes с
   таймаутом 300 с — урок стенда), DNS, шлюз.
4. [удалённо] Пулы (как сделано в клубе 2026-10-16, все с Auto TRIM для NVMe):
   - `ssd` — зеркало 2 × NVMe Samsung (~460 ГБ): ВМ сервера клуба, эталон и диски бездисковых мест, системный датасет;
   - `games` — полоса NVMe 1 ТБ + 500 ГБ (~1,36 ТиБ): онлайн-игры (мастер библиотеки и личные слои мест). Без
     избыточности — потеря диска = восстановление из копии на `hdd`;
   - `hdd` — RAIDZ1 3 × 1 ТБ (~1,75 ТиБ; 2026-10-26 свободно ~1,43 ТиБ): копии G: и образов гибридов (`hdd/backup`) и
     офлайн-игры H: (`hdd/club`, вторая библиотека);
   - `backup` — зеркало 2 × SSD 240 ГБ (~213 ГиБ, с 2026-10-26): копии ВМ сервера клуба и эталона бездиска.
5. [удалённо] Кэш ZFS: System → Advanced → Init/Shutdown Scripts, Post Init, команда
   `echo 34359738368 > /sys/module/zfs/parameters/zfs_arc_max` (32 ГБ из 64; остальное — ВМ и система). Часовой пояс:
   `midclt call system.general.update '{"timezone":"Asia/Tashkent"}'` — по нему идут расписания копий.
6. [удалённо] Датасеты и iSCSI (Shell TrueNAS под `truenas_admin`, `midclt` работает без sudo):

   ```bash
   mk(){ midclt call pool.dataset.create "$1" >/dev/null && echo OK; }
   mk '{"name":"games/club","type":"FILESYSTEM"}'
   mk '{"name":"games/club/lib","type":"VOLUME","volsize":1099511627776,"volblocksize":"64K","sparse":true}'
   mk '{"name":"games/club/seats","type":"FILESYSTEM","quota":214748364800}'
   mk '{"name":"games/club/published","type":"FILESYSTEM"}'
   mk '{"name":"ssd/club","type":"FILESYSTEM"}'
   mk '{"name":"ssd/club/diskless","type":"FILESYSTEM"}'
   mk '{"name":"ssd/club/diskless/seats","type":"FILESYSTEM","quota":214748364800,"reservation":60129542144}'
   mk '{"name":"ssd/club/diskless/win11","type":"VOLUME","volsize":68719476736,"volblocksize":"16K","sparse":true}'
   midclt call iscsi.portal.create '{"listen":[{"ip":"0.0.0.0"}],"comment":"club"}'     # id → Library__PortalId
   midclt call service.update iscsitarget '{"enable": true}'; midclt call service.start iscsitarget
   ```

   Мастер и личные слои игр — в **одном** пуле (клон ZFS живёт только в пуле снапшота), то же для эталона и дисков
   мест. Квоты: личные слои игр 200 ГиБ (пул `games` держать ниже 80 %), диски мест 200 ГиБ + 56 ГиБ гарантии (на
   `ssd` ещё ВМ 64 ГиБ и эталон). Сервер клуба сам ни датасеты, ни портал, ни службу не создаёт — только снапшоты,
   клоны, таргеты, CHAP и группы инициаторов. В Shell TrueNAS (zsh) не называть переменные `GID`/`UID`.

   Офлайн-игры H: (вторая библиотека «Offline Games», `docs/master-editing.md`) — на `hdd`, мастер и личные слои тоже
   в одном пуле. Перед созданием — `zfs list -o space -r hdd` (сколько занимают копии):

   ```bash
   mk '{"name":"hdd/club","type":"FILESYSTEM","quota":966367641600}'                                       # 900 ГиБ
   mk '{"name":"hdd/club/lib","type":"VOLUME","volsize":1099511627776,"volblocksize":"64K","sparse":true}'  # 1 ТиБ, как G:
   mk '{"name":"hdd/club/seats","type":"FILESYSTEM","quota":128849018880}'                                 # 120 ГиБ
   mk '{"name":"hdd/club/published","type":"FILESYSTEM"}'
   ```

   Квоты `hdd`: `hdd/club` — 900 ГиБ на мастер-том H:, его версии (снапшоты) и личные слои мест (из них `hdd/club/seats`
   — 120 ГиБ на всё, что места пишут на H: за день); копии `hdd/backup` — 800 ГиБ (копия G: 640, образы гибридов 160 —
   `club-backup.sh --quotas`, п. 5 раздела 4). Квоты не резервируют место, но вместе (1700 ГиБ из ~1750) помещаются в
   пул: копии не отнимут место у H:, H: — у копий; меняя одну, проверяйте, что сумма не больше пула. NTFS на H: видит
   1 ТиБ (`volsize`), а записать можно меньше квоты: игр на H: — не больше ~700 ГБ. Копии H: нет: игры можно скачать
   снова, откат — версии библиотеки.

   Квота копии G: (`hdd/backup/games`, 640 ГиБ) ограничивает и G:: в ней полная копия мастер-тома G: и изменения за 3
   недели (обновления Steam переписывают десятки ГБ в неделю). Игр на G: больше ~500 ГБ — еженедельная копия G:
   перестаёт помещаться: задача «games-lib to hdd» падает (уведомление TrueNAS «Replication … failed»), а у G: на
   полосе без избыточности другой копии нет. Проверка — `zfs list -o space -r hdd/backup` (USED `hdd/backup/games`
   против квоты); перед этим порогом перераспределить: квоту `hdd/club` вниз, `hdd/backup` и `hdd/backup/games` вверх
   на столько же (`docs/master-editing.md`, «Место на `hdd`»).

   Пользователь сервера: группа `clubsrv`, пользователь `clubsrv` (без пароля, shell `nologin`, без SMB), привилегия
   `club-server` с ролями `READONLY_ADMIN`, `DATASET_WRITE`, `DATASET_DELETE`, `SNAPSHOT_WRITE`, `SNAPSHOT_DELETE`,
   `SHARING_ISCSI_WRITE` (`privilege.create` с `local_groups` = GID группы; `user.create` с `group` = id группы).
   Проверено по `core.get_methods` TrueNAS 25.10.7: этих ролей хватает на все 29 вызовов сервера (клоны мест, сброс
   `rollback -r`, CHAP, группы инициаторов, сессии iSCSI).
7. [удалённо] ВМ сервера клуба: 4 vCPU, **CPU Mode Host Passthrough**, 8 ГБ, диск 64 ГБ (zvol на `ssd`), сеть
   VirtIO на `br0`, **Autostart**, CD-ROM — `club-server-auto-201.iso` (п. 3), порядок загрузки: диск 1000, CD 1001.

## 3. ВМ сервера клуба [удалённо]

1. ISO автоустановки под `192.168.1.201` уже в пакете (`club-server-auto-201.iso`). Если адрес другой —
   пересобрать на ноутбуке (адреса из п. 0):

   ```bash
   scripts/server/make-autoinstall-iso.sh ubuntu-26.04.1-live-server-amd64.iso club-server-auto.iso \
       club-server <IP сервера>/24 <роутер> <роутер>,1.1.1.1 ~/.ssh/club_stand.pub clubadmin
   ```

   В TrueNAS → System → Shell (датасет `ssd/iso` создан): `wget -O /mnt/ssd/iso/club-server-auto-201.iso http://<ноутбук>:8000/club-server-auto-201.iso`.
2. Старт ВМ: установка идёт сама (~10 мин), ВМ выключается. CD-ROM отключить, старт.
3. Пакет и настройка:

   ```bash
   scp -r club-server-*-linux-x64.tar.gz deploy scripts clubadmin@<IP сервера>:~
   ssh clubadmin@<IP сервера> 'sudo bash ~/scripts/server/setup-club-server.sh <IP сервера> <IP TrueNAS> ~/club-server-*.tar.gz ~'
   ```

   Скрипт ставит PostgreSQL, club-server, CA клуба, Kea (замаскирован), TFTP + iPXE/wimboot, Samba.
4. [руки] Пароль Samba для загрузки образов: `sudo smbpasswd -a clubimg` (во вкладке терминала, которую откроет
   администратор).

## 4. Сервер ↔ TrueNAS [удалённо + руки]

1. [руки] Импорт `truenas.crt`/`truenas.key` (из `/etc/club-server/tls` сервера) в TrueNAS: Credentials →
   Certificates → Add → Import Certificate, имя `club-truenas-gui`, в **Certificate** — `truenas.crt`
   (`-----BEGIN CERTIFICATE-----`), в **Private Key** — `truenas.key`; «Add To Trusted Store» не нужен. Закрытый ключ
   вставляет человек, не администратор удалённо.
2. [удалённо] Назначить его веб-интерфейсу: `midclt call system.general.update '{"ui_certificate": <id>}'`, затем
   `midclt call system.general.ui_restart`. Проверка с сервера клуба:
   `curl -s -o /dev/null -w '%{ssl_verify_result}\n' --cacert /etc/club-server/tls/club-ca.crt https://<TrueNAS>/ui/` → `0`.
3. [руки] Ключ API для `clubsrv`: `https://<TrueNAS>/ui/apikeys` → Add, Username `clubsrv`, без срока. Ключ
   показывается один раз — сразу во вкладку терминала, которую откроет администратор:
   `stty -echo; ssh clubadmin@<IP сервера> sudo club-set-truenas-key; stty echo` (вставить, Enter; ввод скрыт, ключ не
   попадает ни в журнал sudo, ни на экран). Ответ — `TrueNas__ApiKey saved (id N, 64 chars)`.
4. [удалённо] Библиотека игр с личным слоем на пуле `games`, в `/etc/club-server/club-server.env`:
   `Library__Enabled=true`, `Library__MasterZvol=games/club/lib`, `Library__PublishedParent=games/club/published`,
   `Library__PortalId=<id портала>`, `Library__PersonalGames=true`, `Library__PersonalParent=games/club/seats`;
   `sudo systemctl restart club-server`. Строка «Connected to TrueNAS» в журнале появится только при первой операции
   с хранилищем (публикация, мастер, диск места) — сразу проверить ключ можно входом `auth.login_ex` с ВМ.
   На ПК — помощник 1.5.0 или новее (старый помощник назначения не получит, игр на G: не будет). Каждое место
   получает свой диск `games-seat-NN` на запись; при загрузке ПК он сбрасывается к опубликованной версии.
   Офлайн-игры H: — после датасетов `hdd/club` (раздел 2, п. 6): блок `Libraries__solo__*` в том же файле
   (`docs/master-editing.md`, «Вторая библиотека — H:») и перезапуск; диск `solo-seat-NN` получают ПК с помощником 1.6.0.
5. [удалённо] Резервные копии: `sh scripts/truenas/club-backup.sh <zvol ВМ>` в Shell TrueNAS (повторный запуск
   безопасен: что уже есть — датасеты, задачи, — не трогается и не дублируется). Раскладка — как в клубе с 2026-10-26:
   на `hdd` — мастер игр G: по понедельникам 05:00 (`hdd/backup/games/lib`, 3 недели) и образы гибридов по
   понедельникам 05:30 (`hdd/backup/images/club-images`, 3 недели); на `backup` — диск ВМ сервера клуба (система,
   PostgreSQL, настройки) ежедневно 04:30 (`backup/vm/clubserver`, 14 дней + воскресные 8 недель) и версии эталона
   бездиска `img-*` ежедневно 05:15 (`backup/diskless/win11`, хранятся, пока есть на `ssd`). Только собственные
   цепочки `bkp-*` TrueNAS (и `img-*` эталона по имени), без рекурсии: снапшоты сервера `lib-*`, `@clean` и
   одноразовые клоны мест не трогаются. Офлайн-игры H: (`hdd/club`) не копируются. Задача ВМ со старым именем
   «club-server VM to hdd» (настройка до 2026-10-26) не дублируется — скрипт подскажет, как перенести её на `backup`.

   Квоты копий на `hdd` — отдельным шагом, после того как владелец подтвердил раскладку `hdd` (п. 6 раздела 2):
   `sh scripts/truenas/club-backup.sh --quotas` — только квоты `hdd/backup` 800 ГиБ, `hdd/backup/games` 640,
   `hdd/backup/images` 160 (числа — переменные в начале скрипта), ни датасетов, ни задач. Ставит их датасетам без
   квоты или с большей (2026-10-26 квот на `hdd/backup` не было), но не ниже занятого — тогда пишет, сколько занято.

## 5. Бездиск: подготовка эталона [удалённо + руки] (когда сервер готов)

Порядок пилота на 2–3 ПК, драйвер сетевой карты для установщика, проверки первой загрузки и настройка эталона перед
каждой публикацией (`scripts/golden/prepare-golden.ps1`) — `docs/diskless-pilot.md`.

1. [удалённо] В TrueNAS: файловые системы `ssd/club/diskless` и `ssd/club/diskless/seats`, zvol эталона
   `ssd/club/diskless/win11` (sparse, 64 ГиБ, volblocksize 16K). В настройках сервера `Diskless__Enabled=true`
   (и `Diskless__ImageZvol`/`SeatsParent`, если пул называется не `ssd`), перезапуск службы.
   Место под личные слои — квотами, иначе один ПК (обновление игры, установка на G:) может заполнить пул, и тогда
   встают все бездисковые ПК: в клубе `ssd/club/diskless/seats` — quota 200 ГиБ + reservation 56 ГиБ (на зеркале
   ~460 ГБ рядом ВМ и эталон), `games/club/seats` (личные слои игр) — quota 200 ГиБ. Периодические задачи
   снапшотов на `ssd/club/diskless/seats` и `games/club/seats` не ставить (клоны одноразовые; сервер их всё равно
   откатывает с `-r` и удаляет вместе со снапшотами).
2. [удалённо] Установщик Windows для режима мастера. ISO Windows 11 — прямо на сервер. Страница Microsoft в регионе
   клуба ссылку не выдаёт, а сервер обновлений Microsoft (`tlu.dl.delivery.mp.microsoft.com`) доступен, поэтому в
   клубе (2026-10-18) ISO собран через UUP dump из оригинальных файлов Windows Update (каждый файл — по SHA-1 из
   метаданных Microsoft; конвертер `convert.sh` закреплён по SHA-256 и в сеть не ходит; запуск от обычного
   пользователя): `uupdump.net/get.php?id=<uuid сборки>&pack=ru-ru&edition=professional` (POST `autodl=2`) →
   `apt install aria2 cabextract wimtools chntpw genisoimage` → `bash uup_download_linux.sh`. Linux-конвертер не
   встраивает обновления: ISO — базовая `26100.1` (24H2, Pro, ru-RU, `install.wim`); накопительное, пакет включения
   26H2 (KB5121794) и .NET лежат в `/srv/club/iso/updates-26300.9807/` — ставятся в эталон в режиме мастера
   (`dism /online /add-package`) или приходят через Windows Update. Готовые «сборки» Windows с чужих сайтов не брать:
   эталон раскатывается на все бездисковые ПК. Затем
   `sudo sh ~/scripts/server/extract-winsetup.sh /srv/club/iso/Win11-26100.1-pro-ru.iso` (ISO оставить на месте,
   `chmod 444`). Скрипт монтирует ISO только для чтения в
   `/srv/club/winsetup` (fstab — ISO не удалять) и отдаёт его SMB-шарой `winsetup` (пользователь `winsetup` только для
   чтения, пароль генерирует сам), а в `/srv/club/pxe/winsetup/` кладёт `boot/bcd`, `boot/boot.sdi`, `sources/boot.wim`
   и `install.cmd` + `winpeshl.ini`: wimboot подкладывает их в WinPE, и установщик запускается с шары (в boot.wim нет
   install.wim) с обходом проверок Windows 11 (LabConfig). `win-noprompt.py` здесь не нужен.
3. [удалённо] Kea уже раздаёт DHCP клуба (`docs/network.md`, переключение 2026-10-18). После
   `Diskless__Enabled=true` конфиг Kea скачать заново (в нём появятся классы PXE `club-reimage-*`), проверить
   `sudo -u _kea kea-dhcp4 -t`, поставить и `systemctl restart kea-dhcp4-server`.
4. [руки] BIOS бездискового ПК: UEFI, сетевая загрузка IPv4 первой, **Secure Boot выключен**, TPM не нужен.
5. [удалённо] Панель → «Рабочие станции» → «Добавить ПК по MAC», режим «Бездиск»: MAC сетевой карты (из BIOS или с
   экрана загрузки по сети) и номер места — ПК сразу одобрен и бездисковый. «Режим мастера»: этот
   ПК, «Установка Windows с нуля» → [руки] перезагрузка ПК (в течение 3 минут: установщик выдаётся один раз, повтор —
   снова «Режим мастера» с галочкой) → установщик Windows видит диск эталона (Диск 0) → установка. После копирования
   файлов установщик перезагружает ПК, тот сам грузится с диска эталона и продолжает. Затем драйверы всех плат и
   видеокарт зон 1–2, помощник, шелл.
6. [удалённо] «Установка завершена» → [руки] ПК выключить → «Опубликовать» версию. Остальные бездисковые ПК
   грузятся с чистыми личными дисками этой версии.

## 6. Проверка дня

- Панель `https://<IP сервера>:5443/panel/` открывается, «Библиотека игр» видит TrueNAS.
- Сеть TrueNAS: `br0` ← `bond1` (LACP, оба порта X520 по 10G в один коммутатор, на нём агрегация с LACP — Reyee:
  Ports → Port Aggregation, LACP включить при создании). У `/proc/net/bonding/bond1` оба порта с одним Aggregator ID;
  STP на `br0` выключен (иначе ~30 с простоя при каждом подключении bond). ПК — 1000Mb/s.
- DHCP раздаёт Kea на сервере клуба (DHCP роутера выключен); `systemctl is-active kea-dhcp4-server` — active.
- Резервные копии: `midclt call replication.query` — обе задачи `FINISHED` после первого запуска.

Дальше: гибридные ПК (зоны 3–5) — помощник 1.5.0 (`docs/helper.md`), игры — личный диск места (раздел 4, п. 4);
бездиск — пилот на 2–3 ПК (раздел 5), затем зоны 1–2 и Kea как DHCP клуба.

## Приложение: BIOS клиентских ПК [руки]

| Зона | Платы | Что выставить |
|---|---|---|
| 1–2 (бездиск) | H410/H510 (Gigabyte, MSI, Colorful), A520 | Boot mode **UEFI** (CSM выкл.); **Network Stack / PXE IPv4 — вкл.**, IPv6 PXE — выкл.; первым в Boot Order — **UEFI: PXE IPv4 (сетевая карта)**; **Secure Boot — выкл.**; Fast Boot — выкл.; Wake on LAN — вкл. (по желанию); энергосбережение сети (ErP/EuP) — выкл. |
| 3–5 и новые (гибрид) | H510/B560, Z490, B450 | UEFI, **TPM (PTT/fTPM) — вкл.**, **Secure Boot — вкл.** (FACEIT, Valorant); порядок загрузки — диск первым, для перезаливки F12 → UEFI IPv4; или сеть первой плюс `Imaging__BcdbootAddLast=true` (docs/imaging.md) |

Где искать: Gigabyte — Settings → IO Ports → Network Stack Configuration; Boot → CSM Support. MSI — Settings →
Advanced → Integrated Peripherals → Network Stack; Boot → Boot mode select. Colorful — Advanced → Network Stack.
Для бездисковых ПК записать **MAC** с экрана загрузки по сети (iPXE показывает его первой строкой) или из BIOS —
по нему ПК добавляется в панели («Рабочие станции» → «Добавить ПК по MAC», режим «Бездиск»; новый гибрид без Windows —
режим «Гибрид (свой диск)», затем «Перезалить»).
