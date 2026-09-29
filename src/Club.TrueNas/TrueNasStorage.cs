using System.Text.Json;

namespace Club.TrueNas;

public sealed record ZfsDataset(string Id, string Type, bool ReadOnly, string? Origin, IReadOnlyDictionary<string, string> Labels);

public sealed record ZfsSnapshot(string Id, string Dataset, IReadOnlyList<string> Clones, IReadOnlyDictionary<string, string> Labels);

public sealed record IscsiExtent(int Id, string Name, string Disk, bool ReadOnly, bool Enabled);

public sealed record IscsiTarget(int Id, string Name);

public sealed record IscsiTargetExtent(int Id, int Target, int Extent, int LunId);

public sealed record IscsiSession(string Initiator, string InitiatorAddress, string Target);

/// <summary>Учётные данные CHAP (iscsi.auth). Таргет ссылается на них по <c>tag</c>, не по id.</summary>
public sealed record IscsiAuth(int Id, int Tag, string User);

/// <summary>Группа инициаторов (iscsi.initiator). Пустой список initiators в TrueNAS = доступ всем.</summary>
public sealed record IscsiInitiatorGroup(int Id, IReadOnlyList<string> Initiators, string Comment);

public enum DeleteOutcome
{
    Deleted,
    AlreadyAbsent,

    /// <summary>Объект остался: занят (сессии, клоны, зависимости). Повторить в следующем цикле сверки.</summary>
    Blocked,
}

public sealed record DeleteResult(DeleteOutcome Outcome, string? Reason = null);

/// <summary>
/// Операции с томами и iSCSI для линии TrueNAS 25.10+ (<c>pool.snapshot.*</c>). Каждая мутация — ensure:
/// сначала query по точному ключу, после любой ошибки — повторный query; тексты ошибок не разбираются
/// (docs/research/truenas-api.md §9.2). Удаление никогда не использует force.
/// </summary>
public sealed class TrueNasStorage(TrueNasClient client)
{
    /// <summary>Префикс наших ZFS user properties: нижний регистр, без точек.</summary>
    public const string LabelPrefix = "clubsrv:";

    // ---- ZFS: датасеты и zvol ------------------------------------------------------------------------------------

    public async Task<ZfsDataset?> GetDatasetAsync(string id, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("pool.dataset.query", [new object[] { new object[] { "id", "=", id } }, new { extra = new { retrieve_children = false } }], ct);
        return rows.EnumerateArray().Select(ParseDataset).FirstOrDefault();
    }

    /// <summary>Датасеты, id которых начинается с префикса (фильтр <c>^</c> middleware).</summary>
    public async Task<IReadOnlyList<ZfsDataset>> ListDatasetsAsync(string idPrefix, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("pool.dataset.query", [new object[] { new object[] { "id", "^", idPrefix } }, new { extra = new { retrieve_children = true } }], ct);
        return rows.EnumerateArray().Select(ParseDataset).Where(d => d.Id.StartsWith(idPrefix, StringComparison.Ordinal)).ToList();
    }

    /// <summary>zvol с метками; уже существующий возвращается как есть (проверка — по факту, а не по тексту ошибки).</summary>
    public async Task<ZfsDataset> EnsureZvolAsync(string id, long volsizeBytes, string volblocksize, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        if (await GetDatasetAsync(id, ct) is { } existing)
        {
            return existing;
        }

        TrueNasRpcException? failure = null;
        try
        {
            await client.CallAsync("pool.dataset.create", [new
            {
                name = id,
                type = "VOLUME",
                volsize = volsizeBytes,
                volblocksize,
                sparse = true,
                user_properties = LabelList(labels),
            }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            // Решение принимается ниже по факту: объект есть — цель достигнута.
            failure = ex;
        }

        return await GetDatasetAsync(id, ct) ?? throw new InvalidOperationException($"zvol {id} was not created", failure);
    }

    /// <summary>Доклеивает метки (после сбоя NAS create/clone могут оставить объект без них).</summary>
    public async Task SetLabelsAsync(string datasetId, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default) =>
        await client.CallAsync("pool.dataset.update", [datasetId, new { user_properties_update = LabelList(labels) }], ct);

    /// <summary>
    /// Удаление zvol/клона. Предохранители: <c>pool.dataset.attachments</c> должен быть пуст — иначе middleware каскадно
    /// удалит iSCSI-экстенты под подключёнными клиентами; force не передаётся никогда.
    /// </summary>
    public async Task<DeleteResult> DeleteDatasetAsync(string id, CancellationToken ct = default)
    {
        if (await GetDatasetAsync(id, ct) is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        var attachments = await client.CallAsync("pool.dataset.attachments", [id], ct);
        if (attachments.ValueKind == JsonValueKind.Array && attachments.GetArrayLength() > 0)
        {
            return new DeleteResult(DeleteOutcome.Blocked, $"dataset has attachments: {attachments}");
        }

        try
        {
            await client.CallAsync("pool.dataset.delete", [id, new { recursive = false, force = false }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            return await GetDatasetAsync(id, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, ex.Reason ?? ex.Message);
        }

        return await GetDatasetAsync(id, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, "still present after delete");
    }

    // ---- ZFS: снапшоты и клоны -----------------------------------------------------------------------------------

    public async Task<ZfsSnapshot?> GetSnapshotAsync(string id, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("pool.snapshot.query", [new object[] { new object[] { "id", "=", id } }, new { extra = new { properties = new[] { "clones" } } }], ct);
        return rows.EnumerateArray().Select(ParseSnapshot).FirstOrDefault();
    }

    public async Task<ZfsSnapshot> EnsureSnapshotAsync(string dataset, string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var id = $"{dataset}@{name}";
        if (await GetSnapshotAsync(id, ct) is { } existing)
        {
            return existing;
        }

        TrueNasRpcException? failure = null;
        try
        {
            // Метки снапшота пишутся в той же txg, что и сам снапшот.
            await client.CallAsync("pool.snapshot.create", [new { dataset, name, properties = labels }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            // Решение принимается ниже по факту: объект есть — цель достигнута.
            failure = ex;
        }

        return await GetSnapshotAsync(id, ct) ?? throw new InvalidOperationException($"snapshot {id} was not created", failure);
    }

    /// <summary>RO-клон с метками одним вызовом. Это не одна транзакция ZFS: после сбоя NAS readonly и метки доводятся отдельно.</summary>
    public async Task<ZfsDataset> EnsureReadOnlyCloneAsync(string snapshot, string target, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var clone = await GetDatasetAsync(target, ct);
        if (clone is null)
        {
            var properties = new Dictionary<string, string>(labels) { ["readonly"] = "on" };
            TrueNasRpcException? failure = null;
            try
            {
                await client.CallAsync("pool.snapshot.clone", [new { snapshot, dataset_dst = target, dataset_properties = properties }], ct);
            }
            catch (TrueNasRpcException ex)
            {
                // Решение принимается ниже по факту: объект есть — цель достигнута.
                failure = ex;
            }

            clone = await GetDatasetAsync(target, ct) ?? throw new InvalidOperationException($"clone {target} was not created", failure);
        }

        if (clone.Origin != snapshot)
        {
            throw new InvalidOperationException($"{target} exists but is not a clone of {snapshot} (origin '{clone.Origin}')");
        }

        if (!clone.ReadOnly)
        {
            // Через update, а не повторным clone: middleware сам выставит ro у включённого экстента.
            await client.CallAsync("pool.dataset.update", [target, new { @readonly = "ON" }], ct);
        }

        var missing = labels.Where(l => !clone.Labels.TryGetValue(l.Key, out var v) || v != l.Value).ToDictionary();
        if (missing.Count > 0)
        {
            await SetLabelsAsync(target, missing, ct);
        }

        return await GetDatasetAsync(target, ct) ?? throw new InvalidOperationException($"clone {target} disappeared");
    }

    /// <summary>
    /// Удаление снапшота всегда с <c>defer=true</c>: без клонов ZFS удаляет сразу, с клонами — сам, когда уйдёт последний
    /// клон. Так не нужно полагаться на свойство <c>clones</c> (его формат на стенде не проверен).
    /// </summary>
    public async Task<DeleteResult> DeleteSnapshotAsync(string id, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(id, ct);
        if (snapshot is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        try
        {
            await client.CallAsync("pool.snapshot.delete", [id, new { defer = true, recursive = false }], ct);
        }
        catch (TrueNasRpcException ex) when (ex.IsNotFound)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }
        catch (TrueNasRpcException ex)
        {
            return await GetSnapshotAsync(id, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, ex.Reason ?? ex.Message);
        }

        return await GetSnapshotAsync(id, ct) is null
            ? new DeleteResult(DeleteOutcome.Deleted)
            : new DeleteResult(DeleteOutcome.Blocked, "deferred: snapshot still has clones");
    }

    // ---- iSCSI ---------------------------------------------------------------------------------------------------

    public async Task<string> GetIscsiBasenameAsync(CancellationToken ct = default) =>
        (await client.CallAsync("iscsi.global.config", [], ct)).GetProperty("basename").GetString() ?? "";

    public async Task<IscsiExtent?> GetExtentAsync(string name, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.extent.query", [new object[] { new object[] { "name", "=", name } }], ct);
        return rows.EnumerateArray().Select(e => new IscsiExtent(
            e.GetProperty("id").GetInt32(), e.GetProperty("name").GetString()!, e.GetProperty("disk").GetString() ?? "",
            e.GetProperty("ro").GetBoolean(), e.GetProperty("enabled").GetBoolean())).FirstOrDefault();
    }

    /// <summary>Экстент тома, сразу read-only. Сразу после clone узел /dev/zvol может ещё не появиться — вызывающий повторяет.</summary>
    public async Task<IscsiExtent> EnsureReadOnlyExtentAsync(string name, string zvol, string comment, CancellationToken ct = default)
    {
        var existing = await GetExtentAsync(name, ct);
        if (existing is null)
        {
            TrueNasRpcException? failure = null;
            try
            {
                await client.CallAsync("iscsi.extent.create", [new { name, type = "DISK", disk = "zvol/" + zvol, ro = true, comment }], ct);
            }
            catch (TrueNasRpcException ex)
            {
                // Решение принимается ниже по факту: объект есть — цель достигнута.
                failure = ex;
            }

            existing = await GetExtentAsync(name, ct) ?? throw new InvalidOperationException($"extent {name} was not created", failure);
        }

        if (existing.Disk != "zvol/" + zvol || !existing.ReadOnly)
        {
            // Правка живого экстента пересоздаёт LUN у всех клиентов (§8.5) — не чиним, сообщаем.
            throw new InvalidOperationException($"extent {name} exists with disk '{existing.Disk}', ro={existing.ReadOnly}; expected zvol/{zvol}, ro=true");
        }

        return existing;
    }

    public async Task<IscsiTarget?> GetTargetAsync(string name, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.target.query", [new object[] { new object[] { "name", "=", name } }], ct);
        return rows.EnumerateArray().Select(t => new IscsiTarget(t.GetProperty("id").GetInt32(), t.GetProperty("name").GetString()!)).FirstOrDefault();
    }

    /// <summary>Таргет версии тома. Группа инициаторов задаётся явно: пустая группа открывает таргет всем.</summary>
    public async Task<IscsiTarget> EnsureTargetAsync(string name, string alias, int portalId, int initiatorGroupId, CancellationToken ct = default)
    {
        if (await GetTargetAsync(name, ct) is { } existing)
        {
            return existing;
        }

        TrueNasRpcException? failure = null;
        try
        {
            await client.CallAsync("iscsi.target.create", [new
            {
                name,
                alias,
                mode = "ISCSI",
                groups = new[] { new { portal = portalId, initiator = initiatorGroupId, authmethod = "NONE" } },
            }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            // Решение принимается ниже по факту: объект есть — цель достигнута.
            failure = ex;
        }

        return await GetTargetAsync(name, ct) ?? throw new InvalidOperationException($"target {name} was not created", failure);
    }

    public async Task<IscsiTargetExtent?> GetTargetExtentAsync(int targetId, int extentId, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.targetextent.query", [new object[] { new object[] { "target", "=", targetId }, new object[] { "extent", "=", extentId } }], ct);
        return rows.EnumerateArray().Select(te => new IscsiTargetExtent(
            te.GetProperty("id").GetInt32(), te.GetProperty("target").GetInt32(), te.GetProperty("extent").GetInt32(), te.GetProperty("lunid").GetInt32())).FirstOrDefault();
    }

    public async Task<IscsiTargetExtent> EnsureLunAsync(int targetId, int extentId, CancellationToken ct = default)
    {
        if (await GetTargetExtentAsync(targetId, extentId, ct) is { } existing)
        {
            return existing;
        }

        TrueNasRpcException? failure = null;
        try
        {
            await client.CallAsync("iscsi.targetextent.create", [new { target = targetId, extent = extentId, lunid = 0 }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            // Решение принимается ниже по факту: объект есть — цель достигнута.
            failure = ex;
        }

        return await GetTargetExtentAsync(targetId, extentId, ct) ?? throw new InvalidOperationException($"LUN for target {targetId} was not created", failure);
    }

    /// <summary>
    /// Экстент мастер-тома на запись (для суперклиента). Существующий экстент с тем же именем, но read-only или на другом
    /// томе — ошибка, не правим.
    /// </summary>
    public async Task<IscsiExtent> EnsureWritableExtentAsync(string name, string zvol, string comment, CancellationToken ct = default)
    {
        var existing = await GetExtentAsync(name, ct);
        if (existing is null)
        {
            TrueNasRpcException? failure = null;
            try
            {
                await client.CallAsync("iscsi.extent.create", [new { name, type = "DISK", disk = "zvol/" + zvol, ro = false, comment }], ct);
            }
            catch (TrueNasRpcException ex)
            {
                failure = ex;
            }

            existing = await GetExtentAsync(name, ct) ?? throw new InvalidOperationException($"extent {name} was not created", failure);
        }

        if (existing.Disk != "zvol/" + zvol || existing.ReadOnly)
        {
            throw new InvalidOperationException($"extent {name} exists with disk '{existing.Disk}', ro={existing.ReadOnly}; expected zvol/{zvol}, ro=false");
        }

        return existing;
    }

    public async Task<IReadOnlyList<IscsiAuth>> ListAuthAsync(CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.auth.query", [], ct);
        return rows.EnumerateArray().Select(a => new IscsiAuth(a.GetProperty("id").GetInt32(), a.GetProperty("tag").GetInt32(), Str(a, "user"))).ToList();
    }

    /// <summary>
    /// Учётные данные CHAP для таргета: запись с этим <paramref name="user"/> и <paramref name="tag"/>; секрет обновляется
    /// (он новый на каждое открытие). Секрет — 12..16 символов (ограничение TrueNAS и Windows).
    /// </summary>
    public async Task<IscsiAuth> EnsureChapAsync(int tag, string user, string secret, CancellationToken ct = default)
    {
        if (secret.Length is < 12 or > 16)
        {
            throw new ArgumentException("CHAP secret must be 12..16 characters", nameof(secret));
        }

        var existing = (await ListAuthAsync(ct)).FirstOrDefault(a => a.User == user && a.Tag == tag);
        if (existing is null)
        {
            TrueNasRpcException? failure = null;
            try
            {
                await client.CallAsync("iscsi.auth.create", [new { tag, user, secret }], ct);
            }
            catch (TrueNasRpcException ex)
            {
                failure = ex;
            }

            return (await ListAuthAsync(ct)).FirstOrDefault(a => a.User == user && a.Tag == tag)
                ?? throw new InvalidOperationException($"CHAP credential {user} was not created", failure);
        }

        await client.CallAsync("iscsi.auth.update", [existing.Id, new { secret }], ct);
        return existing;
    }

    public async Task<DeleteResult> DeleteAuthAsync(int tag, string user, CancellationToken ct = default)
    {
        var existing = (await ListAuthAsync(ct)).FirstOrDefault(a => a.User == user && a.Tag == tag);
        if (existing is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        try
        {
            await client.CallAsync("iscsi.auth.delete", [existing.Id], ct);
        }
        catch (TrueNasRpcException ex)
        {
            return new DeleteResult(DeleteOutcome.Blocked, ex.Reason ?? ex.Message);
        }

        return new DeleteResult(DeleteOutcome.Deleted);
    }

    public async Task<IscsiInitiatorGroup?> GetInitiatorGroupAsync(string comment, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.initiator.query", [new object[] { new object[] { "comment", "=", comment } }], ct);
        return rows.EnumerateArray().Select(g => new IscsiInitiatorGroup(
            g.GetProperty("id").GetInt32(),
            g.GetProperty("initiators").EnumerateArray().Select(i => i.GetString() ?? "").ToList(),
            Str(g, "comment"))).FirstOrDefault();
    }

    /// <summary>
    /// Группа инициаторов из одного IQN. Пустой список в TrueNAS открывает таргет всем, поэтому IQN обязателен;
    /// существующая группа с другим IQN обновляется.
    /// </summary>
    public async Task<IscsiInitiatorGroup> EnsureInitiatorGroupAsync(string comment, string initiatorIqn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(initiatorIqn))
        {
            throw new ArgumentException("initiator IQN is required: an empty group grants access to everyone", nameof(initiatorIqn));
        }

        var existing = await GetInitiatorGroupAsync(comment, ct);
        if (existing is null)
        {
            TrueNasRpcException? failure = null;
            try
            {
                await client.CallAsync("iscsi.initiator.create", [new { initiators = new[] { initiatorIqn }, comment }], ct);
            }
            catch (TrueNasRpcException ex)
            {
                failure = ex;
            }

            return await GetInitiatorGroupAsync(comment, ct) ?? throw new InvalidOperationException($"initiator group {comment} was not created", failure);
        }

        if (existing.Initiators.Count != 1 || existing.Initiators[0] != initiatorIqn)
        {
            await client.CallAsync("iscsi.initiator.update", [existing.Id, new { initiators = new[] { initiatorIqn } }], ct);
        }

        return await GetInitiatorGroupAsync(comment, ct) ?? throw new InvalidOperationException($"initiator group {comment} disappeared");
    }

    /// <summary>Удаление группы — только когда на неё не ссылается таргет: иначе TrueNAS обнулит ссылку и откроет таргет всем.</summary>
    public async Task<DeleteResult> DeleteInitiatorGroupAsync(string comment, CancellationToken ct = default)
    {
        var existing = await GetInitiatorGroupAsync(comment, ct);
        if (existing is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        await client.CallAsync("iscsi.initiator.delete", [existing.Id], ct);
        return new DeleteResult(DeleteOutcome.Deleted);
    }

    /// <summary>Таргет с доступом по CHAP и группе из одного инициатора (мастер-том для суперклиента).</summary>
    public async Task<IscsiTarget> EnsureChapTargetAsync(string name, string alias, int portalId, int initiatorGroupId, int authTag, CancellationToken ct = default)
    {
        if (await GetTargetAsync(name, ct) is { } existing)
        {
            var groups = await TargetGroupsAsync(existing.Id, ct);
            if (groups.Count != 1 || groups[0] != (portalId, initiatorGroupId, "CHAP", authTag))
            {
                throw new InvalidOperationException($"target {name} exists with other access groups; expected CHAP via initiator group {initiatorGroupId}");
            }

            return existing;
        }

        TrueNasRpcException? failure = null;
        try
        {
            await client.CallAsync("iscsi.target.create", [new
            {
                name,
                alias,
                mode = "ISCSI",
                groups = new[] { new { portal = portalId, initiator = initiatorGroupId, authmethod = "CHAP", auth = authTag } },
            }], ct);
        }
        catch (TrueNasRpcException ex)
        {
            failure = ex;
        }

        return await GetTargetAsync(name, ct) ?? throw new InvalidOperationException($"target {name} was not created", failure);
    }

    public async Task<IReadOnlyList<(int Portal, int? Initiator, string AuthMethod, int? Auth)>> TargetGroupsAsync(int targetId, CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.target.query", [new object[] { new object[] { "id", "=", targetId } }], ct);
        var target = rows.EnumerateArray().FirstOrDefault();
        if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return groups.EnumerateArray().Select(g => (
            g.GetProperty("portal").GetInt32(),
            g.TryGetProperty("initiator", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : (int?)null,
            Str(g, "authmethod"),
            g.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : (int?)null)).ToList();
    }

    /// <summary>Удаление таргета без force: пока есть сессии, middleware отказывает — это предохранитель, а не авария.</summary>
    public Task<DeleteResult> DeleteTargetAsync(string name, CancellationToken ct = default) => DeleteTargetAsync(name, force: false, ct);

    /// <summary>
    /// С <paramref name="force"/> TrueNAS рвёт сессии — только для принудительного закрытия мастер-тома, когда ПК
    /// суперклиента недоступен; библиотечные таргеты так не удаляются никогда.
    /// </summary>
    public async Task<DeleteResult> DeleteTargetAsync(string name, bool force, CancellationToken ct = default)
    {
        var target = await GetTargetAsync(name, ct);
        if (target is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        try
        {
            await client.CallAsync("iscsi.target.delete", [target.Id, force, false], ct);
        }
        catch (TrueNasRpcException ex)
        {
            return await GetTargetAsync(name, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, ex.Reason ?? ex.Message);
        }

        return await GetTargetAsync(name, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, "still present after delete");
    }

    public async Task<DeleteResult> DeleteExtentAsync(string name, CancellationToken ct = default)
    {
        var extent = await GetExtentAsync(name, ct);
        if (extent is null)
        {
            return new DeleteResult(DeleteOutcome.AlreadyAbsent);
        }

        try
        {
            await client.CallAsync("iscsi.extent.delete", [extent.Id, false, false], ct);
        }
        catch (TrueNasRpcException ex)
        {
            return await GetExtentAsync(name, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, ex.Reason ?? ex.Message);
        }

        return await GetExtentAsync(name, ct) is null ? new DeleteResult(DeleteOutcome.Deleted) : new DeleteResult(DeleteOutcome.Blocked, "still present after delete");
    }

    public async Task<IReadOnlyList<IscsiSession>> GetSessionsAsync(CancellationToken ct = default)
    {
        var rows = await client.CallAsync("iscsi.global.sessions", [], ct);
        return rows.ValueKind != JsonValueKind.Array
            ? []
            : rows.EnumerateArray().Select(s => new IscsiSession(
                Str(s, "initiator"), Str(s, "initiator_addr"), Str(s, "target"))).ToList();
    }

    // ---- разбор ответов ------------------------------------------------------------------------------------------

    private static object[] LabelList(IReadOnlyDictionary<string, string> labels)
    {
        foreach (var key in labels.Keys)
        {
            if (!key.StartsWith(LabelPrefix, StringComparison.Ordinal) || key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is ':' or '-' or '_')))
            {
                throw new ArgumentException($"label '{key}' must start with '{LabelPrefix}' and use [a-z0-9:-_]");
            }
        }

        return labels.Select(l => (object)new { key = l.Key, value = l.Value }).ToArray();
    }

    private static ZfsDataset ParseDataset(JsonElement d)
    {
        var labels = new Dictionary<string, string>();
        if (d.TryGetProperty("user_properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject().Where(p => p.Name.StartsWith(LabelPrefix, StringComparison.Ordinal)))
            {
                labels[p.Name] = RawValue(p.Value) ?? "";
            }
        }

        return new ZfsDataset(
            d.GetProperty("id").GetString()!,
            Str(d, "type"),
            string.Equals(d.TryGetProperty("readonly", out var ro) ? RawValue(ro) : null, "on", StringComparison.OrdinalIgnoreCase),
            // В 25.10 origin.value приходит в верхнем регистре; сравниваем по rawvalue.
            d.TryGetProperty("origin", out var origin) ? NullIfEmpty(RawValue(origin)) : null,
            labels);
    }

    private static ZfsSnapshot ParseSnapshot(JsonElement s)
    {
        var labels = new Dictionary<string, string>();
        var clones = new List<string>();
        if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                if (p.Name == "clones")
                {
                    clones.AddRange((RawValue(p.Value) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (p.Name.StartsWith(LabelPrefix, StringComparison.Ordinal))
                {
                    labels[p.Name] = RawValue(p.Value) ?? "";
                }
            }
        }

        return new ZfsSnapshot(s.GetProperty("id").GetString()!, Str(s, "dataset"), clones, labels);
    }

    private static string? RawValue(JsonElement property) => property.ValueKind switch
    {
        JsonValueKind.Object when property.TryGetProperty("rawvalue", out var raw) && raw.ValueKind == JsonValueKind.String => raw.GetString(),
        JsonValueKind.Object when property.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
        JsonValueKind.String => property.GetString(),
        _ => null,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) || value == "-" ? null : value;
}
