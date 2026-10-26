using Club.Helper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

/// <summary>
/// Две библиотеки на одной Windows (помощник 1.6.0): VolumeManager и MasterManager G: и H: видят, отключают и отпускают
/// только свои таргеты — личный диск и мастер-том соседней библиотеки для них чужое подключение, как системный диск бездиска.
/// </summary>
public sealed class LibraryMarkersTests
{
    private const string Portal = "192.168.77.10:3260";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string GamesSeat = $"{Basename}:games-seat-07";
    private const string SoloSeat = $"{Basename}:solo-seat-07";
    private const string GamesMaster = $"{Basename}:club-master";
    private const string SoloMaster = $"{Basename}:club-master-solo";

    private static readonly LibraryMarkers Solo = LibraryMarkers.For("solo");

    private readonly FakeWindowsStorage _storage = new();
    private readonly HelperOptions _options = new() { DiskWaitSec = 1 };
    private readonly VolumeManager _games;
    private readonly VolumeManager _solo;
    private readonly MasterManager _gamesMaster;
    private readonly MasterManager _soloMaster;

    public LibraryMarkersTests()
    {
        var processes = new FakeProcesses();
        _games = new VolumeManager(_storage, processes, _options, TimeProvider.System, NullLogger<VolumeManager>.Instance);
        _solo = new VolumeManager(_storage, processes, _options, TimeProvider.System, NullLogger.Instance, Solo);
        _gamesMaster = new MasterManager(_storage, _options, TimeProvider.System, NullLogger<MasterManager>.Instance);
        _soloMaster = new MasterManager(_storage, _options, TimeProvider.System, NullLogger.Instance, Solo);
    }

    private static VolumeAssignment Personal(string iqn, string letter, string version = "v1") =>
        new(version, Portal, iqn, ReadOnly: false, letter, iqn[(iqn.LastIndexOf(':') + 1)..], "S3cretS3cretS3cr");

    private static MasterAssignment Master(string iqn, string user, string letter) => new(iqn, Portal, user, "M4sterM4sterM4st", letter);

    [Fact]
    public void Markers_of_g_are_the_old_ones_and_solo_is_derived_from_its_key()
    {
        Assert.Equal(new LibraryMarkers(null, ":games-", ":games-seat-", ":club-master"), LibraryMarkers.Games);
        Assert.Equal(new LibraryMarkers("solo", ":solo-", ":solo-seat-", ":club-master-solo"), Solo);

        foreach (var key in new[] { "games", "club", "seat", "diskless", "master", "lib", "public", "Solo", "s", "solo-x", "1solo", "thirteenchars", "" })
        {
            Assert.False(LibraryMarkers.IsValidKey(key), key);
            Assert.Throws<ArgumentException>(() => LibraryMarkers.For(key));
        }
    }

    [Theory]
    [InlineData("games-2026-10-28", true, false, false, false, false, false)]
    [InlineData("games-seat-07", true, true, false, false, false, false)]
    [InlineData("club-master", false, false, true, false, false, false)]
    [InlineData("solo-2026-10-28", false, false, false, true, false, false)]
    [InlineData("solo-seat-07", false, false, false, true, true, false)]
    [InlineData("club-master-solo", false, false, false, false, false, true)]
    [InlineData("seat-07", false, false, false, false, false, false)]
    [InlineData("diskless-master", false, false, false, false, false, false)]
    public void Every_target_belongs_to_one_library_at_most(
        string name, bool gamesManaged, bool gamesPersonal, bool gamesMaster, bool soloManaged, bool soloPersonal, bool soloMaster)
    {
        var iqn = $"{Basename}:{name}";
        Assert.Equal(
            (gamesManaged, gamesPersonal, gamesMaster, soloManaged, soloPersonal, soloMaster),
            (LibraryMarkers.Games.IsManaged(iqn), LibraryMarkers.Games.IsPersonal(iqn), LibraryMarkers.Games.IsMaster(iqn),
                Solo.IsManaged(iqn), Solo.IsPersonal(iqn), Solo.IsMaster(iqn)));
    }

    [Fact]
    public async Task Version_and_personal_disk_are_read_with_the_markers_of_the_instance()
    {
        Assert.Equal("v1", _solo.VersionOf($"{Basename}:solo-v1"));
        Assert.Null(_solo.VersionOf(SoloSeat));
        Assert.Null(_solo.VersionOf($"{Basename}:games-v1"));
        Assert.Equal("v1", _games.VersionOf($"{Basename}:games-v1"));
        Assert.Null(_games.VersionOf($"{Basename}:solo-v1"));
        Assert.Null(VolumeManager.VersionOfTarget($"{Basename}:solo-v1"));

        Assert.True(_solo.IsPersonalTarget(Personal(SoloSeat, "H")));
        Assert.False(_solo.IsPersonalTarget(Personal(GamesSeat, "G")));
        Assert.True(_games.IsPersonalTarget(Personal(GamesSeat, "G")));
        Assert.False(_games.IsPersonalTarget(Personal(SoloSeat, "H")));
        Assert.False(VolumeManager.IsPersonal(Personal(SoloSeat, "H")));

        // На запись — только личный диск своей библиотеки: диск H: в назначении G: не монтируется.
        Assert.Equal("failed", (await _games.ApplyAsync(Personal(SoloSeat, "G"), CancellationToken.None)).State);
        Assert.Empty(_storage.Sessions);
    }

    [Fact]
    public async Task Each_library_mounts_its_personal_disk_and_leaves_the_other_alone()
    {
        Assert.Equal("mounted", (await _games.ApplyAsync(Personal(GamesSeat, "G"), CancellationToken.None)).State);
        Assert.Equal("mounted", (await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None)).State);
        Assert.True(_storage.Sessions[GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(_storage.Sessions[SoloSeat] is { ReadOnly: false, Offline: false, Letter: 'H' });

        // attached — по своей библиотеке.
        Assert.True(await _games.PersonalAttachedAsync(CancellationToken.None));
        Assert.True(await _solo.PersonalAttachedAsync(CancellationToken.None));
        await _storage.DisconnectAsync(SoloSeat, CancellationToken.None);
        Assert.True(await _games.PersonalAttachedAsync(CancellationToken.None));
        Assert.False(await _solo.PersonalAttachedAsync(CancellationToken.None));
        await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None);

        // Повтор ничего не меняет: чужой диск — не «старая версия».
        _storage.Log.Clear();
        Assert.Equal("mounted", (await _games.ApplyAsync(Personal(GamesSeat, "G"), CancellationToken.None)).State);
        Assert.Equal("mounted", (await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None)).State);
        Assert.Empty(_storage.Log);
    }

    [Fact]
    public async Task No_assignment_releases_only_the_own_library()
    {
        await _games.ApplyAsync(Personal(GamesSeat, "G"), CancellationToken.None);
        await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None);
        await _gamesMaster.ApplyAsync(Master(GamesMaster, "clubsrv-master", "M"), known: true, CancellationToken.None);

        Assert.Equal("none", (await _solo.ApplyAsync(null, CancellationToken.None)).State);
        Assert.False(_storage.Sessions.ContainsKey(SoloSeat));
        Assert.True(_storage.Sessions.ContainsKey(GamesSeat));
        Assert.True(_storage.Sessions.ContainsKey(GamesMaster));

        await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None);
        Assert.Equal("none", (await _games.ApplyAsync(null, CancellationToken.None)).State);
        Assert.False(_storage.Sessions.ContainsKey(GamesSeat));
        Assert.True(_storage.Sessions.ContainsKey(SoloSeat));
        Assert.True(_storage.Sessions.ContainsKey(GamesMaster));
    }

    [Fact]
    public async Task Release_for_the_master_flushes_and_disconnects_only_the_own_personal_disk()
    {
        await _games.ApplyAsync(Personal(GamesSeat, "G"), CancellationToken.None);
        await _solo.ApplyAsync(Personal(SoloSeat, "H"), CancellationToken.None);
        var gamesDisk = _storage.Sessions[GamesSeat].Number;
        var soloDisk = _storage.Sessions[SoloSeat].Number;

        var released = await _solo.ReleaseForMasterAsync(Personal(SoloSeat, "H"), 'H', CancellationToken.None);
        Assert.Equal(("none", "drive letter H: is given to the master volume (library editing)"), (released.State, released.Error));
        Assert.Contains($"flush+offline {soloDisk} H", _storage.Log);
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith($"flush+offline {gamesDisk} ", StringComparison.Ordinal));
        Assert.DoesNotContain($"disconnect {GamesSeat}", _storage.Log);
        Assert.True(_storage.Sessions[GamesSeat] is { Offline: false, Letter: 'G' });

        await _games.ReleaseForMasterAsync(Personal(GamesSeat, "G"), 'G', CancellationToken.None);
        Assert.False(_storage.Sessions.ContainsKey(GamesSeat));
        Assert.Equal(2, _storage.Log.Count(l => l.StartsWith("disconnect ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Master_managers_see_only_their_own_master()
    {
        await _storage.EnsureSanPolicyOfflineSharedAsync(CancellationToken.None);
        Assert.Equal("mounted", (await _gamesMaster.ApplyAsync(Master(GamesMaster, "clubsrv-master", "G"), known: true, CancellationToken.None))!.State);
        Assert.Equal("mounted", (await _soloMaster.ApplyAsync(Master(SoloMaster, "clubsrv-master-solo", "H"), known: true, CancellationToken.None))!.State);
        Assert.True(_storage.Sessions[GamesMaster] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(_storage.Sessions[SoloMaster] is { ReadOnly: false, Offline: false, Letter: 'H' });

        // Назначенный свой мастер-том — чужой не «устаревший» и не отключается.
        _storage.Log.Clear();
        await _gamesMaster.ApplyAsync(Master(GamesMaster, "clubsrv-master", "G"), known: true, CancellationToken.None);
        await _soloMaster.ApplyAsync(Master(SoloMaster, "clubsrv-master-solo", "H"), known: true, CancellationToken.None);
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith("disconnect ", StringComparison.Ordinal));

        // Без назначения, пока сервер не ответил, — свой (или ничего), не чужой.
        Assert.Equal(new MasterReport("mounted", GamesMaster, "G"), await _gamesMaster.ApplyAsync(null, known: false, CancellationToken.None));
        Assert.Equal(new MasterReport("mounted", SoloMaster, "H"), await _soloMaster.ApplyAsync(null, known: false, CancellationToken.None));

        // Правку H: закрыли — отключается только мастер-том H:.
        var soloDisk = _storage.Sessions[SoloMaster].Number;
        Assert.Equal(new MasterReport("none"), await _soloMaster.ApplyAsync(null, known: true, CancellationToken.None));
        Assert.Equal([$"flush+offline {soloDisk} H", $"disconnect {SoloMaster}"], _storage.Log);
        Assert.True(_storage.Sessions[GamesMaster] is { Offline: false, Letter: 'G' });
        Assert.Null(await _soloMaster.ApplyAsync(null, known: true, CancellationToken.None));

        // И наоборот: правку G: закрыли — мастер-том H: (открытый снова) остаётся.
        await _soloMaster.ApplyAsync(Master(SoloMaster, "clubsrv-master-solo", "H"), known: true, CancellationToken.None);
        Assert.Equal(new MasterReport("none"), await _gamesMaster.ApplyAsync(null, known: true, CancellationToken.None));
        Assert.False(_storage.Sessions.ContainsKey(GamesMaster));
        Assert.True(_storage.Sessions[SoloMaster] is { Offline: false, Letter: 'H' });
        Assert.Null(await _gamesMaster.ApplyAsync(null, known: true, CancellationToken.None));
    }
}
