using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Последнее назначение тома, сохранённое на ПК: сервер недоступен при загрузке — игры всё равно есть. Привязано к
/// HWID: назначение, приехавшее с диском другого ПК (эталон бездиска), не используется.
/// </summary>
public interface IAssignmentCache
{
    Task<VolumeAssignment?> LoadAsync(string hwid, CancellationToken ct);

    Task SaveAsync(string hwid, VolumeAssignment? assignment, CancellationToken ct);
}

public sealed class InMemoryAssignmentCache : IAssignmentCache
{
    public VolumeAssignment? Current { get; set; }

    public string? Hwid { get; set; }

    public bool HasValue { get; private set; }

    public Task<VolumeAssignment?> LoadAsync(string hwid, CancellationToken ct) => Task.FromResult(HasValue && Hwid == hwid ? Current : null);

    public Task SaveAsync(string hwid, VolumeAssignment? assignment, CancellationToken ct)
    {
        Current = assignment;
        Hwid = hwid;
        HasValue = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Один такт помощника: привести том к назначению → сообщить факты серверу → в ответе узнать актуальное назначение.
/// Сервер недоступен — действует последнее известное назначение (из памяти или с диска), том не отключается.
/// Сбой применения тома или мастер-тома отчёт не отменяет: уходит <c>failed</c> с причиной. Иначе ПК в панели выглядит
/// выключенным, а сервер не узнаёт ни причину, ни новое назначение (стенд 2026-10-02, помощник 1.4.1: отказ Windows
/// отключить занятый том старой версии ронял такт целиком, и отчётов не было, пока том не освободился). Windows не
/// ответила на том (таймаут PowerShell, 120 с) — мастер-том в этом такте не опрашивается (unknown), чтобы отчёт не ждал
/// ещё столько же.
/// </summary>
public sealed class HelperLoop(
    DisklessApiClient api, VolumeManager volumes, IAssignmentCache cache, IMachineIdentity identity, HelperOptions options, ILogger<HelperLoop> logger,
    MasterManager? masters = null)
{
    private VolumeAssignment? _desired;
    private bool _known;
    private MasterAssignment? _master;
    private bool _masterKnown;
    private string? _contentsReportedFor;
    private string? _volumeFailure;
    private string? _masterFailure;

    /// <summary>MasterManager после запуска службы хоть раз ответил: известно, подключён ли мастер-том на ПК.</summary>
    private bool _masterSeen;

    /// <summary>
    /// Последний ответ MasterManager, кроме unknown (<c>null</c> — мастер-том не подключён): при сбое отчёт опирается на
    /// него, а не на <see cref="LastMasterReport"/>, где может быть unknown или failed, составленный здесь же.
    /// </summary>
    private MasterReport? _lastKnownMaster;

    /// <summary>
    /// В последнем применении тома Windows не ответила (таймаут PowerShell, 120 с): мастер-том в этом такте не опрашивается.
    /// </summary>
    private bool _windowsTimedOut;

    /// <summary>
    /// Личный диск игр (таргет), который сервер выдал на запрос при старте помощника — значит, сбросил его для этой
    /// загрузки (или подтвердил, что он уже подключён здесь). Только такой личный диск монтируется.
    /// </summary>
    private string? _personalConfirmed;

    /// <summary>С какого момента (Environment.TickCount64) сервер недоступен при запросе назначения; null — доступен.</summary>
    private long? _unreachableSince;

    /// <summary>
    /// Сколько ждать недоступный сервер, прежде чем смонтировать сохранённый личный диск без сброса (с данными прошлого
    /// игрока): перезапуск службы сервера или поздний DHCP при старте Windows — секунды, а без игр клуб не работает.
    /// </summary>
    public static readonly TimeSpan CachedPersonalAfter = TimeSpan.FromMinutes(3);

    public MountedVolume? LastReport { get; private set; }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!_known)
        {
            await LearnAssignmentAsync(ct);
        }

        var report = await ApplyAsync(ct);
        var master = await ApplyMasterAsync(ct);
        var reply = await ReportAsync(report, master, ct);
        if (reply is null)
        {
            return;
        }

        var changed = false;
        if (reply.Volume is null && _desired is not null && VolumeManager.IsPersonal(_desired))
        {
            // Личный диск не отпускаем из-за пустого ответа (диск готовится, сбой TrueNAS): на нём сессия игрока. Он
            // сменится при следующем старте помощника.
            logger.LogDebug("Server sent no assignment; keeping the personal games disk until the next helper start");
        }
        else if (reply.Volume is { } offered && VolumeManager.IsPersonal(offered) && offered.TargetIqn != _personalConfirmed)
        {
            // Личный диск в ответе на отчёт: сервер не сбрасывал его для этой загрузки. Берём только из запроса при старте.
            logger.LogInformation("Personal games disk offered in a status reply; asking for it at boot to get it reset first");
            await LearnAssignmentAsync(ct);
            report = await ApplyAsync(ct);
            changed = true;
        }
        else if (!Equals(reply.Volume, _desired))
        {
            // Новая версия или откат: применяем сразу, не дожидаясь следующего такта.
            logger.LogInformation("Assignment changed: {Old} -> {New}", _desired?.LibraryVersion, reply.Volume?.LibraryVersion);
            _desired = reply.Volume;
            _known = true;
            await cache.SaveAsync((await identity.ReadAsync(ct)).Hwid, _desired, ct);
            report = await ApplyAsync(ct);
            changed = true;
        }

        var masterKnownBefore = _masterKnown;
        _masterKnown = true;
        if (!Equals(reply.Master, _master) || !masterKnownBefore)
        {
            // Администратор открыл или закрыл правку мастер-тома.
            _master = reply.Master;
            master = await ApplyMasterAsync(ct);
            changed = true;
        }

        if (changed)
        {
            await ReportAsync(report, master, ct);
        }
    }

    public MasterReport? LastMasterReport { get; private set; }

    private async Task<MasterReport?> ApplyMasterAsync(CancellationToken ct)
    {
        if (masters is null)
        {
            return LastMasterReport = null;
        }

        if (_windowsTimedOut)
        {
            // Том в этом такте упёрся в таймаут PowerShell: опрос сессий ради мастер-тома почти наверняка ждал бы ещё до
            // 120 с, и отчёт вместе с ним. Мастер-том — в следующем такте, сейчас unknown: сервер прежнее состояние не
            // меняет (DisklessEndpoints.StatusAsync → RecordMasterAsync с keepMaster).
            logger.LogDebug("Master volume not checked this tick: Windows did not answer while applying the library volume");
            return LastMasterReport = new MasterReport(MasterManager.UnknownState, Error: "not checked: timeout");
        }

        try
        {
            LastMasterReport = await masters.ApplyAsync(_master, _masterKnown, ct);
            if (LastMasterReport?.State != MasterManager.UnknownState)
            {
                _lastKnownMaster = LastMasterReport;
                _masterSeen = true;
            }

            _masterFailure = null;
            return LastMasterReport;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailure(ref _masterFailure, ex, "Applying the master volume failed; reporting the failure");

            // Как MasterManager при сбое: failed с назначенным таргетом. Не назначен, но по последнему известному ответу
            // подключён, — тоже failed: отчёт без master сервер понял бы как «не подключён». Последний известный, а не
            // последний отчёт: после unknown (сессии не прочитались) том мог остаться подключённым. Не назначен и с
            // запуска службы ни разу не прочитан (первый такт после перезапуска) — неизвестно, подключён ли он на ПК:
            // отчёт без master сервер прочёл бы как «не подключён» и сбросил бы master_state (например, mounted у ПК, где
            // том ещё открыт), failed без таргета — ложная ошибка у любого ПК. Поэтому unknown: его сервер пропускает и
            // прежнее не меняет (DisklessEndpoints.StatusAsync → RecordMasterAsync с keepMaster). Так же, если прошлый
            // отчёт уже был unknown: состояние так и не прочитано. Не назначен, а последний опрос показал «не подключён»
            // (или «none» — отключили сами), — сообщать не о чем.
            return LastMasterReport = _master is { } m ? new MasterReport("failed", m.TargetIqn, m.DriveLetter, ex.Message)
                : _lastKnownMaster is { State: not "none" } last ? new MasterReport("failed", last.TargetIqn, last.DriveLetter, ex.Message)
                : !_masterSeen || LastMasterReport?.State == MasterManager.UnknownState ? new MasterReport(MasterManager.UnknownState, Error: ex.Message)
                : null;
        }
    }

    private async Task LearnAssignmentAsync(CancellationToken ct)
    {
        try
        {
            _desired = await api.GetVolumeAsync(await volumes.PersonalAttachedAsync(ct), ct);
            _known = true;
            _unreachableSince = null;
            _personalConfirmed = _desired is not null && VolumeManager.IsPersonal(_desired) ? _desired.TargetIqn : null;
            await cache.SaveAsync((await identity.ReadAsync(ct)).Hwid, _desired, ct);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            // Сервер клуба недоступен (или машина не одобрена): монтируем то, что было назначено в прошлый раз.
            _desired = await cache.LoadAsync((await identity.ReadAsync(ct)).Hwid, ct);
            if (ex is ServerUnavailableException { Unreachable: true })
            {
                _unreachableSince ??= Environment.TickCount64;
            }
            else
            {
                _unreachableSince = null;
            }

            if (_desired is not null && VolumeManager.IsPersonal(_desired))
            {
                // Личный диск без сброса сервером — с данными прошлого игрока. Пока сервер отвечает ошибкой или недоступен
                // недолго (перезапуск службы, DHCP ещё не выдал адрес), спрашиваем снова каждый такт; дольше
                // CachedPersonalAfter — монтируем как есть: без игр клуб не работает.
                if (_unreachableSince is not { } since || Environment.TickCount64 - since < (long)CachedPersonalAfter.TotalMilliseconds)
                {
                    logger.LogWarning("Server did not give the assignment ({Reason}); asking again instead of mounting the cached personal disk", ex.Message);
                    _desired = null;
                    _known = false;
                    return;
                }

                _personalConfirmed = _desired.TargetIqn;
            }

            _known = _desired is not null;
            logger.LogWarning("Server unavailable ({Reason}); using cached assignment {Version}", ex.Message, _desired?.LibraryVersion);
        }
    }

    private async Task<MountedVolume> ApplyAsync(CancellationToken ct)
    {
        _windowsTimedOut = false;
        if (!_known)
        {
            // Ни сервера, ни сохранённого назначения — ничего не трогаем (и ничего не отключаем).
            return LastReport = new MountedVolume("none", Error: "no assignment known yet");
        }

        try
        {
            LastReport = await volumes.ApplyAsync(_desired, ct);
            _windowsTimedOut = volumes.WindowsTimedOut;
            _volumeFailure = null;
            return LastReport;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Отмена при живом токене службы — тоже сбой применения, а не остановка (см. CachedMachineIdentity), и,
            // как таймаут PowerShell, значит «Windows не ответила».
            _windowsTimedOut = ex is TimeoutException or OperationCanceledException;
            LogFailure(ref _volumeFailure, ex, "Applying the library volume failed; reporting the failure");
            return LastReport = await volumes.FailedAsync(_desired, ex, ct);
        }
    }

    /// <summary>Сбой — в журнал при появлении и при смене текста, а не каждый такт (15–30 с).</summary>
    private void LogFailure(ref string? last, Exception ex, string message)
    {
        if (ex.Message == last)
        {
            logger.LogDebug("{Message}: {Reason}", message, ex.Message);
            return;
        }

        last = ex.Message;
        logger.LogError(ex, "{Message}", message);
    }

    private async Task<StatusAccepted?> ReportAsync(MountedVolume report, MasterReport? master, CancellationToken ct)
    {
        try
        {
            // Состав версии (папки на томе) — один раз после подключения версии, а не в каждом отчёте.
            if (report.State == "mounted" && report.LibraryVersion != _contentsReportedFor)
            {
                report = report with { Contents = await volumes.ContentsAsync(report, ct) };
            }

            var facts = await identity.ReadAsync(ct);
            var reply = await api.ReportStatusAsync(
                new MachineStatus(options.HelperVersion, facts.BootTime, report, facts.DhcpServers, facts.ImageVersion, facts.SystemDisk, facts.SecureBoot,
                    facts.InitiatorIqn, master),
                ct);
            if (report.Contents is not null)
            {
                _contentsReportedFor = report.LibraryVersion;
            }

            return reply;
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            logger.LogWarning("Status report failed: {Reason}", ex.Message);
            return null;
        }
    }
}
