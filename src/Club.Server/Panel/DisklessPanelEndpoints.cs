using Club.Server.Api;
using Club.Server.Diskless;
using Club.Server.Library;

namespace Club.Server.Panel;

public sealed record DisklessVersionView(string Version, string? Comment, string? CreatedBy, DateTimeOffset CreatedAt);

public sealed record SeatDiskView(Guid MachineId, string Kind, string Target, string? BaseVersion, string State, string? Error, int Boots, DateTimeOffset? LastBootAt);

public sealed record DisklessOverview(
    bool Enabled, string ImageZvol, string? CurrentVersion, string? RollbackVersion, Guid? MasterMachineId, bool MasterInstall,
    IReadOnlyList<DisklessVersionView> Versions, IReadOnlyList<SeatDiskView> Seats);

public sealed record DisklessPublishRequest(string? Label, string? Comment);

public sealed record DisklessMasterRequest(Guid? MachineId, bool? Install);

public sealed record BootModeRequest(string? Mode);

/// <summary>Панель: полный бездиск (docs/diskless-full.md) — эталон, его версии, режим мастера, режим загрузки машин.</summary>
public static class DisklessPanelEndpoints
{
    public static void MapDisklessPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1");

        panel.MapGet("/diskless", async (DisklessRepository repository, DisklessOptions options) =>
        {
            var image = await repository.GetImageAsync();
            var versions = await repository.VersionsAsync();
            var bySnapshot = versions.ToDictionary(v => v.Snapshot, v => v.Version);
            var seats = (await repository.SeatsAsync()).Select(s => new SeatDiskView(
                s.MachineId, s.Kind, s.TargetName, s.BaseSnapshot is { } b ? bySnapshot.GetValueOrDefault(b) : null, s.State, s.LastError, s.Boots, s.LastBootAt)).ToList();
            return Results.Json(new DisklessOverview(
                options.Enabled, options.ImageZvol, image.CurrentVersion, image.RollbackVersion, image.MasterMachineId, image.MasterInstall,
                versions.Select(v => new DisklessVersionView(v.Version, v.Comment, v.CreatedBy, v.CreatedAt)).ToList(), seats), ApiJson.Options);
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

        panel.MapPut("/machines/{id:guid}/boot-mode", async (Guid id, BootModeRequest request, DisklessImages images) =>
        {
            await Guard(() => images.SetBootModeAsync(id, request.Mode ?? "", "panel"));
            return Results.NoContent();
        });
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
