using Club.Helper.Core;
using Club.Helper.Core.Updates;

namespace Club.Helper;

/// <summary>
/// Такт помощника сразу после старта службы и дальше каждые <see cref="HelperOptions.PollIntervalSec"/> секунд: G:, потом
/// остальные библиотеки (<see cref="HelperLoops"/>; сбой такта — там же, в журнал). Время тактов библиотек входит в паузу
/// (<see cref="HelperLoops.DelayAfterTick"/>): отчёт G: идёт с тем же шагом, что у помощника 1.5.
/// После каждого такта (1.7.0) — такт сохранений (<see cref="SavesService.OnTickAsync"/>: список папок, исходное содержимое
/// папок на G:/H:) с библиотеками из последних отчётов циклов. Он идёт в фоне, по одному: снятие исходного содержимого
/// больших папок с тома библиотеки не задерживает отчёт G: (ПК «на связи» — отчёт не реже 90 с).
/// Самообновление (1.8.0, spec 2026-11-06, п. 4.3) — только здесь: сразу после такта <see cref="SelfUpdater.OnTick"/> с
/// последним принятым ответом G: (поручение, отмена, подтверждение), затем, если такт сохранений закончился,
/// <see cref="SelfUpdater.AfterTickAsync"/> (загрузка, готовность, замена и завершение процесса) — до следующего такта
/// сохранений: замена не начинается посреди него. Такт сохранений ещё идёт — шаг замены ждёт следующего такта.
/// </summary>
public sealed class HelperService(HelperLoops loops, SavesService saves, SelfUpdater updater, HelperOptions options, ILogger<HelperService> logger)
    : BackgroundService
{
    private Task _savesTick = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DisableFastStartup();
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.PollIntervalSec));
#if CLUB_TEST_FAULT_HANG
        // Пробная сборка 1.8.92 (spec п. 7): такт не начинается, отчётов нет — сервер ставит noReturn.
        await Task.Delay(Timeout.Infinite, stoppingToken);
#endif
        while (!stoppingToken.IsCancellationRequested)
        {
            await loops.TickAsync(stoppingToken);
            updater.OnTick(loops.Games.LastAccepted, loops.Games.AcceptedReports);
#if CLUB_TEST_FAULT_THROW
            // Пробная сборка 1.8.91 (spec п. 7): исключение в фоновой службе после первого такта.
            if (!stoppingToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("CLUB_TEST_FAULT_THROW: test build fails in HelperService.ExecuteAsync after the first tick");
            }
#endif
            if (_savesTick.IsCompleted)
            {
                await updater.AfterTickAsync(stoppingToken);
            }

            StartSavesTick(stoppingToken);
            await Task.Delay(loops.DelayAfterTick(interval), stoppingToken);
        }
    }

    /// <summary>Библиотеки ПК для сохранений: буква и версия из последнего отчёта цикла, подключена — <c>mounted</c>.</summary>
    public static IReadOnlyList<LibraryMount> Libraries(HelperLoops loops) =>
        [.. new[] { loops.Games }.Concat(loops.Libraries)
            .Select(loop => loop.LastReport)
            .OfType<MountedVolume>()
            .Where(report => report.DriveLetter is { Length: 1 })
            .Select(report => new LibraryMount(report.DriveLetter![0], report.LibraryVersion, report.State == "mounted"))];

    /// <summary>Библиотека в режиме мастера (spec п. 4.3): мастер-том любой библиотеки подключён или подключается.</summary>
    public static bool MasterActive(HelperLoops loops) =>
        new[] { loops.Games }.Concat(loops.Libraries).Any(loop => loop.LastMasterReport?.State is "mounted" or "mounting");

    /// <summary>Такт сохранений в фоне; предыдущий ещё идёт — этот пропускается (следующий такт помощника попробует снова).</summary>
    private void StartSavesTick(CancellationToken stoppingToken)
    {
        if (!_savesTick.IsCompleted)
        {
            return;
        }

        var libraries = Libraries(loops);
        _savesTick = Task.Run(async () =>
        {
            try
            {
                await saves.OnTickAsync(libraries, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Saves tick failed");
            }
        }, CancellationToken.None);
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
