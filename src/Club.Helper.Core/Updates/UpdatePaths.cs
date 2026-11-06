namespace Club.Helper.Core.Updates;

/// <summary>
/// Файлы самообновления помощника (spec 2026-11-06, п. 4.2–4.4). Сборки — рядом с exe службы в
/// <paramref name="InstallDir"/> (<c>%ProgramFiles%\ClubDiskless</c>, тот же диск и права папки): <see cref="New"/> —
/// скачанная новая, <see cref="Old"/> — прежняя до подтверждения, <see cref="Failed"/> — новая после отката. Состояние —
/// в <paramref name="StateDir"/> (<c>%ProgramData%\ClubDiskless</c>, StateDirectory службы): <see cref="StateFile"/>
/// (<c>update.json</c>) и <see cref="FailedFile"/> (<c>update-failed.json</c>). Эти же имена стирают и проверяют скрипты
/// (<c>install-helper.ps1</c>, <c>update-helper-net.ps1</c>, <c>seal-golden.ps1</c>, <c>seal-hybrid.ps1</c>).
/// </summary>
public sealed record UpdatePaths(string InstallDir, string StateDir)
{
    /// <summary>Exe службы <c>ClubDisklessHelper</c>; ImagePath службы не меняется ни заменой, ни откатом.</summary>
    public const string ExeName = "ClubDisklessHelper.exe";

    public string Exe => Path.Combine(InstallDir, ExeName);

    /// <summary>Скачанная новая сборка (до замены).</summary>
    public string New => Exe + ".new";

    /// <summary>Прежняя сборка: хранится, пока новая не подтверждена, — на неё откатывается страж.</summary>
    public string Old => Exe + ".old";

    /// <summary>Новая сборка, с которой откатились.</summary>
    public string Failed => Exe + ".failed";

    /// <summary>Состояние замены (<see cref="UpdateState"/>).</summary>
    public string StateFile => Path.Combine(StateDir, "update.json");

    /// <summary>Поручения, по которым на этом ПК была ошибка (<see cref="FailedUpdate"/>, последние 20).</summary>
    public string FailedFile => Path.Combine(StateDir, "update-failed.json");

    /// <summary>
    /// SHA256 файла сборки строчными (<c>helperBuild</c>; для стража — свой exe и <c>.old</c>). Файл открывается на чтение
    /// без запрета чужого чтения, записи и удаления: работающий exe так читается.
    /// </summary>
    public static string Sha256Of(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Папки службы на ПК: <c>%ProgramFiles%\ClubDiskless</c> и <c>%ProgramData%\ClubDiskless</c>.</summary>
    public static UpdatePaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClubDiskless"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubDiskless"));
}
