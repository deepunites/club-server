using Npgsql;

namespace Club.TestSupport;

/// <summary>Временная база Kea со схемой из пакета Kea 3.0.3 (как после <c>kea-admin db-init pgsql</c>).</summary>
public sealed class KeaDatabase : IAsyncDisposable
{
    private const string AdminConnection = "Host=/var/run/postgresql;Database=postgres";
    private readonly string _name = "kea_test_" + Guid.NewGuid().ToString("N");

    public string ConnectionString => $"Host=/var/run/postgresql;Database={_name}";

    public static async Task<KeaDatabase> CreateAsync()
    {
        var db = new KeaDatabase();
        await using (var admin = new NpgsqlConnection(AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {db._name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using var c = new NpgsqlConnection(db.ConnectionString);
        await c.OpenAsync();
        await using var schema = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Kea", "dhcpdb_create-29.0.pgsql")), c);
        await schema.ExecuteNonQueryAsync();
        return db;
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Строки <c>hosts</c>: MAC (hex), подсеть, IP, имя хоста, user_context.</summary>
    public async Task<List<(string Mac, long Subnet, string Ip, string? Hostname, string? Context)>> HostsAsync()
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT encode(dhcp_identifier, 'hex'), dhcp4_subnet_id, (('0.0.0.0'::inet) + ipv4_address)::text, hostname, user_context FROM hosts ORDER BY ipv4_address", c);
        var rows = new List<(string, long, string, string?, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2).Split('/')[0], reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
