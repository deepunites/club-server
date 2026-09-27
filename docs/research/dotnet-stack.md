# .NET-стек сервера клуба: исследование

Дата: 2026-09-27. Контекст: сервер на C#/.NET, PostgreSQL, контракт — спецификация OpenAPI 3.1 (spec-first), хранилище TrueNAS управляется по JSON-RPC 2.0 поверх WebSocket (подробности в [truenas-api.md](truenas-api.md)). Все находки перепроверены отдельным проходом (verify-dotnet есть).

**Легенда статусов**

| Метка | Значение |
|---|---|
| **[ПОДТВЕРЖДЕНО]** | перепроверено по первичному источнику, ссылка рядом |
| **[ИСПРАВЛЕНО]** | исходная находка оказалась неверной, здесь приведён только исправленный вариант |
| **[ГИПОТЕЗА]** | вывод не подтверждён первоисточником или прогоном |

---

## 0. Коротко

1. Целевая платформа — **.NET 10 LTS** (поддержка до 14.11.2028). .NET 8 заканчивается 10.11.2026, через 44 дня.
2. Готового генератора «OpenAPI 3.1 → серверные заглушки ASP.NET Core на System.Text.Json» без оговорок нет. Кандидаты: понижение спецификации до 3.0 через Microsoft.OpenApi 2.x и NSwag, либо свой небольшой генератор на Microsoft.OpenApi 2.x. Нужен спайк.
3. Клиент TrueNAS — тонкий собственный на `ClientWebSocket` + System.Text.Json. StreamJsonRpc допустим, но тянет Newtonsoft/MessagePack и не закрывает специфику TrueNAS.
4. PostgreSQL: Npgsql 10. Миграции с откатом — EF Core 10 bundles или FluentMigrator 8. DbUp и Evolve отпадают: Down-миграций нет.
5. JWT: `JsonWebTokenHandler` (IdentityModel 8.x), `ValidateTokenAsync`, явные `ValidAlgorithms`; в JwtBearer явно выставить `MapInboundClaims=false`.
6. WebSocket-сервер: явный сабпротокол, проверка Origin (CORS на WS не действует), `KeepAliveTimeout` (.NET 9+), компрессию не включать.

---

## 1. Сроки поддержки .NET

| Версия | Тип | Выпуск | Конец поддержки | Состояние на 27.09.2026 | Статус |
|---|---|---|---|---|---|
| .NET 8 | LTS | 14.11.2023 | **10.11.2026** | maintenance, последний патч 8.0.31 (08.09.2026), осталось 44 дня | [ПОДТВЕРЖДЕНО] |
| .NET 9 | STS | — | 10.11.2026 | maintenance, 9.0.20 | [ПОДТВЕРЖДЕНО] |
| .NET 10 | LTS | 11.11.2025 | **14.11.2028** | active, 10.0.12 (08.09.2026) | [ПОДТВЕРЖДЕНО] |

Политика: LTS поддерживается 3 года, STS — 2; последние 6 месяцев — maintenance (только безопасность); поддерживается только последний патч. Расхождение в документации: в таблице EF Core конец EF Core 10 указан как 10.11.2028, а в политике .NET — 14.11.2028. Источники: [политика поддержки](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [releases-index.json](https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json), [EF Core what's new](https://learn.microsoft.com/en-us/ef/core/what-is-new/).

**Что теряется, если где-то остаться на .NET 8** (например, агенты на машинах клуба):

| Компонент | .NET 8 | .NET 10 |
|---|---|---|
| EF Core | максимум 9 (все миграции одной транзакцией; поддержка до 10.11.2026) | 10 (транзакция на миграцию) |
| Npgsql | 10.x работает (есть `net8.0`) | 10.x |
| Npgsql.EntityFrameworkCore.PostgreSQL | ≤ 9 | 10.0.3 (только `net10.0`) |
| `ClientWebSocketOptions.KeepAliveTimeout` / серверный `KeepAliveTimeout` | нет, нужен прикладной heartbeat | есть (с .NET 9) |
| Microsoft.AspNetCore.OpenApi | 8.x → Microsoft.OpenApi 1.x (без 3.1), встроенной генерации документа нет | 10.x → Microsoft.OpenApi 2.x, документ 3.1 по умолчанию |
| IdentityModel | JwtBearer 8.0.31 тянет 7.7.3 (поддерживаемая 7.x, конец тоже 10.11.2026) | JwtBearer 10.0.12 тянет 8.19.2 |

Статусы строк — в разделах ниже.

---

## 2. OpenAPI 3.1 → C#

### 2.1 Генераторы

| Инструмент | Что умеет | OpenAPI 3.1 | Серверные заглушки | System.Text.Json | Статус |
|---|---|---|---|---|---|
| **NSwag 14.7.1** (20.04.2026, NJsonSchema 11.6.1) | клиенты и контроллеры (`CSharpControllerGenerator`: `ControllerStyle` Partial/Abstract, `ControllerTarget` AspNetCore, `UseCancellationToken`, `UseActionResultType`) | **нет официальной поддержки**: в `SchemaType` только JsonSchema/Swagger2/OpenApi3; issue [NJsonSchema#1603](https://github.com/RicoSuter/NJsonSchema/issues/1603) открыт с 2023 | да | по умолчанию `JsonLibrary=NewtonsoftJson`; для STJ-полиморфизма нужны **оба** флага: `JsonLibrary=SystemTextJson` и `JsonPolymorphicSerializationStyle=SystemTextJson` (иначе будет `JsonInheritanceConverter`); абстрактные наследники в `[JsonDerivedType]` не попадают | [ПОДТВЕРЖДЕНО] [NSwag v14.7.1](https://github.com/RicoSuter/NSwag/releases/tag/v14.7.1), [CSharpControllerGeneratorSettings.cs](https://github.com/RicoSuter/NSwag/blob/master/src/NSwag.CodeGeneration.CSharp/CSharpControllerGeneratorSettings.cs), [Class.liquid](https://github.com/RicoSuter/NJsonSchema/blob/master/src/NJsonSchema.CodeGeneration.CSharp/Templates/Class.liquid) |
| **Kiota 1.35.0** (01.09.2026) | **только клиент** | да (PR #5936, 24.02.2025; type-массивы → union-типы) | нет | модели сериализуют себя сами (`Serialize(ISerializationWriter)`, фабрика `CreateFromDiscriminatorValue(IParseNode)`), к STJ не привязаны; discriminator только через allOf («Using oneOf to constrain derived types is not supported»); в C# все свойства nullable ([#3911](https://github.com/microsoft/kiota/issues/3911)). В документации описаны x-расширения `x-ms-enum`, `x-ms-enum-flags` и `x-ms-primary-error-message`. | [ИСПРАВЛЕНО] [README](https://github.com/microsoft/kiota/blob/main/README.md), [models](https://learn.microsoft.com/en-us/openapi/kiota/models), [errors](https://learn.microsoft.com/en-us/openapi/kiota/errors) |
| **openapi-generator 7.25.0** (24.08.2026), генератор `aspnetcore` | сервер (SERVER, STABLE) | «3.1 (beta support)» | да | `useNewtonsoft=true` по умолчанию (можно выключить); `aspnetCoreVersion` до 8.0 (9.0/10.0 нет); allOf/anyOf/oneOf/Union ✗, Polymorphism ✓; нормализатор `SIMPLIFY_ONEOF_ANYOF` (включён с 7.0.0) убирает null-подсхему и ставит `nullable: true`; нужна Java | [ПОДТВЕРЖДЕНО] [v7.25.0](https://github.com/OpenAPITools/openapi-generator/releases/tag/v7.25.0), [aspnetcore.md](https://github.com/OpenAPITools/openapi-generator/blob/master/docs/generators/aspnetcore.md), [customization.md](https://github.com/OpenAPITools/openapi-generator/blob/master/docs/customization.md) |

**Детали NSwag и 3.1**
- NJsonSchema при чтении принимает массив `type` (сеттер `TypeRaw` складывает его во флаги). `IsNullable(SchemaType)` даёт true при `Null` в type, `null` в enum, nullable-схеме в `oneOf` (при пустом type), `nullable: true` из 3.0 и расширении `nullable`. Оба наших варианта nullable (`type:[X,"null"]` и `oneOf:[{$ref},{type:null}]`) на уровне модели распознаются. [ПОДТВЕРЖДЕНО] [JsonSchema.cs](https://github.com/RicoSuter/NJsonSchema/blob/master/src/NJsonSchema/JsonSchema.cs). Что на выходе получится корректный C# (`T?`, а не обёртка), не проверялось — [ГИПОТЕЗА].
- Известные открытые проблемы: [NSwag#5387](https://github.com/RicoSuter/NSwag/issues/5387) (06.2026) — с выводом .NET 10 в 3.1 ломается генерация `IFormFile`/`FileParameter`, `format: binary` через `$ref` не распознаётся (описано для TS-клиента, подтверждено и для C#-клиента); [NJsonSchema#1536](https://github.com/RicoSuter/NJsonSchema/issues/1536) — ссылки `#/$defs/...` не разрешаются; [NSwag#4839](https://github.com/RicoSuter/NSwag/issues/4839) — enum в стиле 3.1 (`oneOf`+`const`). Совет мейнтейнера про `ISchemaProcessor` относится к генерации **спецификации** из C#-типов и для генерации кода из готовой 3.1 не поможет. [ПОДТВЕРЖДЕНО]
- x-расширения лежат в `JsonSchema.ExtensionData` и доступны только в своих шаблонах или процессорах. [ПОДТВЕРЖДЕНО]

### 2.2 Microsoft.OpenApi (OpenAPI.NET)
- 2.x читает и пишет OpenAPI 2.0/3.0/3.1, 3.x добавляет 3.2. Microsoft.AspNetCore.OpenApi 10.x и Swashbuckle 10.x совместимы **только с 2.x**. Ветка 1.x — для AspNetCore OpenAPI < 10, получает только исправления безопасности, поддержка до ноября 2026. [ПОДТВЕРЖДЕНО] [CONTRIBUTING.md](https://github.com/microsoft/OpenAPI.NET/blob/main/CONTRIBUTING.md#branches-and-support-policy)
- В 2.x `OpenApiSchema.Type` — флаговый `JsonSchemaType` (`nullable` заменён на `String | Null`), `Discriminator.Mapping` — `Dictionary<string, OpenApiSchemaReference>`, JSON читается ядром, YAML — через `Microsoft.OpenApi.YamlReader`. Последние версии: 2.12.2 и 3.10.2 (20.08.2026). TFM `netstandard2.0; net8.0`. Это парсер и объектная модель, генератора кода в нём нет. [ПОДТВЕРЖДЕНО] [upgrade-guide-2.md](https://github.com/microsoft/OpenAPI.NET/blob/main/docs/upgrade-guide-2.md)
- **Понижение 3.1 → 3.0.** `SerializeAsV3` превращает `type` с флагом Null в `type: X` + `nullable: true`; схема только из `{type: null}` пишется как `{enum: [null], nullable: true}`; несколько ненулевых типов переписываются в `anyOf` (или `oneOf`, если `anyOf` занят). Как NSwag обработает `oneOf [{$ref},{enum:[null]}]` и такие `anyOf`, не проверялось. [ПОДТВЕРЖДЕНО] по коду [OpenApiSchema.cs v2.12.2](https://github.com/microsoft/OpenAPI.NET/blob/v2.12.2/src/Microsoft.OpenApi/Models/OpenApiSchema.cs); поведение NSwag на результате — [ГИПОТЕЗА]

### 2.3 ASP.NET Core 10 и 8 (code-first, для сверки)
ASP.NET Core 10 по умолчанию генерирует документ 3.1 на Microsoft.OpenApi 2.0: nullable выражается массивом `type` с `"null"`, версия выбирается через `OpenApiOptions.OpenApiVersion`. Встроенная генерация (`AddOpenApi`/`MapOpenApi`) появилась только в .NET 9. На .NET 8 `Microsoft.AspNetCore.OpenApi` 8.x зависит от Microsoft.OpenApi 1.4.3, без 3.1. [ПОДТВЕРЖДЕНО] [ASP.NET Core 10 release notes](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0), [OpenAPI overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0)

### 2.4 Итог по генерации
Ни NSwag 14.7.1, ни openapi-generator 7.25 `aspnetcore`, ни Kiota 1.35 не делают серверные заглушки ASP.NET Core на System.Text.Json напрямую из 3.1 с type-массивами, oneOf-null, discriminator и x-расширениями без оговорок. Блокеры: у NSwag нет 3.1; у openapi-generator oneOf ✗ и 3.1 в бете (выбор JSON-библиотеки блокером не является, `useNewtonsoft=false` даёт STJ); Kiota генерирует только клиентов. [ГИПОТЕЗА] исходные факты подтверждены, сам вывод требует спайка на нашей спецификации.

---

## 3. JSON-RPC клиент к TrueNAS

### 3.1 Требования сервера (из кода middleware 25.04 = stable/fangtooth, 25.10 = stable/goldeye)
- `jsonrpc == "2.0"`, `id` — null, int или str, `method` — непустая строка, **`params` — только массив** (иначе `-32600` «'params' member must be of type array»; без `params` подставляется `[]`). Именованные параметры не поддерживаются. `-32600` отправляется, только если в запросе есть `id`. Сервер сам шлёт notifications `collection_update` и `notify_unsubscribed`, у них `params` — объект. [ПОДТВЕРЖДЕНО] [rpc.py goldeye](https://github.com/truenas/middleware/blob/stable/goldeye/src/middlewared/middlewared/api/base/server/ws_handler/rpc.py)
- Коды ошибок: стандартные `-32700/-32600/-32601/-32602/-32603` и свои `-32000` TOO_MANY_CONCURRENT_CALLS и `-32001` CALL_ERROR. `error.data = {error, errname, reason, trace|null, extra, py_exception?}`; для валидации `extra` — кортежи `(attribute, errmsg, errno)`. Подробная таблица — в [truenas-api.md §4](truenas-api.md). [ПОДТВЕРЖДЕНО] [api_client jsonrpc.py](https://github.com/truenas/api_client/blob/stable/goldeye/truenas_api_client/jsonrpc.py)
- Маршрутизация версий: `GET /api/{version}` для каждого каталога `api/vXX_YY_Z`, `/api/current` — алиас новейшей, `GET /api/versions` — список, старые версии обслуживает `LegacyAPIMethod`; в 25.10 добавлен `/api/boot_id`; удаление старых версий управляется механизмом `_removed_in`. [ПОДТВЕРЖДЕНО] [main.py goldeye](https://github.com/truenas/middleware/blob/stable/goldeye/src/middlewared/middlewared/main.py). **Уточнение к следствию для Q7:** закреплённая версия сохраняет схему внутри одной мажорной линии, но при переходе 25.04 → 25.10 методы без модели в `v25_04_*` (`pool.dataset.create/update/delete`, `pool.snapshot.*`, `service.*`) через `/api/v25.04.x` дают `-32001` ENOMETHOD — см. [truenas-api.md §3](truenas-api.md) [ИСПРАВЛЕНО по verify-transport и verify-zfs].
- В 25.10 вызов приватного метода пишет в лог warning с `FIXME: Eventually, prohibit this`; для внешнего WS-клиента (`origin.uid == 33`) он будет появляться при каждом вызове. В 25.04 такой проверки нет. Пользоваться только публичными методами. [ПОДТВЕРЖДЕНО]
- Серверный WebSocket TrueNAS — `aiohttp.WebSocketResponse()` без аргументов: autoping=True, heartbeat=None, сабпротокол не согласуется, `max_msg_size` 4 МБ, `compress=True`. Официальный Python-клиент полагается на TCP keepalive (idle 1 с), а не на WS-ping. Версия aiohttp в образе TrueNAS не проверялась, дефолты взяты из текущей документации. [ПОДТВЕРЖДЕНО] [ws_handler/base.py](https://github.com/truenas/middleware/blob/stable/goldeye/src/middlewared/middlewared/api/base/server/ws_handler/base.py), [aiohttp](https://docs.aiohttp.org/en/stable/web_reference.html)

### 3.2 StreamJsonRpc
- Версия 2.25.29 (15.06.2026), TFM `netstandard2.0/2.1`, `net8.0`, `net9.0`; отдельной сборки под net10.0 нет, на .NET 10 используется `net9.0`. `WebSocketMessageHandler(WebSocket, IJsonRpcMessageFormatter, int sizeHint = 4096)`: одно JSON-RPC сообщение = одно WS-сообщение. Есть `SystemTextJsonFormatter`. Пакет **всегда** тянет MessagePack, Nerdbank.MessagePack, Nerdbank.Streams и Newtonsoft.Json. [ПОДТВЕРЖДЕНО] [NuGet](https://www.nuget.org/packages/StreamJsonRpc), [extensibility.md](https://github.com/microsoft/vs-streamjsonrpc/blob/main/docfx/docs/extensibility.md)
- `InvokeAsync`/`NotifyAsync` шлют params массивом (подходит TrueNAS), `InvokeWithParameterObjectAsync` — объектом (TrueNAS отклонит). Для чужого формата `error.data` нужно переопределить `JsonRpc.GetErrorDetailsDataType`, иначе ожидается `CommonErrorData`. [ПОДТВЕРЖДЕНО] [sendrequest.md](https://github.com/microsoft/vs-streamjsonrpc/blob/main/docfx/docs/sendrequest.md), [exceptions.md](https://github.com/microsoft/vs-streamjsonrpc/blob/main/docfx/docs/exceptions.md)
- Корректно ли StreamJsonRpc привяжет `params`-объект входящего `collection_update` к локальному методу с именованными аргументами — не проверено. [ГИПОТЕЗА]

### 3.3 Keepalive клиента
`ClientWebSocketOptions.KeepAliveTimeout` есть только в .NET 9+: клиент шлёт PING и ждёт PONG. По умолчанию значение `InfiniteTimeSpan`, и тогда шлётся только unsolicited PONG без обнаружения мёртвого соединения. `KeepAliveInterval` по умолчанию `WebSocket.DefaultKeepAliveInterval` («typically 30 seconds»). На .NET 8 мёртвое соединение ловится только прикладным `core.ping` с таймаутом. [ПОДТВЕРЖДЕНО] [KeepAliveTimeout](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.keepalivetimeout?view=net-10.0), [KeepAliveInterval](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.keepaliveinterval?view=net-10.0). Связка с 60-секундным таймаутом nginx TrueNAS — см. truenas-api.md §1 [ГИПОТЕЗА].

---

## 4. PostgreSQL и миграции

### 4.1 Драйверы
- **Npgsql 10.0.3** (27.05.2026): `net8.0; net9.0; net10.0`. Npgsql 9.0.5: `net6.0; net8.0`. Ломающие изменения 10: `date`/`time` из нетипизированных методов (`GetValue`, `ExecuteScalar`) читаются как `DateOnly`/`TimeOnly` (через `GetFieldValue<DateTime/TimeSpan>` — как раньше); `cidr` → `IPNetwork` (`NpgsqlCidr` устарел); изменены метрики и трейсинг; при заданном root CA цепочка TLS проверяется только им; .NET 6 не поддерживается. Список неполный. [ПОДТВЕРЖДЕНО] [release notes 10.0](https://www.npgsql.org/doc/release-notes/10.0.html), [Npgsql.csproj v10.0.3](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Npgsql.csproj)
- **Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3** (10.07.2026): только `net10.0`, EF Core `[10.0.4, 11)`, Npgsql ≥ 10.0.3. EF Core 10 требует .NET 10; EF Core 9 и 8 работают на .NET 8, их поддержка до 10.11.2026. [ПОДТВЕРЖДЕНО] [EFCore.PG.csproj](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/EFCore.PG.csproj)

### 4.2 Инструменты миграций

| Инструмент | Откат (Down) | Транзакции | Блокировка от параллельного запуска | Поставка | Статус |
|---|---|---|---|---|---|
| **EF Core 10** | да (`Up/Down`); `efbundle <Migration>` откатывает, выполняя Down всех более новых, `efbundle 0` откатывает всё; документация предупреждает о возможной потере данных | **транзакция на миграцию** (в EF 9 была одна на все, в EF 10 откатили) | есть для bundle/CLI/`Migrate()`, **нет для SQL-скриптов**; своя транзакция вокруг `MigrateAsync` → `MigrationsUserTransactionWarning` | migration bundle — самостоятельный исполняемый файл без SDK и исходников, `--connection`; можно сгенерировать idempotent SQL и скрипт отката | [ПОДТВЕРЖДЕНО] [applying](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying), [EF9 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/breaking-changes) |
| **FluentMigrator 8.0.1** (20.01.2026) | да (`Up/Down`, `MigrateDown`, `Rollback(steps)`, `RollbackToVersion`) | `TransactionBehavior.Default` — транзакция на миграцию; `None`; `RunnerOptions.TransactionPerSession` — одна на сессию | встроенной не найдено (поиск `pg_advisory`, `sp_getapplock` и т.п. по исходникам ничего не дал) | TFM `net48; netstandard2.0`, таблица `VersionInfo`, in-process runner | [ПОДТВЕРЖДЕНО] [IMigrationRunner.cs](https://github.com/fluentmigrator/fluentmigrator/blob/v8.0.1/src/FluentMigrator.Runner.Core/IMigrationRunner.cs), [TransactionBehavior.cs](https://github.com/fluentmigrator/fluentmigrator/blob/v8.0.1/src/FluentMigrator.Abstractions/TransactionBehavior.cs); отсутствие блокировки — [ГИПОТЕЗА] (проверка отсутствия) |
| **DbUp** (dbup-core 6.1.1, dbup-postgresql 7.0.1, 23.02.2026) | **нет** («Schema downgrading is not implemented in DbUp because it's error prone»), отсылает к стороннему DbUp.Downgrade | `WithoutTransaction` (по умолчанию), `WithTransactionPerScript`, `WithTransaction` | — | — | [ПОДТВЕРЖДЕНО] [schema-downgrading](https://dbup.readthedocs.io/en/latest/more-info/schema-downgrading/), [transactions](https://dbup.readthedocs.io/en/latest/more-info/transactions/) |
| **Evolve** | **нет** (issue [#43](https://github.com/lecaillon/Evolve/issues/43) «Rollback support» открыт с 2018) | — | — | последний стабильный 3.2.0 (30.06.2023); пререлизы 3.3.0-alpha1 (01.02.2024) и 3.4.0-alpha1 (NuGet, 30.09.2025, без релиза на GitHub); развивается редкими пререлизами | [ИСПРАВЛЕНО] [releases](https://github.com/lecaillon/Evolve/releases) |

- С EF 9 `Migrate()`/`MigrateAsync()` бросают исключение при `PendingModelChangesWarning`; в CI проверяется командой `dotnet ef migrations has-pending-model-changes` (есть с EF 8). [ПОДТВЕРЖДЕНО]
- Блокировка в провайдере Npgsql для EF: `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE`, `LockReleaseBehavior = Transaction`. В PostgreSQL блокировка снимается в конце транзакции, поэтому «застрявшего» lock после падения процесса быть не должно. [ПОДТВЕРЖДЕНО] по коду [NpgsqlHistoryRepository.cs](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/Migrations/Internal/NpgsqlHistoryRepository.cs) и [PostgreSQL LOCK](https://www.postgresql.org/docs/current/sql-lock.html); прогоном не проверено — [ГИПОТЕЗА]. **Следствие для EF 10:** транзакция коммитится после каждой миграции, и блокировка берётся заново для следующей. Между миграциями и на командах с `suppressTransaction` (например, `CREATE INDEX CONCURRENTLY`) таблица не заблокирована, так что защита от параллельного запуска действует помиграционно, а не на весь прогон. [ПОДТВЕРЖДЕНО] по коду EF Core release/10.0

---

## 5. JWT

- Начиная с ASP.NET Core 8, JwtBearer по умолчанию использует `JsonWebTokenHandler`/`JsonWebToken` (Microsoft.IdentityModel.JsonWebTokens). `JwtSecurityToken` назван «previous generation». Причины: производительность +30%, «last known good» метаданные, асинхронность. Старое поведение возвращает `JwtBearerOptions.UseSecurityTokenValidators = true`. [ПОДТВЕРЖДЕНО] [breaking change 8.0](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/securitytoken-events)
- Версии IdentityModel: 8.x — текущая ветка, привязана к .NET 9/10, поддержка примерно до ноября 2028, TFM от net462 до net10.0, последний релиз 8.23.0 (NuGet 18.09.2026). 7.x поддерживается до 10.11.2026, начиная с 7.7.1. **Актуальный JwtBearer 8.0.31 тянет IdentityModel 7.7.3 (поддерживаемая), JwtBearer 10.0.12 — 8.19.2. Явно поднимать IdentityModel до 8.x на .NET 8 не обязательно.** [ИСПРАВЛЕНО] [IdentityModel README](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/dev/README.md), [JwtBearer на NuGet](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.JwtBearer/10.0.0)
- API: `JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)` создаёт JWS или JWE. `ValidateTokenAsync(string, TokenValidationParameters)` не бросает исключение, а возвращает `TokenValidationResult`, где нужно проверять `IsValid` и `Exception`. Синхронный `ValidateToken` помечен `[Obsolete]`. `ValidAlgorithms`: непустая коллекция разрешает только перечисленные алгоритмы, по умолчанию null. [ПОДТВЕРЖДЕНО] [JsonWebTokenHandler](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler?view=msal-web-dotnet-latest), [ValidAlgorithms](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters.validalgorithms?view=msal-web-dotnet-latest)
- **Ловушка:** `MapInboundClaims=false` — это дефолт самостоятельного `JsonWebTokenHandler`. В ASP.NET Core `JwtBearerOptions.MapInboundClaims` по умолчанию **true**, и claims маппятся. Чтобы получить «сырые» имена claims, нужно явно поставить `options.MapInboundClaims = false`. [ПОДТВЕРЖДЕНО]

---

## 6. WebSocket-сервер ASP.NET Core

| Тема | Факт | Статус |
|---|---|---|
| Сабпротокол | `HttpContext.WebSockets.WebSocketRequestedProtocols`, `AcceptWebSocketAsync()`, `AcceptWebSocketAsync(string subProtocol)`, `AcceptWebSocketAsync(WebSocketAcceptContext)`. В `WebSocketAcceptContext` есть `SubProtocol`, `KeepAliveInterval`, `KeepAliveTimeout`, `DangerousEnableCompression` (документация предупреждает о CRIME/BREACH), `DisableServerContextTakeover`, `ServerMaxWindowBits`. | [ПОДТВЕРЖДЕНО] [WebSocketManager](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.websocketmanager?view=aspnetcore-10.0), [WebSocketAcceptContext](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.websocketacceptcontext?view=aspnetcore-10.0) |
| Keepalive | `WebSocketOptions.KeepAliveInterval` по умолчанию 2 мин. `KeepAliveTimeout` (сервер шлёт PING; без PONG — abort, `ReceiveAsync` бросает) по умолчанию `InfiniteTimeSpan` (выключен), задаётся глобально или на соединение; есть только в ASP.NET Core 9+. «The server isn't automatically informed when the client disconnects due to loss of connectivity». | [ПОДТВЕРЖДЕНО] [websockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0) |
| Origin | `AllowedOrigins` по умолчанию разрешает все; CORS на WebSocket не действует. | [ПОДТВЕРЖДЕНО] там же |
| HTTP/2 | С .NET 7 Kestrel поддерживает WS поверх HTTP/2 (RFC 8441). Запрос идёт методом CONNECT, поэтому вместо `[HttpGet("/path")]` нужен `[Route("/path")]`. | [ПОДТВЕРЖДЕНО] там же |

---

## 7. Рекомендации

1. **Платформа:** `net10.0` для сервера и всех новых компонентов. Если .NET 8 где-то обязателен (например, агент на ПК клуба; он относится к club-shell, а не к серверу), учитывать таблицу §1: EF Core ≤ 9, нет `KeepAliveTimeout`, OpenAPI 1.x. IdentityModel на .NET 8 можно оставить 7.7.x, которую тянет JwtBearer 8.0.31, до конца поддержки .NET 8.
2. **OpenAPI:** спецификация 3.1 — единственный источник правды. В CI парсить и валидировать её Microsoft.OpenApi 2.x (совместим с Microsoft.AspNetCore.OpenApi 10; на 3.x не переходить до ASP.NET Core 11). Для генерации провести спайк двух вариантов:
   - (a) быстрый: понизить до 3.0 через `SerializeAsV3` → NSwag `CSharpControllerGenerator` (`ControllerStyle=Abstract`, `JsonLibrary=SystemTextJson`, `JsonPolymorphicSerializationStyle=SystemTextJson`, `GenerateNullableReferenceTypes=true`);
   - (b) надёжный: свой генератор DTO и интерфейсов на Microsoft.OpenApi 2.x (`JsonSchemaType` flags, `Discriminator.Mapping`, `Extensions` для x-*), который выдаёт record-ы с `[JsonPolymorphic]`/`[JsonDerivedType]`.
   В любом варианте нужны golden-тесты на nullable (type-массив и oneOf-null), discriminator и x-расширения. Kiota (только клиент) и openapi-generator `aspnetcore` (oneOf ✗, 3.1 beta, `aspnetCoreVersion` ≤ 8.0, Java) для сервера не брать.
3. **Клиент TrueNAS:** свой тонкий клиент на `ClientWebSocket` + System.Text.Json: корреляция по строковому GUID, `params` только массивом, свой тип ошибки (`code`, `errno`, `errname`, `reason`, `extra`), обработка `collection_update`/`notify_unsubscribed`, семафор на 10 параллельных вызовов (защита от `-32000`), EJSON-конвертеры, `KeepAliveInterval` + `KeepAliveTimeout` + прикладной `core.ping`, переподключение с повторным логином и переподпиской, выбор версии API и реализации адаптера по мажорной линии TrueNAS (truenas-api.md §9). StreamJsonRpc — только как запасной вариант (`WebSocketMessageHandler` + `SystemTextJsonFormatter`, только `InvokeAsync`, переопределённый `GetErrorDetailsDataType`).
4. **PostgreSQL:** Npgsql 10. Если ORM — EF Core 10: migration bundle (накат `efbundle`, откат `efbundle <PrevMigration>`, оба с `--connection` от отдельной deploy-учётки), в CI `has-pending-model-changes`, обязательное ревью Down-миграций; учитывать помиграционную блокировку. Если ORM нет (Dapper/Npgsql): FluentMigrator 8 с in-process runner плюс своя обёртка `pg_advisory_lock` против параллельного запуска (гипотеза об отсутствии встроенной блокировки, проверить). DbUp и Evolve отклонить: нет Down.
5. **JWT:** `Microsoft.IdentityModel.JsonWebTokens` 8.x, `CreateToken` + `ValidateTokenAsync` с проверкой `IsValid`; RS256 через `RsaSecurityKey` с `KeyId`, при проверке `ValidAlgorithms=["RS256"]`, явные `ValidIssuer`/`ValidAudience`. В JwtBearer — `MapInboundClaims=false`. `System.IdentityModel.Tokens.Jwt` и синхронный `ValidateToken` не использовать.
6. **WebSocket-сервер:** выбирать сабпротокол из `WebSocketRequestedProtocols` и отклонять запрос без нашего; принимать через `WebSocketAcceptContext { SubProtocol, KeepAliveInterval, KeepAliveTimeout }`; проверять Origin (`AllowedOrigins`); компрессию не включать; маршрут через `[Route]`/`Map`, чтобы работал HTTP/2 CONNECT.

---

## 8. Открытые вопросы

1. Как NSwag 14.7.1 обработает нашу реальную спецификацию 3.1 (type-массивы, `oneOf [{$ref},{type:null}]`, `discriminator.mapping`, `$defs`, `const`, x-расширения) напрямую и после понижения до 3.0 через Microsoft.OpenApi (включая `{enum:[null], nullable:true}` и переписанные `anyOf`)? Нужен спайк с golden-файлами.
2. Используем ли EF Core как ORM? От этого зависит выбор между EF Core bundles и FluentMigrator.
3. Нужен ли где-то .NET 8 (агенты на ПК клуба, старые хосты), или весь наш стек — .NET 10?
4. Отпускается ли блокировка миграций Npgsql EF при падении процесса без ручного вмешательства (ожидается да)? Проверить прогоном вместе с параллельным запуском двух bundle.
5. Есть ли в FluentMigrator 8 встроенная межпроцессная блокировка (поиск по коду не нашёл)?
6. Какая версия aiohttp стоит в образах TrueNAS 25.04/25.10 (дефолты `WebSocketResponse` взяты из текущей документации)?
7. Корректно ли StreamJsonRpc привязывает объектные `params` из `collection_update` к локальному методу (если выберем его)?
8. Хранение и ротация RSA-ключей для JWT (JWKS, `kid`) в этом исследовании не рассматривались.
9. Как долго TrueNAS хранит старые версии API (`v25_04_x`) в 26.x и как на практике работает `_removed_in` — см. truenas-api.md, открытый вопрос 4.
