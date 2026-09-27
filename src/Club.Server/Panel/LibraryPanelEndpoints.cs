using Club.Server.Api;
using Club.Server.Library;

namespace Club.Server.Panel;

public sealed record LibraryVersionView(
    Guid Id, string Label, string State, string? Role, string? TargetIqn, DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt, DateTimeOffset? RetiredAt, string? LastError);

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
    IReadOnlyList<StorageWarningView> Warnings);

public sealed record PublishRequest(string Label);

public sealed record OperationAccepted(Guid OperationId);

/// <summary>
/// Экран «Библиотека игр» панели: версии, публикация, откат, ход операций, предупреждения сверки.
/// Операции асинхронные: запрос ставит их в журнал (202), выполняет фоновый исполнитель.
/// </summary>
public static class LibraryPanelEndpoints
{
    public static void MapLibraryPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/library", async (LibraryRepository repository, LibraryOptions options) =>
        {
            var pointers = await repository.PointersAsync();
            var versions = await repository.AllVersionsAsync();
            var labels = versions.ToDictionary(v => v.Id, v => v.Label);
            return Results.Json(
                new LibraryOverview(
                    options.Enabled,
                    pointers.Current is null ? null : View(pointers.Current, pointers),
                    pointers.Rollback is null ? null : View(pointers.Rollback, pointers),
                    versions.Select(v => View(v, pointers)).ToList(),
                    (await repository.OpenOperationsAsync()).Select(o => View(o, labels)).ToList(),
                    (await repository.ActiveWarningsAsync()).Select(View).ToList()),
                ApiJson.Options);
        });

        panel.MapPost("/library/versions", async (PublishRequest request, LibraryPublisher publisher) =>
        {
            var operationId = await Guard(() => publisher.RequestPublishAsync(request.Label?.Trim() ?? "", "panel"));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        panel.MapPost("/library/rollback", async (LibraryPublisher publisher) =>
        {
            var operationId = await Guard(() => publisher.RequestRollbackAsync("panel"));
            return Results.Json(new OperationAccepted(operationId), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        panel.MapGet("/library/operations", async (LibraryRepository repository, int? limit) =>
        {
            var labels = (await repository.AllVersionsAsync()).ToDictionary(v => v.Id, v => v.Label);
            var operations = await repository.RecentOperationsAsync(Math.Clamp(limit ?? 50, 1, 200));
            return Results.Json(operations.Select(o => View(o, labels)).ToList(), ApiJson.Options);
        });

        panel.MapGet("/library/operations/{id:guid}", async (Guid id, LibraryRepository repository) =>
        {
            var operation = await repository.FindOperationAsync(id) ?? throw ApiException.NotFound("operation");
            var labels = (await repository.AllVersionsAsync()).ToDictionary(v => v.Id, v => v.Label);
            return Results.Json(View(operation, labels), ApiJson.Options);
        });

        panel.MapPost("/library/operations/{id:guid}/retry", async (Guid id, LibraryRepository repository, TimeProvider clock) =>
        {
            var operation = await repository.FindOperationAsync(id) ?? throw ApiException.NotFound("operation");
            if (operation.Status != "failed" || !await repository.RetryOperationAsync(id, clock.GetUtcNow()))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Only a failed operation can be retried", new { reason = "notFailed", status = operation.Status });
            }

            return Results.Json(new OperationAccepted(id), ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        panel.MapGet("/storage/warnings", async (LibraryRepository repository) =>
            Results.Json((await repository.ActiveWarningsAsync()).Select(View).ToList(), ApiJson.Options));
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

    private static LibraryVersionView View(LibraryVersion v, LibraryPointers pointers) => new(
        v.Id, v.Label, v.State,
        v.Id == pointers.Current?.Id ? "current" : v.Id == pointers.Rollback?.Id ? "rollback" : null,
        v.TargetIqn, v.CreatedAt, v.PublishedAt, v.RetiredAt, v.LastError);

    private static StorageOperationView View(StorageOperation o, IReadOnlyDictionary<Guid, string> labels) => new(
        o.Id, o.Kind, o.VersionId is { } id && labels.TryGetValue(id, out var label) ? label : null,
        o.Status, o.Step, o.Attempts, o.LastError, o.RequestedBy, o.CreatedAt, o.UpdatedAt);

    private static StorageWarningView View(StorageWarning w) => new(w.Kind, w.Subject, w.Message, w.FirstSeen, w.LastSeen);
}
