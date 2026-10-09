using System.Text.Json;
using System.Text.Json.Nodes;
using Club.Server.Data;
using Npgsql;

namespace Club.Server.Network;

public sealed class KeaOptions
{
    /// <summary>Резервации в Kea включены: сервер пишет в её базу. Базу создаёт <c>kea-admin db-init pgsql</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Отдельная база Kea в том же PostgreSQL (таблицы Kea не смешиваются с нашими).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Мажорная версия схемы Kea, с которой проверена запись (Kea 3.0.x — 29).</summary>
    public int SchemaMajor { get; set; } = 29;
}

public sealed record KeaConflict(string Kind, string Subject, string Message);

public sealed record KeaSyncResult(bool SchemaOk, string? SchemaVersion, int Desired, int Inserted, int Updated, int Deleted, IReadOnlyList<KeaConflict> Conflicts);

/// <summary>
/// Резервации Kea из реестра машин: одобренная машина → MAC основной сетевой карты, IP места, имя хоста.
/// Пишем прямо в таблицу <c>hosts</c> базы Kea (hosts backend pgsql; Kea читает её на каждый запрос).
/// Наши строки помечены <c>user_context.clubsrv</c>; строки без метки (ручные) не трогаются никогда —
/// конфликт с ними (тот же MAC или IP в подсети) становится предупреждением, а машина остаётся без резервации.
/// </summary>
public sealed class KeaHostSync(KeaOptions options, MachineRepository machines, NetworkRepository network, Imaging.ImageRepository images, Diskless.DisklessRepository diskless, ILogger<KeaHostSync> logger)
{
    private const short HwAddress = 0;

    /// <summary>
    /// Класс клиента в резервации = PXE-флаг: только с ним Kea отдаёт загрузчик (классы <c>club-reimage-*</c> в
    /// <see cref="KeaConfig"/>). Без него ПК получает адрес без имени загрузочного файла и сразу грузится с диска.
    /// </summary>
    public const string ReimageClass = "club-reimage";
    private const long LockKey = 0x436C75624B6561; // "ClubKea"

    public sealed record DesiredHost(Guid MachineId, int Seat, byte[] Mac, uint Ip, string Hostname, bool Pxe = false);

    /// <summary>Что должно быть в Kea по реестру и адресному плану; конфликты внутри реестра — сразу предупреждения.</summary>
    public static (IReadOnlyList<DesiredHost> Hosts, IReadOnlyList<KeaConflict> Conflicts) Plan(IReadOnlyList<MachineRow> registry, IpPlan plan, IReadOnlySet<Guid>? pxeArmed = null)
    {
        var hosts = new List<DesiredHost>();
        var conflicts = new List<KeaConflict>();
        foreach (var m in registry.Where(m => m.Approved).OrderBy(m => m.Number))
        {
            if (m.MacAddresses.Length == 0 || ParseMac(m.MacAddresses[0]) is not { } mac)
            {
                conflicts.Add(new("noMac", m.Name, $"У машины {m.Name} нет MAC — резервация не создана"));
                continue;
            }

            if (plan.SeatAddress(m.Number) is not { } ip)
            {
                conflicts.Add(new("seatOutOfRange", m.Name, $"Место №{m.Number} ({m.Name}) не помещается в диапазон резерваций или задевает пул"));
                continue;
            }

            if (hosts.FirstOrDefault(h => h.Mac.AsSpan().SequenceEqual(mac)) is { } twin)
            {
                conflicts.Add(new("duplicateMac", m.MacAddresses[0], $"MAC {m.MacAddresses[0]} у двух машин: №{twin.Seat} и №{m.Number}"));
                continue;
            }

            hosts.Add(new DesiredHost(m.Id, m.Number, mac, ip, Hostname(m.Name, m.Number), pxeArmed?.Contains(m.Id) ?? false));
        }

        return (hosts, conflicts);
    }

    /// <summary>Синхронизация; <c>null</c> — пропущена (запись в Kea выключена или сеть ещё не настроена).</summary>
    public async Task<KeaSyncResult?> SyncAsync(CancellationToken ct)
    {
        var settings = await network.GetAsync();
        if (!options.Enabled || !settings.Configured)
        {
            return null;
        }

        var plan = new IpPlan(settings);
        // PXE-флаг: перезаливка (снимается после заливки) и бездиск/режим мастера (держится всегда).
        var networkBoot = (await images.ArmedMachinesAsync()).Union(await diskless.NetworkBootMachinesAsync()).ToHashSet();
        var (desired, conflicts) = Plan(await machines.AllAsync(), plan, networkBoot);
        var conflictList = conflicts.ToList();

        await using var db = new NpgsqlConnection(options.ConnectionString);
        await db.OpenAsync(ct);
        var version = await SchemaVersionAsync(db, ct);
        if (version is null || !version.StartsWith(options.SchemaMajor + ".", StringComparison.Ordinal))
        {
            // Незнакомая схема Kea (после обновления Kea) — не пишем ничего: структура hosts могла измениться.
            conflictList.Add(new("keaSchema", "kea", $"Схема базы Kea {version ?? "не найдена"}, ожидается {options.SchemaMajor}.x — резервации не обновляются"));
            return new KeaSyncResult(false, version, desired.Count, 0, 0, 0, conflictList);
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", db, tx))
        {
            lockCmd.Parameters.AddWithValue(LockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        var existing = await ReadHostsAsync(db, tx, settings.KeaSubnetId, ct);
        var ours = existing.Where(h => h.MachineId is not null).ToDictionary(h => h.MachineId!.Value);
        var foreign = existing.Where(h => h.MachineId is null && h.SubnetId == settings.KeaSubnetId).ToList();

        // Сначала убираем изменённые и лишние наши строки, потом вставляем: обмен MAC или IP между местами не упирается
        // в уникальный индекс (MAC, подсеть) посреди транзакции.
        int inserted = 0, updated = 0, deleted = 0;
        var toInsert = new List<DesiredHost>();
        var keep = new HashSet<Guid>();
        foreach (var host in desired)
        {
            var clash = foreign.FirstOrDefault(f => f.Mac.AsSpan().SequenceEqual(host.Mac) || f.Ip == host.Ip);
            if (clash is not null)
            {
                conflictList.Add(new("manualReservation", IpPlan.ToIp(host.Ip),
                    $"Ручная резервация Kea (host_id {clash.HostId}) занимает MAC или IP места №{host.Seat} — наша резервация не создана"));
                continue;
            }

            if (ours.TryGetValue(host.MachineId, out var current))
            {
                if (current.Mac.AsSpan().SequenceEqual(host.Mac) && current.Ip == host.Ip && current.Hostname == host.Hostname && current.SubnetId == settings.KeaSubnetId
                    && current.Classes == (host.Pxe ? ReimageClass : null))
                {
                    keep.Add(host.MachineId);
                    continue;
                }

                updated++;
            }
            else
            {
                inserted++;
            }

            toInsert.Add(host);
        }

        foreach (var (machineId, row) in ours)
        {
            if (!keep.Contains(machineId))
            {
                await ExecAsync(db, tx, ct, "DELETE FROM hosts WHERE host_id = $1", row.HostId);
                if (!toInsert.Any(h => h.MachineId == machineId))
                {
                    deleted++;
                }
            }
        }

        foreach (var host in toInsert)
        {
            await ExecAsync(db, tx, ct,
                "INSERT INTO hosts (dhcp_identifier, dhcp_identifier_type, dhcp4_subnet_id, ipv4_address, hostname, user_context, dhcp4_client_classes) VALUES ($1, $2, $3, $4, $5, $6, $7)",
                host.Mac, HwAddress, (long)settings.KeaSubnetId, (long)host.Ip, host.Hostname, Context(host), host.Pxe ? ReimageClass : (object)DBNull.Value);
        }

        await tx.CommitAsync(ct);
        if (inserted + updated + deleted > 0)
        {
            logger.LogInformation("Kea reservations synced: +{Inserted} ~{Updated} -{Deleted}", inserted, updated, deleted);
        }

        return new KeaSyncResult(true, version, desired.Count, inserted, updated, deleted, conflictList);
    }

    private sealed record KeaHost(int HostId, byte[] Mac, long? SubnetId, uint? Ip, string? Hostname, Guid? MachineId, string? Classes);

    /// <summary>Строки Kea в нашей подсети (hw-address) плюс наши строки из других подсетей (после смены subnet id).</summary>
    private static async Task<List<KeaHost>> ReadHostsAsync(NpgsqlConnection db, NpgsqlTransaction tx, int subnetId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT host_id, dhcp_identifier, dhcp4_subnet_id, ipv4_address, hostname, user_context, dhcp4_client_classes FROM hosts
            WHERE dhcp_identifier_type = 0 AND (dhcp4_subnet_id = $1 OR user_context LIKE '%"clubsrv"%')
            FOR UPDATE
            """, db, tx);
        cmd.Parameters.AddWithValue((long)subnetId);
        var rows = new List<KeaHost>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new KeaHost(
                reader.GetInt32(0),
                (byte[])reader[1],
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : (uint)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : OurMachineId(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<string?> SchemaVersionAsync(NpgsqlConnection db, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT version || '.' || minor FROM schema_version LIMIT 1", db);
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    private static async Task ExecAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        foreach (var arg in args)
        {
            cmd.Parameters.AddWithValue(arg);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Context(DesiredHost host) =>
        new JsonObject { ["clubsrv"] = new JsonObject { ["machineId"] = host.MachineId.ToString(), ["seat"] = host.Seat } }.ToJsonString();

    private static Guid? OurMachineId(string userContext)
    {
        try
        {
            return JsonNode.Parse(userContext)?["clubsrv"]?["machineId"]?.GetValue<string>() is { } id && Guid.TryParse(id, out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[]? ParseMac(string mac)
    {
        var parts = mac.Split(':', '-');
        if (parts.Length != 6)
        {
            return null;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>Имя хоста для DHCP/DNS: латиница, цифры, дефис; из «PC-01» — «pc-01».</summary>
    public static string Hostname(string name, int seat)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var host = new string(chars).Trim('-');
        while (host.Contains("--", StringComparison.Ordinal))
        {
            host = host.Replace("--", "-", StringComparison.Ordinal);
        }

        return host.Length is 0 or > 63 ? $"pc-{seat:D2}" : host;
    }
}
