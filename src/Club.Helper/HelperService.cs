using Club.Helper.Core;

namespace Club.Helper;

/// <summary>Такт помощника сразу после старта службы и дальше каждые <see cref="HelperOptions.PollIntervalSec"/> секунд.</summary>
public sealed class HelperService(HelperLoop loop, HelperOptions options, ILogger<HelperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DisableFastStartup();
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

    /// <summary>
    /// Быстрый запуск (по умолчанию в Windows 11): «Завершение работы» усыпляет сеанс 0 вместо выключения, служба не
    /// перезапускается при включении — и личный диск игр не сбрасывается между игроками. Выключаем (как powercfg /h off
    /// для этой настройки); действует со следующего выключения.
    /// </summary>
    private void DisableFastStartup()
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        try
        {
            using var power = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key, writable: true);
            if (power is not null && power.GetValue("HiberbootEnabled") is not 0)
            {
                power.SetValue("HiberbootEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
                logger.LogInformation("Fast Startup turned off (HiberbootEnabled=0): the personal games disk is reset on every power-on");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogWarning("Could not turn off Fast Startup: {Reason}", ex.Message);
        }
    }
}
