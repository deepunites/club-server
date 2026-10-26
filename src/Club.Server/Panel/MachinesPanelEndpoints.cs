using Club.Server.Api;
using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Panel;

/// <summary>Том на ПК по последнему отчёту помощника.</summary>
/// <summary><paramref name="Personal"/> — подключён личный диск игр места (games-seat-NN): он на запись по замыслу.</summary>
public sealed record MachineVolumeView(string State, string? LibraryVersion, bool? ReadOnlyVerified, bool Outdated, string? Error, bool Personal = false);

/// <summary>
/// Строка экрана «Рабочие станции». <c>status</c>: <c>pendingApproval</c> | <c>maintenance</c> | <c>online</c> |
/// <c>offline</c> | <c>neverSeen</c>. Значения, не прошедшие проверку на вменяемость, отдаются как null (на экране — прочерк).
/// <c>masterError</c> — причина из отчёта помощника о мастер-томе (у <c>failed</c> и <c>mounting</c>).
/// </summary>
public sealed record MachineView(
    Guid Id, int Number, string Name, string Zone, string Status, string Hostname, string? IpAddress,
    IReadOnlyList<string> MacAddresses, string? HelperVersion, string? OsVersion, DateTimeOffset? LastSeenAt,
    DateTimeOffset? BootTime, MachineVolumeView Volume, DateTimeOffset RegisteredAt, string? ImageVersion, ReimageView? Reimage,
    Diskless.SecureBootReport? SecureBoot = null, string? MasterState = null, string BootMode = "local", string? MasterError = null,
    IReadOnlyList<MachineLibraryView>? Libraries = null);

/// <summary>
/// Вторая (третья, …) библиотека на ПК — по последнему отчёту помощника о ней (<c>machine_library</c>; нет отчёта — том
/// <c>none</c>). <c>supported</c> — помощник ПК её умеет (Libraries:&lt;ключ&gt;:PersonalMinHelper, 1.6.0): иначе ПК
/// её личного диска не получает. <c>volume</c> — как у G: (<see cref="MachineVolumeView"/>), <c>personal</c> — подключён
/// личный диск этой библиотеки. Мастер-том на её букве — <c>masterState</c>, <c>masterError</c>, как у G:.
/// </summary>
public sealed record MachineLibraryView(
    string Key, string DriveLetter, bool Supported, MachineVolumeView Volume, string? MasterState = null, string? MasterError = null);

/// <summary>
/// Перезаливка машины (последнее задание, если оно не закрыто или закрыто меньше суток назад). <c>state</c>:
/// <c>requested</c> → <c>deploying</c> (<c>step</c>: partition, download, verify, apply, identity, bcdboot) →
/// <c>booting</c> → <c>done</c>; <c>failed</c> (машина остаётся в PXE), <c>cancelled</c>. <c>allowNotGeneralized</c> —
/// задание разрешает образ без sysprep (эталонный ПК).
/// </summary>
public sealed record ReimageView(
    string State, string Image, string? Step, int? Percent, string? Message, string? Failure, bool DiskTouched, bool PxeArmed,
    int Attempts, DateTimeOffset UpdatedAt, bool AllowNotGeneralized);

/// <summary>Зона в списке панели: <c>machines</c> — сколько машин реестра в ней, <c>isDefault</c> — зона новых машин (не удаляется).</summary>
public sealed record ZoneView(string Id, string Name, int Machines, bool IsDefault)
{
    public static ZoneView From(ZoneRow zone) => new(zone.Id, zone.Name, zone.Machines, zone.Id == ZoneRepository.DefaultId);
}

/// <summary>
/// Отправка списка ПК внешней системе (<c>MachineFeed</c>; null — выключена). <c>state</c>: <c>pending</c> (ещё не
/// отправлялся) | <c>ok</c> | <c>failing</c>. <c>target</c> — только хост получателя: путь адреса может нести токен.
/// </summary>
public sealed record MachineFeedView(
    string State, string Target, DateTimeOffset? LastDeliveredAt, DateTimeOffset? LastAttemptAt, int Machines, string? Error, int Failures);

/// <summary>
/// <c>masterDriveLetter</c> — буква мастер-тома на ПК правки (Library:MasterDriveLetter), для отметки у машины.
/// <c>libraries</c> — работающие библиотеки не-G: (нет таких — поля нет, как и <c>libraries</c> у машин).
/// </summary>
public sealed record MachinesOverview(
    IReadOnlyList<MachineView> Machines, IReadOnlyList<ZoneView> Zones, string? CurrentLibraryVersion,
    int Online, int Offline, int Pending, int Maintenance, string? CurrentImageVersion = null, int Reimaging = 0,
    MachineFeedView? MachineFeed = null, string? MasterDriveLetter = null, IReadOnlyList<MachinesLibraryView>? Libraries = null);

/// <summary>Библиотека не-G: в обзоре «Рабочих станций»: название, текущая версия (по ней <c>outdated</c>) и буква мастер-тома.</summary>
public sealed record MachinesLibraryView(string Key, string Title, string? CurrentVersion, string MasterDriveLetter);

/// <summary><c>allowNotGeneralized</c> — только API (в панели нет): залить образ без sysprep, только этим заданием.</summary>
public sealed record ReimageRequest(string? Image, bool? AllowNewDisk, bool? AllowNotGeneralized);

public sealed record MachinePatch(int? Number, string? Name, string? Zone, bool? Maintenance);

/// <summary>Экран «Рабочие станции»: реестр машин бездиска, состояние тома на каждом ПК, одобрение новых машин.</summary>
public static class MachinesPanelEndpoints
{
    /// <summary>Помощник отчитывается раз в 30 с; три пропуска подряд — машина не на связи.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public static void MapMachinesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/machines", async (MachineRepository machines, ZoneRepository zones, LibraryRepository library, LibraryOptions libraryOptions, LibrarySet libraries, Imaging.ImageRepository images, Integration.MachineFeedOptions feedOptions, Integration.MachineFeedState feed, TimeProvider clock, ILoggerFactory logs) =>
        {
            var logger = logs.CreateLogger("Club.Server.Panel.Sanity");
            var now = clock.GetUtcNow();
            var current = (await library.PointersAsync()).Current?.Label;
            var jobs = await images.LatestJobsAsync();
            var labels = (await images.ImagesAsync()).ToDictionary(i => i.Id, i => i.Label);
            var extras = new List<ExtraLibrary>();
            foreach (var slot in libraries.Extras.Where(s => s.Options.Enabled))
            {
                extras.Add(new ExtraLibrary(slot, (await slot.Repository.PointersAsync()).Current?.Label, (await slot.Machines!.AllAsync()).ToDictionary(r => r.MachineId)));
            }

            var views = (await machines.AllAsync())
                .Select(m => View(m, current, now, logger, jobs.TryGetValue(m.Id, out var job) ? job : null, labels) with
                {
                    Libraries = extras.Count == 0 ? null : extras.Select(e => LibraryView(m, e)).ToList(),
                })
                .ToList();
            return Results.Json(
                new MachinesOverview(
                    views,
                    (await zones.AllAsync()).Select(ZoneView.From).ToList(),
                    current,
                    views.Count(v => v.Status == "online"),
                    views.Count(v => v.Status is "offline" or "neverSeen"),
                    views.Count(v => v.Status == "pendingApproval"),
                    views.Count(v => v.Status == "maintenance"),
                    (await images.PointersAsync()).Current?.Label,
                    views.Count(v => v.Status == "reimaging"),
                    FeedView(feedOptions, feed),
                    libraryOptions.MasterDriveLetter,
                    extras.Count == 0 ? null : extras.Select(e => new MachinesLibraryView(e.Slot.Key, e.Slot.Title, e.Current, e.Slot.Options.MasterDriveLetter)).ToList()),
                ApiJson.Options);
        });

        panel.MapPost("/machines/{id:guid}/reimage", async (Guid id, ReimageRequest? request, Imaging.ReimageService reimage, CancellationToken ct) =>
        {
            var job = await reimage.RequestAsync(id, string.IsNullOrWhiteSpace(request?.Image) ? null : request.Image.Trim(), request?.AllowNewDisk ?? false,
                request?.AllowNotGeneralized ?? false, "panel", ct);
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

    /// <summary>Работающая библиотека не-G: для экрана: текущая версия и отчёты ПК о ней.</summary>
    private sealed record ExtraLibrary(LibrarySlot Slot, string? Current, IReadOnlyDictionary<Guid, LibraryMachineRow> Reports);

    /// <summary>Библиотека не-G: на ПК — как <see cref="MachineView.Volume"/> и мастер-том у G:, только из её отчёта.</summary>
    private static MachineLibraryView LibraryView(MachineRow m, ExtraLibrary library)
    {
        var options = library.Slot.Options;
        if (!library.Reports.TryGetValue(m.Id, out var r))
        {
            return new MachineLibraryView(library.Slot.Key, options.DriveLetter, options.SupportsHelper(m.HelperVersion), new MachineVolumeView("none", null, null, false, null));
        }

        var outdated = r.VolumeState == "mounted" && library.Current is not null && r.VolumeVersion != library.Current;
        return new MachineLibraryView(
            library.Slot.Key, options.DriveLetter, options.SupportsHelper(m.HelperVersion),
            new MachineVolumeView(r.VolumeState, r.VolumeVersion, null, outdated, r.VolumeError,
                r.VolumeIqn?.Contains($":{library.Slot.Names.SeatTargetPrefix}", StringComparison.Ordinal) == true),
            r.MasterState is "mounted" or "mounting" or "failed" ? r.MasterState : null,
            r.MasterState is "mounting" or "failed" ? r.MasterError : null);
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
            new MachineVolumeView(m.VolumeState, m.VolumeVersion, m.VolumeRoVerified, outdated, m.VolumeError,
                m.VolumeIqn?.Contains($":{Library.SeatGames.TargetPrefix}", StringComparison.Ordinal) == true), m.CreatedAt,
            m.ImageVersion,
            job is not null && (activeJob || job.FinishedAt is null || now - job.FinishedAt < TimeSpan.FromDays(1))
                ? new ReimageView(job.State, labels.GetValueOrDefault(job.ImageId, "?"), job.Step, job.Percent, job.Message, job.Failure,
                    job.DiskTouched, job.PxeArmed, job.Attempts, job.UpdatedAt, job.AllowNotGeneralized)
                : null,
            Imaging.ReimageService.SecureBoot(m),
            m.MasterState is "mounted" or "mounting" or "failed" ? m.MasterState : null,
            m.BootMode,
            m.MasterState is "mounting" or "failed" ? m.MasterError : null);
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
