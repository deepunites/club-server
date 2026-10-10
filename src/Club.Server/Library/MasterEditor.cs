using System.Security.Cryptography;
using Club.Server.Data;
using Club.TrueNas;

namespace Club.Server.Library;

/// <summary>Ожидание, а не сбой: ПК суперклиента ещё подключён к мастер-тому. Попытки не тратятся.</summary>
public sealed class StorageWaitException(string message) : Exception(message);

/// <summary>
/// Режим суперклиента: мастер-том открыт на запись одному ПК администратора, пока он обновляет игры.
/// Доступ — CHAP с новым секретом на каждое открытие и группа из одного IQN этого ПК. Порядок в TrueNAS важен:
/// группа инициаторов никогда не бывает пустой (пустая = доступ всем) и удаляется только после таргета (иначе
/// TrueNAS обнулит ссылку и откроет таргет всем). Закрытие ждёт, пока ПК отключится (таргет без force).
/// </summary>
public sealed class MasterEditor(
    MasterRepository master,
    LibraryRepository library,
    MachineRepository machines,
    TrueNasStorage storage,
    TargetVerifier verifier,
    LibraryOptions options,
    TimeProvider clock,
    ILogger<MasterEditor> logger)
{
    public const string ChapUser = "clubsrv-master";
    private const string InitiatorComment = "clubsrv master";

    public async Task<Guid> RequestOpenAsync(Guid machineId, string? requestedBy)
    {
        var machine = await machines.FindAsync(machineId) ?? throw new LibraryRequestException("noMachine", "Machine not found");
        if (!machine.Approved)
        {
            throw new LibraryRequestException("notApproved", "Machine is not approved");
        }

        if (string.IsNullOrWhiteSpace(machine.InitiatorIqn))
        {
            // IQN сообщает помощник; без него нельзя ограничить доступ одним ПК.
            throw new LibraryRequestException("noInitiator", "The helper on this PC has not reported its iSCSI initiator name yet");
        }

        if ((await library.OpenOperationsAsync()).Any(o => o.Kind == "publish"))
        {
            // Снапшот мастер-тома ещё впереди — открывать том на запись посреди публикации нельзя.
            throw new LibraryRequestException("publishPending", "A publish is in progress; open the master volume after it finishes");
        }

        var operation = await master.RequestOpenAsync(machineId, machine.InitiatorIqn!, ChapUser, NewSecret(), requestedBy, clock.GetUtcNow())
            ?? throw new LibraryRequestException("masterBusy", "The master volume is already open or being closed");
        logger.LogInformation("Master volume open for {Machine} requested by {User}", machine.Name, requestedBy);
        return operation;
    }

    public async Task<Guid> RequestCloseAsync(bool force, string? requestedBy)
    {
        var operation = await master.RequestCloseAsync(force, requestedBy, clock.GetUtcNow())
            ?? throw new LibraryRequestException("masterClosed", "The master volume is not open");
        logger.LogInformation("Master volume close requested by {User} (force: {Force})", requestedBy, force);
        return operation;
    }

    /// <summary>Шаги открытия. Каждый — ensure: после падения операция выполняется заново.</summary>
    public async Task OpenAsync(Func<string, Task> step, CancellationToken ct)
    {
        var state = await master.GetAsync();
        if (state.State != "opening")
        {
            // Открытие отменено закрытием (или уже выполнено) — делать нечего.
            return;
        }

        await step("auth");
        var tag = await ChapTags.EnsureAsync(storage, state.AuthTag, state.ChapUser!, state.ChapSecret!, ct);
        await master.SetAuthTagAsync(tag);

        await step("initiator");
        var group = await storage.EnsureInitiatorGroupAsync(InitiatorComment, state.InitiatorIqn!, ct);

        await step("extent");
        var extent = await storage.EnsureWritableExtentAsync(options.MasterExtentName, options.MasterZvol, "clubsrv master (superclient)", ct);

        await step("target");
        var target = await storage.EnsureChapTargetAsync(options.MasterTargetName, "club master", options.PortalId, group.Id, tag, ct);
        await storage.EnsureLunAsync(target.Id, extent.Id, ct);

        // Та же проверка, что у публикации, но от имени IQN этого ПК: таргет мастер-тома открыт только ему (discovery
        // без CHAP). Не провал: открытие на стенде работало и без неё, а ПК сам сообщит, если не подключится, —
        // но оговорка видна в панели у открытого тома.
        await step("verify");
        var warning = await verifier.EnsureVisibleAsync(options.MasterTargetName, state.InitiatorIqn!, ct) switch
        {
            TargetCheck.Hidden =>
                $"The master target is not visible to the superclient PC ({state.InitiatorIqn}) even after re-applying the iSCSI configuration; " +
                "if M: does not appear, restart the iSCSI service in TrueNAS (System → Services → iSCSI) — this drops every PC's session",
            TargetCheck.ProbeDenied =>
                $"The initiator group of the master target does not include the superclient PC ({state.InitiatorIqn}); close and open the master volume again",
            _ => null,
        };
        if (warning is not null)
        {
            logger.LogWarning("Master target {Target} opened with a warning: {Warning}", options.MasterTargetName, warning);
        }

        var basename = await storage.GetIscsiBasenameAsync(ct);
        await master.MarkOpenAsync($"{basename}:{options.MasterTargetName}", clock.GetUtcNow(), warning);
        logger.LogInformation("Master volume open for initiator {Iqn}", state.InitiatorIqn);
    }

    public Task OpenFailedAsync(string error) => master.MarkOpenFailedAsync(error, clock.GetUtcNow());

    /// <summary>
    /// Закрытие: таргет (без force — пока ПК подключён, ждём; с force — рвём сессию), затем экстент, группа
    /// инициаторов и CHAP. Экстент удаляется без удаления zvol: мастер-том остаётся.
    /// </summary>
    public async Task CloseAsync(Func<string, Task> step, CancellationToken ct)
    {
        var state = await master.GetAsync();
        if (state.State != "closing")
        {
            return;
        }

        await step("target");
        var target = await storage.DeleteTargetAsync(options.MasterTargetName, state.ForceClose, ct);
        if (target.Outcome == DeleteOutcome.Blocked)
        {
            var message = $"Waiting for the superclient PC to disconnect the master volume ({target.Reason})";
            await master.RecordWaitAsync(message, clock.GetUtcNow());
            throw new StorageWaitException(message);
        }

        await step("extent");
        var extent = await storage.DeleteExtentAsync(options.MasterExtentName, ct);
        if (extent.Outcome == DeleteOutcome.Blocked)
        {
            throw new InvalidOperationException($"master extent not deleted: {extent.Reason}");
        }

        await step("initiator");
        await storage.DeleteInitiatorGroupAsync(InitiatorComment, ct);

        await step("auth");
        if (state.AuthTag is { } tag && (await storage.DeleteAuthAsync(tag, state.ChapUser ?? ChapUser, ct)).Outcome == DeleteOutcome.Blocked)
        {
            throw new InvalidOperationException("CHAP credential of the master target is still in use");
        }

        await master.MarkClosedAsync(clock.GetUtcNow());
        logger.LogInformation("Master volume closed (force: {Force})", state.ForceClose);
    }

    /// <summary>16 символов из букв и цифр — предел и TrueNAS (12..16), и инициатора Windows.</summary>
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
