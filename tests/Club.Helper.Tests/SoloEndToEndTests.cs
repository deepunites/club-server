using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Две библиотеки на одном ПК (помощник 1.6.0 с поддельной Windows против настоящего сервера и поддельного TrueNAS): G: —
/// личный диск <c>games-seat-01</c>, H: — <c>solo-seat-01</c>. Каждый сбрасывается при загрузке по своему attached, правка
/// каждой библиотеки отдаёт мастер-тому только свою букву, сбой или выключение H: G: не задевают.
/// </summary>
public sealed class SoloEndToEndTests : IAsyncLifetime
{
    private LibrariesStand _stand = null!;
    private readonly WireRecorder _recorder = new();
    private readonly ManualClock _clock = new();

    public async Task InitializeAsync() => _stand = await LibrariesStand.StartAsync();

    public async Task DisposeAsync() => await _stand.DisposeAsync();

    private static FakeWindowsStorage Windows()
    {
        var windows = new FakeWindowsStorage();
        windows.Folders['G'] = ["Counter-Strike 2", "Steam"];
        windows.Folders['H'] = ["SteamLibrary", "Witcher 3"];
        return windows;
    }

    /// <summary>Помощник, только что запущенный на ПК с Windows <paramref name="windows"/>.</summary>
    private HelperLoops Helper(FakeWindowsStorage windows, LibrariesStand? stand = null, ListLoggerFactory? loggers = null)
    {
        stand ??= _stand;
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.6.0" };
        var api = new DisklessApiClient(stand.Http(_recorder.Handler()), options, stand.Credentials, stand.Identity);
        return HelperLoops.Create(api, windows, new FakeProcesses(), stand.Identity, options, _clock, loggers ?? new ListLoggerFactory(), stand.Cache);
    }

    private async Task<(HelperLoops Helper, FakeWindowsStorage Windows)> BootAsync()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var windows = Windows();
        var helper = Helper(windows);
        await helper.TickAsync(CancellationToken.None);
        AssertBothMounted(windows);
        return (helper, windows);
    }

    private static void AssertBothMounted(FakeWindowsStorage windows)
    {
        Assert.True(windows.Sessions[LibrariesStand.GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(windows.Sessions[LibrariesStand.SoloSeat] is { ReadOnly: false, Offline: false, Letter: 'H' });
    }

    private Task<Club.Server.Library.LibraryMachineRow?> SoloRowAsync() => _stand.Solo.Machines!.FindAsync(_stand.MachineId);

    private IReadOnlyList<string> BootRequests() =>
        [.. _recorder.Paths.Where(p => p.Contains("/volume?", StringComparison.Ordinal)).Select(p => p[(p.IndexOf("/machines/{id}/", StringComparison.Ordinal) + 15)..])];

    [Fact]
    public async Task Boot_mounts_g_and_h_and_each_is_reset_by_its_own_attached()
    {
        var (helper, windows) = await BootAsync();

        var solo = helper.Libraries[0];
        Assert.Equal(("mounted", "v1", "H"), (solo.LastReport!.State, solo.LastReport.LibraryVersion, solo.LastReport.DriveLetter));
        Assert.Equal(["games-seat-01", "solo-seat-01"], windows.ChapLogins.Select(l => l.User));
        // G: собран по отчёту и сброшен запросом при старте; H: собран по запросу при старте — уже чистый.
        Assert.Equal((1, 0), (_stand.GamesResets, _stand.SoloResets));

        // Сервер: G: — в machines, H: — в solo.machine_library, состав каждой версии — в своей библиотеке.
        var machine = await _stand.MachineAsync();
        Assert.Equal(("mounted", "v1", $"{LibrariesStand.Basename}:games-seat-01"), (machine.VolumeState, machine.VolumeVersion, machine.VolumeIqn));
        var row = await SoloRowAsync();
        Assert.Equal(("mounted", "v1", LibrariesStand.SoloSeat), (row!.VolumeState, row.VolumeVersion, row.VolumeIqn));
        Assert.Equal(["Counter-Strike 2", "Steam"], (await _stand.Games.Repository.FindVersionAsync("v1"))!.Contents);
        Assert.Equal(["SteamLibrary", "Witcher 3"], (await _stand.Solo.Repository.FindVersionAsync("v1"))!.Contents);

        // Перезапуск одной службы: оба диска подключены — ни один не сбрасывается.
        await Helper(windows).TickAsync(CancellationToken.None);
        AssertBothMounted(windows);
        Assert.Equal((1, 0), (_stand.GamesResets, _stand.SoloResets));

        // Сессия H: пропала, G: на месте — сервер сбрасывает только H:.
        await windows.DisconnectAsync(LibrariesStand.SoloSeat, CancellationToken.None);
        await Helper(windows).TickAsync(CancellationToken.None);
        AssertBothMounted(windows);
        Assert.Equal((1, 1), (_stand.GamesResets, _stand.SoloResets));
        Assert.Equal(
            [
                "volume?attached=false", "volume?attached=false", "libraries/solo/volume?attached=false", // загрузка (G: второй раз — за сбросом)
                "volume?attached=true", "libraries/solo/volume?attached=true", // перезапуск службы
                "volume?attached=true", "libraries/solo/volume?attached=false", // перезапуск без сессии H:
            ],
            BootRequests());

        // Перезагрузка: сброс обоих.
        var rebooted = Windows();
        await Helper(rebooted).TickAsync(CancellationToken.None);
        AssertBothMounted(rebooted);
        Assert.Equal((2, 2), (_stand.GamesResets, _stand.SoloResets));
    }

    [Fact]
    public async Task Publishing_h_rebuilds_only_the_h_disk()
    {
        await BootAsync();
        await _stand.PublishAsync(_stand.Solo, "v2");
        var calls = _stand.Nas.CallRecords.Count;

        var rebooted = Windows();
        var helper = Helper(rebooted);
        await helper.TickAsync(CancellationToken.None);

        AssertBothMounted(rebooted);
        Assert.Equal(("mounted", "v1"), (helper.Games.LastReport!.State, helper.Games.LastReport.LibraryVersion));
        Assert.Equal(("mounted", "v2"), (helper.Libraries[0].LastReport!.State, helper.Libraries[0].LastReport!.LibraryVersion));
        var since = _stand.Nas.CallRecords.Skip(calls).ToList();
        Assert.Contains(since, c => c.StartsWith("pool.snapshot.clone ", StringComparison.Ordinal) && c.Contains("hdd/club/lib@lib-v2", StringComparison.Ordinal));
        Assert.DoesNotContain(since, c => c.Contains("games/club/seats/games-seat-01", StringComparison.Ordinal)
            && (c.StartsWith("pool.snapshot.clone ", StringComparison.Ordinal) || c.StartsWith("pool.dataset.delete ", StringComparison.Ordinal)));
        Assert.Equal((2, 0), (_stand.GamesResets, _stand.SoloResets)); // G: — только откат к @clean, H: — новый клон
        Assert.Equal("v1", (await _stand.MachineAsync()).VolumeVersion);
        Assert.Equal("v2", (await SoloRowAsync())!.VolumeVersion);
    }

    [Fact]
    public async Task H_master_takes_h_while_g_stays_mounted()
    {
        var (helper, windows) = await BootAsync();
        var gamesDisk = windows.Sessions[LibrariesStand.GamesSeat].Number;
        var soloDisk = windows.Sessions[LibrariesStand.SoloSeat].Number;
        await _stand.OpenMasterAsync(_stand.Solo);
        var start = windows.Log.Count;

        await helper.TickAsync(CancellationToken.None);

        // Личный диск H: отпущен (кэш, offline, отключение), мастер-том H: — на H:; G: не тронут.
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.SoloSeat));
        Assert.True(windows.Sessions[LibrariesStand.SoloMaster] is { ReadOnly: false, Offline: false, Letter: 'H' });
        Assert.True(windows.Sessions[LibrariesStand.GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        var log = windows.Log.Skip(start).ToList();
        Assert.Contains($"flush+offline {soloDisk} H", log);
        Assert.DoesNotContain(log, l => l.StartsWith($"flush+offline {gamesDisk} ", StringComparison.Ordinal) || l.Contains(":games-", StringComparison.Ordinal));
        Assert.Equal(new MasterReport("mounted", LibrariesStand.SoloMaster, "H"), helper.Libraries[0].LastMasterReport);
        Assert.Null(helper.Games.LastMasterReport);

        // Сервер: мастер-том H: — в machine_library, у G: в machines мастер-тома нет.
        var row = await SoloRowAsync();
        Assert.Equal(("none", "mounted"), (row!.VolumeState, row.MasterState));
        Assert.Equal("drive letter H: is given to the master volume (library editing)", row.VolumeError);
        var machine = await _stand.MachineAsync();
        Assert.Equal("mounted", machine.VolumeState);
        Assert.NotEqual("mounted", machine.MasterState);

        // Правку H: закрыли — мастер-том отключается, личный диск H: возвращается в том же такте.
        await _stand.CloseMasterAsync(_stand.Solo);
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.SoloMaster));
        AssertBothMounted(windows);
        row = await SoloRowAsync();
        Assert.Equal(("mounted", "none"), (row!.VolumeState, row.MasterState));
    }

    [Fact]
    public async Task G_and_h_masters_are_open_together_and_close_independently()
    {
        var (helper, windows) = await BootAsync();
        await _stand.OpenMasterAsync(_stand.Games);
        await _stand.OpenMasterAsync(_stand.Solo);

        await helper.TickAsync(CancellationToken.None);

        Assert.True(windows.Sessions[LibrariesStand.GamesMaster] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(windows.Sessions[LibrariesStand.SoloMaster] is { ReadOnly: false, Offline: false, Letter: 'H' });
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.GamesSeat));
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.SoloSeat));
        Assert.Equal(("none", "mounted"), ((await _stand.MachineAsync()).VolumeState, (await _stand.MachineAsync()).MasterState));
        Assert.Equal(("none", "mounted"), ((await SoloRowAsync())!.VolumeState, (await SoloRowAsync())!.MasterState));

        // Закрыли правку G: — G: снова личный диск, мастер-том H: остаётся.
        await _stand.CloseMasterAsync(_stand.Games);
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.GamesMaster));
        Assert.True(windows.Sessions[LibrariesStand.GamesSeat] is { Offline: false, Letter: 'G' });
        Assert.True(windows.Sessions[LibrariesStand.SoloMaster] is { Offline: false, Letter: 'H' });
        Assert.Equal("mounted", (await SoloRowAsync())!.MasterState);

        // Закрыли правку H: — и H: снова личный диск.
        await _stand.CloseMasterAsync(_stand.Solo);
        await helper.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.SoloMaster));
        AssertBothMounted(windows);
    }

    [Fact]
    public async Task Letter_h_taken_by_a_local_disk_fails_h_and_keeps_g()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var windows = Windows();
        windows.LocalLetters.Add('H'); // картридер на H:
        var loggers = new ListLoggerFactory();
        var helper = Helper(windows, loggers: loggers);

        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        Assert.True(windows.Sessions[LibrariesStand.GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.Equal("mounted", helper.Games.LastReport!.State);
        var solo = helper.Libraries[0].LastReport!;
        Assert.Equal("failed", solo.State);
        Assert.Contains("drive letter H is in use", solo.Error);
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.SoloSeat));
        Assert.Equal("mounted", (await _stand.MachineAsync()).VolumeState);
        var row = await SoloRowAsync();
        Assert.Equal("failed", row!.VolumeState);
        Assert.Contains("drive letter H is in use", row.VolumeError);

        // Буква проверяется до входа в таргет: диск H: ни разу не подключался и не выходил online на запись, в журнале —
        // одно предупреждение на все такты, ошибок нет (иначе — вход, отказ буквы, отключение и Error каждые 30 с).
        Assert.DoesNotContain(windows.ChapLogins, l => l.Iqn == LibrariesStand.SoloSeat);
        Assert.Equal(3, windows.LetterChecks.Count(l => l == 'H'));
        Assert.DoesNotContain(windows.LetterChecks, l => l == 'G'); // у G: путь прежний
        const string soloVolumes = "Club.Helper.Core.VolumeManager.solo";
        Assert.Single(loggers.Of(soloVolumes), e => e.Level == LogLevel.Warning && e.Message.StartsWith("Drive letter H is taken", StringComparison.Ordinal));
        Assert.DoesNotContain(loggers.Entries, e => e.Level >= LogLevel.Error);

        // Букву освободили — H: встаёт в следующем такте.
        windows.LocalLetters.Remove('H');
        await helper.TickAsync(CancellationToken.None);
        AssertBothMounted(windows);
        Assert.Contains(loggers.Of(soloVolumes), e => e.Level == LogLevel.Information && e.Message.StartsWith("Drive letter H is free again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_without_h_puts_the_h_loop_to_sleep_and_g_works()
    {
        await using var stand = await LibrariesStand.StartAsync(solo: false);
        await stand.PublishAsync(stand.Games, "v1");
        var windows = Windows();
        var helper = Helper(windows, stand);

        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        Assert.True(windows.Sessions[LibrariesStand.GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(helper.Libraries[0].Dormant);
        Assert.Null(helper.Libraries[0].LastReport);
        Assert.DoesNotContain(windows.Log, l => l.Contains(":solo-", StringComparison.Ordinal));
        Assert.Equal(["libraries/solo/volume?attached=false"], BootRequests().Where(p => p.StartsWith("libraries/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task H_turned_off_on_a_running_server_sleeps_keeping_the_disk_and_wakes_without_a_reset()
    {
        var (helper, windows) = await BootAsync();
        var slot = _stand.Solo;
        var resets = _stand.SoloResets;
        slot.Options.Enabled = false; // Libraries__solo__Enabled=false и перезапуск сервера
        var start = windows.Log.Count;

        await helper.TickAsync(CancellationToken.None);

        // Отчёт H: получил 404: цикл спит, диск H: остаётся до перезагрузки, assignment.json.solo стёрт; G: работает.
        var solo = helper.Libraries[0];
        Assert.True(solo.Dormant);
        AssertBothMounted(windows);
        Assert.Empty(windows.Log.Skip(start));
        Assert.Null(_stand.Cache("solo").Current);
        Assert.Equal("mounted", helper.Games.LastReport!.State);

        // H: включили обратно; через 10 минут цикл просыпается: диск подключён (attached=true) — без сброса.
        slot.Options.Enabled = true;
        _clock.Advance(HelperLoop.DormantFor);
        await helper.TickAsync(CancellationToken.None);
        Assert.False(solo.Dormant);
        Assert.Equal(("mounted", "v1"), (solo.LastReport!.State, solo.LastReport.LibraryVersion));
        Assert.Equal(resets, _stand.SoloResets);
        Assert.Equal("libraries/solo/volume?attached=true", BootRequests()[^1]);
        Assert.Equal(LibrariesStand.SoloSeat, _stand.Cache("solo").Current!.TargetIqn);
    }
}
