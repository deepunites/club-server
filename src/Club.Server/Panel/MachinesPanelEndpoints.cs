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
    DateTimeOffset? BootTime, MachineVolumeView Volume, DateTimeOffset RegisteredAt, string? ImageVersion, ReimageView? Reimage,
    Diskless.SecureBootReport? SecureBoot = null, string? MasterState = null, string BootMode = "local");

/// <summary>
/// Перезаливка машины (последнее задание, если оно не закрыто или закрыто меньше суток назад). <c>state</c>:
/// <c>requested</c> → <c>deploying</c> (<c>step</c>: partition, download, verify, apply, identity, bcdboot) →
/// <c>booting</c> → <c>done</c>; <c>failed</c> (машина остаётся в PXE), <c>cancelled</c>.
/// </summary>
public sealed record ReimageView(
    string State, string Image, string? Step, int? Percent, string? Message, string? Failure, bool DiskTouched, bool PxeArmed,
    int Attempts, DateTimeOffset UpdatedAt);

public sealed record ZoneView(string Id, string Name);

/// <summary>
/// Отправка списка ПК внешней системе (<c>MachineFeed</c>; null — выключена). <c>state</c>: <c>pending</c> (ещё не
/// отправлялся) | <c>ok</c> | <c>failing</c>. <c>target</c> — только хост получателя: путь адреса может нести токен.
/// </summary>
public sealed record MachineFeedView(
    string State, string Target, DateTimeOffset? LastDeliveredAt, DateTimeOffset? LastAttemptAt, int Machines, string? Error, int Failures);

public sealed record MachinesOverview(
    IReadOnlyList<MachineView> Machines, IReadOnlyList<ZoneView> Zones, string? CurrentLibraryVersion,
    int Online, int Offline, int Pending, int Maintenance, string? CurrentImageVersion = null, int Reimaging = 0,
    MachineFeedView? MachineFeed = null);

public sealed record ReimageRequest(string? Image, bool? AllowNewDisk);

public sealed record MachinePatch(int? Number, string? Name, string? Zone, bool? Maintenance);

/// <summary>Экран «Рабочие станции»: реестр машин бездиска, состояние тома на каждом ПК, одобрение новых машин.</summary>
public static class MachinesPanelEndpoints
{
    /// <summary>Помощник отчитывается раз в 30 с; три пропуска подряд — машина не на связи.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public static void MapMachinesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/machines", async (MachineRepository machines, LibraryRepository library, Imaging.ImageRepository images, Integration.MachineFeedOptions feedOptions, Integration.MachineFeedState feed, TimeProvider clock, ILoggerFactory logs) =>
        {
            var logger = logs.CreateLogger("Club.Server.Panel.Sanity");
            var now = clock.GetUtcNow();
            var current = (await library.PointersAsync()).Current?.Label;
            var jobs = await images.LatestJobsAsync();
            var labels = (await images.ImagesAsync()).ToDictionary(i => i.Id, i => i.Label);
            var views = (await machines.AllAsync())
                .Select(m => View(m, current, now, logger, jobs.TryGetValue(m.Id, out var job) ? job : null, labels))
                .ToList();
            return Results.Json(
                new MachinesOverview(
                    views,
                    (await machines.ZonesAsync()).Select(z => new ZoneView(z.Id, z.Name)).ToList(),
                    current,
                    views.Count(v => v.Status == "online"),
                    views.Count(v => v.Status is "offline" or "neverSeen"),
                    views.Count(v => v.Status == "pendingApproval"),
                    views.Count(v => v.Status == "maintenance"),
                    (await images.PointersAsync()).Current?.Label,
                    views.Count(v => v.Status == "reimaging"),
                    FeedView(feedOptions, feed)),
                ApiJson.Options);
        });

        panel.MapPost("/machines/{id:guid}/reimage", async (Guid id, ReimageRequest? request, Imaging.ReimageService reimage, CancellationToken ct) =>
        {
            var job = await reimage.RequestAsync(id, string.IsNullOrWhiteSpace(request?.Image) ? null : request.Image.Trim(), request?.AllowNewDisk ?? false, "panel", ct);
            return Results.Json(new { state = job.State, jobId = job.Id }, ApiJson.Options, statusCode: StatusCodes.Status202Accepted);
        });

        panel.MapPost("/machines/{id:guid}/reimage/cancel", async (Guid id, Imaging.ReimageService reimage, CancellationToken ct) =>
        {
            await reimage.CancelAsync(id, ct);
            return Results.NoContent();
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

    private static MachineFeedView? FeedView(Integration.MachineFeedOptions options, Integration.MachineFeedState feed) =>
        !options.Enabled
            ? null
            : new MachineFeedView(
                feed.LastError is not null ? "failing" : feed.LastDeliveredAt is null ? "pending" : "ok",
                Integration.MachineFeed.Host(options.Url), feed.LastDeliveredAt, feed.LastAttemptAt, feed.Machines, feed.LastError,
                feed.ConsecutiveFailures);

    private static MachineView View(MachineRow m, string? currentLibrary, DateTimeOffset now, ILogger sanity, Imaging.ReimageJob? job, IReadOnlyDictionary<Guid, string> labels)
    {
        var lastSeen = Sane(m.LastSeenAt, now, m, "lastSeenAt", sanity);
        var bootTime = Sane(m.BootTime, now, m, "bootTime", sanity);
        var activeJob = job is not null && Imaging.ReimageJob.ActiveStates.Contains(job.State);
        var status = !m.Approved ? "pendingApproval"
            : activeJob ? "reimaging"
            : m.Maintenance ? "maintenance"
            : lastSeen is null ? "neverSeen"
            : now - lastSeen <= OnlineWindow ? "online"
            : "offline";

        var outdated = m.VolumeState == "mounted" && currentLibrary is not null && m.VolumeVersion != currentLibrary;
        return new MachineView(
            m.Id, m.Number, m.Name, m.ZoneId, status, m.Hostname, string.IsNullOrEmpty(m.IpAddress) ? null : m.IpAddress,
            m.MacAddresses, string.IsNullOrEmpty(m.HelperVersion) ? null : m.HelperVersion, m.OsVersion, lastSeen, bootTime,
            new MachineVolumeView(m.VolumeState, m.VolumeVersion, m.VolumeRoVerified, outdated, m.VolumeError), m.CreatedAt,
            m.ImageVersion,
            job is not null && (activeJob || job.FinishedAt is null || now - job.FinishedAt < TimeSpan.FromDays(1))
                ? new ReimageView(job.State, labels.GetValueOrDefault(job.ImageId, "?"), job.Step, job.Percent, job.Message, job.Failure,
                    job.DiskTouched, job.PxeArmed, job.Attempts, job.UpdatedAt)
                : null,
            Imaging.ReimageService.SecureBoot(m),
            m.MasterState is "mounted" or "mounting" or "failed" ? m.MasterState : null,
            m.BootMode);
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
