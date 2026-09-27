# club-server

Сервер клуба: API агентов по контракту `club-contracts` и управление томом библиотеки игр на TrueNAS.
.NET 10, PostgreSQL 18, TrueNAS SCALE 25.10+.

```
src/Club.Server      ASP.NET Core: /api/v1 агента, /ws/agent (clubshell.v1), 501 для нереализованного по контракту
src/Club.TrueNas     JSON-RPC-клиент middleware TrueNAS (wss, API-ключ, закреплённая версия API) и операции с томами/iSCSI
contracts/           копия собранного контракта (scripts/sync-contracts.sh)
docs/research/       исследование API TrueNAS и стека .NET (источники и статусы проверки)
tests/               интеграционные тесты: сервер на временной базе PostgreSQL, адаптер на поддельном TrueNAS с TLS
```

## Что реализовано

- Регистрация агента по `X-Club-Key`, JWT RS256 (ключ в `data/jwt-signing-key.pem`), одноразовый refresh с отзывом
  всех токенов при повторе.
- Подпись каждого запроса: `hex(HMAC-SHA256(signingSecret, ts + METHOD + PATH + hex(SHA256(body))))`, PATH — raw target.
  Окно ±300 с, `401 clockSkew` с `X-Server-Time`. Защита от повторов по `(pcId, X-Timestamp, X-Signature)` —
  для изменяющих запросов; повтор после 5xx/408/429 разрешён (агент повторяет без переподписи).
- Heartbeat, конфиг (флаги нереализованных разделов выключены, пул аккаунтов выключен), политики с ETag,
  очередь команд at-least-once по REST и WebSocket.
- Всё, что есть в контракте и не реализовано, отвечает `501` (никогда `404`). Проверка обновлений — `204`,
  `GET /pcs/{pcId}` реализован (агент вызывает их сам, а 501 он повторяет — club-contracts/docs/SHELL_CHANGES.md п. 1).
- Адаптер TrueNAS: выбор версии API из белого списка, `auth.login_ex`, `core.ping`, переподключение; ensure-операции
  zvol → снапшот → RO-клон → extent (ro) → target → LUN; удаление только без `force`, с предохранителями.

- Библиотека игр (`src/Club.Server/Library`, секции `Library` и `TrueNas`): версии хранятся как намерения в БД,
  операции publish/rollback — в журнале `storage_operations`; единственный фоновый исполнитель (advisory lock).
  Публикация: снапшот мастер-zvol → RO-клон → extent (ro) → target с версией в имени → LUN → новая версия текущая,
  прежняя — откатная, ещё более старая разбирается (таргет без force: пока ПК подключены — ждём). Агенты получают
  `storage.gamesShare.iscsi {portal, targetIqn, readOnly: true}` через конфиг. Недоступный TrueNAS не тратит попытки,
  падение на любом шаге — операция выполняется заново. Сверка только пишет предупреждения (`storage_warnings`).

- API панели для библиотеки (`/panel/api/v1`, описание — `docs/panel-api.md`): обзор, публикация, откат, повтор
  упавшей операции, предупреждения. Доступ пока по одному токену `Panel:AdminToken`.

Не сделано: сессии, пользователи, тарифы, каталог игр, бэкенд кассы `/admin/*` (пока отвечают 501), вход в панель
по учётным записям, остальные экраны панели, Kea/PXE, фронтенд панели.

## Запуск

```bash
createdb club_dev
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Club.Server --urls http://127.0.0.1:5080
```

Тесты (нужен локальный PostgreSQL с правом CREATEDB у текущего пользователя):

```bash
dotnet test ClubServer.sln
```
