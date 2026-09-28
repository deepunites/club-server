using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Club.TestSupport;

/// <summary>Сервер в памяти поверх отдельной временной базы в локальном PostgreSQL.</summary>
public class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ClubKey = "test-club-key";
    private const string AdminConnection = "Host=/var/run/postgresql;Database=postgres";
    private readonly string _database = "club_test_" + Guid.NewGuid().ToString("N");
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"club-test-{Guid.NewGuid():N}.pem");

    /// <summary>Дополнительные настройки (например, модуль библиотеки поверх поддельного TrueNAS).</summary>
    public Dictionary<string, string> Settings { get; } = new();

    public virtual async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {_database}", connection);
        await command.ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await TestDatabases.DropAsync(_database);
        File.Delete(_keyPath);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Club", $"Host=/var/run/postgresql;Database={_database}");
        builder.UseSetting("Auth:ClubApiKey", ClubKey);
        builder.UseSetting("Auth:AutoApprovePcs", "true");
        builder.UseSetting("Auth:SigningKeyPath", _keyPath);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
