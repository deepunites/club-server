using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Library;

/// <summary>Последний отчёт помощника о второй (третьей, …) библиотеке на ПК: том и мастер-том на её букве.</summary>
public sealed class LibraryMachineRow
{
    public Guid MachineId { get; init; }
    public string VolumeState { get; init; } = "none";
    public string? VolumeIqn { get; init; }
    public string? VolumeVersion { get; init; }
    public string? VolumeError { get; init; }
    public string? MasterState { get; init; }
    public string? MasterError { get; init; }
    public DateTimeOffset ReportedAt { get; init; }
}

/// <summary>
/// Состояние ПК по библиотеке не-G: (<c>machine_library</c> её схемы). Столбцы G: в <c>machines</c> (volume_*, master_*,
/// last_seen_at) этот репозиторий не трогает: их пишет только отчёт помощника по G:.
/// </summary>
public sealed class LibraryMachineRepository(NpgsqlDataSource db)
{
    /// <summary>Состояния мастер-тома, которые сервер сохраняет как есть (как у G:, DisklessEndpoints).</summary>
    public static readonly IReadOnlySet<string> MasterStates = new HashSet<string>(["none", "mounting", "mounted", "failed"]);

    /// <summary>master.state «неизвестно»: помощник не смог прочитать, подключён ли мастер-том, — прежнее состояние остаётся.</summary>
    public const string MasterUnknownState = "unknown";

    private const string Columns = """
        machine_id AS MachineId, volume_state AS VolumeState, volume_iqn AS VolumeIqn, volume_version AS VolumeVersion, volume_error AS VolumeError,
        master_state AS MasterState, master_error AS MasterError, reported_at AS ReportedAt
        """;

    static LibraryMachineRepository() => DapperSetup.Ensure();

    /// <summary>
    /// Отчёт о библиотеке. Мастер-том — по правилам G:: отчёта о нём нет (<paramref name="masterState"/> null) — не
    /// подключён, прежнее состояние сбрасывается (иначе потерянный «none» навсегда оставил бы «mounted» в панели);
    /// <c>unknown</c> и незнакомое состояние (более новый помощник) — прежнее не меняется.
    /// </summary>
    public async Task RecordAsync(Guid machineId, VolumeReport volume, string? masterState, string? masterError, DateTimeOffset now)
    {
        var keepMaster = masterState is not null && !MasterStates.Contains(masterState);
        var master = keepMaster ? null : masterState;
        var masterText = keepMaster || masterState is null ? null : Truncate(masterError);
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO machine_library (machine_id, volume_state, volume_iqn, volume_version, volume_error, master_state, master_error, reported_at)
            VALUES (@machineId, @volumeState, @volumeIqn, @volumeVersion, @volumeError, @master, @masterText, @now)
            ON CONFLICT (machine_id) DO UPDATE SET volume_state = EXCLUDED.volume_state, volume_iqn = EXCLUDED.volume_iqn,
                volume_version = EXCLUDED.volume_version, volume_error = EXCLUDED.volume_error,
                master_state = CASE WHEN @keepMaster THEN machine_library.master_state ELSE EXCLUDED.master_state END,
                master_error = CASE WHEN @keepMaster THEN machine_library.master_error ELSE EXCLUDED.master_error END,
                reported_at = EXCLUDED.reported_at
            """,
            new
            {
                machineId, volumeState = volume.State, volumeIqn = volume.TargetIqn, volumeVersion = volume.LibraryVersion, volumeError = Truncate(volume.Error),
                master, masterText, keepMaster, now,
            });
    }

    public async Task<LibraryMachineRow?> FindAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<LibraryMachineRow>($"SELECT {Columns} FROM machine_library WHERE machine_id = @machineId", new { machineId });
    }

    public async Task<IReadOnlyList<LibraryMachineRow>> AllAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<LibraryMachineRow>($"SELECT {Columns} FROM machine_library")).ToList();
    }

    private static string? Truncate(string? value) => value is null || value.Length <= 2000 ? value : value[..2000];
}
