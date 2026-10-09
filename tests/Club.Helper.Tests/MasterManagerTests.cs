using Club.Helper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

/// <summary>Мастер-том на ПК суперклиента: подключается на запись только по назначению сервера и отключается чисто.</summary>
public sealed class MasterManagerTests
{
    private const string MasterIqn = "iqn.2005-10.org.freenas.ctl:club-master";
    private const string V1 = "iqn.2005-10.org.freenas.ctl:games-v1";

    private readonly FakeWindowsStorage _storage = new();
    private readonly MasterManager _masters;
    private readonly VolumeManager _volumes;

    public MasterManagerTests()
    {
        var options = new HelperOptions { DiskWaitSec = 1 };
        _masters = new MasterManager(_storage, options, TimeProvider.System, NullLogger<MasterManager>.Instance);
        _volumes = new VolumeManager(_storage, new FakeProcesses(), options, TimeProvider.System, NullLogger<VolumeManager>.Instance);
    }

    private static MasterAssignment Assign(string secret = "S3cretS3cretS3cr") =>
        new(MasterIqn, "192.168.77.10:3260", "clubsrv-master", secret, "M");

    [Fact]
    public async Task Assigned_master_is_mounted_writable_with_chap()
    {
        await _storage.EnsureSanPolicyOfflineSharedAsync(CancellationToken.None);
        _storage.ExpectedChap = ("clubsrv-master", "S3cretS3cretS3cr");

        var report = await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);

        Assert.Equal(new MasterReport("mounted", MasterIqn, "M"), report);
        Assert.Equal([(MasterIqn, "clubsrv-master", "S3cretS3cretS3cr")], _storage.ChapLogins);
        Assert.True(_storage.Sessions[MasterIqn] is { ReadOnly: false, Offline: false, Letter: 'M' });

        // Повтор ничего не меняет и не переподключает.
        await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        Assert.Single(_storage.ChapLogins);
    }

    [Fact]
    public async Task Wrong_chap_secret_is_reported_as_failure()
    {
        _storage.ExpectedChap = ("clubsrv-master", "OtherSecret12345");
        var report = await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        Assert.Equal("failed", report!.State);
        Assert.Contains("CHAP", report.Error);
        Assert.Empty(_storage.Sessions);
    }

    [Fact]
    public async Task Unassigned_master_is_flushed_taken_offline_then_disconnected()
    {
        await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        var disk = _storage.Sessions[MasterIqn].Number;

        var report = await _masters.ApplyAsync(null, known: true, CancellationToken.None);

        Assert.Equal("none", report!.State);
        var flush = _storage.Log.IndexOf($"flush+offline {disk} M");
        Assert.True(flush >= 0 && flush < _storage.Log.IndexOf($"disconnect {MasterIqn}"), string.Join(" | ", _storage.Log));
        Assert.Empty(_storage.Sessions);
        Assert.Null(await _masters.ApplyAsync(null, known: true, CancellationToken.None)); // больше не о чем сообщать
    }

    [Fact]
    public async Task Busy_volume_stays_connected_and_the_server_keeps_waiting()
    {
        await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        _storage.FailOffline = true; // на M: открыты файлы

        var report = await _masters.ApplyAsync(null, known: true, CancellationToken.None);

        Assert.Equal("failed", report!.State);
        Assert.True(_storage.Sessions.ContainsKey(MasterIqn)); // не отключили без сброса кэша
        Assert.DoesNotContain($"disconnect {MasterIqn}", _storage.Log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_sessions_without_an_assignment_are_reported_as_unknown_not_failed(bool known)
    {
        // Подключён ли мастер-том, неизвестно: failed без таргета — ложная ошибка у любого ПК, отчёт без master — ложное
        // «не подключён». unknown сервер пропускает и прежнее состояние не меняет.
        _storage.ConnectedTargetsError = new TimeoutException("PowerShell did not finish in 120 s");
        Assert.Equal(
            new MasterReport(MasterManager.UnknownState, Error: "PowerShell did not finish in 120 s"),
            await _masters.ApplyAsync(null, known, CancellationToken.None));

        // Назначен — как раньше: failed с назначенным таргетом.
        Assert.Equal(
            new MasterReport("failed", MasterIqn, "M", "PowerShell did not finish in 120 s"),
            await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None));
    }

    [Fact]
    public async Task Without_an_answer_from_the_server_the_master_is_left_alone()
    {
        await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        var report = await _masters.ApplyAsync(null, known: false, CancellationToken.None);
        Assert.Equal("mounted", report!.State);
        Assert.True(_storage.Sessions.ContainsKey(MasterIqn));
    }

    [Fact]
    public async Task Library_switching_never_touches_the_master_volume()
    {
        await _masters.ApplyAsync(Assign(), known: true, CancellationToken.None);
        await _volumes.ApplyAsync(new VolumeAssignment("v1", "192.168.77.10:3260", V1, ReadOnly: true, "G"), CancellationToken.None);
        await _volumes.ApplyAsync(null, CancellationToken.None);

        Assert.True(_storage.Sessions.ContainsKey(MasterIqn));
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }
}
