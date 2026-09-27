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

Версия: `id, label, state (publishing|published|retiring|retired|failed), role (current|rollback|null), targetIqn,
createdAt, publishedAt, retiredAt, lastError`.

Операция: `id, kind (publish|rollback), versionLabel, status (pending|running|done|failed), step (snapshot|clone|extent|
target|promote|done), attempts, lastError, requestedBy, createdAt, updatedAt`.

Предупреждение: `kind, subject, message, firstSeen, lastSeen`. Виды: `missingSnapshot`, `missingClone`, `cloneWritable`,
`cloneOrigin`, `missingExtent`, `extentMismatch`, `missingTarget`, `missingLun`, `orphanClone`, `retireBlocked`.
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
