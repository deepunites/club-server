using System.Collections.Concurrent;
using System.Security.Cryptography;
using Club.Server.Data;
using Club.Server.Library;
using Club.TrueNas;

namespace Club.Server.Diskless;

/// <summary>Что отдать iPXE: скрипт загрузки или ожидание с повтором (сервер готовит диск дольше, чем ждёт iPXE).</summary>
public sealed record SeatBoot(string Script);

/// <summary>
/// Личные диски мест бездиска (docs/diskless-full.md §2–4). Перед каждой загрузкой место получает чистый диск:
/// откат клона к <c>@clean</c> (настройки iSCSI не меняются), а если вышла новая версия эталона — клон
/// пересоздаётся от неё. Режим мастера: машина грузится с самого zvol эталона, без клона и отката.
/// Подготовка идёт прямо в запросе iPXE; одновременные запросы одной машины ждут общую задачу.
/// </summary>
public sealed class SeatDisks(
    DisklessRepository repository,
    TrueNasStorage storage,
    TargetVerifier verifier,
    DisklessOptions options,
    LibraryOptions library,
    TimeProvider clock,
    ILogger<SeatDisks> logger)
{
    /// <summary>Сколько iPXE ждёт ответа, прежде чем получить «подождите» и спросить снова.</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Сколько после первой выдачи установщика Windows он выдаётся снова (не загрузился — повтор). Дальше мастер
    /// грузится с диска эталона: так установщик продолжает после своей первой перезагрузки (до неё — минуты копирования).
    /// </summary>
    public static readonly TimeSpan InstallRetryWindow = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<Guid, Lazy<Task<SeatBoot>>> _inFlight = new();

    public async Task<SeatBoot> PrepareBootAsync(MachineRow machine, CancellationToken ct)
    {
        var lazy = _inFlight.GetOrAdd(machine.Id, _ => new Lazy<Task<SeatBoot>>(() => RunAsync(machine)));
        var task = lazy.Value;
        var finished = await Task.WhenAny(task, Task.Delay(AnswerWithin, clock, ct));
        return finished == task ? await task : new SeatBoot(DisklessScripts.Wait(machine.Number, "preparing the seat disk"));
    }

    private async Task<SeatBoot> RunAsync(MachineRow machine)
    {
        try
        {
            return await PrepareAsync(machine, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Diskless boot of seat {Seat} ({Machine}) failed", machine.Number, machine.Name);
            await repository.MarkSeatFailedAsync(machine.Id, ex.Message, clock.GetUtcNow());
            return new SeatBoot(DisklessScripts.Failed(machine.Number, ex.Message));
        }
        finally
        {
            _inFlight.TryRemove(machine.Id, out _);
        }
    }

    private async Task<SeatBoot> PrepareAsync(MachineRow machine, CancellationToken ct)
    {
        var image = await repository.GetImageAsync();
        if (image.MasterMachineId == machine.Id)
        {
            var master = await EnsureMasterAsync(machine, ct);
            await repository.RecordBootAsync(machine.Id, clock.GetUtcNow());
            var install = image.MasterInstall && await repository.TakeInstallAsync(clock.GetUtcNow(), InstallRetryWindow);
            logger.LogInformation("Diskless: seat {Seat} boots the system image in master mode ({How})", machine.Number,
                install ? "Windows setup" : image.MasterInstall ? "setup continues from the disk" : "editing");
            return new SeatBoot(install
                ? DisklessScripts.Install(machine.Number, options.MasterInitiator, master, library.PortalAddress, options.SetupDirectory)
                : DisklessScripts.SanBoot(machine.Number, options.MasterInitiator, master, library.PortalAddress, "system image (master mode)"));
        }

        if (image.CurrentVersion is null || await repository.FindVersionAsync(image.CurrentVersion) is not { } version)
        {
            return new SeatBoot(DisklessScripts.Wait(machine.Number, "no system image is published yet"));
        }

        var seat = await repository.FindSeatAsync(machine.Id);
        var label = DisklessOptions.SeatLabel(machine.Number);
        var current = seat is { Kind: "seat", State: "ready" } && seat.TargetName == label && seat.BaseSnapshot == version.Snapshot;
        if (current)
        {
            if (await IsRunningAsync(machine, seat!, ct))
            {
                // Windows этого места работает с диском: запрос загрузки — не от него (или ПК ещё не отключился).
                return new SeatBoot(DisklessScripts.Wait(machine.Number, "this seat is still running from its disk"));
            }

            await storage.RollbackSnapshotAsync($"{seat!.Zvol}@{DisklessOptions.CleanSnapshot}", ct);
        }
        else
        {
            seat = await RebuildAsync(machine, seat, version.Snapshot, ct);
        }

        await repository.RecordBootAsync(machine.Id, clock.GetUtcNow());
        logger.LogInformation("Diskless: seat {Seat} boots image {Version} ({How})", machine.Number, version.Version, current ? "reset" : "new disk");
        return new SeatBoot(DisklessScripts.SanBoot(machine.Number, seat!.InitiatorIqn, seat, library.PortalAddress, $"image {version.Version}"));
    }

    /// <summary>
    /// Работает ли Windows места прямо сейчас: помощник отчитался недавно и сессия к диску есть. Только вместе:
    /// после перезагрузки сессия может висеть до таймаута iSCSI, а свежий отчёт без сессии — ПК уже в iPXE.
    /// </summary>
    private async Task<bool> IsRunningAsync(MachineRow machine, SeatDiskRow seat, CancellationToken ct)
    {
        if (machine.LastSeenAt is not { } seen || clock.GetUtcNow() - seen > TimeSpan.FromSeconds(options.RunningWindowSec))
        {
            return false;
        }

        var sessions = await storage.GetSessionsAsync(ct);
        return sessions.Any(s => s.Target.EndsWith($":{seat.TargetName}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Новый личный диск места от снапшота версии: старые таргет, экстент, клон (и прежние имена, если номер места
    /// сменился) — удаляются; затем клон, <c>@clean</c>, CHAP, группа из IQN места, экстент, таргет, LUN, проверка.
    /// </summary>
    private async Task<SeatDiskRow> RebuildAsync(MachineRow machine, SeatDiskRow? old, string snapshot, CancellationToken ct)
    {
        var label = DisklessOptions.SeatLabel(machine.Number);
        if (old is not null)
        {
            await TearDownAsync(old, keepAuth: true, ct);
        }

        await ClaimNameAsync(machine, label, new SeatDiskRow { MachineId = machine.Id, Kind = "seat", Zvol = options.SeatZvol(machine.Number), TargetName = label, ChapUser = label }, ct);

        if (await storage.GetDatasetAsync(options.SeatsParent, ct) is null)
        {
            throw new InvalidOperationException($"{options.SeatsParent} does not exist: create the dataset in TrueNAS (docs/install/club.md)");
        }

        var seat = new SeatDiskRow
        {
            MachineId = machine.Id,
            Kind = "seat",
            Zvol = options.SeatZvol(machine.Number),
            TargetName = label,
            InitiatorIqn = options.SeatInitiator(machine.Number),
            BaseSnapshot = snapshot,
            ChapUser = label,
            ChapSecret = NewSecret(),
            AuthTag = old?.AuthTag,
        };
        await repository.UpsertSeatAsync(seat, clock.GetUtcNow());

        var labels = new Dictionary<string, string> { ["clubsrv:role"] = "seat", ["clubsrv:seat"] = machine.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await storage.EnsureWritableCloneAsync(snapshot, seat.Zvol, labels, ct);
        await storage.EnsureSnapshotAsync(seat.Zvol, DisklessOptions.CleanSnapshot, labels, ct);
        return await DropOldChapAsync(old, await ExposeAsync(seat, $"clubsrv seat {machine.Number:D2}", ct), ct);
    }

    private async Task<SeatDiskRow> EnsureMasterAsync(MachineRow machine, CancellationToken ct)
    {
        var existing = await repository.FindSeatAsync(machine.Id);
        if (existing is { Kind: "master", State: "ready" } && existing.TargetName == options.MasterTargetName)
        {
            return existing;
        }

        if (existing is not null)
        {
            await TearDownAsync(existing, keepAuth: true, ct);
        }

        // Мастер-таргет прошлого мастера (другой машины) — освобождается, если с него сейчас никто не работает.
        await ClaimNameAsync(machine, options.MasterTargetName,
            new SeatDiskRow { MachineId = machine.Id, Kind = "master", Zvol = options.ImageZvol, TargetName = options.MasterTargetName, ChapUser = "diskless-master" }, ct);

        if (await storage.GetDatasetAsync(options.ImageZvol, ct) is null)
        {
            throw new InvalidOperationException($"{options.ImageZvol} does not exist: create the system image zvol in TrueNAS (docs/install/club.md)");
        }

        var seat = new SeatDiskRow
        {
            MachineId = machine.Id,
            Kind = "master",
            Zvol = options.ImageZvol,
            TargetName = options.MasterTargetName,
            InitiatorIqn = options.MasterInitiator,
            ChapUser = "diskless-master",
            ChapSecret = NewSecret(),
            AuthTag = existing?.AuthTag,
        };
        await repository.UpsertSeatAsync(seat, clock.GetUtcNow());
        return await DropOldChapAsync(existing, await ExposeAsync(seat, "clubsrv diskless master", ct), ct);
    }

    /// <summary>
    /// Имя таргета (номер места, мастер-таргет) должно быть свободно: диск другой машины с этим именем освобождается
    /// (её номер сменили), объекты без записи (прошлая жизнь базы) — сносятся. Но если к таргету есть сессия — им
    /// пользуется работающий ПК: ничего не трогаем (иначе он потеряет диск — синий экран), загрузка этого места ждёт.
    /// </summary>
    private async Task ClaimNameAsync(MachineRow machine, string targetName, SeatDiskRow orphan, CancellationToken ct)
    {
        var holder = await repository.FindSeatByTargetAsync(targetName);
        if (holder?.MachineId == machine.Id)
        {
            return; // своя запись — уже разобрана
        }

        if ((await storage.GetSessionsAsync(ct)).FirstOrDefault(s => s.Target.EndsWith($":{targetName}", StringComparison.Ordinal)) is { } session)
        {
            throw new InvalidOperationException(
                $"{targetName} is in use by another PC ({session.Initiator}): turn that PC off or give one of them another seat number");
        }

        if (holder is null)
        {
            await TearDownAsync(orphan, keepAuth: false, ct);
            return;
        }

        logger.LogWarning("Diskless: {Target} belonged to another machine {Machine}; its disk is removed for seat {Seat}", targetName, holder.MachineId, machine.Number);
        await TearDownAsync(holder, keepAuth: false, ct);
        await repository.DeleteSeatAsync(holder.MachineId);
    }

    /// <summary>
    /// Прежний CHAP-пользователь (номер места сменился) удаляется только после того, как новый создан в той же группе:
    /// иначе освободившийся tag мог бы достаться другому диску (<see cref="ChapTags"/>), и их CHAP смешались бы.
    /// </summary>
    private async Task<SeatDiskRow> DropOldChapAsync(SeatDiskRow? old, SeatDiskRow ready, CancellationToken ct)
    {
        if (old is { AuthTag: { } tag } && old.ChapUser != ready.ChapUser)
        {
            await storage.DeleteAuthAsync(tag, old.ChapUser, ct);
        }

        return ready;
    }

    /// <summary>CHAP, группа инициаторов из одного IQN, записываемый экстент, таргет, LUN и проверка видимости.</summary>
    private async Task<SeatDiskRow> ExposeAsync(SeatDiskRow seat, string groupComment, CancellationToken ct)
    {
        var tag = await ChapTags.EnsureAsync(storage, seat.AuthTag, seat.ChapUser, seat.ChapSecret, ct);
        var group = await storage.EnsureInitiatorGroupAsync(groupComment, seat.InitiatorIqn, ct);
        var extent = await storage.EnsureWritableExtentAsync(seat.TargetName, seat.Zvol, $"{groupComment} disk", ct);
        var target = await storage.EnsureChapTargetAsync(seat.TargetName, seat.TargetName, library.PortalId, group.Id, tag, ct);
        await storage.EnsureLunAsync(target.Id, extent.Id, ct);

        switch (await verifier.EnsureVisibleAsync(seat.TargetName, seat.InitiatorIqn, ct))
        {
            case TargetCheck.Hidden:
                throw new InvalidOperationException($"target {seat.TargetName} is not visible to {seat.InitiatorIqn} even after re-applying the iSCSI configuration");
            case TargetCheck.ProbeDenied:
                throw new InvalidOperationException($"the initiator group of {seat.TargetName} does not admit {seat.InitiatorIqn}");
        }

        var targetIqn = $"{await storage.GetIscsiBasenameAsync(ct)}:{seat.TargetName}";
        await repository.MarkSeatReadyAsync(seat.MachineId, tag, targetIqn, clock.GetUtcNow());
        return (await repository.FindSeatAsync(seat.MachineId))!;
    }

    /// <summary>
    /// Таргет (с force — ПК уже в iPXE, висящая сессия не нужна), экстент, группа, CHAP, затем @clean и клон.
    /// <paramref name="keepAuth"/> — диск сейчас же пересоздаётся с тем же tag: CHAP не удаляется, чтобы tag не
    /// освобождался (новый секрет запишет <see cref="ChapTags"/>, прежнего пользователя уберёт DropOldChapAsync).
    /// </summary>
    private async Task TearDownAsync(SeatDiskRow seat, bool keepAuth, CancellationToken ct)
    {
        await storage.DeleteTargetAsync(seat.TargetName, force: true, ct);
        var extent = await storage.DeleteExtentAsync(seat.TargetName, ct);
        if (extent.Outcome == DeleteOutcome.Blocked)
        {
            throw new InvalidOperationException($"extent {seat.TargetName} not deleted: {extent.Reason}");
        }

        await storage.DeleteInitiatorGroupAsync(seat.Kind == "master" ? "clubsrv diskless master" : $"clubsrv seat {seat.TargetName["seat-".Length..]}", ct);
        if (!keepAuth && seat.AuthTag is { } tag)
        {
            await storage.DeleteAuthAsync(tag, seat.ChapUser, ct);
        }

        if (seat.Kind == "seat")
        {
            await storage.DeleteSnapshotAsync($"{seat.Zvol}@{DisklessOptions.CleanSnapshot}", ct);
            var zvol = await storage.DeleteDatasetAsync(seat.Zvol, ct);
            if (zvol.Outcome == DeleteOutcome.Blocked)
            {
                throw new InvalidOperationException($"seat disk {seat.Zvol} not deleted: {zvol.Reason}");
            }
        }
    }

    /// <summary>16 символов из букв и цифр: предел TrueNAS (12..16) и инициатора; iPXE передаёт его как есть.</summary>
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
