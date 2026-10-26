using System.Net;
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
/// Мастер-том на букве тома библиотеки (сервер с Library:MasterDriveLetter = DriveLetter, 1.5.1): сначала отпускается том
/// библиотеки, потом подключается мастер-том; пока назначение мастер-тома есть, том библиотеки не монтируется
/// (<see cref="MasterLetter"/>). Правку закрыли — наоборот: сначала отключается мастер-том (не отключился, на нём открыты
/// файлы, — том библиотеки ждёт), потом возвращается том библиотеки. Без назначения (служба перезапустилась, сервер ещё
/// не ответил или недоступен) опора — то, что на ПК: мастер-том на букве тома библиотеки её не отдаёт (<see cref="HeldLetter"/>).
/// Цикл ведёт одну библиотеку: G: (<paramref name="library"/> = <c>null</c>, прежние пути API) или библиотеку с ключом (H: —
/// <c>solo</c>, помощник 1.6.0: пути <c>machines/{id}/libraries/{key}/…</c>; <paramref name="volumes"/>,
/// <paramref name="masters"/> и <paramref name="cache"/> — её собственные). Такого пути на сервере нет (404: библиотека
/// выключена или неизвестна, сервер старше 1.6.0) — цикл библиотеки засыпает на <see cref="DormantFor"/>: Windows не
/// трогается вовсе, подключённое остаётся до перезагрузки, assignment.json библиотеки стирается.
/// </summary>
public sealed class HelperLoop(
    DisklessApiClient api, VolumeManager volumes, IAssignmentCache cache, IMachineIdentity identity, HelperOptions options, ILogger logger,
    MasterManager? masters = null, TimeProvider? clock = null, string? library = null)
{
    /// <summary>Сколько спит цикл библиотеки, которой нет на сервере (404), прежде чем спросить снова.</summary>
    public static readonly TimeSpan DormantFor = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>С какого момента (монотонная отметка) цикл библиотеки спит: сервер ответил 404 на её путь; null — не спит.</summary>
    private long? _dormantSince;

    /// <summary>О сне уже написано в журнал: повторный 404 после пробуждения — без новой записи.</summary>
    private bool _dormancyLogged;

    private VolumeAssignment? _desired;
    private bool _known;
    private MasterAssignment? _master;
    private bool _masterKnown;
    private string? _contentsReportedFor;
    private string? _volumeFailure;
    private string? _masterFailure;

    /// <summary>Сервер в последнем ответе сказал, что буква тома библиотеки отдана мастер-тому (StatusAccepted.LibraryLetterReserved).</summary>
    private bool _libraryLetterReserved;

    /// <summary>
    /// Буква, на которой мастер-том сейчас на ПК, по последнему ответу MasterManager: подключён, подключается или не
    /// отключился (failed с буквой). none и «не подключён» её забывают; unknown и failed без буквы не меняют — подключён ли
    /// он теперь, по ним не понять. Нужна там, где назначения нет: правку закрыли, а на мастер-томе открыты файлы (ждут и
    /// помощник, и сервер); служба перезапустилась во время правки, а сервер не ответил.
    /// </summary>
    private char? _masterOnLetter;

    /// <summary>MasterManager после запуска службы хоть раз ответил: известно, подключён ли мастер-том на ПК.</summary>
    private bool _masterSeen;

    /// <summary>Мастер-том после запуска службы хоть раз опрошен (с любым итогом).</summary>
    private bool _masterChecked;

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

    /// <summary>С какого момента (монотонная отметка TimeProvider) сервер недоступен при запросе назначения; null — доступен.</summary>
    private long? _unreachableSince;

    /// <summary>
    /// Сколько ждать недоступный сервер, прежде чем смонтировать сохранённый личный диск без сброса (с данными прошлого
    /// игрока): перезапуск службы сервера или поздний DHCP при старте Windows — секунды, а без игр клуб не работает.
    /// </summary>
    public static readonly TimeSpan CachedPersonalAfter = TimeSpan.FromMinutes(3);

    public MountedVolume? LastReport { get; private set; }

    /// <summary>Ключ библиотеки этого цикла; <c>null</c> — G:.</summary>
    public string? Library => library;

    /// <summary>Цикл библиотеки спит: её нет на сервере (404).</summary>
    public bool Dormant => _dormantSince is not null;

    /// <summary>
    /// В последнем такте Windows не ответила на том (таймаут PowerShell, 120 с): следующая библиотека в этом такте не
    /// применяется (<see cref="HelperLoops"/>), чтобы отчёт G: следующего такта не ждал ещё столько же.
    /// </summary>
    public bool WindowsTimedOut => _windowsTimedOut;

    /// <summary>
    /// Буква, которую мастер-том занимает вместо тома библиотеки: мастер-том назначен, и его буква — буква назначенного тома
    /// библиотеки, или сервер прямо сказал, что буква отдана мастер-тому (тогда назначения тома может и не быть: ПК
    /// загрузился во время правки). Пока она есть, том библиотеки не монтируется — ни из ответа сервера, ни из
    /// assignment.json, ни после <see cref="CachedPersonalAfter"/> без сервера, — а подключённый отпускается.
    /// </summary>
    private char? MasterLetter =>
        _master is { DriveLetter: { Length: > 0 } letter }
        && (_libraryLetterReserved || (_desired is { DriveLetter: { Length: > 0 } library } && char.ToUpperInvariant(library[0]) == char.ToUpperInvariant(letter[0])))
            ? char.ToUpperInvariant(letter[0])
            : null;

    /// <summary>Буква назначенного тома библиотеки (из ответа сервера или assignment.json).</summary>
    private char? LibraryLetter => _desired is { DriveLetter: { Length: > 0 } letter } ? char.ToUpperInvariant(letter[0]) : null;

    /// <summary>
    /// Буква, на которую тому библиотеки сейчас нельзя: её берёт назначенный мастер-том или мастер-том уже на ней —
    /// снятый, но не отключившийся, или подключённый до перезапуска службы, когда назначения помощник ещё не знает.
    /// </summary>
    private char? HeldLetter => MasterLetter ?? (_masterOnLetter is { } held && held == LibraryLetter ? held : null);

    public async Task TickAsync(CancellationToken ct)
    {
        if (_dormantSince is { } since)
        {
            if (_clock.GetElapsedTime(since) < DormantFor)
            {
                return;
            }

            _dormantSince = null; // проснулись: снова запрос при старте — вдруг библиотеку включили
        }

        if (!_known)
        {
            await LearnAssignmentAsync(ct);
            if (Dormant)
            {
                return;
            }
        }

        // Порядок важен, когда мастер-том на букве библиотеки: букву сначала отпускают, потом берут. Обычно первым идёт том
        // библиотеки. Мастер-том первым — когда с него сняли назначение, а он не отключился в прошлый раз, и в первом такте
        // после запуска службы: мастер-том, подключённый до перезапуска, виден раньше, чем том из assignment.json
        // попробует встать на его букву.
        MountedVolume report;
        MasterReport? master;
        if (!_masterChecked || (MasterLetter is null && HeldLetter is not null))
        {
            _windowsTimedOut = false; // от прошлого такта: в этом том библиотеки ещё не применялся
            master = await ApplyMasterAsync(ct);
            report = await ApplyAsync(ct);
        }
        else
        {
            report = await ApplyAsync(ct);
            master = await ApplyMasterAsync(ct);
        }

        var reply = await ReportAsync(report, master, ct);
        if (reply is null || Dormant)
        {
            return;
        }

        var heldBefore = HeldLetter is not null;
        var masterChanged = !Equals(reply.Master, _master) || !_masterKnown;
        _masterKnown = true;
        _master = reply.Master;
        _libraryLetterReserved = reply.Master is not null && reply.LibraryLetterReserved;

        var changed = false;
        var masterApplied = false;
        if (masterChanged && heldBefore && MasterLetter is null)
        {
            // Правку закрыли: сначала отключается мастер-том — он освобождает букву, на которую вернётся том библиотеки.
            // Не отключился (открыты файлы) — том библиотеки ждёт (HeldLetter), пока не отключится.
            master = await ApplyMasterAsync(ct);
            masterApplied = changed = true;
        }

        var volumeApplied = false;
        if (reply.Volume is null && _libraryLetterReserved)
        {
            // Буква тома отдана мастер-тому: тома нет намеренно. Назначение и assignment.json не трогаем — после правки том
            // вернётся (с ответом сервера, как прежде), а при перезагрузке без сервера будет что подключить.
            logger.LogDebug("Server withholds the library volume: its drive letter is given to the master volume");
        }
        else if (reply.Volume is null && _desired is not null && volumes.IsPersonalTarget(_desired))
        {
            // Личный диск не отпускаем из-за пустого ответа (диск готовится, сбой TrueNAS): на нём сессия игрока. Он
            // сменится при следующем старте помощника.
            logger.LogDebug("Server sent no assignment; keeping the personal games disk until the next helper start");
        }
        else if (reply.Volume is { } offered && volumes.IsPersonalTarget(offered) && offered.TargetIqn != _personalConfirmed)
        {
            // Личный диск в ответе на отчёт: сервер не сбрасывал его для этой загрузки. Берём только из запроса при старте.
            logger.LogInformation("Personal games disk offered in a status reply; asking for it at boot to get it reset first");
            await LearnAssignmentAsync(ct);
            if (Dormant)
            {
                return;
            }

            report = await ApplyAsync(ct);
            volumeApplied = changed = true;
        }
        else if (!Equals(reply.Volume, _desired))
        {
            // Новая версия или откат: применяем сразу, не дожидаясь следующего такта.
            logger.LogInformation("Assignment changed: {Old} -> {New}", _desired?.LibraryVersion, reply.Volume?.LibraryVersion);
            _desired = reply.Volume;
            _known = true;
            await cache.SaveAsync((await identity.ReadAsync(ct)).Hwid, _desired, ct);
            report = await ApplyAsync(ct);
            volumeApplied = changed = true;
        }

        if (!volumeApplied && heldBefore != (HeldLetter is not null))
        {
            // Буква перешла к мастер-тому (том библиотеки отпускается) или вернулась от него (том снова монтируется).
            report = await ApplyAsync(ct);
            changed = true;
        }

        if (masterChanged && !masterApplied)
        {
            // Администратор открыл или закрыл правку мастер-тома.
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
        _masterChecked = true;
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

        if (MasterLetter is { } letter && _master is { } held && LastReport is { State: not "none", TargetIqn: not null } library)
        {
            // Том библиотеки ещё на букве мастер-тома (ApplyAsync в этом такте его не отпустил: с тома запущена игра, открыты
            // файлы, сбой): мастер-том не подключаем — буква занята. Насильно том не отключается; повтор в следующем такте.
            var reason = library.Error is { } error && error.StartsWith(VolumeManager.MasterWaits, StringComparison.Ordinal)
                ? error[VolumeManager.MasterWaits.Length..]
                : library.Error ?? library.State;
            logger.LogDebug("Master volume waits for {Letter}: held by the library volume {Iqn}: {Reason}", letter, library.TargetIqn, reason);
            return LastMasterReport = new MasterReport("failed", held.TargetIqn, held.DriveLetter, $"drive letter {letter}: is held by the library volume: {reason}");
        }

        try
        {
            LastMasterReport = await masters.ApplyAsync(_master, _masterKnown, ct);
            if (LastMasterReport?.State != MasterManager.UnknownState)
            {
                _lastKnownMaster = LastMasterReport;
                _masterSeen = true;
            }

            _masterOnLetter = LastMasterReport switch
            {
                null or { State: "none" } => null,
                { State: "mounted" or "mounting" or "failed", DriveLetter: { Length: > 0 } onLetter } => char.ToUpperInvariant(onLetter[0]),
                _ => _masterOnLetter,
            };
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
            var answer = await api.GetVolumeAsync(await volumes.PersonalAttachedAsync(ct), ct, library);
            Awake();
            _desired = answer.Volume;
            _known = true;
            _unreachableSince = null;
            _personalConfirmed = _desired is not null && volumes.IsPersonalTarget(_desired) ? _desired.TargetIqn : null;
            if (answer.LibraryLetterReserved)
            {
                // Тома нет, потому что его буква отдана мастер-тому (ПК загрузили во время правки), а не потому, что нечего
                // монтировать: assignment.json не затираем — без сервера при следующей загрузке будет что подключить.
                logger.LogInformation("Server withholds the library volume at boot: its drive letter is given to the master volume");
            }
            else
            {
                await cache.SaveAsync((await identity.ReadAsync(ct)).Hwid, _desired, ct);
            }
        }
        catch (ServerUnavailableException ex) when (IsMissingLibrary(ex))
        {
            await SleepAsync(ex, ct);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            // Сервер клуба недоступен (или машина не одобрена): монтируем то, что было назначено в прошлый раз.
            _desired = await cache.LoadAsync((await identity.ReadAsync(ct)).Hwid, ct);
            if (ex is ServerUnavailableException { Unreachable: true })
            {
                _unreachableSince ??= _clock.GetTimestamp();
            }
            else
            {
                _unreachableSince = null;
            }

            if (_desired is not null && volumes.IsPersonalTarget(_desired))
            {
                // Личный диск без сброса сервером — с данными прошлого игрока. Пока сервер отвечает ошибкой или недоступен
                // недолго (перезапуск службы, DHCP ещё не выдал адрес), спрашиваем снова каждый такт; дольше
                // CachedPersonalAfter — монтируем как есть: без игр клуб не работает.
                if (_unreachableSince is not { } since || _clock.GetElapsedTime(since) < CachedPersonalAfter)
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
        var masterLetter = HeldLetter;
        if (!_known && masterLetter is null)
        {
            // Ни сервера, ни сохранённого назначения — ничего не трогаем (и ничего не отключаем).
            return LastReport = new MountedVolume("none", Error: "no assignment known yet");
        }

        try
        {
            // Буква тома отдана мастер-тому: не монтируем, какое бы назначение ни было (ответ сервера, assignment.json,
            // сохранённый личный диск после 3 минут без сервера), — подключённый том отпускаем.
            LastReport = masterLetter is { } letter ? await volumes.ReleaseForMasterAsync(_desired, letter, ct) : await volumes.ApplyAsync(_desired, ct);
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

            // Имя Windows — в каждом отчёте (1.5.2): регистрация гибрида была ещё в specialize, до переименования ПК. Отчёт
            // библиотеки не-G: — только её том и мастер-том: факты о ПК сервер берёт из отчёта G:.
            var facts = await identity.ReadAsync(ct);
            var status = library is null
                ? new MachineStatus(options.HelperVersion, facts.BootTime, report, facts.DhcpServers, facts.ImageVersion, facts.SystemDisk, facts.SecureBoot,
                    facts.InitiatorIqn, master, facts.Hostname)
                : new MachineStatus(options.HelperVersion, facts.BootTime, report, Master: master);
            var reply = await api.ReportStatusAsync(status, ct, library);
            Awake();
            if (report.Contents is not null)
            {
                _contentsReportedFor = report.LibraryVersion;
            }

            return reply;
        }
        catch (ServerUnavailableException ex) when (IsMissingLibrary(ex))
        {
            await SleepAsync(ex, ct);
            return null;
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            logger.LogWarning("Status report failed: {Reason}", ex.Message);
            return null;
        }
    }

    /// <summary>Сервер ответил 404 на путь библиотеки не-G:: её на сервере нет (выключена, неизвестна, сервер старше 1.6.0).</summary>
    private bool IsMissingLibrary(ServerUnavailableException ex) => library is not null && ex.Status == HttpStatusCode.NotFound;

    /// <summary>
    /// Цикл библиотеки засыпает на <see cref="DormantFor"/>: Windows не трогается (подключённый том и мастер-том остаются до
    /// перезагрузки, новые не подключаются), assignment.json библиотеки стирается — без сервера после перезагрузки её том не
    /// подключится. После сна — снова запрос при старте.
    /// </summary>
    private async Task SleepAsync(ServerUnavailableException ex, CancellationToken ct)
    {
        _dormantSince = _clock.GetTimestamp();
        _known = false;
        _desired = null;
        _personalConfirmed = null;
        await cache.SaveAsync((await identity.ReadAsync(ct)).Hwid, null, ct);
        if (_dormancyLogged)
        {
            logger.LogDebug("Library {Library} is still not on the server; checking again in {Wait}", library, DormantFor);
            return;
        }

        _dormancyLogged = true;
        logger.LogInformation(
            "Library {Library} is not on the server ({Reason}); not touching its volumes, checking again every {Wait}", library, ex.Message, DormantFor);
    }

    /// <summary>Путь библиотеки ответил: если она спала — в журнал, что снова работает.</summary>
    private void Awake()
    {
        if (_dormancyLogged)
        {
            _dormancyLogged = false;
            logger.LogInformation("Library {Library} is on the server again", library);
        }
    }
}
