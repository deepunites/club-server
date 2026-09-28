using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Club.Server.Imaging;

public sealed record IncomingFile(string Name, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>
/// Образы Windows на диске сервера. Импорт — намерение в БД, потом шаги, каждый из которых можно повторить после
/// падения: перенос <c>incoming/x.wim → images/&lt;версия&gt;/install.wim</c> (через <c>.partial</c>), разбор WIM, sha256.
/// Хранятся текущая и откатная версии; вытесненная удаляется, когда её не использует ни одна перезаливка.
/// </summary>
public sealed partial class ImageLibrary(ImagingOptions options, ImageRepository repository, TimeProvider clock, ILogger<ImageLibrary> logger)
{
    public const string ImageFileName = "install.wim";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    public static partial Regex LabelPattern();

    private string Incoming => Path.Combine(options.Root, "incoming");

    public string FullPath(WindowsImage image) => Path.Combine(options.Root, image.FilePath);

    public IReadOnlyList<IncomingFile> IncomingFiles()
    {
        if (!Directory.Exists(Incoming))
        {
            return [];
        }

        return new DirectoryInfo(Incoming).EnumerateFiles()
            .Where(f => f.Extension.Equals(".wim", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".esd", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new IncomingFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    public enum ImportResult
    {
        Accepted,
        BadLabel,
        NoSuchFile,
        LabelTaken,
    }

    public async Task<ImportResult> RequestImportAsync(string file, string label, int index, string? requestedBy)
    {
        if (!LabelPattern().IsMatch(label))
        {
            return ImportResult.BadLabel;
        }

        if (Path.GetFileName(file) != file || !IncomingFiles().Any(f => f.Name == file))
        {
            return ImportResult.NoSuchFile;
        }

        var ok = await repository.CreateImportAsync(Guid.NewGuid(), label, file, Path.Combine("images", label, ImageFileName), Math.Max(1, index), requestedBy);
        return ok ? ImportResult.Accepted : ImportResult.LabelTaken;
    }

    /// <summary>Все незавершённые импорты. Недоступный файл или битый WIM — импорт помечается как неудачный.</summary>
    public async Task ProcessImportsAsync(CancellationToken ct)
    {
        foreach (var image in await repository.ImagesInStateAsync("importing"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ImportAsync(image, ct) is { } failure)
                {
                    logger.LogWarning("Image {Label} import failed: {Failure}", image.Label, failure);
                    await repository.MarkImportFailedAsync(image.Id, failure);
                }
            }
            catch (IOException ex)
            {
                // Диск или сетевой том недоступен — не приговор образу, повторим на следующем проходе.
                logger.LogWarning(ex, "Image {Label} import step failed, will retry", image.Label);
                await repository.RecordImportErrorAsync(image.Id, ex.Message);
            }
        }
    }

    private async Task<string?> ImportAsync(WindowsImage image, CancellationToken ct)
    {
        var source = Path.Combine(Incoming, image.SourceFile);
        var target = FullPath(image);
        var partial = target + ".partial";
        if (File.Exists(source))
        {
            // Источник на месте — перенос не завершён (или не начинался): переносим заново.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Delete(partial);
            File.Delete(target);
            File.Move(source, partial);
        }

        if (File.Exists(partial))
        {
            File.Move(partial, target, overwrite: true);
        }

        if (!File.Exists(target))
        {
            return $"Файл {image.SourceFile} не найден в incoming";
        }

        IReadOnlyList<WimImage> images;
        try
        {
            images = WimFile.ReadImages(target);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or EndOfStreamException or FormatException)
        {
            return $"Не WIM или повреждён: {ex.Message}";
        }

        if (images.All(i => i.Index != image.ImageIndex))
        {
            return $"В WIM нет образа с индексом {image.ImageIndex} (есть: {string.Join(", ", images.Select(i => i.Index))})";
        }

        string sha256;
        long size;
        await using (var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }

        await repository.MarkImportedAsync(image.Id, size, sha256, images, clock.GetUtcNow());
        logger.LogInformation("Image {Label} imported: {Size} bytes, sha256 {Sha256}", image.Label, size, sha256);
        return null;
    }

    /// <summary>
    /// Удаление файлов версий, вытесненных публикацией (не текущая, не откатная, уже публиковались), а также
    /// удалённых администратором неопубликованных. Пока версию использует незавершённая заливка — ждём.
    /// </summary>
    public async Task<IReadOnlyList<(string Label, string Reason)>> RetireAsync(CancellationToken ct)
    {
        var pointers = await repository.PointersAsync();
        var keep = new HashSet<Guid?> { pointers.Current?.Id, pointers.Rollback?.Id };
        var inUse = await repository.ImagesInUseAsync();
        var blocked = new List<(string, string)>();
        foreach (var image in await repository.ImagesInStateAsync("ready"))
        {
            ct.ThrowIfCancellationRequested();
            if (keep.Contains(image.Id) || image.PublishedAt is null)
            {
                continue;
            }

            if (inUse.Contains(image.Id))
            {
                blocked.Add((image.Label, "идёт перезаливка этой версией"));
                continue;
            }

            DeleteFiles(image);
            await repository.MarkRetiredAsync(image.Id, clock.GetUtcNow());
            logger.LogInformation("Image {Label} retired", image.Label);
        }

        return blocked;
    }

    /// <summary>Удаление неопубликованной версии по команде администратора.</summary>
    public async Task<bool> DeleteUnpublishedAsync(WindowsImage image)
    {
        var pointers = await repository.PointersAsync();
        if (image.PublishedAt is not null || image.State is not ("ready" or "failed") || image.Id == pointers.Current?.Id || image.Id == pointers.Rollback?.Id
            || (await repository.ImagesInUseAsync()).Contains(image.Id))
        {
            return false;
        }

        DeleteFiles(image);
        await repository.MarkRetiredAsync(image.Id, clock.GetUtcNow());
        return true;
    }

    private void DeleteFiles(WindowsImage image)
    {
        var directory = Path.GetDirectoryName(FullPath(image))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
