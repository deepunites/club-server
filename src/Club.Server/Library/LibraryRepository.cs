using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Library;

public sealed class LibraryVersion
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
    public string State { get; init; } = "";
    public string SnapshotId { get; init; } = "";
    public string CloneId { get; init; } = "";
    public string ExtentName { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string? TargetIqn { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed class StorageOperation
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = "";
    public Guid? VersionId { get; init; }
    public string Status { get; init; } = "";
    public string? Step { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
}

public sealed class StorageWarning
{
    public string Kind { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

public sealed record LibraryPointers(LibraryVersion? Current, LibraryVersion? Rollback);

public sealed class LibraryRepository(NpgsqlDataSource db)
{
    private const string VersionColumns = """
        id, label, state, snapshot_id AS SnapshotId, clone_id AS CloneId, extent_name AS ExtentName,
        target_name AS TargetName, target_iqn AS TargetIqn, created_at AS CreatedAt, published_at AS PublishedAt,
        last_error AS LastError
        """;

    static LibraryRepository() => DapperSetup.Ensure();

    public async Task<LibraryVersion?> FindVersionAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<LibraryVersion>($"SELECT {VersionColumns} FROM library_versions WHERE id = @id", new { id });
    }

    public async Task<LibraryVersion?> FindVersionAsync(string label)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<LibraryVersion>($"SELECT {VersionColumns} FROM library_versions WHERE label = @label", new { label });
    }

    public async Task<IReadOnlyList<LibraryVersion>> VersionsInStateAsync(params string[] states)
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<LibraryVersion>($"SELECT {VersionColumns} FROM library_versions WHERE state = ANY(@states) ORDER BY created_at", new { states })).ToList();
    }

    public async Task<LibraryPointers> PointersAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var ids = await c.QuerySingleAsync<(Guid? Current, Guid? Rollback)>("SELECT current_version_id, rollback_version_id FROM library_state");
        return new LibraryPointers(
            ids.Current is { } cur ? await FindVersionAsync(cur) : null,
            ids.Rollback is { } rb ? await FindVersionAsync(rb) : null);
    }

    /// <summary>Намерение «версия должна существовать» и операция публикации — одной транзакцией.</summary>
    public async Task<(LibraryVersion Version, Guid OperationId)> CreatePublishAsync(LibraryVersion version, string? requestedBy, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO library_versions (id, label, state, snapshot_id, clone_id, extent_name, target_name, created_at)
            VALUES (@Id, @Label, 'publishing', @SnapshotId, @CloneId, @ExtentName, @TargetName, @now)
            """,
            new { version.Id, version.Label, version.SnapshotId, version.CloneId, version.ExtentName, version.TargetName, now }, tx);
        var operationId = Guid.NewGuid();
        await c.ExecuteAsync(
            "INSERT INTO storage_operations (id, kind, version_id, status, requested_by, created_at, updated_at) VALUES (@operationId, 'publish', @versionId, 'pending', @requestedBy, @now, @now)",
            new { operationId, versionId = version.Id, requestedBy, now }, tx);
        await tx.CommitAsync();
        return ((await FindVersionAsync(version.Id))!, operationId);
    }

    public async Task<Guid> CreateOperationAsync(string kind, Guid? versionId, string? requestedBy, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "INSERT INTO storage_operations (id, kind, version_id, status, requested_by, created_at, updated_at) VALUES (@id, @kind, @versionId, 'pending', @requestedBy, @now, @now)",
            new { id, kind, versionId, requestedBy, now });
        return id;
    }

    /// <summary>Незавершённые операции по порядку. running после падения сервера выполняется заново: шаги идемпотентны.</summary>
    public async Task<IReadOnlyList<StorageOperation>> OpenOperationsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<StorageOperation>(
            "SELECT id, kind, version_id AS VersionId, status, step, attempts, last_error AS LastError FROM storage_operations WHERE status IN ('pending', 'running') ORDER BY created_at, id")).ToList();
    }

    public async Task<StorageOperation?> FindOperationAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<StorageOperation>(
            "SELECT id, kind, version_id AS VersionId, status, step, attempts, last_error AS LastError FROM storage_operations WHERE id = @id", new { id });
    }

    public async Task MarkOperationAsync(Guid id, string status, string? step, string? error, bool countAttempt, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE storage_operations
            SET status = @status, step = COALESCE(@step, step), last_error = @error, updated_at = @now,
                attempts = attempts + CASE WHEN @countAttempt THEN 1 ELSE 0 END
            WHERE id = @id
            """,
            new { id, status, step, error, countAttempt, now });
    }

    public async Task SetVersionIqnAsync(Guid id, string iqn)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE library_versions SET target_iqn = @iqn WHERE id = @id", new { id, iqn });
    }

    public async Task SetVersionStateAsync(Guid id, string state, string? error, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE library_versions SET state = @state, last_error = @error,
                   retired_at = CASE WHEN @state = 'retired' THEN @now ELSE retired_at END
            WHERE id = @id
            """,
            new { id, state, error, now });
    }

    /// <summary>
    /// Новая версия становится текущей, прежняя текущая — откатной, прежняя откатная уходит на разборку.
    /// Версии конфига всех зон растут в той же транзакции: агенты получат новый targetIqn со следующим heartbeat.
    /// </summary>
    public async Task<Guid?> PromoteAsync(Guid versionId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var state = await c.QuerySingleAsync<(Guid? Current, Guid? Rollback)>(
            "SELECT current_version_id, rollback_version_id FROM library_state FOR UPDATE", transaction: tx);
        if (state.Current == versionId)
        {
            await tx.CommitAsync();
            return null;
        }

        Guid? retiring = state.Rollback is { } rb && rb != versionId ? rb : null;
        await c.ExecuteAsync(
            "UPDATE library_state SET current_version_id = @versionId, rollback_version_id = @previous, updated_at = @now",
            new { versionId, previous = state.Current, now }, tx);
        await c.ExecuteAsync(
            "UPDATE library_versions SET state = 'published', published_at = COALESCE(published_at, @now), last_error = NULL WHERE id = @versionId",
            new { versionId, now }, tx);
        if (retiring is { } r)
        {
            await c.ExecuteAsync("UPDATE library_versions SET state = 'retiring' WHERE id = @r", new { r }, tx);
        }

        await c.ExecuteAsync("UPDATE zones SET config_version = config_version + 1", transaction: tx);
        await tx.CommitAsync();
        return retiring;
    }

    /// <summary>Откат: текущая и откатная меняются местами. <c>false</c>, если откатной версии нет.</summary>
    public async Task<bool> SwapAsync(DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var swapped = await c.ExecuteAsync(
            """
            UPDATE library_state SET current_version_id = rollback_version_id, rollback_version_id = current_version_id, updated_at = @now
            WHERE rollback_version_id IS NOT NULL
            """,
            new { now }, tx);
        if (swapped == 0)
        {
            await tx.CommitAsync();
            return false;
        }

        await c.ExecuteAsync("UPDATE zones SET config_version = config_version + 1", transaction: tx);
        await tx.CommitAsync();
        return true;
    }

    // ---- предупреждения сверки -------------------------------------------------------------------------------

    /// <summary>Заменяет набор активных предупреждений: новые добавляются, повторные обновляют last_seen, пропавшие закрываются.</summary>
    public async Task ReplaceWarningsAsync(IReadOnlyCollection<(string Kind, string Subject, string Message)> warnings, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        foreach (var (kind, subject, message) in warnings)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO storage_warnings (kind, subject, message, first_seen, last_seen)
                VALUES (@kind, @subject, @message, @now, @now)
                ON CONFLICT (kind, subject) DO UPDATE SET message = EXCLUDED.message, last_seen = @now,
                    first_seen = CASE WHEN storage_warnings.resolved_at IS NULL THEN storage_warnings.first_seen ELSE @now END,
                    resolved_at = NULL
                """,
                new { kind, subject, message, now }, tx);
        }

        var keys = warnings.Select(w => w.Kind + "\u0001" + w.Subject).ToArray();
        await c.ExecuteAsync(
            "UPDATE storage_warnings SET resolved_at = @now WHERE resolved_at IS NULL AND NOT (kind || chr(1) || subject = ANY(@keys))",
            new { now, keys }, tx);
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<StorageWarning>> ActiveWarningsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<StorageWarning>(
            "SELECT kind, subject, message, first_seen AS FirstSeen, last_seen AS LastSeen FROM storage_warnings WHERE resolved_at IS NULL ORDER BY kind, subject")).ToList();
    }
}
