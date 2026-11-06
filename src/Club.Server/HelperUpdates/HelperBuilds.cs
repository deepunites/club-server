using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Club.Server.Imaging;

namespace Club.Server.HelperUpdates;

/// <summary>
/// Сборка помощника в каталоге сборок. <c>Version</c> — из имени файла; <c>Updatable</c> — не старше
/// <see cref="HelperUpdateOptions.MinTargetVersion"/>: её можно поставить кнопкой, иначе — «только вручную».
/// </summary>
public sealed record HelperBuild(string File, string Version, long Size, string Sha256, DateTimeOffset ModifiedAt, bool Updatable);

/// <summary>
/// Сборки помощника, которые положил администратор (spec 2026-11-06, п. 3.1): только файлы
/// <c>ClubDisklessHelper-X.Y.Z.exe</c> не моложе <see cref="HelperUpdateOptions.MinFileAgeSec"/>. SHA256 сервер считает сам
/// и помнит по размеру и времени изменения: файл заменили — сумма считается заново. Файл, который не читается или пропал
/// посреди чтения, пропускается с записью в журнал — исключений наружу нет.
/// </summary>
public sealed partial class HelperBuilds(HelperUpdateOptions options, ImagingOptions imaging, TimeProvider clock, ILogger<HelperBuilds> logger)
{
    [GeneratedRegex(@"\AClubDisklessHelper-(\d+\.\d+\.\d+)\.exe\z")]
    public static partial Regex FilePattern();

    private readonly ConcurrentDictionary<string, CachedSha> _sha = new(StringComparer.Ordinal);

    /// <summary>Каталог сборок: <see cref="HelperUpdateOptions.ToolsDir"/> или <c>&lt;Imaging:PxeRoot&gt;/tools</c>.</summary>
    public string ToolsDir => string.IsNullOrWhiteSpace(options.ToolsDir) ? Path.Combine(imaging.PxeRoot, "tools") : options.ToolsDir;

    /// <summary>Все годные сборки, новые первыми.</summary>
    public IReadOnlyList<HelperBuild> List()
    {
        IEnumerable<FileInfo> files;
        try
        {
            var directory = new DirectoryInfo(ToolsDir);
            if (!directory.Exists)
            {
                return [];
            }

            files = directory.EnumerateFiles().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Helper builds directory {Directory} is not readable", ToolsDir);
            return [];
        }

        var builds = new List<(Version Version, HelperBuild Build)>();
        foreach (var file in files)
        {
            if (Read(file) is { } build)
            {
                builds.Add(build);
            }
        }

        return builds.OrderByDescending(b => b.Version).ThenBy(b => b.Build.File, StringComparer.Ordinal).Select(b => b.Build).ToList();
    }

    /// <summary>Сборка по имени файла; null — нет файла, имя не по шаблону, файл моложе допустимого или не читается.</summary>
    public HelperBuild? Find(string file)
    {
        if (string.IsNullOrEmpty(file) || !FilePattern().IsMatch(file))
        {
            return null;
        }

        try
        {
            return Read(new FileInfo(Path.Combine(ToolsDir, file)))?.Build;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            logger.LogWarning(ex, "Helper build {File} is not readable", file);
            return null;
        }
    }

    private (Version Version, HelperBuild Build)? Read(FileInfo file)
    {
        var match = FilePattern().Match(file.Name);
        if (!match.Success)
        {
            return null;
        }

        if (!System.Version.TryParse(match.Groups[1].Value, out var version))
        {
            logger.LogWarning("Helper build {File}: version {Version} is not a valid version, skipped", file.Name, match.Groups[1].Value);
            return null;
        }

        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                return null;
            }

            var size = file.Length;
            var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (clock.GetUtcNow() - modified < TimeSpan.FromSeconds(options.MinFileAgeSec))
            {
                return null;
            }

            var sha = _sha.TryGetValue(file.FullName, out var cached) && cached.Size == size && cached.ModifiedAt == modified
                ? cached.Sha256
                : Hash(file, size, modified);
            if (sha is null)
            {
                return null;
            }

            var updatable = System.Version.TryParse(options.MinTargetVersion, out var min) && version >= min;
            return (version, new HelperBuild(file.Name, match.Groups[1].Value, size, sha, modified, updatable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Helper build {File} is not readable, skipped", file.Name);
            return null;
        }
    }

    /// <summary>SHA256 файла; файл поменялся, пока читался, — null (в следующий раз посчитается заново).</summary>
    private string? Hash(FileInfo file, long size, DateTimeOffset modified)
    {
        string sha;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        file.Refresh();
        if (!file.Exists || file.Length != size || new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) != modified)
        {
            logger.LogWarning("Helper build {File} changed while hashing, skipped", file.Name);
            return null;
        }

        _sha[file.FullName] = new CachedSha(size, modified, sha);
        return sha;
    }

    private sealed record CachedSha(long Size, DateTimeOffset ModifiedAt, string Sha256);
}
