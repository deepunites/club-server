using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>Каждая миграция откатывается и накатывается заново без ручных шагов.</summary>
public sealed class MigrationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
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
}
