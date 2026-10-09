using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Diskless;

public sealed class DisklessImageRow
{
    public string? CurrentVersion { get; init; }
    public string? RollbackVersion { get; init; }
    public Guid? MasterMachineId { get; init; }
    public bool MasterInstall { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DisklessVersionRow
{
    public string Version { get; init; } = "";
    public string Snapshot { get; init; } = "";
    public string? Comment { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Личный диск машины: <c>seat</c> — клон версии эталона с откатом, <c>master</c> — сам zvol эталона.</summary>
public sealed class SeatDiskRow
{
    public Guid MachineId { get; init; }
    public string Kind { get; init; } = "seat";
    public string Zvol { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string InitiatorIqn { get; init; } = "";
    public string? BaseSnapshot { get; init; }
    public string ChapUser { get; init; } = "";
    public string ChapSecret { get; init; } = "";
    public int? AuthTag { get; init; }
    public string? TargetIqn { get; init; }
    public string State { get; init; } = "new";
    public string? LastError { get; init; }
    public int Boots { get; init; }
    public DateTimeOffset? LastBootAt { get; init; }
}

public sealed class DisklessRepository(NpgsqlDataSource db)
{
    private const string SeatColumns = """
        machine_id AS MachineId, kind, zvol, target_name AS TargetName, initiator_iqn AS InitiatorIqn,
        base_snapshot AS BaseSnapshot, chap_user AS ChapUser, chap_secret AS ChapSecret, auth_tag AS AuthTag,
        target_iqn AS TargetIqn, state, last_error AS LastError, boots, last_boot_at AS LastBootAt
        """;

    static DisklessRepository() => DapperSetup.Ensure();

    public async Task<DisklessImageRow> GetImageAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleAsync<DisklessImageRow>("""
            SELECT current_version AS CurrentVersion, rollback_version AS RollbackVersion, master_machine_id AS MasterMachineId,
                   master_install AS MasterInstall, updated_at AS UpdatedAt
            FROM diskless_image
            """);
    }

    public async Task<IReadOnlyList<DisklessVersionRow>> VersionsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<DisklessVersionRow>(
            "SELECT version, snapshot, comment, created_by AS CreatedBy, created_at AS CreatedAt FROM diskless_versions ORDER BY created_at DESC")).ToList();
    }

    public async Task<DisklessVersionRow?> FindVersionAsync(string version)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<DisklessVersionRow>(
            "SELECT version, snapshot, comment, created_by AS CreatedBy, created_at AS CreatedAt FROM diskless_versions WHERE version = @version",
            new { version });
    }

    /// <summary>Новая версия становится текущей, прежняя текущая — откатной.</summary>
    public async Task AddCurrentVersionAsync(string version, string snapshot, string? comment, string? createdBy, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync(
            "INSERT INTO diskless_versions (version, snapshot, comment, created_by, created_at) VALUES (@version, @snapshot, @comment, @createdBy, @now)",
            new { version, snapshot, comment, createdBy, now }, tx);
        await c.ExecuteAsync(
            "UPDATE diskless_image SET rollback_version = current_version, current_version = @version, updated_at = @now",
            new { version, now }, tx);
        await tx.CommitAsync();
    }

    /// <summary>Текущая и откатная меняются местами. false — откатной версии нет.</summary>
    public async Task<bool> SwapRollbackAsync(DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("""
            UPDATE diskless_image SET current_version = rollback_version, rollback_version = current_version, updated_at = @now
            WHERE rollback_version IS NOT NULL
            """, new { now }) == 1;
    }

    public async Task SetMasterAsync(Guid? machineId, bool install, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE diskless_image SET master_machine_id = @machineId, master_install = @install, updated_at = @now",
            new { machineId, install = machineId is not null && install, now });
    }

    public async Task<bool> SetBootModeAsync(Guid machineId, string mode)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("UPDATE machines SET boot_mode = @mode WHERE id = @machineId", new { machineId, mode }) == 1;
    }

    /// <summary>Машины, которым Kea отдаёт загрузчик всегда (PXE-флаг в резервации): бездиск и режим мастера.</summary>
    public async Task<IReadOnlySet<Guid>> NetworkBootMachinesAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<Guid>("""
            SELECT id FROM machines WHERE boot_mode = 'diskless'
            UNION SELECT master_machine_id FROM diskless_image WHERE master_machine_id IS NOT NULL
            """)).ToHashSet();
    }

    public async Task<SeatDiskRow?> FindSeatAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<SeatDiskRow>($"SELECT {SeatColumns} FROM seat_disks WHERE machine_id = @machineId", new { machineId });
    }

    public async Task<IReadOnlyList<SeatDiskRow>> SeatsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<SeatDiskRow>($"SELECT {SeatColumns} FROM seat_disks ORDER BY target_name")).ToList();
    }

    /// <summary>Запись о диске до создания объектов в TrueNAS: имена и CHAP фиксируются, состояние <c>new</c>.</summary>
    public async Task UpsertSeatAsync(SeatDiskRow seat, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO seat_disks (machine_id, kind, zvol, target_name, initiator_iqn, base_snapshot, chap_user, chap_secret, auth_tag, state, updated_at)
            VALUES (@MachineId, @Kind, @Zvol, @TargetName, @InitiatorIqn, @BaseSnapshot, @ChapUser, @ChapSecret, @AuthTag, 'new', @now)
            ON CONFLICT (machine_id) DO UPDATE SET kind = EXCLUDED.kind, zvol = EXCLUDED.zvol, target_name = EXCLUDED.target_name,
                initiator_iqn = EXCLUDED.initiator_iqn, base_snapshot = EXCLUDED.base_snapshot, chap_user = EXCLUDED.chap_user,
                chap_secret = EXCLUDED.chap_secret, auth_tag = EXCLUDED.auth_tag, state = 'new', last_error = NULL, updated_at = @now
            """, new { seat.MachineId, seat.Kind, seat.Zvol, seat.TargetName, seat.InitiatorIqn, seat.BaseSnapshot, seat.ChapUser, seat.ChapSecret, seat.AuthTag, now });
    }

    public async Task MarkSeatReadyAsync(Guid machineId, int authTag, string targetIqn, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE seat_disks SET state = 'ready', auth_tag = @authTag, target_iqn = @targetIqn, last_error = NULL, updated_at = @now WHERE machine_id = @machineId",
            new { machineId, authTag, targetIqn, now });
    }

    public async Task MarkSeatFailedAsync(Guid machineId, string error, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE seat_disks SET state = 'failed', last_error = @error, updated_at = @now WHERE machine_id = @machineId",
            new { machineId, error, now });
    }

    public async Task RecordBootAsync(Guid machineId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE seat_disks SET boots = boots + 1, last_boot_at = @now, updated_at = @now WHERE machine_id = @machineId",
            new { machineId, now });
    }

    public async Task DeleteSeatAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM seat_disks WHERE machine_id = @machineId", new { machineId });
    }
}
