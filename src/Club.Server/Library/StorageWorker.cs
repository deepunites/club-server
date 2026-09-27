using Npgsql;

namespace Club.Server.Library;

/// <summary>
/// Единственный исполнитель операций с хранилищем: операции TrueNAS выполняются строго последовательно
/// (каждый CRUD iSCSI перезагружает всю конфигурацию SCST). Второй экземпляр сервера ждёт advisory-блокировку.
/// </summary>
public sealed class StorageWorker(
    IServiceProvider services,
    NpgsqlDataSource db,
    LibraryOptions options,
    TimeProvider clock,
    ILogger<StorageWorker> logger) : BackgroundService
{
    private const long WorkerLockKey = 0x436C75625374; // "ClubSt"

    /// <summary>Один проход: операции, разборка, сверка (если подошло время).</summary>
    public static async Task RunOnceAsync(LibraryPublisher publisher, LibraryRepository repository, bool reconcile, TimeProvider clock, CancellationToken ct)
    {
        await publisher.ProcessOperationsAsync(ct);
        var blocked = await publisher.RetireAsync(ct);
        if (reconcile)
        {
            var warnings = (await publisher.ReconcileAsync(ct)).ToList();
            warnings.AddRange(blocked.Select(b => ("retireBlocked", b.Label, $"Версия {b.Label} ещё не разобрана: {b.Reason}")));
            await repository.ReplaceWarningsAsync(warnings, clock.GetUtcNow());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var lockConnection = await db.OpenConnectionAsync(stoppingToken);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", lockConnection))
        {
            take.Parameters.AddWithValue(WorkerLockKey);
            await take.ExecuteNonQueryAsync(stoppingToken);
        }

        var nextReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var reconcile = clock.GetUtcNow() >= nextReconcile;
                await RunOnceAsync(
                    scope.ServiceProvider.GetRequiredService<LibraryPublisher>(),
                    scope.ServiceProvider.GetRequiredService<LibraryRepository>(),
                    reconcile, clock, stoppingToken);
                if (reconcile)
                {
                    nextReconcile = clock.GetUtcNow().AddSeconds(options.ReconcileIntervalSec);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // TrueNAS недоступен или сбой БД: пробуем на следующем такте, клуб продолжает работать.
                logger.LogWarning(ex, "Storage worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSec), clock, stoppingToken);
        }
    }
}
