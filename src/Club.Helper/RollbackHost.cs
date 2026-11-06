using System.Diagnostics;
using System.ServiceProcess;
using Club.Helper.Core.Updates;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Club.Helper;

/// <summary>
/// Конец запуска, в котором страж откатил замену (<see cref="GuardOutcome.RollbackAndTerminate"/>, spec 2026-11-06, п. 4.4):
/// exe на диске уже прежняя сборка, а работает ещё новая. Минимальный хост службы (без циклов, канала и сервера) сообщает
/// Windows, что служба запущена, и, когда Windows считает её запущенной (<see cref="RunningWait"/> самое большее), процесс
/// завершается (<see cref="IServiceControl.TerminateSelf"/>): Windows считает это сбоем и запускает службу — уже
/// прежней сборкой. Без хоста процесс, завершённый до запуска службы, Windows могла бы не перезапустить. Не служба
/// (запуск из консоли) — процесс завершается сразу.
/// </summary>
public static class RollbackHost
{
    /// <summary>Сколько самое большее ждать, пока Windows не сочтёт службу запущенной.</summary>
    public static readonly TimeSpan RunningWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>Запустить хост и завершить процесс; <paramref name="journal"/> — строка в журнал событий (сбой хоста).</summary>
    public static void Run(string[] args, IServiceControl service, Action<string> journal)
    {
        try
        {
            if (WindowsServiceHelpers.IsWindowsService())
            {
                var builder = Host.CreateApplicationBuilder(args);
                builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceControl.ServiceName);
                builder.Services.AddSingleton(service);
                builder.Services.AddHostedService<Terminator>();
                builder.Build().Run();
            }
        }
        catch (Exception ex)
        {
            journal($"Rollback host failed ({ex.GetType().Name}: {ex.Message}); ending the process so that Windows starts the previous build");
        }

        service.TerminateSelf();
    }

    /// <summary>Ждёт состояния «запущена» у Windows и завершает процесс.</summary>
    private sealed class Terminator(IServiceControl service) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var waited = Stopwatch.StartNew();
            try
            {
                using var controller = new ServiceController(WindowsServiceControl.ServiceName);
                while (waited.Elapsed < RunningWait)
                {
                    controller.Refresh();
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        break;
                    }

                    await Task.Delay(Poll, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // Состояние не читается или службу останавливают — всё равно завершить процесс.
            }

            service.TerminateSelf();
        }
    }
}
