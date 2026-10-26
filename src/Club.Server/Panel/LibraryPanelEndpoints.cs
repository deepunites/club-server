using Club.Server.Api;
using Club.Server.Library;

namespace Club.Server.Panel;

/// <summary><c>mountedOn</c> — сколько ПК на связи сейчас работают с этой версией; <c>contents</c> — папки тома по отчёту помощника.</summary>
public sealed record LibraryVersionView(
    Guid Id, string Label, string State, string? Role, string? TargetIqn, DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt, DateTimeOffset? RetiredAt, string? LastError,
    int MountedOn = 0, IReadOnlyList<string>? Contents = null, DateTimeOffset? ContentsAt = null);

/// <summary>
/// Мастер-том для суперклиента. <c>state</c>: closed | opening | open | closing | failed; <c>mounted</c> — по отчёту
/// помощника на ПК суперклиента; <c>dirty</c> — закрыт принудительно, публикация требует подтверждения;
/// <c>machineError</c> — причина из отчёта помощника (у <c>machineState</c> failed и mounting).
/// </summary>
public sealed record MasterView(
    string State, Guid? MachineId, string? MachineName, string? MachineState, bool Dirty, bool ForceClose,
    DateTimeOffset? OpenedAt, DateTimeOffset? CloseRequestedAt, DateTimeOffset? ClosedAt, string? LastError, string DriveLetter,
    string? MachineError = null);

/// <summary>
/// ПК, на котором можно открыть мастер-том: одобрен и сообщил IQN инициатора. <c>supported</c> — только у библиотек не-G::
/// помощник ПК её умеет (PersonalMinHelper, 1.6.0); иначе открытие — 409 <c>helperTooOld</c>. <c>reporting</c> — тоже только
/// у них: последний отчёт ПК об этой библиотеке не старше окна «на связи» (90 с), то есть её цикл на ПК работает; иначе
/// (helper.json <c>"Libraries": []</c>, цикл спит или стоит) мастер-том никто не подключит — открытие 409
/// <c>libraryNotReported</c>.
/// </summary>
public sealed record MasterCandidate(Guid Id, int Number, string Name, bool Online, bool? Supported = null, bool? Reporting = null);

public sealed record MasterOpenRequest(Guid MachineId);

public sealed record MasterCloseRequest(bool? Force);

/// <summary>ПК на связи (одобренные, отчёт не старше 90 с) по отношению к текущей версии.</summary>
public sealed record LibraryAdoption(int Online, int OnCurrent, int OnOlder, int SwitchPending, int Failed, int NotMounted);

public sealed record StorageOperationView(
    Guid Id, string Kind, string? VersionLabel, string Status, string? Step, int Attempts, string? LastError,
    string? RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record StorageWarningView(string Kind, string Subject, string Message, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

public sealed record LibraryOverview(
    bool StorageEnabled,
    LibraryVersionView? Current,
    LibraryVersionView? Rollback,
    IReadOnlyList<LibraryVersionView> Versions,
    IReadOnlyList<StorageOperationView> OpenOperations,
    IReadOnlyList<StorageWarningView> Warnings,
    LibraryAdoption? Machines = null,
    MasterView? Master = null,
    IReadOnlyList<MasterCandidate>? MasterCandidates = null);

public sealed record PublishRequest(string Label, bool? AllowDirtyMaster);

public sealed record OperationAccepted(Guid OperationId);

/// <summary>
/// Библиотека в списке панели (<c>GET /libraries</c>). <c>key</c> — <c>games</c> (G:, секция Library) или ключ
/// Libraries:&lt;ключ&gt;; <c>path</c> — её экран в API панели; <c>minHelper</c> — с какой версии помощник получает её
/// личный диск (нет — без личного слоя). <c>enabled</c> false — библиотека выключена (настройкой или проверкой схемы).
/// </summary>
public sealed record LibraryInfo(string Key, string Title, bool Enabled, string DriveLetter, string MasterDriveLetter, string? MinHelper, string Path);

/// <summary>
/// Экран «Библиотека игр» панели: версии, публикация, откат, ход операций, предупреждения сверки.
/// Операции асинхронные: запрос ставит их в журнал (202), выполняет фоновый исполнитель.
/// G: — по путям <c>/library/…</c>; любая библиотека (и G: как <c>games</c>) — <c>/libraries/{key}/…</c> с теми же ответами.
/// </summary>
public static class LibraryPanelEndpoints
{
    public static void MapLibraryPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/libraries", (LibrarySet libraries) =>
            Results.Json(libraries.Extras.Prepend(libraries.Main).Select(Info).ToList(), ApiJson.Options));

        MapLibraryRoutes(panel.MapGroup("/library"), (_, libraries) => libraries.Main);
        MapLibraryRoutes(panel.MapGroup("/libraries/{key}"), (context, libraries) => ByKey(libraries, context.Request.RouteValues["key"] as string));

        panel.MapGet("/storage/warnings", async (LibraryRepository repository) =>
            Results.Json((await repository.ActiveWarningsAsync()).Select(View).ToList(), ApiJson.Options));
    }

    /// <summary>Пути экрана одной библиотеки; <paramref name="library"/> — какой (у <c>/library</c> — всегда G:).</summary>
    private static void MapLibraryRoutes(RouteGroupBuilder group, Func<HttpContext, LibrarySet, LibrarySlot> library)
    {
        group.MapGet("/", async (HttpContext context, LibrarySet libraries, Data.MachineRepository machines, TimeProvider clock) =>
            Results.Json(await OverviewAsync(library(context, libraries), machines, clock), ApiJson.Options));

        group.MapPost("/versions", async (PublishRequest request, HttpContext context, LibrarySet libraries) =>
        {
            var publisher = library(context, libraries).Publisher;
            var operationId = await Guard(() => publisher.RequestPublishAsync(request.Label?.Trim() ?? "", "panel", request.AllowDirtyMaster ?? false));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapPost("/master/open", async (MasterOpenRequest request, HttpContext context, LibrarySet libraries, Data.MachineRepository machines, TimeProvider clock) =>
        {
            var slot = library(context, libraries);
            await RequireReportsAsync(slot, request.MachineId, machines, clock);
            var editor = slot.Editor;
            var operationId = await Guard(() => editor.RequestOpenAsync(request.MachineId, "panel"));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapPost("/master/close", async (MasterCloseRequest? request, HttpContext context, LibrarySet libraries) =>
        {
            var editor = library(context, libraries).Editor;
            var operationId = await Guard(() => editor.RequestCloseAsync(request?.Force ?? false, "panel"));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapPost("/rollback", async (HttpContext context, LibrarySet libraries) =>
        {
            var publisher = library(context, libraries).Publisher;
            var operationId = await Guard(() => publisher.RequestRollbackAsync("panel"));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapGet("/operations", async (HttpContext context, LibrarySet libraries, int? limit) =>
        {
            var repository = library(context, libraries).Repository;
            var labels = (await repository.AllVersionsAsync()).ToDictionary(v => v.Id, v => v.Label);
            var operations = await repository.RecentOperationsAsync(Math.Clamp(limit ?? 50, 1, 200));
            return Results.Json(operations.Select(o => View(o, labels)).ToList(), ApiJson.Options);
        });

        group.MapGet("/operations/{id:guid}", async (Guid id, HttpContext context, LibrarySet libraries) =>
        {
            var repository = library(context, libraries).Repository;
            var operation = await repository.FindOperationAsync(id) ?? throw ApiException.NotFound("operation");
            var labels = (await repository.AllVersionsAsync()).ToDictionary(v => v.Id, v => v.Label);
            return Results.Json(View(operation, labels), ApiJson.Options);
        });

        group.MapPost("/operations/{id:guid}/retry", async (Guid id, HttpContext context, LibrarySet libraries, TimeProvider clock) =>
        {
            var repository = library(context, libraries).Repository;
            var operation = await repository.FindOperationAsync(id) ?? throw ApiException.NotFound("operation");
            if (operation.Status != "failed" || !await repository.RetryOperationAsync(id, clock.GetUtcNow()))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Only a failed operation can be retried", new { reason = "notFailed", status = operation.Status });
            }

            return Results.Json(new OperationAccepted(id), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Правка библиотеки не-G: — только на ПК, чей цикл этой библиотеки работает (свежий отчёт о ней): иначе сервер открыл
    /// бы таргет, к которому никто не подключится, и панель показывала бы открытую правку без ПК. Остальные отказы (нет
    /// ПК, не одобрен, помощник старше, нет IQN) — у MasterEditor, с прежними кодами; у G: проверки нет.
    /// </summary>
    private static async Task RequireReportsAsync(LibrarySlot library, Guid machineId, Data.MachineRepository machines, TimeProvider clock)
    {
        if (library.Machines is null
            || await machines.FindAsync(machineId) is not { Approved: true } machine
            || !library.Options.SupportsHelper(machine.HelperVersion)
            || string.IsNullOrWhiteSpace(machine.InitiatorIqn))
        {
            return;
        }

        if (!MachinesPanelEndpoints.IsRecent((await library.Machines.FindAsync(machineId))?.ReportedAt, clock.GetUtcNow()))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                $"This PC has not reported the {library.Title} ({library.Options.DriveLetter}:) library recently: its helper does not run it " +
                "(helper.json \"Libraries\") or the loop is stuck; choose a PC whose helper reports it",
                new { reason = "libraryNotReported" });
        }
    }

    /// <summary>Библиотека по ключу из пути: <c>games</c> — G:, иначе работающая из Libraries; выключенная или неизвестная — 404.</summary>
    private static LibrarySlot ByKey(LibrarySet libraries, string? key) =>
        key == LibraryNames.Games.Key ? libraries.Main : libraries.Find(key ?? "") ?? throw ApiException.NotFound("library");

    private static LibraryInfo Info(LibrarySlot library) => new(
        library.Key, library.Title, library.Options.Enabled, library.Options.DriveLetter, library.Options.MasterDriveLetter,
        library.Options.PersonalGames ? library.Options.PersonalMinHelper : null,
        library.Names.IsMain ? "/library" : $"/libraries/{library.Key}");

    /// <summary>
    /// Что ПК сообщил о библиотеке: у G: — столбцы <c>machines</c> (их пишет отчёт по G:), у остальных — <c>machine_library</c>
    /// (отчёт по её пути). Отчёт о томе старше окна «на связи» — тома нет: цикл библиотеки на ПК мог встать, а ПК — на связи.
    /// </summary>
    private readonly record struct PcLibrary(string VolumeState, string? VolumeVersion, string? MasterState, string? MasterError);

    private static async Task<LibraryOverview> OverviewAsync(LibrarySlot library, Data.MachineRepository machines, TimeProvider clock)
    {
        var (repository, options) = (library.Repository, library.Options);
        var pointers = await repository.PointersAsync();
        var versions = await repository.AllVersionsAsync();
        var labels = versions.ToDictionary(v => v.Id, v => v.Label);
        var now = clock.GetUtcNow();
        var registry = await machines.AllAsync();
        bool Recent(DateTimeOffset? seen) => MachinesPanelEndpoints.IsRecent(seen, now);
        bool IsOnline(Data.MachineRow m) => Recent(m.LastSeenAt);
        var reports = library.Machines is null ? null : (await library.Machines.AllAsync()).ToDictionary(r => r.MachineId);
        PcLibrary Reported(Data.MachineRow m) =>
            reports is null ? new(m.VolumeState, m.VolumeVersion, m.MasterState, m.MasterError)
            : !reports.TryGetValue(m.Id, out var r) ? new("none", null, null, null)
            : Recent(r.ReportedAt) ? new(r.VolumeState, r.VolumeVersion, r.MasterState, r.MasterError)
            : new("none", null, r.MasterState, r.MasterError);
        var masterState = await library.Master.GetAsync();
        var masterMachine = registry.FirstOrDefault(m => m.Id == masterState.MachineId);
        var masterPc = masterMachine is null ? (PcLibrary?)null : Reported(masterMachine);
        var masterView = new MasterView(
            masterState.State, masterState.MachineId, masterMachine?.Name, masterPc?.MasterState, masterState.Dirty, masterState.ForceClose,
            masterState.OpenedAt, masterState.CloseRequestedAt, masterState.ClosedAt, masterState.LastError, options.MasterDriveLetter,
            masterPc is { MasterState: "mounting" or "failed" } pc ? pc.MasterError : null);
        // supported и reporting — только у библиотек не-G:: правку открывают лишь помощнику, который её умеет (иначе 409
        // helperTooOld) и чей цикл её сейчас ведёт (иначе 409 libraryNotReported).
        var candidates = registry.Where(m => m.Approved && !string.IsNullOrEmpty(m.InitiatorIqn))
            .Select(m => reports is null
                ? new MasterCandidate(m.Id, m.Number, m.Name, IsOnline(m))
                : new MasterCandidate(m.Id, m.Number, m.Name, IsOnline(m), options.SupportsHelper(m.HelperVersion),
                    reports.TryGetValue(m.Id, out var r) && Recent(r.ReportedAt)))
            .ToList();
        var online = registry.Where(m => m.Approved && IsOnline(m)).Select(Reported).ToList();
        var mounted = online.Where(m => m.VolumeState is "mounted" or "switchPending" && m.VolumeVersion is not null)
            .GroupBy(m => m.VolumeVersion!)
            .ToDictionary(g => g.Key, g => g.Count());
        var current = pointers.Current?.Label;
        var adoption = new LibraryAdoption(
            online.Count,
            online.Count(m => m.VolumeState == "mounted" && current is not null && m.VolumeVersion == current),
            online.Count(m => m.VolumeState == "mounted" && m.VolumeVersion != current),
            online.Count(m => m.VolumeState == "switchPending"),
            online.Count(m => m.VolumeState == "failed"),
            online.Count(m => m.VolumeState is "none" or "mounting"));
        return new LibraryOverview(
            options.Enabled,
            pointers.Current is null ? null : View(pointers.Current, pointers, mounted),
            pointers.Rollback is null ? null : View(pointers.Rollback, pointers, mounted),
            versions.Select(v => View(v, pointers, mounted)).ToList(),
            (await repository.OpenOperationsAsync()).Select(o => View(o, labels)).ToList(),
            (await repository.ActiveWarningsAsync(library.Names.WarningSource)).Select(w => WarningView(w, library.Names.WarningSubjectPrefix)).ToList(),
            adoption,
            masterView,
            candidates);
    }

    private static async Task<Guid> Guard(Func<Task<Guid>> action)
    {
        try
        {
            return await action();
        }
        catch (LibraryRequestException ex) when (ex.Reason == "label")
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, ex.Message, new { field = "label", reason = "format" });
        }
        catch (LibraryRequestException ex)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, ex.Message, new { reason = ex.Reason });
        }
    }

    private static LibraryVersionView View(LibraryVersion v, LibraryPointers pointers, IReadOnlyDictionary<string, int> mounted) => new(
        v.Id, v.Label, v.State,
        v.Id == pointers.Current?.Id ? "current" : v.Id == pointers.Rollback?.Id ? "rollback" : null,
        v.TargetIqn, v.CreatedAt, v.PublishedAt, v.RetiredAt, v.LastError,
        mounted.GetValueOrDefault(v.Label), v.Contents, v.ContentsAt);

    private static StorageOperationView View(StorageOperation o, IReadOnlyDictionary<Guid, string> labels) => new(
        o.Id, o.Kind, o.VersionId is { } id && labels.TryGetValue(id, out var label) ? label : null,
        o.Status, o.Step, o.Attempts, o.LastError, o.RequestedBy, o.CreatedAt, o.UpdatedAt);

    private static StorageWarningView View(StorageWarning w) => new(w.Kind, w.Subject, w.Message, w.FirstSeen, w.LastSeen);

    /// <summary>Предупреждение на экране своей библиотеки — без приставки её ключа (<c>solo:v1</c> → <c>v1</c>): она нужна только в общей таблице.</summary>
    private static StorageWarningView WarningView(StorageWarning w, string prefix) => new(
        w.Kind, prefix.Length > 0 && w.Subject.StartsWith(prefix, StringComparison.Ordinal) ? w.Subject[prefix.Length..] : w.Subject, w.Message, w.FirstSeen, w.LastSeen);
}
