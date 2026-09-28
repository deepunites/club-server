using Npgsql;

namespace Club.TestSupport;

public static class TestDatabases
{
    private const string AdminConnection = "Host=/var/run/postgresql;Database=postgres";

    /// <summary>
    /// Удаление временной базы. FORCE не может завершить служебный процесс autovacuum (у обычной роли нет прав,
    /// ошибка 42501) — он скоро заканчивается сам, поэтому повторяем.
    /// </summary>
    public static async Task DropAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnection);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException ex) when (attempt < 20 && ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
            {
                await Task.Delay(250);
            }
        }
    }
}
