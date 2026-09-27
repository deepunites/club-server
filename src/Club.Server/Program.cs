using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;
using Club.Server.Diskless;
using Club.Server.Library;
using Club.Server.Panel;
using Club.TrueNas;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Club")
    ?? throw new InvalidOperationException("ConnectionStrings:Club is not configured");
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
var trueNasOptions = builder.Configuration.GetSection("TrueNas").Get<TrueNasOptions>() ?? new TrueNasOptions();
var panelOptions = builder.Configuration.GetSection("Panel").Get<PanelOptions>() ?? new PanelOptions();

builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(authOptions);
builder.Services.AddClubDatabase(connectionString);
builder.Services.AddSingleton<MachineRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<MachineAuthenticator>();
builder.Services.AddSingleton(libraryOptions);
builder.Services.AddSingleton(trueNasOptions);
builder.Services.AddSingleton(panelOptions);
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<TrueNasClient>();
builder.Services.AddSingleton<TrueNasStorage>();
builder.Services.AddSingleton<LibraryPublisher>();
if (libraryOptions.Enabled && libraryOptions.RunWorker)
{
    builder.Services.AddHostedService<StorageWorker>();
}

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStart", true))
{
    await Database.MigrateAsync(app.Services);
}

app.UseMiddleware<ApiErrorMiddleware>();
app.UseMiddleware<MachineAuthMiddleware>();
app.UseMiddleware<PanelAuthMiddleware>();

app.MapDisklessEndpoints();
app.MapLibraryPanelEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Точка входа для WebApplicationFactory в тестах.</summary>
public partial class Program;
