using System.Text.Json;

namespace Club.Helper.Core;

/// <summary>
/// Последнее назначение тома — на диске ПК, чтобы при недоступном сервере игры всё равно были. У каждой библиотеки свой
/// файл (<see cref="FileFor"/>): G: — <c>assignment.json</c>, как до 1.6.0; H: — <c>assignment.json.solo</c>. Стирание
/// назначения одной библиотеки файл другой не трогает. Папка — <c>%ProgramData%\ClubDiskless</c> (StateDirectory службы).
/// </summary>
public sealed class FileAssignmentCache(string directory, string fileName = FileAssignmentCache.GamesFile) : IAssignmentCache
{
    /// <summary>Файл назначения G:. Его и файлы библиотек перед захватом эталона стирают скрипты ($StateFiles в seal-golden.ps1).</summary>
    public const string GamesFile = "assignment.json";

    private readonly string _file = Path.Combine(directory, fileName);

    /// <summary>Имя файла назначения библиотеки: <c>null</c> — G: (<see cref="GamesFile"/>), иначе <c>assignment.json.&lt;ключ&gt;</c>.</summary>
    public static string FileFor(string? library) => library is null ? GamesFile : $"{GamesFile}.{library}";

    /// <summary>Назначение и HWID ПК, которому оно выдано (файл прежнего формата, без HWID, не используется).</summary>
    private sealed record Cached(string? Hwid, VolumeAssignment? Assignment);

    public async Task<VolumeAssignment?> LoadAsync(string hwid, CancellationToken ct)
    {
        try
        {
            return File.Exists(_file) && JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(_file, ct)) is { } cached && cached.Hwid == hwid
                ? cached.Assignment
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string hwid, VolumeAssignment? assignment, CancellationToken ct)
    {
        if (assignment is null)
        {
            File.Delete(_file);
            return;
        }

        var temp = _file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Cached(hwid, assignment)), ct);
        File.Move(temp, _file, overwrite: true);
    }
}
