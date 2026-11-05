using System.IO.Compression;
using System.Text;

namespace Club.Saves;

/// <summary>Итог упаковки: сумма размеров файлов (до сжатия) и их число.</summary>
public sealed record SavePackResult(long Bytes, int Files);

/// <summary>Итог распаковки: сумма записанных байт и число файлов (пропущенные записи не считаются).</summary>
public sealed record SaveUnpackResult(long Bytes, int Files);

/// <summary>
/// Архив не принят: <see cref="Reason"/> — <c>badArchive</c> (не zip, плохое имя записи, чужой <c>entryId</c>) или
/// <c>overLimit</c> (больше лимита байт или <see cref="SaveArchive.MaxFiles"/> файлов).
/// </summary>
public sealed class SaveArchiveException(string reason, string message) : Exception(message)
{
    public const string BadArchive = "badArchive";
    public const string OverLimit = "overLimit";

    public string Reason { get; } = reason;
}

/// <summary>
/// Zip с папками игрока (spec 2026-11-05, п. 3.4): записи <c>&lt;entryId&gt;/&lt;относительный путь&gt;</c>, где
/// <c>entryId</c> — ID записи списка папок (GUID в формате <c>D</c>, нижний регистр), разделитель — <c>/</c>, имена в
/// UTF-8 (в путях бывает кириллица). Распаковка проверяет каждое имя так же строго, как шаблоны путей
/// (<see cref="SavePathTemplate"/>): нет абсолютных путей, <c>..</c>, двоеточий, имён устройств Windows, и итоговый
/// путь — внутри папки записи. Размер считается по реально записанным байтам, заголовкам архива не верим.
/// </summary>
public static class SaveArchive
{
    public const int MaxFiles = 50_000;

    private const int BufferSize = 81920;

    /// <summary>Пропускаем ссылки (символические, junction): упаковка не уходит из папки записи и не зацикливается.</summary>
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Упаковывает папки записей в <paramref name="output"/> (поток остаётся открытым). Папки, которой нет, просто нет в
    /// архиве (игра на ПК ни разу не запускалась). Пропускаются файлы под <paramref name="excludes"/>
    /// (<see cref="IsExcluded"/>) и файлы, имя которых распаковка не примет (такие на Windows не создаются; так один
    /// странный файл не делает весь архив негодным). Больше <see cref="MaxFiles"/> файлов —
    /// <see cref="SaveArchiveException"/> с <c>overLimit</c>. Скрытые и системные файлы входят.
    /// </summary>
    public static SavePackResult Pack(Stream output, IEnumerable<(Guid EntryId, string Directory)> entries, IReadOnlyList<string> excludes)
    {
        long bytes = 0;
        var files = 0;
        var buffer = new byte[BufferSize];
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var (entryId, directory) in entries)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var prefix = entryId.ToString("D");
            foreach (var file in Directory.EnumerateFiles(directory, "*", AllFiles))
            {
                var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                if (IsExcluded(relative, excludes) || relative.Split('/').Any(s => SavePathTemplate.SegmentProblem(s) is not null))
                {
                    continue;
                }

                if (++files > MaxFiles)
                {
                    throw new SaveArchiveException(SaveArchiveException.OverLimit, $"В папках сохранений больше {MaxFiles} файлов");
                }

                var entry = archive.CreateEntry($"{prefix}/{relative}", CompressionLevel.Fastest);
                entry.LastWriteTime = ZipTime(File.GetLastWriteTime(file));
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
                using var target = entry.Open();
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    target.Write(buffer, 0, read);
                    bytes += read;
                }
            }
        }

        return new SavePackResult(bytes, files);
    }

    /// <summary>
    /// Распаковывает архив: каждая запись — в папку <paramref name="targetForEntry"/>(<c>entryId</c>), существующие файлы
    /// перезаписываются. <c>null</c> — записи нет в списке (её удалили в панели): при <paramref name="unknownIsError"/>
    /// весь архив — <c>badArchive</c> (сервер), иначе запись пропускается (помощник). Сначала проверяются все имена (плохое
    /// имя или файл и папка с одним именем без учёта регистра — ничего не записано), потом файлы пишутся с подсчётом байт: сумма больше <paramref name="limitBytes"/> —
    /// <c>overLimit</c>. При любой ошибке созданные распаковкой файлы и папки удаляются; перезаписанные файлы остаются как
    /// есть. Поток <paramref name="zip"/> лучше давать с перемоткой (файл): иначе zip копируется в память.
    /// </summary>
    public static SaveUnpackResult Unpack(Stream zip, Func<Guid, string?> targetForEntry, long limitBytes, bool unknownIsError)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        }
        catch (InvalidDataException e)
        {
            throw new SaveArchiveException(SaveArchiveException.BadArchive, $"Не zip: {e.Message}");
        }

        using (archive)
        {
            var plan = Plan(archive, targetForEntry, unknownIsError);
            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            try
            {
                return Extract(plan, limitBytes, createdFiles, createdDirectories);
            }
            catch
            {
                Cleanup(createdFiles, createdDirectories);
                throw;
            }
        }
    }

    /// <summary>
    /// Путь (через <c>/</c> или <c>\</c>, относительно папки записи) под исключением: любая его часть равна шаблону без
    /// <c>*</c> и <c>?</c> (<c>Logs</c> — папка или файл с таким именем), или имя файла подходит под маску с ними
    /// (<c>*.dmp</c>). Без учёта регистра.
    /// </summary>
    public static bool IsExcluded(string relativePath, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var pattern in excludes)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern.IndexOfAny(['*', '?']) >= 0)
            {
                if (Wildcard(segments[^1], pattern))
                {
                    return true;
                }
            }
            else if (segments.Any(s => s.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<(ZipArchiveEntry Entry, string Path)> Plan(ZipArchive archive, Func<Guid, string?> targetForEntry, bool unknownIsError)
    {
        var plan = new List<(ZipArchiveEntry, string)>();
        var targets = new Dictionary<Guid, string?>();

        // Файл и папка с одним именем (a и a/b) не создаются ни на одной ФС; без учёта регистра — как на Windows, чтобы
        // сервер не принял архив, на котором споткнётся помощник. Один и тот же файл дважды (вложенные записи списка) —
        // можно.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<ZipArchiveEntry> entries;
        try
        {
            entries = archive.Entries;
        }
        catch (InvalidDataException e)
        {
            throw new SaveArchiveException(SaveArchiveException.BadArchive, $"Повреждённый zip: {e.Message}");
        }

        foreach (var entry in entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/'))
            {
                throw Bad(name);
            }

            var isDirectory = name.EndsWith('/');
            var segments = (isDirectory ? name[..^1] : name).Split('/');
            if (!Guid.TryParseExact(segments[0], "D", out var entryId) || segments[0] != entryId.ToString("D")
                || (!isDirectory && segments.Length < 2) || segments.Skip(1).Any(s => SavePathTemplate.SegmentProblem(s) is not null))
            {
                throw Bad(name);
            }

            if (!targets.TryGetValue(entryId, out var target))
            {
                target = targetForEntry(entryId) is { } t ? Path.GetFullPath(t) : null;
                targets[entryId] = target;
            }

            if (target is null)
            {
                if (unknownIsError)
                {
                    throw new SaveArchiveException(SaveArchiveException.BadArchive, $"Записи {entryId} нет в списке папок сохранений");
                }

                continue;
            }

            if (isDirectory)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine([target, .. segments.Skip(1)]));
            var root = Path.TrimEndingDirectorySeparator(target);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || folders.Contains(path))
            {
                throw Bad(name);
            }

            files.Add(path);
            for (var d = Path.GetDirectoryName(path); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
            {
                if (files.Contains(d))
                {
                    throw Bad(name);
                }

                if (!folders.Add(d))
                {
                    break;
                }
            }

            plan.Add((entry, path));
            if (plan.Count > MaxFiles)
            {
                throw new SaveArchiveException(SaveArchiveException.OverLimit, $"В архиве больше {MaxFiles} файлов");
            }
        }

        return plan;
    }

    private static SaveUnpackResult Extract(List<(ZipArchiveEntry Entry, string Path)> plan, long limitBytes, List<string> createdFiles, List<string> createdDirectories)
    {
        long bytes = 0;
        var buffer = new byte[BufferSize];
        foreach (var (entry, path) in plan)
        {
            CreateDirectory(Path.GetDirectoryName(path)!, createdDirectories);
            if (!File.Exists(path))
            {
                createdFiles.Add(path);
            }

            try
            {
                using (var source = entry.Open())
                using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        bytes += read;
                        if (bytes > limitBytes)
                        {
                            throw new SaveArchiveException(SaveArchiveException.OverLimit, $"Сохранения больше лимита {limitBytes} байт");
                        }

                        target.Write(buffer, 0, read);
                    }
                }

                File.SetLastWriteTime(path, entry.LastWriteTime.LocalDateTime);
            }
            catch (InvalidDataException e)
            {
                throw new SaveArchiveException(SaveArchiveException.BadArchive, $"Повреждённая запись {entry.FullName}: {e.Message}");
            }
        }

        return new SaveUnpackResult(bytes, plan.Count);
    }

    /// <summary>Создаёт папку и запоминает те её уровни, которых не было (для удаления при ошибке).</summary>
    private static void CreateDirectory(string directory, List<string> created)
    {
        var missing = new Stack<string>();
        for (var d = directory; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d))
        {
            missing.Push(d);
        }

        Directory.CreateDirectory(directory);
        created.AddRange(missing);
    }

    private static void Cleanup(List<string> createdFiles, List<string> createdDirectories)
    {
        foreach (var file in createdFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        for (var i = createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(createdDirectories[i]);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static SaveArchiveException Bad(string name) => new(SaveArchiveException.BadArchive, $"Недопустимое имя в архиве: {name}");

    /// <summary>Сравнение путей как у файловой системы: на Windows без учёта регистра.</summary>
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Время файла в пределах zip (1980–2107): иначе <see cref="ZipArchiveEntry.LastWriteTime"/> бросает исключение.</summary>
    private static DateTimeOffset ZipTime(DateTime local)
    {
        var min = new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 30, 0, 0, 0, DateTimeKind.Local);
        return local < min ? min : local > max ? max : local;
    }

    /// <summary>Маска с <c>*</c> (любые символы) и <c>?</c> (один символ), без учёта регистра.</summary>
    private static bool Wildcard(string name, string pattern)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
