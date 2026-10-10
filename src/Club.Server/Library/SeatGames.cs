using System.Collections.Concurrent;
using System.Security.Cryptography;
using Club.Server.Data;
using Club.Server.Diskless;
using Club.TrueNas;
using Dapper;
using Npgsql;

namespace Club.Server.Library;

public sealed class SeatGameRow
{
    public Guid MachineId { get; init; }
    public string Zvol { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string InitiatorIqn { get; init; } = "";
    public string? BaseSnapshot { get; init; }
    public string? LibraryVersion { get; init; }
    public string ChapUser { get; init; } = "";
    public string ChapSecret { get; init; } = "";
    public int? AuthTag { get; init; }
    public string? TargetIqn { get; init; }
    public string State { get; init; } = "new";
    public string? LastError { get; init; }
    public int Resets { get; init; }
    public DateTimeOffset? LastResetAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class SeatGamesRepository(NpgsqlDataSource db)
{
    private const string Columns = """
        machine_id AS MachineId, zvol, target_name AS TargetName, initiator_iqn AS InitiatorIqn, base_snapshot AS BaseSnapshot,
        library_version AS LibraryVersion, chap_user AS ChapUser, chap_secret AS ChapSecret, auth_tag AS AuthTag, target_iqn AS TargetIqn,
        state, last_error AS LastError, resets, last_reset_at AS LastResetAt, updated_at AS UpdatedAt
        """;

    static SeatGamesRepository() => DapperSetup.Ensure();

    public async Task<SeatGameRow?> FindAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<SeatGameRow>($"SELECT {Columns} FROM seat_games WHERE machine_id = @machineId", new { machineId });
    }

    public async Task<IReadOnlyList<SeatGameRow>> AllAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<SeatGameRow>($"SELECT {Columns} FROM seat_games ORDER BY target_name")).ToList();
    }

    public async Task UpsertAsync(SeatGameRow seat, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO seat_games (machine_id, zvol, target_name, initiator_iqn, base_snapshot, library_version, chap_user, chap_secret, auth_tag, state, updated_at)
            VALUES (@MachineId, @Zvol, @TargetName, @InitiatorIqn, @BaseSnapshot, @LibraryVersion, @ChapUser, @ChapSecret, @AuthTag, 'new', @now)
            ON CONFLICT (machine_id) DO UPDATE SET zvol = EXCLUDED.zvol, target_name = EXCLUDED.target_name, initiator_iqn = EXCLUDED.initiator_iqn,
                base_snapshot = EXCLUDED.base_snapshot, library_version = EXCLUDED.library_version, chap_user = EXCLUDED.chap_user,
                chap_secret = EXCLUDED.chap_secret, auth_tag = EXCLUDED.auth_tag, state = 'new', last_error = NULL, updated_at = @now
            """, new { seat.MachineId, seat.Zvol, seat.TargetName, seat.InitiatorIqn, seat.BaseSnapshot, seat.LibraryVersion, seat.ChapUser, seat.ChapSecret, seat.AuthTag, now });
    }

    public async Task MarkReadyAsync(Guid machineId, int authTag, string targetIqn, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE seat_games SET state = 'ready', auth_tag = @authTag, target_iqn = @targetIqn, last_error = NULL, updated_at = @now WHERE machine_id = @machineId",
            new { machineId, authTag, targetIqn, now });
    }

    public async Task MarkFailedAsync(Guid machineId, string error, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE seat_games SET state = 'failed', last_error = @error, updated_at = @now WHERE machine_id = @machineId", new { machineId, error, now });
    }

    /// <summary>Чей личный диск носит это имя таргета (номер места).</summary>
    public async Task<SeatGameRow?> FindByTargetAsync(string targetName)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<SeatGameRow>($"SELECT {Columns} FROM seat_games WHERE target_name = @targetName", new { targetName });
    }

    /// <summary>Перед разборкой: запись больше не «ready», даже если разборка оборвётся на середине.</summary>
    public async Task MarkRebuildingAsync(Guid machineId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE seat_games SET state = 'new', updated_at = @now WHERE machine_id = @machineId", new { machineId, now });
    }

    public async Task DeleteAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM seat_games WHERE machine_id = @machineId", new { machineId });
    }

    public async Task RecordResetAsync(Guid machineId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE seat_games SET resets = resets + 1, last_reset_at = @now, updated_at = @now WHERE machine_id = @machineId", new { machineId, now });
    }
}

/// <summary>
/// Личный слой игр места (<see cref="LibraryOptions.PersonalGames"/>, docs/diskless-full.md §2): записываемый клон
/// снапшота текущей версии библиотеки, CHAP места и группа из его IQN. Точка сброса — запрос назначения при старте
/// помощника (загрузка ПК): если к диску никто не подключён, клон откатывается к <c>@clean</c>, а при новой версии —
/// пересоздаётся. Пока ПК работает (отчёты), назначение не меняется: новая версия — со следующей загрузки.
/// Read-only устройств нет вовсе — ошибка SCST TrueNAS 25.10 с ними не возникает.
/// Подготовка (сброс, пересборка — десятки секунд при перезагрузках SCST) идёт отдельной задачей на машину, не в
/// запросе помощника: ответ — в пределах <see cref="AnswerWithin"/>, иначе «пока нет» (null), назначение придёт в
/// ответе на отчёт. Обрыв запроса подготовку не прерывает.
/// </summary>
public sealed class SeatGames(
    SeatGamesRepository repository,
    LibraryRepository library,
    TrueNasStorage storage,
    TargetVerifier verifier,
    LibraryOptions options,
    DisklessOptions diskless,
    TimeProvider clock,
    ILogger<SeatGames> logger)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<VolumeAssignment?>>> _inFlight = new();

    public const string TargetPrefix = "games-seat-";

    public static string TargetName(int number) => $"{TargetPrefix}{number:D2}";

    /// <summary>После сбоя сборки отчёты (каждые 15–30 с) не повторяют её раньше этого срока; запуск помощника — сразу.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    /// <summary>Сколько запрос помощника ждёт подготовки диска (HTTP-таймаут помощника — 20 с).</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(12);

    /// <summary>Помощник умеет личный слой (подключение на запись с CHAP).</summary>
    public bool Supports(string helperVersion) =>
        Version.TryParse(helperVersion.Split('-', '+')[0], out var have) && Version.TryParse(options.PersonalMinHelper, out var need) && have >= need;

    /// <summary>
    /// Назначение личного тома игр. <paramref name="atBoot"/> — запрос при старте помощника: можно сбросить диск и
    /// перейти на новую версию (если к диску никто не подключён). <paramref name="attached"/> — что помощник сказал о диске
    /// на ПК: false — не подключён (сессия с IQN этого ПК осталась от прошлой загрузки и сбросу не мешает), true —
    /// подключён (даже если TrueNAS сессии сейчас не видит — например, после перезапуска iSCSI, — не сбрасываем). Помощник
    /// 1.5 монтирует личный диск только из ответа на запрос при старте, поэтому отчёты его не сбрасывают. null — режим
    /// выключен, помощник старый, версии нет, IQN ПК ещё неизвестен (помощник сообщит его в отчёте) или диск ещё готовится.
    /// </summary>
    public async Task<VolumeAssignment?> AssignmentAsync(MachineRow machine, bool atBoot, CancellationToken ct, bool? attached = null)
    {
        if (!options.Enabled || !options.PersonalGames || !Supports(machine.HelperVersion))
        {
            return null;
        }

        var initiators = Initiators(machine);
        if (initiators.Count == 0 || (await library.PointersAsync()).Current is not { } current)
        {
            return null;
        }

        // Отчёт работающего ПК: готовый диск — сразу, без TrueNAS. Но не пока идёт подготовка (сброс под смонтированным
        // томом недопустим: назначение — только после её окончания).
        if (!atBoot && !_inFlight.ContainsKey(machine.Id) && await repository.FindAsync(machine.Id) is { } seat && Usable(seat, machine, initiators))
        {
            return Assignment(seat);
        }

        var lazy = _inFlight.GetOrAdd(machine.Id, _ => new Lazy<Task<VolumeAssignment?>>(() => RunAsync(machine, atBoot, attached, initiators, current)));
        var task = lazy.Value;
        var finished = await Task.WhenAny(task, Task.Delay(AnswerWithin, clock, ct));
        return finished == task ? await task : null;
    }

    private async Task<VolumeAssignment?> RunAsync(MachineRow machine, bool atBoot, bool? attached, List<string> initiators, LibraryVersion current)
    {
        try
        {
            return await PrepareAsync(machine, atBoot, attached, initiators, current, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Personal games disk of seat {Seat} failed", machine.Number);
            await repository.MarkFailedAsync(machine.Id, ex.Message, clock.GetUtcNow());
            return null;
        }
        finally
        {
            _inFlight.TryRemove(machine.Id, out _);
        }
    }

    private async Task<VolumeAssignment?> PrepareAsync(
        MachineRow machine, bool atBoot, bool? attached, List<string> initiators, LibraryVersion current, CancellationToken ct)
    {
        var seat = await repository.FindAsync(machine.Id);
        var name = TargetName(machine.Number);
        var usable = seat is not null && Usable(seat, machine, initiators);
        if (usable && !atBoot)
        {
            return Assignment(seat!);
        }

        var sessions = (await storage.GetSessionsAsync(ct)).Where(s => s.Target.EndsWith($":{seat?.TargetName ?? name}", StringComparison.Ordinal)).ToList();
        if (sessions.Any(s => !initiators.Contains(s.Initiator, StringComparer.OrdinalIgnoreCase)))
        {
            // Диск подключён с чужого IQN (прежний владелец номера ещё работает): не трогаем и не делим.
            return null;
        }

        if ((sessions.Count > 0 && !(atBoot && attached == false)) || (atBoot && attached == true))
        {
            // Диском пользуется этот ПК (служба помощника перезапущена без перезагрузки): не сбрасываем и не меняем версию.
            // Отдаём и «неготовый» (failed после сбоя TrueNAS) — таргет живой, раз к нему подключены: иначе помощник отпустил
            // бы диск игрока, а сервер потом пересоздал бы его посреди сеанса.
            return seat is { TargetIqn: not null } && (sessions.Count > 0 || seat.TargetName == name) ? Assignment(seat) : null;
        }

        if (!atBoot && seat is { State: "failed" } && clock.GetUtcNow() - seat.UpdatedAt < RetryAfter)
        {
            return null; // недавний сбой сборки: отчёты не повторяют её чаще раза в RetryAfter
        }

        if (usable && seat!.BaseSnapshot == current.SnapshotId)
        {
            await storage.RollbackSnapshotAsync($"{seat.Zvol}@{DisklessOptions.CleanSnapshot}", ct, discardNewer: true);
            await repository.RecordResetAsync(machine.Id, clock.GetUtcNow());
            return Assignment(seat);
        }

        return Assignment(await RebuildAsync(machine, seat, current, initiators, ct));
    }

    private static bool Usable(SeatGameRow seat, MachineRow machine, List<string> initiators) =>
        seat.State == "ready" && seat.TargetName == TargetName(machine.Number) && seat.InitiatorIqn == string.Join(' ', initiators);

    /// <summary>IQN, которым ПК подключается: сообщённый помощником; у бездискового — ещё и имя места из iBFT.</summary>
    private List<string> Initiators(MachineRow machine)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(machine.InitiatorIqn))
        {
            list.Add(machine.InitiatorIqn);
        }

        if (machine.BootMode == "diskless")
        {
            list.Add(diskless.SeatInitiator(machine.Number));
        }

        return list.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private VolumeAssignment Assignment(SeatGameRow seat) =>
        new(seat.LibraryVersion ?? "", options.PortalAddress, seat.TargetIqn!, ReadOnly: false, options.DriveLetter, seat.ChapUser, seat.ChapSecret);

    private async Task<SeatGameRow> RebuildAsync(MachineRow machine, SeatGameRow? old, LibraryVersion version, IReadOnlyList<string> initiators, CancellationToken ct)
    {
        var name = TargetName(machine.Number);
        if (old is not null)
        {
            await repository.MarkRebuildingAsync(machine.Id, clock.GetUtcNow());
            await TearDownAsync(old, keepAuth: true, ct);
        }

        await ClaimNameAsync(machine, name, initiators, ct);
        if (await storage.GetDatasetAsync(options.PersonalParent, ct) is null)
        {
            throw new InvalidOperationException($"{options.PersonalParent} does not exist: create the dataset in TrueNAS (Library:PersonalParent)");
        }

        var seat = new SeatGameRow
        {
            MachineId = machine.Id,
            Zvol = $"{options.PersonalParent}/{name}",
            TargetName = name,
            InitiatorIqn = string.Join(' ', initiators),
            BaseSnapshot = version.SnapshotId,
            LibraryVersion = version.Label,
            ChapUser = name,
            ChapSecret = NewSecret(),
            AuthTag = old?.AuthTag,
        };
        await repository.UpsertAsync(seat, clock.GetUtcNow());

        var labels = new Dictionary<string, string> { ["clubsrv:role"] = "seat-games", ["clubsrv:libver"] = version.Label };
        await storage.EnsureWritableCloneAsync(version.SnapshotId, seat.Zvol, labels, ct);
        await storage.EnsureSnapshotAsync(seat.Zvol, DisklessOptions.CleanSnapshot, labels, ct);

        var tag = await ChapTags.EnsureAsync(storage, seat.AuthTag, seat.ChapUser, seat.ChapSecret, ct);
        if (old is { AuthTag: { } oldTag } && old.ChapUser != seat.ChapUser)
        {
            // Номер места сменился: прежний пользователь — только после нового в той же группе (tag не освобождается).
            await storage.DeleteAuthAsync(oldTag, old.ChapUser, ct);
        }

        var group = await storage.EnsureInitiatorGroupAsync(GroupComment(name), initiators, ct);
        var extent = await storage.EnsureWritableExtentAsync(name, seat.Zvol, $"clubsrv personal games seat {machine.Number:D2}", ct);
        var target = await storage.EnsureChapTargetAsync(name, $"games seat {machine.Number}", options.PortalId, group.Id, tag, ct);
        await storage.EnsureLunAsync(target.Id, extent.Id, ct);

        switch (await verifier.EnsureVisibleAsync(name, initiators[0], ct))
        {
            case TargetCheck.Hidden:
                throw new InvalidOperationException($"target {name} is not visible to {initiators[0]} even after re-applying the iSCSI configuration");
            case TargetCheck.ProbeDenied:
                throw new InvalidOperationException($"the initiator group of {name} does not admit {initiators[0]}");
        }

        var targetIqn = $"{await storage.GetIscsiBasenameAsync(ct)}:{name}";
        await repository.MarkReadyAsync(machine.Id, tag, targetIqn, clock.GetUtcNow());
        logger.LogInformation("Seat {Seat}: personal games disk on library {Version}", machine.Number, version.Label);
        return (await repository.FindAsync(machine.Id))!;
    }

    /// <summary>
    /// Имя таргета должно быть свободно: диск другой машины с этим именем (её номер сменили) освобождается, объекты без
    /// записи — сносятся. Если к таргету подключён кто-то, кроме этого ПК, — ничего не трогаем, сборка ждёт.
    /// </summary>
    private async Task ClaimNameAsync(MachineRow machine, string name, IReadOnlyList<string> initiators, CancellationToken ct)
    {
        var holder = await repository.FindByTargetAsync(name);
        if (holder?.MachineId == machine.Id)
        {
            return;
        }

        var foreign = (await storage.GetSessionsAsync(ct))
            .FirstOrDefault(s => s.Target.EndsWith($":{name}", StringComparison.Ordinal) && !initiators.Contains(s.Initiator, StringComparer.OrdinalIgnoreCase));
        if (foreign is not null)
        {
            throw new InvalidOperationException($"{name} is in use by another PC ({foreign.Initiator}): turn that PC off or give one of them another seat number");
        }

        if (holder is null)
        {
            await TearDownAsync(new SeatGameRow { MachineId = machine.Id, Zvol = $"{options.PersonalParent}/{name}", TargetName = name, ChapUser = name }, keepAuth: false, ct);
            return;
        }

        logger.LogWarning("Personal games disk {Target} belonged to another machine {Machine}; removed for seat {Seat}", name, holder.MachineId, machine.Number);
        await TearDownAsync(holder, keepAuth: false, ct);
        await repository.DeleteAsync(holder.MachineId);
    }

    private static string GroupComment(string targetName) => $"clubsrv {targetName}";

    /// <summary>
    /// Таргет (force — ПК не подключён, висящая сессия не нужна), экстент, группа, CHAP, затем @clean и клон.
    /// <paramref name="keepAuth"/> — диск сейчас же пересоздаётся с тем же tag: CHAP остаётся, tag не освобождается.
    /// </summary>
    private async Task TearDownAsync(SeatGameRow seat, bool keepAuth, CancellationToken ct)
    {
        await storage.DeleteTargetAsync(seat.TargetName, force: true, ct);
        var extent = await storage.DeleteExtentAsync(seat.TargetName, ct);
        if (extent.Outcome == DeleteOutcome.Blocked)
        {
            throw new InvalidOperationException($"extent {seat.TargetName} not deleted: {extent.Reason}");
        }

        await storage.DeleteInitiatorGroupAsync(GroupComment(seat.TargetName), ct);
        if (!keepAuth && seat.AuthTag is { } tag)
        {
            await storage.DeleteAuthAsync(tag, seat.ChapUser, ct);
        }

        await storage.DeleteSnapshotAsync($"{seat.Zvol}@{DisklessOptions.CleanSnapshot}", ct);
        var zvol = await storage.DeleteDatasetAsync(seat.Zvol, ct, withSnapshots: true);
        if (zvol.Outcome == DeleteOutcome.Blocked)
        {
            throw new InvalidOperationException($"personal games disk {seat.Zvol} not deleted: {zvol.Reason}");
        }
    }

    private static string NewSecret()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(16, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }
}
