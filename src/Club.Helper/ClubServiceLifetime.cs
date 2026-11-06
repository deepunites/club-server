using Club.Helper.Core.Updates;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Club.Helper;

/// <summary>
/// Жизнь службы помощника (1.8.0, spec 2026-11-06, п. 4.1): отличает остановку по команде Windows (OnStop — остановка
/// службы, OnShutdown — выключение ПК) от любой другой остановки хоста. По команде Windows — <see cref="StopRequested"/> и
/// «чистый выход» в <c>update.json</c> (<see cref="SelfUpdater.MarkCleanExit"/>: следующий запуск неподтверждённой новой
/// сборки — не попытка), потом обычная остановка. Хост остановился без неё (исключение в фоновой службе, ошибка при
/// старте) — <c>Program.cs</c> завершает процесс, и Windows считает это сбоем.
/// </summary>
public sealed class ClubServiceLifetime(
    IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
    IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, SelfUpdater updater)
    : WindowsServiceLifetime(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
{
    private volatile bool _stopRequested;

    /// <summary>Windows попросила службу остановиться (остановка службы или выключение ПК).</summary>
    public bool StopRequested => _stopRequested;

    protected override void OnStop()
    {
        Stopping();
        base.OnStop();
    }

    protected override void OnShutdown()
    {
        Stopping();
        base.OnShutdown();
    }

    /// <summary>До остановки хоста: обработчик <c>ApplicationStopping</c> в <c>Program.cs</c> уже видит команду Windows.</summary>
    private void Stopping()
    {
        _stopRequested = true;
        updater.MarkCleanExit();
    }
}
