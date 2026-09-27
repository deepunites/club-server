using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Приводит том библиотеки на ПК к назначенному сервером. Правила:
/// <list type="bullet">
/// <item>монтирование fail-closed: SAN policy OfflineShared → вход в таргет → атрибут read-only с проверкой →
/// online → буква. Если read-only не подтвердился — таргет отключается, том на запись не монтируется никогда;</item>
/// <item>смена версии не выдёргивает диск из-под игры: пока с тома запущен процесс — <c>switchPending</c>;</item>
/// <item>нет назначения (сервер ничего не опубликовал) — том отключается, когда освободится.</item>
/// </list>
/// Недоступность сервера сюда не доходит: цикл просто не вызывает <see cref="ApplyAsync"/> с новым назначением.
/// </summary>
public sealed class VolumeManager(IWindowsStorage storage, IProcessInspector processes, HelperOptions options, TimeProvider clock, ILogger<VolumeManager> logger)
{
    /// <summary>Таргеты версий библиотеки называются <c>games-&lt;версия&gt;</c>; чужие iSCSI-подключения ПК не трогаем.</summary>
    public const string ManagedTargetMarker = ":games-";

    public async Task<MountedVolume> ApplyAsync(VolumeAssignment? desired, CancellationToken ct)
    {
        var managed = (await storage.ConnectedTargetsAsync(ct))
            .Where(t => t.Contains(ManagedTargetMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (desired is null)
        {
            var busy = await ReleaseAsync(managed, ct);
            return busy is null ? new MountedVolume("none") : new MountedVolume("switchPending", busy.Iqn, Error: $"volume in use by {busy.Process}");
        }

        if (!desired.ReadOnly)
        {
            // Общий том всем ПК — только чтение; иное назначение считаем ошибкой сервера и не выполняем.
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, Error: "assignment is not read-only");
        }

        var letter = char.ToUpperInvariant(desired.DriveLetter[0]);
        var old = managed.Where(t => !t.Equals(desired.TargetIqn, StringComparison.OrdinalIgnoreCase)).ToList();

        if (managed.Any(t => t.Equals(desired.TargetIqn, StringComparison.OrdinalIgnoreCase)))
        {
            // Назначенный уже подключён: старые версии отпускаем, когда освободятся; проверяем здоровье текущего.
            await ReleaseAsync(old, ct);
            return await VerifyAsync(desired, letter, ct);
        }

        var inUse = await ReleaseAsync(old, ct);
        if (inUse is not null)
        {
            logger.LogInformation("Library {Version} is waiting: {Iqn} is used by {Process}", desired.LibraryVersion, inUse.Iqn, inUse.Process);
            return new MountedVolume("switchPending", inUse.Iqn, DriveLetter: letter.ToString(), Error: $"new version {desired.LibraryVersion} waits: volume in use by {inUse.Process}");
        }

        return await MountAsync(desired, letter, ct);
    }

    private async Task<MountedVolume> MountAsync(VolumeAssignment desired, char letter, CancellationToken ct)
    {
        var (host, port) = ParsePortal(desired.Portal);
        try
        {
            await storage.EnsureSanPolicyOfflineSharedAsync(ct);
            await storage.ConnectAsync(desired.TargetIqn, host, port, ct);
            var disk = await WaitForDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk of the target did not appear");
            return await BringUpAsync(desired, disk, letter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Mounting {Iqn} failed; disconnecting", desired.TargetIqn);
            await DisconnectQuietlyAsync(desired.TargetIqn, ct);
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: ex.Message);
        }
    }

    private async Task<MountedVolume> VerifyAsync(VolumeAssignment desired, char letter, CancellationToken ct)
    {
        try
        {
            var disk = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("session has no disk");
            if (disk.IsReadOnly && !disk.IsOffline && disk.DriveLetter == letter)
            {
                return Mounted(desired, letter);
            }

            // Что-то сбилось (буква, offline, кто-то снял read-only) — доводим тем же fail-closed порядком.
            logger.LogWarning("Volume {Iqn} drifted (ro={Ro}, offline={Offline}, letter={Letter}); repairing", desired.TargetIqn, disk.IsReadOnly, disk.IsOffline, disk.DriveLetter);
            return await BringUpAsync(desired, disk, letter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Volume {Iqn} is unhealthy; disconnecting", desired.TargetIqn);
            await DisconnectQuietlyAsync(desired.TargetIqn, ct);
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: ex.Message);
        }
    }

    /// <summary>read-only с проверкой → online → буква. Порядок важен: online раньше read-only = NTFS на запись.</summary>
    private async Task<MountedVolume> BringUpAsync(VolumeAssignment desired, DiskInfo disk, char letter, CancellationToken ct)
    {
        if (!disk.IsReadOnly)
        {
            await storage.SetDiskReadOnlyAsync(disk.Number, ct);
            disk = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk disappeared");
            if (!disk.IsReadOnly)
            {
                throw new InvalidOperationException("disk is not read-only after SetDiskReadOnly; refusing to bring it online");
            }
        }

        if (disk.IsOffline)
        {
            await storage.SetDiskOnlineAsync(disk.Number, ct);
        }

        if (disk.DriveLetter != letter)
        {
            await storage.AssignDriveLetterAsync(disk.Number, letter, ct);
        }

        var final = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk disappeared");
        if (!final.IsReadOnly || final.IsOffline || final.DriveLetter != letter)
        {
            throw new InvalidOperationException($"volume not in expected state (ro={final.IsReadOnly}, offline={final.IsOffline}, letter={final.DriveLetter})");
        }

        logger.LogInformation("Library {Version} mounted read-only at {Letter}:", desired.LibraryVersion, letter);
        return Mounted(desired, letter);
    }

    private sealed record Busy(string Iqn, string Process);

    /// <summary>Отключает перечисленные таргеты, если с их тома ничего не запущено; иначе возвращает первый занятый.</summary>
    private async Task<Busy?> ReleaseAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        Busy? busy = null;
        foreach (var iqn in targets)
        {
            var disk = await storage.FindDiskAsync(iqn, ct);
            if (disk?.DriveLetter is { } letter)
            {
                var running = await processes.ProcessesRunningFromAsync(letter, ct);
                if (running.Count > 0)
                {
                    busy ??= new Busy(iqn, running[0]);
                    continue;
                }
            }

            logger.LogInformation("Disconnecting library target {Iqn}", iqn);
            await storage.DisconnectAsync(iqn, ct);
        }

        return busy;
    }

    private async Task<DiskInfo?> WaitForDiskAsync(string iqn, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddSeconds(options.DiskWaitSec);
        while (true)
        {
            if (await storage.FindDiskAsync(iqn, ct) is { } disk)
            {
                return disk;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, ct);
        }
    }

    private async Task DisconnectQuietlyAsync(string iqn, CancellationToken ct)
    {
        try
        {
            await storage.DisconnectAsync(iqn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Disconnect of {Iqn} failed", iqn);
        }
    }

    private static MountedVolume Mounted(VolumeAssignment desired, char letter) =>
        new("mounted", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: true);

    public static (string Host, int Port) ParsePortal(string portal)
    {
        var colon = portal.LastIndexOf(':');
        return colon > 0 && int.TryParse(portal[(colon + 1)..], out var port) ? (portal[..colon], port) : (portal, 3260);
    }
}
