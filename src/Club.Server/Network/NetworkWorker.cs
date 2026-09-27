using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Network;

/// <summary>Чужой DHCP в сети клуба: кто его видит и когда.</summary>
public sealed record ForeignDhcp(string Server, IReadOnlyList<string> SeenBy);

/// <summary>Последний проход сети — для панели (живёт в памяти процесса).</summary>
public sealed class NetworkState
{
    public KeaSyncResult? LastSync { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public IReadOnlyList<ForeignDhcp> Foreign { get; set; } = [];
}

/// <summary>Синхронизация резерваций Kea и детект чужого DHCP раз в <see cref="IntervalSec"/> секунд.</summary>
public sealed class NetworkWorker(IServiceProvider services, TimeProvider clock, ILogger<NetworkWorker> logger) : BackgroundService
{
    public const int IntervalSec = 30;

    /// <summary>Отчёт помощника старше этого — его сведения о DHCP не учитываются (ПК выключен или ушёл).</summary>
    public static readonly TimeSpan FreshReport = TimeSpan.FromMinutes(10);

    /// <summary>
    /// DHCP-серверы, которые видят ПК, кроме нашего. Сведения — из отчётов помощников (сервер DHCP в аренде каждой
    /// сетевой карты). Пустой список при несконфигурированной сети: не с чем сравнивать.
    /// </summary>
    public static IReadOnlyList<ForeignDhcp> DetectForeign(IReadOnlyList<MachineRow> machines, NetworkSettings settings, DateTimeOffset now)
    {
        if (!settings.Configured)
        {
            return [];
        }

        return machines
            .Where(m => m.LastSeenAt is { } seen && now - seen <= FreshReport && m.DhcpServers is { Length: > 0 })
            .SelectMany(m => m.DhcpServers!.Where(d => d != settings.DhcpServer).Select(d => (Server: d, Machine: m.Name)))
            .GroupBy(x => x.Server)
            .Select(g => new ForeignDhcp(g.Key, g.Select(x => x.Machine).Distinct().OrderBy(n => n).ToList()))
            .OrderBy(f => f.Server)
            .ToList();
    }

    public static async Task RunOnceAsync(IServiceProvider scope, TimeProvider clock, CancellationToken ct)
    {
        var state = scope.GetRequiredService<NetworkState>();
        var warnings = new List<(string Kind, string Subject, string Message)>();
        try
        {
            var result = await scope.GetRequiredService<KeaHostSync>().SyncAsync(ct);
            state.LastSync = result;
            state.LastError = null;
            state.LastSyncAt = result is null ? null : clock.GetUtcNow();
            warnings.AddRange(result?.Conflicts.Select(c => (c.Kind, c.Subject, c.Message)) ?? []);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or FormatException)
        {
            state.LastError = ex.Message;
            state.LastSyncAt = clock.GetUtcNow();
            warnings.Add(("keaUnavailable", "kea", $"База Kea недоступна: {ex.Message}"));
        }

        var settings = await scope.GetRequiredService<NetworkRepository>().GetAsync();
        state.Foreign = DetectForeign(await scope.GetRequiredService<MachineRepository>().AllAsync(), settings, clock.GetUtcNow());
        warnings.AddRange(state.Foreign.Select(f => ("foreignDhcp", f.Server,
            $"Чужой DHCP-сервер {f.Server} в сети клуба (видят: {string.Join(", ", f.SeenBy)})")));

        await scope.GetRequiredService<LibraryRepository>().ReplaceWarningsAsync(warnings, clock.GetUtcNow(), source: "network");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(services, clock, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Network worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(IntervalSec), clock, stoppingToken);
        }
    }
}
