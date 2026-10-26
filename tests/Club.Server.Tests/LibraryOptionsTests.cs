using Club.Server.Library;
using Microsoft.Extensions.Configuration;

namespace Club.Server.Tests;

/// <summary>
/// Настройки библиотеки при старте сервера: буквы томов приводятся к одному виду или останавливают запуск; вторая
/// библиотека (Libraries:solo) — только личный слой, на своих буквах и датасетах, с именами из ключа.
/// </summary>
public sealed class LibraryOptionsTests
{
    private static LibraryOptions Options(string driveLetter, string masterDriveLetter) =>
        new() { Enabled = true, PortalAddress = "192.168.77.10:3260", DriveLetter = driveLetter, MasterDriveLetter = masterDriveLetter };

    [Theory]
    [InlineData("G", " g", "G", "G", true)]
    [InlineData("g ", "m", "G", "M", false)]
    public void Drive_letters_are_trimmed_and_upper_cased(string library, string master, string libraryAfter, string masterAfter, bool sameLetter)
    {
        // Сравнивается и уходит помощнику одно и то же: « g» в конфигурации не станет у помощника буквой « ».
        var options = Options(library, master);
        options.Validate();
        Assert.Equal((libraryAfter, masterAfter, sameLetter), (options.DriveLetter, options.MasterDriveLetter, options.MasterOnLibraryLetter));
    }

    [Theory]
    [InlineData("G", "G:")]
    [InlineData("G", " ")]
    [InlineData("G", "C")]
    [InlineData("GH", "M")]
    public void Drive_letter_that_is_not_one_letter_d_to_z_stops_the_server(string library, string master)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options(library, master).Validate());
        Assert.Contains("single drive letter", error.Message);
    }

    // ---- вторая библиотека ----------------------------------------------------------------------------------------

    private static LibraryOptions GamesOptions()
    {
        var main = new LibraryOptions
        {
            Enabled = true, PortalAddress = "192.168.77.10:3260", DriveLetter = "G", MasterDriveLetter = "G", PersonalGames = true,
            MasterZvol = "games/club/lib", PersonalParent = "games/club/seats", PublishedParent = "games/club/published", ProbeInitiatorIqn = "iqn.2026-10.local.clubsrv:probe",
        };
        main.Validate();
        return main;
    }

    private static readonly Dictionary<string, string?> Solo = new()
    {
        ["Libraries:solo:Enabled"] = "true",
        ["Libraries:solo:MasterZvol"] = "hdd/club/lib",
        ["Libraries:solo:PersonalParent"] = "hdd/club/seats",
        ["Libraries:solo:PublishedParent"] = "hdd/club/published",
        ["Libraries:solo:DriveLetter"] = "H",
        ["Libraries:solo:MasterDriveLetter"] = "H",
        ["Libraries:solo:PersonalMinHelper"] = "1.6.0",
    };

    private static IReadOnlyList<LibraryOptions> Extras(LibraryOptions main, params (string Key, string? Value)[] changes)
    {
        var settings = new Dictionary<string, string?>(Solo);
        foreach (var (key, value) in changes)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.Where(s => s.Value is not null)).Build();
        return LibraryOptions.BindExtras(configuration.GetSection("Libraries"), main);
    }

    [Fact]
    public void Valid_solo_library_takes_shared_settings_and_names_from_its_key()
    {
        var main = GamesOptions();
        main.VerifyDelayMs = 250;
        var solo = Assert.Single(Extras(main, ("Libraries:solo:DriveLetter", " h")));
        Assert.Equal(("solo", "Libraries:solo", "Offline Games", true), (solo.Key, solo.Section, solo.Title, solo.Enabled));
        Assert.Equal(("H", "H", true, true), (solo.DriveLetter, solo.MasterDriveLetter, solo.MasterOnLibraryLetter, solo.PersonalGames));
        Assert.Equal(("club-master-solo", "clubsrv-master-solo"), (solo.MasterTargetName, solo.MasterExtentName));
        Assert.Equal(("192.168.77.10:3260", 250, main.ProbeInitiatorIqn), (solo.PortalAddress, solo.VerifyDelayMs, solo.ProbeInitiatorIqn));
        Assert.Same(LibraryNames.Games, main.Names);
        Assert.Equal(("games", "Library", "Online Games"), (main.Key, main.Section, main.Title));
        Assert.Equal("solo", solo.Names.Key);
        Assert.False(solo.SupportsHelper("1.5.1"));
        Assert.True(solo.SupportsHelper("1.6.0-beta"));
    }

    [Fact]
    public void Master_letter_defaults_to_the_library_letter_and_title_can_be_set()
    {
        var solo = Assert.Single(Extras(GamesOptions(), ("Libraries:solo:MasterDriveLetter", null), ("Libraries:solo:Title", "Одиночные")));
        Assert.Equal(("H", "Одиночные"), (solo.MasterDriveLetter, solo.Title));
    }

    [Fact]
    public void Solo_is_active_only_when_both_switches_are_on()
    {
        var main = GamesOptions();
        main.Enabled = false;
        Assert.False(Assert.Single(Extras(main)).Enabled);
        Assert.False(Assert.Single(Extras(GamesOptions(), ("Libraries:solo:Enabled", "false"))).Enabled);
        // Выключенная не проверяется: незаданная буква не мешает запуску.
        Assert.False(Assert.Single(Extras(GamesOptions(), ("Libraries:solo:Enabled", null), ("Libraries:solo:DriveLetter", null))).Enabled);
    }

    [Theory]
    [InlineData("DriveLetter", "G", null, null, "drive letter G is already used by Library")]
    [InlineData("MasterDriveLetter", "G", null, null, "drive letter G is already used by Library")]
    [InlineData("DriveLetter", "M", null, null, "drive letter M is already used by Library")]
    [InlineData("DriveLetter", "", null, null, "single drive letter")]
    [InlineData("PersonalParent", "games/club/seats2", null, null, "must be in the pool of Libraries:solo:MasterZvol")]
    [InlineData("MasterZvol", "games/club/lib-h", "PersonalParent", "games/club/seats", "overlaps games/club/seats of Library")]
    [InlineData("MasterZvol", "games/club/lib/h", "PersonalParent", "games/club/h-seats", "overlaps games/club/lib of Library")]
    [InlineData("PublishedParent", "games/club", null, null, "overlaps games/club/lib of Library")]
    [InlineData("PublishedParent", "", null, null, "PublishedParent must be a dataset path")]
    [InlineData("PersonalGames", "false", null, null, "PersonalGames must be true")]
    [InlineData("MasterTargetName", "club-master", null, null, "must not be set")]
    [InlineData("MasterExtentName", "clubsrv-master-h", null, null, "must not be set")]
    [InlineData("PersonalMinHelper", "1.5.1", null, null, "must be 1.6.0 or newer")]
    [InlineData("PersonalMinHelper", "new", null, null, "PersonalMinHelper")]
    public void Unsafe_solo_settings_stop_the_server(string key, string value, string? key2, string? value2, string message)
    {
        var main = GamesOptions();
        main.MasterDriveLetter = "M";
        var changes = key2 is null ? new[] { ($"Libraries:solo:{key}", (string?)value) } : [($"Libraries:solo:{key}", value), ($"Libraries:solo:{key2}", value2)];
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => Extras(main, changes)).Message);
    }

    [Fact]
    public void Two_libraries_on_one_letter_stop_the_server()
    {
        var main = GamesOptions();
        var first = Assert.Single(Extras(main));
        var second = Assert.Single(Extras(main, ("Libraries:solo:DriveLetter", "J"), ("Libraries:solo:MasterDriveLetter", "H"),
            ("Libraries:solo:MasterZvol", "ssd/club/lib2"), ("Libraries:solo:PersonalParent", "ssd/club/seats2"), ("Libraries:solo:PublishedParent", "ssd/club/published2")));
        Assert.Contains("drive letter H is already used by Libraries:solo", Assert.Throws<InvalidOperationException>(() => second.ValidateExtra(main, [first])).Message);
        Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(() => first.ValidateExtra(main, [Assert.Single(Extras(main, ("Libraries:solo:DriveLetter", "J"), ("Libraries:solo:MasterDriveLetter", "J")))])).Message);
    }

    [Theory]
    [InlineData("games", "reserved")]
    [InlineData("club", "reserved")]
    [InlineData("seat", "reserved")]
    [InlineData("diskless", "reserved")]
    [InlineData("arcade", "unknown library")]
    public void Unknown_or_clashing_keys_stop_the_server_even_when_disabled(string key, string message)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Libraries:{key}:Enabled"] = "false" }).Build();
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => LibraryOptions.BindExtras(configuration.GetSection("Libraries"), GamesOptions())).Message);
    }
}
