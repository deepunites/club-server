using System.Security.Cryptography;
using Club.Server.Data;
using Club.Server.HelperUpdates;
using Club.Server.Imaging;
using Club.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>
/// Сборки помощника в <c>&lt;PxeRoot&gt;/tools</c> (<see cref="HelperBuilds"/>, spec 2026-11-06, п. 3.1) и таблица
/// поручений <c>helper_updates</c> (M0013, п. 3.2). Файлы — во временном каталоге, таблица — во временной базе.
/// </summary>
public sealed class HelperBuildsTests(ServerFixture server) : IClassFixture<ServerFixture>, IDisposable
{
    private readonly string _pxeRoot = Path.Combine(Path.GetTempPath(), $"club-helperbuilds-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));

    private string Tools => Path.Combine(_pxeRoot, "tools");

    public void Dispose()
    {
        if (Directory.Exists(_pxeRoot))
        {
            Directory.Delete(_pxeRoot, recursive: true);
        }
    }

    private HelperBuilds Builds(HelperUpdateOptions? options = null) =>
        new(options ?? new HelperUpdateOptions(), new ImagingOptions { PxeRoot = _pxeRoot }, _clock, NullLogger<HelperBuilds>.Instance);

    /// <summary>Файл в <c>tools</c> с содержимым <paramref name="content"/>, изменён <paramref name="age"/> назад (по умолчанию — 10 минут).</summary>
    private string Put(string name, string content, TimeSpan? age = null)
    {
        Directory.CreateDirectory(Tools);
        var path = Path.Combine(Tools, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (_clock.GetUtcNow() - (age ?? TimeSpan.FromMinutes(10))).UtcDateTime);
        return path;
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void List_TakesOnlyNamePattern_AndComputesSha()
    {
        Put("ClubDisklessHelper-1.8.0.exe", "build 1.8.0");
        Put("ClubDisklessHelper-1.8.0.exe.tmp", "copy in progress");
        Put("install-helper-1.8.0.ps1", "script");
        Put("clubdisklesshelper-1.8.1.exe", "wrong case");
        Put("ClubDisklessHelper-1.8.exe", "two numbers");
        Directory.CreateDirectory(Path.Combine(Tools, "ClubDisklessHelper-1.9.0.exe"));

        var build = Assert.Single(Builds().List());
        Assert.Equal("ClubDisklessHelper-1.8.0.exe", build.File);
        Assert.Equal("1.8.0", build.Version);
        Assert.Equal("build 1.8.0".Length, build.Size);
        Assert.Equal(Sha("build 1.8.0"), build.Sha256);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromMinutes(10), build.ModifiedAt);
        Assert.True(build.Updatable);

        Assert.Equal(build, Builds().Find("ClubDisklessHelper-1.8.0.exe"));
        Assert.Null(Builds().Find("ClubDisklessHelper-1.8.0.exe.tmp"));
        Assert.Null(Builds().Find("install-helper-1.8.0.ps1"));
        Assert.Null(Builds().Find("../tools/ClubDisklessHelper-1.8.0.exe"));
        Assert.Null(Builds().Find("ClubDisklessHelper-1.8.5.exe"));
    }

    [Fact]
    public void List_WithoutToolsDirectory_IsEmpty()
    {
        Assert.Empty(Builds().List());
        Assert.Null(Builds().Find("ClubDisklessHelper-1.8.0.exe"));
    }

    [Fact]
    public void ToolsDir_OverridesPxeRoot()
    {
        var other = Path.Combine(_pxeRoot, "elsewhere");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "ClubDisklessHelper-1.8.0.exe"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(other, "ClubDisklessHelper-1.8.0.exe"), (_clock.GetUtcNow() - TimeSpan.FromHours(1)).UtcDateTime);
        Put("ClubDisklessHelper-1.9.0.exe", "y");

        var build = Assert.Single(Builds(new HelperUpdateOptions { ToolsDir = other }).List());
        Assert.Equal("ClubDisklessHelper-1.8.0.exe", build.File);
    }

    [Fact]
    public void Updatable_FalseBelow180()
    {
        Put("ClubDisklessHelper-1.7.0.exe", "old");
        Put("ClubDisklessHelper-1.8.0.exe", "first");
        Put("ClubDisklessHelper-1.10.0.exe", "newer than 1.9");
        // Число вне Int32 — Version.TryParse не разбирает: файл пропущен, остальные видны.
        Put("ClubDisklessHelper-99999999999.0.0.exe", "overflow");

        var builds = Builds().List();
        Assert.Equal(["ClubDisklessHelper-1.10.0.exe", "ClubDisklessHelper-1.8.0.exe", "ClubDisklessHelper-1.7.0.exe"], builds.Select(b => b.File));
        Assert.Equal([true, true, false], builds.Select(b => b.Updatable));
        Assert.False(Builds().Find("ClubDisklessHelper-1.7.0.exe")!.Updatable);

        // Порог — из настроек.
        Assert.True(Builds(new HelperUpdateOptions { MinTargetVersion = "1.7.0" }).Find("ClubDisklessHelper-1.7.0.exe")!.Updatable);
        Assert.False(Builds(new HelperUpdateOptions { MinTargetVersion = "1.9.0" }).Find("ClubDisklessHelper-1.8.0.exe")!.Updatable);
    }

    [Fact]
    public void Find_IgnoresFileYoungerThanMinAge()
    {
        // Администратор копирует под временным именем и переименовывает; файл моложе 60 с ещё может дописываться.
        Put("ClubDisklessHelper-1.8.0.exe", "fresh", age: TimeSpan.FromSeconds(30));
        var builds = Builds();
        Assert.Null(builds.Find("ClubDisklessHelper-1.8.0.exe"));
        Assert.Empty(builds.List());

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(Sha("fresh"), builds.Find("ClubDisklessHelper-1.8.0.exe")!.Sha256);
        Assert.Single(builds.List());

        Assert.Null(Builds(new HelperUpdateOptions { MinFileAgeSec = 120 }).Find("ClubDisklessHelper-1.8.0.exe"));
    }

    [Fact]
    public void List_RecomputesShaWhenFileChanges()
    {
        var path = Put("ClubDisklessHelper-1.8.0.exe", "first build");
        var builds = Builds();
        Assert.Equal(Sha("first build"), builds.Find("ClubDisklessHelper-1.8.0.exe")!.Sha256);

        // Тот же размер, другое время изменения — сумма считается заново (кэш — по размеру и времени).
        File.WriteAllText(path, "other build");
        File.SetLastWriteTimeUtc(path, (_clock.GetUtcNow() - TimeSpan.FromMinutes(5)).UtcDateTime);
        var changed = Assert.Single(builds.List());
        Assert.Equal(Sha("other build"), changed.Sha256);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromMinutes(5), changed.ModifiedAt);

        // Другой размер — тоже.
        File.WriteAllText(path, "a longer third build");
        File.SetLastWriteTimeUtc(path, (_clock.GetUtcNow() - TimeSpan.FromMinutes(5)).UtcDateTime);
        Assert.Equal(Sha("a longer third build"), builds.Find("ClubDisklessHelper-1.8.0.exe")!.Sha256);
        Assert.Equal("a longer third build".Length, builds.Find("ClubDisklessHelper-1.8.0.exe")!.Size);

        // Файл удалили — его нет, исключения нет.
        File.Delete(path);
        Assert.Empty(builds.List());
        Assert.Null(builds.Find("ClubDisklessHelper-1.8.0.exe"));
    }

    [Fact]
    public void List_SkipsUnreadableFile()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // права файлов — Unix; root читает файл и без прав
        }

        var locked = Put("ClubDisklessHelper-1.8.0.exe", "no access");
        Put("ClubDisklessHelper-1.8.1.exe", "readable");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var build = Assert.Single(Builds().List());
            Assert.Equal("ClubDisklessHelper-1.8.1.exe", build.File);
            Assert.Null(Builds().Find("ClubDisklessHelper-1.8.0.exe"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Options_Validate_RejectsBrokenSettings()
    {
        new HelperUpdateOptions().Validate();
        new HelperUpdateOptions { MinFileAgeSec = 0, MinTargetVersion = "1.9.2" }.Validate();

        // Кривой порог молча сделал бы все сборки «только вручную», MaxActive = 0 — встала бы очередь.
        foreach (var broken in new[]
        {
            new HelperUpdateOptions { MinTargetVersion = "1.8" },
            new HelperUpdateOptions { MinTargetVersion = "latest" },
            new HelperUpdateOptions { MaxActive = 0 },
            new HelperUpdateOptions { WorkerIntervalSec = 0 },
            new HelperUpdateOptions { SilentMinutes = 0 },
            new HelperUpdateOptions { WaitingMinutes = 0 },
            new HelperUpdateOptions { MinFileAgeSec = -1 },
        })
        {
            Assert.Throws<InvalidOperationException>(broken.Validate);
        }
    }

    [Fact]
    public async Task Migration_CreatesHelperUpdates()
    {
        var machine = await server.Services.GetRequiredService<MachineRepository>().CreateAsync(
            new MachineRegistration(Guid.NewGuid().ToString("N"), "PC-HU", ["aa:bb:cc:dd:ee:01"], "10.0.0.3", "1.7.0", null), approved: true);
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();

        // Сборка помощника из отчёта: столбец есть, пустой у нового ПК.
        Assert.Null(await c.ExecuteScalarAsync<string?>("SELECT helper_build FROM machines WHERE id = @id", new { id = machine.Id }));

        const string insert = """
            INSERT INTO helper_updates (id, machine_id, file, version, sha256, size, state, progress, reason, requested_by)
            VALUES (@id, @machine, 'ClubDisklessHelper-1.8.0.exe', '1.8.0', @sha, 100, @state, @progress, @reason, 'panel')
            """;
        var sha = new string('a', 64);
        await c.ExecuteAsync(insert, new { id = Guid.NewGuid(), machine = machine.Id, sha, state = "done", progress = (string?)null, reason = (string?)null });
        await c.ExecuteAsync(insert, new { id = Guid.NewGuid(), machine = machine.Id, sha, state = "active", progress = "waiting", reason = "session" });

        // Второе незавершённое поручение на тот же ПК — нарушение уникальности; завершённых — сколько угодно.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            c.ExecuteAsync(insert, new { id = Guid.NewGuid(), machine = machine.Id, sha, state = "queued", progress = (string?)null, reason = (string?)null }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("helper_updates_open", duplicate.ConstraintName);
        await c.ExecuteAsync(insert, new { id = Guid.NewGuid(), machine = machine.Id, sha, state = "failed", progress = (string?)null, reason = (string?)null });

        foreach (var (state, progress, reason) in new[] { ("bogus", (string?)null, (string?)null), ("failed", "bogus", null), ("failed", null, "bogus") })
        {
            var check = await Assert.ThrowsAsync<PostgresException>(() =>
                c.ExecuteAsync(insert, new { id = Guid.NewGuid(), machine = machine.Id, sha, state, progress, reason }));
            Assert.Equal(PostgresErrorCodes.CheckViolation, check.SqlState);
        }

        var row = await c.QuerySingleAsync<(DateTime CreatedAt, DateTime UpdatedAt)>(
            "SELECT created_at, updated_at FROM helper_updates WHERE state = 'active' AND machine_id = @id", new { id = machine.Id });
        Assert.NotEqual(default, row.CreatedAt);
        Assert.NotEqual(default, row.UpdatedAt);

        // Поручения удаляются вместе с ПК.
        await c.ExecuteAsync("DELETE FROM machines WHERE id = @id", new { id = machine.Id });
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM helper_updates WHERE machine_id = @id", new { id = machine.Id }));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
