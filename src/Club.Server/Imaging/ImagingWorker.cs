using Npgsql;

namespace Club.Server.Imaging;

/// <summary>Импорт и удаление образов. Один исполнитель на все экземпляры сервера (advisory lock).</summary>
public sealed class ImagingWorker(IServiceProvider services, NpgsqlDataSource db, ImagingOptions options, TimeProvider clock, ILogger<ImagingWorker> logger) : BackgroundService
{
    private const long WorkerLockKey = 0x436C7562496D67; // "ClubImg"

    public static async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var library = scope.GetRequiredService<ImageLibrary>();
        await library.ProcessImportsAsync(ct);
        var blocked = await library.RetireAsync(ct);
        var warnings = blocked.Select(b => ("imageRetireBlocked", b.Label, $"Образ {b.Label} ещё не удалён: {b.Reason}")).ToList();
        await scope.GetRequiredService<Library.LibraryRepository>().ReplaceWarningsAsync(warnings, scope.GetRequiredService<TimeProvider>().GetUtcNow(), source: "imaging");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var lockConnection = await db.OpenConnectionAsync(stoppingToken);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", lockConnection))
        {
            take.Parameters.AddWithValue(WorkerLockKey);
            await take.ExecuteNonQueryAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(services, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Imaging worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSec), clock, stoppingToken);
        }
    }
}
