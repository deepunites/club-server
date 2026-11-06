using Club.Server.Api;
using Club.Server.Data;
using Club.Server.HelperUpdates;

namespace Club.Server.Panel;

/// <summary>
/// Запуск обновления помощника: сборка <c>file</c> (имя из <c>GET /helper/builds</c>) — ПК из <c>numbers</c> и, если
/// <c>allHybrids</c>, всем гибридам (<c>boot_mode = local</c>).
/// </summary>
public sealed record HelperUpdateLaunchRequest(string? File, IReadOnlyList<int>? Numbers, bool? AllHybrids);

/// <summary>
/// Очередь обновления помощника: незавершённые поручения и завершённые за сутки. <c>paused</c> — очередь на паузе
/// (есть поручения в <c>paused</c>), <c>maxActive</c> — <c>HelperUpdates:MaxActive</c>.
/// </summary>
public sealed record HelperUpdatesView(IReadOnlyList<HelperUpdateRow> Items, bool Paused, int MaxActive);

/// <summary>Итог «Продолжить» и «Отменить всё»: сколько поручений затронуто.</summary>
public sealed record HelperUpdatesCount(int Count);

/// <summary>
/// Карточка «Обновление помощника» на экране ПК (spec 2026-11-06, п. 3.5): сборки из <c>tools</c>
/// (<see cref="HelperBuilds"/>), запуск, очередь, продолжение и отмена (<see cref="HelperUpdateRepository"/>: каждое
/// действие — одна транзакция под блокировкой очереди). Отказы отдельным ПК — в ответе запуска, а не 400: 400 — только
/// если запускать нечего (нет файла, пустой выбор, слишком много номеров). Каждое действие — строкой в журнал.
/// </summary>
public static class HelperUpdatesPanelEndpoints
{
    /// <summary>Кто запустил поручение (<c>helper_updates.requested_by</c>).</summary>
    public const string RequestedBy = "panel";

    public static void MapHelperUpdatesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1/helper");

        panel.MapGet("/builds", (HelperBuilds builds) => Results.Json(builds.List(), ApiJson.Options));

        panel.MapPost("/updates", async (HelperUpdateLaunchRequest request, HelperBuilds builds, HelperUpdateRepository queue, TimeProvider clock, ILoggerFactory logs) =>
        {
            var file = request.File?.Trim();
            if (string.IsNullOrEmpty(file))
            {
                throw ApiException.Validation("file", "required");
            }

            // Имя — только из списка сборок (шаблон имени, файл на месте и не моложе MinFileAgeSec).
            if (builds.Find(file) is null)
            {
                throw ApiException.Validation("file", "unknown");
            }

            var numbers = request.Numbers ?? [];
            var allHybrids = request.AllHybrids ?? false;
            if (numbers.Count == 0 && !allHybrids)
            {
                throw ApiException.Validation("numbers", "required");
            }

            if (numbers.Count > ZoneRepository.MaxAssign)
            {
                throw ApiException.Validation("numbers", "tooMany");
            }

            var result = await queue.LaunchAsync(file, numbers, allHybrids, RequestedBy, clock.GetUtcNow());
            Log(logs).LogInformation(
                "Helper update {File} launched in the panel (seats {Numbers}{All}): {Queued} {State}, refused {Refused}",
                file, numbers.Count == 0 ? "-" : string.Join(",", numbers), allHybrids ? ", all hybrids" : "", result.Queued.Count,
                result.Paused ? "paused" : "queued",
                result.Refused.Count == 0 ? "-" : string.Join(", ", result.Refused.Select(r => $"{r.Number}:{r.Reason}")));
            return Results.Json(result, ApiJson.Options);
        });

        panel.MapGet("/updates", async (HelperUpdateRepository queue, HelperUpdateOptions options, TimeProvider clock) =>
        {
            var items = await queue.ListAsync(clock.GetUtcNow());
            return Results.Json(new HelperUpdatesView(items, items.Any(i => i.State == HelperUpdateCodes.States.Paused), options.MaxActive), ApiJson.Options);
        });

        panel.MapPost("/updates/resume", async (HelperUpdateRepository queue, TimeProvider clock, ILoggerFactory logs) =>
        {
            var count = await queue.ResumeAsync(clock.GetUtcNow());
            Log(logs).LogInformation("Helper update queue resumed in the panel: {Count} update(s)", count);
            return Results.Json(new HelperUpdatesCount(count), ApiJson.Options);
        });

        panel.MapPost("/updates/cancel", async (HelperUpdateRepository queue, TimeProvider clock, ILoggerFactory logs) =>
        {
            var count = await queue.CancelAllAsync(clock.GetUtcNow());
            Log(logs).LogInformation("Helper updates cancelled in the panel: {Count} update(s)", count);
            return Results.Json(new HelperUpdatesCount(count), ApiJson.Options);
        });

        panel.MapPost("/updates/{id:guid}/cancel", async (Guid id, HelperUpdateRepository queue, TimeProvider clock, ILoggerFactory logs) =>
        {
            if (!await queue.CancelAsync(id, clock.GetUtcNow()))
            {
                throw ApiException.NotFound("helperUpdate");
            }

            Log(logs).LogInformation("Helper update {Id} cancelled in the panel", id);
            return Results.NoContent();
        });
    }

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger("Club.Server.Panel");
}
