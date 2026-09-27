using Club.Server.Api;
using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Panel;

/// <summary>Том на ПК по последнему отчёту помощника.</summary>
public sealed record MachineVolumeView(string State, string? LibraryVersion, bool? ReadOnlyVerified, bool Outdated, string? Error);

/// <summary>
/// Строка экрана «Рабочие станции». <c>status</c>: <c>pendingApproval</c> | <c>maintenance</c> | <c>online</c> |
/// <c>offline</c> | <c>neverSeen</c>. Значения, не прошедшие проверку на вменяемость, отдаются как null (на экране — прочерк).
/// </summary>
public sealed record MachineView(
    Guid Id, int Number, string Name, string Zone, string Status, string Hostname, string? IpAddress,
    IReadOnlyList<string> MacAddresses, string? HelperVersion, string? OsVersion, DateTimeOffset? LastSeenAt,
    DateTimeOffset? BootTime, MachineVolumeView Volume, DateTimeOffset RegisteredAt);

public sealed record ZoneView(string Id, string Name);

public sealed record MachinesOverview(
    IReadOnlyList<MachineView> Machines, IReadOnlyList<ZoneView> Zones, string? CurrentLibraryVersion,
    int Online, int Offline, int Pending, int Maintenance);

public sealed record MachinePatch(int? Number, string? Name, string? Zone, bool? Maintenance);

/// <summary>Экран «Рабочие станции»: реестр машин бездиска, состояние тома на каждом ПК, одобрение новых машин.</summary>
public static class MachinesPanelEndpoints
{
    /// <summary>Помощник отчитывается раз в 30 с; три пропуска подряд — машина не на связи.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public static void MapMachinesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/machines", async (MachineRepository machines, LibraryRepository library, TimeProvider clock, ILoggerFactory logs) =>
        {
            var logger = logs.CreateLogger("Club.Server.Panel.Sanity");
            var now = clock.GetUtcNow();
            var current = (await library.PointersAsync()).Current?.Label;
            var views = (await machines.AllAsync()).Select(m => View(m, current, now, logger)).ToList();
            return Results.Json(
                new MachinesOverview(
                    views,
                    (await machines.ZonesAsync()).Select(z => new ZoneView(z.Id, z.Name)).ToList(),
                    current,
                    views.Count(v => v.Status == "online"),
                    views.Count(v => v.Status is "offline" or "neverSeen"),
                    views.Count(v => v.Status == "pendingApproval"),
                    views.Count(v => v.Status == "maintenance")),
                ApiJson.Options);
        });

        panel.MapPost("/machines/{id:guid}/approve", async (Guid id, MachineRepository machines, ILoggerFactory logs) =>
        {
            if (!await machines.ApproveAsync(id))
            {
                throw ApiException.NotFound("machine");
            }

            logs.CreateLogger("Club.Server.Panel").LogInformation("Machine {Id} approved from panel", id);
            return Results.NoContent();
        });

        panel.MapPost("/machines/{id:guid}/reject", async (Guid id, MachineRepository machines) =>
        {
            var machine = await machines.FindAsync(id) ?? throw ApiException.NotFound("machine");
            if (machine.Approved || !await machines.RejectPendingAsync(id))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Only a machine waiting for approval can be rejected", new { reason = "alreadyApproved" });
            }

            return Results.NoContent();
        });

        panel.MapPatch("/machines/{id:guid}", async (Guid id, MachinePatch patch, MachineRepository machines) =>
        {
            if (patch.Number is < 1 or > 9999)
            {
                throw ApiException.Validation("number", "range");
            }

            if (patch.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 64))
            {
                throw ApiException.Validation("name", "length");
            }

            var result = await machines.UpdateAsync(id, new MachineRepository.MachineChanges(patch.Number, patch.Name?.Trim(), patch.Zone, patch.Maintenance));
            return result switch
            {
                MachineRepository.UpdateResult.Updated => Results.NoContent(),
                MachineRepository.UpdateResult.NotFound => throw ApiException.NotFound("machine"),
                MachineRepository.UpdateResult.NumberTaken => throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Seat number is already taken", new { reason = "numberTaken" }),
                _ => throw ApiException.Validation("zone", "unknown"),
            };
        });
    }

    private static MachineView View(MachineRow m, string? currentLibrary, DateTimeOffset now, ILogger sanity)
    {
        var lastSeen = Sane(m.LastSeenAt, now, m, "lastSeenAt", sanity);
        var bootTime = Sane(m.BootTime, now, m, "bootTime", sanity);
        var status = !m.Approved ? "pendingApproval"
            : m.Maintenance ? "maintenance"
            : lastSeen is null ? "neverSeen"
            : now - lastSeen <= OnlineWindow ? "online"
            : "offline";

        var outdated = m.VolumeState == "mounted" && currentLibrary is not null && m.VolumeVersion != currentLibrary;
        return new MachineView(
            m.Id, m.Number, m.Name, m.ZoneId, status, m.Hostname, string.IsNullOrEmpty(m.IpAddress) ? null : m.IpAddress,
            m.MacAddresses, string.IsNullOrEmpty(m.HelperVersion) ? null : m.HelperVersion, m.OsVersion, lastSeen, bootTime,
            new MachineVolumeView(m.VolumeState, m.VolumeVersion, m.VolumeRoVerified, outdated, m.VolumeError), m.CreatedAt);
    }

    /// <summary>
    /// Проверка на вменяемость: время из будущего (часы ПК или сервера сбиты) не показывается — на экране прочерк,
    /// в логе запись. Невозможное число на экране убивает доверие ко всем остальным.
    /// </summary>
    private static DateTimeOffset? Sane(DateTimeOffset? value, DateTimeOffset now, MachineRow m, string field, ILogger sanity)
    {
        if (value is { } v && v > now.AddMinutes(5))
        {
            sanity.LogWarning("Machine {Number} ({Id}): {Field}={Value:o} is in the future; hidden in panel", m.Number, m.Id, field, v);
            return null;
        }

        return value;
    }
}
