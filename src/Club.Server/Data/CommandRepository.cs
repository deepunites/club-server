using System.Text.Json;
using Club.Server.Agents;
using Dapper;
using Npgsql;

namespace Club.Server.Data;

/// <summary>Очередь команд сервер → агент: at-least-once, агент подтверждает по id (WS или REST).</summary>
public sealed class CommandRepository(NpgsqlDataSource db)
{
    static CommandRepository() => DapperSetup.Ensure();

    private sealed class CommandRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Payload { get; init; }
        public string? IssuedBy { get; init; }
        public Guid? Supersedes { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    public async Task<ServerCommandEnvelope> EnqueueAsync(Guid pcId, string name, JsonElement? payload, string? issuedBy, TimeSpan? ttl, Guid? supersedes, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var expiresAt = ttl is null ? (DateTimeOffset?)null : now + ttl.Value;
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO agent_commands (id, pc_id, name, payload, issued_by, supersedes, created_at, expires_at)
            VALUES (@id, @pcId, @name, @payload::jsonb, @issuedBy, @supersedes, @now, @expiresAt)
            """,
            new { id, pcId, name, payload = payload?.GetRawText(), issuedBy, supersedes, now, expiresAt });
        return new ServerCommandEnvelope(id, now, name, payload, issuedBy, supersedes, expiresAt);
    }

    /// <summary>Неподтверждённые и не истёкшие команды ПК в порядке постановки.</summary>
    public async Task<IReadOnlyList<ServerCommandEnvelope>> PendingAsync(Guid pcId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<CommandRow>(
            """
            SELECT id, name, payload::text AS Payload, issued_by AS IssuedBy, supersedes, created_at AS CreatedAt, expires_at AS ExpiresAt
            FROM agent_commands
            WHERE pc_id = @pcId AND acked_at IS NULL AND (expires_at IS NULL OR expires_at > @now)
            ORDER BY created_at, id
            """,
            new { pcId, now });
        return rows.Select(r => new ServerCommandEnvelope(
            r.Id, r.CreatedAt, r.Name,
            r.Payload is null ? null : JsonDocument.Parse(r.Payload).RootElement.Clone(),
            r.IssuedBy, r.Supersedes, r.ExpiresAt)).ToList();
    }

    public async Task<int> PendingCountAsync(Guid pcId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM agent_commands WHERE pc_id = @pcId AND acked_at IS NULL AND (expires_at IS NULL OR expires_at > @now)",
            new { pcId, now });
    }

    /// <summary>Подтверждение команды. Повторное подтверждение — не ошибка. <c>false</c>, если команды нет у этого ПК.</summary>
    public async Task<bool> AckAsync(Guid pcId, Guid commandId, CommandAck ack, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var exists = await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM agent_commands WHERE id = @commandId AND pc_id = @pcId)", new { commandId, pcId });
        if (!exists)
        {
            return false;
        }

        await c.ExecuteAsync(
            "UPDATE agent_commands SET acked_at = @now, ack = @ack::jsonb WHERE id = @commandId AND pc_id = @pcId AND acked_at IS NULL",
            new { commandId, pcId, now, ack = JsonSerializer.Serialize(ack, Api.ApiJson.Options) });
        return true;
    }
}
