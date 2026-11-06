namespace Club.Server.HelperUpdates;

/// <summary>
/// Работник очереди самообновления помощника (spec 2026-11-06, п. 3.3): раз в <see cref="HelperUpdateOptions.WorkerIntervalSec"/>
/// секунд — проход <see cref="HelperUpdateRepository.SweepAsync"/> (молчащие ПК, долгое ожидание, <c>noReturn</c>,
/// <c>noProgress</c>, файлы сборок). Проход идёт под блокировкой очереди, поэтому второй экземпляр сервера ничего не
/// ломает. Ошибка прохода — строка в журнал и следующий проход.
/// </summary>
public sealed class HelperUpdateWorker(HelperUpdateRepository queue, HelperUpdateOptions options, TimeProvider clock, ILogger<HelperUpdateWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.SweepAsync(clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Helper update worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSec), clock, stoppingToken);
        }
    }
}
