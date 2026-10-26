using System.Globalization;
using Club.Server.Api;
using Club.Server.Data;

namespace Club.Server.Panel;

public sealed record ZoneRequest(string? Name);

/// <summary>Номера мест, которые переходят в зону (например, 11…20 и 25 — из поля «11-20, 25» панели).</summary>
public sealed record ZoneAssignRequest(IReadOnlyList<int>? Numbers);

/// <summary><c>assigned</c> — сколько машин теперь в зоне из списка; <c>unknown</c> — номера, которых нет в реестре.</summary>
public sealed record ZoneAssignResult(int Assigned, IReadOnlyList<int> Unknown);

/// <summary>
/// Зоны мест на экране «Рабочие станции»: оператор создаёт их сам («Зона 4», «VIP») и назначает места пачкой. Новые
/// машины попадают в зону по умолчанию (<see cref="ZoneRepository.DefaultId"/>).
/// </summary>
public static class ZonesPanelEndpoints
{
    public static void MapZonesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/zones", async (ZoneRepository zones) =>
            Results.Json((await zones.AllAsync()).Select(ZoneView.From).ToList(), ApiJson.Options));

        panel.MapPost("/zones", async (ZoneRequest request, ZoneRepository zones, ILoggerFactory logs) =>
        {
            var name = ValidName(request.Name);
            var (result, zone) = await zones.CreateAsync(name);
            if (result == ZoneRepository.ChangeResult.NameTaken)
            {
                throw NameTaken();
            }

            Log(logs).LogInformation("Zone {Id} «{Name}» created in the panel", zone!.Id, zone.Name);
            return Results.Json(new { id = zone.Id, name = zone.Name }, ApiJson.Options, statusCode: StatusCodes.Status201Created);
        });

        panel.MapPatch("/zones/{id}", async (string id, ZoneRequest request, ZoneRepository zones, ILoggerFactory logs) =>
        {
            var name = ValidName(request.Name);
            switch (await zones.RenameAsync(id, name))
            {
                case ZoneRepository.ChangeResult.NotFound:
                    throw ApiException.NotFound("zone");
                case ZoneRepository.ChangeResult.NameTaken:
                    throw NameTaken();
            }

            Log(logs).LogInformation("Zone {Id} renamed to «{Name}» in the panel", id, name);
            return Results.NoContent();
        });

        panel.MapDelete("/zones/{id}", async (string id, ZoneRepository zones, ILoggerFactory logs) =>
        {
            var (result, machines) = await zones.DeleteAsync(id);
            switch (result)
            {
                case ZoneRepository.ChangeResult.NotFound:
                    throw ApiException.NotFound("zone");
                case ZoneRepository.ChangeResult.DefaultZone:
                    throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "The default zone cannot be deleted (it can be renamed)", new { reason = "defaultZone" });
                case ZoneRepository.ChangeResult.InUse:
                    throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "The zone still has machines; move them to another zone first", new { reason = "zoneInUse", machines });
            }

            Log(logs).LogInformation("Zone {Id} deleted in the panel", id);
            return Results.NoContent();
        });

        panel.MapPost("/zones/{id}/machines", async (string id, ZoneAssignRequest request, ZoneRepository zones, ILoggerFactory logs) =>
        {
            if (request.Numbers is not { Count: > 0 } numbers)
            {
                throw ApiException.Validation("numbers", "required");
            }

            if (numbers.Count > ZoneRepository.MaxAssign)
            {
                throw ApiException.Validation("numbers", "tooMany");
            }

            var (assigned, unknown) = await zones.AssignAsync(id, numbers) ?? throw ApiException.NotFound("zone");
            Log(logs).LogInformation("Zone {Id}: {Assigned} machine(s) assigned in the panel, unknown seats: {Unknown}", id, assigned, unknown.Count == 0 ? "-" : string.Join(",", unknown));
            return Results.Json(new ZoneAssignResult(assigned, unknown), ApiJson.Options);
        });
    }

    /// <summary>
    /// Название: 1…40 символов. Пробелы любого вида (неразрывный из Excel или Telegram, табуляция) — один обычный пробел,
    /// по краям обрезаются. Невидимые символы форматирования (U+200B, U+FEFF, метки направления текста) и управляющие —
    /// ошибка: иначе рядом с «VIP» появилась бы такая же на вид «VIP» с невидимым символом, и проверка уникальности её бы
    /// пропустила. Нормализации Unicode (NFC) нет: сервер собран с InvariantGlobalization, Normalize там кириллицу не трогает.
    /// </summary>
    private static string ValidName(string? raw)
    {
        var name = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > ZoneRepository.MaxNameLength)
        {
            throw ApiException.Validation("name", "length");
        }

        if (name.Any(ch => char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format))
        {
            throw ApiException.Validation("name", "format");
        }

        return name;
    }

    private static ApiException NameTaken() =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "A zone with this name already exists", new { reason = "zoneNameTaken" });

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger("Club.Server.Panel");
}
