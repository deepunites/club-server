using Dapper;
using Npgsql;

namespace Club.Server.Data;

/// <summary>Машина бездиска: реестр (номер места, зона, MAC для DHCP/PXE) и последний отчёт помощника о томе.</summary>
public sealed class MachineRow
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string ZoneId { get; init; } = "";
    public string Hwid { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string[] MacAddresses { get; init; } = [];
    public string IpAddress { get; init; } = "";
    public bool Approved { get; init; }
    public bool Maintenance { get; init; }
    public string HelperVersion { get; init; } = "";
    public string? OsVersion { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? BootTime { get; init; }
    public string VolumeState { get; init; } = "none";
    public string? VolumeIqn { get; init; }
    public string? VolumeVersion { get; init; }
    public bool? VolumeRoVerified { get; init; }
    public string? VolumeError { get; init; }
    public string[]? DhcpServers { get; init; }
    public int CredentialsVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class RefreshTokenRow
{
    public Guid MachineId { get; init; }
    public int CredentialsVersion { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
}

public sealed record MachineRegistration(string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string IpAddress, string HelperVersion, string? OsVersion);

public sealed record VolumeReport(string State, string? TargetIqn, string? LibraryVersion, bool? ReadOnlyVerified, string? Error);

public sealed class MachineRepository(NpgsqlDataSource db)
{
    private const string Columns = """
        id, number, name, zone_id AS ZoneId, hwid, hostname, mac_addresses AS MacAddresses, ip_address AS IpAddress,
        approved, maintenance, helper_version AS HelperVersion, os_version AS OsVersion, last_seen_at AS LastSeenAt,
        boot_time AS BootTime, volume_state AS VolumeState, volume_iqn AS VolumeIqn, volume_version AS VolumeVersion,
        volume_ro_verified AS VolumeRoVerified, volume_error AS VolumeError, dhcp_servers AS DhcpServers, credentials_version AS CredentialsVersion,
        created_at AS CreatedAt
        """;

    static MachineRepository() => DapperSetup.Ensure();

    public async Task<MachineRow?> FindAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<MachineRow>($"SELECT {Columns} FROM machines WHERE id = @id", new { id });
    }

    public async Task<MachineRow?> FindByHwidAsync(string hwid)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<MachineRow>($"SELECT {Columns} FROM machines WHERE hwid = @hwid", new { hwid });
    }

    public async Task<IReadOnlyList<MachineRow>> AllAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<MachineRow>($"SELECT {Columns} FROM machines ORDER BY number")).ToList();
    }

    /// <summary>Новая машина: следующий свободный номер места, зона по умолчанию.</summary>
    public async Task<MachineRow> CreateAsync(MachineRegistration registration, bool approved)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("LOCK TABLE machines IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        var number = await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(number), 0) + 1 FROM machines", transaction: tx);
        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            """
            INSERT INTO machines (id, number, name, zone_id, hwid, hostname, mac_addresses, ip_address, approved, maintenance,
                                  helper_version, os_version)
            VALUES (@id, @number, @name, 'standard', @Hwid, @Hostname, @macs, @IpAddress, @approved, NOT @approved,
                    @HelperVersion, @OsVersion)
            """,
            new
            {
                id, number, name = $"PC-{number:D2}", registration.Hwid, registration.Hostname, macs = registration.MacAddresses.ToArray(),
                registration.IpAddress, approved, registration.HelperVersion, registration.OsVersion,
            },
            tx);
        await tx.CommitAsync();
        return (await FindAsync(id))!;
    }

    /// <summary>Повторная регистрация известной машины (переустановка помощника): прежние токены отзываются.</summary>
    public async Task<MachineRow> ReregisterAsync(Guid id, MachineRegistration registration)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE machines SET credentials_version = credentials_version + 1, hostname = @Hostname, mac_addresses = @macs,
                                ip_address = @IpAddress, helper_version = @HelperVersion, os_version = @OsVersion
            WHERE id = @id
            """,
            new { id, registration.Hostname, macs = registration.MacAddresses.ToArray(), registration.IpAddress, registration.HelperVersion, registration.OsVersion });
        await c.ExecuteAsync("DELETE FROM machine_refresh_tokens WHERE machine_id = @id", new { id });
        return (await FindAsync(id))!;
    }

    public async Task StoreRefreshTokenAsync(byte[] hash, Guid machineId, int credentialsVersion, DateTimeOffset expiresAt)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "INSERT INTO machine_refresh_tokens (token_hash, machine_id, credentials_version, expires_at) VALUES (@hash, @machineId, @credentialsVersion, @expiresAt)",
            new { hash, machineId, credentialsVersion, expiresAt });
    }

    /// <summary>Атомарно помечает refresh-токен использованным. Возвращает строку до пометки и признак повтора.</summary>
    public async Task<(RefreshTokenRow? Row, bool AlreadyUsed)> ConsumeRefreshTokenAsync(byte[] hash, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        var consumed = await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            """
            UPDATE machine_refresh_tokens SET used_at = @now
            WHERE token_hash = @hash AND used_at IS NULL
            RETURNING machine_id AS MachineId, credentials_version AS CredentialsVersion, expires_at AS ExpiresAt, NULL::timestamptz AS UsedAt
            """,
            new { hash, now });
        if (consumed is not null)
        {
            return (consumed, false);
        }

        var existing = await c.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            "SELECT machine_id AS MachineId, credentials_version AS CredentialsVersion, expires_at AS ExpiresAt, used_at AS UsedAt FROM machine_refresh_tokens WHERE token_hash = @hash",
            new { hash });
        return (existing, existing is not null);
    }

    /// <summary>Отзыв всех токенов машины (повтор одноразового refresh-токена — признак кражи).</summary>
    public async Task RevokeCredentialsAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE machines SET credentials_version = credentials_version + 1 WHERE id = @id", new { id });
        await c.ExecuteAsync("DELETE FROM machine_refresh_tokens WHERE machine_id = @id", new { id });
    }

    /// <summary>Одобрение новой машины: при следующей попытке регистрации помощник получит токены.</summary>
    public async Task<bool> ApproveAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("UPDATE machines SET approved = true, maintenance = false WHERE id = @id", new { id }) == 1;
    }

    /// <summary>Отклонение: неодобренная машина удаляется из реестра (одобренные так не удаляются).</summary>
    public async Task<bool> RejectPendingAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("DELETE FROM machines WHERE id = @id AND NOT approved", new { id }) == 1;
    }

    public sealed record MachineChanges(int? Number, string? Name, string? ZoneId, bool? Maintenance);

    public enum UpdateResult
    {
        Updated,
        NotFound,
        NumberTaken,
        UnknownZone,
    }

    public async Task<UpdateResult> UpdateAsync(Guid id, MachineChanges changes)
    {
        await using var c = await db.OpenConnectionAsync();
        if (changes.ZoneId is { } zone && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM zones WHERE id = @zone)", new { zone }))
        {
            return UpdateResult.UnknownZone;
        }

        try
        {
            var rows = await c.ExecuteAsync(
                """
                UPDATE machines SET number = COALESCE(@Number, number), name = COALESCE(@Name, name),
                                    zone_id = COALESCE(@ZoneId, zone_id), maintenance = COALESCE(@Maintenance, maintenance)
                WHERE id = @id
                """,
                new { id, changes.Number, changes.Name, changes.ZoneId, changes.Maintenance });
            return rows == 1 ? UpdateResult.Updated : UpdateResult.NotFound;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return UpdateResult.NumberTaken;
        }
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> ZonesAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<(string, string)>("SELECT id, name FROM zones ORDER BY name")).ToList();
    }

    /// <summary>Отчёт помощника: факты о томе на ПК и отметка «машина на связи» (считается для подписки).</summary>
    public async Task RecordStatusAsync(Guid id, string helperVersion, string ipAddress, DateTimeOffset? bootTime, VolumeReport volume, IReadOnlyList<string>? dhcpServers, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE machines SET last_seen_at = @now, helper_version = @helperVersion, ip_address = @ipAddress, boot_time = @bootTime,
                   volume_state = @State, volume_iqn = @TargetIqn, volume_version = @LibraryVersion,
                   volume_ro_verified = @ReadOnlyVerified, volume_error = @Error,
                   dhcp_servers = COALESCE(@dhcpServers, dhcp_servers)
            WHERE id = @id
            """,
            new { id, now, helperVersion, ipAddress, bootTime, volume.State, volume.TargetIqn, volume.LibraryVersion, volume.ReadOnlyVerified, volume.Error, dhcpServers = dhcpServers?.ToArray() });
    }
}
