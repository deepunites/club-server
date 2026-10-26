using FluentMigrator.Runner;
using Npgsql;

namespace Club.Server.Data;

public static class Database
{
    /// <summary>Ключ advisory-блокировки миграций: два процесса сервера не накатывают схему одновременно.</summary>
    private const long MigrationLockKey = 0x436C75624D6967; // "ClubMig"

    public static IServiceCollection AddClubDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(Database).Assembly).For.Migrations());
        return services;
    }

    /// <summary>
    /// Источник соединений библиотеки <paramref name="key"/> (keyed singleton): search_path начинается с её схемы, поэтому
    /// тот же SQL репозиториев библиотеки идёт в её таблицы (<c>solo.library_versions</c>, …), а общие —
    /// <c>machines</c>, <c>storage_warnings</c> — находятся там же, где у G:. Проверка при старте —
    /// <see cref="Library.LibrarySet.CheckSchemasAsync"/>.
    /// </summary>
    public static IServiceCollection AddLibraryDataSource(this IServiceCollection services, string connectionString, string key)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // Путь G: — заданный в строке подключения или умолчание PostgreSQL; перед ним — схема библиотеки.
        builder.SearchPath = $"{key}, {(string.IsNullOrWhiteSpace(builder.SearchPath) ? "\"$user\", public" : builder.SearchPath)}";
        var libraryConnectionString = builder.ConnectionString;
        services.AddKeyedSingleton(key, (_, _) => new NpgsqlDataSourceBuilder(libraryConnectionString).Build());
        return services;
    }

    /// <summary>Накатывает миграции до последней под межпроцессной блокировкой.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            take.Parameters.AddWithValue(MigrationLockKey);
            await take.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            release.Parameters.AddWithValue(MigrationLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
