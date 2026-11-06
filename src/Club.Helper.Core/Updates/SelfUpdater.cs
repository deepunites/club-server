using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Core.Updates;

/// <summary>Ход поручения в <c>helperUpdate.state</c> отчёта G: (как <c>HelperUpdateCodes.Progress</c> сервера, spec п. 3.4).</summary>
public static class UpdateProgress
{
    public const string Downloading = "downloading";
    public const string Waiting = "waiting";
    public const string Applying = "applying";
    public const string Confirming = "confirming";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
}

/// <summary>Почему помощник ждёт (<c>helperUpdate.reason</c> при <see cref="UpdateProgress.Waiting"/>, как <c>HelperUpdateCodes.Reasons</c>).</summary>
public static class UpdateWaitReasons
{
    /// <summary>Сохранения игрока выданы на этом ПК: идёт сеанс.</summary>
    public const string Session = "session";

    /// <summary>Идёт команда канала шелла (выдача или сохранение) или такт сохранений.</summary>
    public const string Command = "command";

    /// <summary>Мастер-том назначен или не отключён (правка библиотеки).</summary>
    public const string Master = "master";
}

/// <summary>
/// Самообновление в работающей службе (spec 2026-11-06, п. 4.2–4.4). Вызывается из цикла <c>HelperService</c>:
/// <see cref="Report"/> — ход поручения для отчёта G: (источник <c>updateReport</c> цикла G:, никогда не бросает);
/// <see cref="OnTick"/> — после такта, с последним принятым ответом G: и числом принятых отчётов (поручение, отмена, учёт
/// подтверждения); <see cref="AfterTickAsync"/> — после такта сохранений (загрузка в фоне, готовность, <c>applying</c>,
/// замена, завершение процесса); <see cref="MarkCleanExit"/> — остановка службы по команде Windows.
/// <list type="bullet">
/// <item>Поручение принимается, если путь строго <c>pxe/v1/files/tools/ClubDisklessHelper-X.Y.Z.exe</c>, SHA256 — 64 hex
/// строчными, размер — от 1 байта до <see cref="MaxSize"/>; иначе <c>failed</c>/<c>downloadFailed</c>. Своя сборка —
/// ничего (сервер закроет поручение по <c>helperBuild</c>). Поручение из <c>update-failed.json</c> не выполняется снова:
/// в каждом отчёте — <c>failed</c> с прежней ошибкой. Любая ошибка поручения — туда же.</item>
/// <item>Отмена — принятый ответ без этого поручения (нет или другой <c>id</c>/SHA256): загрузка или ожидание бросаются,
/// <c>.new</c> стирается. Ответа нет (сервер недоступен) — не отмена.</item>
/// <item>Загрузка — в фоне (<see cref="DisklessApiClient.DownloadAsync(string, string, long, CancellationToken)"/>, клиент
/// для больших файлов) в <see cref="UpdatePaths.New"/>; не скачалось — <c>downloadFailed</c>, SHA256 не тот —
/// <c>hashMismatch</c>, <c>.new</c> стёрт.</item>
/// <item>Готовность (п. 4.3, шаг 1): действия Windows при сбое службы на месте (иначе <c>noRecovery</c>); свободно —
/// замок <see cref="SavesService"/> взят без ожидания и сохранения не выданы (иначе <c>waiting</c>: <c>session</c> или
/// <c>command</c>), мастер-том не занят ни в одном цикле (иначе <c>master</c>). Свободно — <c>applying</c>: он уходит
/// следующим отчётом G:; замена — только после такта, где такой отчёт принят и ответ несёт то же поручение.</item>
/// <item>Замена (шаги 3–4): снова готовность, замок сохранений держится до конца; <c>.new</c> монопольно (антивирус
/// дочитал; повтор до 60 с) и SHA256 ещё раз; <c>update.json</c> <c>prepared</c>; exe → <c>.old</c>, <c>.new</c> → exe
/// (ошибка — имена обратно, <c>swapFailed</c>; вернуть не удалось — <c>restoreFailed</c> в тексте, служба не
/// завершается и повторяет возврат каждый такт); <c>swapped</c>; замок продления токена
/// (<see cref="DisklessApiClient.HoldRenewalAsync"/>) — и <see cref="IServiceControl.TerminateSelf"/>, один раз.</item>
/// <item>Новая сборка (<c>update.json</c> <c>swapped</c>, своя сборка = <c>toSha</c>, ошибки нет): <c>confirming</c>;
/// <see cref="ConfirmAfter"/> работы этого процесса и не меньше <see cref="ConfirmReports"/> принятых отчётов G: с его
/// запуска — стереть <c>update.json</c>, <c>.old</c>, <c>.failed</c>, <c>confirmed</c>, пока принятый ответ несёт поручение.</item>
/// <item>В <c>update.json</c> ошибка (страж: откат <c>startFailed</c>, <c>noRollback</c>): <c>failed</c> с ошибкой и текстом,
/// пока принятый ответ несёт это поручение; потом <c>update.json</c> стирается (поручение уже в
/// <c>update-failed.json</c>).</item>
/// </list>
/// Шаги — по строке в журнал «Приложение» с версиями и SHA256 (п. 4.5). Исключения наружу не выходят (кроме отмены
/// <c>AfterTickAsync</c> при остановке службы): сбой шага — журнал и повтор после следующего такта.
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>Сколько должна проработать новая сборка (этот процесс), чтобы стать подтверждённой.</summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(10);

    /// <summary>Сколько отчётов G: сервер должен принять у этого процесса, чтобы новая сборка стала подтверждённой.</summary>
    public const int ConfirmReports = 5;

    /// <summary>Наибольший размер сборки в поручении (spec п. 4.2).</summary>
    public const long MaxSize = 300L * 1024 * 1024;

    /// <summary>Наибольшая длина <c>helperUpdate.message</c> (spec п. 3.4).</summary>
    public const int MessageLength = 500;

    /// <summary>Сколько раз пробовать открыть <c>.new</c> монопольно (раз в <see cref="OpenPause"/>): до 60 с.</summary>
    private const int OpenTries = 60;

    private static readonly TimeSpan OpenPause = TimeSpan.FromSeconds(1);

    private const int RestoreTries = 3;

    private static readonly TimeSpan RestorePause = TimeSpan.FromMilliseconds(500);

    private const int BufferSize = 81920;

    private static readonly Regex OfferPath = new(@"\Apxe/v1/files/tools/ClubDisklessHelper-[0-9]+\.[0-9]+\.[0-9]+\.exe\z", RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Hex = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    private readonly DisklessApiClient _api;
    private readonly UpdatePaths _paths;
    private readonly UpdateStateStore _store;
    private readonly IServiceControl _service;
    private readonly SavesService _saves;
    private readonly Func<bool> _masterBusy;
    private readonly HelperOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SelfUpdater> _logger;

    /// <summary>Запуск этого процесса (монотонная отметка): от него считается подтверждение.</summary>
    private readonly long _startedAt;

    /// <summary>
    /// <see cref="MarkCleanExit"/> (поток остановки службы) и подтверждение (цикл помощника) не пересекаются: иначе «чистый
    /// выход» вернул бы на место только что стёртый <c>update.json</c>.
    /// </summary>
    private readonly Lock _stateLock = new();

    /// <summary>Ход поручения для отчёта G: — пересчитывается после каждого шага.</summary>
    private volatile HelperUpdateReport? _report;

    /// <summary>Поручение, которое выполняет эта (прежняя) сборка; <c>null</c> — нет.</summary>
    private Job? _job;

    /// <summary>Новая сборка, которая ещё не подтверждена (<c>update.json</c> при запуске); <c>null</c> — не она.</summary>
    private UpdateState? _confirming;

    /// <summary>Новая сборка подтверждена: <c>update.json</c> и <c>.old</c> стёрты, в отчёт — <c>confirmed</c>.</summary>
    private bool _confirmed;

    private Task<string>? _download;
    private CancellationTokenSource? _downloadCancel;

    /// <summary>Брошенная загрузка (отмена): следующая начнётся, только когда она закончится, — тот же <c>.new</c>.</summary>
    private Task? _abandoned;

    /// <summary>Сколько принятых отчётов G: было в прошлом <see cref="OnTick"/>: больше — пришёл новый ответ.</summary>
    private int _seenReports;

    /// <summary>Поручение на свою сборку, о котором уже написано в журнал.</summary>
    private Guid? _ignored;

    /// <summary>Возврат <c>.old</c> → exe после неудачной замены не удался: повторять каждый такт.</summary>
    private bool _restorePending;

    /// <summary>
    /// Замена сделана, процесс завершается: больше ничего не делается. Замки сохранений и продления токена, взятые заменой,
    /// не отпускаются — до конца процесса.
    /// </summary>
    private bool _terminated;

    public SelfUpdater(
        DisklessApiClient api, UpdatePaths paths, UpdateStateStore store, IServiceControl service, SavesService saves, Func<bool> masterBusy,
        HelperOptions options, TimeProvider clock, ILogger<SelfUpdater> logger)
    {
        _api = api;
        _paths = paths;
        _store = store;
        _service = service;
        _saves = saves;
        _masterBusy = masterBusy;
        _options = options;
        _clock = clock;
        _logger = logger;
        _startedAt = clock.GetTimestamp();
        Load();
        Publish();
    }

    private enum Step
    {
        /// <summary>Сборка скачивается (или загрузка ещё не начата).</summary>
        Downloading,

        /// <summary>Скачана и проверена, ПК занят (<see cref="Job.Reason"/>).</summary>
        Waiting,

        /// <summary>Свободно: <c>applying</c> в отчёт; замена — после принятого отчёта.</summary>
        Applying,

        Failed,
    }

    /// <summary>
    /// Поручение: <paramref name="offer"/> — из ответа сервера (<c>null</c> — восстановлено из <c>update.json</c> для отчёта
    /// об ошибке, <see cref="FromState"/>).
    /// </summary>
    private sealed class Job(Guid id, string sha256, string version, HelperUpdateOffer? offer)
    {
        public Guid Id => id;

        public string Sha256 => sha256;

        public string Version => version;

        public HelperUpdateOffer? Offer => offer;

        public bool FromState { get; init; }

        public Step Step { get; set; }

        public string? Reason { get; set; }

        public string? Error { get; set; }

        public string? Message { get; set; }

        /// <summary>Сколько было принятых отчётов, когда стало <c>applying</c>: больше — отчёт с ним принят.</summary>
        public int ApplyingSince { get; set; }

        public bool ApplyingAccepted { get; set; }
    }

    /// <summary>Сборка этого процесса (SHA256 exe), <see cref="HelperOptions.HelperBuild"/>.</summary>
    private string? Own => _options.HelperBuild;

    /// <summary>Ход поручения для отчёта G: (<c>null</c> — поручения нет или о нём нечего сообщить).</summary>
    public HelperUpdateReport? Report() => _report;

    /// <summary>
    /// После такта: <paramref name="accepted"/> — последний принятый ответ на отчёт G: (<see cref="HelperLoop.LastAccepted"/>),
    /// <paramref name="acceptedReports"/> — сколько отчётов G: принято с запуска процесса (<see cref="HelperLoop.AcceptedReports"/>).
    /// Число не выросло — нового ответа нет (сервер недоступен): ничего не отменяется.
    /// </summary>
    public void OnTick(StatusAccepted? accepted, int acceptedReports)
    {
        try
        {
            var fresh = accepted is not null && acceptedReports != _seenReports;
            _seenReports = acceptedReports;
            if (_terminated)
            {
                return;
            }

            var offer = fresh ? accepted!.HelperUpdate : null;
            if (_confirming is not null && Confirming(fresh, offer, acceptedReports))
            {
                return;
            }

            if (!fresh)
            {
                return;
            }

            if (_job is { } job)
            {
                if (!Carries(offer, job))
                {
                    Drop(job, offer is null ? "the server no longer sends it" : $"the server sends update {offer.Id} ({Short(offer.Sha256)}) instead");
                }
                else if (job.Step == Step.Applying && acceptedReports > job.ApplyingSince)
                {
                    job.ApplyingAccepted = true;
                }
            }

            if (_job is null && offer is not null)
            {
                Accept(offer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-update: the status reply was not handled");
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>
    /// После такта сохранений: загрузка (начать в фоне, забрать итог), готовность, замена после принятого <c>applying</c>.
    /// </summary>
    public async Task AfterTickAsync(CancellationToken ct)
    {
        try
        {
            if (_terminated)
            {
                return;
            }

            if (_restorePending)
            {
                RetryRestore();
            }

            if (_abandoned is { } abandoned)
            {
                await abandoned;
                _abandoned = null;
                if (_download is null)
                {
                    // Отменённая загрузка могла успеть докачать файл.
                    Remove(_paths.New);
                }
            }

            switch (_job)
            {
                case { Step: Step.Downloading } job:
                    await DownloadStepAsync(job);
                    break;
                case { Step: Step.Waiting } job:
                    await ReadyAsync(job);
                    break;
                case { Step: Step.Applying, ApplyingAccepted: true } job:
                    await SwapAsync(job, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-update step failed; retrying after the next tick");
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>
    /// Служба остановлена по команде Windows (OnStop/OnShutdown наследника <c>WindowsServiceLifetime</c>): у неподтверждённой
    /// новой сборки — «чистый выход» в <c>update.json</c>, следующий запуск — не попытка (перезагрузка ПК). Не бросает.
    /// </summary>
    public void MarkCleanExit()
    {
        try
        {
            lock (_stateLock)
            {
                if (Own is { } own && _store.Read(out _) is { Error: null, Phase: UpdatePhases.Swapped, CleanExit: false } state && Same(own, state.ToSha))
                {
                    _store.Write(state with { CleanExit = true });
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                _logger.LogWarning("Self-update: clean exit not recorded in update.json: {Error}", ex.Message);
            }
            catch (Exception)
            {
                // Остановку службы журнал не задерживает.
            }
        }
    }

    /// <summary>Состояние замены при запуске: ошибка для отчёта или новая сборка на проверке.</summary>
    private void Load()
    {
        UpdateState? state;
        try
        {
            state = _store.Read(out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Self-update: update.json not read: {Error}", ex.Message);
            return;
        }

        if (state is null)
        {
            return;
        }

        if (state.Error is not null)
        {
            _job = new Job(state.Id, state.ToSha, state.ToVersion, offer: null)
            {
                FromState = true, Step = Step.Failed, Error = state.Error, Message = Clip(state.Message),
            };
            _logger.LogWarning("Update {Id} to {Version} ({Sha}) failed: {Error}: {Message}; reported until the server closes it",
                state.Id, state.ToVersion, state.ToSha, state.Error, state.Message);
        }
        else if (Own is { } own && Same(own, state.ToSha) && state.Phase == UpdatePhases.Swapped)
        {
            _confirming = state;
            _logger.LogInformation(
                "Update {Id}: build {Version} ({Sha}) started (attempt {Attempts}); confirmed after {Minutes} minutes and {Reports} accepted reports without a crash",
                state.Id, state.ToVersion, state.ToSha, state.Attempts, ConfirmAfter.TotalMinutes, ConfirmReports);
        }
    }

    /// <summary>
    /// Новая сборка на проверке: подтвердить, когда пора; после подтверждения — <c>confirmed</c>, пока принятый ответ несёт
    /// поручение. <c>false</c> — проверка закончилась, ответ разбирается как обычно.
    /// </summary>
    private bool Confirming(bool fresh, HelperUpdateOffer? offer, int acceptedReports)
    {
        var state = _confirming!;
        if (_confirmed)
        {
            if (fresh && offer?.Id != state.Id)
            {
                _logger.LogInformation("Update {Id}: closed by the server; build {Version} ({Sha}) is installed", state.Id, state.ToVersion, state.ToSha);
                _confirming = null;
                _confirmed = false;
                return false;
            }

            return true;
        }

        if (_clock.GetElapsedTime(_startedAt) >= ConfirmAfter && acceptedReports >= ConfirmReports)
        {
            Confirm(state, acceptedReports);
        }

        return true;
    }

    /// <summary>Подтверждение: сначала <c>update.json</c> (без него страж сотрёт <c>.old</c> сам), потом <c>.old</c> и <c>.failed</c>.</summary>
    private void Confirm(UpdateState state, int acceptedReports)
    {
        try
        {
            lock (_stateLock)
            {
                _store.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Update {Id}: update.json not removed ({Error}); confirming again after the next tick", state.Id, ex.Message);
            return;
        }

        Remove(_paths.Old);
        Remove(_paths.Failed);
        _confirmed = true;
        _logger.LogInformation(
            "Update {Id}: build {Version} ({Sha}) confirmed after {Elapsed:g} and {Reports} accepted reports; previous build {From} removed",
            state.Id, state.ToVersion, state.ToSha, _clock.GetElapsedTime(_startedAt), acceptedReports, state.FromSha);
    }

    /// <summary>Ответ несёт то же поручение: тот же <c>id</c> (и SHA256, если поручение из ответа).</summary>
    private static bool Carries(HelperUpdateOffer? offer, Job job) =>
        offer is not null && offer.Id == job.Id && (job.Offer is null || string.Equals(offer.Sha256, job.Sha256, StringComparison.Ordinal));

    private void Accept(HelperUpdateOffer offer)
    {
        var sha = offer.Sha256 ?? "";
        if (Own is { } own && Same(sha, own))
        {
            if (_ignored != offer.Id)
            {
                _ignored = offer.Id;
                _logger.LogInformation("Update {Id}: build {Version} ({Sha}) is already running here; nothing to do", offer.Id, offer.Version, sha);
            }

            return;
        }

        var job = new Job(offer.Id, sha, offer.Version ?? "", offer);
        _job = job;
        if (_store.Failed().LastOrDefault(f => f.Id == offer.Id) is { } failed)
        {
            job.Step = Step.Failed;
            job.Error = failed.Error;
            job.Message = $"Update {offer.Id} already failed on this PC ({failed.Error}); not retried";
            _logger.LogWarning("Update {Id} to {Version} ({Sha}) failed on this PC earlier ({Error}); reporting it, not retrying",
                offer.Id, offer.Version, sha, failed.Error);
            return;
        }

        if (Implausible(offer) is { } why)
        {
            Fail(job, UpdateErrors.DownloadFailed, $"Implausible update: {why}");
            return;
        }

        job.Step = Step.Downloading;
        _logger.LogInformation("Update {Id}: build {Version} ({Sha}, {Size} bytes) offered by the server; downloading in the background",
            offer.Id, offer.Version, sha, offer.Size);
    }

    /// <summary>Почему поручению не верить (путь, SHA256, размер, версия); <c>null</c> — правдоподобно.</summary>
    private static string? Implausible(HelperUpdateOffer offer)
    {
        if (offer.Path is null || !OfferPath.IsMatch(offer.Path))
        {
            return $"download path {Clip(offer.Path, 200)} is not pxe/v1/files/tools/ClubDisklessHelper-X.Y.Z.exe";
        }

        if (offer.Sha256 is null || !Sha256Hex.IsMatch(offer.Sha256))
        {
            return $"SHA256 {Clip(offer.Sha256, 100)} is not 64 lowercase hex digits";
        }

        if (offer.Size is <= 0 or > MaxSize)
        {
            return $"size {offer.Size} bytes is not between 1 and {MaxSize}";
        }

        return string.IsNullOrWhiteSpace(offer.Version) ? "no version" : null;
    }

    /// <summary>Отмена поручения: загрузка бросается, <c>.new</c> стирается (состояние из <c>update.json</c> — тоже).</summary>
    private void Drop(Job job, string why)
    {
        _job = null;
        if (_download is { } download)
        {
            var cancel = _downloadCancel;
            cancel?.Cancel();
            _abandoned = download.ContinueWith(_ => cancel?.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _download = null;
            _downloadCancel = null;
        }
        else
        {
            Remove(_paths.New);
        }

        if (job.FromState)
        {
            DeleteState();
        }

        if (job.Step == Step.Failed)
        {
            _logger.LogInformation("Update {Id} to {Version} ({Sha}): closed by the server ({Why}); failure no longer reported", job.Id, job.Version, job.Sha256, why);
        }
        else
        {
            _logger.LogInformation("Update {Id} to {Version} ({Sha}) cancelled: {Why}; .new removed", job.Id, job.Version, job.Sha256, why);
        }
    }

    /// <summary>Ошибка поручения: <c>.new</c> стёрт, поручение — в <c>update-failed.json</c>, в отчёт — <c>failed</c>.</summary>
    private void Fail(Job job, string error, string message)
    {
        job.Step = Step.Failed;
        job.Reason = null;
        job.Error = error;
        job.Message = Clip(message);
        job.ApplyingAccepted = false;
        Remove(_paths.New);
        try
        {
            _store.AddFailed(new FailedUpdate(job.Id, job.Sha256, error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Update {Id}: update-failed.json not written: {Error}", job.Id, ex.Message);
        }

        _logger.LogWarning("Update {Id} to {Version} ({Sha}) failed: {Error}: {Message}", job.Id, job.Version, job.Sha256, error, message);
    }

    /// <summary>Загрузка в фоне: начать, а когда кончилась — сверить SHA256 и проверить готовность.</summary>
    private async Task DownloadStepAsync(Job job)
    {
        if (_download is null)
        {
            var cancel = new CancellationTokenSource();
            var offer = job.Offer!;
            _downloadCancel = cancel;
            _download = Task.Run(() => _api.DownloadAsync(offer.Path, _paths.New, offer.Size, cancel.Token), CancellationToken.None);
            return;
        }

        if (!_download.IsCompleted)
        {
            return;
        }

        var download = _download;
        _download = null;
        _downloadCancel?.Dispose();
        _downloadCancel = null;
        string sha;
        try
        {
            sha = await download;
        }
        catch (Exception ex)
        {
            Fail(job, UpdateErrors.DownloadFailed, $"Download failed: {ex.Message}");
            return;
        }

        if (!Same(sha, job.Sha256))
        {
            Fail(job, UpdateErrors.HashMismatch, $"SHA256 of the download is {sha}, the server sent {job.Sha256}");
            return;
        }

        _logger.LogInformation("Update {Id}: build {Version} downloaded ({Size} bytes), SHA256 {Sha} verified", job.Id, job.Version, job.Offer!.Size, sha);
        job.Step = Step.Waiting;
        job.Reason = null;
        await ReadyAsync(job);
    }

    /// <summary>Готовность (п. 4.3, шаг 1): нет действий при сбое — <c>noRecovery</c>; занято — <c>waiting</c>; свободно — <c>applying</c>.</summary>
    private async Task ReadyAsync(Job job)
    {
        if (RecoveryProblem() is { } why)
        {
            Fail(job, UpdateErrors.NoRecovery, why);
            return;
        }

        var (hold, reason) = await FreeAsync();
        hold?.Dispose();
        if (reason is not null)
        {
            Wait(job, reason);
            return;
        }

        job.Step = Step.Applying;
        job.Reason = null;
        job.ApplyingSince = _seenReports;
        job.ApplyingAccepted = false;
        _logger.LogInformation("Update {Id} to {Version} ({Sha}): the PC is free; applying goes with the next status report", job.Id, job.Version, job.Sha256);
    }

    private void Wait(Job job, string reason)
    {
        if (job.Step != Step.Waiting || job.Reason != reason)
        {
            _logger.LogInformation("Update {Id} to {Version} ({Sha}): waiting ({Reason})", job.Id, job.Version, job.Sha256, reason);
        }

        job.Step = Step.Waiting;
        job.Reason = reason;
        job.ApplyingAccepted = false;
    }

    /// <summary>
    /// Свободен ли помощник: замок сохранений (взят — держатель для вызывающего) и мастер-том. Занято — причина
    /// (<see cref="UpdateWaitReasons"/>), замок не взят.
    /// </summary>
    private async Task<(IDisposable? Hold, string? Reason)> FreeAsync()
    {
        var hold = await _saves.TryHoldForUpdateAsync();
        if (hold is null)
        {
            return (null, _saves.Attached ? UpdateWaitReasons.Session : UpdateWaitReasons.Command);
        }

        bool master;
        try
        {
            master = _masterBusy();
        }
        catch
        {
            hold.Dispose();
            throw;
        }

        if (master)
        {
            hold.Dispose();
            return (null, UpdateWaitReasons.Master);
        }

        return (hold, null);
    }

    /// <summary>Действий Windows при сбое службы нет — почему замены не будет; есть — <c>null</c>.</summary>
    private string? RecoveryProblem()
    {
        try
        {
            return _service.RecoveryConfigured()
                ? null
                : "The service has no recovery actions restart/5000/restart/5000/restart/30000: Windows would not start it again after the swap";
        }
        catch (Exception ex)
        {
            return $"The service recovery actions were not read: {ex.Message}";
        }
    }

    /// <summary>Замена (п. 4.3, шаги 3–4) после такта, где отчёт с <c>applying</c> принят.</summary>
    private async Task SwapAsync(Job job, CancellationToken ct)
    {
        job.ApplyingAccepted = false;
        if (RecoveryProblem() is { } why)
        {
            Fail(job, UpdateErrors.NoRecovery, why);
            return;
        }

        var (hold, reason) = await FreeAsync();
        if (hold is null)
        {
            Wait(job, reason!);
            return;
        }

        var swapped = false;
        try
        {
            if (Own is not { Length: > 0 } own)
            {
                Fail(job, UpdateErrors.SwapFailed, "The SHA256 of the running exe is unknown");
                return;
            }

            string sha;
            try
            {
                sha = await HashExclusiveAsync(_paths.New, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(job, UpdateErrors.SwapFailed, $".new not opened exclusively: {ex.Message}");
                return;
            }

            if (!Same(sha, job.Sha256))
            {
                Fail(job, UpdateErrors.HashMismatch, $"SHA256 of .new is {sha} before the swap, the server sent {job.Sha256}");
                return;
            }

            var prepared = new UpdateState(UpdateState.CurrentVersion, job.Id, own.ToLowerInvariant(), job.Sha256, job.Version, UpdatePhases.Prepared,
                Attempts: 0, CleanExit: false, _clock.GetUtcNow(), Error: null, Message: null);
            try
            {
                _store.Write(prepared);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteState();
                Fail(job, UpdateErrors.SwapFailed, $"update.json not written: {ex.Message}");
                return;
            }

            if (Rename() is { } failure)
            {
                DeleteState();
                Fail(job, UpdateErrors.SwapFailed, failure);
                return;
            }

            // Имена уже поменяны: дальше ничто, кроме остановки службы, не мешает завершить процесс — иначе он жил бы с
            // новым exe на диске и с замком сохранений (attach — updating) до перезагрузки.
            swapped = true;
            try
            {
                _store.Write(prepared with { Phase = UpdatePhases.Swapped });
            }
            catch (Exception ex)
            {
                // Страж новой сборки считает prepared после переименований тем же, что swapped.
                Quietly(log => log.LogWarning("Update {Id}: phase swapped not written to update.json ({Error}); prepared stays", job.Id, ex.Message));
            }

            Quietly(log => log.LogInformation(
                "Update {Id}: exe replaced with build {Version} ({Sha}), previous build {From} kept as .old; ending the process so that Windows restarts the service",
                job.Id, job.Version, job.Sha256, own));
            _ = await _api.HoldRenewalAsync(ct);
            _terminated = true;
            Publish();
            _service.TerminateSelf();
        }
        finally
        {
            if (!swapped)
            {
                hold.Dispose();
            }
        }
    }

    /// <summary>
    /// <c>.new</c> монопольно (антивирус ещё читает — повтор раз в <see cref="OpenPause"/>, до <see cref="OpenTries"/> раз) и
    /// SHA256 по нему же.
    /// </summary>
    private async Task<string> HashExclusiveAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (attempt < OpenTries && File.Exists(path))
            {
                await Task.Delay(OpenPause, _clock, ct);
                continue;
            }

            await using (stream)
            {
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            }
        }
    }

    /// <summary>
    /// Переименования подряд: exe → <c>.old</c>, <c>.new</c> → exe. Ошибка — имена обратно и текст ошибки (вернуть не
    /// удалось — <c>restoreFailed</c>, возврат повторяется каждый такт); <c>null</c> — прошло.
    /// </summary>
    private string? Rename()
    {
        try
        {
            File.Move(_paths.Exe, _paths.Old, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Cannot rename the exe to .old: {ex.Message}; names unchanged";
        }

        try
        {
            File.Move(_paths.New, _paths.Exe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (RestoreExe())
            {
                return $"Cannot rename .new to the exe: {ex.Message}; names restored";
            }

            _restorePending = true;
            return $"Cannot rename .new to the exe: {ex.Message}; restoreFailed: .old is not renamed back to the exe, retrying every tick";
        }
    }

    /// <summary>Вернуть <c>.old</c> на место exe (несколько попыток); <c>false</c> — не вышло.</summary>
    private bool RestoreExe()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(_paths.Old, _paths.Exe);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < RestoreTries)
            {
                Thread.Sleep(RestorePause);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Self-update: cannot rename .old back to the exe: {Error}; the service would not start", ex.Message);
                return false;
            }
        }
    }

    private void RetryRestore()
    {
        if (File.Exists(_paths.Exe) || !File.Exists(_paths.Old))
        {
            _restorePending = false;
            return;
        }

        if (RestoreExe())
        {
            _restorePending = false;
            _logger.LogInformation("Self-update: .old renamed back to the exe; the previous build is in place again");
        }
    }

    /// <summary>
    /// Строка в журнал после переименований: отказ журнала (агрегатор журналов бросает исключение провайдера, например
    /// журнала событий) не мешает завершить процесс.
    /// </summary>
    private void Quietly(Action<ILogger> write)
    {
        try
        {
            write(_logger);
        }
        catch (Exception)
        {
            // Журнал — не повод оставить процесс жить после замены exe.
        }
    }

    private void DeleteState()
    {
        try
        {
            _store.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Self-update: update.json not removed: {Error}", ex.Message);
        }
    }

    private void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Self-update: {File} not removed: {Error}", Path.GetFileName(path), ex.Message);
        }
    }

    /// <summary>Пересчитать <see cref="Report"/> после шага.</summary>
    private void Publish()
    {
        try
        {
            _report = Current();
        }
        catch (Exception)
        {
            _report = null;
        }
    }

    private HelperUpdateReport? Current()
    {
        if (_confirming is { } state)
        {
            // Состояние восстановлено стражем без update.json: поручение неизвестно — сообщать не о чем.
            return state.Id == Guid.Empty ? null : new HelperUpdateReport(state.Id.ToString("D"), _confirmed ? UpdateProgress.Confirmed : UpdateProgress.Confirming);
        }

        if (_job is not { } job || job.Id == Guid.Empty)
        {
            return null;
        }

        var id = job.Id.ToString("D");
        return job.Step switch
        {
            Step.Downloading => new HelperUpdateReport(id, UpdateProgress.Downloading),
            Step.Waiting => new HelperUpdateReport(id, UpdateProgress.Waiting, Reason: job.Reason),
            Step.Applying => new HelperUpdateReport(id, UpdateProgress.Applying),
            _ => new HelperUpdateReport(id, UpdateProgress.Failed, Error: job.Error, Message: job.Message),
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";

    private static string? Clip(string? text, int length = MessageLength) => text is { } t && t.Length > length ? t[..length] : text;
}
