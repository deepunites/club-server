# Сохранения игр игрока — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** помощник на ПК по команде шелла раскладывает сохранения игрока с сервера клуба при входе и забирает их в конце
сеанса; сервер хранит папку каждого игрока на общем диске; панель задаёт папки и показывает игроков.

**Architecture:** общий код путей и архивов — новая библиотека `Club.Saves` (сервер и помощник проверяют одно и то же).
Сервер: таблицы `save_*`, файловое хранилище `SaveStore` на `/srv/club/saves`, API помощника под `/diskless/v1/…/saves`,
API и вкладка панели. Помощник: методы API в `DisklessApiClient`, логика в `SavesService` (Core, проверяемая с
подделками), протокол канала в `SavesCommandHandler`, транспорт — `SavesPipeServer` (Windows).

**Tech Stack:** C#/.NET 10, ASP.NET Core minimal API, PostgreSQL + Dapper + FluentMigrator, xunit, System.IO.Compression,
System.IO.Pipes (Windows), статическая панель на vanilla JS.

**Spec:** `docs/superpowers/specs/2026-11-05-player-saves-design.md`

## Global Constraints

- Лимит по умолчанию 200 МБ на игрока; исключения по умолчанию `Logs`, `Crashes`, `CrashDumps`, `*.dmp`.
- `playerId`: `[A-Za-z0-9-]{1,64}`, хранится и сравнивается в нижнем регистре.
- Архив: zip, записи `<entryId>/<относительный путь>` (entryId — GUID в формате `D`, нижний регистр), имена UTF-8, не
  больше 50 000 файлов; запрещены абсолютные пути, `..`, `:` в имени, имена устройств Windows.
- Шаблоны путей: `%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%LOCALAPPDATA_LOW%`, `%DOCUMENTS%`, `%SAVEDGAMES%`
  или абсолютный путь на букве библиотеки (G:, H: — из настроек библиотек); длина ≤ 260; не корень профиля и не корень
  диска.
- Канал `\\.\pipe\club-diskless-helper`: доступ SYSTEM и BUILTIN\Administrators; одна строка JSON на запрос и ответ;
  коды ошибок `disabled`, `serverUnavailable`, `overLimit`, `notAttached`, `masterMode`, `profileNotFound`, `badRequest`,
  `internal`.
- Корень хранилища `Saves:Root` (`/srv/club/saves`), без файла-метки `.club-saves` — 503 `savesUnavailable`.
- Помощник — версия 1.7.0. Тексты панели — ru/uz/en. Вывод `.ps1` для `irm | iex` — только ASCII, без BOM.
- Dev box: `dotnet build`/`dotnet test` — по одному проекту за раз, никогда параллельно (и в агентах).
- Коммиты — только свои файлы (в рабочем дереве есть чужие правки: `SeatDisks.cs`, `SeatGames.cs`, `TrueNasStorage.cs`,
  их тесты, `TargetNameLocks.cs`, `offline-games/`, PDF — не трогать и не добавлять), с
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Ничего не выкатывать на ВМ, TrueNAS и ПК без отдельного ОК владельца (задача 12).

## Review Focus

1. Кириллица в именах папок и файлов (`H:\Игры\…`, `Сохранение 1.sav`) — сохраняется и раскладывается без искажений
   (тест в задаче 1).
2. `playerId` в разном регистре от шелла (`ABC…` / `abc…`) — одна и та же папка и аренда (тест в задачах 1 и 4).
3. Файлы только для чтения и скрытые в очищаемой папке — очистка их удаляет, а не падает (тест в задаче 8).
4. Папки из списка нет на ПК (игра ни разу не запускалась) — сохранение проходит, для этой записи просто нет файлов
   (тест в задаче 8).
5. Запись удалили из списка в панели, а в архиве игрока она ещё есть — выдача на ПК пропускает её, а не падает; сервер
   при приёме её не принимает (тесты в задачах 1, 4, 8).

---

### Task 1: Библиотека `Club.Saves` — шаблоны путей, ID игрока, архив

**Files:**
- Create: `src/Club.Saves/Club.Saves.csproj` (net10.0, как `Club.Helper.Core.csproj`), `src/Club.Saves/SavePathTemplate.cs`,
  `src/Club.Saves/PlayerIds.cs`, `src/Club.Saves/SaveArchive.cs`
- Modify: `ClubServer.sln` (`dotnet sln add`), `src/Club.Server/Club.Server.csproj` и
  `src/Club.Helper.Core/Club.Helper.Core.csproj` — ProjectReference на `Club.Saves`
- Test: `tests/Club.Server.Tests/SavesLibraryTests.cs`

**Interfaces:**
- Produces:
  - `enum SaveTargetKind { Profile, Library }`; `sealed record SaveTarget(SaveTargetKind Kind, string Anchor, string RelativePath)`
    — `Anchor`: имя переменной (`%APPDATA%`) или буква (`G`).
  - `static class SavePathTemplate`: `string? Validate(string? template, IReadOnlyCollection<char> libraryLetters)` (null —
    годится, иначе код: `empty`, `tooLong`, `badVariable`, `dotDot`, `drive`, `root`, `invalidChars`);
    `SaveTarget Parse(string template)` (только для прошедших `Validate`);
    `string ProfileSubpath(string variable)` (`%USERPROFILE%` → `""`, `%APPDATA%` → `AppData\Roaming`,
    `%LOCALAPPDATA%` → `AppData\Local`, `%LOCALAPPDATA_LOW%` → `AppData\LocalLow`, `%DOCUMENTS%` → `Documents`,
    `%SAVEDGAMES%` → `Saved Games`).
  - `static class PlayerIds`: `string? Normalize(string? value)` — нижний регистр или null, если не `[A-Za-z0-9-]{1,64}`.
  - `static class SaveArchive`: `const int MaxFiles = 50_000`;
    `SavePackResult Pack(Stream output, IEnumerable<(Guid EntryId, string Directory)> entries, IReadOnlyList<string> excludes)`;
    `SaveUnpackResult Unpack(Stream zip, Func<Guid, string?> targetForEntry, long limitBytes, bool unknownIsError)`;
    `bool IsExcluded(string relativePath, IReadOnlyList<string> excludes)`.
  - `sealed record SavePackResult(long Bytes, int Files)`; `sealed record SaveUnpackResult(long Bytes, int Files)`;
    `sealed class SaveArchiveException(string reason, string message) : Exception` с `string Reason` (`badArchive`, `overLimit`).

- [ ] **Step 1: Write the failing tests** — `SavesLibraryTests`:
  - `Validate_accepts_profile_and_library_paths`: `null` для `%LOCALAPPDATA%\GameName\Saved`,
    `%SAVEDGAMES%\Игра`, `H:\Игры\Saves` (буквы `['G','H']`).
  - `Validate_rejects`: `""`→`empty`, `%TEMP%\x`→`badVariable`, `%APPDATA%\..\x`→`dotDot`, `C:\Games\x`→`drive`,
    `%USERPROFILE%`→`root`, `H:\`→`root`, `H:\a|b`→`invalidChars`, строка 261 символ→`tooLong`.
  - `Parse_splits_anchor_and_relative`: `%LOCALAPPDATA%\GameName\Saved` → `(Profile, "%LOCALAPPDATA%", "GameName\Saved")`;
    `h:\Игра\Saves` → `(Library, "H", "Игра\Saves")`.
  - `PlayerIds_normalize`: `"ABC-1"`→`"abc-1"`; `"a/b"`, `""`, 65 символов → `null`.
  - `Pack_then_unpack_roundtrips_cyrillic_names`: папка с `Сохранение 1.sav` и `sub\data.bin`, `Logs\x.log` и `crash.dmp` —
    после `Pack`/`Unpack` есть первые два, нет двух исключённых; `Bytes`/`Files` совпадают.
  - `Unpack_rejects_traversal_and_absolute`: записи `../x`, `<id>/../../x`, `C:/x`, `<id>/con` → `SaveArchiveException`
    с `Reason == "badArchive"`.
  - `Unpack_over_limit`: файл 2 МБ при `limitBytes = 1 МБ` → `Reason == "overLimit"`, во временной папке ничего не
    осталось.
  - `Unpack_unknown_entry`: `targetForEntry` возвращает null для чужого id — при `unknownIsError: false` запись
    пропущена, при `true` → `badArchive`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Server.Tests --filter SavesLibraryTests` → не собирается.
- [ ] **Step 3: Implement** `Club.Saves` по Interfaces. Пути в архиве — через `/`, при распаковке — `Path.Combine` и
  проверка, что итог внутри цели (`Path.GetFullPath(...).StartsWith(target + separator)`). Сумма — по распакованным
  размерам по мере записи (не доверять заголовку). Исключения: сегмент пути равен имени папки из списка без `*`, или
  имя файла подходит под маску с `*` (без учёта регистра).
- [ ] **Step 4: Run, verify pass** — та же команда.
- [ ] **Step 5: Commit** — `Saves library: path templates, player ids, safe archive`.

### Task 2: Сервер — настройки, миграция, репозиторий

**Files:**
- Create: `src/Club.Server/Saves/SavesOptions.cs`, `src/Club.Server/Data/Migrations/M0012_PlayerSaves.cs`,
  `src/Club.Server/Saves/SavesRepository.cs`
- Modify: `src/Club.Server/Program.cs` (bind `Saves`, регистрация синглтонов)
- Test: `tests/Club.Server.Tests/SavesRepositoryTests.cs`; `MigrationTests` должен проходить без правок

**Interfaces:**
- Produces:
  - `sealed class SavesOptions { bool Enabled; string Root = "/srv/club/saves"; const string MarkerFile = ".club-saves"; }`
  - Миграция `[Migration(2026110501, "player saves")]`: `save_settings` (singleton, `limit_mb int` 1…2000 default 200,
    `excludes text[]` default `{Logs,Crashes,CrashDumps,*.dmp}`, строка по умолчанию вставляется), `save_entries`
    (`id uuid pk`, `name text` 1…60, `path text` ≤ 260, `enabled bool default true`, `created_at`), `player_saves`
    (`player_id text pk check '^[a-z0-9-]{1,64}$'`, `size_bytes bigint`, `file_count int`, `saved_at`, `machine_id uuid
    null references machines on delete set null`, `last_result text check in ('ok','overLimit','rejected','error')`,
    `last_result_at`, `last_error`), `save_leases` (`player_id text pk`, `machine_id uuid not null references machines on
    delete cascade`, `attached_at`). `Down` удаляет все четыре.
  - `sealed record SaveSettings(int LimitMb, IReadOnlyList<string> Excludes)`;
    `sealed record SaveEntry(Guid Id, string Name, string Path, bool Enabled)`;
    `sealed record PlayerSaveRow(string PlayerId, long SizeBytes, int FileCount, DateTimeOffset? SavedAt, Guid? MachineId,
    string? MachineName, string? LastResult, DateTimeOffset? LastResultAt, string? LastError)`.
  - `sealed class SavesRepository(NpgsqlDataSource db)`: `SettingsAsync()`, `UpdateSettingsAsync(SaveSettings)`,
    `EntriesAsync(bool enabledOnly)`, `CreateEntryAsync(string name, string path, bool enabled) → SaveEntry`,
    `UpdateEntryAsync(SaveEntry) → bool`, `DeleteEntryAsync(Guid) → bool`, `LockPlayerAsync(string playerId) →
    Task<IAsyncDisposable>` (`pg_advisory_lock(hashtext('save:'||id))` на своём соединении), `TakeLeaseAsync(string, Guid)`,
    `LeaseHolderAsync(string) → Guid?`, `ReleaseLeaseAsync(string, Guid)`, `RecordSavedAsync(string, Guid, long, int)` (и
    `last_result='ok'`), `RecordResultAsync(string, string result, string? error)`, `PlayersAsync(string? query, int page,
    int pageSize) → (IReadOnlyList<PlayerSaveRow>, int Total)` (поиск — подстрока ID; имя машины — из `machines`),
    `DeletePlayerAsync(string)` (строка и аренда).

- [ ] **Step 1: Write the failing tests** — `SavesRepositoryTests` (на `ServerFixture`):
  `Defaults_after_migration` (200, четыре исключения), `Entries_crud`, `Lease_take_over_and_release` (машина B забирает
  аренду у A; `ReleaseLeaseAsync(id, A)` её не снимает), `Lock_serializes_same_player` (второй `LockPlayerAsync` того же
  ID ждёт освобождения первого, другого ID — нет), `Players_paging_and_search`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Server.Tests --filter SavesRepositoryTests`.
- [ ] **Step 3: Implement** по Interfaces; SQL — как в `ZoneRepository` (Dapper, `DapperSetup.Ensure()`).
- [ ] **Step 4: Run, verify pass** — та же команда и `--filter MigrationTests`.
- [ ] **Step 5: Commit** — `Server: player saves tables and repository`.

### Task 3: Сервер — файловое хранилище `SaveStore`

**Files:**
- Create: `src/Club.Server/Saves/SaveStore.cs`
- Modify: `src/Club.Server/Program.cs` (синглтон; `CleanTemp()` при старте, если `Available`)
- Test: `tests/Club.Server.Tests/SaveStoreTests.cs` (временный каталог, без базы)

**Interfaces:**
- Consumes: `SavesOptions`, `SaveArchive` (задача 1).
- Produces: `sealed class SaveStore(SavesOptions options, ILogger<SaveStore> logger)`:
  `bool Available` (корень и `.club-saves` есть); `string? CurrentDirectory(string playerId)` (`<id>`, иначе
  `<id>.prev`, иначе null); `string NewTempDirectory()` (`.tmp/<guid>`); `void Promote(string playerId, string tempDirectory)`
  (удалить `<id>.prev`; `<id>` → `<id>.prev`, если есть; `temp` → `<id>`); `void Delete(string playerId)` (обе папки);
  `void CleanTemp()`; `(long Bytes, int Files) Measure(string directory)`; `void WriteZip(string directory, Stream output)`
  (каждая подпапка — `entryId`, через `SaveArchive.Pack` без исключений).

- [ ] **Step 1: Write the failing tests** — `Unavailable_without_marker`, `Promote_keeps_previous` (два `Promote` →
  текущая = вторая, `.prev` = первая, третьей нет), `Current_falls_back_to_prev` (удалить `<id>` вручную → вернёт
  `.prev`), `Delete_removes_both`, `CleanTemp_empties_tmp`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Server.Tests --filter SaveStoreTests`.
- [ ] **Step 3: Implement** по Interfaces (`Directory.Move` в пределах корня).
- [ ] **Step 4: Run, verify pass**.
- [ ] **Step 5: Commit** — `Server: saves file store with previous copy`.

### Task 4: Сервер — API помощника

**Files:**
- Create: `src/Club.Server/Saves/SavesEndpoints.cs`
- Modify: `src/Club.Server/Program.cs` (`app.MapSavesEndpoints()`), `docs/diskless-api.yaml` (пути и схемы
  `SavesConfig`, `SaveEntryConfig`), `docs/diskless-api.json` (`scripts/build-spec.sh`)
- Test: `tests/Club.Server.Tests/SavesApiTests.cs` (`ServerFixture` с `Settings["Saves:Enabled"]="true"`,
  `Settings["Saves:Root"]=<temp с .club-saves>`; `TestMachine`)

**Interfaces:**
- Consumes: задачи 1–3.
- Produces (под `MachineAuthMiddleware.Prefix`, всё с `RequireSelf` — как в `DisklessEndpoints`):
  - `sealed record SavesConfig(bool Enabled, int LimitMb, IReadOnlyList<string> Excludes, IReadOnlyList<SaveEntryConfig> Entries)`;
    `sealed record SaveEntryConfig(Guid Id, string Path)`; `const string ConfigHeader = "X-Club-Saves-Config"` (base64 от
    UTF-8 JSON `SavesConfig` в `ApiJson.Options`).
  - `GET /machines/{id}/saves/config` → `SavesConfig` (только включённые записи; `Enabled` = `options.Enabled && store.Available`).
  - `POST /machines/{id}/saves/{playerId}/attach` → под `LockPlayerAsync`: `TakeLeaseAsync`; `200 application/zip`
    (`store.WriteZip(CurrentDirectory)`) или `204`; заголовок `ConfigHeader` в обоих случаях.
  - `PUT /machines/{id}/saves/{playerId}` → под блокировкой: аренда не у этой машины — `409` `conflict`
    `{reason:"notAttached"}` + `RecordResultAsync(rejected)`; поднять `IHttpMaxRequestBodySizeFeature` до
    `limitMb * 1.1`; тело во временный файл; `SaveArchive.Unpack(..., unknownIsError: true)` во `NewTempDirectory()`;
    `413` `validation` `{reason:"overLimit"}` (+ запись результата, аренда снимается) / `400` `validation`
    `{reason:"badArchive"}`; `Promote`, `RecordSavedAsync`, `ReleaseLeaseAsync` → `204`.
  - `DELETE /machines/{id}/saves/{playerId}/lease` → `ReleaseLeaseAsync` → `204`.
  - Ошибки для всех: выключено или `!store.Available` — `503` `serverUnavailable` `{reason:"savesUnavailable"}` (кроме
    `config`); `playerId` не проходит `PlayerIds.Normalize` — `400` `validation`.

- [ ] **Step 1: Write the failing tests** — `SavesApiTests`:
  `Config_lists_enabled_entries`, `Attach_without_data_is_204_with_config_header`,
  `Save_then_attach_on_other_machine_returns_files` (A: attach→PUT; B: attach → zip с теми же файлами),
  `Put_without_lease_is_409_and_recorded`, `Put_after_takeover_is_409` (A attach, B attach, A PUT → 409),
  `Put_over_limit_is_413_previous_kept` (лимит 1 МБ через репозиторий; прошлая версия на месте),
  `Put_bad_archive_is_400`, `Put_unknown_entry_is_400`, `Player_id_case_is_one_folder` (attach `ABC…`, PUT `abc…` → 204),
  `Unavailable_without_marker_is_503`, `Other_machine_token_is_403`. Тела JSON сверять со схемой (`SpecSchema` из
  `TestHelpers.cs`).
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Server.Tests --filter SavesApiTests`.
- [ ] **Step 3: Implement** `MapSavesEndpoints` по Interfaces; спецификацию — в `diskless-api.yaml`, затем
  `bash scripts/build-spec.sh`.
- [ ] **Step 4: Run, verify pass**.
- [ ] **Step 5: Commit** — `Server: saves API for the helper (attach, save, lease)`.

### Task 5: Сервер — API панели

**Files:**
- Create: `src/Club.Server/Panel/SavesPanelEndpoints.cs`
- Modify: `src/Club.Server/Program.cs` (`app.MapSavesPanelEndpoints()`), `docs/panel-api.md` (раздел «Сохранения игр»)
- Test: `tests/Club.Server.Tests/SavesPanelTests.cs`

**Interfaces:**
- Consumes: задачи 1–3; буквы библиотек — `LibraryOptions.DriveLetter` и буквы из `LibrarySet` (найти свойство с буквой
  каждой библиотеки в `LibrarySet`/`LibraryOptions`).
- Produces (под `PanelAuthMiddleware.Prefix + "/v1"`):
  `GET/PUT /saves/settings` (`SaveSettingsRequest(int? LimitMb, IReadOnlyList<string>? Excludes)`; 1…2000; исключения —
  до 50 строк по 1…100 символов), `GET/POST /saves/entries`, `PUT/DELETE /saves/entries/{id}`
  (`SaveEntryRequest(string? Name, string? Path, bool? Enabled)`; путь — `SavePathTemplate.Validate`, ошибка —
  `400 validation {field:"path", reason:<код>}`), `GET /saves/players?query=&page=` (`{items, total, pageSize:50}`),
  `DELETE /saves/players/{playerId}` (под `LockPlayerAsync`: `store.Delete`, `DeletePlayerAsync`), и
  `GET /saves/status` → `{enabled, available, root, usedBytes}`.
- [ ] **Step 1: Write the failing tests** — `Settings_roundtrip`, `Entry_path_validation_codes` (`C:\x` → `drive`),
  `Entries_crud`, `Players_list_after_save` (через API помощника из задачи 4), `Delete_player_removes_files_and_lease`,
  `Requires_panel_token`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Server.Tests --filter SavesPanelTests`.
- [ ] **Step 3: Implement**; раздел в `docs/panel-api.md` — по образцу раздела «Сеть».
- [ ] **Step 4: Run, verify pass**.
- [ ] **Step 5: Commit** — `Panel API: saves settings, folders, players`.

### Task 6: Панель — вкладка «Сохранения»

**Files:**
- Modify: `src/Club.Server/wwwroot/panel/index.html` (ссылка `data-view="saves"` и `<section>`), `app.js` (`loadSaves`,
  `saveSavesSettings`, CRUD записей, таблица игроков с поиском и страницами, удаление с `confirm`), `i18n.js` (ключи
  `saves.*` в ru, uz, en)
- Test: `tests/Club.Server.Tests/SavesPanelTests.cs` — `Panel_page_has_saves_view` (GET `/panel/` содержит
  `data-view="saves"`); ручная проверка — шаг 4

- [ ] **Step 1: Write the failing test** `Panel_page_has_saves_view`.
- [ ] **Step 2: Run, verify fail**.
- [ ] **Step 3: Implement** по образцу вкладки «Сеть» (`index.html:341-380`, `app.js` `loadSettings`/`saveNetwork`):
  карточки «Состояние» (`/saves/status`: включено, диск подключён, занято), «Настройки», «Папки» (подсказка — примеры
  `%LOCALAPPDATA%\GameName\Saved`, `%SAVEDGAMES%\…`, `H:\Игра\Saves`; ошибки пути — по коду из ответа), «Игроки».
- [ ] **Step 4: Verify** — `node --check src/Club.Server/wwwroot/panel/app.js`; `dotnet test ... --filter SavesPanelTests`;
  запустить сервер локально (`dotnet run --project src/Club.Server` с тестовой базой и `Saves__Root` во временном каталоге)
  и пройти вкладку во встроенном браузере: добавить папку, увидеть ошибку для `C:\x`, сменить лимит.
- [ ] **Step 5: Commit** — `Panel: saves tab`.

### Task 7: Помощник — методы API сохранений

**Files:**
- Modify: `src/Club.Helper.Core/DisklessApiClient.cs`
- Test: `tests/Club.Helper.Tests/SavesClientTests.cs` (настоящий сервер в процессе, как `HelperEndToEndTests`)

**Interfaces:**
- Consumes: API задачи 4.
- Produces:
  - Конструктор `DisklessApiClient(HttpClient http, HelperOptions options, ICredentialStore store, IMachineIdentity identity,
    HttpClient? transfers = null)` — `transfers` (или `http`, если null) для attach и PUT.
  - Записи в Core: `SavesConfig`, `SaveEntryConfig` (те же поля, что на сервере); `sealed class SavesAttach(SavesConfig Config,
    Stream? Archive) : IAsyncDisposable`; `sealed class SavesRejectedException(string reason) : Exception` (`Reason`:
    `notAttached`, `overLimit`, `badArchive`, `disabled`).
  - `Task<SavesConfig> GetSavesConfigAsync(CancellationToken ct)`; `Task<SavesAttach> AttachSavesAsync(string playerId,
    CancellationToken ct)`; `Task PutSavesAsync(string playerId, Stream archive, CancellationToken ct)`;
    `Task ReleaseSavesLeaseAsync(string playerId, CancellationToken ct)`.
  - Ошибки: 409 → `notAttached`, 413 → `overLimit`, 400 → `badArchive`, 503 с `reason=savesUnavailable` → `disabled`; сеть
    и прочие коды → `ServerUnavailableException` (как сейчас). Для этого — приватная перегрузка `SendAuthorizedAsync`,
    которая отдаёт ответ без исключения на не-2xx.
- [ ] **Step 1: Write the failing tests** — `Attach_put_attach_roundtrip`, `Put_without_attach_is_notAttached`,
  `Disabled_server_is_disabled`, `Config_header_with_cyrillic_path`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Helper.Tests --filter SavesClientTests`.
- [ ] **Step 3: Implement** по Interfaces.
- [ ] **Step 4: Run, verify pass**; затем весь `tests/Club.Helper.Tests` (прежние тесты не сломаны).
- [ ] **Step 5: Commit** — `Helper: saves API client`.

### Task 8: Помощник — `SavesService` (выдача, сохранение, исходное содержимое G:/H:)

**Files:**
- Create: `src/Club.Helper.Core/SavesService.cs`, `src/Club.Helper.Core/SavesAbstractions.cs`
- Test: `tests/Club.Helper.Tests/SavesServiceTests.cs` (подделки `IProfileLocator`/`IDriveRoots` на временные каталоги,
  настоящий сервер в процессе)

**Interfaces:**
- Consumes: задачи 1, 7.
- Produces:
  - `interface IProfileLocator { string? ProfilePath(string sid); }`; `interface IDriveRoots { string RootOf(char letter); }`
  - `sealed record SavesResult(bool Ok, string? Error = null, string? Message = null)`; `static class SavesErrors` с
    константами восьми кодов из Global Constraints.
  - `sealed record LibraryMount(char Letter, string? Version, bool Mounted)`.
  - `sealed class SavesService(DisklessApiClient api, IProfileLocator profiles, IDriveRoots drives, Func<bool> masterActive,
    string stateDirectory, TimeProvider clock, ILogger logger)`:
    `Task<SavesResult> AttachAsync(string playerId, string windowsUserSid, CancellationToken ct)`,
    `Task<SavesResult> DetachAsync(string playerId, CancellationToken ct)`,
    `Task OnTickAsync(IReadOnlyList<LibraryMount> libraries, CancellationToken ct)` (конфигурация раз в 5 мин; исходное
    содержимое — в `<stateDirectory>\saves-baseline\<entryId>.zip` для записей библиотек на подключённой букве, один раз на
    `(entryId, Version)`, только когда никому не выдано; больше лимита — пропуск с предупреждением).
  - Порядок attach и detach — по spec п. 4.3: проверка мастера → профиль по SID → сервер → очистка (снять
    `ReadOnly`/`Hidden` перед удалением) → исходное содержимое для G:/H: → распаковка (`unknownIsError: false`) →
    «выдано»; ошибка после ответа сервера → `ReleaseSavesLeaseAsync` и `internal`. Detach: не «выдано» этому ID →
    `notAttached`; упаковка во временный файл `<stateDirectory>\saves-tmp\`; занятые файлы — 3 попытки через 1 с;
    больше лимита → снять аренду, `overLimit`; `PutSavesAsync`; забыть «выдано». Каждая команда — строка журнала
    (ID игрока, размер, время, результат), spec п. 4.5.
- [ ] **Step 1: Write the failing tests** — `SavesServiceTests`:
  `Attach_restores_player_files_into_profile_and_library` (две машины, два профиля: сохранить на A, выдать на B),
  `Attach_clears_previous_player_files` (в папке файлы прошлого игрока, у нового ничего нет → папка пустая; для G:/H: —
  исходное содержимое), `Clear_removes_readonly_and_hidden`, `Missing_folder_saves_fine`,
  `Unknown_entry_in_archive_is_skipped`, `Master_active_refuses`, `Unknown_sid_is_profileNotFound`,
  `Detach_without_attach_sends_nothing` (на сервере прошлая версия не тронута), `Server_down_at_attach_then_detach_is_notAttached`,
  `Detach_over_limit_keeps_server_copy`, `Baseline_taken_once_per_library_version`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Helper.Tests --filter SavesServiceTests`.
- [ ] **Step 3: Implement** по Interfaces.
- [ ] **Step 4: Run, verify pass**.
- [ ] **Step 5: Commit** — `Helper: saves service (attach, detach, library baselines)`.

### Task 9: Помощник — протокол канала

**Files:**
- Create: `src/Club.Helper.Core/SavesCommandHandler.cs`
- Test: `tests/Club.Helper.Tests/SavesCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `SavesService` (задача 8).
- Produces: `sealed class SavesCommandHandler(SavesService saves, string helperVersion)`:
  `Task<string> HandleAsync(string requestLine, CancellationToken ct)` — одна строка JSON без `\n`; команды выполняются по
  одной (`SemaphoreSlim`); `ping` → `{"ok":true,"version":"1.7.0"}`; неизвестная команда, пустые поля, длиннее 4096 —
  `badRequest`; неизвестные поля игнорируются; `playerId` — через `PlayerIds.Normalize`.
- [ ] **Step 1: Write the failing tests** — `Ping`, `Attach_and_detach_dispatch`, `Bad_json_is_badRequest`,
  `Unknown_fields_ignored` (`rememberLaunchers`), `Commands_run_one_at_a_time`.
- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Club.Helper.Tests --filter SavesCommandHandlerTests`.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run, verify pass**.
- [ ] **Step 5: Commit** — `Helper: pipe command protocol`.

### Task 10: Помощник — Windows: канал, профиль по SID, подключение, версия 1.7.0

**Files:**
- Create: `src/Club.Helper/SavesPipeServer.cs`, `src/Club.Helper/WindowsSaves.cs` (`WindowsProfileLocator`,
  `WindowsDriveRoots`)
- Modify: `src/Club.Helper/Program.cs` (второй `HttpClient` с `Timeout = 130 s` для передач; `SavesService`,
  `SavesCommandHandler`, `AddHostedService<SavesPipeServer>()`), `src/Club.Helper/HelperService.cs` (после каждого
  `TickAsync` — `saves.OnTickAsync(...)` из `loops.Games`/`loops.Libraries`: буква и версия из `LastReport`, подключено —
  `State == "mounted"`; `masterActive` — любой `LastMasterReport?.State` = `mounted` или `mounting`),
  `src/Club.Helper/Club.Helper.csproj` (`<Version>1.7.0</Version>` и строка истории), `docs/helper.md` (раздел «Сохранения
  игр»)
- Test: сборка `dotnet build src/Club.Helper` (Windows-код без тестов на Linux; логика проверена в задачах 8–9)

**Interfaces:**
- Consumes: задачи 7–9.
- Produces: `SavesPipeServer : BackgroundService` — `NamedPipeServerStreamAcl.Create("club-diskless-helper",
  PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security)` с правами FullControl
  для `WellKnownSidType.LocalSystemSid` и `BuiltinAdministratorsSid`; на подключение — прочитать строку (≤ 4096 байт),
  `HandleAsync` с токеном службы (обрыв клиента не прерывает начатую команду), записать ответ и `\n`.
  `WindowsProfileLocator` — `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\<sid>\ProfileImagePath`
  через `Environment.ExpandEnvironmentVariables`; `WindowsDriveRoots.RootOf(l)` → `$"{l}:\\"`.
- [ ] **Step 1: Implement** по Interfaces.
- [ ] **Step 2: Verify** — `dotnet build src/Club.Helper` без предупреждений; `dotnet test tests/Club.Helper.Tests` целиком.
- [ ] **Step 3: Commit** — `Helper 1.7.0: saves pipe for the shell`.

### Task 11: Тестовая команда для ПК и заметки для шелла

**Files:**
- Create: `scripts/golden/saves-test.ps1`
- Modify: `/home/deep/projects/club-contracts/docs/SHELL_CHANGES.md` (п. 23 — переписать по spec п. 5, P0; ветка
  `shell-changes-2026-10-02`), `docs/diskless-full.md` (§7 — ссылка на спецификацию)
- Test: `tests/Club.Server.Tests/DeployScriptTests.cs` — добавить `saves-test.ps1` в проверку ASCII/без BOM, если тест
  перечисляет скрипты (посмотреть, как он устроен)

**Interfaces:**
- Produces: `saves-test.ps1 -Op ping|attach|detach [-PlayerId <id>] [-Sid <sid>]` — `-Sid` по умолчанию — SID
  пользователя `player`; `NamedPipeClientStream('.', 'club-diskless-helper', InOut)`, тайм-аут подключения 5 с, одна
  строка туда и обратно, ответ печатает как есть; требует администратора.
- [ ] **Step 1: Implement** скрипт и тексты.
- [ ] **Step 2: Verify** — `DeployScriptTests` проходят; п. 23 в SHELL_CHANGES совпадает с протоколом задачи 9.
- [ ] **Step 3: Commit** в обоих репозиториях; push `stand-2026-10-02` и `shell-changes-2026-10-02`.

### Task 12: Выкатка (каждый пункт — СТОП до ОК владельца)

- [ ] **Диск:** zvol `games/club/saves` 250 ГиБ sparse; iSCSI-таргет для ВМ как у `games/club/images` (найти в
  `scripts/server/` или в истории, как подключён диск образов); ext4, `/srv/club/saves` в fstab, владелец — пользователь
  службы; `touch /srv/club/saves/.club-saves`; задача репликации в `backup` по образцу задач 1–4.
- [ ] **Сервер:** сборка, деплой на ВМ, как прошлые выкатки (резервная копия базы перед миграцией),
  `Saves__Enabled=true`, `Saves__Root=/srv/club/saves`; в панели — проверить вкладку.
- [ ] **Помощник:** сборка 1.7.0, файлы в `/srv/club/pxe/tools`, хэши в `install-helper.ps1`/`update-helper-net.ps1`;
  PC-22: обновить, `saves-test.ps1` — ping, attach/detach двух ID, проверка папок; затем бездисковый ПК через эталон.
- [ ] Обновить память (`memory/`) по итогам.
