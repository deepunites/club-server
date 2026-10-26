using Club.TestSupport;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>Каждая миграция откатывается и накатывается заново без ручных шагов.</summary>
public sealed class MigrationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    /// <summary>Таблицы библиотеки, копии которых живут в схеме каждой следующей библиотеки (M0011_SoloLibrary).</summary>
    private static readonly string[] LibraryTables = ["library_versions", "library_state", "storage_operations", "library_master", "seat_games"];

    [Fact]
    public void All_migrations_roll_back_to_empty_and_forward_again()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateDown(0);
        runner.MigrateUp();
        runner.MigrateDown(0);
        runner.MigrateUp();
    }

    [Fact]
    public async Task Solo_schema_appears_with_seeded_rows_and_goes_away_on_rollback()
    {
        using var scope = server.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        string[] tables = ["library_master", "library_state", "library_versions", "machine_library", "seat_games", "storage_operations"];
        runner.MigrateUp();
        await using (var c = await db.OpenConnectionAsync())
        {
            Assert.Equal(tables, (await TablesAsync(c, "solo")).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM solo.library_state WHERE current_version_id IS NULL AND rollback_version_id IS NULL"));
            Assert.Equal("closed", await c.ExecuteScalarAsync<string>("SELECT state FROM solo.library_master"));
            await c.ExecuteAsync("INSERT INTO storage_warnings (kind, subject, message, first_seen, last_seen, source) VALUES ('retiredHeld', 'solo:x', 'm', now(), now(), 'solo')");
        }

        runner.MigrateDown(2026102301);
        await using (var c = await db.OpenConnectionAsync())
        {
            Assert.False(await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'solo')"));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM storage_warnings WHERE source = 'solo'"));
        }

        runner.MigrateUp();
        await using (var c = await db.OpenConnectionAsync())
        {
            Assert.Equal(tables, (await TablesAsync(c, "solo")).Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    /// Таблицы solo — точные копии таблиц библиотеки G:: те же столбцы, умолчания, проверки, ключи и индексы. Кто меняет
    /// таблицу библиотеки в public, должен изменить её и в каждой схеме библиотеки — иначе этот тест упадёт.
    /// </summary>
    [Fact]
    public async Task Solo_tables_are_exact_copies_of_the_library_tables()
    {
        using (var scope = server.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }

        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var solo = await ShapeAsync(c, "solo");
        Assert.Equal(await ShapeAsync(c, "public"), solo);
        Assert.Contains(solo, line => line.StartsWith("storage_operations storage_operations_kind_check c CHECK", StringComparison.Ordinal) && line.Contains("masterClose"));
    }

    private static async Task<List<string>> TablesAsync(NpgsqlConnection c, string schema) =>
        (await c.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema", new { schema })).ToList();

    /// <summary>Столбцы, ограничения и индексы таблиц библиотеки в схеме — строками, без имени схемы.</summary>
    private static async Task<List<string>> ShapeAsync(NpgsqlConnection c, string schema)
    {
        var tables = LibraryTables;
        var columns = await c.QueryAsync<string>(
            """
            SELECT table_name || ' ' || ordinal_position || ' ' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '-')
            FROM information_schema.columns WHERE table_schema = @schema AND table_name = ANY(@tables) ORDER BY table_name, ordinal_position
            """, new { schema, tables });
        var constraints = await c.QueryAsync<string>(
            """
            SELECT cl.relname || ' ' || co.conname || ' ' || co.contype::text || ' ' || pg_get_constraintdef(co.oid)
            FROM pg_constraint co JOIN pg_class cl ON cl.oid = co.conrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
            WHERE n.nspname = @schema AND cl.relname = ANY(@tables) ORDER BY cl.relname, co.conname
            """, new { schema, tables });
        var indexes = await c.QueryAsync<string>(
            "SELECT tablename || ' ' || indexdef FROM pg_indexes WHERE schemaname = @schema AND tablename = ANY(@tables) ORDER BY tablename, indexname",
            new { schema, tables });
        // Таблицы своей схемы печатаются с её именем (search_path проверки — public), public — без.
        return columns.Concat(constraints).Concat(indexes).Select(line => line.Replace($" {schema}.", " ").Replace($"REFERENCES {schema}.", "REFERENCES ")).ToList();
    }
}
