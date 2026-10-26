using Npgsql;

namespace Club.Server.Library;

/// <summary>
/// Единственный исполнитель операций с хранилищем: операции TrueNAS выполняются строго последовательно
/// (каждый CRUD iSCSI перезагружает всю конфигурацию SCST). Второй экземпляр сервера ждёт advisory-блокировку.
/// Библиотеки — по очереди в одном проходе: сначала G:, затем каждая следующая со своей обработкой сбоя (её ошибка G: не
/// останавливает).
/// </summary>
public sealed class StorageWorker(
    IServiceProvider services,
    NpgsqlDataSource db,
    LibraryOptions options,
    TimeProvider clock,
    ILogger<StorageWorker> logger) : BackgroundService
{
    private const long WorkerLockKey = 0x436C75625374; // "ClubSt"

    /// <summary>
    /// Один проход: операции, разборка, сверка (если подошло время). <paramref name="source"/> и
    /// <paramref name="subjectPrefix"/> — источник и приставка предупреждений библиотеки (<see cref="LibraryNames"/>):
    /// сверка закрывает только предупреждения своей библиотеки, а ключи (kind, subject) двух библиотек не совпадают.
    /// </summary>
    public static async Task RunOnceAsync(LibraryPublisher publisher, LibraryRepository repository, bool reconcile, TimeProvider clock, CancellationToken ct,
        string source = "library", string subjectPrefix = "")
    {
        await publisher.ProcessOperationsAsync(ct);
        var blocked = await publisher.RetireAsync(ct);
        if (reconcile)
        {
            var warnings = (await publisher.ReconcileAsync(ct)).ToList();
            warnings.AddRange(blocked.Select(b => ("retireBlocked", b.Label, $"Версия {b.Label} ещё не разобрана: {b.Reason}")));
            await repository.ReplaceWarningsAsync(warnings.Select(w => (w.Kind, subjectPrefix + w.Subject, w.Message)).ToList(), clock.GetUtcNow(), source);
        }
    }

    /// <summary>Проход одной библиотеки со своими источником и приставкой предупреждений.</summary>
    public static Task RunOnceAsync(LibrarySlot library, bool reconcile, TimeProvider clock, CancellationToken ct) =>
        RunOnceAsync(library.Publisher, library.Repository, reconcile, clock, ct, library.Names.WarningSource, library.Names.WarningSubjectPrefix);

    /// <summary>Проходы работающих библиотек, кроме G:, — каждая со своей обработкой сбоя.</summary>
    public static async Task RunExtrasAsync(LibrarySet libraries, bool reconcile, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        foreach (var library in libraries.Extras.Where(l => l.Options.Enabled))
        {
            try
            {
                await RunOnceAsync(library, reconcile, clock, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Storage worker pass of library {Library} failed", library.Key);
            }
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

        var libraries = services.GetRequiredService<LibrarySet>();
        var nextReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var reconcile = clock.GetUtcNow() >= nextReconcile;
            try
            {
                using var scope = services.CreateScope();
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

            await RunExtrasAsync(libraries, reconcile, clock, logger, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSec), clock, stoppingToken);
        }
    }
}
