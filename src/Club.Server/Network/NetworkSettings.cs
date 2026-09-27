using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Club.Server.Network;

/// <summary>Сеть клуба для DHCP. Только IPv4: клиентские ПК и PXE клуба работают в IPv4.</summary>
public sealed record NetworkSettings(
    bool Configured,
    string Subnet,
    int KeaSubnetId,
    string Interface,
    string DhcpServer,
    string Gateway,
    IReadOnlyList<string> DnsServers,
    string PoolStart,
    string PoolEnd,
    string ReservedStart,
    int LeaseTimeSec);

/// <summary>Адресный план: подсеть, динамический пул и диапазон резерваций по номерам мест.</summary>
public sealed class IpPlan
{
    private readonly uint _network;
    private readonly uint _broadcast;

    public IpPlan(NetworkSettings settings)
    {
        Settings = settings;
        var (address, prefix) = ParseCidr(settings.Subnet);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        _network = address & mask;
        _broadcast = _network | ~mask;
        Prefix = prefix;
    }

    public NetworkSettings Settings { get; }

    public int Prefix { get; }

    public string Mask => ToIp(Prefix == 0 ? 0u : uint.MaxValue << (32 - Prefix));

    /// <summary>IP места: начало диапазона резерваций + номер − 1. <c>null</c>, если выходит за подсеть или задевает пул.</summary>
    public uint? SeatAddress(int seat)
    {
        if (seat < 1)
        {
            return null;
        }

        var ip = (ulong)ToUInt(Settings.ReservedStart) + (ulong)(seat - 1);
        if (ip > uint.MaxValue || !IsUsable((uint)ip) || InPool((uint)ip) || (uint)ip == ToUInt(Settings.Gateway) || (uint)ip == ToUInt(Settings.DhcpServer))
        {
            return null;
        }

        return (uint)ip;
    }

    public bool InPool(uint ip) => ip >= ToUInt(Settings.PoolStart) && ip <= ToUInt(Settings.PoolEnd);

    public bool IsUsable(uint ip) => ip > _network && ip < _broadcast;

    /// <summary>Ошибки настроек (поле → причина); пусто — настройки согласованы.</summary>
    public static IReadOnlyList<(string Field, string Reason)> Validate(NetworkSettings s)
    {
        var errors = new List<(string, string)>();
        if (!TryParseCidr(s.Subnet, out var address, out var prefix) || prefix is < 8 or > 30)
        {
            errors.Add(("subnet", "format"));
            return errors;
        }

        if ((address & (uint.MaxValue << (32 - prefix))) != address)
        {
            // 192.168.77.5/24 вместо 192.168.77.0/24: Kea такую подсеть не примет.
            errors.Add(("subnet", "notNetworkAddress"));
            return errors;
        }

        var plan = new IpPlan(s);
        foreach (var (field, value) in new[] { ("dhcpServer", s.DhcpServer), ("gateway", s.Gateway), ("poolStart", s.PoolStart), ("poolEnd", s.PoolEnd), ("reservedStart", s.ReservedStart) })
        {
            if (!TryParseIp(value, out var ip) || !plan.IsUsable(ip))
            {
                errors.Add((field, "outsideSubnet"));
            }
        }

        if (s.DnsServers.Count == 0 || s.DnsServers.Any(d => !TryParseIp(d, out _)))
        {
            errors.Add(("dnsServers", "format"));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        if (ToUInt(s.PoolStart) > ToUInt(s.PoolEnd))
        {
            errors.Add(("poolEnd", "beforeStart"));
        }

        if (plan.InPool(ToUInt(s.ReservedStart)))
        {
            errors.Add(("reservedStart", "insidePool"));
        }

        if (plan.InPool(ToUInt(s.DhcpServer)) || plan.InPool(ToUInt(s.Gateway)))
        {
            errors.Add(("poolStart", "coversInfrastructure"));
        }

        if (s.KeaSubnetId < 1)
        {
            errors.Add(("keaSubnetId", "range"));
        }

        if (string.IsNullOrWhiteSpace(s.Interface) || s.Interface.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')))
        {
            errors.Add(("interface", "format"));
        }

        if (s.LeaseTimeSec is < 300 or > 604800)
        {
            errors.Add(("leaseTimeSec", "range"));
        }

        return errors;
    }

    public static uint ToUInt(string ip) => TryParseIp(ip, out var value) ? value : throw new FormatException($"invalid IPv4 {ip}");

    public static string ToIp(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes).ToString();
    }

    public static bool TryParseIp(string? text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || text!.Count(c => c == '.') != 3)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return true;
    }

    private static (uint Address, int Prefix) ParseCidr(string cidr) =>
        TryParseCidr(cidr, out var address, out var prefix) ? (address, prefix) : throw new FormatException($"invalid subnet {cidr}");

    private static bool TryParseCidr(string? cidr, out uint address, out int prefix)
    {
        address = 0;
        prefix = 0;
        var parts = cidr?.Split('/');
        return parts is { Length: 2 } && TryParseIp(parts[0], out address) && int.TryParse(parts[1], out prefix) && prefix is >= 0 and <= 32;
    }
}

public sealed class NetworkRepository(NpgsqlDataSource db)
{
    private sealed class Row
    {
        public bool Configured { get; init; }
        public string Subnet { get; init; } = "";
        public int KeaSubnetId { get; init; }
        public string Interface { get; init; } = "";
        public string DhcpServer { get; init; } = "";
        public string Gateway { get; init; } = "";
        public string[] DnsServers { get; init; } = [];
        public string PoolStart { get; init; } = "";
        public string PoolEnd { get; init; } = "";
        public string ReservedStart { get; init; } = "";
        public int LeaseTimeSec { get; init; }
    }

    public async Task<NetworkSettings> GetAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var r = await c.QuerySingleAsync<Row>(
            """
            SELECT configured, subnet::text AS Subnet, kea_subnet_id AS KeaSubnetId, interface, host(dhcp_server) AS DhcpServer,
                   host(gateway) AS Gateway, ARRAY(SELECT host(d) FROM unnest(dns_servers) d) AS DnsServers,
                   host(pool_start) AS PoolStart, host(pool_end) AS PoolEnd, host(reserved_start) AS ReservedStart,
                   lease_time_sec AS LeaseTimeSec
            FROM network_settings
            """);
        return new NetworkSettings(r.Configured, r.Subnet, r.KeaSubnetId, r.Interface, r.DhcpServer, r.Gateway, r.DnsServers,
            r.PoolStart, r.PoolEnd, r.ReservedStart, r.LeaseTimeSec);
    }

    public async Task SaveAsync(NetworkSettings s, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE network_settings SET configured = true, subnet = $1::cidr, kea_subnet_id = $2, interface = $3,
                   dhcp_server = $4::inet, gateway = $5::inet, dns_servers = $6::inet[], pool_start = $7::inet, pool_end = $8::inet,
                   reserved_start = $9::inet, lease_time_sec = $10, updated_at = $11
            """, c);
        cmd.Parameters.AddWithValue(s.Subnet);
        cmd.Parameters.AddWithValue(s.KeaSubnetId);
        cmd.Parameters.AddWithValue(s.Interface);
        cmd.Parameters.AddWithValue(s.DhcpServer);
        cmd.Parameters.AddWithValue(s.Gateway);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, s.DnsServers.ToArray());
        cmd.Parameters.AddWithValue(s.PoolStart);
        cmd.Parameters.AddWithValue(s.PoolEnd);
        cmd.Parameters.AddWithValue(s.ReservedStart);
        cmd.Parameters.AddWithValue(s.LeaseTimeSec);
        cmd.Parameters.AddWithValue(now);
        await cmd.ExecuteNonQueryAsync();
    }
}
