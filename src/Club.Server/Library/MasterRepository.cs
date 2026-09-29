using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Library;

/// <summary>Состояние мастер-тома: <c>closed → opening → open → closing → closed</c>; <c>failed</c> — открытие не удалось.</summary>
public sealed class MasterState
{
    public string State { get; init; } = "closed";
    public Guid? MachineId { get; init; }
    public string? InitiatorIqn { get; init; }
    public string? ChapUser { get; init; }
    public string? ChapSecret { get; init; }
    public int? AuthTag { get; init; }
    public string? TargetIqn { get; init; }
    public bool ForceClose { get; init; }
    public bool Dirty { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? CloseRequestedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed class MasterRepository(NpgsqlDataSource db)
{
    private const string Columns = """
        state, machine_id AS MachineId, initiator_iqn AS InitiatorIqn, chap_user AS ChapUser, chap_secret AS ChapSecret,
        auth_tag AS AuthTag, target_iqn AS TargetIqn, force_close AS ForceClose, dirty, opened_at AS OpenedAt,
        close_requested_at AS CloseRequestedAt, closed_at AS ClosedAt, last_error AS LastError
        """;

    static MasterRepository() => DapperSetup.Ensure();

    public async Task<MasterState> GetAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleAsync<MasterState>($"SELECT {Columns} FROM library_master");
    }

    /// <summary>Открытие — только из закрытого состояния; намерение и операция одной транзакцией.</summary>
    public async Task<Guid?> RequestOpenAsync(Guid machineId, string initiatorIqn, string chapUser, string chapSecret, string? requestedBy, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var updated = await c.ExecuteAsync(
            """
            UPDATE library_master SET state = 'opening', machine_id = @machineId, initiator_iqn = @initiatorIqn, chap_user = @chapUser,
                   chap_secret = @chapSecret, target_iqn = NULL, force_close = false, opened_at = NULL, close_requested_at = NULL,
                   closed_at = NULL, last_error = NULL, updated_at = @now
            WHERE state = 'closed'
            """,
            new { machineId, initiatorIqn, chapUser, chapSecret, now }, tx);
        if (updated != 1)
        {
            return null;
        }

        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            "INSERT INTO storage_operations (id, kind, status, requested_by, created_at, updated_at) VALUES (@id, 'masterOpen', 'pending', @requestedBy, @now, @now)",
            new { id, requestedBy, now }, tx);
        await tx.CommitAsync();
        return id;
    }

    /// <summary>Закрытие (или ужесточение до принудительного). Операция закрытия создаётся, если её ещё нет.</summary>
    public async Task<Guid?> RequestCloseAsync(bool force, string? requestedBy, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var updated = await c.ExecuteAsync(
            """
            UPDATE library_master SET state = 'closing', force_close = force_close OR @force,
                   close_requested_at = COALESCE(close_requested_at, @now), updated_at = @now
            WHERE state IN ('opening', 'open', 'closing', 'failed')
            """,
            new { force, now }, tx);
        if (updated != 1)
        {
            return null;
        }

        // Незавершённое открытие больше не нужно: закрытие разберёт всё, что оно успело создать.
        await c.ExecuteAsync(
            "UPDATE storage_operations SET status = 'failed', last_error = 'superseded by close', updated_at = @now WHERE kind = 'masterOpen' AND status IN ('pending', 'running', 'failed')",
            new { now }, tx);
        var existing = await c.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM storage_operations WHERE kind = 'masterClose' AND status IN ('pending', 'running') LIMIT 1", transaction: tx);
        var id = existing ?? Guid.NewGuid();
        if (existing is null)
        {
            await c.ExecuteAsync(
                "INSERT INTO storage_operations (id, kind, status, requested_by, created_at, updated_at) VALUES (@id, 'masterClose', 'pending', @requestedBy, @now, @now)",
                new { id, requestedBy, now }, tx);
        }

        await tx.CommitAsync();
        return id;
    }

    public async Task SetAuthTagAsync(int tag)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE library_master SET auth_tag = @tag", new { tag });
    }

    public async Task MarkOpenAsync(string targetIqn, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE library_master SET state = 'open', target_iqn = @targetIqn, opened_at = @now, last_error = NULL, updated_at = @now WHERE state = 'opening'",
            new { targetIqn, now });
    }

    public async Task MarkOpenFailedAsync(string error, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE library_master SET state = 'failed', last_error = @error, updated_at = @now WHERE state = 'opening'", new { error, now });
    }

    /// <summary>Закрыто. Принудительное закрытие оставляет NTFS мастер-тома, возможно, недописанной — пометка dirty.</summary>
    public async Task MarkClosedAsync(DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE library_master SET state = 'closed', dirty = force_close, machine_id = NULL, chap_secret = NULL, target_iqn = NULL,
                   closed_at = @now, last_error = NULL, updated_at = @now
            WHERE state = 'closing'
            """,
            new { now });
    }

    public async Task RecordWaitAsync(string message, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE library_master SET last_error = @message, updated_at = @now", new { message, now });
    }

    /// <summary>Администратор подтвердил, что публикация с «грязного» мастер-тома допустима, либо том закрыт чисто.</summary>
    public async Task ClearDirtyAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE library_master SET dirty = false");
    }
}
