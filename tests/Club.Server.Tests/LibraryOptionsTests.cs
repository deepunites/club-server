using Club.Server.Library;

namespace Club.Server.Tests;

/// <summary>Настройки библиотеки при старте сервера: буквы томов приводятся к одному виду или останавливают запуск.</summary>
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
}
