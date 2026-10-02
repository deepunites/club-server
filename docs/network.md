# Сеть клуба: Kea DHCP

DHCP в клубе раздаёт **Kea 3.0** (пакет Ubuntu `kea-dhcp4-server`) на машине сервера клуба. Резервации адресов
по номерам мест хранятся в базе Kea в PostgreSQL (hosts backend): Kea читает таблицу `hosts` на каждый запрос,
перезапуск для новых резерваций не нужен.

Разделение обязанностей:

| Кто | Что |
|---|---|
| Сервер клуба | Адресный план (экран «Сеть»), резервации в `hosts` базы `kea`, генерация `kea-dhcp4.conf`, предупреждения |
| Администратор (root) | Установка Kea, создание базы, права, установка конфига, перезапуск Kea |
| Помощник на ПК | MAC основной карты первым при регистрации; адреса DHCP-серверов текущих аренд в каждом отчёте |

Сервер **не** правит `/etc/kea` и не перезапускает службы: для этого нужен root. PXE и перезаливка — следующий этап.

## Адресный план

По умолчанию (всё меняется на экране «Сеть»):

| | |
|---|---|
| Подсеть | `192.168.77.0/24` |
| Шлюз, DNS, этот DHCP | `192.168.77.1` |
| Резервации мест | IP места = `192.168.77.101` + № − 1 (места 1…99) |
| Пул гостей и новых ПК | `192.168.77.200` – `192.168.77.250` |
| Аренда | 12 ч |

- Резервация создаётся только для **одобренной** машины и только по **первому** MAC из регистрации (помощник ставит
  первой карту с IPv4-шлюзом). Новая машина до одобрения получает адрес из пула.
- Место, чей IP выпадает из подсети, попадает в пул, на шлюз или на DHCP-сервер, резервацию не получает —
  предупреждение `seatOutOfRange`. Сколько мест помещается — показано под формой.
- Смена номера места в «Рабочих станциях» переносит резервацию сама (фоновая служба раз в 30 с, сохранение
  настроек сети — сразу).

## Установка (Ubuntu 26.04, от root)

```bash
systemctl mask kea-dhcp4-server     # до установки: Kea не стартует сам, пока в сети работает DHCP роутера
apt install -y kea-dhcp4-server kea-admin
```

База Kea — отдельная от базы сервера клуба, в том же PostgreSQL:

```bash
sudo -u postgres createuser _kea
sudo -u postgres createdb -O _kea kea
sudo -u _kea kea-admin db-init pgsql -h /var/run/postgresql -u _kea -n kea
```

Без `-h`/`-u` `kea-admin` 3.0.3 подключается к `localhost` как `keatest` с паролем `1234` (умолчания
`/usr/share/kea/scripts/admin-utils.sh`) и падает на `password authentication failed for user "keatest"`; с каталогом
сокета и ролью `_kea` — peer-аутентификация, пароль не нужен. Схема после `db-init` — `29.0` (Kea 3.0.x); с другой
мажорной версией сервер клуба в `hosts` не пишет (предупреждение `keaSchema`).

Права серверу клуба на таблицу резерваций (`clubsrv` — роль, под которой работает сервер клуба):

```bash
sudo -u _kea psql -d kea <<'SQL'
GRANT CONNECT ON DATABASE kea TO clubsrv;
GRANT SELECT ON schema_version TO clubsrv;
GRANT SELECT, INSERT, UPDATE, DELETE ON hosts TO clubsrv;
GRANT USAGE ON SEQUENCE hosts_host_id_seq TO clubsrv;
SQL
```

Проверено на стенде (Ubuntu 26.04, Kea 3.0.3, PostgreSQL 18, 2026-10-02): под отдельной ролью `clubsrv` синхронизация
записала резервацию (`Kea reservations synced: +1`) без `permission denied`.

Настройки сервера клуба (`appsettings.json` или переменные окружения `Kea__Enabled`, `Kea__ConnectionString`):

```json
"Kea": {
  "Enabled": true,
  "ConnectionString": "Host=/var/run/postgresql;Database=kea;Username=clubsrv"
}
```

Конфиг Kea: на экране «Сеть» — «Скачать kea-dhcp4.conf» (или `GET /panel/api/v1/network/kea-dhcp4.conf`), затем:

```bash
install -m 640 -o root -g _kea kea-dhcp4.conf /etc/kea/kea-dhcp4.conf
kea-dhcp4 -t /etc/kea/kea-dhcp4.conf
echo '/run/postgresql/.s.PGSQL.* rw,' >> /etc/apparmor.d/local/usr.sbin.kea-dhcp4   # один раз, см. ниже
apparmor_parser -r /etc/apparmor.d/usr.sbin.kea-dhcp4
systemctl unmask kea-dhcp4-server
systemctl enable --now kea-dhcp4-server
journalctl -u kea-dhcp4-server -n 20 --no-pager      # DHCP4_STARTED, без ERROR
```

**AppArmor.** Профиль `kea-dhcp4` из пакета Ubuntu включает `abstractions/mysql`, но не разрешает сокет PostgreSQL:
без правила выше Kea не стартует — `DHCP4_CONFIG_LOAD_FAIL … Unable to open database: connection to server on socket
"/var/run/postgresql/.s.PGSQL.5432" failed: Permission denied`, в `journalctl -k` — `apparmor="DENIED"
operation="connect" profile="kea-dhcp4"`. `kea-dhcp4 -t` к базе не подключается и этого не ловит. Правило лежит в
`local/` — обновление пакета его не затирает.

`kea-dhcp4 -t` проверяет интерфейс: в поле «Интерфейс сервера» должно быть имя карты этой машины, смотрящей в сеть
клуба (подсказки в поле — интерфейсы машины сервера). Конфиг перегенерировать и поставить заново нужно только при
смене настроек сети; резервации в него не входят.

Что в конфиге: один `subnet4` с пулом, шлюзом и DNS; `hosts-database` postgresql на базу `kea` под ролью `_kea`
через сокет (пароля в файле нет); хук `libdhcp_pgsql.so`; резервации по `hw-address` только внутри подсети;
`authoritative: true`; аренды — в memfile.

Проверено на стенде (2026-10-02): Kea с правилом AppArmor открывает базу резерваций по сокету без пароля
(`PGSQL_HB_DB … DHCP4_STARTED`), ПК с резервацией места 19 получил свой IP, повторно — после `ipconfig /release` и
`/renew`; помощник сообщил DHCP-сервер `192.168.1.51`, чужого DHCP нет.

## Резервации и ручные строки

Строки сервера клуба помечены `user_context = {"clubsrv": {"machineId": …, "seat": …}}`. Синхронизация:

1. проверяет версию схемы Kea (иначе — ничего не пишет);
2. в транзакции под advisory lock читает строки `hosts` своей подсети и все свои строки;
3. удаляет изменённые и лишние **свои** строки, затем вставляет новые (перестановка сетевых карт между ПК не
   упирается в уникальные индексы Kea).

Строки без метки (добавленные вручную через `kea-shell`/SQL) не трогаются никогда. Если ручная резервация занимает
MAC или IP места — наша резервация для этого места не создаётся, в панели предупреждение `manualReservation`.
Расхождения не исправляются автоматически сверх этого — только предупреждения.

## Чужой DHCP

Помощник в каждом отчёте присылает `dhcpServers` — адреса DHCP-серверов, выдавших аренды его картам. Если адрес
отличается от «Адреса этого DHCP-сервера», в панели на всех экранах — красная плашка «Чужой DHCP-сервер …» со
списком ПК, которые его видят (учитываются отчёты не старше 10 минут), и предупреждение `foreignDhcp`.
Чаще всего это роутер провайдера с включённым DHCP.

Ограничение: ПК видит только тот сервер, который выдал ему аренду. Чужой DHCP, проигравший гонку нашему на всех
ПК, так не обнаруживается; ПК со статическим адресом ничего не сообщает.

## Проверка без стенда

Тесты (`tests/Club.Server.Tests/NetworkTests.cs`) создают временную базу по схеме из пакета Kea 3.0.3
(`tests/Club.TestSupport/Kea/`). Сгенерированный конфиг проверяется настоящим `kea-dhcp4 -t`, если заданы:

```bash
KEA_DHCP4=/usr/sbin/kea-dhcp4 KEA_HOOKS=/usr/lib/x86_64-linux-gnu/kea/hooks dotnet test ClubServer.sln
```

Без `KEA_DHCP4` проверяется только структура конфига.
