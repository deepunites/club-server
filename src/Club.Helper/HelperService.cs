using Club.Helper.Core;

namespace Club.Helper;

/// <summary>Такт помощника сразу после старта службы и дальше каждые <see cref="HelperOptions.PollIntervalSec"/> секунд.</summary>
public sealed class HelperService(HelperLoop loop, HelperOptions options, ILogger<HelperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.PollIntervalSec));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await loop.TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Любой сбой такта — в журнал; том остаётся как есть, следующий такт попробует снова.
                logger.LogError(ex, "Helper tick failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
