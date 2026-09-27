using System.Text.Json;
using Club.Server.Agents;
using Dapper;
using Npgsql;

namespace Club.Server.Data;

public sealed class PcRow
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string ZoneId { get; init; } = "";
    public string Hwid { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public bool Approved { get; init; }
    public bool Maintenance { get; init; }
    public string ReportedStatus { get; init; } = "offline";
    public string AgentVersion { get; init; } = "";
    public string ShellVersion { get; init; } = "";
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public int ConfigVersion { get; init; }
    public byte[] SigningSecret { get; init; } = [];
    public int CredentialsVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ZoneRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Policy { get; init; } = "{}";
    public int PolicyVersion { get; init; }
    public DateTimeOffset PolicyUpdatedAt { get; init; }
    public string? Storage { get; init; }
    public int ConfigVersion { get; init; }
}

public sealed class RefreshTokenRow
{
    public Guid PcId { get; init; }
    public int CredentialsVersion { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
}

public sealed class PcRepository(NpgsqlDataSource db)
{
    private const string PcColumns = """
        id, number, name, zone_id AS ZoneId, hwid, ip_address AS IpAddress, approved, maintenance,
        reported_status AS ReportedStatus, agent_version AS AgentVersion, shell_version AS ShellVersion,
        last_heartbeat_at AS LastHeartbeatAt, config_version AS ConfigVersion, signing_secret AS SigningSecret,
        credentials_version AS CredentialsVersion, created_at AS CreatedAt
        """;

    static PcRepository() => DapperSetup.Ensure();

    public async Task<PcRow?> FindAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<PcRow>($"SELECT {PcColumns} FROM pcs WHERE id = @id", new { id });
    }

    public async Task<PcRow?> FindByHwidAsync(string hwid)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<PcRow>($"SELECT {PcColumns} FROM pcs WHERE hwid = @hwid", new { hwid });
    }

    public async Task<ZoneRow> GetZoneAsync(string id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleAsync<ZoneRow>(
            "SELECT id, name, policy::text AS Policy, policy_version AS PolicyVersion, policy_updated_at AS PolicyUpdatedAt, storage::text AS Storage, config_version AS ConfigVersion FROM zones WHERE id = @id",
            new { id });
    }

    /// <summary>Новый ПК: следующий свободный номер места, зона по умолчанию.</summary>
    public async Task<PcRow> CreateAsync(AgentRegisterRequest request, bool approved, byte[] signingSecret)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("LOCK TABLE pcs IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        var number = await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(number), 0) + 1 FROM pcs", transaction: tx);
        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            """
            INSERT INTO pcs (id, number, name, zone_id, hwid, machine_name, mac_address, ip_address, hardware, approved,
                             maintenance, agent_version, signing_secret)
            VALUES (@id, @number, @name, 'standard', @hwid, @machineName, @mac, @ip, @hardware::jsonb, @approved,
                    NOT @approved, @agentVersion, @secret)
            """,
            new
            {
                id, number, name = $"PC-{number:D2}", hwid = request.Hwid, machineName = request.MachineName,
                mac = request.MacAddress, ip = request.IpAddress, hardware = request.Hardware.GetRawText(),
                approved, agentVersion = request.AgentVersion, secret = signingSecret,
            },
            tx);
        await tx.CommitAsync();
        return (await FindAsync(id))!;
    }

    /// <summary>Повторная регистрация известного ПК: новый секрет подписи, прежние токены отзываются.</summary>
    public async Task<PcRow> ReissueCredentialsAsync(Guid id, AgentRegisterRequest request, byte[] signingSecret)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE pcs SET signing_secret = @secret, credentials_version = credentials_version + 1,
                           machine_name = @machineName, mac_address = @mac, ip_address = @ip, hardware = @hardware::jsonb,
                           agent_version = @agentVersion
            WHERE id = @id
            """,
            new
            {
                id, secret = signingSecret, machineName = request.MachineName, mac = request.MacAddress,
                ip = request.IpAddress, hardware = request.Hardware.GetRawText(), agentVersion = request.AgentVersion,
            });
        await c.ExecuteAsync("DELETE FROM agent_refresh_tokens WHERE pc_id = @id", new { id });
        return (await FindAsync(id))!;
    }

    public async Task StoreRefreshTokenAsync(byte[] hash, Guid pcId, int credentialsVersion, DateTimeOffset expiresAt)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "INSERT INTO agent_refresh_tokens (token_hash, pc_id, credentials_version, expires_at) VALUES (@hash, @pcId, @credentialsVersion, @expiresAt)",
            new { hash, pcId, credentialsVersion, expiresAt });
    }

    /// <summary>Атомарно помечает refresh-токен использованным. Возвращает строку до пометки или <c>null</c>.</summary>
    public async Task<(RefreshTokenRow? Row, bool AlreadyUsed)> ConsumeRefreshTokenAsync(byte[] hash, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var consumed = await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            """
            UPDATE agent_refresh_tokens SET used_at = @now
            WHERE token_hash = @hash AND used_at IS NULL
            RETURNING pc_id AS PcId, credentials_version AS CredentialsVersion, expires_at AS ExpiresAt, NULL::timestamptz AS UsedAt
            """,
            new { hash, now });
        if (consumed is not null)
        {
            return (consumed, false);
        }

        var existing = await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            "SELECT pc_id AS PcId, credentials_version AS CredentialsVersion, expires_at AS ExpiresAt, used_at AS UsedAt FROM agent_refresh_tokens WHERE token_hash = @hash",
            new { hash });
        return (existing, existing is not null);
    }

    /// <summary>Отзыв всех токенов ПК (повтор одноразового refresh-токена — признак кражи).</summary>
    public async Task RevokeCredentialsAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE pcs SET credentials_version = credentials_version + 1 WHERE id = @id", new { id });
        await c.ExecuteAsync("DELETE FROM agent_refresh_tokens WHERE pc_id = @id", new { id });
    }

    public async Task RecordHeartbeatAsync(Guid id, HeartbeatRequest request, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE pcs SET last_heartbeat_at = @now, reported_status = @status, agent_version = @agentVersion,
                           shell_version = @shellVersion, ip_address = @ip
            WHERE id = @id
            """,
            new
            {
                id, now, status = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
                agentVersion = request.AgentVersion, shellVersion = request.ShellVersion, ip = request.IpAddress,
            });
    }
}
