using System.Net;
using System.Security.Cryptography;
using System.Text;
using Club.Helper.Core;
using Club.Helper.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Самообновление в работающей службе (spec 2026-11-06, п. 4.2–4.4) на временных папках, с поддельным сервером клуба и
/// поддельной службой Windows: правдоподобие поручения, своя сборка, список ошибок; загрузка и SHA256; свободный момент и
/// причины ожидания (<c>session</c>, <c>command</c>, <c>master</c>); <c>applying</c> — только принятым отчётом, замена с
/// возвратом имён, завершение процесса один раз под замками сохранений и продления токена; отмена (принятый ответ без
/// поручения) и «ответа нет — не отмена»; подтверждение — 10 минут и 5 принятых отчётов с запуска процесса.
/// </summary>
public sealed class SelfUpdaterTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string BuildPath = "pxe/v1/files/tools/ClubDisklessHelper-1.8.1.exe";
    private static readonly byte[] OldBuild = Encoding.UTF8.GetBytes("helper 1.8.0 " + new string('o', 5000));
    private static readonly byte[] NewBuild = Encoding.UTF8.GetBytes("helper 1.8.1 " + new string('n', 70_000));
    private static readonly Guid JobId = Guid.Parse("6f1c2a3b-0000-4000-8000-000000000022");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-self-updater-{Guid.NewGuid():N}");
    private readonly UpdatePaths _paths;
    private readonly UpdateStateStore _store;
    private readonly ManualClock _clock = new();
    private readonly FakeServer _server = new();
    private readonly FakeService _service = new();
    private readonly DisklessApiClient _api;
    private readonly SavesService _saves;
    private readonly ListLogger<SelfUpdater> _log = new();
    private bool _masterBusy;

    /// <summary>Принятых отчётов G: с запуска процесса (<see cref="HelperLoop.AcceptedReports"/>).</summary>
    private int _reports;

    public SelfUpdaterTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "Program Files", "ClubDiskless"), Path.Combine(_root, "ProgramData", "ClubDiskless"));
        Directory.CreateDirectory(_paths.InstallDir);
        Directory.CreateDirectory(_paths.StateDir);
        File.WriteAllBytes(_paths.Exe, OldBuild);
        _store = new UpdateStateStore(_paths);
        _server.Files["/" + BuildPath] = NewBuild;

        var credentials = new InMemoryCredentialStore { Current = new StoredCredentials(Guid.NewGuid(), "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "hwid") };
        _api = new DisklessApiClient(Client(), new HelperOptions { HelperVersion = "1.8.0" }, credentials, new FakeIdentity("hwid"), downloads: Client());
        var profile = Path.Combine(_root, "Users", "player");
        Directory.CreateDirectory(profile);
        var profiles = new FakeProfiles();
        profiles.Paths[Sid] = profile;
        _saves = new SavesService(_api, profiles, new FakeDrives(), () => false, _paths.StateDir, _clock, new ListLogger<SavesService>());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HttpClient Client() => new(_server, disposeHandler: false) { BaseAddress = new Uri("https://club-server/") };

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static string ShaOf(string path) => Sha(File.ReadAllBytes(path));

    private static HelperUpdateOffer Offer() => new(JobId, "1.8.1", Sha(NewBuild), NewBuild.Length, BuildPath);

    /// <summary>Служба запущена сборкой <paramref name="running"/> (по умолчанию — прежняя, 1.8.0).</summary>
    private SelfUpdater Updater(byte[]? running = null)
    {
        _reports = 0; // счётчик принятых отчётов — с запуска процесса
        return new SelfUpdater(_api, _paths, _store, _service, _saves, () => _masterBusy,
            new HelperOptions { HelperVersion = "1.8.0", HelperBuild = Sha(running ?? OldBuild) }, _clock, _log);
    }

    /// <summary>Такт: отчёт G: принят, ответ несёт <paramref name="offer"/> (или поручения нет).</summary>
    private void Accepted(SelfUpdater updater, HelperUpdateOffer? offer)
    {
        _reports++;
        updater.OnTick(new StatusAccepted(DateTimeOffset.UtcNow, null, HelperUpdate: offer), _reports);
    }

    /// <summary>Такт: сервер недоступен — отчёт не принят, у цикла остался прежний ответ.</summary>
    private void NotAccepted(SelfUpdater updater, HelperUpdateOffer? lastOffer) =>
        updater.OnTick(_reports == 0 ? null : new StatusAccepted(DateTimeOffset.UtcNow, null, HelperUpdate: lastOffer), _reports);

    /// <summary>Шаги после тактов, пока загрузка в фоне не кончится (до 10 с).</summary>
    private static async Task<HelperUpdateReport?> DownloadedAsync(SelfUpdater updater)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await updater.AfterTickAsync(CancellationToken.None);
            if (updater.Report() is not { State: UpdateProgress.Downloading } || DateTime.UtcNow > deadline)
            {
                return updater.Report();
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Поручение принято, сборка скачана и проверена.</summary>
    private async Task<SelfUpdater> DownloadedUpdaterAsync()
    {
        var updater = Updater();
        Accepted(updater, Offer());
        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Downloading), updater.Report());
        await DownloadedAsync(updater);
        return updater;
    }

    private UpdateState Stored()
    {
        var state = _store.Read(out var unreadable);
        Assert.False(unreadable);
        return Assert.IsType<UpdateState>(state);
    }

    private void AssertNotSwapped()
    {
        Assert.Equal(0, _service.Terminations);
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
        Assert.False(File.Exists(_paths.Old));
        Assert.Null(_store.Read(out _));
        Assert.False(_saves.Updating);
    }

    /// <summary>Замок продления токена свободен (берётся за 200 мс).</summary>
    private bool RenewalFree()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            _api.HoldRenewalAsync(timeout.Token).GetAwaiter().GetResult().Dispose();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("path-parent")]
    [InlineData("path-absolute")]
    [InlineData("path-name")]
    [InlineData("sha-upper")]
    [InlineData("sha-short")]
    [InlineData("size-over")]
    [InlineData("size-zero")]
    public async Task Offer_BadPath_FailedDownloadFailed(string kind)
    {
        var offer = kind switch
        {
            "path-parent" => Offer() with { Path = "pxe/v1/files/tools/../../../secrets/ClubDisklessHelper-1.8.1.exe" },
            "path-absolute" => Offer() with { Path = "https://evil.example/pxe/v1/files/tools/ClubDisklessHelper-1.8.1.exe" },
            "path-name" => Offer() with { Path = "pxe/v1/files/tools/install-helper-1.8.1.ps1" },
            "sha-upper" => Offer() with { Sha256 = Sha(NewBuild).ToUpperInvariant() },
            "sha-short" => Offer() with { Sha256 = Sha(NewBuild)[..63] },
            "size-over" => Offer() with { Size = SelfUpdater.MaxSize + 1 },
            _ => Offer() with { Size = 0 },
        };
        var updater = Updater();

        Accepted(updater, offer);
        await updater.AfterTickAsync(CancellationToken.None);

        var report = Assert.IsType<HelperUpdateReport>(updater.Report());
        Assert.Equal((JobId.ToString(), UpdateProgress.Failed, UpdateErrors.DownloadFailed), (report.Id, report.State, report.Error));
        Assert.False(string.IsNullOrEmpty(report.Message));
        Assert.Empty(_server.Downloads);
        Assert.False(File.Exists(_paths.New));
        Assert.Equal([new FailedUpdate(JobId, offer.Sha256, UpdateErrors.DownloadFailed)], _store.Failed());

        // То же поручение снова — только «failed», без загрузки.
        Accepted(updater, offer);
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(UpdateProgress.Failed, updater.Report()?.State);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task Offer_OwnBuild_Ignored()
    {
        var updater = Updater();

        Accepted(updater, Offer() with { Sha256 = Sha(OldBuild), Size = OldBuild.Length });
        await updater.AfterTickAsync(CancellationToken.None);
        Accepted(updater, Offer() with { Sha256 = Sha(OldBuild), Size = OldBuild.Length });
        await updater.AfterTickAsync(CancellationToken.None);

        Assert.Null(updater.Report());
        Assert.Empty(_server.Downloads);
        Assert.False(File.Exists(_paths.New));
        Assert.Empty(_store.Failed());
    }

    [Fact]
    public async Task Offer_InFailedList_ReportsFailed_NoDownload()
    {
        _store.AddFailed(new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.HashMismatch));

        // Список ошибок переживает перезапуск службы: каждый процесс читает его заново.
        foreach (var updater in new[] { Updater(), Updater() })
        {
            Accepted(updater, Offer());
            await updater.AfterTickAsync(CancellationToken.None);
            var report = Assert.IsType<HelperUpdateReport>(updater.Report());
            Assert.Equal((JobId.ToString(), UpdateProgress.Failed, UpdateErrors.HashMismatch), (report.Id, report.State, report.Error));
        }

        Assert.Empty(_server.Downloads);

        // Новое поручение (другой id) выполняется.
        var next = Updater();
        Accepted(next, Offer() with { Id = Guid.NewGuid() });
        Assert.Equal(UpdateProgress.Downloading, next.Report()?.State);
    }

    [Fact]
    public async Task Download_HashMismatch_NewDeleted()
    {
        _server.Files["/" + BuildPath] = [.. NewBuild[..^1], (byte)'!'];
        var updater = Updater();

        Accepted(updater, Offer());
        var report = Assert.IsType<HelperUpdateReport>(await DownloadedAsync(updater));

        Assert.Equal((UpdateProgress.Failed, UpdateErrors.HashMismatch), (report.State, report.Error));
        Assert.Contains(Sha(NewBuild), report.Message);
        Assert.Equal(["/" + BuildPath], _server.Downloads);
        Assert.False(File.Exists(_paths.New));
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.HashMismatch)], _store.Failed());
        AssertNotSwapped();
    }

    [Fact]
    public async Task Download_ServerFails_DownloadFailed()
    {
        _server.Files.Clear();
        var updater = Updater();

        Accepted(updater, Offer());
        var report = Assert.IsType<HelperUpdateReport>(await DownloadedAsync(updater));

        Assert.Equal((UpdateProgress.Failed, UpdateErrors.DownloadFailed), (report.State, report.Error));
        Assert.False(File.Exists(_paths.New));
        AssertNotSwapped();
    }

    [Fact]
    public async Task Busy_Attached_WaitingSession()
    {
        Assert.True((await _saves.AttachAsync("player-1", Sid, CancellationToken.None)).Ok);

        var updater = await DownloadedUpdaterAsync();

        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Waiting, Reason: UpdateWaitReasons.Session), updater.Report());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.New));
        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(UpdateWaitReasons.Session, updater.Report()?.Reason);
        AssertNotSwapped();
    }

    [Fact]
    public async Task Busy_Command_WaitingCommand()
    {
        _server.AttachGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attach = _saves.AttachAsync("player-1", Sid, CancellationToken.None);
        await _server.AttachStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var updater = await DownloadedUpdaterAsync();

        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Waiting, Reason: UpdateWaitReasons.Command), updater.Report());
        _server.AttachGate.SetResult();
        Assert.True((await attach).Ok);
        AssertNotSwapped();
    }

    [Fact]
    public async Task Busy_Master_WaitingMaster()
    {
        _masterBusy = true;

        var updater = await DownloadedUpdaterAsync();

        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Waiting, Reason: UpdateWaitReasons.Master), updater.Report());
        Assert.False(_saves.Updating);

        // Мастер-том отключён — свободно.
        _masterBusy = false;
        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Applying), updater.Report());
        AssertNotSwapped();
    }

    [Fact]
    public async Task Applying_OnlyAfterAcceptedFullReport()
    {
        var updater = await DownloadedUpdaterAsync();
        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Applying), updater.Report());

        // applying ещё не ушёл ни одним принятым отчётом — замены нет.
        await updater.AfterTickAsync(CancellationToken.None);
        AssertNotSwapped();

        // Сервер недоступен: отчёт с applying не принят — замены нет, попытка в следующем такте.
        NotAccepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        AssertNotSwapped();
        Assert.Equal(UpdateProgress.Applying, updater.Report()?.State);

        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(1, _service.Terminations);
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
    }

    [Fact]
    public async Task NoRecovery_NoSwap()
    {
        _service.Recovery = false;

        var updater = await DownloadedUpdaterAsync();

        var report = Assert.IsType<HelperUpdateReport>(updater.Report());
        Assert.Equal((UpdateProgress.Failed, UpdateErrors.NoRecovery), (report.State, report.Error));
        Assert.False(File.Exists(_paths.New));
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.NoRecovery)], _store.Failed());
        AssertNotSwapped();
    }

    [Fact]
    public async Task NoRecovery_AfterApplying_NoSwap()
    {
        var updater = await DownloadedUpdaterAsync();
        _service.Recovery = false; // действия при сбое сняли между applying и заменой

        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);

        Assert.Equal(UpdateErrors.NoRecovery, updater.Report()?.Error);
        AssertNotSwapped();
    }

    [Fact]
    public async Task Swap_RenameFails_NamesRestored_SwapFailed()
    {
        // Первое переименование (exe → .old) не проходит: на месте .old — непустая папка.
        Directory.CreateDirectory(_paths.Old);
        File.WriteAllText(Path.Combine(_paths.Old, "busy"), "x");
        var updater = await DownloadedUpdaterAsync();

        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);

        var report = Assert.IsType<HelperUpdateReport>(updater.Report());
        Assert.Equal((UpdateProgress.Failed, UpdateErrors.SwapFailed), (report.State, report.Error));
        Assert.Equal(0, _service.Terminations);
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Exe));
        Assert.False(File.Exists(_paths.New));
        Assert.Null(_store.Read(out var unreadable));
        Assert.False(unreadable);
        Assert.False(_saves.Updating);
        Assert.True(RenewalFree());
        Assert.Equal([new FailedUpdate(JobId, Sha(NewBuild), UpdateErrors.SwapFailed)], _store.Failed());

        // Замок сохранений отпущен: команды шелла снова выполняются.
        Assert.True((await _saves.AttachAsync("player-1", Sid, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Swap_Success_StateSwapped_Terminated()
    {
        bool? savesHeld = null;
        bool? renewalHeld = null;
        _service.OnTerminate = () =>
        {
            savesHeld = _saves.Updating;
            renewalHeld = !RenewalFree();
        };
        var updater = await DownloadedUpdaterAsync();

        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);

        Assert.Equal(1, _service.Terminations);
        Assert.True(savesHeld);
        Assert.True(renewalHeld);
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
        Assert.False(File.Exists(_paths.New));
        var state = Stored();
        Assert.Equal((UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.Swapped, 0, false, (string?)null),
            (state.V, state.Id, state.FromSha, state.ToSha, state.ToVersion, state.Phase, state.Attempts, state.CleanExit, state.Error));

        // Процесс в тесте не завершился: больше ничего не делается, TerminateSelf — один раз.
        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(1, _service.Terminations);

        // Страж нового процесса — новая сборка, первая попытка, проверка.
        Assert.Equal(GuardOutcome.RunConfirming, UpdateGuard.Run(_paths, _store, Sha(NewBuild), ShaOf, _clock));
    }

    [Fact]
    public async Task Swap_LogFailsAfterRenames_StillTerminated()
    {
        // Журнал событий отказал (агрегатор журналов бросает исключение провайдера) сразу после переименований: exe уже
        // новый — процесс всё равно завершается, а не остаётся жить с замком сохранений до перезагрузки.
        var updater = new SelfUpdater(_api, _paths, _store, _service, _saves, () => _masterBusy,
            new HelperOptions { HelperVersion = "1.8.0", HelperBuild = Sha(OldBuild) }, _clock, new FailingLogger("exe replaced"));
        _reports = 0;
        Accepted(updater, Offer());
        await DownloadedAsync(updater);
        Assert.Equal(UpdateProgress.Applying, updater.Report()?.State);

        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);

        Assert.Equal(1, _service.Terminations);
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(UpdatePhases.Swapped, Stored().Phase);
    }

    [Fact]
    public async Task Cancel_WhileWaiting_NewDeleted_NoSwap()
    {
        _masterBusy = true;
        var updater = await DownloadedUpdaterAsync();
        Assert.Equal(UpdateProgress.Waiting, updater.Report()?.State);
        Assert.True(File.Exists(_paths.New));

        // Review Focus 3: отменили, пока ПК ждёт со скачанным .new.
        Accepted(updater, null);

        Assert.Null(updater.Report());
        Assert.False(File.Exists(_paths.New));
        _masterBusy = false;
        await updater.AfterTickAsync(CancellationToken.None);
        NotAccepted(updater, null);
        await updater.AfterTickAsync(CancellationToken.None);
        AssertNotSwapped();
        Assert.Empty(_store.Failed());
    }

    [Fact]
    public async Task Cancel_WhileApplying_NoSwap()
    {
        var updater = await DownloadedUpdaterAsync();
        Assert.Equal(UpdateProgress.Applying, updater.Report()?.State);

        // Ответ на отчёт с applying уже без поручения (отменили) — замены нет.
        Accepted(updater, null);
        await updater.AfterTickAsync(CancellationToken.None);

        Assert.Null(updater.Report());
        Assert.False(File.Exists(_paths.New));
        AssertNotSwapped();
    }

    [Fact]
    public async Task NoReply_IsNotCancel()
    {
        _masterBusy = true;
        var updater = Updater();
        Accepted(updater, Offer());
        NotAccepted(updater, Offer());
        await DownloadedAsync(updater);
        Assert.Equal(UpdateWaitReasons.Master, updater.Report()?.Reason);

        // Сервер недоступен много тактов — поручение не отменяется, .new на месте.
        for (var i = 0; i < 5; i++)
        {
            NotAccepted(updater, Offer());
            await updater.AfterTickAsync(CancellationToken.None);
        }

        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Waiting, Reason: UpdateWaitReasons.Master), updater.Report());
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.New));

        _masterBusy = false;
        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        Assert.Equal(UpdateProgress.Applying, updater.Report()?.State);
    }

    [Fact]
    public async Task Cancel_WhileDownloading_NewDeleted_NextOfferDownloads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.DownloadGate = gate;
        var updater = Updater();
        Accepted(updater, Offer());
        await updater.AfterTickAsync(CancellationToken.None);
        await _server.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Отменили посреди загрузки; сразу же — новое поручение на ту же сборку.
        var next = Offer() with { Id = Guid.NewGuid() };
        Accepted(updater, next);
        Assert.Equal(new HelperUpdateReport(next.Id.ToString(), UpdateProgress.Downloading), updater.Report());
        _server.DownloadGate = null;
        gate.SetResult();

        Assert.Equal(new HelperUpdateReport(next.Id.ToString(), UpdateProgress.Applying), await DownloadedAsync(updater));
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.New));
        Assert.Empty(_store.Failed());
    }

    [Fact]
    public void Confirm_After10MinAnd5Reports_DeletesOldAndState()
    {
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        File.WriteAllBytes(_paths.Failed, [1]);
        _store.Write(new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.Swapped, 1, false,
            _clock.GetUtcNow(), null, null));
        var confirming = new HelperUpdateReport(JobId.ToString(), UpdateProgress.Confirming);

        // Больше 5 отчётов, но меньше 10 минут — проверка.
        var first = Updater(NewBuild);
        Assert.Equal(confirming, first.Report());
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1.5));
            Accepted(first, Offer());
        }

        Assert.Equal(confirming, first.Report());

        // Перезапуск службы: счёт заново. 10 минут, но 4 отчёта — проверка; пятый — подтверждено.
        var updater = Updater(NewBuild);
        for (var i = 0; i < 4; i++)
        {
            Accepted(updater, Offer());
        }

        _clock.Advance(TimeSpan.FromMinutes(10));
        NotAccepted(updater, Offer());
        Assert.Equal(confirming, updater.Report());
        Assert.True(File.Exists(_paths.Old));

        Accepted(updater, Offer());
        Assert.Equal(new HelperUpdateReport(JobId.ToString(), UpdateProgress.Confirmed), updater.Report());

        // Отчёт с confirmed сервер ещё не принял: update.json, .old и .failed на месте (перезапуск сейчас — снова проверка).
        NotAccepted(updater, Offer());
        Assert.Equal(UpdateProgress.Confirmed, updater.Report()?.State);
        Assert.True(File.Exists(_paths.Old));
        Assert.True(File.Exists(_paths.Failed));
        Assert.Equal(UpdatePhases.Swapped, Stored().Phase);

        // Принят — стёрты; confirmed — пока принятый ответ несёт поручение; без него — ничего.
        Accepted(updater, Offer());
        Assert.Equal(UpdateProgress.Confirmed, updater.Report()?.State);
        Assert.False(File.Exists(_paths.Old));
        Assert.False(File.Exists(_paths.Failed));
        Assert.Null(_store.Read(out var unreadable));
        Assert.False(unreadable);
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        NotAccepted(updater, null);
        Assert.Equal(UpdateProgress.Confirmed, updater.Report()?.State);
        Accepted(updater, null);
        Assert.Null(updater.Report());
        Assert.Equal(0, _service.Terminations);
    }

    [Fact]
    public void Confirm_PowerCutBeforeConfirmedAccepted_StateKept_ConfirmedAfterRestart()
    {
        // Подтверждено на ПК (10 минут, 5 принятых отчётов), но сервер недоступен, а потом пропало питание — отчёт с
        // confirmed так и не принят. Стёртый при подтверждении update.json оставил бы сервер без confirmed: поручение —
        // noReturn и пауза очереди, хотя сборка на месте. update.json и .old живут, пока отчёт с confirmed не принят.
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        _store.Write(new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.Swapped, 1, false,
            _clock.GetUtcNow(), null, null));
        var confirming = new HelperUpdateReport(JobId.ToString(), UpdateProgress.Confirming);
        var confirmed = new HelperUpdateReport(JobId.ToString(), UpdateProgress.Confirmed);

        var first = Updater(NewBuild);
        for (var i = 0; i < SelfUpdater.ConfirmReports; i++)
        {
            Accepted(first, Offer());
        }

        _clock.Advance(SelfUpdater.ConfirmAfter);
        NotAccepted(first, Offer());
        Assert.Equal(confirmed, first.Report());
        Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
        Assert.Equal(UpdatePhases.Swapped, Stored().Phase);

        // Питание пропало (не «чистый выход»): та же новая сборка, попытка 2 — снова проверка, а не «готово» без сервера.
        Assert.Equal(GuardOutcome.RunConfirming, UpdateGuard.Run(_paths, _store, Sha(NewBuild), ShaOf, _clock));
        Assert.Equal(2, Stored().Attempts);
        var second = Updater(NewBuild);
        Assert.Equal(confirming, second.Report());
        for (var i = 0; i < SelfUpdater.ConfirmReports - 1; i++)
        {
            Accepted(second, Offer());
        }

        _clock.Advance(SelfUpdater.ConfirmAfter);
        Accepted(second, Offer());
        Assert.Equal(confirmed, second.Report());
        Assert.True(File.Exists(_paths.Old));

        // Сервер принял confirmed и закрыл поручение (ответ без него): теперь update.json и .old стёрты.
        Accepted(second, null);
        Assert.Null(second.Report());
        Assert.False(File.Exists(_paths.Old));
        Assert.Null(_store.Read(out var unreadable));
        Assert.False(unreadable);
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Equal(0, _service.Terminations);
    }

    [Fact]
    public void Confirm_ServerDownCleanReboots_NoAttempts_ConfirmedAfterServerReturns()
    {
        // Новая сборка на проверке, сервер пропал на час, ПК трижды перезагрузили (служба остановлена командой Windows):
        // ни попыток, ни отката, .old на месте; подтверждение — когда сервер вернётся, стирание — по принятому confirmed.
        File.WriteAllBytes(_paths.Exe, NewBuild);
        File.WriteAllBytes(_paths.Old, OldBuild);
        _store.Write(new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.Swapped, 0, false,
            _clock.GetUtcNow(), null, null));
        Assert.Equal(GuardOutcome.RunConfirming, UpdateGuard.Run(_paths, _store, Sha(NewBuild), ShaOf, _clock));

        var running = Updater(NewBuild);
        for (var i = 0; i < SelfUpdater.ConfirmReports; i++)
        {
            Accepted(running, Offer());
        }

        // Сервер пропал, 10 минут работы прошли без него: confirmed ждёт сервера, update.json и .old на месте.
        _clock.Advance(SelfUpdater.ConfirmAfter);
        NotAccepted(running, Offer());
        Assert.Equal(UpdateProgress.Confirmed, running.Report()?.State);

        for (var reboot = 0; reboot < 3; reboot++)
        {
            running.MarkCleanExit();
            Assert.Equal(GuardOutcome.RunConfirming, UpdateGuard.Run(_paths, _store, Sha(NewBuild), ShaOf, _clock));
            Assert.Equal(1, Stored().Attempts);
            Assert.Equal(OldBuild, File.ReadAllBytes(_paths.Old));
            running = Updater(NewBuild);
            _clock.Advance(TimeSpan.FromMinutes(20));
            NotAccepted(running, null);
            Assert.Equal(UpdateProgress.Confirming, running.Report()?.State); // принятых отчётов этого процесса нет
        }

        // Сервер вернулся: 5 принятых отчётов этого процесса — confirmed; принят, поручение закрыто — всё стёрто.
        for (var i = 0; i < SelfUpdater.ConfirmReports; i++)
        {
            Accepted(running, Offer());
        }

        Assert.Equal(UpdateProgress.Confirmed, running.Report()?.State);
        Accepted(running, null);
        Assert.Null(running.Report());
        Assert.False(File.Exists(_paths.Old));
        Assert.Null(_store.Read(out _));
        Assert.Equal(NewBuild, File.ReadAllBytes(_paths.Exe));
        Assert.Empty(_store.Failed());
        Assert.Equal(0, _service.Terminations);
    }

    [Fact]
    public void FailedState_ReportedUntilOfferGone_ThenStateDeleted()
    {
        // Страж откатился: прежняя сборка, rolledBack/startFailed с текстом исключения.
        _store.Write(new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.RolledBack, 4, false,
            _clock.GetUtcNow(), UpdateErrors.StartFailed, "InvalidOperationException: boom"));
        var updater = Updater();
        var failed = new HelperUpdateReport(JobId.ToString(), UpdateProgress.Failed, Error: UpdateErrors.StartFailed, Message: "InvalidOperationException: boom");

        Assert.Equal(failed, updater.Report());
        NotAccepted(updater, null);
        Assert.Equal(failed, updater.Report());
        Accepted(updater, Offer());
        Assert.Equal(failed, updater.Report());
        Assert.NotNull(_store.Read(out _));

        Accepted(updater, null);
        Assert.Null(updater.Report());
        Assert.Null(_store.Read(out _));
    }

    [Fact]
    public void MarkCleanExit_OnlyForUnconfirmedNewBuild()
    {
        var state = new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.Swapped, 1, false,
            _clock.GetUtcNow(), null, null);
        _store.Write(state);

        Updater().MarkCleanExit(); // прежняя сборка — не её запуск
        Assert.False(Stored().CleanExit);

        Updater(NewBuild).MarkCleanExit();
        Assert.True(Stored().CleanExit);

        _store.Delete();
        Updater(NewBuild).MarkCleanExit(); // нечего отмечать — и не падает
        Assert.Null(_store.Read(out _));
    }

    [Fact]
    public void Report_MessageClippedTo500()
    {
        _store.Write(new UpdateState(UpdateState.CurrentVersion, JobId, Sha(OldBuild), Sha(NewBuild), "1.8.1", UpdatePhases.RolledBack, 4, false,
            _clock.GetUtcNow(), UpdateErrors.StartFailed, new string('m', 2000)));

        Assert.Equal(SelfUpdater.MessageLength, Updater().Report()?.Message?.Length);
    }

    /// <summary>Сервер клуба: файлы сборок (<c>/pxe/…</c>), выдача сохранений с пустым списком папок, остальное — 204.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Downloads { get; } = [];

        /// <summary>Задан — загрузка ждёт его (после <see cref="DownloadStarted"/>), а не отдаёт файл сразу.</summary>
        public TaskCompletionSource? DownloadGate { get; set; }

        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Задан — выдача сохранений ждёт его (после <see cref="AttachStarted"/>): идёт команда шелла.</summary>
        public TaskCompletionSource? AttachGate { get; set; }

        public TaskCompletionSource AttachStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/pxe/", StringComparison.Ordinal))
            {
                lock (Downloads)
                {
                    Downloads.Add(path);
                }

                if (DownloadGate is { } gate)
                {
                    DownloadStarted.TrySetResult();
                    await gate.Task.WaitAsync(cancellationToken);
                }

                return Files.TryGetValue(path, out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/attach", StringComparison.Ordinal))
            {
                AttachStarted.TrySetResult();
                if (AttachGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }

                var response = new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
                response.Headers.Add(DisklessApiClient.SavesConfigHeader,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"enabled":true,"limitMb":100,"excludes":[],"entries":[]}""")));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
        }
    }

    /// <summary>Журнал, который бросает исключение на строке с <paramref name="fragment"/> (отказ журнала событий).</summary>
    private sealed class FailingLogger(string fragment) : ILogger<SelfUpdater>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(fragment, StringComparison.Ordinal))
            {
                throw new AggregateException(new InvalidOperationException("The event log is full"));
            }
        }
    }

    /// <summary>Служба Windows: действия при сбое (<see cref="Recovery"/>) и счёт завершений процесса.</summary>
    private sealed class FakeService : IServiceControl
    {
        public bool Recovery { get; set; } = true;

        public int Terminations { get; private set; }

        /// <summary>Что проверить в момент завершения процесса (замки ещё держатся).</summary>
        public Action? OnTerminate { get; set; }

        public bool RecoveryConfigured() => Recovery;

        public void EnsureRecovery()
        {
        }

        public void TerminateSelf()
        {
            Terminations++;
            OnTerminate?.Invoke();
        }
    }
}
