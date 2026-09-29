using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Мастер-том библиотеки на ПК суперклиента. Единственное место, где помощник подключает том на запись, — и только
/// по назначению сервера (администратор открыл правку этому ПК). Назначение снято — кэш сбрасывается на диск, диск
/// уходит offline, таргет отключается; пока это не удалось, сервер ждёт и таргет не удаляет.
/// Назначение в памяти, не на диске: после перезагрузки без сервера мастер-том не подключается.
/// </summary>
public sealed class MasterManager(IWindowsStorage storage, HelperOptions options, TimeProvider clock, ILogger<MasterManager> logger)
{
    /// <summary>Таргет мастер-тома на сервере называется <c>club-master</c>; по нему находится и после перезапуска помощника.</summary>
    public const string TargetMarker = ":club-master";

    public async Task<MasterReport?> ApplyAsync(MasterAssignment? desired, bool known, CancellationToken ct)
    {
        try
        {
            var connected = (await storage.ConnectedTargetsAsync(ct)).Where(t => t.EndsWith(TargetMarker, StringComparison.Ordinal)).ToList();
            if (desired is null)
            {
                if (!known)
                {
                    // Сервер ещё не ответил — ничего не трогаем (и не отключаем то, с чем, возможно, идёт работа).
                    return connected.Count == 0 ? null : new MasterReport("mounted", connected[0]);
                }

                foreach (var iqn in connected)
                {
                    await DetachAsync(iqn, ct);
                }

                return connected.Count == 0 ? null : new MasterReport("none");
            }

            return await AttachAsync(desired, connected, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            logger.LogWarning(ex, "Master volume: {Reason}", ex.Message);
            return new MasterReport("failed", desired?.TargetIqn, desired?.DriveLetter, ex.Message);
        }
    }

    private async Task<MasterReport> AttachAsync(MasterAssignment desired, List<string> connected, CancellationToken ct)
    {
        foreach (var stale in connected.Where(t => t != desired.TargetIqn))
        {
            await DetachAsync(stale, ct);
        }

        if (!connected.Contains(desired.TargetIqn))
        {
            var (host, port) = VolumeManager.ParsePortal(desired.Portal);
            await storage.ConnectChapAsync(desired.TargetIqn, host, port, desired.ChapUser, desired.ChapSecret, ct);
            logger.LogInformation("Master volume {Iqn} connected for editing", desired.TargetIqn);
        }

        var disk = await WaitForDiskAsync(desired.TargetIqn, ct);
        if (disk is null)
        {
            return new MasterReport("mounting", desired.TargetIqn, desired.DriveLetter, "disk has not appeared yet");
        }

        if (disk.IsReadOnly)
        {
            await storage.SetDiskWritableAsync(disk.Number, ct);
        }

        if (disk.IsOffline)
        {
            await storage.SetDiskOnlineAsync(disk.Number, ct);
        }

        var letter = char.ToUpperInvariant(desired.DriveLetter[0]);
        if (disk.DriveLetter != letter)
        {
            await storage.AssignDriveLetterAsync(disk.Number, letter, ct);
        }

        return new MasterReport("mounted", desired.TargetIqn, letter.ToString());
    }

    private async Task DetachAsync(string iqn, CancellationToken ct)
    {
        if (await storage.FindDiskAsync(iqn, ct) is { } disk)
        {
            await storage.FlushAndOfflineAsync(disk.Number, disk.DriveLetter, ct);
        }

        await storage.DisconnectAsync(iqn, ct);
        logger.LogInformation("Master volume {Iqn} flushed and disconnected", iqn);
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

            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
    }
}
