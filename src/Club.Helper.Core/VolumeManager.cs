using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Приводит том библиотеки на ПК к назначенному сервером. Правила:
/// <list type="bullet">
/// <item>монтирование fail-closed: SAN policy OfflineShared → вход в таргет → атрибут read-only с проверкой →
/// online → буква. Если read-only не подтвердился — таргет отключается, том на запись не монтируется никогда;</item>
/// <item>смена версии не выдёргивает диск из-под игры: пока с тома запущен процесс или Windows отказывается
/// завершить сессию старой версии (на томе открыты файлы) — <c>switchPending</c> со старой версией в отчёте, таргет
/// остаётся, повтор в следующем такте; насильно не отключается никогда;</item>
/// <item>Windows не ответила (таймаут PowerShell) при проверке подключённой версии — не повод её отключать: <c>failed</c>
/// с причиной, сессия остаётся (кроме диска, оказавшегося на запись, — тогда fail-closed);</item>
/// <item>нет назначения (сервер ничего не опубликовал) — том отключается, когда освободится;</item>
/// <item>личный диск игр места (<c>games-seat-NN</c>, помощник 1.5+): единственный том, который подключается на запись, —
/// с CHAP места; сервер сбрасывает его при каждой загрузке ПК. Read-only тут не проверяется (ReadOnlyVerified = null).
/// Любое другое назначение на запись не выполняется.</item>
/// </list>
/// Недоступность сервера сюда не доходит: цикл просто не вызывает <see cref="ApplyAsync"/> с новым назначением.
/// </summary>
public sealed class VolumeManager(IWindowsStorage storage, IProcessInspector processes, HelperOptions options, TimeProvider clock, ILogger<VolumeManager> logger)
{
    /// <summary>Таргеты версий библиотеки называются <c>games-&lt;версия&gt;</c>; чужие iSCSI-подключения ПК не трогаем.</summary>
    public const string ManagedTargetMarker = ":games-";

    /// <summary>Личный диск игр места: <c>games-seat-NN</c> (SeatGames на сервере; версии с меткой seat-* сервер не публикует).</summary>
    public const string PersonalTargetMarker = ":games-seat-";

    /// <summary>Назначение — личный диск места: на запись, с CHAP, таргет <c>games-seat-NN</c>.</summary>
    public static bool IsPersonal(VolumeAssignment desired) =>
        !desired.ReadOnly && desired.ChapUser is { Length: > 0 } && desired.ChapSecret is { Length: > 0 }
        && desired.TargetIqn.Contains(PersonalTargetMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Подключён ли на ПК личный диск игр — для запроса назначения при старте (сервер сбрасывает диск, только если нет);
    /// опрос не удался — <c>null</c>: сервер решит по своим сессиям.
    /// </summary>
    public async Task<bool?> PersonalAttachedAsync(CancellationToken ct)
    {
        try
        {
            return (await ManagedTargetsAsync(ct)).Any(t => t.Contains(PersonalTargetMarker, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not list iSCSI sessions before asking for the assignment: {Reason}", ex.Message);
            return null;
        }
    }

    /// <summary>Старые версии, которые не удалось отпустить в прошлый раз, и почему: в журнал — смена причины, а не каждый такт.</summary>
    private Dictionary<string, string> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Таргеты библиотеки, подключённые по последним сведениям: опрос сессий, свои вход и отключение. Нужны отчёту о
    /// сбое, когда Windows только что не ответила за 120 с и спрашивать её снова нельзя (<see cref="FailedAsync"/>).
    /// </summary>
    private readonly List<string> _connected = [];

    /// <summary>
    /// Буква, на которой помощник в последний раз видел том таргета (смонтировал, проверил или нашёл перед отключением).
    /// По ней — диагностика отказа Windows, когда диска таргета (или его буквы) служба уже не видит.
    /// </summary>
    private readonly Dictionary<string, char> _letters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// В последнем <see cref="ApplyAsync"/> Windows не ответила (PowerShell дольше 120 с), а сбой пойман здесь же и отчёт
    /// уже составлен. <see cref="HelperLoop"/> по нему не опрашивает в этом такте сессии ради мастер-тома: ждал бы ещё до
    /// 120 с. Сбой, ушедший исключением наверх, <see cref="HelperLoop"/> распознаёт сам.
    /// </summary>
    public bool WindowsTimedOut { get; private set; }

    /// <summary>
    /// Версия библиотеки по имени таргета: сервер называет таргет <c>games-&lt;версия&gt;</c> (LibraryPublisher), версия —
    /// строчные латинские буквы, цифры и дефис, до 40 символов. Имя не такое — <c>null</c>.
    /// </summary>
    public static string? VersionOfTarget(string iqn)
    {
        var at = iqn.LastIndexOf(ManagedTargetMarker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var label = iqn[(at + ManagedTargetMarker.Length)..].ToLowerInvariant();
        if (label.StartsWith("seat-", StringComparison.Ordinal))
        {
            return null; // личный диск места: версия не в имени таргета
        }

        return label.Length is > 0 and <= 40 && label[0] != '-' && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') ? label : null;
    }

    /// <summary>Папки на смонтированном томе; не удалось прочитать — <c>null</c> (сообщим в следующий раз).</summary>
    public async Task<IReadOnlyList<string>?> ContentsAsync(MountedVolume report, CancellationToken ct)
    {
        if (report.State != "mounted" || report.DriveLetter is not { Length: 1 } letter)
        {
            return null;
        }

        try
        {
            return await storage.ListFoldersAsync(letter[0], ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("Could not list folders on {Letter}: {Reason}", letter, ex.Message);
            return null;
        }
    }

    public async Task<MountedVolume> ApplyAsync(VolumeAssignment? desired, CancellationToken ct)
    {
        WindowsTimedOut = false;
        var managed = await ManagedTargetsAsync(ct);

        if (desired is null)
        {
            var busy = await ReleaseAsync(managed, keepGoing: false, assignedLetter: null, ct);
            return busy is null ? new MountedVolume("none") : new MountedVolume("switchPending", busy.Iqn, VersionOfTarget(busy.Iqn), Error: busy.Reason);
        }

        if (!desired.ReadOnly && !IsPersonal(desired))
        {
            // Общий том всем ПК — только чтение; на запись — только личный диск места с CHAP. Иное назначение считаем
            // ошибкой сервера и не выполняем.
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, Error: "assignment is not read-only");
        }

        var letter = char.ToUpperInvariant(desired.DriveLetter[0]);
        var old = managed.Where(t => !t.Equals(desired.TargetIqn, StringComparison.OrdinalIgnoreCase)).ToList();

        if (managed.Any(t => t.Equals(desired.TargetIqn, StringComparison.OrdinalIgnoreCase)))
        {
            // Назначенный уже подключён: старые версии отпускаем, когда освободятся; проверяем здоровье текущего. Сбой
            // проверки старой (опрос диска или процессов) проверку текущей не отменяет: в журнал, повтор в следующем такте.
            // Кроме таймаута: Windows только что не ответила за 120 с — проверка текущей почти наверняка ждала бы столько
            // же, а её сбой не повод отключать том из-под игры. Такт без проверки: failed с причиной, сессия остаётся.
            // Назначенная буква здесь — буква текущего тома, для диагностики старой версии она не годится.
            await ReleaseAsync(old, keepGoing: true, assignedLetter: null, ct);
            return WindowsTimedOut
                ? new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: "not checked: timeout on the old version")
                : await VerifyAsync(desired, letter, ct);
        }

        var inUse = await ReleaseAsync(old, keepGoing: false, letter, ct);
        if (inUse is not null)
        {
            // Версия в отчёте — старая, ещё подключённая: пока ПК ждёт переключения, панель считает его на ней (mountedOn).
            return new MountedVolume(
                "switchPending", inUse.Iqn, VersionOfTarget(inUse.Iqn), letter.ToString(), Error: $"new version {desired.LibraryVersion} waits: {inUse.Reason}");
        }

        return await MountAsync(desired, letter, ct);
    }

    /// <summary>
    /// Отчёт <c>failed</c>, когда применение назначения прервалось исключением (его ловит <see cref="HelperLoop"/>, чтобы
    /// отчёт ушёл всё равно): какой таргет библиотеки сейчас подключён — повторный опрос сессий, best effort; не удался —
    /// последний известный. Сбой — таймаут (PowerShell не ответил за 120 с): сессии заново не опрашиваются, иначе отчёт
    /// ждал бы ещё до 120 с, — берётся последний известный таргет. Версия — только если подключён назначенный таргет.
    /// </summary>
    public async Task<MountedVolume> FailedAsync(VolumeAssignment? desired, Exception error, CancellationToken ct)
    {
        IReadOnlyList<string> managed = [.. _connected];
        if (error is not (TimeoutException or OperationCanceledException))
        {
            try
            {
                managed = await ManagedTargetsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug("Could not list library targets for the failure report: {Reason}", ex.Message);
            }
        }

        var connected = managed.FirstOrDefault(t => t.Equals(desired?.TargetIqn, StringComparison.OrdinalIgnoreCase)) ?? managed.FirstOrDefault();
        var current = desired is not null && desired.TargetIqn.Equals(connected, StringComparison.OrdinalIgnoreCase);
        var message = desired is null ? error.Message : $"library {desired.LibraryVersion}: {error.Message}";
        return new MountedVolume("failed", connected, current ? desired!.LibraryVersion : null, ReadOnlyVerified: false, Error: message);
    }

    private async Task<List<string>> ManagedTargetsAsync(CancellationToken ct)
    {
        var managed = (await storage.ConnectedTargetsAsync(ct))
            .Where(t => t.Contains(ManagedTargetMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _connected.Clear();
        _connected.AddRange(managed);
        return managed;
    }

    private void Connected(string iqn)
    {
        if (!_connected.Contains(iqn, StringComparer.OrdinalIgnoreCase))
        {
            _connected.Add(iqn);
        }
    }

    private void Disconnected(string iqn)
    {
        _connected.RemoveAll(t => t.Equals(iqn, StringComparison.OrdinalIgnoreCase));
        _letters.Remove(iqn);
    }

    /// <summary>Таймаут PowerShell (120 с) или отмена не по токену службы — Windows не ответила.</summary>
    private static bool IsTimeout(Exception ex, CancellationToken ct) =>
        ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task<MountedVolume> MountAsync(VolumeAssignment desired, char letter, CancellationToken ct)
    {
        var (host, port) = ParsePortal(desired.Portal);
        try
        {
            await storage.EnsureSanPolicyOfflineSharedAsync(ct);
            if (IsPersonal(desired))
            {
                await storage.ConnectChapAsync(desired.TargetIqn, host, port, desired.ChapUser!, desired.ChapSecret!, ct);
            }
            else
            {
                await storage.ConnectAsync(desired.TargetIqn, host, port, ct);
            }

            Connected(desired.TargetIqn);
            var disk = await WaitForDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk of the target did not appear");
            return await BringUpAsync(desired, disk, letter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WindowsTimedOut |= ex is TimeoutException;
            logger.LogError(ex, "Mounting {Iqn} failed; disconnecting", desired.TargetIqn);
            await DisconnectQuietlyAsync(desired.TargetIqn, ct);
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: ex.Message);
        }
    }

    private async Task<MountedVolume> VerifyAsync(VolumeAssignment desired, char letter, CancellationToken ct)
    {
        DiskInfo? seen = null;
        try
        {
            seen = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("session has no disk");
            if ((seen.IsReadOnly || IsPersonal(desired)) && !seen.IsOffline && seen.DriveLetter == letter)
            {
                return Mounted(desired, letter);
            }

            // Что-то сбилось (буква, offline, кто-то снял read-only) — доводим тем же fail-closed порядком.
            logger.LogWarning("Volume {Iqn} drifted (ro={Ro}, offline={Offline}, letter={Letter}); repairing", desired.TargetIqn, seen.IsReadOnly, seen.IsOffline, seen.DriveLetter);
            return await BringUpAsync(desired, seen, letter, ct);
        }
        catch (Exception ex) when (IsTimeout(ex, ct) && (IsPersonal(desired) || seen is not { IsReadOnly: false }))
        {
            // Windows не ответила (PowerShell дольше 120 с) — это не «том нездоров»: выдёргивать проверенный read-only том
            // из-под игры из-за медленной Windows нельзя. failed с причиной, сессия остаётся, повтор в следующем такте.
            // Диск оказался на запись (кто-то снял read-only) и починить его не успели — отключаем, как при любом сбое.
            WindowsTimedOut = true;
            logger.LogWarning("Volume {Iqn} not checked: Windows did not answer ({Reason}); keeping it, retrying next tick", desired.TargetIqn, ex.Message);
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: "not checked: timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            WindowsTimedOut |= IsTimeout(ex, ct);
            logger.LogError(ex, "Volume {Iqn} is unhealthy; disconnecting", desired.TargetIqn);
            await DisconnectQuietlyAsync(desired.TargetIqn, ct);
            return new MountedVolume("failed", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: false, Error: ex.Message);
        }
    }

    /// <summary>read-only с проверкой → online → буква. Порядок важен: online раньше read-only = NTFS на запись.</summary>
    private async Task<MountedVolume> BringUpAsync(VolumeAssignment desired, DiskInfo disk, char letter, CancellationToken ct)
    {
        if (IsPersonal(desired))
        {
            return await BringUpPersonalAsync(desired, disk, letter, ct);
        }

        if (!disk.IsReadOnly)
        {
            await storage.SetDiskReadOnlyAsync(disk.Number, ct);
            disk = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk disappeared");
            if (!disk.IsReadOnly)
            {
                throw new InvalidOperationException("disk is not read-only after SetDiskReadOnly; refusing to bring it online");
            }
        }

        if (disk.IsOffline)
        {
            await storage.SetDiskOnlineAsync(disk.Number, ct);
        }

        if (disk.DriveLetter != letter)
        {
            await storage.AssignDriveLetterAsync(disk.Number, letter, ct);
        }

        var final = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk disappeared");
        if (!final.IsReadOnly || final.IsOffline || final.DriveLetter != letter)
        {
            throw new InvalidOperationException($"volume not in expected state (ro={final.IsReadOnly}, offline={final.IsOffline}, letter={final.DriveLetter})");
        }

        logger.LogInformation("Library {Version} mounted read-only at {Letter}:", desired.LibraryVersion, letter);
        return Mounted(desired, letter);
    }

    /// <summary>Личный диск места: снять read-only (если Windows его помнит) → online → буква.</summary>
    private async Task<MountedVolume> BringUpPersonalAsync(VolumeAssignment desired, DiskInfo disk, char letter, CancellationToken ct)
    {
        if (disk.IsReadOnly)
        {
            await storage.SetDiskWritableAsync(disk.Number, ct);
        }

        if (disk.IsOffline)
        {
            await storage.SetDiskOnlineAsync(disk.Number, ct);
        }

        if (disk.DriveLetter != letter)
        {
            await storage.AssignDriveLetterAsync(disk.Number, letter, ct);
        }

        var final = await storage.FindDiskAsync(desired.TargetIqn, ct) ?? throw new InvalidOperationException("disk disappeared");
        if (final.IsOffline || final.DriveLetter != letter)
        {
            throw new InvalidOperationException($"volume not in expected state (offline={final.IsOffline}, letter={final.DriveLetter})");
        }

        logger.LogInformation("Personal games disk (library {Version}) mounted at {Letter}:", desired.LibraryVersion, letter);
        return Mounted(desired, letter);
    }

    /// <summary>
    /// Старая версия ещё не отпущена: <paramref name="Error"/> — сбой, если отключение (или проверка) не удалось;
    /// <paramref name="Refused"/> — Windows отказала завершить сессию, а процесса с тома не нашлось.
    /// </summary>
    private sealed record Busy(string Iqn, string Reason, Exception? Error = null, Refusal? Refused = null);

    /// <summary>
    /// Отказ Windows отключить старую версию: что вернул поиск диска таргета (<c>null</c> — диска служба не видит) и по
    /// какой букве смотреть процессы для журнала (<c>null</c> — буква неизвестна) и откуда она.
    /// </summary>
    private sealed record Refusal(DiskInfo? Disk, char? ScanLetter, string? ScanLetterSource);

    /// <summary>
    /// Отключает перечисленные таргеты, если их том свободен; занятые остаются (повтор — в следующем такте), возвращается
    /// первый из них. Занят — с тома запущен процесс или Windows отказалась завершить сессию. <paramref name="keepGoing"/>
    /// — сбой проверки таргета не прерывает остальное (таргет считается занятым), иначе исключение уходит наверх.
    /// <paramref name="assignedLetter"/> — назначенная буква, пока назначенный том не подключён: по ней диагностика
    /// отказа, если буквы таргета не знаем.
    /// </summary>
    private async Task<Busy?> ReleaseAsync(IReadOnlyList<string> targets, bool keepGoing, char? assignedLetter, CancellationToken ct)
    {
        Busy? first = null;
        var held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var iqn in targets)
        {
            Busy? busy;
            try
            {
                busy = await TryReleaseAsync(iqn, assignedLetter, ct);
            }
            catch (Exception ex) when (keepGoing && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                var timeout = IsTimeout(ex, ct);
                WindowsTimedOut |= timeout;
                busy = new Busy(iqn, $"old version not checked: {(timeout ? "timeout" : ShortMessage(ex))}", ex);
            }

            if (busy is null)
            {
                continue;
            }

            first ??= busy;
            held[iqn] = busy.Reason;
            if (_held.GetValueOrDefault(iqn) != busy.Reason)
            {
                // Одна запись на смену причины: такт каждые 15–30 с, а ждать, пока закроют игру или Steam, — минуты.
                await LogHeldAsync(busy, ct);
            }
        }

        _held = held;
        return first;
    }

    private async Task LogHeldAsync(Busy busy, CancellationToken ct)
    {
        if (busy.Error is null)
        {
            logger.LogInformation("Library target {Iqn} is not released: {Reason}; retrying every tick", busy.Iqn, busy.Reason);
            return;
        }

        if (busy.Refused is not { } refused)
        {
            logger.LogWarning(busy.Error, "Library target {Iqn} is not released: {Reason}; keeping it, retrying every tick", busy.Iqn, busy.Reason);
            return;
        }

        // Windows не отпускает том, а процесса с него не нашлось. На стенде 2026-10-02 так было при запущенных с G:
        // cstrike.exe и steam.exe, которых помощник 1.4.1 не увидел, — поэтому в ту же запись то, что видит служба: что
        // вернул поиск диска таргета (не нашёл диск или букву — процессы перед отключением не проверялись вовсе),
        // устройство тома, сбои открытия процессов, пути образов вне папки Windows. Дорого — только здесь, раз на причину.
        var disk = DescribeDisk(refused.Disk);
        var processCheck = refused.Disk?.DriveLetter is { } volumeLetter ? $"no process from {volumeLetter}: found" : "processes not checked (no drive letter)";
        if (refused.ScanLetter is not { } letter)
        {
            logger.LogWarning(
                busy.Error, "Library target {Iqn} is not released: {Reason}; keeping it, retrying every tick. Disk of the target: {Disk}; {ProcessCheck}; process scan skipped: no drive letter known for the target",
                busy.Iqn, busy.Reason, disk, processCheck);
            return;
        }

        string scan;
        try
        {
            scan = (await processes.DiagnoseAsync(letter, ct)).ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            scan = $"process scan failed: {ex.Message}";
        }

        logger.LogWarning(
            busy.Error, "Library target {Iqn} is not released: {Reason}; keeping it, retrying every tick. Disk of the target: {Disk}; {ProcessCheck}; process scan of {Letter}: ({LetterSource}): {Scan}",
            busy.Iqn, busy.Reason, disk, processCheck, letter, refused.ScanLetterSource, scan);
    }

    /// <summary>Результат FindDisk для журнала: номер, буква, offline, read-only — или «не найден».</summary>
    private static string DescribeDisk(DiskInfo? disk) =>
        disk is null
            ? "not found"
            : $"number {disk.Number}, letter {(disk.DriveLetter is { } l ? $"{l}:" : "none")}, offline {disk.IsOffline}, read-only {disk.IsReadOnly}";

    private async Task<Busy?> TryReleaseAsync(string iqn, char? assignedLetter, CancellationToken ct)
    {
        var disk = await storage.FindDiskAsync(iqn, ct);
        if (disk?.DriveLetter is { } letter)
        {
            _letters[iqn] = letter;
            var running = await processes.ProcessesRunningFromAsync(letter, ct);
            if (running.Count > 0)
            {
                return new Busy(iqn, $"volume in use by {running[0]}");
            }
        }

        try
        {
            await storage.DisconnectAsync(iqn, ct);
        }
        catch (Exception ex) when (IsTimeout(ex, ct))
        {
            // Windows не ответила (PowerShell дольше 120 с) — это не «открытые файлы», том мог и освободиться.
            WindowsTimedOut = true;
            return new Busy(iqn, "old version not disconnected: timeout", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Командлет упал (InvalidOperationException от PowerShell), а процесса с тома не нашлось: Windows не завершает
            // сессию — «The session cannot be logged out since a device on that session is currently being used». Так
            // бывает, когда на томе открыты файлы (окно проводника, Steam или другой лаунчер, антивирус, индексатор), — и
            // так было на стенде 2026-10-02 при запущенных с G: игре и Steam, которых помощник не увидел (диагностика —
            // LogHeldAsync). Текст отказа локализован, по нему не судим, а показываем первой строкой в причине: любой сбой
            // отключения — таргет остаётся, повтор в следующем такте, насильно не отключаем.
            if (ex is not InvalidOperationException)
            {
                return new Busy(iqn, $"old version not disconnected: {ShortMessage(ex)}", ex);
            }

            var refusal = ShortMessage(ex);
            var reason = disk switch
            {
                { DriveLetter: { } l } => $"volume in use (open files on {l}:): {refusal}",
                { IsOffline: false } => $"volume in use (open files): {refusal}",
                _ => $"old version not disconnected: {refusal}",
            };

            // Буква для диагностики: тома; служба не видит диск или его букву (так тоже могло быть на стенде 1.4.1 —
            // тогда процессы не проверялись вовсе) — та, на которой помощник видел этот таргет, иначе назначенная.
            (char? Letter, string? Source) scan = disk?.DriveLetter is { } own ? (own, "volume letter")
                : _letters.TryGetValue(iqn, out var seen) ? (seen, "letter this helper last saw the target at")
                : assignedLetter is { } assigned ? (assigned, "assigned letter")
                : (null, null);
            return new Busy(iqn, reason, ex, new Refusal(disk, scan.Letter, scan.Source));
        }

        Disconnected(iqn);
        logger.LogInformation("Library target {Iqn} disconnected", iqn);
        return null;
    }

    /// <summary>
    /// Первая строка текста ошибки для причины в панели: без обёртки «PowerShell failed (1): » (её добавляет служба), не
    /// длиннее 200 символов.
    /// </summary>
    private static string ShortMessage(Exception ex)
    {
        const string wrapper = "PowerShell failed (";
        var line = ex.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ex.GetType().Name;
        if (line.StartsWith(wrapper, StringComparison.Ordinal) && line.IndexOf("): ", StringComparison.Ordinal) is var end and > 0)
        {
            line = line[(end + 3)..];
        }

        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private async Task<DiskInfo?> WaitForDiskAsync(string iqn, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddSeconds(options.DiskWaitSec);
        while (true)
        {
            if (await storage.FindDiskAsync(iqn, ct) is { } disk)
            {
                return disk;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, ct);
        }
    }

    private async Task DisconnectQuietlyAsync(string iqn, CancellationToken ct)
    {
        try
        {
            await storage.DisconnectAsync(iqn, ct);
            Disconnected(iqn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Disconnect of {Iqn} failed", iqn);
        }
    }

    private MountedVolume Mounted(VolumeAssignment desired, char letter)
    {
        // Буква теперь у этого тома: запомненная у другого таргета устарела — диагностика его отказа не должна смотреть
        // процессы текущей версии.
        foreach (var stale in _letters.Where(l => l.Value == letter && !l.Key.Equals(desired.TargetIqn, StringComparison.OrdinalIgnoreCase)).Select(l => l.Key).ToList())
        {
            _letters.Remove(stale);
        }

        _letters[desired.TargetIqn] = letter;
        return new("mounted", desired.TargetIqn, desired.LibraryVersion, letter.ToString(), ReadOnlyVerified: IsPersonal(desired) ? null : true);
    }

    public static (string Host, int Port) ParsePortal(string portal)
    {
        var colon = portal.LastIndexOf(':');
        return colon > 0 && int.TryParse(portal[(colon + 1)..], out var port) ? (portal[..colon], port) : (portal, 3260);
    }
}
