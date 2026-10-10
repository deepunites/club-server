using Club.Helper.Core;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

public sealed class VolumeManagerTests
{
    private const string V1 = "iqn.2005-10.org.freenas.ctl:games-v1";
    private const string V2 = "iqn.2005-10.org.freenas.ctl:games-v2";

    private readonly FakeWindowsStorage _storage = new();
    private readonly FakeProcesses _processes = new();
    private readonly ListLogger<VolumeManager> _log = new();
    private readonly VolumeManager _volumes;

    public VolumeManagerTests() =>
        _volumes = new VolumeManager(_storage, _processes, new HelperOptions { DiskWaitSec = 1 }, TimeProvider.System, _log);

    private static VolumeAssignment Assign(string iqn, string version) => new(version, "192.168.77.10:3260", iqn, ReadOnly: true, "G");

    [Fact]
    public async Task Mount_is_fail_closed_san_then_readonly_then_online_then_letter()
    {
        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);

        Assert.Equal("mounted", report.State);
        Assert.True(report.ReadOnlyVerified);
        Assert.Equal(["san", $"connect {V1} 192.168.77.10:3260", "ro 2", "online 2", "letter 2 G"], _storage.Log);
        Assert.False(_storage.EverWritableOnline);
        Assert.False(_volumes.WindowsTimedOut);
        var disk = _storage.Sessions[V1];
        Assert.True(disk is { ReadOnly: true, Offline: false, Letter: 'G' });
    }

    [Fact]
    public async Task Disk_that_refuses_read_only_is_never_brought_online()
    {
        _storage.RefuseReadOnly = true;
        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);

        Assert.Equal("failed", report.State);
        Assert.False(report.ReadOnlyVerified);
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith("online", StringComparison.Ordinal));
        Assert.Contains($"disconnect {V1}", _storage.Log);
        Assert.Empty(_storage.Sessions);
        Assert.False(_storage.EverWritableOnline);
    }

    [Fact]
    public async Task Repeated_apply_changes_nothing()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.Log.Clear();

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("mounted", report.State);
        Assert.Empty(_storage.Log);
    }

    [Fact]
    public async Task New_version_waits_while_a_game_runs_from_the_volume()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _processes.Running['G'] = [@"G:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"];

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(("switchPending", V1, "v1"), (waiting.State, waiting.TargetIqn, waiting.LibraryVersion)); // ПК пока на старой версии
        Assert.Contains("cs2.exe", waiting.Error);
        Assert.True(_storage.Sessions.ContainsKey(V1));
        Assert.False(_storage.Sessions.ContainsKey(V2));

        _processes.Running.Clear(); // игру закрыли
        var switched = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("mounted", switched.State);
        Assert.Equal("v2", switched.LibraryVersion);
        Assert.False(_storage.Sessions.ContainsKey(V1));
        Assert.Equal('G', _storage.Sessions[V2].Letter);
        Assert.False(_storage.EverWritableOnline);
    }

    [Fact]
    public async Task No_assignment_releases_the_volume_when_idle()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _processes.Running['G'] = [@"G:\game.exe"];
        Assert.Equal("switchPending", (await _volumes.ApplyAsync(null, CancellationToken.None)).State);

        _processes.Running.Clear();
        Assert.Equal("none", (await _volumes.ApplyAsync(null, CancellationToken.None)).State);
        Assert.Empty(_storage.Sessions);
    }

    /// <summary>Русская Windows отказывает своим текстом (дословный перевод не важен — помощник по тексту не судит).</summary>
    private const string SessionBusyRu = "Невозможно завершить сеанс: устройство этого сеанса сейчас используется.";

    [Theory]
    [InlineData(FakeWindowsStorage.SessionBusy)]
    [InlineData(SessionBusyRu)]
    public async Task Old_version_held_by_open_files_waits_and_switches_once_released(string refusal)
    {
        // Стенд 2026-10-02: на G: открыт проводник (или Steam с C:) — процесса с тома нет, но Windows сессию не завершает.
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.BusySessions[V1] = refusal;

        for (var tick = 0; tick < 3; tick++)
        {
            var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
            Assert.Equal(new MountedVolume("switchPending", V1, "v1", "G", Error: $"new version v2 waits: volume in use (open files on G:): {refusal}"), waiting);
        }

        Assert.Equal(3, _storage.Log.Count(l => l == $"disconnect {V1}")); // повтор каждый такт, без насильного отключения
        Assert.True(_storage.Sessions[V1] is { ReadOnly: true, Offline: false, Letter: 'G' });
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith($"connect {V2}", StringComparison.Ordinal));

        _storage.BusySessions.Clear(); // проводник закрыли
        var switched = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(("mounted", "v2"), (switched.State, switched.LibraryVersion));
        Assert.False(_storage.Sessions.ContainsKey(V1));
        Assert.Equal('G', _storage.Sessions[V2].Letter);
        Assert.False(_storage.EverWritableOnline);
    }

    [Fact]
    public async Task No_assignment_with_open_files_keeps_the_volume_until_released()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.BusySessions[V1] = FakeWindowsStorage.SessionBusy;

        var waiting = await _volumes.ApplyAsync(null, CancellationToken.None);
        Assert.Equal(new MountedVolume("switchPending", V1, "v1", Error: $"volume in use (open files on G:): {FakeWindowsStorage.SessionBusy}"), waiting);
        Assert.True(_storage.Sessions.ContainsKey(V1));

        _storage.BusySessions.Clear();
        Assert.Equal("none", (await _volumes.ApplyAsync(null, CancellationToken.None)).State);
        Assert.Empty(_storage.Sessions);
    }

    [Fact]
    public async Task Refused_disconnect_is_logged_once_per_reason_not_every_tick()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.BusySessions[V1] = FakeWindowsStorage.SessionBusy;
        for (var tick = 0; tick < 3; tick++)
        {
            await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        }

        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("open files on G:", warning.Message);

        // Процесса с G: не нашлось, а Windows не отпускает: в ту же запись — что вернул поиск диска и что видит служба
        // (один проход на причину).
        Assert.Contains("Disk of the target: number 2, letter G:, offline False, read-only True; no process from G: found", warning.Message);
        Assert.Equal(['G'], _processes.Diagnoses);
        Assert.Contains(@"process scan of G: (volume letter): drive G: = \Device\HarddiskVolume12", warning.Message);
        Assert.Contains(@"C:\Program Files (x86)\Steam\steam.exe", warning.Message);

        // Причина сменилась (с тома запустили игру) — новая запись; освободился — запись об отключении.
        _processes.Running['G'] = [@"G:\Games\Dota 2\game\bin\win64\dota2.exe"];
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Single(_log.Entries, e => e.Message.Contains("dota2.exe", StringComparison.Ordinal));

        _processes.Running.Clear();
        _storage.BusySessions.Clear();
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains($"{V1} disconnected", StringComparison.Ordinal));
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(_processes.Diagnoses); // процесс с тома нашёлся — без прохода
    }

    public static TheoryData<Exception> Timeouts => new()
    {
        new TimeoutException("PowerShell did not finish in 120 s"),
        new OperationCanceledException(), // не по токену службы
    };

    [Theory]
    [MemberData(nameof(Timeouts))]
    public async Task Disconnect_timeout_is_not_reported_as_open_files(Exception timeout)
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.FailingDisconnects[V1] = timeout;

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(new MountedVolume("switchPending", V1, "v1", "G", Error: "new version v2 waits: old version not disconnected: timeout"), waiting);
        Assert.True(_storage.Sessions.ContainsKey(V1));
        Assert.True(_volumes.WindowsTimedOut); // мастер-том в этом такте помощник не опрашивает
        Assert.Empty(_processes.Diagnoses); // это не отказ Windows — проход по процессам ни к чему
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("open files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_refusal_is_shown_by_its_first_line_and_other_failures_by_their_message()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.BusySessions[V1] = "Сеанс не может быть завершён.\r\nCategoryInfo: NotSpecified: (MSFT_iSCSITarget) [Disconnect-IscsiTarget]";

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("new version v2 waits: volume in use (open files on G:): Сеанс не может быть завершён.", waiting.Error);

        _storage.BusySessions.Clear();
        _storage.FailingDisconnects[V1] = new UnauthorizedAccessException("Access is denied");
        waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("new version v2 waits: old version not disconnected: Access is denied", waiting.Error);
        Assert.Single(_processes.Diagnoses); // только при отказе Windows
    }

    [Theory]
    [InlineData("iqn.2005-10.org.freenas.ctl:games-v1", "v1")]
    [InlineData("iqn.2005-10.org.freenas.ctl:games-2026-10-02-4", "2026-10-02-4")]
    [InlineData("IQN.2005-10.ORG.FREENAS.CTL:GAMES-V1", "v1")]
    [InlineData("iqn.2005-10.org.freenas.ctl:club-master", null)]
    [InlineData("iqn.2005-10.org.freenas.ctl:games-", null)]
    [InlineData("iqn.2005-10.org.freenas.ctl:games--v1", null)]
    [InlineData("iqn.2005-10.org.freenas.ctl:games-v1_x", null)]
    public void Library_version_is_read_from_the_target_name(string iqn, string? version) =>
        Assert.Equal(version, VolumeManager.VersionOfTarget(iqn));

    [Fact]
    public async Task Failure_report_after_a_timeout_does_not_ask_windows_again()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None); // переключился: V1 отключён, V2 подключён
        _storage.ConnectedTargetsError = new TimeoutException("PowerShell did not finish in 120 s");

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None));
        var calls = _storage.ConnectedTargetsCalls;
        Assert.Equal(
            new MountedVolume("failed", V2, ReadOnlyVerified: false, Error: "library v1: PowerShell did not finish in 120 s"),
            await _volumes.FailedAsync(Assign(V1, "v1"), timeout, CancellationToken.None));
        Assert.Equal(
            new MountedVolume("failed", V2, "v2", ReadOnlyVerified: false, Error: "library v2: PowerShell did not finish in 120 s"),
            await _volumes.FailedAsync(Assign(V2, "v2"), timeout, CancellationToken.None));
        Assert.Equal(calls, _storage.ConnectedTargetsCalls); // ещё 2×120 с не ждали

        // Иной сбой — сессии опрашиваются заново; не вышло — последний известный таргет.
        var denied = new UnauthorizedAccessException("Access is denied");
        Assert.Equal(V2, (await _volumes.FailedAsync(Assign(V2, "v2"), denied, CancellationToken.None)).TargetIqn);
        Assert.Equal(calls + 1, _storage.ConnectedTargetsCalls);
    }

    [Fact]
    public async Task Failure_to_check_a_stale_old_version_does_not_skip_verifying_the_current_one()
    {
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        _storage.AddForeignSession(V1); // старая версия всё ещё подключена (E:)
        _processes.Error = new UnauthorizedAccessException("Access is denied"); // опрос процессов на E: падает

        for (var tick = 0; tick < 3; tick++)
        {
            _storage.MakeWritable(V2); // текущую кто-то сбил — проверка обязана её починить
            var report = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
            Assert.Equal(("mounted", "v2", (bool?)true), (report.State, report.LibraryVersion, report.ReadOnlyVerified));
            Assert.True(_storage.Sessions[V2].ReadOnly);
            Assert.False(_volumes.WindowsTimedOut);
        }

        Assert.True(_storage.Sessions.ContainsKey(V1)); // не проверенную старую не трогаем
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("old version not checked: Access is denied", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Timeouts))]
    public async Task Timeout_on_a_stale_old_version_skips_verifying_the_current_one_and_keeps_it(Exception timeout)
    {
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        _storage.AddForeignSession(V1); // старая версия всё ещё подключена (E:)
        _storage.FindDiskErrors[V1] = timeout; // PowerShell не ответил за 120 с на поиск её диска
        _storage.DiskLookups.Clear();

        // Проверка текущей ждала бы ещё 120 с, а её сбой отключил бы том из-под игры: в этом такте её нет.
        var report = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(new MountedVolume("failed", V2, "v2", "G", ReadOnlyVerified: false, Error: "not checked: timeout on the old version"), report);
        Assert.Equal([V1], _storage.DiskLookups);
        Assert.True(_volumes.WindowsTimedOut);
        Assert.True(_storage.Sessions[V2] is { ReadOnly: true, Offline: false, Letter: 'G' });
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith("disconnect", StringComparison.Ordinal));
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("old version not checked: timeout", StringComparison.Ordinal));

        // Windows снова отвечает: старая отключается, текущая проверена.
        _storage.FindDiskErrors.Clear();
        report = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(("mounted", "v2", (bool?)true), (report.State, report.LibraryVersion, report.ReadOnlyVerified));
        Assert.False(_volumes.WindowsTimedOut);
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }

    [Theory]
    [MemberData(nameof(Timeouts))]
    public async Task Timeout_while_checking_the_current_version_keeps_it_connected(Exception timeout)
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.FindDiskErrors[V1] = timeout;

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal(new MountedVolume("failed", V1, "v1", "G", ReadOnlyVerified: false, Error: "not checked: timeout"), report);
        Assert.True(_volumes.WindowsTimedOut);
        Assert.DoesNotContain($"disconnect {V1}", _storage.Log); // медленная Windows — не повод выдёргивать том из-под игры
        Assert.True(_storage.Sessions[V1] is { ReadOnly: true, Offline: false, Letter: 'G' });

        // Иной сбой проверки — как раньше: том нездоров, отключается.
        _storage.FindDiskErrors[V1] = new UnauthorizedAccessException("Access is denied");
        report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal(("failed", "Access is denied"), (report.State, report.Error));
        Assert.False(_volumes.WindowsTimedOut);
        Assert.Contains($"disconnect {V1}", _storage.Log);
    }

    [Fact]
    public async Task Timeout_while_repairing_a_writable_volume_still_disconnects_it()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.MakeWritable(V1); // кто-то снял read-only — том online и на запись
        _storage.SetReadOnlyError = new TimeoutException("PowerShell did not finish in 120 s");

        // Починить не успели: fail-closed важнее, чем не трогать том при медленной Windows.
        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("failed", report.State);
        Assert.True(_volumes.WindowsTimedOut);
        Assert.Contains($"disconnect {V1}", _storage.Log);
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }

    [Fact]
    public async Task Failed_disconnect_of_an_old_version_without_a_volume_also_waits()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.Sessions[V1].Letter = null;
        _storage.Sessions[V1].Offline = true;
        _storage.BusySessions[V1] = "The device is not ready.";

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("switchPending", waiting.State);
        Assert.Contains("old version not disconnected", waiting.Error);
        Assert.Contains("The device is not ready.", waiting.Error); // причину видно в панели
        Assert.False(_storage.Sessions.ContainsKey(V2));

        // Буквы у диска нет — процессы перед отключением не проверялись; проход для журнала — по букве, на которой
        // помощник этот том смонтировал.
        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Disk of the target: number 2, letter none, offline True, read-only True; processes not checked (no drive letter)", warning.Message);
        Assert.Contains("process scan of G: (letter this helper last saw the target at)", warning.Message);
        Assert.Equal(['G'], _processes.Diagnoses);
    }

    [Fact]
    public async Task Refused_disconnect_of_a_target_whose_disk_the_service_does_not_see_is_still_diagnosed()
    {
        // Стенд 2026-10-02, 1.4.1: не исключено, что поиск диска в службе вернул пустоту — тогда процессы с G: не
        // проверялись вовсе. В записи — что вернул поиск, а проход по процессам — по известной букве.
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.HiddenDisks.Add(V1);
        _storage.BusySessions[V1] = FakeWindowsStorage.SessionBusy;

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(new MountedVolume("switchPending", V1, "v1", "G", Error: $"new version v2 waits: old version not disconnected: {FakeWindowsStorage.SessionBusy}"), waiting);
        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Disk of the target: not found; processes not checked (no drive letter)", warning.Message);
        Assert.Contains(@"process scan of G: (letter this helper last saw the target at): drive G: = \Device\HarddiskVolume12", warning.Message);
        Assert.Equal(['G'], _processes.Diagnoses);

        // Служба перезапущена — буквы таргета помощник не видел: проход по назначенной букве.
        var log = new ListLogger<VolumeManager>();
        var restarted = new VolumeManager(_storage, _processes, new HelperOptions { DiskWaitSec = 1 }, TimeProvider.System, log);
        await restarted.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Contains("process scan of G: (assigned letter)", Assert.Single(log.Entries, e => e.Level == LogLevel.Warning).Message);
        Assert.Equal(['G', 'G'], _processes.Diagnoses);

        // Назначения нет и буква неизвестна — проход пропущен, но поиск диска в записи есть.
        log = new ListLogger<VolumeManager>();
        restarted = new VolumeManager(_storage, _processes, new HelperOptions { DiskWaitSec = 1 }, TimeProvider.System, log);
        Assert.Equal("switchPending", (await restarted.ApplyAsync(null, CancellationToken.None)).State);
        var skipped = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains("Disk of the target: not found", skipped);
        Assert.Contains("process scan skipped: no drive letter known for the target", skipped);
        Assert.Equal(2, _processes.Diagnoses.Count);
        Assert.True(_storage.Sessions.ContainsKey(V1));
    }

    [Fact]
    public async Task Stuck_old_version_does_not_disturb_the_mounted_current_one()
    {
        await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        _storage.AddForeignSession(V1); // старая версия всё ещё подключена (E:) и занята
        _storage.BusySessions[V1] = FakeWindowsStorage.SessionBusy;

        var report = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal(("mounted", "v2"), (report.State, report.LibraryVersion));
        Assert.True(_storage.Sessions.ContainsKey(V1));
    }

    [Fact]
    public async Task Failure_report_names_the_connected_library_target()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        var error = new UnauthorizedAccessException("Access is denied");

        Assert.Equal(
            new MountedVolume("failed", V1, "v1", ReadOnlyVerified: false, Error: "library v1: Access is denied"),
            await _volumes.FailedAsync(Assign(V1, "v1"), error, CancellationToken.None));
        Assert.Equal(
            new MountedVolume("failed", V1, ReadOnlyVerified: false, Error: "library v2: Access is denied"),
            await _volumes.FailedAsync(Assign(V2, "v2"), error, CancellationToken.None));

        await _volumes.ApplyAsync(null, CancellationToken.None);
        Assert.Equal(new MountedVolume("failed", ReadOnlyVerified: false, Error: "Access is denied"), await _volumes.FailedAsync(null, error, CancellationToken.None));
    }

    [Fact]
    public async Task Drift_to_writable_is_repaired_back_to_read_only()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.MakeWritable(V1);

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("mounted", report.State);
        Assert.True(_storage.Sessions[V1].ReadOnly);
    }

    [Fact]
    public async Task Foreign_iscsi_sessions_are_left_alone()
    {
        _storage.AddForeignSession("iqn.2000-01.com.vendor:backup-disk");
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        await _volumes.ApplyAsync(null, CancellationToken.None);
        Assert.True(_storage.Sessions.ContainsKey("iqn.2000-01.com.vendor:backup-disk"));
    }

    [Fact]
    public async Task Writable_assignment_is_refused()
    {
        var report = await _volumes.ApplyAsync(Assign(V1, "v1") with { ReadOnly = false }, CancellationToken.None);
        Assert.Equal("failed", report.State);
        Assert.Empty(_storage.Log);
    }

    [Fact]
    public async Task Letter_taken_by_another_volume_fails_and_disconnects()
    {
        _storage.AddForeignSession("iqn.2000-01.com.vendor:other");
        var foreign = _storage.Sessions["iqn.2000-01.com.vendor:other"];
        foreign.Letter = 'G';

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("failed", report.State);
        Assert.Contains("in use", report.Error);
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }

    private const string Seat = "iqn.2005-10.org.freenas.ctl:games-seat-07";

    private static VolumeAssignment Personal() => new("v1", "192.168.77.10:3260", Seat, ReadOnly: false, "G", "games-seat-07", "Secret0123456789");

    [Fact]
    public async Task Personal_disk_is_mounted_writable_with_chap()
    {
        var report = await _volumes.ApplyAsync(Personal(), CancellationToken.None);

        Assert.Equal(("mounted", (bool?)null), (report.State, report.ReadOnlyVerified));
        Assert.Equal([(Seat, "games-seat-07", "Secret0123456789")], _storage.ChapLogins);
        Assert.Equal(["san", $"connect {Seat} 192.168.77.10:3260", "online 2", "letter 2 G"], _storage.Log);
        Assert.True(_storage.Sessions[Seat] is { ReadOnly: false, Offline: false, Letter: 'G' });

        _storage.Log.Clear();
        Assert.Equal("mounted", (await _volumes.ApplyAsync(Personal(), CancellationToken.None)).State);
        Assert.Empty(_storage.Log); // проверка не делает его read-only и ничего не трогает
        Assert.True(await _volumes.PersonalAttachedAsync(CancellationToken.None));
        Assert.Null(VolumeManager.VersionOfTarget(Seat));
    }

    [Fact]
    public async Task Shared_version_gives_way_to_the_personal_disk_when_free()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _processes.Running['G'] = [@"G:\Steam\steam.exe"];

        var report = await _volumes.ApplyAsync(Personal(), CancellationToken.None);
        Assert.Equal(("switchPending", "v1"), (report.State, report.LibraryVersion));
        Assert.False(_storage.Sessions.ContainsKey(Seat));

        _processes.Running.Clear();
        report = await _volumes.ApplyAsync(Personal(), CancellationToken.None);
        Assert.Equal("mounted", report.State);
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }

    [Fact]
    public async Task Writable_assignment_without_chap_or_for_another_target_is_refused()
    {
        Assert.Equal("failed", (await _volumes.ApplyAsync(Personal() with { ChapSecret = null }, CancellationToken.None)).State);
        Assert.Equal("failed", (await _volumes.ApplyAsync(Assign(V1, "v1") with { ReadOnly = false, ChapUser = "u", ChapSecret = "s" }, CancellationToken.None)).State);
        Assert.Empty(_storage.Log);
        Assert.False(await _volumes.PersonalAttachedAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("192.168.77.10:3260", "192.168.77.10", 3260)]
    [InlineData("nas.club.lan:3261", "nas.club.lan", 3261)]
    [InlineData("192.168.77.10", "192.168.77.10", 3260)]
    public void Portal_is_parsed(string portal, string host, int port) =>
        Assert.Equal((host, port), VolumeManager.ParsePortal(portal));
}
