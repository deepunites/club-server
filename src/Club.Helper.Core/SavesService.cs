using System.Text.Json;
using Club.Saves;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Сохранения игрока на ПК (spec 2026-11-05, п. 4.3–4.5): по команде шелла раскладывает папки игрока с сервера клуба
/// (attach) и забирает их в конце сеанса (detach); в тактах помощника обновляет список папок и снимает исходное
/// содержимое папок на буквах библиотек (<see cref="OnTickAsync"/>).
/// attach: библиотека в режиме мастера — <c>masterMode</c>; профиль по SID (<paramref name="profiles"/>); сервер (аренда
/// «игрок X на этом ПК» и архив); для каждой папки из списка — очистка (с <c>ReadOnly</c>/<c>Hidden</c>), для G:/H: —
/// исходное содержимое, поверх — файлы игрока (записи архива, которых нет в списке, пропускаются). Ошибка после ответа
/// сервера — аренда снимается, <c>internal</c>.
/// detach: только если этому игроку выдано здесь (иначе <c>notAttached</c> и ничего не отправляется: пустые или чужие
/// папки не затирают прошлую версию на сервере); упаковка во временный файл без исключений из настроек, занятые файлы —
/// повтор <see cref="BusyRetries"/> раза через <see cref="BusyRetryDelay"/>; больше лимита — аренда снимается с отметкой
/// <c>overLimit</c> у игрока на сервере (для панели), <c>overLimit</c>; отправка.
/// «Выдано» — в памяти (spec п. 4.3): после перезапуска службы detach отвечает <c>notAttached</c>, на сервере остаётся
/// прошлая версия. Папки записей, пропущенных на этом ПК (библиотека не подключена, нет исходного содержимого и т. п.),
/// не трогаются, а их файлы из архива сервера ждут в <c>saves-carry</c> и уходят в detach как были: сервер заменяет папку
/// игрока целиком и иначе потерял бы их. Удаляется только содержимое папок записей списка; ссылки (symlink, junction)
/// внутри удаляются сами, не то, куда ведут, а папка записи, которая сама ссылка или лежит под ссылкой (до корня профиля
/// или буквы), пропускается — и при выдаче, и при сохранении. Команды и такт — по одной
/// (<see cref="SemaphoreSlim"/>): такт, пока идёт команда, пропускается и цикл помощника не ждёт.
/// </summary>
public sealed class SavesService(
    DisklessApiClient api, IProfileLocator profiles, IDriveRoots drives, Func<bool> masterActive, string stateDirectory, TimeProvider clock, ILogger logger)
{
    /// <summary>Как часто такт обновляет список папок с сервера (spec п. 3.3).</summary>
    public static readonly TimeSpan ConfigEvery = TimeSpan.FromMinutes(5);

    /// <summary>Сколько раз повторить упаковку, если файл занят другим процессом (spec п. 4.3).</summary>
    public const int BusyRetries = 3;

    public static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(1);

    private const long BytesPerMb = 1024 * 1024;

    private const int BufferSize = 81920;

    /// <summary>Атрибуты, которые снимаются перед удалением (только для чтения не даёт удалить файл).</summary>
    private const FileAttributes Protective = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    /// <summary>Содержимое одной папки, со скрытыми и системными, без захода в ссылки.</summary>
    private static readonly EnumerationOptions Children = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Исходное содержимое папок на буквах библиотек: <c>&lt;entryId&gt;.zip</c> и <c>&lt;entryId&gt;.json</c> (версия, путь).</summary>
    private readonly string _baselines = Path.Combine(stateDirectory, "saves-baseline");

    /// <summary>Временные архивы detach (удаляются при закрытии).</summary>
    private readonly string _temp = Path.Combine(stateDirectory, "saves-tmp");

    /// <summary>Файлы игрока из архива сервера для записей, пропущенных на этом ПК: <c>&lt;entryId&gt;\…</c>, до конца выдачи.</summary>
    private readonly string _carry = Path.Combine(stateDirectory, "saves-carry");

    /// <summary>Библиотеки ПК из последнего такта (attach берёт их отсюда).</summary>
    private volatile IReadOnlyList<LibraryMount> _libraries = [];

    private SavesConfig? _config;

    /// <summary>Когда (монотонная отметка) список папок получен; <c>null</c> — ещё не получен.</summary>
    private long? _configAt;

    /// <summary>О том, что список не получить, уже написано в журнал.</summary>
    private bool _configFailing;

    /// <summary>Кому выдано на этом ПК и какие папки разложены; <c>null</c> — никому.</summary>
    private Session? _attached;

    /// <summary>Папка записи списка на этом ПК: ID записи, путь и корень, под которым он (профиль, буква библиотеки).</summary>
    private sealed record Folder(Guid Id, string Directory, string Root);

    /// <summary>
    /// Выдача: кому, какие папки разложены (<paramref name="Folders"/>) и где файлы игрока для записей, пропущенных на этом
    /// ПК (<paramref name="Carried"/>, в <c>saves-carry</c>).
    /// </summary>
    private sealed record Session(string PlayerId, IReadOnlyList<Folder> Folders, IReadOnlyList<Folder> Carried);

    /// <summary>
    /// Состояние исходного содержимого записи: для какой версии библиотеки и какого шаблона снято;
    /// <paramref name="OverLimit"/> — больше лимита, запись на этом ПК пропускается до новой версии.
    /// </summary>
    private sealed record BaselineState(string Version, string Path, bool OverLimit);

    /// <summary>
    /// Выдача сохранений игрока: <paramref name="playerId"/> — ID аккаунта в кассе (регистр не важен),
    /// <paramref name="windowsUserSid"/> — пользователь Windows, в чей профиль раскладывать.
    /// </summary>
    public async Task<SavesResult> AttachAsync(string playerId, string windowsUserSid, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        var id = PlayerIds.Normalize(playerId);
        if (id is null || string.IsNullOrWhiteSpace(windowsUserSid))
        {
            return Logged("attach", id, (SavesResult.Fail(SavesErrors.BadRequest, "playerId and windowsUserSid are required"), 0, 0), started);
        }

        await _gate.WaitAsync(ct);
        try
        {
            return Logged("attach", id, await AttachLockedAsync(id, windowsUserSid, ct), started);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Сохранение в конце сеанса (шелл уже закрыл игры).</summary>
    public async Task<SavesResult> DetachAsync(string playerId, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        var id = PlayerIds.Normalize(playerId);
        if (id is null)
        {
            return Logged("detach", id, (SavesResult.Fail(SavesErrors.BadRequest, "playerId is required"), 0, 0), started);
        }

        await _gate.WaitAsync(ct);
        try
        {
            return Logged("detach", id, await DetachLockedAsync(id, ct), started);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Такт помощника: запоминает библиотеки ПК, раз в <see cref="ConfigEvery"/> обновляет список папок (не получилось —
    /// пробует в следующем такте) и, когда никому не выдано, снимает исходное содержимое папок на подключённых буквах
    /// библиотек — один раз на запись и версию библиотеки (spec п. 4.4). Идёт команда шелла — такт пропускается.
    /// </summary>
    public async Task OnTickAsync(IReadOnlyList<LibraryMount> libraries, CancellationToken ct)
    {
        _libraries = libraries;
        if (!await _gate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            if (_configAt is not { } at || clock.GetElapsedTime(at) >= ConfigEvery)
            {
                try
                {
                    _config = await api.GetSavesConfigAsync(ct);
                    _configAt = clock.GetTimestamp();
                    if (_configFailing)
                    {
                        logger.LogInformation("Saves config received again");
                        _configFailing = false;
                    }
                }
                catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
                {
                    if (!_configFailing)
                    {
                        logger.LogWarning("Saves config not received, retrying every tick: {Error}", ex.Message);
                        _configFailing = true;
                    }
                }
            }

            if (_config is { } config && _attached is null)
            {
                TakeBaselines(config, libraries);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(SavesResult Result, long Bytes, int Files)> AttachLockedAsync(string id, string sid, CancellationToken ct)
    {
        // Новая выдача: прежний игрок закончил (или detach не пришёл) — его «выдано» больше не действует.
        Forget();
        if (masterActive())
        {
            return (SavesResult.Fail(SavesErrors.MasterMode, "A library on this PC is in master mode"), 0, 0);
        }

        var profile = profiles.ProfilePath(sid);
        if (profile is null || !Directory.Exists(profile))
        {
            return (SavesResult.Fail(SavesErrors.ProfileNotFound, $"No Windows profile for SID {sid}"), 0, 0);
        }

        SavesAttach attach;
        try
        {
            attach = await api.AttachSavesAsync(id, ct);
        }
        catch (SavesRejectedException ex)
        {
            return (Rejected(ex), 0, 0);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            return (SavesResult.Fail(SavesErrors.ServerUnavailable, ex.Message), 0, 0);
        }

        await using (attach)
        {
            try
            {
                if (!attach.Config.Enabled)
                {
                    await ReleaseAsync(id);
                    return (SavesResult.Fail(SavesErrors.Disabled, "Player saves are disabled on the club server"), 0, 0);
                }

                DropCarry();
                var baselines = new List<(Folder Folder, string Zip)>();
                var skipped = new List<Guid>();
                var folders = Folders(attach.Config, profile, _libraries, baselines, skipped);
                var carried = skipped.Select(e => new Folder(e, Path.Combine(_carry, e.ToString("D")), _carry)).ToList();
                foreach (var folder in folders)
                {
                    Clear(folder.Directory);
                }

                foreach (var (folder, zip) in baselines)
                {
                    Restore(folder, zip);
                }

                var unpacked = new SaveUnpackResult(0, 0);
                if (attach.Archive is { } archive)
                {
                    var targets = folders.Concat(carried).ToDictionary(f => f.Id, f => f.Directory);
                    unpacked = SaveArchive.Unpack(archive, entryId => targets.GetValueOrDefault(entryId), LimitBytes(attach.Config.LimitMb),
                        unknownIsError: false);
                }

                _attached = new Session(id, folders, carried);
                return (SavesResult.Success, unpacked.Bytes, unpacked.Files);
            }
            catch (Exception ex)
            {
                // Аренда взята, а файлы игрока не разложены: снимаем её, в конце сеанса ничего не отправится.
                await ReleaseAsync(id);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                logger.LogError(ex, "Saves of player {PlayerId} not laid out", id);
                return (SavesResult.Fail(SavesErrors.Internal, $"Saves not laid out: {ex.Message}"), 0, 0);
            }
        }
    }

    private async Task<(SavesResult Result, long Bytes, int Files)> DetachLockedAsync(string id, CancellationToken ct)
    {
        if (_attached is not { } session || session.PlayerId != id)
        {
            return (SavesResult.Fail(SavesErrors.NotAttached, "Saves of this player were not attached on this PC; nothing sent"), 0, 0);
        }

        if (masterActive())
        {
            Forget();
            await ReleaseAsync(id);
            return (SavesResult.Fail(SavesErrors.MasterMode, "A library on this PC is in master mode; nothing sent"), 0, 0);
        }

        // Свежий список: запись, удалённую в панели посреди сеанса, сервер не принял бы (badArchive) — её не отправляем.
        SavesConfig config;
        try
        {
            config = await api.GetSavesConfigAsync(ct);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            return (SavesResult.Fail(SavesErrors.ServerUnavailable, ex.Message), 0, 0);
        }

        if (!config.Enabled)
        {
            return (SavesResult.Fail(SavesErrors.Disabled, "Player saves are disabled on the club server"), 0, 0);
        }

        var listed = config.Entries.Select(e => e.Id).ToHashSet();
        var folders = new List<(Guid, string)>();
        foreach (var folder in session.Folders.Concat(session.Carried).Where(f => listed.Contains(f.Id)))
        {
            // Ссылку сделали посреди сеанса — не забираем то, куда она ведёт (у службы прав больше, чем у игрока).
            if (ThroughLink(folder))
            {
                logger.LogWarning("Saves folder {EntryId} not sent: {Directory} is a link or under one", folder.Id, folder.Directory);
                continue;
            }

            folders.Add((folder.Id, folder.Directory));
        }

        Directory.CreateDirectory(_temp);
        await using var archive = new FileStream(Path.Combine(_temp, $"{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.DeleteOnClose);
        SavePackResult packed;
        try
        {
            packed = await PackAsync(archive, folders, config.Excludes, ct);
        }
        catch (SaveArchiveException ex) when (ex.Reason == SaveArchiveException.OverLimit)
        {
            Forget();
            await ReleaseAsync(id, overLimit: true);
            return (SavesResult.Fail(SavesErrors.OverLimit, ex.Message), 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Saves of player {PlayerId} not packed", id);
            return (SavesResult.Fail(SavesErrors.Internal, $"Saves folders not read (a game still running?): {ex.Message}"), 0, 0);
        }

        if (packed.Bytes > LimitBytes(config.LimitMb))
        {
            Forget();
            await ReleaseAsync(id, overLimit: true, packed.Bytes);
            return (SavesResult.Fail(SavesErrors.OverLimit, $"Saves are {packed.Bytes} bytes, the limit is {config.LimitMb} MB; nothing sent"), packed.Bytes,
                packed.Files);
        }

        archive.Position = 0;
        try
        {
            await api.PutSavesAsync(id, archive, ct);
        }
        catch (SavesRejectedException ex)
        {
            // Отказ по существу — эта выдача кончилась; «выключено» (диск сохранений отвалился) — можно повторить.
            if (ex.Reason != "disabled")
            {
                Forget();
            }

            return (Rejected(ex), packed.Bytes, packed.Files);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            return (SavesResult.Fail(SavesErrors.ServerUnavailable, ex.Message), packed.Bytes, packed.Files);
        }

        Forget();
        return (SavesResult.Success, packed.Bytes, packed.Files);
    }

    /// <summary>Упаковка с повтором, пока файл занят другим процессом (игра ещё закрывается).</summary>
    private async Task<SavePackResult> PackAsync(FileStream archive, List<(Guid, string)> folders, IReadOnlyList<string> excludes, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            archive.SetLength(0);
            archive.Position = 0;
            try
            {
                return SaveArchive.Pack(archive, folders, excludes);
            }
            catch (IOException ex) when (attempt < BusyRetries)
            {
                logger.LogDebug("Saves not packed ({Error}); retrying in {Delay}", ex.Message, BusyRetryDelay);
                await Task.Delay(BusyRetryDelay, clock, ct);
            }
        }
    }

    /// <summary>
    /// Папки записей на этом ПК. Пропускаются (с предупреждением): шаблон, который помощник не принял бы (буква не
    /// библиотеки ПК), библиотека не подключена, нет исходного содержимого для её версии (или оно больше лимита), папка —
    /// ссылка или под ссылкой. В <paramref name="baselines"/> — исходное содержимое для папок на буквах библиотек, в
    /// <paramref name="skipped"/> — ID пропущенных записей.
    /// </summary>
    private List<Folder> Folders(SavesConfig config, string profile, IReadOnlyList<LibraryMount> libraries, List<(Folder, string)> baselines,
        List<Guid> skipped)
    {
        var letters = libraries.Select(l => char.ToUpperInvariant(l.Letter)).ToList();
        var folders = new List<Folder>();
        foreach (var entry in config.Entries)
        {
            if (SavePathTemplate.Validate(entry.Path, letters) is { } problem)
            {
                logger.LogWarning("Saves folder {EntryId} ({Path}) skipped on this PC: {Problem} (libraries here: {Letters})",
                    entry.Id, entry.Path, problem, string.Join(", ", letters));
                skipped.Add(entry.Id);
                continue;
            }

            var target = SavePathTemplate.Parse(entry.Path);
            Folder folder;
            string? baseline = null;
            if (target.Kind == SaveTargetKind.Profile)
            {
                folder = new Folder(entry.Id, Under(Under(profile, SavePathTemplate.ProfileSubpath(target.Anchor)), target.RelativePath), profile);
            }
            else
            {
                var mount = libraries.First(l => char.ToUpperInvariant(l.Letter) == target.Anchor[0]);
                var state = ReadBaseline(entry.Id);
                if (!mount.Mounted || mount.Version is null || state is null || state.Version != mount.Version || state.Path != entry.Path
                    || state.OverLimit || !File.Exists(ZipOf(entry.Id)))
                {
                    logger.LogWarning("Saves folder {EntryId} ({Path}) skipped on this PC: {Reason}", entry.Id, entry.Path,
                        !mount.Mounted ? "library not mounted"
                        : state is { OverLimit: true } && state.Version == mount.Version && state.Path == entry.Path ? "its original content is over the limit"
                        : "no original content for this library version yet");
                    skipped.Add(entry.Id);
                    continue;
                }

                var root = drives.RootOf(mount.Letter);
                folder = new Folder(entry.Id, Under(root, target.RelativePath), root);
                baseline = ZipOf(entry.Id);
            }

            if (ThroughLink(folder))
            {
                logger.LogWarning("Saves folder {EntryId} ({Path}) skipped on this PC: {Directory} is a link or under one", entry.Id, entry.Path, folder.Directory);
                skipped.Add(entry.Id);
                continue;
            }

            folders.Add(folder);
            if (baseline is not null)
            {
                baselines.Add((folder, baseline));
            }
        }

        return folders;
    }

    /// <summary>Исходное содержимое папок на подключённых буквах библиотек, которого ещё нет для их версии и шаблона.</summary>
    private void TakeBaselines(SavesConfig config, IReadOnlyList<LibraryMount> libraries)
    {
        var letters = libraries.Select(l => char.ToUpperInvariant(l.Letter)).ToList();
        foreach (var entry in config.Entries)
        {
            if (SavePathTemplate.Validate(entry.Path, letters) is not null || SavePathTemplate.Parse(entry.Path) is not { Kind: SaveTargetKind.Library } target)
            {
                continue;
            }

            var mount = libraries.First(l => char.ToUpperInvariant(l.Letter) == target.Anchor[0]);
            if (!mount.Mounted || mount.Version is not { } version || ReadBaseline(entry.Id) is { } state && state.Version == version && state.Path == entry.Path)
            {
                continue;
            }

            var root = drives.RootOf(mount.Letter);
            var folder = new Folder(entry.Id, Under(root, target.RelativePath), root);
            if (!ThroughLink(folder))
            {
                TakeBaseline(entry, version, folder.Directory, LimitBytes(config.LimitMb));
            }
        }
    }

    /// <summary>Снимает копию папки (все файлы, без исключений); больше лимита — отметка, копии нет. Сбой — повтор в следующем такте.</summary>
    private void TakeBaseline(SaveEntryConfig entry, string version, string directory, long limit)
    {
        var zip = ZipOf(entry.Id);
        var temp = zip + ".tmp";
        try
        {
            Directory.CreateDirectory(_baselines);
            File.Delete(StateOf(entry.Id));
            SavePackResult? packed;
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                try
                {
                    packed = SaveArchive.Pack(file, [(entry.Id, directory)], []);
                }
                catch (SaveArchiveException ex) when (ex.Reason == SaveArchiveException.OverLimit)
                {
                    packed = null;
                }
            }

            if (packed is null || packed.Bytes > limit)
            {
                File.Delete(temp);
                File.Delete(zip);
                WriteBaseline(entry.Id, new BaselineState(version, entry.Path, OverLimit: true));
                logger.LogWarning(
                    "Saves folder {EntryId} ({Path}) is over the limit in library version {Version} ({Bytes} bytes); skipped on this PC",
                    entry.Id, entry.Path, version, packed?.Bytes);
                return;
            }

            File.Move(temp, zip, overwrite: true);
            WriteBaseline(entry.Id, new BaselineState(version, entry.Path, OverLimit: false));
            logger.LogInformation("Original content of saves folder {EntryId} ({Path}) taken for library version {Version}: {Bytes} bytes, {Files} files",
                entry.Id, entry.Path, version, packed.Bytes, packed.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            logger.LogWarning(ex, "Original content of saves folder {EntryId} ({Path}) not taken; retrying next tick", entry.Id, entry.Path);
        }
    }

    private string ZipOf(Guid entryId) => Path.Combine(_baselines, $"{entryId:D}.zip");

    private string StateOf(Guid entryId) => Path.Combine(_baselines, $"{entryId:D}.json");

    private BaselineState? ReadBaseline(Guid entryId)
    {
        try
        {
            return JsonSerializer.Deserialize<BaselineState>(File.ReadAllText(StateOf(entryId)), Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private void WriteBaseline(Guid entryId, BaselineState state)
    {
        var temp = StateOf(entryId) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StateOf(entryId), overwrite: true);
    }

    /// <summary>
    /// Папка записи — ссылка или под ссылкой (symlink, junction) ниже корня профиля или буквы: служба туда не заходит (её
    /// мог сделать игрок, чтобы служба удалила или забрала чужие файлы).
    /// </summary>
    private static bool ThroughLink(Folder folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Root));
        for (var d = Path.GetFullPath(folder.Directory); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
        {
            if (new DirectoryInfo(d).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Выдача кончилась: забыть «выдано» и файлы пропущенных записей.</summary>
    private void Forget()
    {
        _attached = null;
        try
        {
            DropCarry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Saves carry folder not removed; removed at the next attach");
        }
    }

    /// <summary>Удаляет <c>saves-carry</c> (папка помощника, ссылок там нет).</summary>
    private void DropCarry()
    {
        if (Directory.Exists(_carry))
        {
            Clear(_carry);
            Directory.Delete(_carry);
        }
    }

    /// <summary>Исходное содержимое — в очищенную папку записи.</summary>
    private static void Restore(Folder folder, string zip)
    {
        using var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        SaveArchive.Unpack(stream, entryId => entryId == folder.Id ? folder.Directory : null, long.MaxValue, unknownIsError: false);
    }

    /// <summary>
    /// Удаляет всё внутри папки, сама папка остаётся (нет — нечего очищать). <c>ReadOnly</c>, <c>Hidden</c>, <c>System</c>
    /// снимаются перед удалением; ссылка удаляется сама, без захода внутрь.
    /// </summary>
    private static void Clear(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            return;
        }

        Unprotect(root);
        ClearContents(root);
    }

    private static void ClearContents(DirectoryInfo directory)
    {
        foreach (var item in directory.EnumerateFileSystemInfos("*", Children).ToList())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                item.Delete();
                continue;
            }

            Unprotect(item);
            if (item is DirectoryInfo child)
            {
                ClearContents(child);
            }

            item.Delete();
        }
    }

    private static void Unprotect(FileSystemInfo item)
    {
        if ((item.Attributes & Protective) != 0)
        {
            item.Attributes &= ~Protective;
        }
    }

    /// <summary>
    /// Снять аренду, не дождавшись отмены команды; не вышло — только предупреждение (следующая выдача её перезапишет).
    /// <paramref name="overLimit"/> — сохранения больше лимита и не отправлены: сервер ставит игроку отметку
    /// <c>overLimit</c> для панели (spec п. 6), <paramref name="bytes"/> — сколько набралось.
    /// </summary>
    private async Task ReleaseAsync(string id, bool overLimit = false, long? bytes = null)
    {
        try
        {
            if (overLimit)
            {
                await api.ReleaseSavesLeaseOverLimitAsync(id, bytes, CancellationToken.None);
            }
            else
            {
                await api.ReleaseSavesLeaseAsync(id, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is ServerUnavailableException or SavesRejectedException or PendingApprovalException)
        {
            logger.LogWarning("Saves lease of player {PlayerId} not released: {Error}", id, ex.Message);
        }
    }

    private static SavesResult Rejected(SavesRejectedException ex) => ex.Reason switch
    {
        "disabled" => SavesResult.Fail(SavesErrors.Disabled, "Player saves are disabled on the club server or its saves disk is not mounted"),
        "notAttached" => SavesResult.Fail(SavesErrors.NotAttached, "The club server attached this player's saves to another PC"),
        "overLimit" => SavesResult.Fail(SavesErrors.OverLimit, "The club server rejected the saves: over the limit"),
        _ => SavesResult.Fail(SavesErrors.Internal, $"The club server rejected the saves: {ex.Reason}"),
    };

    /// <summary>Строка журнала на команду (spec п. 4.5): ID игрока, размер, время, результат.</summary>
    private SavesResult Logged(string command, string? playerId, (SavesResult Result, long Bytes, int Files) outcome, long started)
    {
        var (result, bytes, files) = outcome;
        var elapsed = (long)clock.GetElapsedTime(started).TotalMilliseconds;
        if (result.Ok)
        {
            logger.LogInformation("Saves {Command} of player {PlayerId}: ok, {Bytes} bytes, {Files} files, {Elapsed} ms",
                command, playerId, bytes, files, elapsed);
        }
        else
        {
            logger.LogWarning("Saves {Command} of player {PlayerId}: {Error}, {Bytes} bytes, {Files} files, {Elapsed} ms: {Message}",
                command, playerId ?? "(invalid)", result.Error, bytes, files, elapsed, result.Message);
        }

        return result;
    }

    private static long LimitBytes(int limitMb) => limitMb * BytesPerMb;

    /// <summary>Путь под корнем: части шаблона через <c>\</c> — разделителем этой ОС.</summary>
    private static string Under(string root, string relative) =>
        relative.Length == 0 ? root : Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
