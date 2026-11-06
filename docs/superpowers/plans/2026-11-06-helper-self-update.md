# Самообновление помощника (1.8.0) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** помощник на ПК по кнопке в панели скачивает новую сборку с сервера клуба, сверяет SHA256, заменяет себя между
сеансами и сам откатывается, если новая сборка не работает; сервер раздаёт поручения не больше чем трём ПК сразу и
ставит очередь на паузу при первой ошибке.

**Architecture:** сервер: таблица `helper_updates`, очередь `HelperUpdateRepository` (всё под одной advisory-блокировкой),
список сборок `HelperBuilds` из `tools`, поле `helperUpdate` в ответе на отчёт G:, фоновый `HelperUpdateWorker`, API и
карточка панели. Помощник: логика в Core (`UpdateStateStore`, `UpdateGuard`, `SelfUpdater` — проверяются на временных
папках и подделке службы), Windows-часть — каркас процесса в `Program.cs` (страж первым, любой ненормальный конец хоста —
завершение процесса), `WindowsServiceControl`, наследник `WindowsServiceLifetime`.

**Tech Stack:** C#/.NET 10, ASP.NET Core minimal API, PostgreSQL + Dapper + FluentMigrator, xunit, Generic Host +
Microsoft.Extensions.Hosting.WindowsServices, статическая панель на vanilla JS, PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-06-helper-self-update-design.md` (ред. 2)

## Global Constraints

- Цель кнопки — только сборка ≥ 1.8.0; имя файла строго `^ClubDisklessHelper-\d+\.\d+\.\d+\.exe$`; файл моложе 60 с не
  берётся; `path` в поручении — `pxe/v1/files/tools/<имя>`; размер ≤ 300 МБ; SHA256 — 64 hex строчными.
- Очередь: `HelperUpdates:MaxActive` = 3; ПК молчит 10 мин → `queued`; `waiting` > 30 мин → `queued`; после
  `applying`/`confirming` 10 мин без прогресса и без `confirmed` → `failed`/`noReturn`; активно без прогресса 10 мин при
  живых отчётах → `failed`/`noProgress`; пауза на любой ошибке, кроме `helperTooOld`, `diskless`, отмены.
- Порядок отказов при запуске: `unknown`, `notApproved`, `diskless`, `helperTooOld`, `targetTooOld`, `busy`, `current`.
- Новые поля отчёта (`helperBuild`, `helperUpdate.*`) — строки; незнакомое значение — журнал и пропуск, **никогда 400**.
- Помощник: `.new`, `.old`, `.failed` — в `%ProgramFiles%\ClubDiskless\`; `update.json` (`v:1`) и `update-failed.json`
  (последние 20) — в `%ProgramData%\ClubDiskless\`; фазы `prepared`, `swapped`, `rolledBack`.
- Подтверждение: 10 минут работы, ≥ 5 принятых отчётов G:, без падений. Попытка считается, только если прошлый запуск
  этой сборки не оставил «чистый выход»; больше 3 попыток — откат.
- Завершение процесса для замены и при ненормальном конце хоста — `Process.GetCurrentProcess().Kill()`, не
  `Environment.Exit` и не остановка хоста.
- Действия при сбое службы: `restart/5000/restart/5000/restart/30000`, сброс 86400.
- Коды: `downloadFailed`, `hashMismatch`, `noRecovery`, `swapFailed` (`restoreFailed` в тексте), `startFailed`,
  `noRollback`, `noReturn`, `noProgress`, `buildChanged`, `buildMissing`, `helperTooOld`, `diskless`; канал — `updating`.
- Помощник — версия 1.8.0. Тексты панели — ru/uz (латиница)/en. Вывод `.ps1` для `irm | iex` — только ASCII, без BOM.
- Dev box: `dotnet build`/`dotnet test` — по одному проекту, никогда параллельно (и в агентах).
- Git: только `git add <явные пути>`; никогда `-a`, `-A`, `.` (в дереве чужие правки: `SeatDisks.cs`, `SeatGames.cs`,
  `TrueNasStorage.cs`, их тесты, `TargetNameLocks.cs`, `offline-games/`, PDF); после коммита — `git show --stat HEAD`.
  Подпись `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Ничего не выкатывать на ВМ, TrueNAS и ПК без отдельного ОК владельца (задачи 0 и 12).

## Review Focus

1. Несколько ПК отчитываются в одну секунду при 2 активных — активных становится ровно 3, не больше (тест в задаче 3).
2. Сервер недоступен, ПК перезагружают трижды во время проверки новой сборки — отката нет, подтверждение после
   возврата сервера (тест в задаче 7).
3. Отмена, пока ПК ждёт конца сеанса со скачанным `.new` — `.new` стёрт, замены нет (тест в задаче 8).
4. Отчёт с незнакомыми `helperUpdate.state`/`error` или кривым `helperBuild` — 200, отчёт записан (тест в задаче 3).
5. Питание пропало после записи `update.json` (`prepared`), до переименований — старая сборка молча стирает `.new`,
   ошибки и паузы нет (тест в задаче 7).

---

### Task 0: Шаг 0 на PC-22 — проверка основы (СТОП до ОК владельца на запуск на ПК)

**Files:**
- Create (не в репозиторий, scratchpad): одноразовый проект `ClubUpdateSpike` (net10.0-windows, single-file, как
  помощник) и `spike-install.ps1`/`spike-uninstall.ps1` (ASCII)

Отдельная служба `ClubUpdateSpike` (настоящий помощник не трогается). Команды — файлы-флаги в
`%ProgramData%\ClubUpdateSpike\`; журнал `spike.log` (время, PID, событие).

- [ ] **Step 1:** сервис на Generic Host + `AddWindowsService`, свой наследник `WindowsServiceLifetime` (как в задаче 9),
  `sc failure … restart/5000/restart/5000/restart/30000`. Флаги: `kill.flag` → запись в журнал и `Kill()`;
  `throw.flag` → исключение в `BackgroundService` (обработчик `ApplicationStopping` без команды Windows → `Kill()`);
  `swap.flag` → копия своего exe в `.new`, переименования работающего `exe → .old`, `.new → exe`, `Kill()`;
  `rollback.flag` → в начале `Main` переименования назад и минимальный хост, `Kill()` после `Running`.
- [ ] **Step 2:** собрать, положить в `/var/tmp` на ВМ (не в `tools`); инструкция владельцу: установка, каждый флаг,
  чтение `spike.log` и событий 7031/7034 (`Get-WinEvent -FilterHashtable @{LogName='System'; Id=7031,7034}`).
- [ ] **Step 3 (ПК, после ОК):** ожидаемо — после каждого флага служба снова `Running` через 5 с (третий сбой за сутки —
  30 с), PID новый, `swap` → стартовал «новый» exe, `rollback` → «старый». Не так — СТОП, пересмотреть спецификацию.
- [ ] **Step 4:** удалить службу и файлы с ПК; итог записать в `docs/helper.md` (раздел самообновления, задача 10).

### Task 1: Сервер — сборки, настройки, миграция

**Files:**
- Create: `src/Club.Server/HelperUpdates/HelperUpdateOptions.cs`, `src/Club.Server/HelperUpdates/HelperBuilds.cs`,
  `src/Club.Server/Data/Migrations/M0013_HelperUpdates.cs`
- Modify: `src/Club.Server/Program.cs` (секция `HelperUpdates`, регистрация), `deploy/club-server.env.example`
- Test: `tests/Club.Server.Tests/HelperBuildsTests.cs`

**Interfaces:**
- Produces: `HelperUpdateOptions { int MaxActive = 3; string? ToolsDir; int WorkerIntervalSec = 30; int SilentMinutes = 10;
  int WaitingMinutes = 30; int MinFileAgeSec = 60; string MinTargetVersion = "1.8.0" }` (секция `HelperUpdates`;
  `ToolsDir` по умолчанию `Path.Combine(Imaging:PxeRoot, "tools")`).
- Produces: `record HelperBuild(string File, string Version, long Size, string Sha256, DateTimeOffset ModifiedAt, bool Updatable)`;
  `class HelperBuilds(HelperUpdateOptions, ImagingOptions, TimeProvider)`: `IReadOnlyList<HelperBuild> List()`,
  `HelperBuild? Find(string file)` (null — нет файла, имя не по шаблону или файл моложе `MinFileAgeSec`).
- Produces: миграция `[Migration(2026110601)] M0013_HelperUpdates`: `machines.helper_build text NULL`; таблица
  `helper_updates` ровно по spec п. 3.2 с CHECK на `state`, `progress`, `reason`; частичный уникальный индекс
  `helper_updates_open (machine_id) WHERE state IN ('queued','paused','active')`.

- [ ] **Step 1: тесты** `List_TakesOnlyNamePattern_AndComputesSha` (файлы `ClubDisklessHelper-1.8.0.exe`,
  `ClubDisklessHelper-1.8.0.exe.tmp`, `install-helper-1.8.0.ps1` → в списке одна сборка, `Sha256` = SHA256 содержимого
  строчными, `Size` верный); `Updatable_FalseBelow180` (1.7.0 — `Updatable=false`); `Find_IgnoresFileYoungerThanMinAge`;
  `List_RecomputesShaWhenFileChanges` (перезапись → новый SHA); `Migration_CreatesHelperUpdates` (вставка второго открытого
  поручения на ПК — ошибка уникальности; `state='bogus'` — ошибка CHECK).
- [ ] **Step 2:** `dotnet test tests/Club.Server.Tests --filter HelperBuilds` — FAIL.
- [ ] **Step 3:** реализовать; кэш SHA по (размер, время изменения); версия — `Version.TryParse` из имени.
- [ ] **Step 4:** тесты — PASS. Затем весь `tests/Club.Server.Tests` один раз.
- [ ] **Step 5:** коммит «Server: helper builds list, update settings and table».

### Task 2: Сервер — очередь поручений (репозиторий)

**Files:**
- Create: `src/Club.Server/HelperUpdates/HelperUpdateRepository.cs`, `src/Club.Server/HelperUpdates/HelperUpdateDtos.cs`
- Test: `tests/Club.Server.Tests/HelperUpdateQueueTests.cs`

**Interfaces:**
- Consumes: `HelperBuilds`, `HelperUpdateOptions` (задача 1), `MachineRepository`, `DisklessRepository`, `LibraryOptions.SupportsHelper`
  (разбор версии).
- Produces DTO (сервер): `record HelperUpdateOffer(Guid Id, string Version, string Sha256, long Size, string Path)`;
  `record HelperUpdateReport(string? Id, string? State, string? Reason, string? Error, string? Message)` (все строки);
  `record LaunchRefusal(int Number, string Reason)`; `record LaunchResult(IReadOnlyList<int> Queued, bool Paused, IReadOnlyList<LaunchRefusal> Refused)`;
  `record HelperUpdateRow(Guid Id, Guid MachineId, int Number, string File, string Version, string Sha256, string State, string? Progress, string? Reason, string? Error, string? Message, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? FinishedAt)`.
- Produces `HelperUpdateRepository`:
  - `Task<LaunchResult> LaunchAsync(string file, IReadOnlyList<int>? numbers, bool allHybrids, string requestedBy, DateTimeOffset now)`;
  - `Task<HelperUpdateOffer?> OnReportAsync(Guid machineId, string helperVersion, string? helperBuild, HelperUpdateReport? report, DateTimeOffset now)` —
    шаги 2–5 spec п. 3.3 в одной транзакции под `pg_advisory_xact_lock(hashtext('helper-updates'))`;
  - `Task SweepAsync(DateTimeOffset now)` — правила работника spec п. 3.3 и проверка файлов сборок;
  - `Task<int> ResumeAsync(DateTimeOffset now)`, `Task<int> CancelAllAsync(DateTimeOffset now)`, `Task<bool> CancelAsync(Guid id, DateTimeOffset now)`;
  - `Task<IReadOnlyList<HelperUpdateRow>> ListAsync(DateTimeOffset now)` (незавершённые + завершённые за сутки);
  - `Task<string?> IneligibleReasonAsync(Guid machineId)` — `notApproved` | `diskless` | `helperTooOld` | null; мастер
    эталона — по spec п. 3.3 (`master_machine_id`, `seat_disks.kind='master'`, `last_boot_at` после
    `diskless_image.updated_at` установки режима мастера).

- [ ] **Step 1: тесты** (ServerFixture, временная база): `Launch_RefusalOrder` (по ПК на каждую причину → ровно коды в
  порядке Global Constraints); `Launch_DuringPause_CreatesPaused`; `Report_PromotesOnlyOwnQueued_UpToMaxActive`;
  `Report_Confirmed_MarksDone`; `Report_HelperBuildEqualsTarget_DoneWithoutOffer` (queued/paused/active без прогресса);
  `Report_Failed_PausesQueue_ExceptHelperTooOld`; `Report_OldHelperOnActive_FailedHelperTooOld_NoPause`;
  `Offer_OnlyForActive_AndEligibleNow` (мастер эталона после закрытия сеанса → `cancelled`/`diskless`, без поручения);
  `Sweep_SilentDownloading_Requeued`; `Sweep_Waiting30Min_Requeued`; `Sweep_ApplyingSilent_NoReturnAndPause`;
  `Sweep_ActiveNoProgressWhileReporting_NoProgressAndPause`; `Sweep_BuildReplaced_BuildChangedAllOpen`;
  `Sweep_BuildDeleted_BuildMissing`; `Resume_PausedToQueued`; `Cancel_All_And_One`.
- [ ] **Step 2:** `--filter HelperUpdateQueue` — FAIL.
- [ ] **Step 3:** реализовать. Время — только из параметра `now`; «ПК молчит» — `machines.last_seen_at`; «без прогресса» —
  `progress_at`.
- [ ] **Step 4:** PASS; весь проект один раз.
- [ ] **Step 5:** коммит «Server: helper update queue».

### Task 3: Сервер — отчёт G:, регистрация, работник, описание API

**Files:**
- Modify: `src/Club.Server/Diskless/DisklessEndpoints.cs` (`MachineStatus` + `HelperBuild`, `HelperUpdate` в конце;
  `StatusAccepted` + `HelperUpdate` в конце; регистрация + `HelperBuild`), `src/Club.Server/Data/MachineRepository.cs`
  (`helper_build` в `RecordStatusAsync`, `CreateAsync`, `AdoptAsync`, `ReregisterAsync` — без COALESCE),
  `src/Club.Server/Program.cs`, `docs/diskless-api.yaml`
- Create: `src/Club.Server/HelperUpdates/HelperUpdateWorker.cs`
- Test: `tests/Club.Server.Tests/HelperUpdateApiTests.cs`

**Interfaces:**
- Consumes: `HelperUpdateRepository.OnReportAsync`, `SweepAsync` (задача 2).
- Produces: в `StatusAsync` после `RecordStatusAsync` — `OnReportAsync(...)`, результат — в
  `StatusAccepted(..., HelperUpdate: offer)`; `helperBuild` нормализуется (`Trim().ToLowerInvariant()`, 64 hex — иначе
  null с записью в журнал). `HelperUpdateWorker : BackgroundService` — `SweepAsync` раз в `WorkerIntervalSec`, ошибка —
  журнал и следующий проход.

- [ ] **Step 1: тесты:** `Status_WithoutUpdate_NoHelperUpdateField` (JSON ответа без ключа `helperUpdate`);
  `Status_ActiveUpdate_OfferInReply` (поля `id`, `version`, `sha256`, `size`, `path`); `Status_UnknownValues_Return200`
  (`state:"weird"`, `error:"x"`, `id:"not-guid"`, `helperBuild:"zz"` → 200, `helper_build` null);
  `Status_HelperBuildCleared_WhenFieldAbsent`; `Register_StoresHelperBuild`; `Status_ConcurrentReports_MaxActiveHeld`
  (10 ПК параллельно при 2 активных → активных 3); существующие проверки провода 1.5.1 не меняются и зелёные.
- [ ] **Step 2:** FAIL. **Step 3:** реализовать; `docs/diskless-api.yaml` — схемы `HelperUpdateOffer`,
  `HelperUpdateReport`, поля, абзац «желаемая сборка» рядом с принципом «сервер не управляет Windows».
- [ ] **Step 4:** PASS; весь проект. **Step 5:** коммит «Server: helper update in status reply, worker».

### Task 4: Сервер — API панели

**Files:**
- Create: `src/Club.Server/Panel/HelperUpdatesPanelEndpoints.cs`
- Modify: `src/Club.Server/Panel/MachinesPanelEndpoints.cs` (`MachineView` + `HelperBuild`, `HelperUpdate`
  (`HelperUpdateRow?` за сутки), `HelperUpdatable` (bool), `HelperUpdateBlock` (причина или null)), `src/Club.Server/Program.cs`
- Test: `tests/Club.Server.Tests/HelperUpdatesPanelTests.cs`

**Interfaces:**
- Produces: `GET /panel/api/v1/helper/builds` → `HelperBuild[]`; `POST /panel/api/v1/helper/updates`
  `{file, numbers?: int[], allHybrids?: bool}` → `LaunchResult` (400 `validation` — нет файла, пустой выбор, больше 1000
  номеров; `targetTooOld` — в `Refused` у каждого ПК); `GET /panel/api/v1/helper/updates` → `{items: HelperUpdateRow[], paused: bool, maxActive}`;
  `POST …/updates/resume`, `POST …/updates/cancel` → `{count}`; `POST …/updates/{id}/cancel` → 204 / 404.
  `requested_by` = `panel`. Строка журнала на каждое действие.

- [ ] **Step 1: тесты:** авторизация на каждом маршруте (без токена — 401); запуск по номерам и «все гибриды» (места
  бездиска — в `Refused` с `diskless`); `MachineView` у места бездиска — `helperUpdatable=false`, `helperUpdateBlock="diskless"`,
  у мастера эталона в сеансе — `true`; отмена одного.
- [ ] **Step 2–4:** FAIL → реализовать → PASS; весь проект. **Step 5:** коммит «Panel API: helper updates».

### Task 5: Панель — карточка «Обновление помощника» и столбец «Помощник»

**Files:**
- Modify: `src/Club.Server/wwwroot/panel/index.html`, `app.js`, `i18n.js`, стили при необходимости
- Test: `tests/Club.Server.Tests/PanelStaticTests.cs` (если есть проверка ключей i18n — дополнить)

**Interfaces:**
- Consumes: API задачи 4; `parseSeats`/`formatSeats`, `oneAtATime`, свои `<dialog>` подтверждения.

- [ ] **Step 1:** карточка на экране ПК: список сборок (версия, размер, 8 символов SHA256, «только вручную» для
  `updatable=false`), выбор сборки, поле номеров, кнопки «Запустить», «Все гибриды» (подтверждение диалогом), очередь
  (строки поручений), «Продолжить» при паузе, «Отменить всё», отмена строки.
- [ ] **Step 2:** столбец «Помощник»: `версия · 8 символов SHA` и метка состояния — все пары из spec п. 3.5 (`назначено`
  = `active` без прогресса; `ждёт: сеанс / команда шелла / правка библиотеки` по `reason`; `проверка` = `confirming`;
  `ошибка: <код> <текст>`; `вручную (старше 1.8.0)`; `через эталон`).
- [ ] **Step 3:** ключи `helperUpd*` во всех трёх языках; `node --check app.js`; одноразовый node-скрипт вне репозитория
  сверяет, что каждый `t('helperUpd…')`/`data-i18n` есть в ru, uz, en.
- [ ] **Step 4:** локальный сервер с временной базой и папкой `tools` (две сборки: 1.7.0 и 1.8.0) — curl по API, как
  делает `app.js`; браузер — не нужен. **Step 5:** коммит «Panel: helper update card and column».

### Task 6: Помощник — провод и загрузка

**Files:**
- Modify: `src/Club.Helper.Core/DisklessApiClient.cs` (DTO, `RegisterRequest` + `HelperBuild`, загрузка),
  `src/Club.Helper.Core/Abstractions.cs` (`HelperOptions.HelperBuild`), `src/Club.Helper.Core/HelperLoop.cs`
  (отчёт с `helperBuild`/`helperUpdate`; последний принятый ответ)
- Test: `tests/Club.Helper.Tests/HelperUpdateWireTests.cs`

**Interfaces:**
- Produces DTO (помощник): `record HelperUpdateOffer(Guid Id, string Version, string Sha256, long Size, string Path)`;
  `record HelperUpdateReport(string Id, string State, string? Reason = null, string? Error = null, string? Message = null)`;
  `StatusAccepted(..., HelperUpdateOffer? HelperUpdate = null)`; `MachineStatus(..., string? HelperBuild = null, HelperUpdateReport? HelperUpdate = null)`.
- Produces: `Task<string> DisklessApiClient.DownloadAsync(string relativePath, string destination, long maxBytes, CancellationToken ct)`
  — клиент для больших файлов, без токена, `FileMode.Create`/`FileShare.None`, SHA256 по ходу, больше `maxBytes` —
  ошибка и файл стёрт, в конце `Flush(true)`; возвращает SHA256 строчными; сеть/тайм-аут → `ServerUnavailableException`.
- Produces: `HelperLoop.LastAccepted` (`StatusAccepted?` последнего принятого ответа G:), `HelperLoop.AcceptedReports`
  (счётчик), `HelperLoop.MasterBusy` (`_master` назначен или `LastMasterReport?.State` не null/`none`);
  `HelperLoops.MasterBusy()` — любой цикл; источник `Func<HelperUpdateReport?>` для отчёта G: (только G:).

- [ ] **Step 1: тесты:** `StatusReply_WithHelperUpdate_Parsed`; `G_Report_CarriesHelperBuildAndUpdate` (через
  `WireRecorder`); `Library_Report_NoHelperFields` (H: — без новых полей); `Download_ShaAndSize` (подделка сервера);
  `Download_OverMax_DeletesFile`; `MasterBusy_FailedWithLetter_IsBusy`; `GamesWireTests` без изменений.
- [ ] **Step 2–4:** FAIL → реализовать → PASS; весь `tests/Club.Helper.Tests` один раз. **Step 5:** коммит «Helper: update wire and download».

### Task 7: Помощник — файлы состояния и страж

**Files:**
- Create: `src/Club.Helper.Core/Updates/UpdatePaths.cs`, `UpdateState.cs`, `UpdateStateStore.cs`, `UpdateGuard.cs`,
  `IServiceControl.cs`
- Test: `tests/Club.Helper.Tests/UpdateGuardTests.cs`

**Interfaces:**
- Produces: `record UpdatePaths(string InstallDir, string StateDir)` с `Exe`, `New`, `Old`, `Failed`, `StateFile`
  (`update.json`), `FailedFile` (`update-failed.json`); `UpdatePaths.Default()` — `%ProgramFiles%\ClubDiskless`,
  `%ProgramData%\ClubDiskless`.
- Produces: `record UpdateState(int V, Guid Id, string FromSha, string ToSha, string ToVersion, string Phase, int Attempts, bool CleanExit, DateTimeOffset StartedAt, string? Error, string? Message)`;
  `UpdateStateStore`: `UpdateState? Read(out bool unreadable)`, `void Write(UpdateState)` (`.tmp` → `Flush(true)` → перенос),
  `void Delete()`, `IReadOnlyList<FailedUpdate> Failed()`, `void AddFailed(FailedUpdate)` (последние 20),
  `record FailedUpdate(Guid Id, string Sha256, string Error)`.
- Produces: `interface IServiceControl { bool RecoveryConfigured(); void EnsureRecovery(); void TerminateSelf(); }`.
- Produces: `enum GuardOutcome { Run, RunConfirming, RollbackAndTerminate }`; `static GuardOutcome UpdateGuard.Run(UpdatePaths paths, UpdateStateStore store, string ownSha, Func<string, string> shaOf, TimeProvider clock)`
  — все ветки spec п. 4.4 (попытки, откат с возвратом имён, `noRollback`, `prepared`, чужая сборка, нечитаемый файл,
  хвосты).

- [ ] **Step 1: тесты** на временных папках: `Swapped_FirstStart_Attempt1_RunConfirming`;
  `CleanExitThenStart_NoAttempt` (сервер недоступен, перезагрузки — Review Focus 2); `FourthUncleanStart_Rollback`
  (файлы: `exe` = старое содержимое, `.failed` = новое, фаза `rolledBack`, `startFailed`); `Rollback_OldMissing_NoRollback`
  (файлы не тронуты, ошибка `noRollback`, `Run`); `Rollback_OldWrongSha_NoRollback`; `OldBuild_Prepared_DeletesNewSilently`
  (Review Focus 5); `OldBuild_RolledBack_KeepsStateForReport`; `ForeignBuild_CleansAll`; `Unreadable_KeepsOld`;
  `NoState_CleansTails`; `FailedList_KeepsLast20`.
- [ ] **Step 2–4:** FAIL → реализовать → PASS. **Step 5:** коммит «Helper: update state and start guard».

### Task 8: Помощник — `SelfUpdater` (загрузка, ожидание, замена, подтверждение)

**Files:**
- Create: `src/Club.Helper.Core/Updates/SelfUpdater.cs`
- Modify: `src/Club.Helper.Core/SavesService.cs` (`TryHoldForUpdate`, `Updating`), `src/Club.Helper.Core/SavesCommandHandler.cs`
  (`updating`, `build` в `ping`), `src/Club.Helper.Core/DisklessApiClient.cs` (`HoldRenewalAsync`)
- Test: `tests/Club.Helper.Tests/SelfUpdaterTests.cs`, дополнить `SavesCommandHandlerTests.cs`

**Interfaces:**
- Consumes: задачи 6, 7.
- Produces: `Task<IDisposable?> SavesService.TryHoldForUpdateAsync()` (замок без ожидания и нет выдачи — держатель, иначе null),
  `bool SavesService.Updating`; `Task<IDisposable> DisklessApiClient.HoldRenewalAsync(CancellationToken ct)`.
- Produces: `class SelfUpdater(DisklessApiClient api, UpdatePaths paths, UpdateStateStore store, IServiceControl service, SavesService saves, Func<bool> masterBusy, HelperOptions options, TimeProvider clock, ILogger<SelfUpdater> logger)`:
  `HelperUpdateReport? Report()` (для отчёта G:); `void OnTick(StatusAccepted? accepted, int acceptedReports)` (поручение,
  отмена, учёт подтверждения); `Task AfterTickAsync(CancellationToken ct)` (загрузка в фоне, готовность, `applying`,
  замена, `TerminateSelf`); `void MarkCleanExit()`.
- Порядок замены — spec п. 4.3, шаги 1–4; подтверждение — 10 минут и ≥ 5 принятых отчётов с момента старта;
  `confirmed` шлётся, пока принятый ответ несёт поручение.

- [ ] **Step 1: тесты** (временные папки, подделка сервера и `IServiceControl`): `Offer_BadPath_FailedDownloadFailed`;
  `Offer_OwnBuild_Ignored`; `Offer_InFailedList_ReportsFailed_NoDownload`; `Download_HashMismatch_NewDeleted`;
  `Busy_Attached_WaitingSession`; `Busy_Command_WaitingCommand`; `Busy_Master_WaitingMaster`;
  `Applying_OnlyAfterAcceptedFullReport`; `NoRecovery_NoSwap`; `Swap_RenameFails_NamesRestored_SwapFailed`;
  `Swap_Success_StateSwapped_Terminated` (`TerminateSelf` вызван один раз, замок продления взят);
  `Cancel_WhileWaiting_NewDeleted_NoSwap` (Review Focus 3); `NoReply_IsNotCancel`;
  `Confirm_After10MinAnd5Reports_DeletesOldAndState`; `Pipe_DuringUpdate_UpdatingError_PingUpdatingTrue`.
- [ ] **Step 2–4:** FAIL → реализовать → PASS; весь проект. **Step 5:** коммит «Helper: self-updater».

### Task 9: Помощник — каркас процесса (Windows), версия 1.8.0

**Files:**
- Create: `src/Club.Helper/ClubServiceLifetime.cs` (наследник `WindowsServiceLifetime`), `src/Club.Helper/WindowsServiceControl.cs`,
  `src/Club.Helper/RollbackHost.cs`
- Modify: `src/Club.Helper/Program.cs`, `src/Club.Helper/HelperService.cs` (после `loops.TickAsync` и конца такта
  сохранений — `SelfUpdater.OnTick`/`AfterTickAsync`), `src/Club.Helper/Club.Helper.csproj` (`<Version>1.8.0</Version>`,
  комментарий версий; свойство `ClubTestFault` → константы `CLUB_TEST_FAULT_CRASH|THROW|HANG`)
- Test: сборка `dotnet build src/Club.Helper` без предупреждений (Windows-часть на машине разработки не запускается)

**Interfaces:**
- Produces в `Program.cs` по порядку: SHA256 своего exe (`HelperOptions.HelperBuild`) → `UpdateGuard.Run` → `RollbackAndTerminate` — `RollbackHost.Run()`
  (минимальный хост службы; `Kill()` после `Running`); иначе `WindowsServiceControl.EnsureRecovery()` и обычный хост с `ClubServiceLifetime`, `ApplicationStopping`
  без команды Windows → текст ошибки в `update.json` и `Kill()`; `host.Run()` вернулся без команды Windows — то же;
  `AppDomain.UnhandledException` → текст в `update.json`. `WindowsServiceControl`: `sc.exe qfailure`/`sc.exe failure` через
  `ProcessStartInfo.ArgumentList`; `TerminateSelf` = `Process.GetCurrentProcess().Kill()`. Строки стража —
  `EventLog.WriteEntry("ClubDisklessHelper", …)`.
- Пробные сборки: `-p:Version=1.8.90 -p:ClubTestFault=crash` (падение после старта хоста), `1.8.91 … =throw`
  (исключение в `HelperService.ExecuteAsync`), `1.8.92 … =hang` (такт висит, отчётов нет).

- [ ] **Step 1:** реализовать; `dotnet build src/Club.Helper -c Release` — 0 предупреждений; `dotnet test
  tests/Club.Helper.Tests` — зелёные.
- [ ] **Step 2:** `dotnet publish` по `docs/helper.md` с `-p:RuntimeFrameworkVersion=10.0.13` — exe собирается; в
  манифесте пакета 0 нативных файлов (как сейчас).
- [ ] **Step 3:** коммит «Helper 1.8.0: process skeleton, service control, self-update wiring».

### Task 10: Скрипты, документация, шелл

**Files:**
- Modify: `scripts/shell/shell-preflight.ps1`, `shell-install.ps1`, `shell-uninstall.ps1`, `scripts/golden/install-helper.ps1`,
  `scripts/golden/update-helper-net.ps1`, `scripts/golden/seal-golden.ps1`, `scripts/golden/seal-hybrid.ps1`,
  `docs/helper.md`; в репозитории `club-contracts` — `docs/SHELL_CHANGES.md` п. 23
- Проверка: кодировки и концы строк как были (BOM/CRLF), строки вывода — ASCII; PowerShell на машине разработки нет —
  ревьюер читает изменения построчно

- [ ] **Step 1:** скрипты шелла — по spec п. 5.1 (версия файла числами ≥ 1.6.0; `shell-install` «после» — работает и
  версия ≥ «до»; `shell-uninstall` — возврат копии только если exe пропал, после выхода процесса).
- [ ] **Step 2:** `install-helper.ps1`/`update-helper-net.ps1` — 1.8.0 (SHA256 после сборки задачи 9), удаление
  `.new`/`.old`/`.failed`, `update.json`, `update-failed.json`; тексты версий. `seal-golden.ps1`/`seal-hybrid.ps1` — по
  spec п. 5.1.
- [ ] **Step 3:** `docs/helper.md` — раздел «Самообновление» (как работает, коды, что видно в панели, ручной откат,
  итог шага 0) и «Обновление до 1.8.0 в клубе»; `SHELL_CHANGES` п. 23 — spec п. 5.2 прямым текстом.
- [ ] **Step 4:** коммиты в обоих репозиториях (явные пути).

### Task 11: Пробные сборки и инструкция проверки

**Files:**
- Create: `scripts/golden/self-update-test.md` (порядок проверки на PC-22/PC-23 по spec п. 7: что нажать в панели, что
  смотреть: `Get-IscsiSession`, `Get-WinEvent` 7031/7034, журнал «Приложение»)
- [ ] **Step 1:** собрать 1.8.0, 1.8.1 (только номер), 1.8.90/91/92 (`ClubTestFault`) в scratchpad, SHA256 в
  инструкцию; на ВМ — во временную папку, не в `tools`.
- [ ] **Step 2:** коммит инструкции.

### Task 12: Выкатка (каждый пункт — СТОП до ОК владельца)

- [ ] Шаг 0 (задача 0) — если не сделан раньше.
- [ ] Сервер: копия базы, `server.sh`-подобный скрипт (сборка из чистой копии коммита), `HelperUpdates__*` — по
  умолчанию; панель — карточка видна.
- [ ] PC-22 и PC-23: 1.8.0 вручную (`update-helper-net-1.8.0.ps1` под отдельным именем в `tools`), проверка по
  `self-update-test.md` с `HelperUpdates__MaxActive=1` (затем вернуть 3), пробные файлы убрать из `tools`.
- [ ] Остальные гибриды: `update-helper-net.ps1` → 1.8.0 (старый — `update-helper-net-1.7.0.ps1`), `publish-kit.sh`.
- [ ] Бездиск — сеанс мастера; h1f — `patch-hybrid-wim.sh`; SHELL_CHANGES — разработчику шелла.
- [ ] Обновить память (`memory/`) по итогам.
