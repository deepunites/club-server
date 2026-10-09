# API панели администратора

Внутренний API нашей панели (не часть контракта с шеллом). Префикс `/panel/api/v1`, JSON как у агентского API:
camelCase, enum строками, время `2026-09-27T10:15:30.123Z`, ошибки — конверт `{ error: { code, message, details, traceId } }`.

**Доступ — временный:** `Authorization: Bearer <Panel:AdminToken>`. Пустой токен в конфиге закрывает панель целиком
(`401 unauthorized`, `details.reason = "panelToken"`). Заменить входом владельца, когда появятся учётные записи персонала.

## Библиотека игр

Операции асинхронные: запрос ставит операцию в журнал и отвечает `202 { operationId }`; выполняет фоновый
исполнитель (раз в `Library:WorkerIntervalSec`, по умолчанию 15 с). Ход — через `GET /library` или
`GET /library/operations/{id}`.

| Метод и путь | Что делает | Ответы |
|---|---|---|
| `GET /library` | Обзор: `storageEnabled`, `current`, `rollback`, все `versions`, `openOperations`, `warnings` | 200 |
| `POST /library/versions` `{ label }` | Опубликовать новую версию (снапшот мастер-тома → RO-клон → iSCSI). `label`: `^[a-z0-9][a-z0-9-]{0,39}$` | 202; 400 `validation`; 409 `conflict` (`exists`) |
| `POST /library/rollback` | Сделать откатную версию текущей (объекты не пересоздаются) | 202; 409 `conflict` (`noRollback`) |
| `GET /library/operations?limit=50` | Последние операции (1…200) | 200 |
| `GET /library/operations/{id}` | Одна операция | 200; 404 |
| `POST /library/operations/{id}/retry` | Повторить упавшую операцию (после исправления причины) | 202; 404; 409 (`notFailed`) |
| `GET /storage/warnings` | Активные предупреждения сверки намерений с TrueNAS | 200 |
| `POST /library/master/open` `{ machineId }` | Открыть мастер-том на запись этому ПК (суперклиент, `docs/master-editing.md`) | 202; 409 (`noInitiator`, `notApproved`, `publishPending`, `masterBusy`) |
| `POST /library/master/close` `{ force? }` | Закончить правку (ждёт отключения ПК) или отключить принудительно | 202; 409 (`masterClosed`) |

`POST /library/versions` принимает `allowDirtyMaster` и отвечает 409 `masterOpen` (мастер-том открыт) или
`masterDirty` (отключён принудительно, нужна явная публикация с `allowDirtyMaster: true`).

В обзоре `master { state (closed|opening|open|closing|failed), machineId, machineName, machineState, dirty, forceClose,
openedAt, closeRequestedAt, closedAt, lastError, driveLetter }` и `masterCandidates [{ id, number, name, online }]` —
одобренные ПК, чей помощник сообщил IQN инициатора. `lastError` у открытого тома — оговорка открытия шага verify, а
не ошибка: либо таргет мастер-тома так и не стал виден ПК суперклиента и после повторных применений конфигурации iSCSI
(`The master target is not visible to the superclient PC …`, подсказка — перезапуск службы iSCSI, если `M:` не
появится), либо группа инициаторов мастер-тома не включает IQN этого ПК (`The initiator group of the master target
does not include the superclient PC …`, подсказка — закрыть и открыть том заново). `machineState` — последний отчёт помощника о мастер-томе
(`mounted|mounting|failed|none`); отчёт без мастер-тома и закрытие тома его сбрасывают. Операции `masterOpen` (шаги auth, initiator, extent, target,
verify) и `masterClose` (target, extent, initiator, auth) — в общем журнале.

Версия: `id, label, state (publishing|published|retiring|retired|failed), role (current|rollback|null), targetIqn,
createdAt, publishedAt, retiredAt, lastError, mountedOn, contents, contentsAt`. `mountedOn` — сколько ПК на связи
работают с этой версией (подключена или ПК ждёт освобождения тома, чтобы переключиться на новую: запущена игра, Steam
или на томе открыты файлы; помощник 1.4.2+ сообщает в `switchPending` старую, ещё подключённую версию); `contents` —
папки верхнего уровня тома по отчёту помощника (null — ещё ни один ПК не сообщил).

В обзоре также `machines { online, onCurrent, onOlder, switchPending, failed, notMounted }` — ПК на связи (одобренные,
отчёт не старше 90 с) по отношению к текущей версии.

Экран — вкладка «Библиотека игр» (`/panel/#library`): текущая и откатная версии, откат, публикация новой версии
(метка по умолчанию — сегодняшняя дата), шаги выполняющихся операций, распределение ПК по версиям, состав версии,
журнал операций с повтором упавших, предупреждения сверки.

Операция: `id, kind (publish|rollback), versionLabel, status (pending|running|done|failed), step (snapshot|clone|target|
extent|lun|verify|promote|done), attempts, lastError, requestedBy, createdAt, updatedAt`. `verify` — таргет виден ПК
(iSCSI SendTargets с сервера к `Library:DiscoveryAddress`, по умолчанию `Library:PortalAddress`, от имени
`Library:ProbeInitiatorIqn`; таймаут `Library:DiscoveryTimeoutMs`, пауза перед проверкой `Library:VerifyDelayMs`);
если нет, сервер заново применяет конфигурацию iSCSI в TrueNAS, а если и это не помогло — операция падает с
подсказкой перезапустить службу iSCSI (версия не становится текущей); если таргет открыт только по списку
инициаторов (имя проверки пускает конкретная строка, а не `*` и не строка с ведущим `!`), подсказка сначала — проверить,
что в списке есть имя проверки. Портал, ответивший хоть раз, потом замолчал — проверка продолжается и кончается тем же
провалом. Группа инициаторов таргета не пускает имя проверки (строки сравниваются как в SCST: `*`, `?`, ведущий `!`,
без учёта регистра) — операция падает без reload-ов с подсказкой добавить его в группу. Оба провала завершают
операцию на той попытке, где случились (`Library:MaxAttempts` на них не действует: повтор прогнал бы те же reload-ы
и упал бы так же; `attempts` учитывает и прежние неудачные попытки); версия — `failed`, повтор — `retry` после
исправления причины. Портал проверить нельзя (не ответил ни разу) — версия становится текущей без проверки (после
повторных применений конфигурации вслепую), сверка пишет `discoveryUnavailable`. Сервер остановился между
переключением версии и отметкой операции — после рестарта операция завершается без повторной проверки: версия уже
текущая.

Предупреждение: `kind, subject, message, firstSeen, lastSeen`. Виды: `missingSnapshot`, `missingClone`, `cloneWritable`,
`cloneOrigin`, `missingExtent`, `extentMismatch`, `missingTarget`, `missingLun`, `targetNotDiscovered` (таргет
текущей/откатной версии есть в конфиге TrueNAS, но ПК его не видят; группа инициаторов открыта всем, или имя проверки
пускает `*` либо строка с `!`, — подсказка перезапустить службу iSCSI, это рвёт сессии всех ПК), `targetNotDiscoveredListed` (то же, но таргет открыт только по списку
инициаторов: подсказка сначала проверить, что список включает имя проверки `Library:ProbeInitiatorIqn`, и добавить
его, если нет), `discoveryUnavailable` (сервер не может проверить портал: `subject` — адрес), `probeDenied` (группа
инициаторов таргета версии не включает имя проверки сервера — проверить таргет нельзя), `orphanClone`, `retireBlocked`.
Сервер ничего не исправляет сам — решение за администратором.

## Рабочие станции

Экран — `/panel/` (статическая страница из `src/Club.Server/wwwroot/panel`, без сборки и внешних зависимостей,
ru/uz/en; узбекский перевод — черновой, проверить носителем). Обновляется раз в 5 с; перерисовывается только при
изменении данных.

| Метод и путь | Что делает | Ответы |
|---|---|---|
| `GET /machines` | Реестр: `machines`, `zones`, `currentLibraryVersion`, счётчики `online/offline/pending/maintenance` | 200 |
| `POST /machines/{id}/approve` | Одобрить новую машину: при следующей попытке регистрации помощник получит токены | 204; 404 |
| `POST /machines/{id}/reject` | Удалить неодобренную машину из реестра | 204; 404; 409 (`alreadyApproved`) |
| `PATCH /machines/{id}` `{ number?, name?, zone?, maintenance? }` | Номер места (1…9999, уникален), имя, зона, режим обслуживания | 204; 400; 404; 409 (`numberTaken`) |

Машина: `id, number, name, zone, status, hostname, ipAddress, macAddresses, helperVersion, osVersion, lastSeenAt,
bootTime, volume { state, libraryVersion, readOnlyVerified, outdated, error }, registeredAt`.

`status`: `pendingApproval` → `maintenance` → `neverSeen` / `online` (отчёт помощника не старше 90 с) / `offline`.
Время из будущего (сбитые часы) считается невменяемым: в ответе `null`, на экране прочерк, в логе предупреждение.

## Сеть

Экран «Сеть» (`/panel/#network`). Подробно про Kea — `docs/network.md`.

| Метод и путь | Что делает | Ответы |
|---|---|---|
| `GET /network/settings` | Адресный план: `configured, subnet, mask, keaSubnetId, interface, dhcpServer, gateway, dnsServers, poolStart, poolEnd, reservedStart, leaseTimeSec, seatCapacity, serverInterfaces` | 200 |
| `PUT /network/settings` | Сохранить план (все поля, кроме вычисляемых) и сразу синхронизировать резервации Kea | 200 (план); 400 |
| `GET /network/status` | `configured, keaEnabled, sync { at, schemaOk, schemaVersion, desired, inserted, updated, deleted, error }?, foreignDhcp [{ server, seenBy }], reservations [{ seat, name, mac, ip, hostname, problem }], warnings` | 200 |
| `GET /network/kea-dhcp4.conf` | Конфиг Kea для `/etc/kea/kea-dhcp4.conf` | 200; 409 (`notConfigured`) |

400 при сохранении: `details = { field, reason, errors: [{ field, reason }] }`. Причины: `format`, `outsideSubnet`,
`notNetworkAddress`, `beforeStart`, `insidePool`, `coversInfrastructure`, `range`.

`sync` отсутствует, пока запись в Kea выключена (`Kea:Enabled`) или план не сохранён. `warnings` — источник
`network`: `manualReservation`, `seatOutOfRange`, `noMac`, `duplicateMac`, `keaSchema`, `keaUnavailable`, `foreignDhcp`.

## Образы Windows и перезаливка

Подробно — `docs/imaging.md`.

| Метод и путь | Что делает | Ответы |
|---|---|---|
| `GET /images` | `enabled, current, rollback, images [{ label, state, role, sizeBytes, sha256, imageIndex, wimImages, generalized, createdAt, importedAt, publishedAt, lastError }], incoming [{ name, sizeBytes, modifiedAt }], warnings` | 200 |
| `POST /images/import` `{ file, label, index }` | Импорт файла из `incoming/` (фоновый: перенос, разбор WIM, sha256) | 202; 400 (`file`/`label`); 409 (`labelTaken`, `imagingDisabled`) |
| `POST /images/{label}/publish` | Сделать текущей; прежняя — откатная, более старая удаляется | 204; 404; 409 (`imageNotReady`) |
| `POST /images/rollback` | Поменять текущую и откатную местами | 204; 409 (`noRollback`) |
| `DELETE /images/{label}` | Удалить неопубликованную версию | 204; 409 (`inUse`) |
| `POST /machines/{id}/reimage` `{ image?, allowNewDisk? }` | Перезалить (по умолчанию текущей версией) | 202; 409 (`noImage`, `imageNotReady`, `noReservation`, `alreadyRequested`, `keaDisabled`, `imagingDisabled`, `notApproved`, `bootFilesMissing`, `secureBootThirdPartyCa`, `bootFilesNotSigned`, `needsCa2023BootManager`) |
| `POST /machines/{id}/reimage/cancel` | Отменить, пока диск не тронут | 204; 404; 409 (`diskTouched`) |

`GET /images` также отдаёт `bootFiles { tftpChecked, files [{ name, location (tftp|pxe), required, status (ok|missing|invalid|unsigned|badSignature|wrongSigner), sizeBytes, sha256, component, signedBy, expected }] }` — цепочка загрузки и подписи.

В `GET /machines` у машины `secureBoot { enabled, thirdPartyCa2011, windowsCa2023, pca2011Revoked }` (по отчёту помощника), `imageVersion` и `reimage { state, image, step, percent, message, failure, diskTouched,
pxeArmed, attempts, updatedAt }`, статус `reimaging`, в обзоре `currentImageVersion` и `reimaging`.

В обзоре `GET /machines` — `machineFeed { state, target, lastDeliveredAt, lastAttemptAt, machines, error, failures }`,
если включена отправка списка ПК (`docs/machine-feed.md`): `state` — `pending` (ещё не отправлялся) | `ok` | `failing`,
`target` — только хост получателя.
`failure` — шаг или причина: `systemDiskNotFound`, `ambiguousDisks`, `diskTooSmall`, `noInternalDisk`,
`biosNotSupported`, `imageUnavailable`, `notGeneralized`, `partition`, `download`, `verify`, `apply`, `identity`, `bcdboot`.

Для WinPE и iPXE (без авторизации): `GET /pxe/v1/boot.ipxe`, `/pxe/v1/machines/{mac}/boot.ipxe`,
`/pxe/v1/winpe/{club-deploy.ps1|winpeshl.ini|clubdeploy.json}`, `/pxe/v1/files/{путь в PxeRoot}`;
`POST /deploy/v1/start`, `PUT /deploy/v1/jobs/{id}/progress`, `GET /deploy/v1/jobs/{id}/image` (Range),
`POST /deploy/v1/jobs/{id}/unattend|fail|complete`.

