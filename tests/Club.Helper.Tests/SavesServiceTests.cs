using System.IO.Compression;
using System.Security.Cryptography;
using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SavesOptions = Club.Server.Saves.SavesOptions;
using SavesRepository = Club.Server.Saves.SavesRepository;
using ServerSaveEntry = Club.Server.Saves.SaveEntry;
using ServerSaveSettings = Club.Server.Saves.SaveSettings;

namespace Club.Helper.Tests;

/// <summary>
/// <see cref="SavesService"/> (spec 2026-11-05, п. 4.3 и 4.4) против настоящего сервера клуба: выдача и сохранение между
/// ПК, очистка папок, исходное содержимое папок на H:, отказы (мастер, профиль, сервер недоступен, лимит). Каждый «ПК» —
/// свои каталоги профиля, H: и состояния помощника и своя машина на сервере. Своя база и свой корень сохранений на тест.
/// </summary>
public sealed class SavesServiceTests : IAsyncLifetime
{
    private const string PlayerId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string OtherPlayer = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-saves-service-{Guid.NewGuid():N}");
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"club-saves-service-work-{Guid.NewGuid():N}");
    private readonly ServerFixture _server = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_root, SavesOptions.MarkerFile), "");
        _server.Settings["Saves:Enabled"] = "true";
        _server.Settings["Saves:Root"] = _root;
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        foreach (var directory in new[] { _root, _work })
        {
            if (Directory.Exists(directory))
            {
                MakeWritable(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private SavesRepository Repository => _server.Services.GetRequiredService<SavesRepository>();

    private Task<ServerSaveEntry> EntryAsync(string path) => Repository.CreateEntryAsync(Path.GetFileName(path.Replace('\\', '/')), path, enabled: true);

    private async Task<Club.Server.Saves.PlayerSaveRow?> PlayerAsync(string playerId) =>
        (await Repository.PlayersAsync(playerId, 1, 50)).Items.SingleOrDefault();

    private static LibraryMount[] H(string version) => [new('H', version, true)];

    /// <summary>Один ПК клуба: профиль игрока по <see cref="Sid"/>, H: и папка состояния помощника — во временных каталогах.</summary>
    private sealed class Pc
    {
        private readonly SavesServiceTests _test;

        public Pc(SavesServiceTests test, string name, HttpClient? transfers = null)
        {
            _test = test;
            var directory = Path.Combine(test._work, name);
            Profile = Path.Combine(directory, "Users", "player");
            Library = Path.Combine(directory, "H");
            State = Path.Combine(directory, "ProgramData", "ClubDiskless");
            Directory.CreateDirectory(Profile);
            Directory.CreateDirectory(Library);
            Api = new DisklessApiClient(test._server.CreateClient(), new HelperOptions { ClubKey = ServerFixture.ClubKey }, new InMemoryCredentialStore(),
                new FakeIdentity($"hwid-{name}-{Guid.NewGuid():N}"), transfers);
            Restart();
        }

        public string Profile { get; }

        public string Library { get; }

        public string State { get; }

        public DisklessApiClient Api { get; }

        public bool Master { get; set; }

        public ManualClock Clock { get; } = new();

        public ListLogger<SavesService> Log { get; private set; } = null!;

        public SavesService Service { get; private set; } = null!;

        /// <summary>Перезапуск службы помощника: новый SavesService, те же каталоги и машина.</summary>
        public void Restart()
        {
            var profiles = new FakeProfiles();
            profiles.Paths[Sid] = Profile;
            var drives = new FakeDrives();
            drives.Roots['H'] = Library;
            Log = new ListLogger<SavesService>();
            Service = new SavesService(Api, profiles, drives, () => Master, State, Clock, Log);
        }

        /// <summary>Путь в профиле: части через <c>\</c>, как в шаблоне.</summary>
        public string InProfile(string relative) => Path.Combine(Profile, relative.Replace('\\', Path.DirectorySeparatorChar));

        public string OnH(string relative) => Path.Combine(Library, relative.Replace('\\', Path.DirectorySeparatorChar));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string[] Listing(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Select(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)]
            : [];

    private static void MakeWritable(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void Ok(SavesResult result) => Assert.True(result.Ok, $"{result.Error}: {result.Message}");

    [Fact]
    public async Task Attach_restores_player_files_into_profile_and_library()
    {
        var game = await EntryAsync(@"%LOCALAPPDATA%\Игра\Saved");
        var library = await EntryAsync(@"H:\Игры\Сохранения");
        var a = new Pc(this, "A");
        var b = new Pc(this, "B");
        foreach (var pc in new[] { a, b })
        {
            Write(pc.OnH(@"Игры\Сохранения\default.cfg"), "published");
            await pc.Service.OnTickAsync(H("v1"), CancellationToken.None);
        }

        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        var save = RandomNumberGenerator.GetBytes(300_000);
        Directory.CreateDirectory(a.InProfile(@"AppData\Local\Игра\Saved\Профиль"));
        File.WriteAllBytes(a.InProfile(@"AppData\Local\Игра\Saved\Профиль\Сохранение 1.sav"), save);
        Write(a.OnH(@"Игры\Сохранения\slot1.sav"), "mine");
        Write(a.OnH(@"Игры\Сохранения\default.cfg"), "changed");
        Write(a.InProfile(@"AppData\Local\Игра\Saved\Logs\game.log"), "excluded");
        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));

        // ID от шелла в другом регистре — тот же игрок.
        Ok(await b.Service.AttachAsync(PlayerId.ToUpperInvariant(), Sid, CancellationToken.None));
        Assert.Equal(save, File.ReadAllBytes(b.InProfile(@"AppData\Local\Игра\Saved\Профиль\Сохранение 1.sav")));
        Assert.Equal(["Профиль", "Профиль/Сохранение 1.sav"], Listing(b.InProfile(@"AppData\Local\Игра\Saved")));
        Assert.Equal("mine", File.ReadAllText(b.OnH(@"Игры\Сохранения\slot1.sav")));
        Assert.Equal("changed", File.ReadAllText(b.OnH(@"Игры\Сохранения\default.cfg")));
        Assert.Equal(b.Api.MachineId, await Repository.LeaseHolderAsync(PlayerId));
        Assert.Contains(game.Id, Directory.EnumerateDirectories(Path.Combine(_root, PlayerId)).Select(d => Guid.Parse(Path.GetFileName(d))));
        Assert.Contains(library.Id, Directory.EnumerateDirectories(Path.Combine(_root, PlayerId)).Select(d => Guid.Parse(Path.GetFileName(d))));

        // Журнал: по строке на команду — ID игрока, размер, результат.
        var detach = Assert.Single(a.Log.Entries, e => e.Message.Contains("detach"));
        Assert.Equal(LogLevel.Information, detach.Level);
        Assert.Contains(PlayerId, detach.Message);
        Assert.Contains($"{save.Length + "mine".Length + "changed".Length} bytes", detach.Message);
        Assert.Contains(PlayerId, Assert.Single(b.Log.Entries, e => e.Message.Contains("attach")).Message);
    }

    [Fact]
    public async Task Attach_clears_previous_player_files()
    {
        await EntryAsync(@"%APPDATA%\Game");
        await EntryAsync(@"H:\Game\Saves");
        var pc = new Pc(this, "A");
        Write(pc.OnH(@"Game\Saves\default.cfg"), "published");
        Write(pc.OnH(@"Game\Saves\Sub\level.dat"), "published level");
        await pc.Service.OnTickAsync(H("v1"), CancellationToken.None);

        // Прошлый игрок (без выдачи: сервер был недоступен) оставил свои файлы.
        Write(pc.InProfile(@"AppData\Roaming\Game\old.sav"), "previous player");
        Write(pc.InProfile(@"AppData\Roaming\Game\Deep\nick.txt"), "previous nick");
        Write(pc.OnH(@"Game\Saves\old.sav"), "previous player");
        Write(pc.OnH(@"Game\Saves\default.cfg"), "previous settings");
        Write(pc.InProfile(@"AppData\Roaming\Other\keep.txt"), "not ours");

        Ok(await pc.Service.AttachAsync(OtherPlayer, Sid, CancellationToken.None));
        Assert.True(Directory.Exists(pc.InProfile(@"AppData\Roaming\Game")));
        Assert.Empty(Listing(pc.InProfile(@"AppData\Roaming\Game")));
        Assert.Equal(["Sub", "Sub/level.dat", "default.cfg"], Listing(pc.OnH(@"Game\Saves")));
        Assert.Equal("published", File.ReadAllText(pc.OnH(@"Game\Saves\default.cfg")));
        Assert.Equal("published level", File.ReadAllText(pc.OnH(@"Game\Saves\Sub\level.dat")));
        Assert.Equal("not ours", File.ReadAllText(pc.InProfile(@"AppData\Roaming\Other\keep.txt")));
    }

    [Fact]
    public async Task Clear_removes_readonly_and_hidden()
    {
        await EntryAsync(@"%DOCUMENTS%\My Games\Game");
        var pc = new Pc(this, "A");
        var folder = pc.InProfile(@"Documents\My Games\Game");
        Write(Path.Combine(folder, "readonly.sav"), "ro");
        File.SetAttributes(Path.Combine(folder, "readonly.sav"), FileAttributes.ReadOnly);
        Write(Path.Combine(folder, ".hidden"), "hidden");
        File.SetAttributes(Path.Combine(folder, ".hidden"), FileAttributes.Hidden);
        Write(Path.Combine(folder, "Locked", "inner.sav"), "inner");
        File.SetAttributes(Path.Combine(folder, "Locked", "inner.sav"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(folder, "Locked"), FileAttributes.Directory | FileAttributes.ReadOnly);

        // Ссылка из папки наружу: удаляется сама ссылка, не то, куда она ведёт.
        var outside = Path.Combine(pc.Profile, "Outside");
        Write(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(folder, "Link"), outside);

        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Empty(Listing(folder));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task Link_above_folder_is_not_followed()
    {
        await EntryAsync(@"%LOCALAPPDATA%\Game\Saved");
        var pc = new Pc(this, "A");

        // Папка выше папки записи — ссылка наружу (её сделал игрок): выдача там ничего не удаляет.
        var outside = Path.Combine(pc.Profile, "..", "Outside");
        Write(Path.Combine(outside, "Saved", "keep.txt"), "keep");
        Directory.CreateDirectory(pc.InProfile(@"AppData\Local"));
        Directory.CreateSymbolicLink(pc.InProfile(@"AppData\Local\Game"), outside);
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "Saved", "keep.txt")));
        Directory.Delete(pc.InProfile(@"AppData\Local\Game"));

        // Ссылку сделали посреди сеанса: сохранение не забирает то, куда она ведёт.
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(pc.InProfile(@"AppData\Local\Game\Saved\save.dat"), "mine");
        Directory.Delete(pc.InProfile(@"AppData\Local\Game"), recursive: true);
        Write(Path.Combine(outside, "Saved", "secret.txt"), "not the player's");
        Directory.CreateSymbolicLink(pc.InProfile(@"AppData\Local\Game"), outside);
        await pc.Service.DetachAsync(PlayerId, CancellationToken.None);
        var stored = Path.Combine(_root, PlayerId);
        Assert.DoesNotContain(Directory.Exists(stored) ? Directory.EnumerateFiles(stored, "*", SearchOption.AllDirectories) : [],
            f => Path.GetFileName(f) is "secret.txt" or "keep.txt");
    }

    [Fact]
    public async Task Skipped_library_folder_keeps_server_copy()
    {
        await EntryAsync(@"%APPDATA%\Game");
        await EntryAsync(@"H:\Game\Saves");
        var a = new Pc(this, "A");
        await a.Service.OnTickAsync(H("v1"), CancellationToken.None);
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(a.OnH(@"Game\Saves\slot1.sav"), "library progress");
        Write(a.InProfile(@"AppData\Roaming\Game\save.dat"), "profile progress");
        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));

        // На ПК B библиотека H: не подключена: папку на H: не трогаем, а её копию на сервере не теряем.
        var b = new Pc(this, "B");
        await b.Service.OnTickAsync([new('H', "v1", false)], CancellationToken.None);
        Ok(await b.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Empty(Listing(b.OnH("Game")));
        Write(b.InProfile(@"AppData\Roaming\Game\save.dat"), "profile progress 2");
        Ok(await b.Service.DetachAsync(PlayerId, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(b.State, "saves-carry")));

        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal("library progress", File.ReadAllText(a.OnH(@"Game\Saves\slot1.sav")));
        Assert.Equal("profile progress 2", File.ReadAllText(a.InProfile(@"AppData\Roaming\Game\save.dat")));
    }

    [Fact]
    public async Task Missing_folder_saves_fine()
    {
        await EntryAsync(@"%APPDATA%\Played");
        await EntryAsync(@"%SAVEDGAMES%\Never Launched");
        var a = new Pc(this, "A");
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(a.InProfile(@"AppData\Roaming\Played\save.dat"), "progress");
        Assert.False(Directory.Exists(a.InProfile(@"Saved Games\Never Launched")));

        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));
        var player = await PlayerAsync(PlayerId);
        Assert.Equal((1, "progress".Length), (player!.FileCount, (int)player.SizeBytes));

        var b = new Pc(this, "B");
        Ok(await b.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal("progress", File.ReadAllText(b.InProfile(@"AppData\Roaming\Played\save.dat")));
        Assert.Empty(Listing(b.InProfile(@"Saved Games\Never Launched")));
    }

    [Fact]
    public async Task Unknown_entry_in_archive_is_skipped()
    {
        var kept = await EntryAsync(@"%APPDATA%\Kept");
        var removed = await EntryAsync(@"%APPDATA%\Removed");
        var a = new Pc(this, "A");
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(a.InProfile(@"AppData\Roaming\Kept\save.dat"), "kept");
        Write(a.InProfile(@"AppData\Roaming\Removed\save.dat"), "removed");
        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));

        // Запись удалили в панели, а в папке игрока на сервере она ещё есть: выдача её пропускает.
        Assert.True(await Repository.DeleteEntryAsync(removed.Id));
        var b = new Pc(this, "B");
        Write(b.InProfile(@"AppData\Roaming\Removed\local.dat"), "not in the list any more");
        Ok(await b.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal("kept", File.ReadAllText(b.InProfile(@"AppData\Roaming\Kept\save.dat")));
        Assert.Equal(["local.dat"], Listing(b.InProfile(@"AppData\Roaming\Removed")));
        Assert.True(Directory.Exists(Path.Combine(_root, PlayerId, removed.Id.ToString("D"))));

        // Сохранение — только папки из списка.
        Ok(await b.Service.DetachAsync(PlayerId, CancellationToken.None));
        Assert.Equal([kept.Id.ToString("D")], Directory.EnumerateDirectories(Path.Combine(_root, PlayerId)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Entry_removed_during_session_is_not_sent()
    {
        var kept = await EntryAsync(@"%APPDATA%\Kept");
        var removed = await EntryAsync(@"%APPDATA%\Removed");
        var pc = new Pc(this, "A");
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(pc.InProfile(@"AppData\Roaming\Kept\save.dat"), "kept");
        Write(pc.InProfile(@"AppData\Roaming\Removed\save.dat"), "removed");

        // Запись удалили посреди сеанса: сервер такой архив не принял бы (badArchive) — она не отправляется.
        Assert.True(await Repository.DeleteEntryAsync(removed.Id));
        Ok(await pc.Service.DetachAsync(PlayerId, CancellationToken.None));
        Assert.Equal([kept.Id.ToString("D")], Directory.EnumerateDirectories(Path.Combine(_root, PlayerId)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Master_active_refuses()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var pc = new Pc(this, "A") { Master = true };
        Write(pc.InProfile(@"AppData\Roaming\Game\local.sav"), "untouched");

        var attach = await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None);
        Assert.Equal((false, SavesErrors.MasterMode), (attach.Ok, attach.Error));
        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
        Assert.Equal("untouched", File.ReadAllText(pc.InProfile(@"AppData\Roaming\Game\local.sav")));
        Assert.Equal(SavesErrors.NotAttached, (await pc.Service.DetachAsync(PlayerId, CancellationToken.None)).Error);
        Assert.Null(await PlayerAsync(PlayerId));
    }

    [Fact]
    public async Task Unknown_sid_is_profileNotFound()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var pc = new Pc(this, "A");

        var attach = await pc.Service.AttachAsync(PlayerId, "S-1-5-21-1-2-3-9999", CancellationToken.None);
        Assert.Equal((false, SavesErrors.ProfileNotFound), (attach.Ok, attach.Error));
        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));
        Assert.Equal(LogLevel.Warning, Assert.Single(pc.Log.Entries).Level);
    }

    [Fact]
    public async Task Bad_player_id_is_badRequest()
    {
        var pc = new Pc(this, "A");

        Assert.Equal(SavesErrors.BadRequest, (await pc.Service.AttachAsync("../etc", Sid, CancellationToken.None)).Error);
        Assert.Equal(SavesErrors.BadRequest, (await pc.Service.DetachAsync("", CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Detach_without_attach_sends_nothing()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var pc = new Pc(this, "A");
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(pc.InProfile(@"AppData\Roaming\Game\save.dat"), "good progress");
        Ok(await pc.Service.DetachAsync(PlayerId, CancellationToken.None));
        var before = await PlayerAsync(PlayerId);

        // Служба перезапустилась (или выдачи не было): «выдано» только в памяти — ничего не отправляется.
        pc.Restart();
        File.Delete(pc.InProfile(@"AppData\Roaming\Game\save.dat"));
        var detach = await pc.Service.DetachAsync(PlayerId, CancellationToken.None);
        Assert.Equal((false, SavesErrors.NotAttached), (detach.Ok, detach.Error));
        Assert.Equal(before, await PlayerAsync(PlayerId));

        // Выдано другому игроку — тоже notAttached для этого.
        Ok(await pc.Service.AttachAsync(OtherPlayer, Sid, CancellationToken.None));
        Assert.Equal(SavesErrors.NotAttached, (await pc.Service.DetachAsync(PlayerId, CancellationToken.None)).Error);
        Assert.Equal(before, await PlayerAsync(PlayerId));
        Ok(await pc.Service.DetachAsync(OtherPlayer, CancellationToken.None));
    }

    [Fact]
    public async Task Server_down_at_attach_then_detach_is_notAttached()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var a = new Pc(this, "A");
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(a.InProfile(@"AppData\Roaming\Game\save.dat"), "good progress");
        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));
        var before = await PlayerAsync(PlayerId);

        var b = new Pc(this, "B", transfers: new HttpClient(new ServerDown()) { BaseAddress = new Uri("http://localhost/") });
        Write(b.InProfile(@"AppData\Roaming\Game\local.dat"), "previous player");
        var attach = await b.Service.AttachAsync(PlayerId, Sid, CancellationToken.None);
        Assert.Equal((false, SavesErrors.ServerUnavailable), (attach.Ok, attach.Error));
        Assert.Equal("previous player", File.ReadAllText(b.InProfile(@"AppData\Roaming\Game\local.dat")));

        // Игрок играл без сохранений: в конце сеанса пустые или чужие папки не затирают прошлую версию.
        Assert.Equal(SavesErrors.NotAttached, (await b.Service.DetachAsync(PlayerId, CancellationToken.None)).Error);
        Assert.Equal(before, await PlayerAsync(PlayerId));
    }

    [Fact]
    public async Task Detach_over_limit_keeps_server_copy()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var a = new Pc(this, "A");
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Write(a.InProfile(@"AppData\Roaming\Game\save.dat"), "good progress");
        Ok(await a.Service.DetachAsync(PlayerId, CancellationToken.None));
        var before = await PlayerAsync(PlayerId);

        await Repository.UpdateSettingsAsync(new ServerSaveSettings(1, ["Logs"]));
        Ok(await a.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        File.WriteAllBytes(a.InProfile(@"AppData\Roaming\Game\huge.dat"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        var detach = await a.Service.DetachAsync(PlayerId, CancellationToken.None);
        Assert.Equal((false, SavesErrors.OverLimit), (detach.Ok, detach.Error));
        Assert.Null(await Repository.LeaseHolderAsync(PlayerId));

        // Не отправлено, но в панели у игрока отметка overLimit (spec п. 6); прошлая версия и её размер те же.
        var after = await PlayerAsync(PlayerId);
        Assert.NotNull(after);
        Assert.Equal("overLimit", after.LastResult);
        Assert.Equal(before! with { LastResult = after.LastResult, LastResultAt = after.LastResultAt, LastError = after.LastError }, after);
        Assert.Equal(SavesErrors.NotAttached, (await a.Service.DetachAsync(PlayerId, CancellationToken.None)).Error);

        // Прошлая версия на сервере цела: другой ПК получает её.
        await Repository.UpdateSettingsAsync(new ServerSaveSettings(200, ["Logs"]));
        var b = new Pc(this, "B");
        Ok(await b.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal(["save.dat"], Listing(b.InProfile(@"AppData\Roaming\Game")));
    }

    [Fact]
    public async Task Disabled_server_is_disabled()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var pc = new Pc(this, "A");
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        File.Delete(Path.Combine(_root, SavesOptions.MarkerFile)); // диск сохранений отвалился посреди сеанса

        Assert.Equal(SavesErrors.Disabled, (await pc.Service.DetachAsync(PlayerId, CancellationToken.None)).Error);
        Assert.Equal(SavesErrors.Disabled, (await pc.Service.AttachAsync(OtherPlayer, Sid, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Busy_file_is_retried()
    {
        await EntryAsync(@"%APPDATA%\Game");
        var pc = new Pc(this, "A");
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        var path = pc.InProfile(@"AppData\Roaming\Game\save.dat");
        Write(path, "written by the game");

        // Игра держит файл все попытки: internal, ничего не отправлено, «выдано» остаётся.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var held = await pc.Service.DetachAsync(PlayerId, CancellationToken.None);
            Assert.Equal((false, SavesErrors.Internal), (held.Ok, held.Error));
        }

        Assert.Equal(SavesService.BusyRetries, pc.Log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("retrying")));
        Assert.Null(await PlayerAsync(PlayerId));

        // Игра ещё закрывается: первая попытка не открывает файл, следующая — уже да.
        var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Delay(TimeSpan.FromMilliseconds(500)).ContinueWith(_ => busy.Dispose(), TaskScheduler.Default);
        Ok(await pc.Service.DetachAsync(PlayerId, CancellationToken.None));
        await release;
        Assert.Equal("written by the game".Length, (await PlayerAsync(PlayerId))!.SizeBytes);
    }

    [Fact]
    public async Task Baseline_taken_once_per_library_version()
    {
        var entry = await EntryAsync(@"H:\Game\Saves");
        var pc = new Pc(this, "A");
        var cfg = pc.OnH(@"Game\Saves\default.cfg");
        Write(cfg, "v1 published");
        await pc.Service.OnTickAsync(H("v1"), CancellationToken.None);
        Assert.Equal("v1 published", BaselineText(pc, entry.Id, "default.cfg"));

        // Та же версия: копия не снимается заново, сколько бы ни прошло тактов и обновлений списка.
        Write(cfg, "changed by somebody");
        pc.Clock.Advance(SavesService.ConfigEvery);
        await pc.Service.OnTickAsync(H("v1"), CancellationToken.None);
        Assert.Equal("v1 published", BaselineText(pc, entry.Id, "default.cfg"));
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal("v1 published", File.ReadAllText(cfg));

        // Новая версия, пока выдано игроку, — копии нет; после сохранения — снимается.
        Write(cfg, "v2 published");
        await pc.Service.OnTickAsync(H("v2"), CancellationToken.None);
        Assert.Equal("v1 published", BaselineText(pc, entry.Id, "default.cfg"));
        Ok(await pc.Service.DetachAsync(PlayerId, CancellationToken.None));
        await pc.Service.OnTickAsync(H("v2"), CancellationToken.None);
        Assert.Equal("v2 published", BaselineText(pc, entry.Id, "default.cfg"));

        Write(cfg, "left by the player");
        Ok(await pc.Service.AttachAsync(OtherPlayer, Sid, CancellationToken.None));
        Assert.Equal("v2 published", File.ReadAllText(cfg));
    }

    [Fact]
    public async Task Baseline_over_limit_skips_entry()
    {
        await Repository.UpdateSettingsAsync(new ServerSaveSettings(1, ["Logs"]));
        var entry = await EntryAsync(@"H:\Game\Huge");
        var pc = new Pc(this, "A");
        Directory.CreateDirectory(pc.OnH(@"Game\Huge"));
        File.WriteAllBytes(pc.OnH(@"Game\Huge\data.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        await pc.Service.OnTickAsync(H("v1"), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(pc.State, "saves-baseline", $"{entry.Id:D}.zip")));
        Assert.Contains(pc.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(entry.Id.ToString("D")));

        // Запись пропускается на этом ПК: папку не трогаем.
        Ok(await pc.Service.AttachAsync(PlayerId, Sid, CancellationToken.None));
        Assert.Equal(["data.bin"], Listing(pc.OnH(@"Game\Huge")));
    }

    private static string BaselineText(Pc pc, Guid entryId, string file)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(pc.State, "saves-baseline", $"{entryId:D}.zip"));
        using var reader = new StreamReader(zip.GetEntry($"{entryId:D}/{file}")!.Open());
        return reader.ReadToEnd();
    }

    private sealed class ServerDown : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
