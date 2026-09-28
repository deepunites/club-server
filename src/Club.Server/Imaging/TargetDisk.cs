namespace Club.Server.Imaging;

/// <summary>Диск, как его видит Windows (<c>Get-Disk</c>): номер, серийный номер, шина, размер.</summary>
public sealed record DiskInfo(int Number, string? Serial, string? Model, long SizeBytes, string BusType);

/// <summary>Системный диск по отчёту помощника (с него загружалась Windows).</summary>
public sealed record SystemDisk(string? Serial, string? Model, long SizeBytes, string BusType);

public sealed record DiskChoice(DiskInfo? Disk, string? Failure);

/// <summary>
/// Какой диск стирать. Ошибиться нельзя: на ПК может стоять второй диск игрока/клуба или флешка. Правила:
/// только внутренние шины; если помощник сообщал системный диск — только он (по серийному номеру), иначе —
/// единственный подходящий по размеру. Любая неоднозначность — отказ, машина остаётся в PXE (безопасно).
/// </summary>
public static class TargetDisk
{
    private static readonly HashSet<string> InternalBuses = new(StringComparer.OrdinalIgnoreCase) { "NVMe", "SATA", "SAS", "RAID", "ATA", "SCSI" };

    public const long MinDiskBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// Нужный объём: EFI и MSR, распакованный образ с запасом 15 %, временный раздел под сам WIM (он скачивается
    /// на диск целиком и проверяется по sha256 до применения) и 4 ГиБ запаса.
    /// </summary>
    public static long RequiredBytes(long wimBytes, long expandedBytes) =>
        Math.Max(MinDiskBytes, 276L * 1024 * 1024 + expandedBytes * 115 / 100 + wimBytes + 4L * 1024 * 1024 * 1024);

    public static DiskChoice Choose(IReadOnlyList<DiskInfo> disks, SystemDisk? systemDisk, bool allowNewDisk, long requiredBytes)
    {
        var internalDisks = disks.Where(d => InternalBuses.Contains(d.BusType ?? "")).ToList();
        var serial = Normalize(systemDisk?.Serial);
        if (serial is not null)
        {
            var match = internalDisks.Where(d => Normalize(d.Serial) == serial).ToList();
            if (match.Count == 1)
            {
                return match[0].SizeBytes >= requiredBytes ? new(match[0], null) : new(null, "diskTooSmall");
            }

            if (match.Count > 1)
            {
                return new(null, "ambiguousDisks");
            }

            if (!allowNewDisk)
            {
                // Системного диска нет (заменили SSD?) — стирать другой диск можно только с явного разрешения.
                return new(null, "systemDiskNotFound");
            }
        }

        var fit = internalDisks.Where(d => d.SizeBytes >= requiredBytes).ToList();
        return fit.Count switch
        {
            1 => new(fit[0], null),
            0 => new(null, internalDisks.Count == 0 ? "noInternalDisk" : "diskTooSmall"),
            _ => new(null, "ambiguousDisks"),
        };
    }

    private static string? Normalize(string? serial)
    {
        var s = serial?.Trim().TrimEnd('.').Trim();
        return string.IsNullOrEmpty(s) ? null : s.ToUpperInvariant();
    }
}
