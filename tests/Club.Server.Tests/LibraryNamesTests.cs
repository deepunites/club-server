using System.Text.RegularExpressions;
using Club.Server.Library;

namespace Club.Server.Tests;

/// <summary>
/// Имена библиотек: у G: — ровно прежние литералы (их знают помощники 1.5.1 и скрипты эталона), у H: — выведены из ключа
/// и не попадают ни под одну чужую метку. Метки помощника и скриптов здесь — копии их кода (VolumeManager,
/// MasterManager, seal-*.ps1, prepare-*.ps1).
/// </summary>
public sealed class LibraryNamesTests
{
    private const string Basename = "iqn.2005-10.org.freenas.ctl";

    [Fact]
    public void Games_names_are_the_literals_before_the_second_library()
    {
        var g = LibraryNames.Games;
        Assert.Equal(
            ("games", true, "games-seat-", "games seat", "clubsrv personal games seat", "seat-games", "games-", "games", "lib-"),
            (g.Key, g.IsMain, g.SeatTargetPrefix, g.SeatAlias, g.SeatExtentComment, g.SeatRole, g.VersionTargetPrefix, g.VersionTargetAlias, g.VersionExtentPrefix));
        Assert.Equal(
            ("club-master", "club master", "clubsrv-master", "clubsrv master (superclient)", "clubsrv-master", "clubsrv master"),
            (g.MasterTargetName, g.MasterAlias, g.MasterExtentName, g.MasterExtentComment, g.MasterChapUser, g.MasterGroupComment));
        Assert.Equal(("library", "", "machines", (string?)null, "Online Games"), (g.WarningSource, g.WarningSubjectPrefix, g.MachineStateTable, g.LibraryLabel, g.DefaultTitle));
        Assert.Equal("games-seat-07", g.SeatTarget(7));
        Assert.Equal(SeatGames.TargetName(7), g.SeatTarget(7));
        Assert.Equal(SeatGames.TargetPrefix, g.SeatTargetPrefix);
        Assert.Equal(MasterEditor.ChapUser, g.MasterChapUser);
        Assert.Equal(new LibraryOptions().MasterTargetName, g.MasterTargetName);
        Assert.Equal(new LibraryOptions().MasterExtentName, g.MasterExtentName);
        Assert.Empty(g.Problems());
    }

    [Fact]
    public void Solo_names_follow_the_plan()
    {
        var h = LibraryNames.For("solo");
        Assert.Equal(
            ("solo", false, "solo-seat-", "solo seat", "clubsrv personal solo seat", "seat-solo", "solo-", "solo", "solo-lib-"),
            (h.Key, h.IsMain, h.SeatTargetPrefix, h.SeatAlias, h.SeatExtentComment, h.SeatRole, h.VersionTargetPrefix, h.VersionTargetAlias, h.VersionExtentPrefix));
        Assert.Equal(
            ("club-master-solo", "club master solo", "clubsrv-master-solo", "clubsrv master solo (superclient)", "clubsrv-master-solo", "clubsrv master solo"),
            (h.MasterTargetName, h.MasterAlias, h.MasterExtentName, h.MasterExtentComment, h.MasterChapUser, h.MasterGroupComment));
        Assert.Equal(("solo", "solo:", "machine_library", (string?)"solo", "Offline Games"), (h.WarningSource, h.WarningSubjectPrefix, h.MachineStateTable, h.LibraryLabel, h.DefaultTitle));
        Assert.Equal("solo-seat-07", h.SeatTarget(7));
        Assert.Empty(h.Problems());
    }

    [Fact]
    public void Solo_targets_are_invisible_to_g_helpers_and_image_scripts()
    {
        var h = LibraryNames.For("solo");
        foreach (var target in new[] { h.SeatTarget(7), h.SeatTarget(24), $"{h.VersionTargetPrefix}2026-10-28", h.MasterTargetName }.Select(t => $"{Basename}:{t}"))
        {
            // Помощник G: (VolumeManager.ManagedTargetMarker, MasterManager.TargetMarker).
            Assert.DoesNotContain(":games-", target);
            Assert.False(target.EndsWith(":club-master", StringComparison.Ordinal), target);
            // Скрипты эталона: системный диск бездиска.
            Assert.DoesNotMatch(new Regex(@":(diskless-master|seat-\d+)$"), target);
            Assert.False(target.EndsWith(":seat-07", StringComparison.Ordinal) || target.EndsWith(":seat-24", StringComparison.Ordinal), target);
        }

        // Помощник H: (метки :solo-, :solo-seat-, конец :club-master-solo): свои диски — свои, мастер-том — не диск.
        Assert.Contains(":solo-seat-", $"{Basename}:{h.SeatTarget(7)}");
        Assert.Contains(":solo-", $"{Basename}:{h.VersionTargetPrefix}2026-10-28");
        Assert.DoesNotContain(":solo-", $"{Basename}:{h.MasterTargetName}");
        Assert.EndsWith(":club-master-solo", $"{Basename}:{h.MasterTargetName}");

        // И наоборот: таргеты G: и бездиска не под метками H:.
        foreach (var target in new[] { "games-seat-07", "games-2026-10-28", "games-solo-x", "club-master", "seat-07", "diskless-master" }.Select(t => $"{Basename}:{t}"))
        {
            Assert.DoesNotContain(":solo-", target);
            Assert.False(target.EndsWith(":club-master-solo", StringComparison.Ordinal), target);
        }
    }

    [Theory]
    [InlineData("games")]
    [InlineData("club")]
    [InlineData("seat")]
    [InlineData("diskless")]
    [InlineData("master")]
    [InlineData("lib")]
    [InlineData("public")]
    public void Keys_that_collide_with_existing_markers_are_refused(string key) =>
        Assert.Contains("reserved", Assert.Throws<InvalidOperationException>(() => LibraryNames.For(key)).Message);

    [Theory]
    [InlineData("Solo")]
    [InlineData("s")]
    [InlineData("solo-games")]
    [InlineData("1solo")]
    public void Keys_must_be_short_lowercase_words(string key) =>
        Assert.Contains("must match", Assert.Throws<InvalidOperationException>(() => LibraryNames.For(key)).Message);
}
