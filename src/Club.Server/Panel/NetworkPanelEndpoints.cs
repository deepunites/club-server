using Club.Server.Api;
using Club.Server.Data;
using Club.Server.Library;
using Club.Server.Network;

namespace Club.Server.Panel;

public sealed record NetworkSettingsView(
    bool Configured, string Subnet, string Mask, int KeaSubnetId, string Interface, string DhcpServer, string Gateway,
    IReadOnlyList<string> DnsServers, string PoolStart, string PoolEnd, string ReservedStart, int LeaseTimeSec, int SeatCapacity,
    IReadOnlyList<string> ServerInterfaces);

public sealed record NetworkSettingsInput(
    string Subnet, int KeaSubnetId, string Interface, string DhcpServer, string Gateway,
    IReadOnlyList<string> DnsServers, string PoolStart, string PoolEnd, string ReservedStart, int LeaseTimeSec);

public sealed record ReservationView(int Seat, string Name, string? Mac, string? Ip, string? Hostname, string? Problem);

public sealed record KeaSyncView(DateTimeOffset? At, bool SchemaOk, string? SchemaVersion, int Desired, int Inserted, int Updated, int Deleted, string? Error);

public sealed record NetworkStatusView(
    bool Configured, bool KeaEnabled, KeaSyncView? Sync, IReadOnlyList<ForeignDhcp> ForeignDhcp,
    IReadOnlyList<ReservationView> Reservations, IReadOnlyList<StorageWarningView> Warnings);

/// <summary>
/// Экран «Сеть»: адресный план клуба, резервации Kea по номерам мест, чужой DHCP. Конфиг Kea сервер только генерирует —
/// файл в <c>/etc/kea</c> ставит администратор (docs/network.md).
/// </summary>
public static class NetworkPanelEndpoints
{
    /// <summary>Сколько мест помещается в диапазон резерваций до пула, шлюза или конца подсети.</summary>
    public static int SeatCapacity(NetworkSettings s)
    {
        var plan = new IpPlan(s);
        var seat = 0;
        while (seat < 4096 && plan.SeatAddress(seat + 1) is not null)
        {
            seat++;
        }

        return seat;
    }

    public static void MapNetworkPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1/network");

        panel.MapGet("/settings", async (NetworkRepository network) => Results.Json(View(await network.GetAsync()), ApiJson.Options));

        panel.MapPut("/settings", async (NetworkSettingsInput input, NetworkRepository network, IServiceProvider services, TimeProvider clock, ILoggerFactory logs) =>
        {
            var settings = new NetworkSettings(
                true, input.Subnet?.Trim() ?? "", input.KeaSubnetId, input.Interface?.Trim() ?? "", input.DhcpServer?.Trim() ?? "",
                input.Gateway?.Trim() ?? "", (input.DnsServers ?? []).Select(d => d.Trim()).Where(d => d.Length > 0).ToList(),
                input.PoolStart?.Trim() ?? "", input.PoolEnd?.Trim() ?? "", input.ReservedStart?.Trim() ?? "", input.LeaseTimeSec);
            var errors = IpPlan.Validate(settings);
            if (errors.Count > 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Invalid network settings",
                    new { field = errors[0].Field, reason = errors[0].Reason, errors = errors.Select(e => new { field = e.Field, reason = e.Reason }) });
            }

            await network.SaveAsync(settings, clock.GetUtcNow());
            logs.CreateLogger("Club.Server.Panel").LogInformation("Network settings changed: {Subnet} pool {PoolStart}-{PoolEnd} seats from {ReservedStart}",
                settings.Subnet, settings.PoolStart, settings.PoolEnd, settings.ReservedStart);

            // Резервации под новый план — сразу, не дожидаясь прохода фоновой службы. Ошибка Kea попадёт в предупреждения.
            await NetworkWorker.RunOnceAsync(services, clock, CancellationToken.None);
            return Results.Json(View(await network.GetAsync()), ApiJson.Options);
        });

        panel.MapGet("/status", async (NetworkRepository network, MachineRepository machines, LibraryRepository library, NetworkState state, KeaOptions kea) =>
        {
            var settings = await network.GetAsync();
            var registry = await machines.AllAsync();
            var reservations = new List<ReservationView>();
            if (settings.Configured)
            {
                var (hosts, conflicts) = KeaHostSync.Plan(registry, new IpPlan(settings));
                foreach (var m in registry.Where(m => m.Approved).OrderBy(m => m.Number))
                {
                    var host = hosts.FirstOrDefault(h => h.MachineId == m.Id);
                    var problem = host is null ? conflicts.FirstOrDefault(c => c.Subject == m.Name || (m.MacAddresses.Length > 0 && c.Subject == m.MacAddresses[0]))?.Kind : null;
                    reservations.Add(new ReservationView(
                        m.Number, m.Name, m.MacAddresses.FirstOrDefault(), host is null ? null : IpPlan.ToIp(host.Ip), host?.Hostname, problem));
                }
            }

            var sync = state.LastSyncAt is null
                ? null
                : new KeaSyncView(state.LastSyncAt, state.LastSync?.SchemaOk ?? false, state.LastSync?.SchemaVersion, state.LastSync?.Desired ?? 0,
                    state.LastSync?.Inserted ?? 0, state.LastSync?.Updated ?? 0, state.LastSync?.Deleted ?? 0, state.LastError);
            var warnings = (await library.ActiveWarningsAsync("network"))
                .Select(w => new StorageWarningView(w.Kind, w.Subject, w.Message, w.FirstSeen, w.LastSeen)).ToList();
            return Results.Json(new NetworkStatusView(settings.Configured, kea.Enabled, sync, state.Foreign, reservations, warnings), ApiJson.Options);
        });

        panel.MapGet("/kea-dhcp4.conf", async (NetworkRepository network) =>
        {
            var settings = await network.GetAsync();
            if (!settings.Configured)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Network settings are not saved yet", new { reason = "notConfigured" });
            }

            return Results.Text(KeaConfig.Render(settings), "application/json; charset=utf-8");
        });
    }

    private static NetworkSettingsView View(NetworkSettings s) =>
        new(s.Configured, s.Subnet, new IpPlan(s).Mask, s.KeaSubnetId, s.Interface, s.DhcpServer, s.Gateway, s.DnsServers,
            s.PoolStart, s.PoolEnd, s.ReservedStart, s.LeaseTimeSec, SeatCapacity(s), ServerInterfaces());

    /// <summary>Интерфейсы этой машины — подсказка для поля «интерфейс» (Kea не примет подсеть на несуществующем).</summary>
    private static List<string> ServerInterfaces() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => n.Name)
            .Order()
            .ToList();
}
