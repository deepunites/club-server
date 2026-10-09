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
    public string? ImageVersion { get; init; }
    public string? SystemDiskJson { get; init; }
    public string? SecureBootJson { get; init; }
    public string? InitiatorIqn { get; init; }
    public string? MasterState { get; init; }
    public string? MasterError { get; init; }
    public int CredentialsVersion { get; init; }

    /// <summary><c>local</c> — Windows на диске ПК (гибрид), <c>diskless</c> — загрузка по сети с личного диска места.</summary>
    public string BootMode { get; init; } = "local";
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
        volume_ro_verified AS VolumeRoVerified, volume_error AS VolumeError, dhcp_servers AS DhcpServers, image_version AS ImageVersion, system_disk::text AS SystemDiskJson, secure_boot::text AS SecureBootJson, initiator_iqn AS InitiatorIqn, master_state AS MasterState, master_error AS MasterError, credentials_version AS CredentialsVersion, boot_mode AS BootMode,
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

    public async Task<MachineRow?> FindByMacAsync(string mac)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<MachineRow>($"SELECT {Columns} FROM machines WHERE @mac = ANY(mac_addresses) ORDER BY approved DESC LIMIT 1", new { mac });
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

    /// <summary>HWID заготовки, добавленной в панели по MAC: настоящий HWID придёт с первой регистрацией помощника.</summary>
    public const string PlaceholderPrefix = "mac:";

    public enum AddResult
    {
        Added,
        NumberTaken,
        MacTaken,
    }

    /// <summary>
    /// Машина по MAC из панели (новый бездисковый ПК без своей Windows: помощнику негде зарегистрироваться, пока
    /// ПК не загрузится по сети). Сразу одобрена; помощник из эталона потом «усыновит» эту запись по MAC.
    /// </summary>
    public async Task<(AddResult Result, MachineRow? Machine)> AddByMacAsync(string mac, int? number, string? name, string bootMode)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("LOCK TABLE machines IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM machines WHERE @mac = ANY(mac_addresses))", new { mac }, tx))
        {
            return (AddResult.MacTaken, null);
        }

        var seat = number ?? await c.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(number), 0) + 1 FROM machines", transaction: tx);
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM machines WHERE number = @seat)", new { seat }, tx))
        {
            return (AddResult.NumberTaken, null);
        }

        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            """
            INSERT INTO machines (id, number, name, zone_id, hwid, hostname, mac_addresses, ip_address, approved, maintenance,
                                  helper_version, os_version, boot_mode)
            VALUES (@id, @seat, @name, 'standard', @hwid, '', @macs, '', true, false, '', NULL, @bootMode)
            """,
            new { id, seat, name = string.IsNullOrWhiteSpace(name) ? $"PC-{seat:D2}" : name.Trim(), hwid = PlaceholderPrefix + mac, macs = new[] { mac }, bootMode },
            tx);
        await tx.CommitAsync();
        return (AddResult.Added, await FindAsync(id));
    }

    /// <summary>Заготовка из панели с одним из этих MAC (ещё без настоящего HWID).</summary>
    public async Task<MachineRow?> FindPlaceholderAsync(IReadOnlyList<string> macs)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<MachineRow>(
            $"SELECT {Columns} FROM machines WHERE hwid LIKE 'mac:%' AND mac_addresses && @macs ORDER BY number LIMIT 1", new { macs = macs.ToArray() });
    }

    /// <summary>
    /// Первая регистрация помощника на ПК, добавленном по MAC: заготовка получает настоящий HWID и факты; номер места,
    /// имя, режим загрузки и одобрение остаются. MAC из панели — первым (по нему резервация Kea).
    /// </summary>
    public async Task<MachineRow> AdoptAsync(Guid id, MachineRegistration registration)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE machines SET hwid = @Hwid, hostname = @Hostname, ip_address = @IpAddress, helper_version = @HelperVersion,
                                os_version = @OsVersion, credentials_version = credentials_version + 1,
                                mac_addresses = ARRAY(SELECT m FROM unnest(mac_addresses || @macs) WITH ORDINALITY AS u(m, o) GROUP BY m ORDER BY min(o))
            WHERE id = @id
            """,
            new { id, registration.Hwid, registration.Hostname, registration.IpAddress, registration.HelperVersion, registration.OsVersion, macs = registration.MacAddresses.ToArray() });
        await c.ExecuteAsync("DELETE FROM machine_refresh_tokens WHERE machine_id = @id", new { id });
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

    /// <summary>
    /// IQN инициатора iSCSI и мастер-том на ПК суперклиента (по отчёту помощника). <paramref name="masterState"/> null —
    /// мастер-том не подключён; <paramref name="keepMaster"/> — прежнее состояние мастер-тома не трогать.
    /// </summary>
    public async Task RecordMasterAsync(Guid id, string? initiatorIqn, string? masterState, string? masterError, bool keepMaster = false)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE machines SET initiator_iqn = COALESCE(@initiatorIqn, initiator_iqn),
                   master_state = CASE WHEN @keepMaster THEN master_state ELSE @masterState END,
                   master_error = CASE WHEN @keepMaster THEN master_error ELSE @masterError END
            WHERE id = @id
            """,
            new { id, initiatorIqn, masterState, masterError, keepMaster });
    }

    /// <summary>Отчёт помощника: факты о томе на ПК и отметка «машина на связи» (считается для подписки).</summary>
    public async Task RecordStatusAsync(Guid id, string helperVersion, string ipAddress, DateTimeOffset? bootTime, VolumeReport volume, IReadOnlyList<string>? dhcpServers, DateTimeOffset now,
        string? imageVersion = null, string? systemDiskJson = null, string? secureBootJson = null)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE machines SET last_seen_at = @now, helper_version = @helperVersion, ip_address = @ipAddress, boot_time = @bootTime,
                   volume_state = @State, volume_iqn = @TargetIqn, volume_version = @LibraryVersion,
                   volume_ro_verified = @ReadOnlyVerified, volume_error = @Error,
                   dhcp_servers = COALESCE(@dhcpServers, dhcp_servers),
                   image_version = COALESCE(@imageVersion, image_version),
                   system_disk = COALESCE(@systemDiskJson::jsonb, system_disk),
                   secure_boot = COALESCE(@secureBootJson::jsonb, secure_boot)
            WHERE id = @id
            """,
            new
            {
                id, now, helperVersion, ipAddress, bootTime, volume.State, volume.TargetIqn, volume.LibraryVersion, volume.ReadOnlyVerified, volume.Error,
                dhcpServers = dhcpServers?.ToArray(), imageVersion, systemDiskJson, secureBootJson,
            });
    }
}
