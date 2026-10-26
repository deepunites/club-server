using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Club.Helper;
using Club.Helper.Core;

// Помощник бездиска: служба Windows (LocalSystem). Настройки — %ProgramData%\ClubDiskless\helper.json:
// { "Helper": { "ServerUrl": "https://club-server", "ClubKey": "...", "CaCertificatePath": "C:\\ProgramData\\ClubDiskless\\club-ca.pem" } }
// Библиотеки кроме G: (1.6.0) — "Libraries": по умолчанию ["solo"] (H:), [] — только G: на этом ПК.
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(StateDirectory.Path, "helper.json"), optional: true, reloadOnChange: false);
builder.Services.AddWindowsService(o => o.ServiceName = "ClubDisklessHelper");
builder.Logging.AddEventLog(o => o.SourceName = "ClubDisklessHelper");

var options = builder.Configuration.GetSection("Helper").Get<HelperOptions>() ?? new HelperOptions();
options.HelperVersion = typeof(HelperService).Assembly.GetName().Version?.ToString(3) ?? options.HelperVersion;
options.Libraries = HelperOptions.LibrariesFrom(builder.Configuration.AsEnumerable());

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
builder.Services.AddSingleton<DisklessApiClient>();
// Цикл G: и по циклу на библиотеку (H: — solo) над одной Windows и одним клиентом API; у каждой библиотеки свой файл
// назначения (assignment.json, assignment.json.solo) и свои категории журнала.
builder.Services.AddSingleton(services => HelperLoops.Create(
    services.GetRequiredService<DisklessApiClient>(), services.GetRequiredService<IWindowsStorage>(), services.GetRequiredService<IProcessInspector>(),
    services.GetRequiredService<IMachineIdentity>(), options, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILoggerFactory>(),
    library => new FileAssignmentCache(StateDirectory.Ensure(), FileAssignmentCache.FileFor(library)),
    services.GetRequiredService<ILogger<HelperService>>()));
builder.Services.AddHostedService<HelperService>();

builder.Build().Run();

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
