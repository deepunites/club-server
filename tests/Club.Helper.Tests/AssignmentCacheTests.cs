using System.Text;
using Club.Helper.Core;
using Microsoft.Extensions.Configuration;

namespace Club.Helper.Tests;

/// <summary>
/// Файлы назначения библиотек (1.6.0): у G: — прежний <c>assignment.json</c> в прежнем формате, у H: —
/// <c>assignment.json.solo</c>; стирание одного другой не трогает, чужой HWID не читается. Список библиотек из helper.json.
/// </summary>
public sealed class AssignmentCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"club-helper-cache-{Guid.NewGuid():N}");

    public AssignmentCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly VolumeAssignment GamesDisk =
        new("v1", "192.168.77.10:3260", "iqn.2005-10.org.freenas.ctl:games-seat-07", ReadOnly: false, "G", "games-seat-07", "S3cretS3cretS3cr");

    private static readonly VolumeAssignment SoloDisk =
        new("v3", "192.168.77.10:3260", "iqn.2005-10.org.freenas.ctl:solo-seat-07", ReadOnly: false, "H", "solo-seat-07", "S0loS0loS0loS0lo");

    [Fact]
    public async Task Each_library_has_its_own_file_and_clearing_one_keeps_the_other()
    {
        Assert.Equal("assignment.json", FileAssignmentCache.FileFor(null));
        Assert.Equal("assignment.json.solo", FileAssignmentCache.FileFor("solo"));
        var games = new FileAssignmentCache(_directory);
        var solo = new FileAssignmentCache(_directory, FileAssignmentCache.FileFor("solo"));

        await games.SaveAsync("hw-1", GamesDisk, CancellationToken.None);
        await solo.SaveAsync("hw-1", SoloDisk, CancellationToken.None);
        Assert.Equal(["assignment.json", "assignment.json.solo"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Формат файла G: — прежний (1.5.x читает его после отката помощника).
        Assert.Equal(
            """{"Hwid":"hw-1","Assignment":{"LibraryVersion":"v1","Portal":"192.168.77.10:3260","TargetIqn":"iqn.2005-10.org.freenas.ctl:games-seat-07","ReadOnly":false,"DriveLetter":"G","ChapUser":"games-seat-07","ChapSecret":"S3cretS3cretS3cr"}}""",
            await File.ReadAllTextAsync(Path.Combine(_directory, "assignment.json")));

        await games.SaveAsync("hw-1", null, CancellationToken.None);
        Assert.Equal(["assignment.json.solo"], Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Null(await games.LoadAsync("hw-1", CancellationToken.None));
        Assert.Equal(SoloDisk, await solo.LoadAsync("hw-1", CancellationToken.None));

        await games.SaveAsync("hw-1", GamesDisk, CancellationToken.None);
        await solo.SaveAsync("hw-1", null, CancellationToken.None);
        Assert.Equal(GamesDisk, await games.LoadAsync("hw-1", CancellationToken.None));
        Assert.Null(await solo.LoadAsync("hw-1", CancellationToken.None));
    }

    [Fact]
    public async Task Solo_file_of_another_pc_is_ignored()
    {
        // Файл H: приехал с диском другого ПК (эталон бездиска снят с ПК мастера).
        await new FileAssignmentCache(_directory, "assignment.json.solo").SaveAsync("hw-master", SoloDisk, CancellationToken.None);
        var solo = new FileAssignmentCache(_directory, "assignment.json.solo");
        Assert.Null(await solo.LoadAsync("hw-seat", CancellationToken.None));
        Assert.Equal(SoloDisk, await solo.LoadAsync("hw-master", CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(_directory, "assignment.json.solo"), "{ broken");
        Assert.Null(await solo.LoadAsync("hw-master", CancellationToken.None));
    }

    [Theory]
    [InlineData("""{ "Helper": { "ServerUrl": "https://club-server" } }""", "solo")]
    [InlineData("""{ "Helper": { "Libraries": [] } }""", "")]
    [InlineData("""{ "Helper": { "Libraries": "" } }""", "")]
    [InlineData("""{ "Helper": { "Libraries": ["solo"] } }""", "solo")]
    [InlineData("""{ "Helper": { "Libraries": ["solo", "extra"] } }""", "solo,extra")]
    [InlineData("""{ "Helper": { "Libraries": "solo" } }""", "solo")]
    public void Libraries_come_from_helper_json(string json, string expected)
    {
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        Assert.Equal(expected, string.Join(",", HelperOptions.LibrariesFrom(configuration.AsEnumerable())));

        // Привязка настроек рядом с этим не ломается (Program.cs: Get<HelperOptions>, потом LibrariesFrom).
        Assert.NotNull(configuration.GetSection("Helper").Get<HelperOptions>() ?? new HelperOptions());
    }
}
