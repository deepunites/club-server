using Club.Helper.Core;
using Club.Server.Library;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Helper.Tests;

/// <summary>
/// Мастер-том на букве библиотеки (Library:MasterDriveLetter = DriveLetter = G, помощник 1.5.1): лаунчеры на ПК правки
/// записывают пути G:\… так, как их видят места. На ПК правки мастер-том занимает G: вместо личного диска: сначала личный
/// диск отпускается (кэш, offline, отключение только его таргета), потом подключается мастер-том; пока правка открыта,
/// личный диск не возвращается ни из ответа сервера, ни после 3 минут без сервера; правку закрыли — он снова на G:.
/// </summary>
public sealed partial class PersonalGamesEndToEndTests
{
    private const string MasterTarget = $"{Basename}:club-master";

    private LibraryOptions Options => _server.Services.GetRequiredService<LibraryOptions>();

    /// <summary>ПК загрузился: личный диск на G:, мастер-том будет на <paramref name="masterLetter"/>.</summary>
    private async Task<(HelperLoop Helper, FakeWindowsStorage Windows)> PersonalDiskMountedAsync(string masterLetter = "G", TimeProvider? clock = null)
    {
        Options.MasterDriveLetter = masterLetter;
        await PublishAsync("v1");
        var windows = new FakeWindowsStorage();
        var helper = Helper(windows, clock: clock);
        await helper.TickAsync(CancellationToken.None);
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        return (helper, windows);
    }

    private async Task OpenMasterAsync()
    {
        await _server.Services.GetRequiredService<MasterEditor>().RequestOpenAsync((await MachineAsync()).Id, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        Assert.Equal("open", (await _server.Services.GetRequiredService<MasterRepository>().GetAsync()).State);
    }

    [Fact]
    public async Task Master_on_the_library_letter_takes_g_after_the_personal_disk_is_released()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        const string systemDisk = $"{Basename}:seat-01"; // системный диск бездиска — не таргет библиотеки
        windows.AddForeignSession(systemDisk);
        var personalDisk = windows.Sessions[SeatTarget].Number;
        var resets = _nas.Rollbacks($"{Seats}/games-seat-01");
        await OpenMasterAsync();

        await helper.TickAsync(CancellationToken.None);

        Assert.False(windows.Sessions.ContainsKey(SeatTarget));
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.Equal(new MasterReport("mounted", MasterTarget, "G"), helper.LastMasterReport);

        // Сначала личный диск: кэш на диск, offline, отключение; потом вход в мастер-том и буква G:.
        var log = windows.Log;
        var flush = log.IndexOf($"flush+offline {personalDisk} G");
        var disconnect = log.IndexOf($"disconnect {SeatTarget}", Math.Max(flush, 0));
        var connect = log.FindIndex(Math.Max(disconnect, 0), l => l.StartsWith($"connect {MasterTarget} ", StringComparison.Ordinal));
        var letter = log.IndexOf($"letter {windows.Sessions[MasterTarget].Number} G", Math.Max(connect, 0));
        Assert.True(flush >= 0 && disconnect > flush && connect > disconnect && letter > connect, string.Join(" | ", log));

        // Чужие сессии (системный диск) не тронуты.
        Assert.True(windows.Sessions.ContainsKey(systemDisk));
        Assert.DoesNotContain($"disconnect {systemDisk}", log);

        // В панели том библиотеки — не сбой, а «нет» с причиной; личный диск на сервере не сброшен и не удалён.
        var machine = await MachineAsync();
        Assert.Equal(("none", "mounted"), (machine.VolumeState, machine.MasterState));
        Assert.Equal("drive letter G: is given to the master volume (library editing)", machine.VolumeError);
        Assert.Equal(resets, _nas.Rollbacks($"{Seats}/games-seat-01"));
        Assert.NotNull(await _server.Services.GetRequiredService<TrueNasStorage>().GetDatasetAsync($"{Seats}/games-seat-01"));

        // Следующие такты: личный диск не возвращается, мастер-том остаётся на G:.
        var connects = log.Count(l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(connects, windows.Log.Count(l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal)));
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        machine = await MachineAsync();
        Assert.Equal(("none", "mounted"), (machine.VolumeState, machine.MasterState));
    }

    [Fact]
    public async Task Closing_the_master_gives_g_back_to_the_personal_disk()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        await OpenMasterAsync();
        await helper.TickAsync(CancellationToken.None);
        var masterDisk = windows.Sessions[MasterTarget].Number;
        Assert.Equal('G', windows.Sessions[MasterTarget].Letter);

        // «Закончить правку»: сессии club-master на поддельном TrueNAS нет — таргет удаляется сразу.
        await _server.Services.GetRequiredService<MasterEditor>().RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        var start = windows.Log.Count;

        await helper.TickAsync(CancellationToken.None);

        // Сначала мастер-том (кэш, offline, отключение), в том же такте — личный диск снова на G:.
        Assert.False(windows.Sessions.ContainsKey(MasterTarget));
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        var log = windows.Log;
        var flush = log.IndexOf($"flush+offline {masterDisk} G", start);
        var disconnect = log.IndexOf($"disconnect {MasterTarget}", Math.Max(flush, start));
        var connect = log.FindIndex(Math.Max(disconnect, start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.True(flush >= start && disconnect > flush && connect > disconnect, string.Join(" | ", log.Skip(start)));

        var machine = await MachineAsync();
        Assert.Equal(("mounted", "v1", (string?)null, "none"), (machine.VolumeState, machine.VolumeVersion, machine.VolumeError, machine.MasterState));
    }

    [Fact]
    public async Task Personal_disk_waits_while_files_are_open_on_the_closed_master()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        await OpenMasterAsync();
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal('G', windows.Sessions[MasterTarget].Letter);
        await _server.Services.GetRequiredService<MasterEditor>().RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        windows.FailOffline = true; // на G: мастер-тома открыт файл — Windows не переводит диск offline
        var start = windows.Log.Count;

        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        // Мастер-том насильно не отключается, а личный диск на занятую G: не лезет: ни входа в таргет, ни попыток буквы.
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.DoesNotContain(windows.Log.Skip(start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.Equal("failed", helper.LastMasterReport!.State);
        Assert.Equal(("none", "drive letter G: is given to the master volume (library editing)"), (helper.LastReport!.State, helper.LastReport.Error));
        var machine = await MachineAsync();
        Assert.Equal(("none", "failed"), (machine.VolumeState, machine.MasterState));

        windows.FailOffline = false; // файл закрыли: в том же такте мастер-том отключается, личный диск — снова на G:
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(MasterTarget));
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        machine = await MachineAsync();
        Assert.Equal(("mounted", "none"), (machine.VolumeState, machine.MasterState));
    }

    [Fact]
    public async Task Master_on_its_own_letter_leaves_the_personal_disk_alone()
    {
        var (helper, windows) = await PersonalDiskMountedAsync(masterLetter: "M");
        await OpenMasterAsync();

        await helper.TickAsync(CancellationToken.None);

        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'M' });
        Assert.DoesNotContain($"disconnect {SeatTarget}", windows.Log);
        Assert.DoesNotContain(windows.Log, l => l.StartsWith($"flush+offline {windows.Sessions[SeatTarget].Number} ", StringComparison.Ordinal));
        var machine = await MachineAsync();
        Assert.Equal(("mounted", "mounted"), (machine.VolumeState, machine.MasterState));
    }

    [Fact]
    public async Task Open_files_on_the_personal_disk_keep_it_and_the_master_waits()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        var personalDisk = windows.Sessions[SeatTarget].Number;
        await OpenMasterAsync();
        windows.FailOffline = true; // лаунчер держит файлы на G: — Windows не переводит диск offline

        await helper.TickAsync(CancellationToken.None);

        // Насильно не отключаем: личный диск на месте, мастер-том не подключался; причина — в отчётах обоих.
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.False(windows.Sessions.ContainsKey(MasterTarget));
        Assert.DoesNotContain(windows.ChapLogins, l => l.Iqn == MasterTarget);
        Assert.DoesNotContain($"disconnect {SeatTarget}", windows.Log);
        const string reason = "volume in use (open files on G:): The disk is in use";
        Assert.Equal(new MasterReport("failed", MasterTarget, "G", $"drive letter G: is held by the library volume: {reason}"), helper.LastMasterReport);
        var machine = await MachineAsync();
        Assert.Equal(("failed", $"drive letter G: is held by the library volume: {reason}"), (machine.MasterState, machine.MasterError));
        Assert.Equal(("switchPending", "v1", $"master volume waits: {reason}"), (machine.VolumeState, machine.VolumeVersion, machine.VolumeError));

        // Повтор в следующем такте.
        var attempts = windows.Log.Count(l => l == $"flush+offline {personalDisk} G");
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(attempts + 1, windows.Log.Count(l => l == $"flush+offline {personalDisk} G"));
        Assert.Equal("failed", helper.LastMasterReport!.State);
        Assert.True(windows.Sessions.ContainsKey(SeatTarget));

        windows.FailOffline = false; // лаунчер закрыли
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(SeatTarget));
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        machine = await MachineAsync();
        Assert.Equal(("none", "mounted", (string?)null), (machine.VolumeState, machine.MasterState, machine.MasterError));
    }

    [Fact]
    public async Task Cached_personal_disk_is_not_mounted_after_three_minutes_without_the_server_while_the_master_holds_g()
    {
        Options.MasterDriveLetter = "G";
        await PublishAsync("v1");
        await Helper(new FakeWindowsStorage()).TickAsync(CancellationToken.None);
        Assert.True(VolumeManager.IsPersonal(_cache.Current!));
        await OpenMasterAsync();

        // ПК правки перезагрузили; запрос назначения при старте не проходит (соединение отклонено), отчёты — проходят.
        var rebooted = new FakeWindowsStorage();
        var outage = new Outage { Volume = true };
        var clock = new ManualClock();
        var helper = Helper(rebooted, outage, clock);
        await helper.TickAsync(CancellationToken.None);
        Assert.True(rebooted.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });

        // Сервер пропал совсем, дольше 3 минут: сохранённый личный диск смонтировался бы как есть, но G: — у мастер-тома.
        outage.All = true;
        clock.Advance(HelperLoop.CachedPersonalAfter + TimeSpan.FromSeconds(1));
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        Assert.DoesNotContain(rebooted.Log, l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.True(rebooted.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.Equal(("none", "drive letter G: is given to the master volume (library editing)"), (helper.LastReport!.State, helper.LastReport.Error));
        Assert.True(VolumeManager.IsPersonal(_cache.Current!)); // assignment.json не затёрт
    }

    [Fact]
    public async Task Forced_close_with_files_open_on_g_gives_g_back_once_the_dead_master_session_is_dropped()
    {
        var clock = new ManualClock();
        var (helper, windows) = await PersonalDiskMountedAsync(clock: clock);
        await OpenMasterAsync();
        await helper.TickAsync(CancellationToken.None);
        var masterDisk = windows.Sessions[MasterTarget].Number;
        Assert.Equal('G', windows.Sessions[MasterTarget].Letter);

        // Лаунчер держит файлы на G:, «Закончить правку» ждёт — администратор отключает принудительно: TrueNAS удаляет
        // таргет и рвёт сессию. Windows восстанавливает связь (без толку), том мастер-тома остаётся на G:.
        await _server.Services.GetRequiredService<MasterEditor>().RequestCloseAsync(force: true, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        windows.RecoveringSessions.Add(MasterTarget);
        windows.BusySessions[MasterTarget] = FakeWindowsStorage.SessionBusy;
        var start = windows.Log.Count;

        // Пока связь может вернуться, сессию не отключают; личный диск на занятую G: не лезет.
        await helper.TickAsync(CancellationToken.None);
        Assert.True(windows.Sessions.ContainsKey(MasterTarget));
        Assert.DoesNotContain($"disconnect {MasterTarget}", windows.Log.Skip(start));
        Assert.DoesNotContain(windows.Log.Skip(start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.Equal(("failed", MasterTarget), (helper.LastMasterReport!.State, helper.LastMasterReport.TargetIqn));
        Assert.Contains("reconnecting", helper.LastMasterReport.Error);

        // Связь не вернулась — сессию отключают без сброса кэша, но на её томе открыты файлы: Windows отказывает, ждём.
        clock.Advance(MasterManager.ReconnectingDetachAfter);
        await helper.TickAsync(CancellationToken.None);
        Assert.Contains($"disconnect {MasterTarget}", windows.Log.Skip(start));
        Assert.True(windows.Sessions.ContainsKey(MasterTarget));
        Assert.DoesNotContain(windows.Log.Skip(start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        var machine = await MachineAsync();
        Assert.Equal(("failed", "none"), (machine.MasterState, machine.VolumeState));
        Assert.Contains("cannot be logged out", machine.MasterError);

        // Файлы закрыли: сессия отключается, в том же такте G: снова у личного диска.
        windows.BusySessions.Remove(MasterTarget);
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(MasterTarget));
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.DoesNotContain($"flush+offline {masterDisk} G", windows.Log.Skip(start)); // мёртвой сессии сбрасывать некуда
        machine = await MachineAsync();
        Assert.Equal(("mounted", "none"), (machine.VolumeState, machine.MasterState));
    }

    [Fact]
    public async Task Helper_restart_during_editing_without_the_server_keeps_the_cached_personal_disk_off_g()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        await OpenMasterAsync();
        await helper.TickAsync(CancellationToken.None);
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(VolumeManager.IsPersonal(_cache.Current!));

        // Служба помощника перезапустилась (обновление, сбой), сервер клуба недоступен: назначения мастер-тома помощник не
        // знает, а через 3 минуты смонтировал бы сохранённый личный диск как есть. Но G: — у мастер-тома, он на ПК.
        var clock = new ManualClock();
        var start = windows.Log.Count;
        var restarted = Helper(windows, new Outage { All = true }, clock);
        await restarted.TickAsync(CancellationToken.None);
        clock.Advance(HelperLoop.CachedPersonalAfter + TimeSpan.FromSeconds(1));
        await restarted.TickAsync(CancellationToken.None);
        await restarted.TickAsync(CancellationToken.None);

        Assert.DoesNotContain(windows.Log.Skip(start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.Equal(new MasterReport("mounted", MasterTarget, "G"), restarted.LastMasterReport);
        Assert.Equal(("none", "drive letter G: is given to the master volume (library editing)"), (restarted.LastReport!.State, restarted.LastReport.Error));
    }

    [Fact]
    public async Task Helper_restart_while_the_closed_master_still_holds_g_leaves_the_personal_disk_waiting()
    {
        var (helper, windows) = await PersonalDiskMountedAsync();
        await OpenMasterAsync();
        await helper.TickAsync(CancellationToken.None);
        await _server.Services.GetRequiredService<MasterEditor>().RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _library, reconcile: false, TimeProvider.System, CancellationToken.None);
        windows.FailOffline = true; // на G: мастер-тома открыт файл — Windows не переводит диск offline

        // Служба перезапустилась раньше, чем отключила мастер-том. Запрос при старте отдаёт личный диск, но мастер-том
        // помощник видит на G: ещё до него — и личный диск на занятую букву не лезет.
        var start = windows.Log.Count;
        var restarted = Helper(windows);
        await restarted.TickAsync(CancellationToken.None);
        await restarted.TickAsync(CancellationToken.None);

        Assert.True(windows.Sessions[MasterTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.DoesNotContain(windows.Log.Skip(start), l => l.StartsWith($"connect {SeatTarget} ", StringComparison.Ordinal));
        Assert.Equal(new MasterReport("failed", MasterTarget, "G", "The disk is in use"), restarted.LastMasterReport);
        var machine = await MachineAsync();
        Assert.Equal(("none", "failed"), (machine.VolumeState, machine.MasterState));

        windows.FailOffline = false; // файл закрыли
        await restarted.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(MasterTarget));
        Assert.True(windows.Sessions[SeatTarget] is { ReadOnly: false, Offline: false, Letter: 'G' });
    }

    /// <summary>Сервер недоступен (соединение отклонено): только запрос назначения при старте или все запросы.</summary>
    private sealed class Outage : DelegatingHandler
    {
        public bool Volume { get; set; }

        public bool All { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            All || (Volume && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/volume", StringComparison.Ordinal))
                ? throw new HttpRequestException("Connection refused")
                : base.SendAsync(request, cancellationToken);
    }
}
