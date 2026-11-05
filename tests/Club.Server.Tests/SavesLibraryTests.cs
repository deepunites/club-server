using System.IO.Compression;
using System.Text;
using Club.Saves;

namespace Club.Server.Tests;

/// <summary>
/// Общая библиотека сохранений игроков (<c>Club.Saves</c>): шаблоны путей из панели, ID игрока от шелла, архив папок
/// игрока — одни и те же проверки на сервере и в помощнике (spec 2026-11-05, п. 3.4 и 4.2).
/// </summary>
public sealed class SavesLibraryTests : IDisposable
{
    private static readonly char[] Letters = ['G', 'H'];
    private static readonly string[] DefaultExcludes = ["Logs", "Crashes", "CrashDumps", "*.dmp"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-saves-{Guid.NewGuid():N}");

    public SavesLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"%LOCALAPPDATA%\GameName\Saved")]
    [InlineData(@"%SAVEDGAMES%\Игра")]
    [InlineData(@"H:\Игры\Saves")]
    [InlineData(@"g:\Games\CS2\cfg")]
    [InlineData(@"%DOCUMENTS%\My Games\Skyrim")]
    public void Validate_accepts_profile_and_library_paths(string template) =>
        Assert.Null(SavePathTemplate.Validate(template, Letters));

    [Theory]
    [InlineData("", "empty")]
    [InlineData(null, "empty")]
    [InlineData(@"%TEMP%\x", "badVariable")]
    [InlineData(@"%APPDATA%\..\x", "dotDot")]
    [InlineData(@"C:\Games\x", "drive")]
    [InlineData(@"Games\x", "drive")]
    [InlineData(@"\\server\share\x", "drive")]
    [InlineData("%USERPROFILE%", "root")]
    [InlineData(@"%APPDATA%\", "root")]
    [InlineData(@"H:\", "root")]
    [InlineData(@"H:\a|b", "invalidChars")]
    [InlineData(@"H:\Games\con", "invalidChars")]
    public void Validate_rejects(string? template, string code) =>
        Assert.Equal(code, SavePathTemplate.Validate(template, Letters));

    [Fact]
    public void Validate_rejects_too_long()
    {
        var template = @"H:\" + string.Concat(Enumerable.Repeat(@"abcdefghi\", 25)) + "abcdefgh";
        Assert.Equal(261, template.Length);
        Assert.Equal("tooLong", SavePathTemplate.Validate(template, Letters));
        Assert.Null(SavePathTemplate.Validate(template[..260], Letters));
    }

    [Fact]
    public void Parse_splits_anchor_and_relative()
    {
        Assert.Equal(new SaveTarget(SaveTargetKind.Profile, "%LOCALAPPDATA%", @"GameName\Saved"), SavePathTemplate.Parse(@"%LOCALAPPDATA%\GameName\Saved"));
        Assert.Equal(new SaveTarget(SaveTargetKind.Library, "H", @"Игра\Saves"), SavePathTemplate.Parse(@"h:\Игра\Saves"));
        Assert.Equal(new SaveTarget(SaveTargetKind.Profile, "%APPDATA%", @"Game\Saves"), SavePathTemplate.Parse("%appdata%/Game/Saves/"));
    }

    [Fact]
    public void Profile_subpaths()
    {
        Assert.Equal("", SavePathTemplate.ProfileSubpath("%USERPROFILE%"));
        Assert.Equal(@"AppData\Roaming", SavePathTemplate.ProfileSubpath("%APPDATA%"));
        Assert.Equal(@"AppData\Local", SavePathTemplate.ProfileSubpath("%LOCALAPPDATA%"));
        Assert.Equal(@"AppData\LocalLow", SavePathTemplate.ProfileSubpath("%LOCALAPPDATA_LOW%"));
        Assert.Equal("Documents", SavePathTemplate.ProfileSubpath("%DOCUMENTS%"));
        Assert.Equal("Saved Games", SavePathTemplate.ProfileSubpath("%SAVEDGAMES%"));
    }

    [Fact]
    public void PlayerIds_normalize()
    {
        Assert.Equal("abc-1", PlayerIds.Normalize("ABC-1"));
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", PlayerIds.Normalize("3F2504E0-4F89-11D3-9A0C-0305E82C3301"));
        Assert.Equal(new string('a', 64), PlayerIds.Normalize(new string('A', 64)));
        Assert.Null(PlayerIds.Normalize("a/b"));
        Assert.Null(PlayerIds.Normalize(""));
        Assert.Null(PlayerIds.Normalize(null));
        Assert.Null(PlayerIds.Normalize("ab c"));
        Assert.Null(PlayerIds.Normalize("игрок"));
        Assert.Null(PlayerIds.Normalize(new string('a', 65)));
    }

    [Theory]
    [InlineData("Logs/x.log", true)]
    [InlineData(@"sub\Logs\x.log", true)]
    [InlineData("crash.DMP", true)]
    [InlineData("sub/crash.dmp", true)]
    [InlineData("Logsbook/x.txt", false)]
    [InlineData("save.dmp.bak", false)]
    [InlineData("Сохранение 1.sav", false)]
    public void IsExcluded_matches_folder_names_and_masks(string relativePath, bool excluded) =>
        Assert.Equal(excluded, SaveArchive.IsExcluded(relativePath, DefaultExcludes));

    [Fact]
    public void Pack_then_unpack_roundtrips_cyrillic_names()
    {
        var source = Path.Combine(_root, "src", "Игра");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        Directory.CreateDirectory(Path.Combine(source, "Logs"));
        File.WriteAllText(Path.Combine(source, "Сохранение 1.sav"), "прогресс игрока");
        File.WriteAllBytes(Path.Combine(source, "sub", "data.bin"), [1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(source, "Logs", "x.log"), "log");
        File.WriteAllText(Path.Combine(source, "crash.dmp"), "dump");
        var entryId = Guid.NewGuid();

        using var zip = new MemoryStream();
        var packed = SaveArchive.Pack(zip, [(entryId, source), (Guid.NewGuid(), Path.Combine(_root, "missing"))], DefaultExcludes);

        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(
                new[] { $"{entryId:D}/sub/data.bin", $"{entryId:D}/Сохранение 1.sav" },
                archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        zip.Position = 0;
        var target = Path.Combine(_root, "dst");
        var unpacked = SaveArchive.Unpack(zip, id => id == entryId ? target : null, 1024 * 1024, unknownIsError: true);

        Assert.Equal("прогресс игрока", File.ReadAllText(Path.Combine(target, "Сохранение 1.sav")));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(Path.Combine(target, "sub", "data.bin")));
        Assert.False(Directory.Exists(Path.Combine(target, "Logs")));
        Assert.False(File.Exists(Path.Combine(target, "crash.dmp")));
        Assert.Equal(new SavePackResult(Encoding.UTF8.GetByteCount("прогресс игрока") + 5, 2), packed);
        Assert.Equal(new SaveUnpackResult(packed.Bytes, packed.Files), unpacked);
    }

    [Fact]
    public void Pack_includes_hidden_files()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, ".hidden"), "x");
        var entryId = Guid.NewGuid();

        using var zip = new MemoryStream();
        Assert.Equal(new SavePackResult(1, 1), SaveArchive.Pack(zip, [(entryId, source)], []));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("{id}/../../x")]
    [InlineData("{id}/./x")]
    [InlineData("{id}//x")]
    [InlineData("C:/x")]
    [InlineData("/{id}/x")]
    [InlineData(@"{id}\..\x")]
    [InlineData("{id}/con")]
    [InlineData("{id}/sub/NUL.txt")]
    [InlineData("{id}/a:b")]
    [InlineData("{ID}/x")]
    [InlineData("x")]
    public void Unpack_rejects_traversal_and_absolute(string name)
    {
        var entryId = Guid.NewGuid();
        var target = Path.Combine(_root, "dst");
        using var zip = Zip((name.Replace("{id}", entryId.ToString("D")).Replace("{ID}", entryId.ToString("D").ToUpperInvariant()), [1]));

        var error = Assert.Throws<SaveArchiveException>(() => SaveArchive.Unpack(zip, id => id == entryId ? target : null, 1024, unknownIsError: false));

        Assert.Equal("badArchive", error.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Unpack_not_a_zip_is_badArchive()
    {
        using var garbage = new MemoryStream(Encoding.ASCII.GetBytes("not a zip at all"));
        var error = Assert.Throws<SaveArchiveException>(() => SaveArchive.Unpack(garbage, _ => _root, 1024, unknownIsError: true));
        Assert.Equal("badArchive", error.Reason);
    }

    [Fact]
    public void Unpack_over_limit()
    {
        var entryId = Guid.NewGuid();
        var prefix = entryId.ToString("D");
        using var zip = Zip(($"{prefix}/small.sav", new byte[1000]), ($"{prefix}/deep/big.sav", new byte[2 * 1024 * 1024]));
        var target = Path.Combine(_root, "tmp", "upload");

        var error = Assert.Throws<SaveArchiveException>(() => SaveArchive.Unpack(zip, id => id == entryId ? Path.Combine(target, prefix) : null, 1024 * 1024, unknownIsError: true));

        Assert.Equal("overLimit", error.Reason);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Unpack_unknown_entry()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var target = Path.Combine(_root, "dst");
        byte[] Archive()
        {
            using var zip = Zip(($"{known:D}/a.sav", [1, 2]), ($"{unknown:D}/b.sav", [3]));
            return zip.ToArray();
        }

        using (var zip = new MemoryStream(Archive()))
        {
            var error = Assert.Throws<SaveArchiveException>(() => SaveArchive.Unpack(zip, id => id == known ? target : null, 1024, unknownIsError: true));
            Assert.Equal("badArchive", error.Reason);
            Assert.False(Directory.Exists(target));
        }

        using (var zip = new MemoryStream(Archive()))
        {
            Assert.Equal(new SaveUnpackResult(2, 1), SaveArchive.Unpack(zip, id => id == known ? target : null, 1024, unknownIsError: false));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(target, "a.sav")));
            Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Unpack_overwrites_existing_files()
    {
        var entryId = Guid.NewGuid();
        var target = Path.Combine(_root, "dst");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.sav"), "baseline");
        using var zip = Zip(($"{entryId:D}/a.sav", "player"u8.ToArray()));

        SaveArchive.Unpack(zip, _ => target, 1024, unknownIsError: true);

        Assert.Equal("player", File.ReadAllText(Path.Combine(target, "a.sav")));
    }

    private static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        zip.Position = 0;
        return zip;
    }
}
