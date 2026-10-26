using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Мастер-том библиотеки на ПК суперклиента. Единственное место, где помощник подключает том на запись, — и только
/// по назначению сервера (администратор открыл правку этому ПК). Назначение снято — кэш сбрасывается на диск, диск
/// уходит offline, таргет отключается; пока это не удалось, сервер ждёт и таргет не удаляет.
/// Назначение в памяти, не на диске: после перезагрузки без сервера мастер-том не подключается.
/// Назначение снято, а сессия мастер-тома восстанавливает связь (таргет удалён принудительным закрытием, TrueNAS
/// перезапустил iSCSI): том всё ещё смонтирован на своей букве. Связь не вернулась за <see cref="ReconnectingDetachAfter"/>
/// — сессия отключается без сброса кэша, сбрасывать уже некуда; иначе букву (на букве библиотеки — G:) никто не отпустил
/// бы до перезагрузки ПК.
/// Экземпляр ведёт мастер-том одной библиотеки (<paramref name="markers"/>, по умолчанию G:): мастер-том H:
/// (<c>club-master-solo</c>, помощник 1.6.0) для мастер-тома G: чужой и не отключается, и наоборот.
/// </summary>
public sealed class MasterManager(IWindowsStorage storage, HelperOptions options, TimeProvider clock, ILogger logger, LibraryMarkers? markers = null)
{
    private readonly LibraryMarkers _markers = markers ?? LibraryMarkers.Games;

    /// <summary>Таргет мастер-тома на сервере называется <c>club-master</c>; по нему находится и после перезапуска помощника.</summary>
    public const string TargetMarker = ":club-master";

    /// <summary>
    /// Состояние «неизвестно»: мастер-том не назначен, а подключён ли он, прочитать не удалось (или в этом такте его не
    /// опрашивали, см. <see cref="HelperLoop"/>). Сервер прежнее состояние не меняет (docs/diskless-api.yaml, MasterReport);
    /// серверы, где unknown ещё нет в контракте, пропускают его как незнакомое — с тем же итогом.
    /// </summary>
    public const string UnknownState = "unknown";

    /// <summary>
    /// Сколько ждать, пока сессия мастер-тома без назначения восстановит связь, прежде чем отключить её без сброса кэша.
    /// Вернулась (сбой сети во время закрытия) — том отключается как обычно, со сбросом кэша. Не вернулась — сбрасывать
    /// нечего: запросы к пропавшему таргету Windows держит не дольше MaxRequestHoldTime (60 с по умолчанию).
    /// </summary>
    public static readonly TimeSpan ReconnectingDetachAfter = TimeSpan.FromMinutes(2);

    /// <summary>С какого момента (монотонная отметка) сессия мастер-тома без назначения видна без диска.</summary>
    private readonly Dictionary<string, long> _reconnectingSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Отчёт о мастер-томе: <c>null</c> — не назначен и не подключён. Буква в отчёте — та, на которой том сейчас (или
    /// назначенная): по ней <see cref="HelperLoop"/> не ставит том библиотеки на букву, которую держит мастер-том.
    /// </summary>
    public async Task<MasterReport?> ApplyAsync(MasterAssignment? desired, bool known, CancellationToken ct)
    {
        List<string>? connected = null;
        (string Iqn, char? Letter)? detaching = null;
        try
        {
            if (desired is null && known)
            {
                // Назначение снято — отключаем все сессии мастер-тома, и восстанавливающие связь (IsConnected = false):
                // такая всё ещё держит смонтированный том на его букве.
                connected = [.. (await storage.SessionTargetsAsync(ct)).Where(IsMaster).Distinct(StringComparer.OrdinalIgnoreCase)];
                foreach (var gone in _reconnectingSince.Keys.Where(t => !connected.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    _reconnectingSince.Remove(gone);
                }

                MasterReport? waiting = null;
                foreach (var iqn in connected)
                {
                    var disk = await storage.FindDiskAsync(iqn, ct);
                    detaching = (iqn, disk?.DriveLetter);
                    if (disk is not null)
                    {
                        _reconnectingSince.Remove(iqn);
                    }
                    else if (Reconnecting(iqn) is { } left)
                    {
                        // Диска служба не видит — сессия восстанавливает связь (или диск ещё не появился). Сбросить кэш нельзя:
                        // ждём, вдруг связь вернётся. Букву том держит — failed (не none): HelperLoop том библиотеки на неё не ставит.
                        waiting ??= new MasterReport("failed", iqn, Error: $"iSCSI session is reconnecting; disconnecting it in {left.TotalSeconds:0} s unless it recovers");
                        continue;
                    }

                    await DetachAsync(iqn, disk, ct);
                    _reconnectingSince.Remove(iqn);
                }

                return waiting ?? (connected.Count == 0 ? null : new MasterReport("none"));
            }

            connected = (await storage.ConnectedTargetsAsync(ct)).Where(IsMaster).ToList();
            if (desired is null)
            {
                // Сервер ещё не ответил — ничего не трогаем (и не отключаем то, с чем, возможно, идёт работа). Буква тома —
                // чтобы HelperLoop не поставил на неё том из assignment.json (служба перезапустилась во время правки).
                return connected.Count == 0 ? null : new MasterReport("mounted", connected[0], await LetterAsync(connected[0], ct));
            }

            return await AttachAsync(desired, connected, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            logger.LogWarning(ex, "Master volume: {Reason}", ex.Message);

            // Не назначен и сессии не прочитались — подключён ли том, неизвестно: failed без таргета был бы ложной ошибкой
            // у любого ПК (и затёр бы mounted на сервере), отчёт без master — ложным «не подключён». Не назначен и не
            // отключился — failed с таргетом и буквой, на которой он остался.
            return desired is null && connected is null ? new MasterReport(UnknownState, Error: ex.Message)
                : desired is null && detaching is { } d ? new MasterReport("failed", d.Iqn, d.Letter?.ToString(), ex.Message)
                : new MasterReport("failed", desired?.TargetIqn, desired?.DriveLetter, ex.Message);
        }
    }

    private bool IsMaster(string iqn) => _markers.IsMaster(iqn);

    /// <summary>
    /// Сессия без диска: сколько ещё ждать восстановления связи, прежде чем отключить; <c>null</c> — ждать хватит.
    /// </summary>
    private TimeSpan? Reconnecting(string iqn)
    {
        if (!_reconnectingSince.TryGetValue(iqn, out var since))
        {
            _reconnectingSince[iqn] = since = clock.GetTimestamp();
            logger.LogWarning("Master volume {Iqn}: iSCSI session has no disk (reconnecting); disconnecting it in {Wait} unless it recovers", iqn, ReconnectingDetachAfter);
        }

        var left = ReconnectingDetachAfter - clock.GetElapsedTime(since);
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>Буква тома подключённого мастер-тома для отчёта; не прочиталась — без буквы.</summary>
    private async Task<string?> LetterAsync(string iqn, CancellationToken ct)
    {
        try
        {
            return (await storage.FindDiskAsync(iqn, ct))?.DriveLetter?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Master volume {Iqn}: drive letter not read: {Reason}", iqn, ex.Message);
            return null;
        }
    }

    private async Task<MasterReport> AttachAsync(MasterAssignment desired, List<string> connected, CancellationToken ct)
    {
        foreach (var stale in connected.Where(t => t != desired.TargetIqn))
        {
            await DetachAsync(stale, await storage.FindDiskAsync(stale, ct), ct);
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

    /// <summary>Кэш на диск, диск offline, отключение. Диска нет (сессия восстанавливала связь и не восстановила) — только отключение.</summary>
    private async Task DetachAsync(string iqn, DiskInfo? disk, CancellationToken ct)
    {
        if (disk is not null)
        {
            await storage.FlushAndOfflineAsync(disk.Number, disk.DriveLetter, ct);
        }

        await storage.DisconnectAsync(iqn, ct);
        if (disk is not null)
        {
            logger.LogInformation("Master volume {Iqn} flushed and disconnected", iqn);
        }
        else
        {
            logger.LogWarning("Master volume {Iqn} disconnected without a flush: its iSCSI session had no disk", iqn);
        }
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
