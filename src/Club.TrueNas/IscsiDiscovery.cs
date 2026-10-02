using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Club.TrueNas;

/// <summary>
/// Портал проверить не удалось: нет связи, вход отклонён (например, discovery с CHAP), ответ не по протоколу.
/// Это «не знаю», а не «таргета нет».
/// </summary>
public sealed class IscsiDiscoveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Минимальный инициатор iSCSI ровно для одного действия: discovery-сессия без аутентификации и <c>SendTargets=All</c>
/// (RFC 7143 §6.3, §11.10–§11.15). iscsi-scstd отдаёт таргет, только если он включён и у этого инициатора в нём есть
/// LUN, — то же, что нужно ПК для входа. Рантайм SCST через API TrueNAS не виден (docs/research/truenas-api.md §8.4),
/// поэтому это единственная проверка «таргет действительно отдан».
/// </summary>
public static class IscsiDiscovery
{
    public const int DefaultPort = 3260;

    private const int BhsLength = 48;
    private const int MaxRecvDataSegmentLength = 65536;
    private const int MaxTextBytes = 1 << 20;

    private const byte OpLoginRequest = 0x03;
    private const byte OpTextRequest = 0x04;
    private const byte OpLogoutRequest = 0x06;
    private const byte OpNopIn = 0x20;
    private const byte OpLoginResponse = 0x23;
    private const byte OpTextResponse = 0x24;
    private const byte OpLogoutResponse = 0x26;
    private const byte OpAsyncMessage = 0x32;
    private const byte OpReject = 0x3F;
    private const byte Immediate = 0x40;
    private const byte FlagFinal = 0x80;
    private const byte FlagTransit = 0x80;

    private const int StageSecurity = 0;
    private const int StageOperational = 1;
    private const int StageFullFeature = 3;

    /// <summary>IQN таргетов, которые портал <paramref name="portal"/> (<c>host[:port]</c>) показывает инициатору <paramref name="initiatorName"/>.</summary>
    public static async Task<IReadOnlyList<string>> SendTargetsAsync(string portal, string initiatorName, TimeSpan timeout, CancellationToken ct = default)
    {
        var (host, port) = ParsePortal(portal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, port, deadline.Token);
            var session = new Session(tcp.GetStream());
            await session.LoginAsync(initiatorName, deadline.Token);
            var targets = await session.SendTargetsAsync(deadline.Token);
            try
            {
                await session.LogoutAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or IscsiDiscoveryException)
            {
                // Список уже получен; без чистого выхода iscsi-scstd просто закроет сессию.
            }

            return targets;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IscsiDiscoveryException($"iSCSI discovery at {portal}: no answer within {timeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new IscsiDiscoveryException($"iSCSI discovery at {portal}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <c>host</c>, <c>host:port</c>, <c>[IPv6]</c> или <c>[IPv6]:port</c>; без порта — 3260. Строка с двумя и более
    /// двоеточиями без скобок — целиком IPv6-адрес, и в скобках — тоже только IPv6-адрес: иначе <c>http://host:3260</c>
    /// или <c>host:3260:3260</c> сошли бы за IPv6 и всплыли бы лишь ошибкой сокета на шаге verify. Порт вне 1..65535 или
    /// не число — ошибка разбора, а не исключение сокета: оно не было бы «не знаю».
    /// </summary>
    public static (string Host, int Port) ParsePortal(string portal)
    {
        var value = portal.Trim();
        if (value.Length == 0)
        {
            throw new IscsiDiscoveryException("iSCSI portal address is not configured");
        }

        string host;
        string? port = null;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0 || (end + 1 < value.Length && value[end + 1] != ':'))
            {
                throw new IscsiDiscoveryException($"bad iSCSI portal address '{portal}'");
            }

            host = RequireIPv6(value[1..end], portal);
            port = end + 1 < value.Length ? value[(end + 2)..] : null;
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0 && colon == value.LastIndexOf(':'))
            {
                (host, port) = (value[..colon], value[(colon + 1)..]);
            }
            else
            {
                host = colon >= 0 ? RequireIPv6(value, portal) : value;
            }
        }

        if (host.Length == 0)
        {
            throw new IscsiDiscoveryException($"bad iSCSI portal address '{portal}': no host");
        }

        if (port is null)
        {
            return (host, DefaultPort);
        }

        return int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535
            ? (host, number)
            : throw new IscsiDiscoveryException($"bad iSCSI portal address '{portal}': port must be 1..65535");
    }

    private static string RequireIPv6(string host, string portal) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? host
            : throw new IscsiDiscoveryException($"bad iSCSI portal address '{portal}': expected host[:port], [IPv6]:port or a bare IPv6 address");

    private sealed class Session(Stream stream)
    {
        private readonly byte[] _isid = NewIsid();
        private uint _cmdSn = 1;
        private uint _expStatSn;

        /// <summary>Security (AuthMethod=None) → operational → full feature; таргет может сократить или растянуть стадии.</summary>
        public async Task LoginAsync(string initiatorName, CancellationToken ct)
        {
            var stage = StageSecurity;
            var keys = $"InitiatorName={initiatorName}\0SessionType=Discovery\0AuthMethod=None\0";
            for (var round = 0; round < 8; round++)
            {
                var next = stage == StageSecurity ? StageOperational : StageFullFeature;
                var bhs = NewBhs(Immediate | OpLoginRequest, (byte)(FlagTransit | (stage << 2) | next), itt: 0);
                _isid.CopyTo(bhs, 8);
                BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(24), _cmdSn);
                BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28), _expStatSn);
                await SendAsync(bhs, keys, ct);

                var (response, _) = await ReceiveAsync(OpLoginResponse, ct);
                if (response[36] != 0)
                {
                    // 0x02xx — ошибка инициатора (0x0201: нужна аутентификация), 0x03xx — ошибка таргета.
                    throw new IscsiDiscoveryException($"iSCSI discovery login rejected: status 0x{response[36]:X2}{response[37]:X2}");
                }

                _expStatSn = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(24)) + 1;
                if ((response[1] & FlagTransit) == 0)
                {
                    keys = ""; // таргет просит остаться на стадии; нового сказать нечего
                    continue;
                }

                var nsg = response[1] & 0x03;
                if (nsg == StageFullFeature)
                {
                    return;
                }

                keys = nsg == StageOperational && stage != StageOperational
                    ? $"HeaderDigest=None\0DataDigest=None\0MaxRecvDataSegmentLength={MaxRecvDataSegmentLength}\0"
                    : "";
                stage = nsg;
            }

            throw new IscsiDiscoveryException("iSCSI discovery login did not reach the full feature phase");
        }

        /// <summary>Text-запрос; длинный ответ таргет режет на части (F=0) — дочитываются пустыми запросами с его TTT.</summary>
        public async Task<IReadOnlyList<string>> SendTargetsAsync(CancellationToken ct)
        {
            using var text = new MemoryStream();
            var ttt = 0xFFFFFFFFu;
            var request = "SendTargets=All\0";
            for (var round = 0; round < 64; round++)
            {
                var bhs = NewBhs(OpTextRequest, FlagFinal, itt: 1);
                BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(20), ttt);
                BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(24), _cmdSn++);
                BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28), _expStatSn);
                await SendAsync(bhs, request, ct);

                var (response, data) = await ReceiveAsync(OpTextResponse, ct);
                _expStatSn = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(24)) + 1;
                text.Write(data);
                if (text.Length > MaxTextBytes)
                {
                    throw new IscsiDiscoveryException("iSCSI SendTargets response is too long");
                }

                if ((response[1] & FlagFinal) != 0)
                {
                    return Encoding.UTF8.GetString(text.ToArray())
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                        .Where(pair => pair.StartsWith("TargetName=", StringComparison.Ordinal))
                        .Select(pair => pair["TargetName=".Length..])
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }

                ttt = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(20));
                request = "";
            }

            throw new IscsiDiscoveryException("iSCSI SendTargets response did not end");
        }

        public async Task LogoutAsync(CancellationToken ct)
        {
            var bhs = NewBhs(Immediate | OpLogoutRequest, FlagFinal, itt: 2); // reason 0: закрыть сессию
            BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(24), _cmdSn);
            BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(28), _expStatSn);
            await SendAsync(bhs, "", ct);
            await ReceiveAsync(OpLogoutResponse, ct);
        }

        private async Task SendAsync(byte[] bhs, string text, CancellationToken ct)
        {
            var data = Encoding.UTF8.GetBytes(text);
            bhs[5] = (byte)(data.Length >> 16);
            bhs[6] = (byte)(data.Length >> 8);
            bhs[7] = (byte)data.Length;
            var pdu = new byte[BhsLength + Padded(data.Length)];
            bhs.CopyTo(pdu, 0);
            data.CopyTo(pdu, BhsLength);
            await stream.WriteAsync(pdu, ct);
        }

        private async Task<(byte[] Bhs, byte[] Data)> ReceiveAsync(byte expected, CancellationToken ct)
        {
            for (var skipped = 0; skipped < 8; skipped++)
            {
                var bhs = new byte[BhsLength];
                await stream.ReadExactlyAsync(bhs, ct);
                var ahs = bhs[4] * 4;
                var length = (bhs[5] << 16) | (bhs[6] << 8) | bhs[7];
                var rest = new byte[ahs + Padded(length)];
                await stream.ReadExactlyAsync(rest, ct);

                var opcode = (byte)(bhs[0] & 0x3F);
                if (opcode == expected)
                {
                    return (bhs, rest.AsSpan(ahs, length).ToArray());
                }

                if (opcode == OpReject)
                {
                    throw new IscsiDiscoveryException($"iSCSI target rejected the request (reason 0x{bhs[2]:X2})");
                }

                if (opcode is not (OpNopIn or OpAsyncMessage))
                {
                    throw new IscsiDiscoveryException($"unexpected iSCSI PDU 0x{opcode:X2} while waiting for 0x{expected:X2}");
                }
            }

            throw new IscsiDiscoveryException($"no iSCSI PDU 0x{expected:X2} from the target");
        }

        private static byte[] NewBhs(int opcode, byte flags, uint itt)
        {
            var bhs = new byte[BhsLength];
            bhs[0] = (byte)opcode;
            bhs[1] = flags;
            BinaryPrimitives.WriteUInt32BigEndian(bhs.AsSpan(16), itt);
            return bhs;
        }

        /// <summary>ISID случайного формата (T=10b): так сессия проверки не совпадёт ни с одной сессией ПК.</summary>
        private static byte[] NewIsid()
        {
            var isid = new byte[6];
            RandomNumberGenerator.Fill(isid.AsSpan(1, 3));
            isid[0] = 0x80;
            return isid;
        }

        private static int Padded(int length) => (length + 3) & ~3;
    }
}
