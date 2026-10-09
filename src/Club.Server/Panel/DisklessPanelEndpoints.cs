using Club.Server.Api;
using Club.Server.Diskless;
using Club.Server.Library;

namespace Club.Server.Panel;

public sealed record DisklessVersionView(string Version, string? Comment, string? CreatedBy, DateTimeOffset CreatedAt);

public sealed record SeatDiskView(
    Guid MachineId, int? Number, string? MachineName, string Kind, string Target, string? BaseVersion, string State, string? Error, int Boots,
    DateTimeOffset? LastBootAt);

/// <summary>Одобренная машина: кандидат в мастер эталона; <c>bootMode</c> — как она грузится сейчас.</summary>
public sealed record DisklessMachineView(Guid Id, int Number, string Name, string BootMode, bool Online);

public sealed record DisklessOverview(
    bool Enabled, string ImageZvol, string? CurrentVersion, string? RollbackVersion, Guid? MasterMachineId, string? MasterMachineName,
    bool MasterInstall, IReadOnlyList<DisklessVersionView> Versions, IReadOnlyList<SeatDiskView> Seats, IReadOnlyList<DisklessMachineView> Machines);

public sealed record DisklessPublishRequest(string? Label, string? Comment);

public sealed record DisklessMasterRequest(Guid? MachineId, bool? Install);

public sealed record BootModeRequest(string? Mode);

/// <summary>Новый ПК по MAC (бездисковый ПК без своей Windows). <c>bootMode</c> по умолчанию — <c>diskless</c>.</summary>
public sealed record AddMachineRequest(string? MacAddress, int? Number, string? Name, string? BootMode);

/// <summary>Панель: полный бездиск (docs/diskless-full.md) — эталон, его версии, режим мастера, режим загрузки машин.</summary>
public static class DisklessPanelEndpoints
{
    public static void MapDisklessPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/diskless", async (DisklessRepository repository, Data.MachineRepository machines, DisklessOptions options, TimeProvider clock) =>
        {
            var image = await repository.GetImageAsync();
            var versions = await repository.VersionsAsync();
            var bySnapshot = versions.ToDictionary(v => v.Snapshot, v => v.Version);
            var registry = (await machines.AllAsync()).Where(m => m.Approved).ToList();
            var byId = registry.ToDictionary(m => m.Id);
            var seats = (await repository.SeatsAsync()).Select(s => new SeatDiskView(
                s.MachineId, byId.GetValueOrDefault(s.MachineId)?.Number, byId.GetValueOrDefault(s.MachineId)?.Name, s.Kind, s.TargetName,
                s.BaseSnapshot is { } b ? bySnapshot.GetValueOrDefault(b) : null, s.State, s.LastError, s.Boots, s.LastBootAt))
                .OrderBy(s => s.Number ?? int.MaxValue).ToList();
            var now = clock.GetUtcNow();
            var candidates = registry.Select(m => new DisklessMachineView(m.Id, m.Number, m.Name, m.BootMode,
                m.LastSeenAt is { } seen && now - seen < MachinesPanelEndpoints.OnlineWindow)).ToList();
            var master = image.MasterMachineId is { } id ? byId.GetValueOrDefault(id)?.Name : null;
            return Results.Json(new DisklessOverview(
                options.Enabled, options.ImageZvol, image.CurrentVersion, image.RollbackVersion, image.MasterMachineId, master, image.MasterInstall,
                versions.Select(v => new DisklessVersionView(v.Version, v.Comment, v.CreatedBy, v.CreatedAt)).ToList(), seats, candidates), ApiJson.Options);
        });

        panel.MapPost("/diskless/versions", async (DisklessPublishRequest request, DisklessImages images, CancellationToken ct) =>
        {
            await Guard(() => images.PublishAsync(request.Label?.Trim() ?? "", request.Comment, "panel", ct));
            return Results.NoContent();
        });

        panel.MapPost("/diskless/rollback", async (DisklessImages images) =>
        {
            await Guard(() => images.RollbackAsync("panel"));
            return Results.NoContent();
        });

        panel.MapPut("/diskless/master", async (DisklessMasterRequest request, DisklessImages images) =>
        {
            await Guard(() => images.SetMasterAsync(request.MachineId, request.Install ?? false, "panel"));
            return Results.NoContent();
        });

        panel.MapPost("/machines", async (AddMachineRequest request, Data.MachineRepository machines, ILoggerFactory logs) =>
        {
            var mac = Imaging.ReimageService.NormalizeMac(request.MacAddress ?? "");
            if (mac is null)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "MAC address like aa:bb:cc:dd:ee:ff is required", new { field = "macAddress", reason = "format" });
            }

            var mode = request.BootMode ?? "diskless";
            if (mode is not ("local" or "diskless") || request.Number is <= 0 or > 9999 || request.Name is { Length: > 64 })
            {
                throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Invalid number, name or boot mode", new { field = "machine", reason = "format" });
            }

            var (result, machine) = await machines.AddByMacAsync(mac, request.Number, request.Name, mode);
            return result switch
            {
                Data.MachineRepository.AddResult.MacTaken => throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "This MAC address already belongs to a machine", new { reason = "macTaken" }),
                Data.MachineRepository.AddResult.NumberTaken => throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Seat number is already taken", new { reason = "numberTaken" }),
                _ => LogAdded(logs, machine!),
            };
        });

        panel.MapPut("/machines/{id:guid}/boot-mode", async (Guid id, BootModeRequest request, DisklessImages images) =>
        {
            await Guard(() => images.SetBootModeAsync(id, request.Mode ?? "", "panel"));
            return Results.NoContent();
        });
    }

    private static IResult LogAdded(ILoggerFactory logs, Data.MachineRow machine)
    {
        logs.CreateLogger("Club.Server.Panel").LogInformation("Machine {Name} (seat {Number}, {Mac}) added by MAC in the panel, boot mode {Mode}",
            machine.Name, machine.Number, machine.MacAddresses[0], machine.BootMode);
        return Results.Json(new { id = machine.Id, number = machine.Number, name = machine.Name }, ApiJson.Options, statusCode: StatusCodes.Status201Created);
    }

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (LibraryRequestException ex) when (ex.Reason is "label" or "mode")
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, ex.Message, new { field = ex.Reason, reason = "format" });
        }
        catch (LibraryRequestException ex) when (ex.Reason == "noMachine")
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, ex.Message, new { reason = ex.Reason });
        }
        catch (LibraryRequestException ex)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, ex.Message, new { reason = ex.Reason });
        }
    }
}
