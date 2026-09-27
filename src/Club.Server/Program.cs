using Club.Server.Agents;
using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;
using Club.Server.Realtime;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Club")
    ?? throw new InvalidOperationException("ConnectionStrings:Club is not configured");
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var realtimeOptions = builder.Configuration.GetSection("Realtime").Get<RealtimeOptions>() ?? new RealtimeOptions();
var contractPath = Path.Combine(AppContext.BaseDirectory, builder.Configuration["Contracts:OpenApiPath"] ?? "contracts/openapi.yaml");

builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton(realtimeOptions);
builder.Services.AddClubDatabase(connectionString);
builder.Services.AddSingleton<PcRepository>();
builder.Services.AddSingleton<CommandRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AgentAuthenticator>();
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<AgentSocketHub>();

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStart", true))
{
    await Database.MigrateAsync(app.Services);
}

app.UseMiddleware<ApiErrorMiddleware>();
app.UseWebSockets();
app.UseMiddleware<AgentAuthMiddleware>();

app.MapAgentEndpoints();
app.Map("/ws/agent", (HttpContext context, AgentSocketHub hub) => hub.HandleAsync(context));
app.MapNotImplemented(ContractStatus.Load(contractPath), AgentEndpoints.Implemented);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Точка входа для WebApplicationFactory в тестах.</summary>
public partial class Program;
