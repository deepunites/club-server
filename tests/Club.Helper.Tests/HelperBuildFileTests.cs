using System.Text.Json;
using Club.Helper.Core.Updates;

namespace Club.Helper.Tests;

/// <summary>
/// <c>helper-build.json</c> (spec 2026-11-06, п. 4.5, 5.1): версия и SHA256 своего exe для скриптов — версия файла exe,
/// собранного на Linux, не наша. Запись целым файлом (<c>.tmp</c>, сброс, перенос с заменой), прежний файл заменяется,
/// неудача — <c>false</c> и текст предупреждения, а не исключение.
/// </summary>
public sealed class HelperBuildFileTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-helper-build-{Guid.NewGuid():N}");
    private readonly UpdatePaths _paths;
    private readonly List<string> _warnings = [];

    public HelperBuildFileTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "Program Files", "ClubDiskless"), Path.Combine(_root, "ProgramData", "ClubDiskless"));
        Directory.CreateDirectory(_paths.StateDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Write_puts_version_and_lowercase_build_next_to_the_update_state()
    {
        Assert.True(HelperBuildFile.Write(_paths, "1.8.0", Sha.ToUpperInvariant(), _warnings.Add));

        Assert.Equal(Path.Combine(_paths.StateDir, "helper-build.json"), _paths.BuildFile);
        using var json = JsonDocument.Parse(File.ReadAllText(_paths.BuildFile));
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("1.8.0", json.RootElement.GetProperty("version").GetString());
        Assert.Equal(Sha, json.RootElement.GetProperty("build").GetString());
        Assert.Equal(new HelperBuildInfo(1, "1.8.0", Sha), HelperBuildFile.Read(_paths));
        Assert.False(File.Exists(_paths.BuildFile + ".tmp"));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Write_replaces_the_file_of_the_previous_build()
    {
        HelperBuildFile.Write(_paths, "1.8.0", Sha, _warnings.Add);
        var newer = new string('e', 64);

        Assert.True(HelperBuildFile.Write(_paths, "1.8.1", newer, _warnings.Add));

        Assert.Equal(new HelperBuildInfo(1, "1.8.1", newer), HelperBuildFile.Read(_paths));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Write_over_a_leftover_tmp_of_a_lost_power_still_lands_whole()
    {
        File.WriteAllText(_paths.BuildFile + ".tmp", "{\"v\":1,\"vers");

        Assert.True(HelperBuildFile.Write(_paths, "1.8.0", Sha, _warnings.Add));

        Assert.Equal(new HelperBuildInfo(1, "1.8.0", Sha), HelperBuildFile.Read(_paths));
        Assert.False(File.Exists(_paths.BuildFile + ".tmp"));
    }

    [Fact]
    public void A_failed_write_keeps_the_previous_file_whole_and_does_not_throw()
    {
        HelperBuildFile.Write(_paths, "1.7.9", Sha, _warnings.Add);
        // .tmp не создаётся (на его месте папка): запись не удалась до переноса — прежний файл цел.
        Directory.CreateDirectory(_paths.BuildFile + ".tmp");

        Assert.False(HelperBuildFile.Write(_paths, "1.8.0", new string('e', 64), _warnings.Add));

        Assert.Equal(new HelperBuildInfo(1, "1.7.9", Sha), HelperBuildFile.Read(_paths));
        var warning = Assert.Single(_warnings);
        Assert.Contains("helper-build.json", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_state_folder_is_a_warning_not_an_exception()
    {
        Directory.Delete(_paths.StateDir, recursive: true);

        Assert.False(HelperBuildFile.Write(_paths, "1.8.0", Sha, _warnings.Add));

        Assert.Single(_warnings);
        Assert.Null(HelperBuildFile.Read(_paths));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"version\":\"1.8.0\",\"build\":\"" + Sha + "\"}")]
    [InlineData("{\"v\":1,\"build\":\"" + Sha + "\"}")]
    [InlineData("{\"v\":1,\"version\":\"1.8.0\"}")]
    public void Read_of_a_broken_or_foreign_file_is_null(string text)
    {
        File.WriteAllText(_paths.BuildFile, text);

        Assert.Null(HelperBuildFile.Read(_paths));
    }
}
