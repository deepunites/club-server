using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Club.Helper;
using Club.Helper.Core;
using Club.Helper.Core.Updates;
using Microsoft.Extensions.Hosting.WindowsServices;

// Помощник бездиска: служба Windows (LocalSystem). Настройки — %ProgramData%\ClubDiskless\helper.json:
// { "Helper": { "ServerUrl": "https://club-server", "ClubKey": "...", "CaCertificatePath": "C:\\ProgramData\\ClubDiskless\\club-ca.pem" } }
// Библиотеки кроме G: (1.6.0) — "Libraries": по умолчанию ["solo"] (H:), [] — только G: на этом ПК.
// Сохранения игрока (1.7.0) — канал шелла \\.\pipe\club-diskless-helper (docs/helper.md, «Сохранения игр»).
// Самообновление (1.8.0, spec 2026-11-06-helper-self-update-design.md, п. 4.1) — каркас процесса по порядку:
// 1. SHA256 своего exe (helperBuild) и страж (UpdateGuard) — первыми, до хоста, только файлы; его строки — прямо в журнал
//    событий. Страж откатил замену — минимальный хост службы и завершение процесса (RollbackHost), Windows запускает
//    прежнюю сборку.
// 2. Действия Windows при сбое службы (restart/5000/restart/5000/restart/30000, сброс сутки) — поставить, если их нет.
// 3. Обычный хост с ClubServiceLifetime. Любой ненормальный конец хоста службы — исключение в фоновой службе, ошибка при
//    старте, остановка или конец хоста не по команде Windows — текст ошибки в update.json (только пока новая сборка на
//    проверке) и Process.Kill: Windows считает это сбоем и запускает службу снова (по умолчанию .NET остановил бы хост
//    аккуратно, и служба осталась бы остановленной). Необработанное исключение — тоже текст в update.json.
var asService = WindowsServiceHelpers.IsWindowsService();
var paths = UpdatePaths.Default();
var store = new UpdateStateStore(paths);
var serviceControl = new WindowsServiceControl();
var ownBuild = OwnBuild();
if (Guard() == GuardOutcome.RollbackAndTerminate)
{
    RollbackHost.Run(args, serviceControl, line => Journal(line, EventLogEntryType.Error));
    return;
}

if (asService)
{
    try
    {
        serviceControl.EnsureRecovery();
    }
    catch (Exception ex)
    {
        Journal($"Service recovery actions not set ({ex.GetType().Name}: {ex.Message}); self-update will report noRecovery", EventLogEntryType.Warning);
    }
}

var crash = new CrashRecorder(store, ownBuild);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    crash.Record(e.ExceptionObject is Exception ex ? CrashRecorder.Describe(ex) : $"Unhandled exception: {e.ExceptionObject}");

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(StateDirectory.Path, "helper.json"), optional: true, reloadOnChange: false);
builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceControl.ServiceName);
builder.Logging.AddEventLog(o => o.SourceName = WindowsServiceControl.ServiceName);

var options = builder.Configuration.GetSection("Helper").Get<HelperOptions>() ?? new HelperOptions();
options.HelperVersion = typeof(HelperService).Assembly.GetName().Version?.ToString(3) ?? options.HelperVersion;
options.HelperBuild = ownBuild;
options.Libraries = HelperOptions.LibrariesFrom(builder.Configuration.AsEnumerable());
// Версия и сборка для скриптов (helper-build.json, spec п. 5.1): только обычный путь службы, после стража — ветка отката
// сюда не доходит (её процесс — новая сборка, которая сейчас завершится).
if (asService && ownBuild is not null)
{
    HelperBuildFile.Write(paths, options.HelperVersion, ownBuild, line => Journal(line, EventLogEntryType.Warning));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWindowsStorage, WindowsStorage>();
builder.Services.AddSingleton<IProcessInspector, ProcessInspector>();
builder.Services.AddSingleton<IMachineFactsSource, WindowsMachineFacts>();
builder.Services.AddSingleton<IMachineIdentity, CachedMachineIdentity>();
builder.Services.AddSingleton<ICredentialStore, DpapiCredentialStore>();
builder.Services.AddSingleton(_ => new HttpClient(ServerHandler(options))
{
    BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(20),
});
// Архивы сохранений (1.7.0: attach и PUT, до сотен мегабайт) — свой HttpClient с долгим тайм-аутом; сборки помощника
// (1.8.0, самообновление: до 300 МБ, в фоне) — ещё один, на всю загрузку 15 минут; токены, отчёты и остальное — через
// основной (20 с). Клиент API один на такт помощника и канал шелла.
builder.Services.AddSingleton(services => new DisklessApiClient(
    services.GetRequiredService<HttpClient>(), options, services.GetRequiredService<ICredentialStore>(), services.GetRequiredService<IMachineIdentity>(),
    new HttpClient(ServerHandler(options)) { BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(130) },
    new HttpClient(ServerHandler(options)) { BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(15) }));
// Цикл G: и по циклу на библиотеку (H: — solo) над одной Windows и одним клиентом API; у каждой библиотеки свой файл
// назначения (assignment.json, assignment.json.solo) и свои категории журнала.
builder.Services.AddSingleton(services => HelperLoops.Create(
    services.GetRequiredService<DisklessApiClient>(), services.GetRequiredService<IWindowsStorage>(), services.GetRequiredService<IProcessInspector>(),
    services.GetRequiredService<IMachineIdentity>(), options, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILoggerFactory>(),
    library => new FileAssignmentCache(StateDirectory.Ensure(), FileAssignmentCache.FileFor(library)),
    services.GetRequiredService<ILogger<HelperService>>(),
    // Ход самообновления — в отчёт G:; берётся при отчёте (SelfUpdater зависит от сохранений, а они — от циклов).
    () => services.GetRequiredService<SelfUpdater>().Report()));
// Сохранения игрока (1.7.0, spec 2026-11-05): команды шелла по каналу \\.\pipe\club-diskless-helper; библиотека в режиме
// мастера — любой мастер-том на ПК подключён или подключается.
builder.Services.AddSingleton(services =>
{
    var loops = services.GetRequiredService<HelperLoops>();
    return new SavesService(
        services.GetRequiredService<DisklessApiClient>(), new WindowsProfileLocator(), new WindowsDriveRoots(), () => HelperService.MasterActive(loops),
        StateDirectory.Ensure(), services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<SavesService>>());
});
builder.Services.AddSingleton(services => new SavesCommandHandler(services.GetRequiredService<SavesService>(), options.HelperVersion, options.HelperBuild));
// Самообновление (1.8.0): поручение из ответа на отчёт G:, загрузка, замена, подтверждение; мастер-том — любой цикл.
builder.Services.AddSingleton<IServiceControl>(serviceControl);
builder.Services.AddSingleton(services => new SelfUpdater(
    services.GetRequiredService<DisklessApiClient>(), paths, store, serviceControl, services.GetRequiredService<SavesService>(),
    () => services.GetRequiredService<HelperLoops>().MasterBusy(), options, services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<SelfUpdater>>()));
if (asService)
{
    builder.Services.AddSingleton<ClubServiceLifetime>();
    builder.Services.AddSingleton<IHostLifetime>(services => services.GetRequiredService<ClubServiceLifetime>());
}

builder.Services.AddHostedService<HelperService>();
builder.Services.AddHostedService<SavesPipeServer>();

ClubServiceLifetime? lifetime = null;
try
{
    var host = builder.Build();
    if (asService)
    {
        lifetime = host.Services.GetRequiredService<ClubServiceLifetime>();
        var service = lifetime;
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            if (!service.StopRequested)
            {
                EndAbnormally(HostFault(host) ?? "the service host is stopping without a stop command from Windows");
            }
        });
    }

#if CLUB_TEST_FAULT_CRASH
    // Пробная сборка 1.8.90 (spec п. 7): падение вскоре после старта хоста — необработанное исключение в своём потоке.
    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => new Thread(() =>
    {
        Thread.Sleep(TimeSpan.FromSeconds(10));
        throw new InvalidOperationException("CLUB_TEST_FAULT_CRASH: test build crashes shortly after the host started");
    }) { IsBackground = true }.Start());
#endif

    host.Run();
    if (lifetime is { StopRequested: false })
    {
        EndAbnormally(HostFault(host) ?? "the service host ended without a stop command from Windows");
    }
}
catch (Exception ex) when (asService && lifetime is not { StopRequested: true })
{
    EndAbnormally($"the service host failed: {CrashRecorder.Describe(ex)}");
}

// SHA256 своего exe (helperBuild); не считается — null: страж не запускается, сервер сборку ПК не видит.
static string? OwnBuild()
{
    try
    {
        return Environment.ProcessPath is { } exe ? UpdatePaths.Sha256Of(exe) : null;
    }
    catch (Exception ex)
    {
        Journal($"SHA256 of the running exe not computed ({ex.GetType().Name}: {ex.Message}); self-update is off in this run", EventLogEntryType.Warning);
        return null;
    }
}

// Страж самообновления (spec п. 4.4) до хоста; папка состояния — заранее (хранилище её не создаёт). Строки — в журнал.
GuardOutcome Guard()
{
    var journal = new List<string>();
    var outcome = GuardOutcome.Run;
    try
    {
        StateDirectory.Ensure();
    }
    catch (Exception ex)
    {
        journal.Add($"State folder not prepared ({ex.GetType().Name}: {ex.Message})");
    }

    if (ownBuild is null)
    {
        journal.Add("SHA256 of the running exe is unknown: the self-update guard is skipped");
    }
    else
    {
        outcome = UpdateGuard.Run(paths, store, ownBuild, UpdatePaths.Sha256Of, TimeProvider.System, journal);
    }

    foreach (var line in journal)
    {
        Journal(line, outcome == GuardOutcome.RollbackAndTerminate ? EventLogEntryType.Warning : EventLogEntryType.Information);
    }

    return outcome;
}

// Ненормальный конец хоста службы: текст в update.json, строка в журнал и завершение процесса (сбой для Windows).
void EndAbnormally(string why)
{
    crash.Record(why);
    Journal($"Helper {options.HelperVersion} ({ownBuild ?? "build unknown"}) ended abnormally: {why}; ending the process so that Windows restarts the service",
        EventLogEntryType.Error);
    serviceControl.TerminateSelf();
}

// Исключение фоновой службы, из-за которого хост останавливается (BackgroundService.ExecuteTask); нет — null.
static string? HostFault(IHost host)
{
    try
    {
        return host.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Select(service => service.ExecuteTask?.Exception).OfType<Exception>().Select(CrashRecorder.Describe).FirstOrDefault();
    }
    catch (Exception)
    {
        return null;
    }
}

// Строка журнала событий «Приложение» (источник ClubDisklessHelper) без хоста: источника нет или журнал не пишется —
// запуск службы из-за этого не прерывается.
static void Journal(string line, EventLogEntryType type)
{
    try
    {
        EventLog.WriteEntry(WindowsServiceControl.ServiceName, line, type);
    }
    catch (Exception)
    {
        // Журнал — не повод не запустить службу.
    }
}

// Сертификат сервера клуба проверяется по внутреннему CA клуба, если он задан.
static SocketsHttpHandler ServerHandler(HelperOptions options)
{
    var handler = new SocketsHttpHandler();
    if (!string.IsNullOrWhiteSpace(options.CaCertificatePath))
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(options.CaCertificatePath);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        };
    }

    return handler;
}
