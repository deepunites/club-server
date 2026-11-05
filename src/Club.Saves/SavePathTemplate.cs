namespace Club.Saves;

/// <summary>Где лежит папка записи списка: в профиле Windows игрока или в папке игры на букве библиотеки.</summary>
public enum SaveTargetKind
{
    Profile,
    Library,
}

/// <summary>
/// Разобранный шаблон пути: <see cref="Anchor"/> — имя переменной профиля (<c>%APPDATA%</c>, верхний регистр) или
/// буква библиотеки (<c>G</c>); <see cref="RelativePath"/> — путь под ней через <c>\</c>, без пустых частей.
/// </summary>
public sealed record SaveTarget(SaveTargetKind Kind, string Anchor, string RelativePath);

/// <summary>
/// Шаблоны путей папок сохранений из панели (spec 2026-11-05, п. 4.2): переменная профиля Windows
/// (<c>%LOCALAPPDATA%\GameName\Saved</c>) или абсолютный путь на букве библиотеки (<c>H:\Игра\Saves</c>). Одна и та же
/// проверка — в панели при сохранении записи и в помощнике перед очисткой папки. Разделитель — <c>\</c> или <c>/</c>.
/// </summary>
public static class SavePathTemplate
{
    public const int MaxLength = 260;

    /// <summary>Переменные профиля и их подпапки под корнем профиля (перенаправление папок на ПК клуба выключено).</summary>
    private static readonly Dictionary<string, string> ProfileVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["%USERPROFILE%"] = "",
        ["%APPDATA%"] = @"AppData\Roaming",
        ["%LOCALAPPDATA%"] = @"AppData\Local",
        ["%LOCALAPPDATA_LOW%"] = @"AppData\LocalLow",
        ["%DOCUMENTS%"] = "Documents",
        ["%SAVEDGAMES%"] = "Saved Games",
    };

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Символы, запрещённые Windows в имени файла или папки (кроме управляющих — они проверяются отдельно).</summary>
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*', '\\', '/'];

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// <c>null</c> — шаблон годится; иначе код ошибки для панели: <c>empty</c>, <c>tooLong</c> (длиннее 260),
    /// <c>badVariable</c> (переменная не из списка, <c>%</c> дальше в пути), <c>dotDot</c> (часть <c>..</c> или <c>.</c>),
    /// <c>drive</c> (буква не библиотеки, относительный или сетевой путь), <c>root</c> (корень профиля или диска),
    /// <c>invalidChars</c> (символы, запрещённые Windows, имена устройств, точка или пробел в конце имени).
    /// <paramref name="libraryLetters"/> — буквы библиотек сервера (G:, H:), без учёта регистра.
    /// </summary>
    public static string? Validate(string? template, IReadOnlyCollection<char> libraryLetters)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "empty";
        }

        if (template.Length > MaxLength)
        {
            return "tooLong";
        }

        var problem = Split(template, out var kind, out var anchor, out _);
        if (problem is not null)
        {
            return problem;
        }

        return kind == SaveTargetKind.Library && !libraryLetters.Any(l => char.ToUpperInvariant(l) == anchor[0]) ? "drive" : null;
    }

    /// <summary>Разбор шаблона, прошедшего <see cref="Validate"/>; иначе <see cref="ArgumentException"/>.</summary>
    public static SaveTarget Parse(string template)
    {
        var problem = string.IsNullOrEmpty(template) ? "empty" : template.Length > MaxLength ? "tooLong" : Split(template, out _, out _, out _);
        if (problem is not null)
        {
            throw new ArgumentException($"Шаблон пути сохранений не годится ({problem}): {template}", nameof(template));
        }

        Split(template, out var kind, out var anchor, out var relative);
        return new SaveTarget(kind, anchor, relative);
    }

    /// <summary>
    /// Подпапка переменной под корнем профиля через <c>\</c>: <c>%USERPROFILE%</c> → <c>""</c>, <c>%APPDATA%</c> →
    /// <c>AppData\Roaming</c>, <c>%LOCALAPPDATA%</c> → <c>AppData\Local</c>, <c>%LOCALAPPDATA_LOW%</c> →
    /// <c>AppData\LocalLow</c>, <c>%DOCUMENTS%</c> → <c>Documents</c>, <c>%SAVEDGAMES%</c> → <c>Saved Games</c>.
    /// </summary>
    public static string ProfileSubpath(string variable) =>
        ProfileVariables.TryGetValue(variable, out var subpath) ? subpath : throw new ArgumentException($"Неизвестная переменная профиля: {variable}", nameof(variable));

    /// <summary>
    /// Код ошибки для одной части пути (имени папки или файла) или <c>null</c>: <c>dotDot</c> для <c>.</c> и <c>..</c>,
    /// <c>invalidChars</c> для запрещённых Windows символов, имён устройств (<c>con</c>, <c>NUL.txt</c>) и точки или
    /// пробела в конце. Та же проверка — для имён в архиве (<see cref="SaveArchive"/>).
    /// </summary>
    internal static string? SegmentProblem(string segment)
    {
        if (segment is "." or "..")
        {
            return "dotDot";
        }

        if (segment.Length == 0 || segment.Length > 255 || segment.IndexOfAny(InvalidNameChars) >= 0 || segment.Any(char.IsControl)
            || segment[^1] is '.' or ' ')
        {
            return "invalidChars";
        }

        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return DeviceNames.Contains(stem) ? "invalidChars" : null;
    }

    private static string? Split(string template, out SaveTargetKind kind, out string anchor, out string relative)
    {
        kind = SaveTargetKind.Profile;
        anchor = "";
        relative = "";
        string rest;
        if (template[0] == '%')
        {
            var close = template.IndexOf('%', 1);
            if (close < 0 || !ProfileVariables.ContainsKey(template[..(close + 1)]))
            {
                return "badVariable";
            }

            anchor = template[..(close + 1)].ToUpperInvariant();
            rest = template[(close + 1)..];
            if (rest.Contains('%'))
            {
                return "badVariable";
            }
        }
        else if (template.Length >= 2 && char.IsAsciiLetter(template[0]) && template[1] == ':')
        {
            kind = SaveTargetKind.Library;
            anchor = char.ToUpperInvariant(template[0]).ToString();
            rest = template[2..];
        }
        else
        {
            return "drive";
        }

        if (rest.Length > 0 && Array.IndexOf(Separators, rest[0]) < 0)
        {
            return kind == SaveTargetKind.Profile ? "badVariable" : "invalidChars";
        }

        var segments = rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (SegmentProblem(segment) is { } problem)
            {
                return problem;
            }
        }

        if (segments.Length == 0)
        {
            return "root";
        }

        relative = string.Join('\\', segments);
        return null;
    }
}
