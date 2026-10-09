using System.Text;

namespace Club.Helper.Core;

/// <summary>
/// Пути исполняемых файлов процессов и том библиотеки — чистая логика (Windows-часть — <c>ProcessInspector</c> в
/// службе), поэтому тестируется на Linux. Путь образа читается в двух формах: Win32 (<c>G:\Games\cs2.exe</c>,
/// QueryFullProcessImageName с флагом 0) и NT (<c>\Device\HarddiskVolume12\Games\cs2.exe</c>, флаг
/// PROCESS_NAME_NATIVE). Процесс «с тома», если под томом лежит любая из них. Стенд 2026-10-02, помощник 1.4.1 (служба
/// LocalSystem, сеанс 0): запущенные с G: cstrike.exe и steam.exe по Win32-пути не нашлись, причина не установлена;
/// путь NT от букв дисков и пространства имён DOS-устройств не зависит.
/// </summary>
public static class VolumeImagePaths
{
    /// <summary>Префиксы пространств имён объектов, за которыми идёт путь DOS (<c>G:\…</c>, <c>Volume{…}</c>) или GLOBALROOT.</summary>
    private static readonly string[] NamespacePrefixes = [@"\\?\", @"\\.\", @"\??\", @"\GLOBAL??\", @"\DosDevices\"];

    private const string GlobalRoot = "GLOBALROOT";

    public static string DriveRoot(char letter) => $"{char.ToUpperInvariant(letter)}:\\";

    /// <summary>
    /// <paramref name="path"/> лежит под <paramref name="root"/>: без учёта регистра и только целым компонентом пути —
    /// <c>\Device\HarddiskVolume1</c> не корень для <c>\Device\HarddiskVolume12\game.exe</c>. Завершающая обратная косая
    /// черта у корня не важна.
    /// </summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var bare = root.TrimEnd('\\');
        return bare.Length > 0 && path.Length > bare.Length && path[bare.Length] == '\\' && path.StartsWith(bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Убирает префикс пространства имён: <c>\\?\G:\x</c>, <c>\??\G:\x</c> → <c>G:\x</c>;
    /// <c>\\?\GLOBALROOT\Device\…</c>, <c>\??\GLOBALROOT\Device\…</c> → <c>\Device\…</c>. Остальное — как есть.
    /// </summary>
    public static string Unprefix(string path)
    {
        foreach (var prefix in NamespacePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[prefix.Length..];
                return rest.StartsWith(GlobalRoot + "\\", StringComparison.OrdinalIgnoreCase) ? rest[GlobalRoot.Length..] : rest;
            }
        }

        return path;
    }

    /// <summary>
    /// Имя устройства NT тома по букве. <paramref name="queryDosDevice"/> — первая строка ответа QueryDosDevice для
    /// имени («G:», «Volume{…}»), <c>null</c> — запрос не удался. Обычно ответ сразу <c>\Device\HarddiskVolume12</c>;
    /// формы <c>\??\…</c>, <c>\\?\…</c> и GLOBALROOT разворачиваются, ссылка на другую букву (subst — вместе с папкой)
    /// или на <c>Volume{GUID}</c> — ещё одним запросом, не глубже четырёх. Не вышло — <c>null</c>.
    /// </summary>
    public static string? ResolveNtDevice(char letter, Func<string, string?> queryDosDevice)
    {
        var name = $"{char.ToUpperInvariant(letter)}:";
        var suffix = "";
        for (var depth = 0; depth < 4; depth++)
        {
            if (queryDosDevice(name) is not { } answer || Unprefix(answer.Trim()).TrimEnd('\\') is not { Length: > 0 } target)
            {
                return null;
            }

            if (target.StartsWith('\\'))
            {
                return target + suffix; // путь NT: \Device\HarddiskVolume12
            }

            // Путь DOS: «C:\games» (subst) или «Volume{…}» — разворачиваем его первый компонент.
            var slash = target.IndexOf('\\');
            suffix = (slash < 0 ? "" : target[slash..]) + suffix;
            name = slash < 0 ? target : target[..slash];
            if (name.Length == 0)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Запущен ли процесс с тома <paramref name="letter"/>: путь (любая из форм, без префикса пространства имён) лежит
    /// под <c>G:\</c> или под устройством NT тома <paramref name="ntDevice"/>. Возвращает путь для отчёта в форме
    /// <c>G:\…</c> или <c>null</c>.
    /// </summary>
    public static string? Match(string? win32Path, string? nativePath, char letter, string? ntDevice)
    {
        var root = DriveRoot(letter);
        foreach (var candidate in new[] { win32Path, nativePath })
        {
            if (candidate is null)
            {
                continue;
            }

            var path = Unprefix(candidate);
            if (IsUnder(path, root))
            {
                return path;
            }

            if (IsUnder(path, ntDevice))
            {
                return root + path[(ntDevice!.TrimEnd('\\').Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Как <see cref="Match"/>, и вдобавок — почему помощник 1.4.1 (только путь Win32, начинающийся с <c>G:\</c>) этот
    /// процесс не нашёл бы: путь Win32 не прочитался или лежит не под буквой (другая форма, префикс пространства имён).
    /// </summary>
    public static VolumeImageMatch? MatchImage(string? win32Path, string? nativePath, char letter, string? ntDevice)
    {
        if (Match(win32Path, nativePath, letter, ntDevice) is not { } path)
        {
            return null;
        }

        var root = DriveRoot(letter);
        var miss = win32Path is null ? "Win32 path not read" : IsUnder(win32Path, root) ? null : $"Win32 path not under {root}";
        return new VolumeImageMatch(path, miss);
    }

    /// <summary>
    /// Образы для диагностики: без непрочитанных и без системных (под папкой Windows в любой из форм). Первыми — не на
    /// системном диске или без пути Win32: среди них игра и лаунчер, и там видно, как службе представлены их пути.
    /// </summary>
    public static List<ProcessImage> OutsideWindows(IEnumerable<ProcessImage> images, string windowsDirectory, string? windowsNtDirectory)
    {
        var systemRoot = windowsDirectory.Length >= 2 && windowsDirectory[1] == ':' ? DriveRoot(windowsDirectory[0]) : null;
        return images
            .Where(i => i.Win32Path is not null || i.NativePath is not null)
            .Where(i => !IsUnder(Plain(i.Win32Path), windowsDirectory) && !IsUnder(Plain(i.NativePath), windowsNtDirectory))
            .OrderBy(i => i.Win32Path is { } w && IsUnder(Unprefix(w), systemRoot) ? 1 : 0)
            .ToList();
    }

    private static string? Plain(string? path) => path is null ? null : Unprefix(path);
}

/// <summary>
/// Процесс с тома: путь для отчёта (<c>G:\…</c>) и почему по одному пути Win32 под буквой он бы не нашёлся
/// (<c>null</c> — нашёлся бы, как в 1.4.1).
/// </summary>
public sealed record VolumeImageMatch(string Path, string? Win32Miss);

/// <summary>Образ процесса для диагностики: обе формы пути, <c>null</c> — не прочиталась.</summary>
public sealed record ProcessImage(int Pid, string? Win32Path, string? NativePath);

/// <summary>Сбои вызовов Win32: сколько и какой код ошибки чаще всего.</summary>
public sealed class ErrorTally
{
    private readonly Dictionary<int, int> _codes = [];

    public int Count { get; private set; }

    /// <summary>Самый частый код; при равенстве — меньший. Сбоев нет — <c>null</c>.</summary>
    public int? MostCommon => _codes.Count == 0 ? null : _codes.OrderByDescending(c => c.Value).ThenBy(c => c.Key).First().Key;

    public void Add(int code)
    {
        Count++;
        _codes[code] = _codes.GetValueOrDefault(code) + 1;
    }

    public override string ToString() => Count == 0 ? "0" : $"{Count} (most often error {MostCommon})";
}

/// <summary>
/// Что служба видит в процессах, когда Windows не отпускает том, а процесса с него не нашлось: устройство тома, сколько
/// процессов открылось и как выглядят пути их образов. Строка — для одной записи в журнале.
/// </summary>
public sealed record ProcessScanReport(
    char DriveLetter, string? NtDevice, string? NtDeviceError, int Processes, int Matched, ErrorTally OpenFailures,
    ErrorTally Win32PathFailures, ErrorTally NativePathFailures, string WindowsDirectory, int OutsideWindowsCount, IReadOnlyList<ProcessImage> Samples)
{
    /// <summary>Сколько образов вне папки Windows попадает в запись.</summary>
    public const int MaxSamples = 10;

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append($"drive {char.ToUpperInvariant(DriveLetter)}: = ")
            .Append(NtDevice ?? $"no NT device ({NtDeviceError ?? "unknown error"})")
            .Append($"; processes {Processes}, from the volume {Matched}")
            .Append($"; OpenProcess failed {OpenFailures}; Win32 path failed {Win32PathFailures}; NT path failed {NativePathFailures}")
            .Append($"; outside {WindowsDirectory}: {OutsideWindowsCount}");
        if (Samples.Count > 0)
        {
            text.Append(Samples.Count < OutsideWindowsCount ? $", first {Samples.Count}:" : ":");
            foreach (var image in Samples)
            {
                text.Append($" [{image.Pid}] {image.Win32Path ?? "?"} | {image.NativePath ?? "?"};");
            }

            text.Length--;
        }

        return text.ToString();
    }
}
