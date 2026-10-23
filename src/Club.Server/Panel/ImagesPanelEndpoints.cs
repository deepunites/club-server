using Club.Server.Api;
using Club.Server.Imaging;
using Club.Server.Library;

namespace Club.Server.Panel;

/// <summary><c>role</c>: <c>current</c> | <c>rollback</c> | null. <c>state</c>: importing, ready, failed, retired.</summary>
public sealed record ImageView(
    string Label, string State, string? Role, long? SizeBytes, string? Sha256, int ImageIndex, IReadOnlyList<WimImage> WimImages,
    bool? Generalized, DateTimeOffset CreatedAt, DateTimeOffset? ImportedAt, DateTimeOffset? PublishedAt, string? LastError);

public sealed record ImagesOverview(
    bool Enabled, string? Current, string? Rollback, IReadOnlyList<ImageView> Images, IReadOnlyList<IncomingFile> Incoming,
    IReadOnlyList<StorageWarningView> Warnings, BootChainReport? BootFiles);

/// <summary>
/// <c>notGeneralized</c> — только API: образ без sysprep (эталон гибрида), версия сразу <c>generalized: false</c>. То же
/// делает описание образа в WIM со словами «not generalized» (make-hybrid-ref.py).
/// </summary>
public sealed record ImportRequest(string? File, string? Label, int? Index, bool? NotGeneralized);

/// <summary>Экран «Образы Windows»: импорт install.wim из incoming, публикация, откат. Хранятся текущая и откатная версии.</summary>
public static class ImagesPanelEndpoints
{
    public static void MapImagesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1/images");

        panel.MapGet("", async (ImagingOptions options, ImageRepository images, ImageLibrary library, LibraryRepository warnings, BootFiles bootFiles) =>
        {
            var pointers = await images.PointersAsync();
            var views = (await images.ImagesAsync())
                .Where(i => i.State != "retired")
                .Select(i => new ImageView(
                    i.Label, i.State, i.Id == pointers.Current?.Id ? "current" : i.Id == pointers.Rollback?.Id ? "rollback" : null,
                    i.SizeBytes, i.Sha256, i.ImageIndex, i.WimImages, i.KnownNotGeneralized ? false : i.Generalized, i.CreatedAt, i.ImportedAt,
                    i.PublishedAt, i.LastError))
                .ToList();
            return Results.Json(
                new ImagesOverview(
                    options.Enabled, pointers.Current?.Label, pointers.Rollback?.Label, views, options.Enabled ? library.IncomingFiles() : [],
                    (await warnings.ActiveWarningsAsync("imaging")).Select(w => new StorageWarningView(w.Kind, w.Subject, w.Message, w.FirstSeen, w.LastSeen)).ToList(),
                    options.Enabled ? bootFiles.Check() : null),
                ApiJson.Options);
        });

        panel.MapPost("/import", async (ImportRequest request, ImagingOptions options, ImageLibrary library) =>
        {
            RequireEnabled(options);
            var result = await library.RequestImportAsync(request.File?.Trim() ?? "", request.Label?.Trim() ?? "", request.Index ?? 1, request.NotGeneralized ?? false, "panel");
            return result switch
            {
                ImageLibrary.ImportResult.Accepted => Results.Accepted(),
                ImageLibrary.ImportResult.BadLabel => throw ApiException.Validation("label", "format"),
                ImageLibrary.ImportResult.NoSuchFile => throw ApiException.Validation("file", "notFound"),
                _ => throw ReimageService.Conflict("labelTaken", "Image version with this label already exists"),
            };
        });

        panel.MapPost("/{label}/publish", async (string label, ImagingOptions options, ImageRepository images, TimeProvider clock, ILoggerFactory logs) =>
        {
            RequireEnabled(options);
            var image = await images.FindImageAsync(label) ?? throw ApiException.NotFound("image");
            if (image.State != "ready")
            {
                throw ReimageService.Conflict("imageNotReady", $"Image {label} is {image.State}");
            }

            if (image.KnownNotGeneralized)
            {
                // Текущая версия — то, что получает любой ПК по умолчанию: без sysprep у всех был бы один MachineGUID.
                throw ReimageService.Conflict("imageNotGeneralized",
                    $"Image {label} is not generalized with sysprep; it cannot be published, only reinstalled on the reference PC (allowNotGeneralized)");
            }

            await images.PublishAsync(image.Id, clock.GetUtcNow());
            logs.CreateLogger("Club.Server.Panel").LogInformation("Windows image {Label} published", label);
            return Results.NoContent();
        });

        panel.MapPost("/rollback", async (ImagingOptions options, ImageRepository images) =>
        {
            RequireEnabled(options);
            if ((await images.PointersAsync()).Rollback is { KnownNotGeneralized: true } rollback)
            {
                // Откат делает откатную текущей — то же правило, что у публикации (откатную без sysprep нашла заливка).
                throw ReimageService.Conflict("imageNotGeneralized",
                    $"Rollback image {rollback.Label} is not generalized with sysprep; it cannot become the current version");
            }

            return await images.RollbackAsync() ? Results.NoContent() : throw ReimageService.Conflict("noRollback", "No rollback image");
        });

        panel.MapDelete("/{label}", async (string label, ImageRepository images, ImageLibrary library) =>
        {
            var image = await images.FindImageAsync(label) ?? throw ApiException.NotFound("image");
            return await library.DeleteUnpublishedAsync(image)
                ? Results.NoContent()
                : throw ReimageService.Conflict("inUse", "Only an unpublished image that no reinstall uses can be deleted");
        });
    }

    private static void RequireEnabled(ImagingOptions options)
    {
        if (!options.Enabled)
        {
            throw ReimageService.Conflict("imagingDisabled", "Windows imaging is disabled (Imaging:Enabled)");
        }
    }
}
