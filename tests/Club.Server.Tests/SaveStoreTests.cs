using System.IO.Compression;
using Club.Saves;
using Club.Server.Saves;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Server.Tests;

/// <summary>
/// Файловое хранилище сохранений (<see cref="SaveStore"/>, spec 2026-11-05, п. 3.1 и 3.4): метка корня, текущая папка
/// игрока и предыдущая копия, временные папки приёма. Без базы — во временном каталоге.
/// </summary>
public sealed class SaveStoreTests : IDisposable
{
    private const string PlayerId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-savestore-{Guid.NewGuid():N}");

    public SaveStoreTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, SavesOptions.MarkerFile), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SaveStore Store(string? root = null) =>
        new(new SavesOptions { Enabled = true, Root = root ?? _root }, NullLogger<SaveStore>.Instance);

    /// <summary>Временная папка с одним файлом <c>&lt;entryId&gt;/version.txt</c> = <paramref name="version"/>.</summary>
    private static string Upload(SaveStore store, Guid entryId, string version)
    {
        var temp = store.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp, entryId.ToString("D")));
        File.WriteAllText(Path.Combine(temp, entryId.ToString("D"), "version.txt"), version);
        return temp;
    }

    private static string Version(string directory, Guid entryId) =>
        File.ReadAllText(Path.Combine(directory, entryId.ToString("D"), "version.txt"));

    [Fact]
    public void Unavailable_without_marker()
    {
        Assert.True(Store().Available);

        // Корень есть, метки нет — диск не смонтирован, пишем мимо него на системный диск ВМ: нельзя.
        File.Delete(Path.Combine(_root, SavesOptions.MarkerFile));
        Assert.False(Store().Available);

        Assert.False(Store(Path.Combine(_root, "missing")).Available);
    }

    [Fact]
    public void Promote_keeps_previous()
    {
        var store = Store();
        var entry = Guid.NewGuid();
        Assert.Null(store.CurrentDirectory(PlayerId));

        var first = Upload(store, entry, "1");
        Assert.StartsWith(Path.Combine(_root, ".tmp") + Path.DirectorySeparatorChar, first);
        store.Promote(PlayerId, first);
        Assert.False(Directory.Exists(first));
        Assert.Equal(Path.Combine(_root, PlayerId), store.CurrentDirectory(PlayerId));
        Assert.Equal("1", Version(store.CurrentDirectory(PlayerId)!, entry));
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId + ".prev")));

        store.Promote(PlayerId, Upload(store, entry, "2"));
        Assert.Equal("2", Version(Path.Combine(_root, PlayerId), entry));
        Assert.Equal("1", Version(Path.Combine(_root, PlayerId + ".prev"), entry));

        // Третья версия вытесняет первую: хранятся только текущая и предыдущая.
        store.Promote(PlayerId, Upload(store, entry, "3"));
        Assert.Equal("3", Version(Path.Combine(_root, PlayerId), entry));
        Assert.Equal("2", Version(Path.Combine(_root, PlayerId + ".prev"), entry));
        Assert.Equal(
            [".club-saves", ".tmp", PlayerId, PlayerId + ".prev"],
            Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));
    }

    [Fact]
    public void Current_falls_back_to_prev()
    {
        var store = Store();
        var entry = Guid.NewGuid();
        store.Promote(PlayerId, Upload(store, entry, "1"));
        store.Promote(PlayerId, Upload(store, entry, "2"));

        // Сбой между двумя переименованиями: текущей нет, есть .prev — её и выдаём.
        Directory.Delete(Path.Combine(_root, PlayerId), recursive: true);
        Assert.Equal(Path.Combine(_root, PlayerId + ".prev"), store.CurrentDirectory(PlayerId));
        Assert.Equal("1", Version(store.CurrentDirectory(PlayerId)!, entry));
    }

    [Fact]
    public void Promote_after_crash_keeps_prev()
    {
        var store = Store();
        var entry = Guid.NewGuid();
        store.Promote(PlayerId, Upload(store, entry, "1"));
        store.Promote(PlayerId, Upload(store, entry, "2"));
        Directory.Move(Path.Combine(_root, PlayerId), Path.Combine(_root, ".tmp", "lost"));

        // Текущей нет — .prev и есть последняя хорошая версия: её не удаляем, она остаётся предыдущей.
        store.Promote(PlayerId, Upload(store, entry, "3"));
        Assert.Equal("3", Version(Path.Combine(_root, PlayerId), entry));
        Assert.Equal("1", Version(Path.Combine(_root, PlayerId + ".prev"), entry));
    }

    [Fact]
    public void Delete_removes_both()
    {
        var store = Store();
        var entry = Guid.NewGuid();
        store.Promote(PlayerId, Upload(store, entry, "1"));
        store.Promote(PlayerId, Upload(store, entry, "2"));
        var other = "abc-1";
        store.Promote(other, Upload(store, entry, "x"));
        File.SetAttributes(Path.Combine(_root, PlayerId, entry.ToString("D"), "version.txt"), FileAttributes.ReadOnly);

        store.Delete(PlayerId);
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
        Assert.False(Directory.Exists(Path.Combine(_root, PlayerId + ".prev")));
        Assert.Null(store.CurrentDirectory(PlayerId));
        Assert.Equal("x", Version(store.CurrentDirectory(other)!, entry));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));

        // Удалять нечего — не ошибка.
        store.Delete(PlayerId);
    }

    [Fact]
    public void Delete_hides_both_even_when_files_stick()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = Store();
        var entry = Guid.NewGuid();
        store.Promote(PlayerId, Upload(store, entry, "1"));
        store.Promote(PlayerId, Upload(store, entry, "2"));
        // Папка без права записи: файл в ней не удалить (сбой посреди рекурсивного удаления).
        var stuck = Path.Combine(_root, PlayerId, entry.ToString("D"));
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            store.Delete(PlayerId);

            // Наполовину удалённая папка не выдаётся как сохранение игрока, и .prev не всплывает вместо неё.
            Assert.False(Directory.Exists(Path.Combine(_root, PlayerId)));
            Assert.False(Directory.Exists(Path.Combine(_root, PlayerId + ".prev")));
            Assert.Null(store.CurrentDirectory(PlayerId));
        }
        finally
        {
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // Остаток лежит в .tmp — его убирает очистка при старте.
        store.CleanTemp();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));
    }

    [Fact]
    public void CleanTemp_empties_tmp()
    {
        var store = Store();
        // До первого приёма .tmp ещё нет.
        store.CleanTemp();

        var entry = Guid.NewGuid();
        store.Promote(PlayerId, Upload(store, entry, "1"));
        Upload(store, entry, "half-written");
        File.WriteAllText(Path.Combine(_root, ".tmp", $"{Guid.NewGuid()}.zip"), "zip");

        store.CleanTemp();
        Assert.True(Directory.Exists(Path.Combine(_root, ".tmp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".tmp")));
        Assert.Equal("1", Version(store.CurrentDirectory(PlayerId)!, entry));
        Assert.True(File.Exists(Path.Combine(_root, SavesOptions.MarkerFile)));
    }

    [Fact]
    public void Measure_and_WriteZip_cover_every_entry()
    {
        var store = Store();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var temp = store.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp, first.ToString("D"), "Игры"));
        File.WriteAllText(Path.Combine(temp, first.ToString("D"), "Игры", "Сохранение 1.sav"), "abc");
        // Файлы с точкой в начале на Linux — «скрытые»: считаются и упаковываются, как все.
        File.WriteAllText(Path.Combine(temp, first.ToString("D"), ".hidden"), "12345");
        Directory.CreateDirectory(Path.Combine(temp, second.ToString("D")));
        File.WriteAllText(Path.Combine(temp, second.ToString("D"), "Logs.txt"), "1");
        store.Promote(PlayerId, temp);
        var current = store.CurrentDirectory(PlayerId)!;

        Assert.Equal((9L, 3), store.Measure(current));
        Assert.Equal((0L, 0), store.Measure(Path.Combine(_root, "missing")));

        using var zip = new MemoryStream();
        store.WriteZip(current, zip);
        Assert.True(zip.CanWrite);
        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(
                new[] { $"{first:D}/.hidden", $"{first:D}/Игры/Сохранение 1.sav", $"{second:D}/Logs.txt" }.Order(StringComparer.Ordinal),
                archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        // Тот же архив принимает распаковка (как на сервере, со списком записей).
        zip.Position = 0;
        var target = Path.Combine(_root, "unpacked");
        var result = SaveArchive.Unpack(zip, id => id == first || id == second ? Path.Combine(target, id.ToString("D")) : null, 1024, unknownIsError: true);
        Assert.Equal(new SaveUnpackResult(9, 3), result);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(target, first.ToString("D"), "Игры", "Сохранение 1.sav")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("ABC")]
    [InlineData("a/b")]
    [InlineData(".tmp")]
    public void Rejects_not_normalized_player_id(string playerId)
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.CurrentDirectory(playerId));
        Assert.Throws<ArgumentException>(() => store.Delete(playerId));
        Assert.Throws<ArgumentException>(() => store.Promote(playerId, store.NewTempDirectory()));
    }

    [Fact]
    public void Promote_accepts_only_own_temp_directory()
    {
        var store = Store();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Assert.Throws<ArgumentException>(() => store.Promote(PlayerId, outside));
        Assert.Throws<ArgumentException>(() => store.Promote(PlayerId, Path.Combine(_root, ".tmp")));
        Assert.True(Directory.Exists(outside));
        Assert.Null(store.CurrentDirectory(PlayerId));
    }
}
