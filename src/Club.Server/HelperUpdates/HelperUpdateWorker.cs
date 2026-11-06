namespace Club.Server.HelperUpdates;

/// <summary>
/// Работник очереди самообновления помощника (spec 2026-11-06, п. 3.3): раз в <see cref="HelperUpdateOptions.WorkerIntervalSec"/>
/// секунд — проход <see cref="HelperUpdateRepository.SweepAsync"/> (молчащие ПК, долгое ожидание, <c>noReturn</c>,
/// <c>noProgress</c>, файлы сборок). Проход идёт под блокировкой очереди, поэтому второй экземпляр сервера ничего не
/// ломает. Ошибка прохода — строка в журнал и следующий проход. Время до запуска работника не считается: каждый проход
/// получает начало работы как <c>upSince</c> — после простоя сервера ПК сначала дают отчитаться.
/// </summary>
public sealed class HelperUpdateWorker(HelperUpdateRepository queue, HelperUpdateOptions options, TimeProvider clock, ILogger<HelperUpdateWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upSince = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.SweepAsync(clock.GetUtcNow(), upSince);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Helper update worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSec), clock, stoppingToken);
        }
    }
}
