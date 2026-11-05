using Club.Saves;

namespace Club.Server.Saves;

/// <summary>
/// Папки игроков на общем диске сохранений (spec 2026-11-05, п. 3.1 и 3.4): <c>&lt;playerId&gt;/&lt;entryId&gt;/…</c> —
/// текущая версия, <c>&lt;playerId&gt;.prev/</c> — предыдущая, <c>.tmp/</c> — приём. Все папки на одном разделе ext4:
/// замена версии — два переименования (<see cref="Directory.Move"/>), каждое атомарно; между ними текущей может не
/// быть — тогда выдаётся <c>.prev</c>. ID игрока сюда приходит уже приведённым (<see cref="PlayerIds.Normalize"/>),
/// другой — <see cref="ArgumentException"/>: путь из ID не выходит за корень. Работа с одним игроком — под его
/// блокировкой (<see cref="SavesRepository.LockPlayerAsync"/>), здесь её нет.
/// </summary>
public sealed class SaveStore(SavesOptions options, ILogger<SaveStore> logger)
{
    private const string PreviousSuffix = ".prev";
    private const string TempFolder = ".tmp";

    /// <summary>Скрытые и системные файлы тоже считаются (на Linux «скрытый» — любой с точкой в начале); ссылки — нет.</summary>
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Для подсчёта занятого в панели: как <see cref="AllFiles"/>, но папки, которые служба не читает, пропускаются —
    /// в корне раздела ext4 лежит <c>lost+found</c> (root, 0700).
    /// </summary>
    private static readonly EnumerationOptions ReadableFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
    };

    private string Root => Path.GetFullPath(options.Root);

    private string Temp => Path.Combine(Root, TempFolder);

    /// <summary>Корень есть и в нём файл-метка: диск сохранений смонтирован. Иначе API сохранений отвечает 503.</summary>
    public bool Available => File.Exists(Path.Combine(Root, SavesOptions.MarkerFile));

    /// <summary>Папка для выдачи: текущая версия, иначе предыдущая (сбой между переименованиями), иначе <c>null</c>.</summary>
    public string? CurrentDirectory(string playerId)
    {
        var current = PlayerDirectory(playerId);
        if (Directory.Exists(current))
        {
            return current;
        }

        var previous = current + PreviousSuffix;
        return Directory.Exists(previous) ? previous : null;
    }

    /// <summary>Новая пустая папка <c>.tmp/&lt;guid&gt;</c> для приёма (<c>.tmp</c> создаётся, если её нет).</summary>
    public string NewTempDirectory()
    {
        var directory = NewTempPath();
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Свободное имя файла <c>.tmp/&lt;guid&gt;.zip</c> — тело приёма или архив выдачи (сам файл не создаётся, <c>.tmp</c> —
    /// создаётся). Удаляет его вызывающий; остаток после падения убирает <see cref="CleanTemp"/>.
    /// </summary>
    public string NewTempZipPath() => NewTempPath() + ".zip";

    /// <summary>Свободное имя <c>.tmp/&lt;guid&gt;</c> (сама папка не создаётся, <c>.tmp</c> — создаётся).</summary>
    private string NewTempPath()
    {
        Directory.CreateDirectory(Temp);
        return Path.Combine(Temp, Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Делает принятую папку <paramref name="tempDirectory"/> (из <see cref="NewTempDirectory"/>) текущей версией:
    /// удаляет <c>.prev</c>, текущая становится <c>.prev</c>, принятая — текущей. Текущей нет (сбой в прошлый раз) —
    /// <c>.prev</c> и есть последняя хорошая версия: она остаётся предыдущей, а не удаляется.
    /// </summary>
    public void Promote(string playerId, string tempDirectory)
    {
        var current = PlayerDirectory(playerId);
        var incoming = Path.GetFullPath(tempDirectory);
        if (!string.Equals(Path.GetDirectoryName(incoming), Temp, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Папка {tempDirectory} — не временная папка приёма в {Temp}", nameof(tempDirectory));
        }

        if (!Directory.Exists(incoming))
        {
            throw new DirectoryNotFoundException($"Временной папки {incoming} нет");
        }

        var previous = current + PreviousSuffix;
        if (Directory.Exists(current))
        {
            if (Directory.Exists(previous))
            {
                Directory.Delete(previous, recursive: true);
            }

            Directory.Move(current, previous);
        }

        Directory.Move(incoming, current);
    }

    /// <summary>
    /// Удаляет текущую версию и <c>.prev</c> игрока (панель: «Удалить»). Нечего удалять — не ошибка. Обе папки сначала
    /// переносятся в <c>.tmp/</c> (переименование атомарно; <c>.prev</c> первой — иначе при сбое между переносами она
    /// выдавалась бы вместо текущей), потом удаляются: сбой посреди удаления не оставляет полупустую папку, которую
    /// выдали бы как сохранение игрока. Что не удалилось — предупреждение в журнале, остаток убирает <see cref="CleanTemp"/>.
    /// </summary>
    public void Delete(string playerId)
    {
        var current = PlayerDirectory(playerId);
        var moved = new List<string>();
        foreach (var directory in new[] { current + PreviousSuffix, current })
        {
            if (Directory.Exists(directory))
            {
                var trash = NewTempPath();
                Directory.Move(directory, trash);
                moved.Add(trash);
            }
        }

        foreach (var trash in moved)
        {
            try
            {
                Directory.Delete(trash, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Saves of player {PlayerId} moved to {Path} but not removed", playerId, trash);
            }
        }
    }

    /// <summary>
    /// Очищает <c>.tmp/</c> при старте сервера: остатки приёмов, прерванных падением. Что не удалилось — предупреждение
    /// в журнале, старт продолжается.
    /// </summary>
    public void CleanTemp()
    {
        if (!Directory.Exists(Temp))
        {
            return;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(Temp))
        {
            try
            {
                if (Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Saves temp {Path} not removed", path);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Saves temp cleaned: {Count} leftovers removed", removed);
        }
    }

    /// <summary>Сумма размеров и число файлов в папке (со всеми подпапками); папки нет — (0, 0).</summary>
    public (long Bytes, int Files) Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long bytes = 0;
        var files = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", AllFiles))
        {
            bytes += file.Length;
            files++;
        }

        return (bytes, files);
    }

    /// <summary>
    /// Сколько занимают файлы под корнем — текущие версии, <c>.prev</c>, временные (состояние в панели); корня нет — 0.
    /// Нечитаемые службой папки не считаются (<see cref="ReadableFiles"/>).
    /// </summary>
    public long UsedBytes() => Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", ReadableFiles).Sum(f => f.Length) : 0;

    /// <summary>
    /// Пишет папку игрока в <paramref name="output"/> архивом выдачи (поток остаётся открытым): каждая подпапка
    /// <c>&lt;entryId&gt;</c> — записи <c>&lt;entryId&gt;/…</c>, через <see cref="SaveArchive.Pack"/> без исключений
    /// (исключения применил помощник при сборке). Подпапки с другими именами пропускаются.
    /// </summary>
    public void WriteZip(string directory, Stream output)
    {
        var entries = new List<(Guid EntryId, string Directory)>();
        foreach (var sub in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            if (Guid.TryParseExact(name, "D", out var entryId) && entryId.ToString("D") == name)
            {
                entries.Add((entryId, sub));
            }
        }

        SaveArchive.Pack(output, entries, []);
    }

    private string PlayerDirectory(string playerId)
    {
        if (PlayerIds.Normalize(playerId) != playerId)
        {
            throw new ArgumentException($"ID игрока «{playerId}» не приведён (PlayerIds.Normalize)", nameof(playerId));
        }

        return Path.Combine(Root, playerId);
    }
}
