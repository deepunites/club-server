using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;
using Club.Server.Diskless;
using Club.Server.Imaging;
using Club.Server.Integration;
using Club.Server.Library;
using Club.Server.Network;
using Club.Server.Panel;
using Club.TrueNas;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Club")
    ?? throw new InvalidOperationException("ConnectionStrings:Club is not configured");
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
libraryOptions.Validate();
// Вторая и следующие библиотеки игр (Libraries:<ключ>, H: — solo): свои схема БД и имена в TrueNAS; общие настройки — из Library.
var extraLibraries = LibraryOptions.BindExtras(builder.Configuration.GetSection("Libraries"), libraryOptions);
var trueNasOptions = builder.Configuration.GetSection("TrueNas").Get<TrueNasOptions>() ?? new TrueNasOptions();
var panelOptions = builder.Configuration.GetSection("Panel").Get<PanelOptions>() ?? new PanelOptions();
var keaOptions = builder.Configuration.GetSection("Kea").Get<KeaOptions>() ?? new KeaOptions();
var imagingOptions = builder.Configuration.GetSection("Imaging").Get<ImagingOptions>() ?? new ImagingOptions();
var machineFeedOptions = builder.Configuration.GetSection("MachineFeed").Get<MachineFeedOptions>() ?? new MachineFeedOptions();
machineFeedOptions.Validate();
var disklessOptions = builder.Configuration.GetSection("Diskless").Get<DisklessOptions>() ?? new DisklessOptions();
disklessOptions.Validate();

builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(authOptions);
builder.Services.AddClubDatabase(connectionString);
foreach (var extra in extraLibraries)
{
    builder.Services.AddLibraryDataSource(connectionString, extra.Key);
}

builder.Services.AddSingleton<MachineRepository>();
builder.Services.AddSingleton<ZoneRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<MachineAuthenticator>();
builder.Services.AddSingleton(libraryOptions);
builder.Services.AddSingleton(trueNasOptions);
builder.Services.AddSingleton(panelOptions);
builder.Services.AddSingleton(keaOptions);
builder.Services.AddSingleton(imagingOptions);
builder.Services.AddSingleton<ImageRepository>();
builder.Services.AddSingleton<ImageLibrary>();
builder.Services.AddSingleton<BootFiles>();
builder.Services.AddSingleton<ReimageService>();
if (imagingOptions.Enabled && imagingOptions.RunWorker)
{
    builder.Services.AddHostedService<ImagingWorker>();
}
builder.Services.AddSingleton<NetworkRepository>();
// Адреса TrueNAS/портала/самого сервера: ни место, ни пул гостей их не получают (docs/network.md).
builder.Services.AddSingleton(StaticHosts.From(trueNasOptions.Host, libraryOptions.PortalAddress, libraryOptions.DiscoveryAddress, imagingOptions.PublicBaseUrl));
builder.Services.AddSingleton<KeaHostSync>();
builder.Services.AddSingleton<NetworkState>();
if (builder.Configuration.GetValue("Network:RunWorker", true))
{
    builder.Services.AddHostedService<NetworkWorker>();
}
builder.Services.AddSingleton(machineFeedOptions);
builder.Services.AddSingleton<MachineFeedState>();
builder.Services.AddSingleton<MachineFeed>();
if (machineFeedOptions.Enabled && machineFeedOptions.RunWorker)
{
    builder.Services.AddHostedService<MachineFeedWorker>();
}
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<TrueNasClient>();
builder.Services.AddSingleton<TrueNasStorage>();
builder.Services.AddSingleton<TargetVerifier>();
builder.Services.AddSingleton<MasterRepository>();
builder.Services.AddSingleton<MasterEditor>();
builder.Services.AddSingleton<LibraryPublisher>();
builder.Services.AddSingleton<SeatGamesRepository>();
builder.Services.AddSingleton<SeatGames>();
// Без ключа зарегистрирована библиотека G: (её же берут воркеры образов и сети); остальные — в LibrarySet.
builder.Services.AddSingleton(services => LibrarySet.Create(services, extraLibraries));
builder.Services.AddSingleton(disklessOptions);
builder.Services.AddSingleton<DisklessRepository>();
builder.Services.AddSingleton<DisklessImages>();
builder.Services.AddSingleton<SeatDisks>();
if (libraryOptions.Enabled && libraryOptions.RunWorker)
{
    builder.Services.AddHostedService<StorageWorker>();
}

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStart", true))
{
    await Database.MigrateAsync(app.Services);
}

// Схема каждой следующей библиотеки на месте — иначе она выключается, G: работает.
await app.Services.GetRequiredService<LibrarySet>().CheckSchemasAsync();

app.UseMiddleware<ApiErrorMiddleware>();
app.UseDefaultFiles();
// Файлы панели — с no-cache: браузер сверяет их (ETag, 304) при каждой загрузке. Без Cache-Control он держал app.js по
// эвристике, и после обновления сервера новая страница могла достаться со старым скриптом (пустая панель до Ctrl+F5).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseMiddleware<MachineAuthMiddleware>();
app.UseMiddleware<PanelAuthMiddleware>();

app.MapDisklessEndpoints();
app.MapLibraryPanelEndpoints();
app.MapMachinesPanelEndpoints();
app.MapZonesPanelEndpoints();
app.MapNetworkPanelEndpoints();
app.MapImagesPanelEndpoints();
app.MapDisklessPanelEndpoints();
app.MapPxeEndpoints();
app.MapDeployEndpoints();
app.MapGet("/", () => Results.Redirect("/panel/"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Точка входа для WebApplicationFactory в тестах.</summary>
public partial class Program;
