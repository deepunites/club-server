# TrueNAS SCALE middleware API: транспорт, ZFS, iSCSI

Дата: 2026-09-27. Базис: middleware `TS-25.04.2.6` и `TS-25.10.7` (ветки `release/25.04.2.6` и `release/25.10.7` в iSCSI/ZFS-коде совпадают с тегами), для взгляда вперёд — `stable/26` и `release/26.0.0-RC.1`. Также использованы `truenas/api_client`, `truenas/scst`, `truenas/zfs`, `truenas/py-libzfs` тех же веток, api.truenas.com, docs.truenas.com, man-страницы OpenZFS.

Каждая находка перепроверена отдельным проходом (verify-файлы есть по всем трём темам: transport, zfs, iscsi).

**Легенда статусов**

| Метка | Значение |
|---|---|
| **[ПОДТВЕРЖДЕНО]** | перепроверено по первичному источнику, ссылка рядом |
| **[ИСПРАВЛЕНО]** | исходная находка оказалась неверной, здесь приведён только исправленный вариант |
| **[ГИПОТЕЗА]** | вывод не подтверждён первоисточником или стендом; для части случаев указано «подтверждено кодом, без запуска» |

Сокращения ссылок: `mw04` = `github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared`, `mw10` = `.../TS-25.10.7/...`.

---

## 0. Коротко

1. Только `wss://<host>/api/<версия>`. API-ключ, успешно предъявленный по незащищённому транспорту, TrueNAS **сам отзывает** (§2.3).
2. Закрепление версии в URL спасает внутри одной линейки (25.04.x, 25.10.x), но **не** при переходе 25.04 → 25.10: методы, которые в 25.04 были old-style (`pool.dataset.create`, `service.*`) или появились позже (`pool.snapshot.*`), через `/api/v25.04.x` на 25.10 дают `-32001` с `ENOMETHOD`. Нужны адаптеры по мажорным линиям (§3).
3. Снапшоты: 25.04 — публичный `zfs.snapshot.*`; 25.10 — `pool.snapshot.*`, а `zfs.snapshot.*` стал приватным; в 26.0-RC `zfs.snapshot` удалён (§7.1).
4. Ни один из нужных нам методов ZFS/iSCSI не является job, кроме `service.control` в 25.10 (§5.5, §8.8).
5. Идемпотентность строить на `query` до и после вызова, а не на разборе ошибок: коды «уже существует» различаются по методам и версиям (§7.7).
6. Смена версии тома = **новый** клон + extent + target. `extent.update`/`targetextent.update` на живом таргете выбивают LUN у всех клиентов (§8.4–8.6).
7. `pool.dataset.delete` на zvol каскадно и без проверки сессий удаляет его iSCSI-экстенты, связки и опустевшие таргеты (§7.3, §8.7).
8. Успех `iscsi.*` не значит, что SCST применил конфигурацию: код возврата scstadmin middleware не смотрит, а на 25.10.7 reload срывается на read-only устройстве в copy_manager — стенд 2026-10-02, таргет новой версии остался выключенным (§8.4). Публикацию проверять по сети (SendTargets) и при необходимости повторять reload.

---

## 1. Транспорт

| Тема | Факт | Статус / источник |
|---|---|---|
| Эндпоинт | `GET /api/{version}` (WebSocket, JSON-RPC 2.0) для каждого каталога `api/vNN_NN_N` (`_`→`.`) плюс алиас `current`. `current` и последняя версия обслуживаются классом `Method`, более старые — `LegacyAPIMethod`. middlewared слушает `127.0.0.1:6000`, наружу проксирует nginx (`location /api`). Внешний URL: `wss://<host>[:ui_httpsport]/api/v25.04.2`. Маршрут `/api/v24.10` тоже зарегистрирован, хотя в `/api/versions` не показан. В `stable/26` nginx проксирует на upstream `middlewared`. | [ПОДТВЕРЖДЕНО] [mw04 main.py#L150-L233](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/main.py#L150-L233), [nginx.conf.mako#L189-L199](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/etc_files/local/nginx/nginx.conf.mako#L189-L199) |
| Список версий | `GET /api/versions` без аутентификации отдаёт JSON-массив вида `["v25.04.0", …]`, без `current`, в 25.04.2.6 и 25.10.7 без `v24.10`. В 25.10 добавлен `GET /api/boot_id`. | [ПОДТВЕРЖДЕНО] [mw10 main.py#L1302-L1305](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/main.py#L1302-L1305) |
| Формат запроса | `{"jsonrpc":"2.0","id":…,"method":"svc.method","params":[…]}`. `params` **только массив** (объект → `-32600` «'params' member must be of type array»); без `params` подставляется `[]`. Без `id` — notification: метод выполняется, ответа нет, и `-32600` тоже не приходит. Batch не поддерживается (`-32700`, id=null). | [ПОДТВЕРЖДЕНО] [mw10 rpc.py#L273-L320](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py#L273-L320), [utils/limits.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/limits.py#L41-L90) |
| Лимиты | На соединение `SoftHardSemaphore(10, 20)`: 10 вызовов выполняются параллельно, до 20 в работе и очереди, 21-й получает `-32000`. Размер сообщения: 8192 до логина (закрытие 1007), 65536 после (1009), исключения до 2 МиБ (`filesystem.file_receive`, `failover.datastore.sql`). Длина считается в **символах** (`len(str)`), а не в байтах. | [ПОДТВЕРЖДЕНО] [mw10 utils/lock.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/lock.py#L8-L25), [limits.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/limits.py#L10-L40) |
| Rate limit | Действует только на неаутентифицированные вызовы методов, не требующих аутентификации: 20 вызовов за 60 с на пару (метод, IP); при превышении сервер спит 1–10 с и отвечает `-32001` «Rate Limit Exceeded» (EBUSY). Кэш глобальный на 100 пар: когда он заполнен, лимитируются все неаутентифицированные вызовы с любых IP. Методы без аутентификации: `auth.login*`, `auth.login_ex_continue`, `auth.login_with_token`, `auth.mechanism_choices`, `system.advanced.login_banner` и др.; `core.ping` без аутентификации доступен **только с 25.10**. AF_UNIX и HA не лимитируются. | [ИСПРАВЛЕНО] [mw10 rate_limit/cache.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/rate_limit/cache.py#L12-L95), [mw04 core_service.py L506](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/service/core_service.py) |
| Сериализация | EJSON из `truenas_api_client`: datetime → `{"$date": мс UTC}`, date → `{"$type":"date","$value":"YYYY-MM-DD"}`, time → `{"$time":"HH:MM:SS[.ffffff]"}`, set → `{"$set":[…]}`, `$ipv4_interface`/`$ipv6_interface`. Сервер кодирует `$date` через `timetuple()`, так что значение всегда кратно 1000 (точность 1 с); naive datetime трактуется как UTC. Для C# нужны свои `JsonConverter`. | [ПОДТВЕРЖДЕНО] [api_client ejson.py](https://github.com/truenas/api_client/blob/TS-25.10.7/truenas_api_client/ejson.py) |
| Keepalive (сервер) | Сервер сам WS-PING не шлёт: `WebSocketResponse()` без heartbeat, autoping только отвечает PONG на PING клиента. В nginx 25.04/25.10 для `/api` **не задан** `proxy_read_timeout` → действует дефолт 60 с («closed if the proxied server does not transmit any data within 60 seconds»). В `stable/26` задано 3600. | [ПОДТВЕРЖДЕНО] [mw10 ws_handler/base.py L17](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/base.py#L25-L35), [nginx 25.10](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/etc_files/local/nginx/nginx.conf.mako#L242-L255), [nginx stable/26](https://github.com/truenas/middleware/blob/stable/26/src/middlewared/middlewared/etc_files/local/nginx/nginx.conf.mako#L289-L304), [nginx docs](https://nginx.org/en/docs/http/websocket.html) |
| Keepalive (.NET) | По умолчанию `ClientWebSocket` (`KeepAliveTimeout = InfiniteTimeSpan`) шлёт unsolicited PONG. Клиентские фреймы таймер чтения nginx от upstream не сбрасывают, поэтому простаивающее соединение с 25.04/25.10 закроется примерно через 60 с. Лечится `core.ping` раз в 20–30 с (после логина; в 25.04 он требует аутентификации) и/или `KeepAliveTimeout` на .NET 9+. | [ГИПОТЕЗА] согласуется с [MS docs](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.keepalivetimeout), [aiohttp](https://docs.aiohttp.org/en/stable/web_reference.html), nginx; на стенде не проверено |
| TLS | По умолчанию самоподписанный `truenas_default`. `renew_certs` раз в сутки перевыпускает его **с новым ключом**, когда до истечения меньше `renew_days` (по умолчанию 10) дней, так что pinning отпечатка сломается. Версии TLS задаются через `system.general ui_httpsprotocols`. В 25.10 удалён встроенный CA. IP allowlist (`ui_allowlist`) закрывает WS кодом 1008. | [ПОДТВЕРЖДЕНО] [mw10 renew_certs.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/crypto_/renew_certs.py#L20-L66), [25.10 version notes](https://www.truenas.com/docs/scale/25.10/gettingstarted/versionnotes/) |
| REST / legacy | REST `/api/v2.0` deprecated в 25.04, алерт «will be removed in version 26.04» появился с 25.10.1, в 26 REST не работает. Старый DDP-протокол на `/websocket` ещё маршрутизируется, но использовать его не надо. | [ПОДТВЕРЖДЕНО] [26 deprecations](https://www.truenas.com/docs/scale/26/gettingstarted/deprecations/), [alert/source/rest.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/alert/source/rest.py#L5-L16) |

**Официальный Python-клиент** (`truenas/api_client`, теги TS-25.04.2.6 и TS-25.10.7), для справки. URI на `/websocket` выбирает `LegacyClient`, иначе используется JSON-RPC. Таймаут сокета 10 с, handshake 30 с, TCP keepalive 1/1/5, фонового WS-ping нет, `CALL_TIMEOUT=60`, id запроса — uuid4. `verify_ssl` есть в коде уже в 25.04, хотя README пишет «только 25.10». [ПОДТВЕРЖДЕНО] [api_client __init__.py](https://github.com/truenas/api_client/blob/TS-25.10.7/truenas_api_client/__init__.py#L73-L180)

**Документация.** api.truenas.com/v25.04 собрана новыми шаблонами. Её `jobs.html` описывает `message_ids`, которых в сервере 25.04 нет, а old-style методы (`zfs.snapshot.*`, `pool.dataset.create/query`, `core.job_wait`, `service.start/query`) отдают 404. Для 25.04 первичный источник — исходники тега или `/api/docs` на самом устройстве. [ПОДТВЕРЖДЕНО] [jobs.rst 25.04](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared_docs/docs/jobs.rst)

---

## 2. Аутентификация и права

### 2.1 Логин
- Аутентификация идёт внутри WS-сессии. `auth.login_ex({mechanism:"API_KEY_PLAIN", username, api_key, login_options:{user_info}})` возвращает `{response_type: SUCCESS|AUTH_ERR|EXPIRED|OTP_REQUIRED|REDIRECT}`. На неверные учётные данные исключения нет, `response_type` нужно проверять. Но `login_ex` бросает `CallError EOPNOTSUPP`, если механизм запрещён текущим AAL, и `EINVAL` на `OTP_TOKEN` без активного диалога; возможны также `-32602` и «Rate Limit Exceeded». `auth.login_with_api_key(key)` берёт id из префикса `<id>-` и возвращает bool. Ключ имеет вид `<id>-<64 символа>` и показывается один раз. [ПОДТВЕРЖДЕНО] [mw04 auth.py](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/plugins/auth.py#L1155-L1190), [api login_ex](https://api.truenas.com/v25.04/api_methods_auth.login_ex.html)
- В 26 `auth.login` и `auth.login_with_api_key` объявлены deprecated, удаление в 27. `login_ex` + `API_KEY_PLAIN` поддерживается и дальше. SCRAM-SHA-512 для ключей есть только с 26. [ПОДТВЕРЖДЕНО] [26 deprecations](https://www.truenas.com/docs/scale/26/gettingstarted/deprecations/), [api_client README](https://github.com/truenas/api_client/blob/master/README.md)

### 2.2 Сессия и AAL
AAL1 (по умолчанию): сессия по ключу живёт до 30 дней, таймаута неактивности нет. STIG включает **AAL2**, и тогда `API_KEY_PLAIN` отклоняется `CallError EOPNOTSUPP` (`-32001`), а не через `response_type`. «Session is expired» (EACCES) приходит только на методы, требующие авторизации. Методы `no_authz_required` (`core.get_jobs`, `core.subscribe`, `core.ping`) после истечения сессии продолжают отвечать, поэтому **живость сессии через `core.ping` проверять нельзя**. [ИСПРАВЛЕНО] [mw10 utils/auth.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/auth.py#L66-L113), [mw10 main.py#L913-L921](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/main.py#L913-L926)

### 2.3 Незащищённый транспорт
Если API-ключ успешно прошёл аутентификацию по незащищённому транспорту, TrueNAS **автоматически отзывает** ключ (`api_key.revoke`): клиент получает `EXPIRED`, в аудит пишется «API key revoked due to insecure transport». Защищённым считается TLS (nginx передаёт `X-Https: on`), AF_UNIX, loopback и HA. [ПОДТВЕРЖДЕНО] [mw04 auth.py#L930-L945](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/plugins/auth.py#L930-L945), [25.04 release notes](https://www.truenas.com/docs/scale/25.04/gettingstarted/scalereleasenotes/). Следствие: TLS-терминирующий прокси, который ходит к TrueNAS по HTTP, тоже приведёт к отзыву — [ГИПОТЕЗА] (выведено из кода).

### 2.4 RBAC

| Тема | Факт | Статус |
|---|---|---|
| Ключи | С 25.04 ключ привязан к пользователю (`api_key.create {name, username, expires_at?}`) и получает роли пользователя через privilege (`privilege.create {name, local_groups:[GID], ds_groups, roles, web_shell}`). Ключи 24.10 с allowlist при апгрейде отзываются. У целевого пользователя должна быть хотя бы одна роль. `READONLY_ADMIN` без `API_KEY_WRITE` создаёт ключи только себе. В GPOS STIG менять ключи нельзя. | [ПОДТВЕРЖДЕНО] [mw04 api_key.py](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/plugins/api_key.py#L160-L170) |
| CRUD-роли | Из `role_prefix`: query/get_instance/config → `<P>_READ`, create/update → `<P>_WRITE`, delete → `<P>_DELETE` при `role_separate_delete`. Из нескольких ролей метода достаточно **любой**. Метод без ролей доступен только FULL_ADMIN. Префиксы: `pool.dataset` → DATASET (+DATASET_DELETE); `zfs.snapshot` (25.04) и `pool.snapshot` (25.10) → SNAPSHOT (+SNAPSHOT_DELETE); `iscsi.extent/target/targetextent/portal/initiator/auth/global` → `SHARING_ISCSI_*`; `SHARING_ISCSI_WRITE` включает все iSCSI `*_WRITE`. | [ПОДТВЕРЖДЕНО] [mw04 role.py](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/role.py#L141-L206), [rbac](https://api.truenas.com/v25.10/rbac.html) |
| 25.04: clone/rollback | У `zfs.snapshot.clone` и `rollback` в 25.04 ролей нет (`@accepts` без roles, у сервиса нет `role_prefix`), значит, доступ только у FULL_ADMIN. Другого публичного пути клонирования в 25.04 по коду нет (на стенде не проверено). `system.state/ready` в 25.04 тоже только для FULL_ADMIN, в 25.10 им нужна `SYSTEM_GENERAL_READ`. `system.info` требует `READONLY_ADMIN`. | [ПОДТВЕРЖДЕНО] [mw04 snapshot_actions.py](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/plugins/zfs_/snapshot_actions.py#L8-L60) |
| Без авторизации | Сессия по ключу — пользовательская (`is_user_session=True`), поэтому ключу без ролей доступны `core.ping`, `core.get_jobs`, `core.job_wait`, `core.job_abort`, `core.subscribe/unsubscribe`, `core.get_methods`, `system.version`, `system.version_short`. Комментарий в `main.py` «authorization is _always_ enforced» устарел. | [ПОДТВЕРЖДЕНО] [mw10 auth.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/auth.py#L56-L160) |
| service.* для iSCSI | `service.control` (job) и устаревшие `service.reload/restart` принимают любую из `SERVICE_WRITE`, `SHARING_ISCSI_WRITE` (и NFS/SMB/FTP/NVME `*_WRITE`); проверка в рантайме сопоставляет `iscsitarget` с `SHARING_ISCSI_WRITE`. Значит, `clubsrv` может делать RELOAD/RESTART iSCSI без `SERVICE_WRITE`. Сервер этим не пользуется: reload — через `iscsi.target.update` (§8.4), RESTART рвёт сессии всех ПК (§9.3 п. 7). `*_choices` (например, `iscsi.extent.disk_choices`) автоматически доступны `READONLY_ADMIN`. | [ПОДТВЕРЖДЕНО] кодом, без запуска: [service.py#L147-L159](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/service.py#L147-L159), [service_/utils.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/service_/utils.py#L5-L38), [main.py#L342-L356](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/main.py#L342-L356) |
| system.* | `system.version` = `'TrueNAS-<version>'`, `system.version_short` = `<version>`, `system.boot_id` без аутентификации (это kernel boot_id). `core.get_methods` отдаёт по каждому методу флаги `job`, `roles`, `downloadable` и др. | [ПОДТВЕРЖДЕНО] [mw10 product.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/system/product.py#L74-L118) |

---

## 3. Версии API и закрепление (Q7)

| Факт | Статус |
|---|---|
| Новая версия API появляется **только при изменении схем**, а не на каждом minor-релизе. 25.04.2.1–2.6, 25.10.3, 25.10.3.1, 25.10.6 и 25.10.7 новых версий не добавили, 25.10.4 добавил сразу две (v25_10_3 и v25_10_4). По тегам: 25.04.2.6 → current `v25.04.2`; 25.10.0 → `v25.10.0`; 25.10.7 → `v25.10.5`. Старые каталоги до `stable/26` включительно не удалялись (там ещё есть `v24_10`). **Номер API ≠ номер релиза**, URL из `system.version` собирать нельзя. | [ИСПРАВЛЕНО] [дерево api/ TS-25.10.7](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/current.py) |
| `LegacyAPIMethod` адаптирует params/result только у new-style методов (`@api_method`). Old-style (`@accepts` или без декоратора) проходят без адаптации. Если у new-style метода нет модели в старой версии, ещё до выполнения бросается `MethodNotFoundError`: клиент видит **`-32001` «Method call error», `data.error=201`, `errname=ENOMETHOD`**, а не `-32601`. | [ИСПРАВЛЕНО] [mw10 legacy_api_method.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/legacy_api_method.py#L55-L80), [call.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/utils/service/call.py#L12-L14) |
| Private-методы на `@api_method(private=True)` (их много в 25.10) имеют new-style модели вне `api/vXX`, поэтому через `/api/v25.04.x` они тоже получат `ENOMETHOD`. | [ГИПОТЕЗА] выведено из кода |
| На 25.10 через `/api/v25.04.2`: `pool.dataset.create/update/delete` → ENOMETHOD (в `v25_04_2/pool_dataset.py` дерева 25.10 есть только `PoolDatasetDestroySnapshots*`). `pool.snapshot.clone/create/update/delete/rollback/hold/release/rename` → ENOMETHOD. `service.start/control/update` → ENOMETHOD (в `v25_04_*` нет `service.py`). `iscsi.*` работают по схеме 25.04 (модели `v25_04_2` в дереве 25.10 есть). | [ПОДТВЕРЖДЕНО] по коду, на стенде не проверено: [v25_04_2/pool_dataset.py (дерево 25.10.7)](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/v25_04_2/pool_dataset.py), [legacy_api_method](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/base/server/legacy_api_method.py#L36-L86) |
| `pool.snapshot.query/get_instance` через `/api/v25.04.x` на 25.10: аргументы (`QueryArgs`) в `v25_04_2` есть, метод выполнится, но адаптация результата `PoolSnapshotQueryResult` упадёт → `-32001` с EINVAL. | [ГИПОТЕЗА] выведено из кода |
| Внутри линейки 25.10 модели `pool_dataset.py` одинаковы в `v25_10_0..5`. `pool_snapshot.py` изменился в `v25_10_1`: `value`/`rawvalue` в ответе и `user_properties_update[].value` во входе стали `LongString` (снят лимит 1024). На `/api/v25.10.0` метка снапшота длиннее 1024 символов будет отвергнута. iSCSI-модели внутри серий идентичны. | [ПОДТВЕРЖДЕНО] [v25_10_1/pool_snapshot.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/v25_10_1/pool_snapshot.py#L47-L50) |
| Приватные методы: в 25.10 вызов через WS пока выполняется, пишется только warning «Private method … called on a connection without private_methods enabled» с комментарием `# FIXME: Eventually, prohibit this`. Авторизация при этом работает: у приватных методов ролей нет, так что проходит только FULL_ADMIN. В 25.04 проверки нет вовсе. | [ПОДТВЕРЖДЕНО] [mw10 rpc.py#L320-L330](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py#L320-L330) |

---

## 4. Ошибки JSON-RPC

| Код | Когда | `error.message` | `error.data` |
|---|---|---|---|
| -32700 | невалидный JSON, batch | конкретный текст причины | — |
| -32600 | невалидный запрос (приходит только если есть `id`) | конкретный текст | — |
| -32601 | метода нет в таблице | «Method does not exist» | — |
| -32602 | ValidationError(s), pydantic, InstanceNotFound | «Invalid params» | `error` **всегда 22 (EINVAL)**; настоящие ошибки в `extra = [[attribute, errmsg, errno], …]`. «Не найдено» = `extra[i][2] == 2` (ENOENT) |
| -32603 | сбой сериализации ответа | «Failed to JSON serialize server message» | errno EFAULT |
| -32000 | более 20 одновременных вызовов | текст с числом | — |
| -32001 | CallError и прочие исключения | «Method call error» | `{error: errno, errname, reason: "[ERRNAME] msg", trace|null, extra|null, py_exception?}`; errno CallError по умолчанию EFAULT |

[ИСПРАВЛЕНО] (исходная находка неверно описывала `data.error` для `-32602`) [mw10 rpc.py#L123-L128, L366-L373](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py), [service_exception.py#L115-L118](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/service_exception.py#L115-L118), [api_client jsonrpc.py](https://github.com/truenas/api_client/blob/TS-25.10.7/truenas_api_client/jsonrpc.py#L10-L19).

Собственные errno: `ENOMETHOD=201`, `EDATASETISLOCKED=205`, `ENOTAUTHENTICATED=207`, `EZFS_*` 2000–2100. [ПОДТВЕРЖДЕНО] [api_client exc.py](https://github.com/truenas/api_client/blob/TS-25.10.7/truenas_api_client/exc.py)

---

## 5. Jobs

| # | Факт | Статус |
|---|---|---|
| 5.1 | **25.04:** job-метод сразу отвечает целым job id. Результат получают через `core.subscribe("core.get_jobs")` (`collection_update` added/changed: `id`, `state` WAITING/RUNNING/SUCCESS/FAILED/ABORTED, `progress`, `result`, `error`, `exc_info{type,extra,repr,errno}`, `time_*`) или опросом `core.get_jobs([["id","=",N]])`. Поля `message_ids` в 25.04 нет. | [ПОДТВЕРЖДЕНО] [mw04 method.py](https://github.com/truenas/middleware/blob/TS-25.04.2.6/src/middlewared/middlewared/api/base/server/method.py#L40-L60) |
| 5.2 | **25.10:** по умолчанию поведение то же (`App.legacy_jobs=True`). После `core.set_options({"legacy_jobs": false})` ответ приходит только по завершении job, а job id сообщается событием с `fields.message_ids`, где лежит id нашего запроса. Запросы с id `0` или `""` в `message_ids` не попадают, поэтому id должны быть непустыми и ненулевыми. | [ПОДТВЕРЖДЕНО] [mw10 app.py L17](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/app.py#L17), [job.py L300/L683](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/job.py#L683) |
| 5.3 | В new-style режиме вызов держит слот семафора (10/20) до конца job. При разрыве WS финальный ответ теряется, а job, судя по коду, продолжает работать; найти её можно через `core.get_jobs([["message_ids","rin",<id>]])`. | [ПОДТВЕРЖДЕНО] (часть про разрыв выведена из кода) [mw10 rpc.py#L355-L365](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py#L355-L365) |
| 5.4 | Через `/api/v25.04.x` на 25.10 new-style jobs не включить: модель `core.set_options` в `v25_04_2` знает только `py_exceptions` (`extra="ignore"`), поле `legacy_jobs` молча отбрасывается. | [ПОДТВЕРЖДЕНО] [v25_04_2/core.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/v25_04_2/core.py#L27-L43) |
| 5.5 | `core.job_wait(id)` сам является `@job`. В legacy-режиме он возвращает **новый** job id. | [ПОДТВЕРЖДЕНО] [mw10 core_service.py#L165-L170](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/service/core_service.py#L165-L170) |
| 5.6 | Видимость: без FULL_ADMIN видны только jobs того же username (или при роли из `options['read_roles']`); после переподключения тем же ключом свои jobs видны. `result` по умолчанию редактируется, сырой — через `{"extra":{"raw_result":true}}`. Jobs живут в памяти (`JobsDeque maxlen=1000`). **Id — счётчик процесса с 1: после рестарта middlewared старый id может указывать на чужую новую job.** `system.boot_id` — kernel boot_id, при рестарте только middlewared он не меняется. Найденную job надо сверять по method/arguments/time_started. | [ПОДТВЕРЖДЕНО] с поправками верификатора [mw10 job.py#L230-L262](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/job.py#L230-L262) |
| 5.7 | `@job(lock, lock_queue_size=5)`: если очередь с тем же lock переполнена, новая job не создаётся, возвращается id последней из очереди (в 25.10 к ней дописывается `message_id`). При `lock_queue_size=0` и уже идущей job — EBUSY «This job is already being performed» (или «…by another user»). `transient=True` события **не подавляет**: ADDED/CHANGED отправляются, а job только удаляется из `core.get_jobs` после завершения (docstring расходится с кодом). | [ИСПРАВЛЕНО] [mw10 job.py#L126-L158](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/job.py#L126-L158) |
| 5.8 | Из ZFS-методов наших сценариев jobs нет, всё синхронно. Единственный job в области — `pool.dataset.destroy_snapshots` (`removed_in='v26.04'`). | [ПОДТВЕРЖДЕНО] [mw10 pool_/dataset.py#L852-L883](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/dataset.py#L852-L883) |

---

## 6. События

- `core.subscribe("<event>")` возвращает строковый ident (uuid4), `core.unsubscribe(ident)` снимает подписку. Уведомления приходят как `{"method":"collection_update","params":{msg:"added"|"changed"|"removed", collection, id?, fields?, extra?}}`, при завершении источника — `notify_unsubscribed`. Подписки живут в пределах соединения, после reconnect их надо восстанавливать. Для `<ns>.query` нужна роль `<P>_READ`. [ПОДТВЕРЖДЕНО] [mw10 rpc.py#L150-L200](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py#L150-L200)
- У `pool.snapshot` в 25.10 `event_send=False`, неявных CRUD-событий нет, но явные есть: create шлёт ADDED, удаление — REMOVED в `pool.snapshot.query` (роль SNAPSHOT_READ). [ПОДТВЕРЖДЕНО] [mw10 pool_/snapshot.py](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/pool_/snapshot.py#L12-L20)
- Для iSCSI есть только события конфигурации `iscsi.{extent,target,targetextent,portal,initiator,auth}.query` (у REMOVED только id). Событий о входе и выходе сессий и о применении конфига SCST нет. `service.query` CHANGED шлётся при start/stop/restart и `service.update`, при reload — нет. [ПОДТВЕРЖДЕНО] [api_events iscsi.targetextent](https://api.truenas.com/v25.04/api_events_iscsi.targetextent.query.html)
- При каскадном удалении через `pool.dataset.delete` события REMOVED для `iscsi.extent.query` и `iscsi.targetextent.query` **не приходят** (delegate вызывает `datastore.delete` напрямую), для `iscsi.target.query` осиротевших таргетов — приходят. [ГИПОТЕЗА] подтверждено кодом, на стенде нет. [crud_service.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/service/crud_service.py#L255-L297), [fs_attachment_delegate.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/fs_attachment_delegate.py#L15-L30)

---

## 7. ZFS: zvol, снапшоты, клоны, user properties

### 7.1 Пространства имён по версиям

| Операция | 25.04 | 25.10 | 26.0-RC.1 |
|---|---|---|---|
| zvol / датасет | `pool.dataset.*` (old-style `@accepts`) | `pool.dataset.*` (pydantic, `extra='forbid'`, `str_max_length=1024`) + `rename` | `pool.dataset.*` |
| снапшот | `zfs.snapshot.*` (публичный, `role_prefix=SNAPSHOT`) + clone/rollback/hold/release | `pool.snapshot.*` (clone, rollback, hold, release, query, create, update, delete, rename, get_instance); `zfs.snapshot.*` **private** | `pool.snapshot.*`; `zfs.snapshot`/`zfs.dataset` удалены, есть `zfs.resource.snapshot.*` |
| чтение плоских свойств | — | `zfs.resource.query` (роль ZFS_RESOURCE_READ, снапшоты не поддерживает) | есть |
| `zfs.dataset.*` | private | private | удалён |

[ПОДТВЕРЖДЕНО] [mw04 zfs_/snapshot.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/zfs_/snapshot.py#L15-L23), [mw10 pool_/snapshot.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/snapshot.py#L12-L124), [zfs.resource.query](https://api.truenas.com/v25.10/api_methods_zfs.resource.query.html), [26.0-RC.1 snapshot_crud.py](https://github.com/truenas/middleware/blob/release/26.0.0-RC.1/src/middlewared/middlewared/plugins/zfs/snapshot_crud.py#L62). Отличия 25.04 → 25.10 в `create`: нет `suspend_vms`; в `update` удаление меток вынесено в отдельный `user_properties_remove`. [ПОДТВЕРЖДЕНО]

### 7.2 zvol: создание и изменение

- `pool.dataset.create`: `name` (полный путь, обязательно со `/`), `type:"VOLUME"`, `volsize` (байты, обязателен), `volblocksize` (enum `512…128K`; по умолчанию middleware ставит `16K`, на dRAID `128K`), `sparse` (без refreservation), `force_size` (снимает проверку 80%), `comments` (хранится в `org.freenas:description`), `readonly` `ON/OFF/INHERIT`, `user_properties:[{key:"ns:name", value}]`, `create_ancestors`. Метод не job. [ПОДТВЕРЖДЕНО] [mw04 dataset.py#L451-L498](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/pool_/dataset.py#L451-L498), [v25_10_5/pool_dataset.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/v25_10_5/pool_dataset.py#L184-L290)
- Значение user property длиннее 1024 символов API 25.10 отвергнет (`str_max_length=1024`), хотя ZFS допускает 8192 байта. [ПОДТВЕРЖДЕНО] [model.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/base/model.py#L149-L152)
- Валидации. **25.04:** «This field is required for VOLUME», «This field is not valid for VOLUME», 80% от available родителя без `force_size`, кратность `volsize` блоку, «Turn off readonly mode on <parent>». **25.10:** отсутствие `volsize` и лишние поля ловит pydantic (тексты вида «Field required», «Extra inputs are not permitted», attribute `data.<Model>.<field>`, точные строки не проверялись); проверки 80%, кратности и readonly родителя остались. В обеих версиях это `-32602`, но **тексты разные**, разбирать ошибки по тексту нельзя. [ИСПРАВЛЕНО] [mw10 dataset.py#L315-L356](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/dataset.py#L315-L356)
- `pool.dataset.recommended_zvol_blocksize(pool)` (DATASET_READ): для RAIDZ1 3 диска → 16K, 4–5 → 32K, 6–9 → 64K, 10+ → 128K. `create` его сам не вызывает. `volblocksize` после создания менять нельзя. [ПОДТВЕРЖДЕНО] [dataset_info.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/pool_/dataset_info.py#L42-L69)
- `pool.dataset.update(id, data)`: `user_properties_update:[{key,value}|{key,remove:true}]` (value и remove вместе нельзя); `user_properties:[…]` — полная замена; оба поля сразу — ошибка. Смена `readonly` у zvol вызывает `iscsi.global.resync_readonly_property_for_zvol`: у **включённого** экстента `zvol/<id>` ставится `ro` (True только при `'on'`, при `INHERIT` будет False). Рост `volsize` ресинхронизирует размер LUN, уменьшение запрещено. Нельзя менять `name`, `type`, `sparse`, `volblocksize`, `casesensitivity`, `encryption*`. Не job. [ПОДТВЕРЖДЕНО] [mw04 dataset.py#L805-L960](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/pool_/dataset.py#L805-L960), [global_linux.py#L92-L103](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/iscsi_/global_linux.py#L92-L103)
- В 25.10 полная замена через `user_properties` может упасть, если в текущих свойствах окажутся переименованные внутренние ключи (§7.5); безопаснее всегда использовать `user_properties_update`. [ГИПОТЕЗА]
- `pool.dataset.promote(id)` (не job) переносит исходный и более ранние снапшоты на клон. В нашей схеме не нужен и вреден. `pool.dataset.rename` и `pool.snapshot.rename` (только 25.10) требуют `force=true` («No safety checks…»); у `pool.dataset.rename` есть ещё `recursive`. [ПОДТВЕРЖДЕНО] [mw10 dataset.py#L892-L929](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/dataset.py#L892-L903)

### 7.3 zvol: удаление

- `pool.dataset.delete(id, {recursive=false, force=false})`, не job. Если объекта нет — InstanceNotFound (`-32602`, ENOENT в `extra`). Своя предпроверка смотрит только дочерние датасеты (`ENOTEMPTY`). Снапшоты без `recursive` → `-32001` EFAULT «…volume has children / use '-r'…». Клоны при `recursive` → «…has dependent clones / use '-R'…», а `-R` публичный API не даёт. «dataset is busy» → EBUSY. [ПОДТВЕРЖДЕНО] [mw04 zfs_/dataset.py#L182-L222](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/zfs_/dataset.py#L182-L222)
- **До** `zfs destroy` метод обходит attachment delegates. iSCSI-делегат удаляет **включённые** DISK-экстенты `zvol/<name>` и их связки через `datastore.delete` (без проверки сессий), осиротевшие таргеты — через `iscsi.target.delete(id, force=True)`, затем делает reload. Выключенные экстенты остаются сиротами. Если потом упадёт сам destroy (например, есть снапшоты), iSCSI-объекты **уже удалены**. [ПОДТВЕРЖДЕНО] [fs_attachment_delegate.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/iscsi_/fs_attachment_delegate.py#L7-L31), [common/attachment](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/common/attachment/__init__.py#L94-L119)

### 7.4 Снапшоты и клоны

| Операция | Параметры и поведение | Статус |
|---|---|---|
| create | `dataset`, ровно одно из `name`/`naming_schema`, `recursive`, `exclude`, `vmware_sync`, `properties` (в т.ч. user props); `suspend_vms` только в 25.04. ID = `<ds>@<name>`. Не job. | [ПОДТВЕРЖДЕНО] [mw04 zfs_/snapshot.py#L134-L211](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/zfs_/snapshot.py#L134-L211) |
| query | Фильтры по id/name/dataset/pool; одиночный `["id","=",…]` уходит прямо в libzfs. `extra`: `holds`, `properties` (пусто или не задано — все свойства), `min_txg/max_txg`, `retention`. Запись: id, name, pool, dataset, snapshot_name, type, createtxg, properties (включая user props с source). | [ПОДТВЕРЖДЕНО] [mw10 zfs_/snapshot.py#L41-L136](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/zfs_/snapshot.py#L41-L136) |
| update меток | 25.04: `zfs.snapshot.update(id,{user_properties_update:[{key,value}|{key,remove:true}]})`. 25.10: `pool.snapshot.update(id,{user_properties_update:[{key,value}], user_properties_remove:[key]})`. | [ПОДТВЕРЖДЕНО] [v25_10_5/pool_snapshot.py#L171-L183](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/v25_10_5/pool_snapshot.py#L171-L183) |
| delete | `(id, {defer=false, recursive=false})`, не job, роль SNAPSHOT_DELETE (в обеих версиях). Если есть клоны, а `defer=false`, приходит `-32602` с attribute `options.defer` и списком клонов. При `defer=true` ZFS ставит `defer_destroy` и удалит снапшот сам после ухода последнего клона или hold; пока он помечен, он виден и от него можно клонировать. | [ПОДТВЕРЖДЕНО] [mw10 zfs_/snapshot.py#L231-L263](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/zfs_/snapshot.py#L231-L263), [zfs-destroy(8)](https://openzfs.github.io/openzfs-docs/man/master/8/zfs-destroy.8.html) |
| clone | `{snapshot, dataset_dst, dataset_properties={}}`, возвращает `true`, не job. 25.10: роли `DATASET_WRITE | SNAPSHOT_WRITE` (любая из двух). Клон может лежать где угодно, но в том же пуле; родитель должен существовать. Тип клона = тип источника (из zvol — zvol). | [ПОДТВЕРЖДЕНО] [mw04 snapshot_actions.py#L14-L46](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/zfs_/snapshot_actions.py#L14-L46), [pool.snapshot.clone](https://api.truenas.com/v25.10/api_methods_pool.snapshot.clone.html) |
| RO-клон | `dataset_properties {"readonly":"on"}` (строго в нижнем регистре, уходит в libzfs как есть; туда же можно положить метки). Это один вызов API, но **не одна транзакция ZFS** (см. §7.5), и экстент при этом не синхронизируется. Альтернатива — `pool.dataset.update(id,{readonly:"ON"})`: тогда middleware сам выставит `ro` у включённого экстента. | [ИСПРАВЛЕНО] [zfs_ioctl.c zfs_ioc_clone](https://github.com/truenas/zfs/blob/release/25.04.2/module/zfs/zfs_ioctl.c#L3613-L3645) |
| связь клон→снапшот | Свойство `origin` клона. В 25.04 `origin.value` отдаётся как есть; в 25.10 `value = rawvalue.upper()`, а `rawvalue`/`parsed` регистр сохраняют. Фильтровать по `[["origin.rawvalue","=","tank/lib@v42"]]`. | [ПОДТВЕРЖДЕНО] по коду [dataset_query_utils.py#L336-L463](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/dataset_query_utils.py#L336-L365); что у не-клона будет `''`, а не `'None'` — [ГИПОТЕЗА] |
| список клонов снапшота | `query` с `extra.properties:["clones"]` (или без ограничения) должен вернуть `properties.clones`: имена через запятую без пробелов, без клонов — `''` и source `NONE`. | [ГИПОТЕЗА] подтверждено кодом libzfs и py-libzfs, на стенде нет [zfs_prop.c/libzfs_dataset.c](https://github.com/truenas/zfs/blob/release/25.04.2/lib/libzfs/libzfs_dataset.c#L2805-L2808) |

### 7.5 User properties (метки)

- Правила OpenZFS: имя обязательно содержит `:`; символы `[a-z0-9:-._]`; длина ≤ 256; имя не начинается с `-`; значение ≤ 8192 байт; свойства **всегда наследуются**; ставятся на filesystem, volume и snapshot. [ПОДТВЕРЖДЕНО] [zfsprops(7)](https://openzfs.github.io/openzfs-docs/man/master/7/zfsprops.7.html)
- `pool.dataset.query` возвращает `user_properties` как `{key:{value, rawvalue, parsed, source,…}}`. В 25.04 `source` честный, внутренние свойства TrueNAS исключены. В 25.10 `source` захардкожен `"LOCAL"`, а внутренние свойства, судя по коду, попадают под переименованными ключами (`comments`, `managedby`, `quota_warning` и т.п.). Унаследованная метка в 25.10 будет видна как LOCAL — [ГИПОТЕЗА]. Фильтр: `[["user_properties.clubsrv:libver.value","=","42"]]`; точку в ключе экранировать `\\.`, двоеточие не нужно. [ИСПРАВЛЕНО] [dataset_query_utils.py#L511-L548, L761-L767](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/pool_/dataset_query_utils.py#L511-L548)
- Атомарность. Метки в `properties` при создании **снапшота** пишутся в той же txg. У **create zvol** и **clone** это один ioctl, но две sync-задачи: сначала `dmu_objset_create/clone`, потом отдельным вызовом `zfs_set_prop_nvlist` (в коде комментарий «It would be nice to do this atomically»). При ошибке установки свойств объект удаляется, но при падении ядра или питания между txg может остаться zvol или клон **без меток и без `readonly=on`**. [ИСПРАВЛЕНО] [zfs_ioctl.c zfs_ioc_create/zfs_ioc_clone](https://github.com/truenas/zfs/blob/release/25.04.2/module/zfs/zfs_ioctl.c#L3473-L3645), [dsl_dataset_snapshot_sync](https://github.com/truenas/zfs/blob/release/25.04.2/module/zfs/dsl_dataset.c#L1902-L1922)
- Поиск существования: `pool.dataset.query([["id","=",X]], {"extra":{"retrieve_children":false}})` или `get_instance` (InstanceNotFound). В 25.04 одиночный фильтр по id/name уходит в ZFS; **в 25.10 идёт обход дерева при любых фильтрах**. Внутренние датасеты (`.system`, `ix-applications`, `ix-apps`, `.ix-virt`, `boot-pool`) query всегда исключает, поэтому наши zvol под ними не размещать. [ПОДТВЕРЖДЕНО] [mw04 dataset.py#L165-L245](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/pool_/dataset.py#L165-L245)

### 7.6 «Уже существует» и «не найдено»

| Ситуация | Что приходит | Статус |
|---|---|---|
| zvol уже есть (25.x) | `-32001`, errno **EFAULT**, «Failed to create dataset: cannot create '<name>': dataset already exists» (проверка `/mnt/<name>` для zvol не срабатывает) | [ПОДТВЕРЖДЕНО] по коду [mw04 zfs_/dataset.py#L138-L151](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/zfs_/dataset.py#L138-L151) |
| снапшот уже есть (25.x) | `-32001`, errno **EEXIST** (17), «Failed to snapshot …: … already exists» | [ПОДТВЕРЖДЕНО] [mw10 zfs_/snapshot.py#L196-L201](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/zfs_/snapshot.py#L196-L201) |
| цель клона уже есть (25.x) | `-32001`, errno **EFAULT**, «Failed to clone snapshot: cannot create '<dst>': dataset already exists» | [ПОДТВЕРЖДЕНО] [mw10 snapshot_actions.py#L26-L37](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/zfs_/snapshot_actions.py#L26-L37) |
| нет исходного снапшота для клона | `-32001` EFAULT «Failed to clone snapshot: Snapshot <name> not found» | [ПОДТВЕРЖДЕНО] |
| клон в другой пул | `-32001` EFAULT (`data.error=14`) «…source and target pools differ» | [ПОДТВЕРЖДЕНО] |
| 26.0-RC.1 | `pool.snapshot.create` → `-32602` (EEXIST в `extra`); `pool.snapshot.clone` → `-32001` errno EINVAL «'<dst>' already exists»; `zfs.resource.snapshot.clone` → ValidationError EEXIST. Формы ошибок различаются даже внутри одной версии. | [ИСПРАВЛЕНО] [26.0-RC.1 pool_/snapshot.py](https://github.com/truenas/middleware/blob/release/26.0.0-RC.1/src/middlewared/middlewared/plugins/pool_/snapshot.py#L46-L55) |
| объект не найден (delete/get_instance) | `-32602`, `data.error=22`, ENOENT (2) в `extra[i][2]` | [ПОДТВЕРЖДЕНО] |

---

## 8. iSCSI (SCST)

### 8.1 Объекты и методы
Имена методов одинаковы в 25.04 и 25.10: `iscsi.global.{config,update,sessions,client_count,alua_enabled,iser_enabled}`, `iscsi.{portal,initiator,auth,target,extent,targetextent}.{query,get_instance,create,update,delete}`, `portal.listen_ip_choices`, `target.validate_name`, `extent.disk_choices`. `iscsi.scst.*` приватный. [ПОДТВЕРЖДЕНО] [api index 25.04](https://api.truenas.com/v25.04/index.html), [api index 25.10](https://api.truenas.com/v25.10/index.html)

Модели одинаковы внутри серий. Между 25.04 и 25.10 по протоколу два отличия: (1) у extent появился `product_id` (1..16 символов или null; query в 25.10 вернёт `'iSCSI Disk'`, а не null); **на 25.04 это поле даст ошибку валидации** (`extra='forbid'`); (2) `iscsi.global.sessions` в 25.04 возвращает `list|item|int`, в 25.10 — `list`. [ПОДТВЕРЖДЕНО] [v25_10_0/iscsi_extent.py#L60](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/v25_10_0/iscsi_extent.py#L60)

В `stable/26` (не релиз) появились новые поля: у extent `dataset` и `relative_path` (read-only), у global — `direct_config` и `mode`; `sessions` переведён на `filterable_api_method`; `service.start/stop/restart/reload` помечены `removed_in='v26'`. [ИСПРАВЛЕНО] [stable/26 iscsi_global.py](https://github.com/truenas/middleware/blob/stable/26/src/middlewared/middlewared/api/v26_0_0/iscsi_global.py)

### 8.2 extent

- `iscsi.extent.create`: `name` (1..64, уникально, без `"`), `type` DISK|FILE, `disk` для DISK обязателен и начинается с `zvol/` (`/dev/<disk>` должен существовать и не быть в boot-pool), `serial` (≤ 20, уникален; по умолчанию 15 hex), `blocksize` 512/1024/2048/4096 (по умолчанию 512), `pblocksize`, `avail_threshold`, `comment`, `insecure_tpc`, `xen`, `rpm` (по умолчанию SSD), `ro` (по умолчанию false), `enabled` (по умолчанию true). `naa`, `vendor`, `locked`, `id` задать нельзя; `naa` = `0x6589cfc000000` + 19 hex, уникален. [ПОДТВЕРЖДЕНО] [mw04 extents.py#L277-L377](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/extents.py#L277-L377)
- Побочные эффекты create: **до** вставки в БД ставит на zvol `volthreading=off` и `readonly=on|off` по `ro`. **Reload SCST не делает** — extent попадёт в SCST при следующем reload (например, от `targetextent.create`). При падении между шагами остаётся zvol с изменёнными свойствами без записи extent. [ПОДТВЕРЖДЕНО] [mw10 extents.py#L108-L150](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/iscsi_/extents.py#L108-L150)
- Новые проверки в 25.10: один disk/path нельзя дать второму extent; zvol, занятый NVMe-oF namespace, запрещён; имя должно быть уникальным после замены `.`→`_` и `/`→`-`; в пути FILE нельзя пробелы. В 25.04 на один zvol можно создать два extent. [ПОДТВЕРЖДЕНО] [mw10 extents.py#L396-L530](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/iscsi_/extents.py#L469-L530)
- Экспорт снапшота напрямую (`zvol/pool/vol@snap`, только при `ro=true` и `snapdev=visible`) проходит валидацию, но упадёт на `zfs.dataset.update` по имени со `@`. Нужен клон. [ГИПОТЕЗА] подтверждено кодом, без запуска. [mw04 extents.py#L357-L358](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/extents.py#L357-L358)
- `extent.create` сам проверяет `os.path.exists('/dev/zvol/<clone>')` (`clean_type_and_path`) и без узла отказывает ValidationError «Device … does not exist»; ожидания в middleware нет, поэтому сервер повторяет вызов с паузой (`Library:ExtentAttempts`). [ПОДТВЕРЖДЕНО] [extents.py#L478-L482](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/iscsi_/extents.py#L478-L482). Что узел свежего клона появляется не сразу — [ГИПОТЕЗА] (udev асинхронен, [zvol_wait(1)](https://openzfs.github.io/openzfs-docs/man/master/1/zvol_wait.1.html)). Причиной сбоя публикации на стенде 2026-10-02 узел **не был**: `extent.create` прошёл с первого раза, а устройство в SCST открылось с этим путём (§8.4).
- `iscsi.extent.update`: модель = create с необязательными полями, `disk` можно сменить. `naa` сохраняется, `serial` сохраняется, если не передан новый. `readonly` ставится только на новом zvol, `volthreading` на нём не ставится, старый zvol не откатывается. Reload вызывается **дважды** («scstadmin can have issues when modifying an existing extent, re-run»). **Сессии не проверяются.** [ПОДТВЕРЖДЕНО] [mw04 extents.py#L139-L184](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/extents.py#L139-L184)
- `iscsi.extent.delete(id, remove=false, force=false)`: если есть сессии на связанных таргетах и `force=false` → CallError «Associated target(s) … in use». Иначе удаляет связки, возвращает zvol `volthreading=on`, удаляет запись, делает reload. `readonly` на zvol не снимается. [ПОДТВЕРЖДЕНО] [mw10 extents.py#L287-L335](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/plugins/iscsi_/extents.py#L287-L335)

### 8.3 target, targetextent, portal, initiator, auth

- `iscsi.target.create`: `name` `^[-a-z0-9\.:]+$`, ≤ 120, уникально; IQN в SCST всегда `<global.basename>:<name>`. `alias` (без `"`, не `target`, уникален, `''` → NULL). `mode` ISCSI/FC/BOTH (любой mode кроме ISCSI требует FIBRECHANNEL). `groups[]{portal, initiator, authmethod NONE|CHAP|CHAP_MUTUAL, auth}`, `auth_networks`, `iscsi_parameters.QueuedCommands` 32|128|null. `rel_tgt_id` назначается автоматически. [ПОДТВЕРЖДЕНО] [mw04 targets.py#L103-L307](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/targets.py#L103-L307)
- `groups[].auth` в документации описан как «ID of the authentication credential», но код везде трактует его как **tag** записи `iscsi.auth`. [ПОДТВЕРЖДЕНО] [mw04 targets.py#L257-L271](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/targets.py#L257-L271)
- Доступ: `groups[].initiator=null` или пустой список initiators → в конфиг пишется `INITIATOR *#<ip портала>`, то есть доступ получает **любой**. FK группы на `iscsi.initiator` объявлен с `ondelete='SET NULL'`, поэтому удаление группы инициаторов молча открывает таргет всем. Таргет без groups не публикует LUN никому. [ПОДТВЕРЖДЕНО] [mw04 targets.py#L40-L58](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/targets.py#L40-L58), [scst.conf.mako#L488](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/etc_files/scst.conf.mako#L488)
- `iscsi.target.delete(id, force=false, delete_extents=false)`: если есть сессии и `force=false` → «Target X is in use.». Иначе удаляет каждую связку вызовом `iscsi.targetextent.delete(id, force)` — **у каждого свой reload** (таргет на это время в scst.conf без LUN, `enabled 0`), (опционально) extents (zvol остаётся), группы, запись; затем `scstadmin -force -noprompt -rem_target …` (ошибка только логируется) и ещё один reload. Итого на таргет с одним LUN — два reload-а. Операция многошаговая, не транзакционная. [ПОДТВЕРЖДЕНО] кодом TS-25.10.7 (2026-10-02): [targets.py do_delete](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/iscsi_/targets.py), [target_to_extent.py do_delete](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/iscsi_/target_to_extent.py); поддельный TrueNAS в тестах повторяет эту последовательность
- При `force=true` scstadmin выключает таргет и закрывает каждую сессию через `force_close`, ожидая до 300 с (иначе `SCST_C_TGT_BUSY`, которое middleware только логирует). Клиенты теряют диск. [ПОДТВЕРЖДЕНО] механизм по [SCST.pm](https://github.com/truenas/scst/blob/release/25.04.2.6/scstadmin/scstadmin.sysfs/scst-1.0.0/lib/SCST/SCST.pm#L1327-L1364), [iscsi-scst target.c](https://github.com/truenas/scst/blob/release/25.04.2.6/iscsi-scst/kernel/target.c#L244-L323); как это видит Windows — [ГИПОТЕЗА]
- `iscsi.targetextent.create {target, extent, lunid|null}`: `lunid` null → первый свободный с 0; `0 ≤ lunid ≤ 16382`; пара (target, lunid) уникальна; extent можно привязать только к одному таргету; после вставки — reload. [ПОДТВЕРЖДЕНО] [mw04 target_to_extent.py#L198-L253](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/target_to_extent.py#L198-L253)
- `iscsi.targetextent.delete(id, force=false)`: при сессиях без force → «Associated target X is in use.». С force LUN удаляется, клиенты получают UA REPORTED LUNS DATA HAS CHANGED, сама сессия остаётся. [ПОДТВЕРЖДЕНО] [scst_sysfs.c](https://github.com/truenas/scst/blob/release/25.04.2.6/scst/src/scst_sysfs.c#L1307-L1427)
- portal/initiator/auth без изменений между 25.04 и 25.10. `portal.create {listen:[{ip}]}`: IP должен быть из `listen_ip_choices` (включая 0.0.0.0/::), `tag` = число порталов + 1; `portal.delete` молча удаляет ссылающиеся группы. `initiator.create {initiators:[…]}`: содержимое не валидируется, пустой список = ALL. `auth`: `secret` 12..16 символов, `peersecret ≠ secret`; удалить последнюю запись используемого tag нельзя; уникальность tag не проверяется. [ПОДТВЕРЖДЕНО] [portal.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/portal.py#L110-L250), [auth.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/auth.py#L43-L200)
- `iscsi.global.update`: `basename` (его смена меняет IQN **всех** таргетов), `isns_servers`, `listen_port` 1025..65535 (по умолчанию 3260), `pool_avail_threshold`, `alua` (только Enterprise HA), `iser` (смена перезапускает сервис). [ПОДТВЕРЖДЕНО] [iscsi_global.py](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/iscsi_global.py#L107-L222)

### 8.4 Как конфигурация попадает в SCST

- `create/update/delete` у portal/auth/initiator/target/targetextent и `extent.update/delete` сами вызывают reload `iscsitarget`, но только если сервис RUNNING. **`iscsi.extent.create` reload не делает.** Если reload вернул false, бросается CallError ESERVICESTARTFAILURE, хотя запись в БД уже сделана. В 25.10 `_service_change` идёт через `service.control(...).wait()`. [ИСПРАВЛЕНО] [service_mixin.py](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/service/service_mixin.py#L18-L28)
- Reload = перегенерация `/etc/scst.conf` + `scstadmin -noprompt -force -config`. `service.reload` **игнорирует код возврата** scstadmin и отдаёт true, если сервис running; scstadmin при ошибке выходит, и конфиг может примениться частично. С `-force` удаляется всё, чего нет в файле, и пересоздаются только расходящиеся устройства и LUN; остальные таргеты и их сессии не затрагиваются (по коду scstadmin; для LUN read-only устройств на 25.10.7 возможно исключение — гипотеза о `[key]` ниже, не проверено). В 25.10 у reload таймаут 120 с. [ПОДТВЕРЖДЕНО] [service.py 25.04](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/service.py#L347-L381), [scstadmin#L2205-L2345](https://github.com/truenas/scst/blob/release/25.04.2.6/scstadmin/scstadmin.sysfs/scstadmin#L2205-L2345)
- Генерация scst.conf: extent с `enabled=false` отфильтровывается запросом; на заблокированном датасете или без `/dev/zvol` — пропускается; таргет без LUN пишется с `enabled 0`; DISK → `vdisk_blockio` (filename, blocksize, read_only, usn=serial, naa_id, prod_id, rotational, t10_*, threads_num 32); имя устройства = имя extent с заменами `.`→`_`, `/`→`-`. `insecure_tpc` и `avail_threshold` в SCST не попадают. [ПОДТВЕРЖДЕНО] [scst.conf.mako 25.04](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/etc_files/scst.conf.mako#L229-L276)
- **Ошибка 25.10.7: read-only устройство в copy_manager_tgt срывает reload.** SCST сам добавляет каждое новое
  устройство vdisk_blockio в `copy_manager_tgt` (`auto_cm_assignment`, флаг `SCST_ADD_LUN_CM` пропускает проверку
  read-only). Следующий reload пишет в секцию `TARGET_DRIVER copy_manager` все живые LUN copy_manager включённых
  экстентов строками `LUN n <dev>` без атрибутов, а у read-only устройства атрибут LUN `read_only` помечен `[key]`.
  `scstadmin -force` в первом проходе (удаления) считает такой LUN «configured differently» и переназначает: remove
  проходит, add — `EINVAL` «Copy Manager does not support read only devices», `condExit` → exit 1. Дальше не
  применяется ничего (устройства, LUN таргетов, `enabled`), а middleware отвечает успехом. Каждый такой срыв снимает
  одно устройство из copy_manager; следующий reload без read-only устройств там проходит и доводит всё, включая
  `enabled 1` у уже существующего таргета. Старт службы (`clear_config` + `-config` без `-force`) прохода удалений не
  делает — поэтому стоп/старт в интерфейсе чинит. В 26 исправлено: в секцию copy_manager пишутся только RW-экстенты
  (коммит [6ca0a235](https://github.com/truenas/middleware/commit/6ca0a235d6b3cbe7a2dd8019b517b78864e1de3a), NAS-131279).
  [ПОДТВЕРЖДЕНО] кодом: [scst.conf.mako#L18-L45](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/etc_files/scst.conf.mako#L18-L45),
  [scst_main.c#L1412-L1424](https://github.com/truenas/scst/blob/TS-25.10.7/scst/src/scst_main.c#L1412-L1424),
  [scst_copy_mgr.c#L2834-L2856](https://github.com/truenas/scst/blob/TS-25.10.7/scst/src/scst_copy_mgr.c#L2834-L2856),
  [scstadmin#L2347-L2399](https://github.com/truenas/scst/blob/TS-25.10.7/scstadmin/scstadmin.sysfs/scstadmin#L2347-L2399),
  [scstadmin#L2205-L2226](https://github.com/truenas/scst/blob/TS-25.10.7/scstadmin/scstadmin.sysfs/scstadmin#L2205-L2226),
  [init.d/scst#L250-L252](https://github.com/truenas/scst/blob/TS-25.10.7/scstadmin/init.d/scst#L250-L252). Что на стенде сработало именно это — [ГИПОТЕЗА]
  до проверки журнала ядра (ниже).
- **Стенд 2026-10-02** (25.10.7, API v25.10.5): публикация `2026-10-02` в старом порядке extent → target → LUN; все
  вызовы успешны, `*.query` верны, служба RUNNING. В SCST (sysfs): устройство `lib-2026-10-02` открыто (`read_only=1`),
  таргет `games-2026-10-02` с `enabled=0`, в `security_group` инициатор `*#*` и **ни одного LUN**; SendTargets с другого
  хоста пуст, Windows: «The target name is not found or is marked as hidden from login». Стоп/старт службы iSCSI в
  интерфейсе — сразу видно, ПК подключил том только для чтения. Мастер-том (RW, CHAP, давно существующий zvol)
  подключился с первого раза. Картина совпадает с ошибкой выше: reload от `target.create` открыл новое RO-устройство
  (SCST добавил его в copy_manager), reload от `targetextent.create` на нём сорвался. Версия «узел /dev/zvol не успел»
  не сходится: `extent.create` проверяет узел, а устройство в SCST открыто с этим путём.
- **Что делает сервер** (обход до TrueNAS 26): (1) порядок публикации **target → extent → LUN**: новое RO-устройство
  впервые открывает тот же reload, который даёт LUN и включает таргет; reload от `target.create` «съедает» срыв на
  устройстве прошлой версии. (2) Шаг `verify` перед переключением версии: iSCSI SendTargets с сервера клуба к
  `Library:DiscoveryAddress` (по умолчанию `PortalAddress`) — iscsi-scstd показывает только включённый таргет, где у
  инициатора есть LUN ([target.c#L272-L275](https://github.com/truenas/scst/blob/TS-25.10.7/iscsi-scst/usr/target.c#L272-L275)). Не виден — reload через
  `iscsi.target.update(id, {})` (без изменений: перегенерация scst.conf и `scstadmin -force -config`; группы не
  пересохраняются, [targets.py#L320-L357](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/iscsi_/targets.py#L320-L357)), пауза, снова проверка; не больше
  «число RO-экстентов + 2» раз (портал, ответивший хоть раз, потом замолчал — лестница продолжается, молчание
  считается «не виден»). Не помогло — операция завершается провалом на этой же попытке (`Library:MaxAttempts` не
  применяется: повтор через такт воркера прогнал бы ту же лестницу reload-ов), с подсказкой перезапустить службу iSCSI
  (если таргет открыт только по списку инициаторов — имя проверки пускает конкретная строка, а не `*` и не строка с
  ведущим `!`, — сначала проверить в списке имя проверки); версия не становится текущей. Проверка идёт от имени `Library:ProbeInitiatorIqn` (по умолчанию `iqn.2026-10.local.clubsrv:probe`):
  iscsi-scstd показывает таргет, только если у инициатора в нём есть LUN (`scst_initiator_has_luns`), а LUN — в
  security_group, куда TrueNAS пишет строки всех групп таргета как `INITIATOR строка#адрес-портала` (группа без списка
  — `*`, `per_portal_acl 1`); поэтому в суженной группе версий это имя должно быть. Строки SCST сравнивает с
  `IQN#адрес` функцией `wildcmp` ([scst_targ.c](https://github.com/truenas/scst/blob/TS-25.10.7/scst/src/scst_targ.c),
  `__scst_find_acg`): `*`, `?`, первый `!` обращает сравнение остатка, регистр не различается; хватает одной
  подходящей строки. Сервер повторяет это для IQN (`ScstWildcard`; суффикс `#адрес` не сравнивается — у пришедшего на
  портал он совпадает). Если ни одна строка имя проверки не пускает (например, `iqn.1991-05.com.microsoft:*`),
  публикация сразу падает с ошибкой о группе — без reload-ов и без совета перезапускать службу (не помог бы); сверка в
  этом случае пишет `probeDenied`, а не `targetNotDiscovered`. Портал проверить нельзя (ни разу не
  ответил: нет связи, отказ в соединении, discovery с CHAP, неверный адрес) — версия становится текущей **без проверки**: после
  reload-ов вслепую («RO-экстентов + 1» — худший случай после перезапуска TrueNAS, когда в copy_manager все
  RO-устройства; обычно 3–4) и с предупреждением сверки `discoveryUnavailable` (сверка раз в 5 минут). Отказ в
  соединении от доступного хоста сознательно не считается провалом: его не отличить от неверного `DiscoveryAddress`
  или фильтра между сервером и TrueNAS, а при остановленной службе iSCSI ПК не видят ни одной версии, и запуск службы
  применяет конфигурацию целиком (без `-force`) — провал публикации ничего бы не защитил. Шаг выполняется на каждом
  проходе: после рестарта сервера ensure-шаги ничего не создают и reload не вызывают (если сервер остановился уже после
  переключения версии, операция просто завершается: проверять работающую версию заново незачем). (3) Открытие мастер-тома — та
  же проверка от имени IQN ПК суперклиента, но без провала: не виден (или группа не включает IQN ПК) — том открыт с
  оговоркой (`lastError` у открытого тома в панели). (4) Сверка раз в 5 минут: `targetNotDiscovered`, если таргет
  текущей или откатной версии не виден (подсказка — перезапуск службы), а при группе-списке — `targetNotDiscoveredListed`
  (подсказка — сначала проверить в списке имя проверки). Что каждый успешный `-force` reload не задевает сессии других таргетов — замысел scstadmin, но не
  проверено (гипотеза о `[key]` ниже); поэтому лишних reload-ов сервер не делает: при видимом таргете — ни одного.
  `iscsi.global.update({})` для reload не годится: он перезаписывает `isns_servers` пустым списком
  ([iscsi_global.py#L107-L193](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/iscsi_/iscsi_global.py#L107-L193)). RESTART службы автоматически не
  делается никогда (рвёт сессии всех ПК). Не покрыто: разборка старой версии. Если reload от `extent.delete` сорвётся
  (после перезапуска TrueNAS в copy_manager два и больше RO-устройств), устройство останется открытым, удаление клона
  будет отказывать как «busy» (`retireBlocked`) до следующего удачного reload — например, следующей публикации
  [ГИПОТЕЗА].
- **Проверить на стенде** (TrueNAS → System → Shell): `sudo journalctl -k | grep -i "copy manager does not support read only"`
  около 02:12:16 2 октября; после стоп/старта `ls /sys/kernel/scst_tgt/targets/copy_manager/copy_manager_tgt/luns/`
  снова содержит `lib-2026-10-02`, а `cat …/luns/<n>/read_only` — `1` и `[key]`; `sudo scstadmin -noprompt -force
  -config /etc/scst.conf; echo $?` — «configured differently … Re-assigning», FATAL, `1`; второй запуск — `0`.
  Отдельно — побочный эффект [ГИПОТЕЗА]: тот же `[key]` у LUN read-only устройств в группах iSCSI-таргетов может
  заставлять каждый успешный `-force` reload переназначать LUN старых версий (UA REPORTED LUNS DATA HAS CHANGED у ПК):
  с ПК на `games-…` вызвать `iscsi.target.update(id, {})` и смотреть журнал ядра и журнал System Windows (iScsiPrt).
- Наблюдаемость: публичного чтения фактической таблицы устройств и LUN SCST нет, все `query` возвращают конфиг из БД middleware. О рантайме видно только `iscsi.global.sessions` (читает sysfs: initiator, initiator_addr, target, target_alias, digests…) и состояние сервиса в `service.query`. `client_count` — число уникальных `initiator_addr`. Для таргетов с именами на `iqn.`/`naa.`/`eui.` проверка сессий промахивается. [ПОДТВЕРЖДЕНО] [global_linux.py#L17-L89](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/global_linux.py#L17-L89), [targets.py#L460-L478](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/targets.py#L460-L478)

### 8.5 Поведение подключённых инициаторов

| Действие на живом таргете | Что видит клиент | Статус |
|---|---|---|
| `extent.update` с новым `disk`, `ro` или `name` | `filename` и `read_only` у vdisk_blockio — create-атрибуты (в sysfs `read_only` 0444). scstadmin `-force` закрывает устройство и открывает заново («Closing and re-opening with new attributes»): все LUN устройства удаляются (UA REPORTED LUNS DATA HAS CHANGED), потом возвращаются **с прежними NAA и serial, но другим содержимым**. | [ПОДТВЕРЖДЕНО] [scst_vdisk.c#L9625-L9645](https://github.com/truenas/scst/blob/release/25.04.2.6/scst/src/dev_handlers/scst_vdisk.c#L9625-L9645), [scstadmin#L2292-L2312](https://github.com/truenas/scst/blob/release/25.04.2.6/scstadmin/scstadmin.sysfs/scstadmin#L2292-L2312) |
| `pool.dataset.update(readonly)` на zvol под живым extent | resync идёт через `extent.update` (двойной reload) → то же пересоздание устройства | [ПОДТВЕРЖДЕНО] |
| `targetextent.update {extent: new}` | Проверки сессий нет, один reload. scstadmin не использует атомарный `replace`: LUN сначала снимается, потом добавляется (UA REPORTED LUNS CHANGED). Атомарный `replace` (UA INQUIRY DATA HAS CHANGED) в SCST есть, но middleware вызывает его только в ALUA-коде. | [ПОДТВЕРЖДЕНО] [target_to_extent.py#L103-L124](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/target_to_extent.py#L103-L124) |
| Создание **нового** target/extent | Сессии других таргетов не затрагиваются (`-force` трогает только расходящиеся объекты) | [ПОДТВЕРЖДЕНО] |
| `target/targetextent/extent.delete` без force | CallError «in use», пока есть сессии: это предохранитель | [ПОДТВЕРЖДЕНО] |
| `pool.dataset.delete` zvol | Каскад удаляет extent и связки без проверки сессий, осиротевший таргет — с force | [ПОДТВЕРЖДЕНО] |

Документация TrueNAS (UI и API) сценарий «переключить клиентов на новую версию тома» не описывает; всё выше выведено из кода. [ПОДТВЕРЖДЕНО] [iscsisharesscreens 25.04](https://www.truenas.com/docs/scale/25.04/scaleuireference/shares/iscsisharesscreens/)

### 8.6 Read-only и Windows

- При `ro=true` в scst.conf пишется `read_only 1`: SCST ставит бит WP (0x80) в MODE SENSE и отклоняет запись с sense DATA PROTECT. [ПОДТВЕРЖДЕНО] [scst_vdisk.c#L4329](https://github.com/truenas/scst/blob/release/25.04.2.6/scst/src/dev_handlers/scst_vdisk.c#L4329), [scst_targ.c](https://github.com/truenas/scst/blob/release/25.04.2.6/scst/src/scst_targ.c#L2720-L2730)
- В Windows-driver-samples драйвер диска на `IOCTL_DISK_IS_WRITABLE` смотрит `MODE_DSP_WRITE_PROTECT` и возвращает `STATUS_MEDIA_WRITE_PROTECTED`, так что RO-extent будет виден как защищённый от записи. [ПОДТВЕРЖДЕНО] по sample [disk.c#L5016-L5205](https://github.com/microsoft/Windows-driver-samples/blob/main/storage/class/disk/src/disk.c#L5016-L5205); что штатный `disk.sys` ведёт себя так же — [ГИПОТЕЗА]
- NTFS на RO-LUN смонтируется только для чтения; «грязный» журнал на RO-носителе не проиграется, и том может не смонтироваться; игры, пишущие в каталог установки, будут падать. [ГИПОТЕЗА] источников нет
- SAN policy Windows: `onlineAll` — все новые диски online и read/write; `offlineShared` — iSCSI считается shared bus, диск остаётся offline; `offlineAll` — offline всё, кроме загрузочного. [ПОДТВЕРЖДЕНО] [san](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/san). Каждая версия (новый extent) получит новые NAA и serial, поэтому для Windows это новый диск, к которому применяется SAN policy. [ГИПОТЕЗА] согласуется с кодом (`naa` не задаётся, `serial` уникален)
- Если extent R/W, а zvol R/O, по комментарию в коде «Windows clients seem to not handle this very well». UI-док про LUN RPM: «Do not change this setting when using Windows as the initiator». [ПОДТВЕРЖДЕНО] [dataset.py#L945-L956](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/pool_/dataset.py#L945-L956)

### 8.7 Идемпотентность iSCSI
Естественные ключи: `extent.name`, `extent.serial`, `target.name`, пары (target, extent) и (target, lunid). Повторный create с тем же ключом падает с ValidationError («must be unique», «already exists»). Методы многошаговые и не транзакционные: `extent.create` меняет ZFS до вставки в БД; `target.create` пишет таргет, потом groups (при исключении откатывает запись, при падении процесса — нет); `target.delete` — серия удалений плюс `scstadmin -rem_target`. [ПОДТВЕРЖДЕНО] [targets.py#L109-L147](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/iscsi_/targets.py#L109-L147)

### 8.8 Управление сервисом iscsitarget

| | 25.04 | 25.10 | 26 (stable/26) |
|---|---|---|---|
| Запуск | `service.start('iscsitarget', {ha_propagate, silent})` — синхронно, bool, не job; при `silent=true` сбой даёт false. На api.truenas.com не описан (404), но доступен. | `service.control("START","iscsitarget",{ha_propagate, silent, timeout=120})` — **job** (lock `service_<name>`), результат bool. `service.start/stop/restart/reload` помечены `removed_in='v26.04'`. | `service.start/stop/restart/reload` с `removed_in='v26'` |
| Автозапуск | `service.update('iscsitarget', {enable:true})` | то же (`id_or_name: int|str`) | — |
| Через `/api/v25.04.x` на 25.10 | — | `-32001` ENOMETHOD «Method 'start' not found in 'service'» (не `-32601`) | — |

[ПОДТВЕРЖДЕНО] [service.py 25.04](https://github.com/truenas/middleware/blob/release/25.04.2.6/src/middlewared/middlewared/plugins/service.py#L112-L209), [service.control](https://api.truenas.com/v25.10/api_methods_service.control.html); строка про `/api/v25.04.x` — [ИСПРАВЛЕНО] [legacy_api_method.py#L64-L75](https://github.com/truenas/middleware/blob/release/25.10.7/src/middlewared/middlewared/api/base/server/legacy_api_method.py#L64-L75)

---

## 9. Рекомендации для нашего адаптера

### 9.1 Подключение и версия API
1. Только `wss://`, TLS с проверкой цепочки по внутреннему CA клуба: импортировать в TrueNAS сертификат, подписанный этим CA, а не пинить `truenas_default` (он перевыпускается с новым ключом). Минимум TLS 1.2. Никаких HTTP-прокси между нами и TrueNAS: ключ будет отозван.
2. **Закрепление версии.** При старте: `GET /api/versions` → выбрать максимальную версию из нашего белого списка **для текущей мажорной линии** TrueNAS (25.04 → `v25.04.2`; 25.10 → `v25.10.N`, проверенная на стенде). `/api/current` в продакшене не использовать. Если подходящей версии нет, адаптер переходит в read-only и поднимает алерт. В БД хранить фактически использованную версию API и `system.version`.
3. **Адаптер по линиям.** Интерфейс `IStorageGateway` и реализации `TrueNas2504` / `TrueNas2510` (позже `TrueNas26`). Различия: снапшоты (`zfs.snapshot.*` и `pool.snapshot.*`, форма `update`), `service.start` и `service.control` (job), `product_id`, форма `iscsi.global.sessions`, тексты валидации. Выбор реализации — по линии из `system.version` + подтверждение через `core.get_methods` (наличие методов, флаг `job`, роли).
4. **Minor-обновление TrueNAS.** Внутри линии модели iSCSI и `pool_dataset` стабильны, а при закреплённой версии сервер держит старые модели (например, `LongString` у меток снапшота появился только в `v25_10_1`). Порядок: прогнать контрактные тесты на ВМ с новым минором → добавить версию в белый список → обновить TrueNAS. **Major-обновление** (25.04 → 25.10) требует переключения на другую реализацию адаптера: закрепление `/api/v25.04.x` от ENOMETHOD не спасает.
5. Логин: `auth.login_ex` + `API_KEY_PLAIN`, проверять `response_type == "SUCCESS"`; `login_with_api_key` не использовать. Не включать STIG на этом TrueNAS.
6. Минимальные права: сервисный пользователь без пароля и web_shell → локальная группа → privilege с `DATASET_WRITE`, `DATASET_DELETE`, `SNAPSHOT_WRITE`, `SNAPSHOT_DELETE`, `SHARING_ISCSI_EXTENT_WRITE`, `SHARING_ISCSI_TARGET_WRITE`, `SHARING_ISCSI_TARGETEXTENT_WRITE` (+ portal/initiator/auth при их управлении), `SYSTEM_GENERAL_READ` на 25.10, `READONLY_ADMIN` для `system.info`. **На 25.04 clone по коду требует FULL_ADMIN** — проверить на стенде (см. открытые вопросы). Стенд 2026-10-02 (25.10.7): `READONLY_ADMIN`, `ACCOUNT_READ`, `DATASET_WRITE`, `DATASET_DELETE`, `SNAPSHOT_WRITE`, `SNAPSHOT_DELETE`, `SHARING_ISCSI_WRITE` — этого хватает и на повторный reload через `iscsi.target.update` (§8.4); `SERVICE_WRITE` не нужен (§2.4).
7. Клиент: одно долгоживущее соединение, мультиплексирование по строковому GUID (не 0 и не пусто), не больше 10 параллельных вызовов; ошибки маппить по `code` + `data.error/errname`, «не найдено» — по `extra[i][2]==2`; обрабатывать ENOMETHOD как «метод недоступен в этой версии API»; EJSON-конвертеры.
8. Keepalive: `core.ping` каждые 20–30 с после логина + `KeepAliveInterval`/`KeepAliveTimeout` (.NET 10). Переподключение с экспоненциальной задержкой и джиттером (≤ 20 логинов в минуту с IP), после него — повторный логин и переподписка.

### 9.2 Идемпотентность и переживание падений
1. Каждый шаг — **ensure**: перед мутацией `query` по точному ключу (dataset id, `ds@snap`, `extent.name`, `target.name`, пара target+extent), после **любой** ошибки — повторный `query`, решение принимается по факту. Разбор текстов ошибок не использовать (§7.6). ENOENT при delete считать успехом.
2. Детерминированные имена от id операции и версии библиотеки, например `tank/club/lib@v42`, `tank/club/published/lib-v42`, extent `lib-v42` (без `.` и `/`, ≤ 64), target `games-v42` (не начинать с `iqn.`/`naa.`/`eui.`).
3. Метки `clubsrv:*` (нижний регистр, без точек): `clubsrv:managed=1`, `clubsrv:libver`, `clubsrv:role`, `clubsrv:opid`. Ставить при создании (`user_properties`/`properties`/`dataset_properties`) и на **каждый** объект; значения короче 1024. Reconcile ищет объекты **по имени**, а не только по меткам, и умеет доклеить метки и `readonly=on`: после сбоя NAS у create и clone они могут отсутствовать. `source` в 25.10 не доверять, фильтровать ключи по префиксу `clubsrv:`.
4. RO-клон создавать одним вызовом `clone` с `dataset_properties {"readonly":"on", "clubsrv:…"}` под отдельным родителем-filesystem в том же пуле (не под `ix-apps`/`.system`, без `readonly=on` у родителя). Перед публикацией перечитать `readonly.rawvalue` и метки.
5. Перед `extent.create` ждать появления zvol с retry (ValidationError «Device … does not exist» считать временной ошибкой). Extent создавать сразу с `ro=true`.
6. Все операции с хранилищем выполнять последовательно, в одном воркере: в 25.04 у reload нет job-lock, и каждый CRUD-вызов запускает полный `scstadmin -config`.
7. Jobs (сейчас только `service.control` на 25.10): оставаться в legacy-режиме, подписываться на `core.get_jobs` **до** вызова, ждать терминального state с резервным опросом. Для crash-recovery сверять найденную job по method/arguments/time_started, а не только по id: id переиспользуются после рестарта middlewared.

### 9.3 Переключение версии тома (сценарий)
1. `pool.snapshot.create` (или `zfs.snapshot.create` на 25.04) мастер-zvol с метками.
2. `clone` → RO-клон с метками.
3. `iscsi.target.create` (новое имя с версией, groups с порталом и **явной** группой инициаторов, `auth` = tag) → `iscsi.extent.create` (`disk=zvol/<clone>`, `ro=true`, детерминированное имя) → `iscsi.targetextent.create` (`lunid=0`). Таргет раньше экстента — из-за ошибки copy_manager в 25.10.7 (§8.4). Сессии текущей версии не затрагиваются.
4. Проверка до переключения: iSCSI SendTargets с сервера клуба от имени `Library:ProbeInitiatorIqn` (в суженной группе инициаторов оно должно быть); таргет не виден — `iscsi.target.update(id, {})` (reload) и снова проверка, ограниченное число раз; не помогло — операция падает, оператор перезапускает службу iSCSI (§8.4). Портал проверить нельзя — переключение без проверки, после reload-ов вслепую, с предупреждением сверки. `*.query` и `iscsi.global.sessions` рантайм SCST не показывают.
5. Агент клуба входит в новый IQN, переключает пути, выходит из старого.
6. Разборка старой версии, когда она перестаёт быть откатной: `iscsi.target.delete(id, force=false)`, CallError «in use» считать нормальным ответом «ещё рано» → `iscsi.extent.delete(force=false)` → `pool.dataset.delete` клона (только когда `pool.dataset.attachments` пуст) → `snapshot.delete` (или сразу с `defer=true`).
7. **Никогда** не переключать версию через `extent.update` или `targetextent.update` и не делать `service.control RESTART` автоматически. Не удалять `iscsi.initiator`, на который ссылается таргет. Не менять `iscsi.global.basename` после ввода в эксплуатацию.
8. Сервис: однократно `service.update('iscsitarget',{enable:true})`; запуск на 25.04 — `service.start(…, {silent:false})`, на 25.10 — `service.control("START", …)` как job.

---

## 10. Открытые вопросы

1. Можно ли на 25.04 вызвать `zfs.snapshot.clone` ключом без FULL_ADMIN? По коду нельзя. Если стенд это подтвердит, на 25.04 придётся либо давать сервисному аккаунту FULL_ADMIN, либо ставить минимальной версией 25.10.
2. Подтвердить на стенде 25.10, что `pool.dataset.create`, `pool.snapshot.*` и `service.*` через `/api/v25.04.2` возвращают `-32001` ENOMETHOD, и как ведёт себя `pool.dataset.query`/`pool.snapshot.query` через этот endpoint.
3. Закрывается ли WS через nginx на 25.04/25.10 после 60 с тишины, и сбрасывает ли таймер PING от .NET `KeepAliveTimeout`?
4. Сколько релизов TrueNAS держит старые версии API? Официальной политики удаления нет (в `stable/26` ещё есть `v24_10`).
5. Когда будет исполнен `FIXME: Eventually, prohibit this` для приватных методов? Возможна поломка без смены версии API.
6. Возвращает ли `snapshot.query` свойство `properties.clones` в ожидаемом формате; какой `origin.value` у не-клона в 25.04 и 25.10 (`''` или null)?
7. Попадают ли в 25.10 внутренние свойства TrueNAS в `user_properties` под переименованными ключами, и видна ли унаследованная метка как LOCAL?
8. ~~Как быстро появляется `/dev/zvol/<clone>` после clone, нужен ли retry?~~ На стенде 2026-10-02 `extent.create` сразу после clone прошёл с первого раза; retry оставлен (§8.2). Сбой публикации в тот день — ошибка copy_manager (§8.4), подтвердить журналом ядра.
9. Как Windows 10/11 (iSCSI initiator, disk.sys, NTFS) переносит UA REPORTED LUNS DATA HAS CHANGED и `target.delete(force)` при смонтированном томе: зависание I/O, ошибки игр, скорость отказа от переподключения?
10. Монтирует ли Windows NTFS на LUN с WP-битом, если образ снят с «грязным» журналом; работают ли игры, пишущие рядом с каталогом установки?
11. Приходят ли события REMOVED `iscsi.extent/targetextent.query` при каскадном удалении через `pool.dataset.delete` (по коду — нет)?
12. Сколько длится reload (`scstadmin -config`) при десятках объектов, и что происходит при конкурентных reload в 25.04?
13. Сохранят ли `pool.snapshot.*`/`pool.dataset.*` в финальном 26.0 аргументы и формы ошибок RC.1? Новые поля iSCSI в 26 (`dataset`, `relative_path`, `direct_config`, `mode`) — нужен отдельный разбор после релиза.
14. Срок действия самоподписанного `truenas_default` и точный формат `system.version` на реальных 25.04.x/25.10.x — снять на стенде.
15. Поведение плагинов iSCSI между точечными релизами (25.10.0 … 25.10.7) построчно не сравнивалось; нужен diff `plugins/iscsi_` по всем тегам `TS-25.10.*`.
