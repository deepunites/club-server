using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Club.Server.Agents;
using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;

namespace Club.Server.Realtime;

/// <summary>Кадр WebSocket <c>clubshell.v1</c> (схема <c>WsFrame</c>). <c>payload</c> обязателен, поэтому null пишется явно.</summary>
public sealed record WsFrame(string Type, Guid Id, DateTimeOffset Ts, string? Name, JsonElement Payload, WsAck? Ack, Guid? Supersedes, DateTimeOffset? ExpiresAt);

public sealed record WsAck(Guid Id, bool Ok, JsonElement? Error, JsonElement? Result);

public sealed class RealtimeOptions
{
    public int PingIntervalSec { get; set; } = 20;
    public int PongTimeoutSec { get; set; } = 10;
    public int MaxFrameBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// <c>wss://…/ws/agent</c>: токен в <c>Authorization: Bearer</c> (основной способ) или <c>?token=</c> (агент шлёт оба),
/// сабпротокол <c>clubshell.v1</c>, ping сервера каждые 20 с, pong в течение 10 с. Команды доставляются at-least-once:
/// при подключении — все неподтверждённые, дальше — по мере постановки. По истечении токена сокет закрывается кодом
/// 4401, агент обновляет токен и переподключается.
/// </summary>
public sealed class AgentSocketHub(AgentAuthenticator authenticator, CommandRepository commands, RealtimeOptions options, TimeProvider clock, ILogger<AgentSocketHub> logger)
{
    public const string Subprotocol = "clubshell.v1";
    private static readonly JsonElement NullPayload = JsonDocument.Parse("null").RootElement.Clone();
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public bool IsConnected(Guid pcId) => _connections.ContainsKey(pcId);

    /// <summary>Отправка команды, если ПК на связи. Команда уже лежит в очереди, так что неудача не теряет её.</summary>
    public async Task<bool> TrySendCommandAsync(Guid pcId, ServerCommandEnvelope command)
    {
        if (!_connections.TryGetValue(pcId, out var connection))
        {
            return false;
        }

        try
        {
            await connection.SendAsync(CommandFrame(command), CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "WebSocket upgrade required", new { reason = "notWebSocket" });
        }

        // До upgrade: неверный токен — HTTP 401, агент на него делает refresh (RealtimeClient.cs:121-123).
        var token = AgentAuthenticator.BearerToken(context) ?? context.Request.Query["token"].ToString();
        var agent = await authenticator.AuthenticateAsync(token);

        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(Subprotocol))
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync();
            await rejected.CloseAsync((WebSocketCloseStatus)4426, "subprotocol clubshell.v1 required", CancellationToken.None);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = Subprotocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        var connection = new Connection(socket);
        if (_connections.TryGetValue(agent.Pc.Id, out var previous))
        {
            await previous.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, "replaced by a new connection");
        }

        _connections[agent.Pc.Id] = connection;
        logger.LogInformation("Agent {PcId} connected over WebSocket", agent.Pc.Id);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            foreach (var command in await commands.PendingAsync(agent.Pc.Id, clock.GetUtcNow()))
            {
                await connection.SendAsync(CommandFrame(command), lifetime.Token);
            }

            var receive = ReceiveLoopAsync(agent, connection, lifetime.Token);
            var keepAlive = KeepAliveLoopAsync(agent, connection, lifetime.Token);
            await Task.WhenAny(receive, keepAlive);
            await lifetime.CancelAsync();
            await Task.WhenAll(Swallow(receive), Swallow(keepAlive));
        }
        finally
        {
            _connections.TryRemove(new KeyValuePair<Guid, Connection>(agent.Pc.Id, connection));
            logger.LogInformation("Agent {PcId} disconnected", agent.Pc.Id);
        }
    }

    private async Task ReceiveLoopAsync(AgentContext agent, Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(buffer.GetMemory(8192), cancellationToken);
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > options.MaxFrameBytes)
                {
                    await connection.CloseQuietlyAsync(WebSocketCloseStatus.MessageTooBig, "frame larger than 1 MiB");
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, "bye");
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            WsFrame? frame;
            try
            {
                frame = JsonSerializer.Deserialize<WsFrame>(buffer.WrittenSpan, ApiJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Malformed WS frame from {PcId}", agent.Pc.Id);
                continue;
            }

            if (frame is not null)
            {
                await HandleFrameAsync(agent, connection, frame, cancellationToken);
            }
        }
    }

    private async Task HandleFrameAsync(AgentContext agent, Connection connection, WsFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case "pong":
                connection.LastPong = clock.GetUtcNow();
                break;
            case "ping":
                await connection.SendAsync(new WsFrame("pong", Guid.NewGuid(), clock.GetUtcNow(), null, NullPayload, null, null, null), cancellationToken);
                break;
            case "ack" when frame.Ack is { } ack:
                await commands.AckAsync(agent.Pc.Id, ack.Id, new CommandAck(ack.Ok, ack.Error, ack.Result), clock.GetUtcNow());
                break;
            case "event":
                // События агента пока только журналируются; хранение (античит, железо) — отдельная задача.
                logger.LogInformation("Agent {PcId} event {Name}", agent.Pc.Id, frame.Name);
                break;
        }
    }

    private async Task KeepAliveLoopAsync(AgentContext agent, Connection connection, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(options.PingIntervalSec);
        var pongTimeout = TimeSpan.FromSeconds(options.PongTimeoutSec);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            if (now >= agent.Principal.ExpiresAt)
            {
                await connection.CloseQuietlyAsync((WebSocketCloseStatus)4401, "token expired");
                return;
            }

            var sentAt = now;
            await connection.SendAsync(new WsFrame("ping", Guid.NewGuid(), now, null, NullPayload, null, null, null), cancellationToken);
            await Task.Delay(pongTimeout, clock, cancellationToken);
            if (connection.LastPong < sentAt)
            {
                await connection.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "pong timeout");
                return;
            }

            var untilNext = interval - pongTimeout;
            var untilExpiry = agent.Principal.ExpiresAt - clock.GetUtcNow();
            var delay = untilExpiry < untilNext ? untilExpiry : untilNext;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, cancellationToken);
            }
        }
    }

    private static WsFrame CommandFrame(ServerCommandEnvelope command) =>
        new("command", command.Id, command.Ts, command.Name, command.Payload ?? NullPayload, null, command.Supersedes, command.ExpiresAt);

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private sealed class Connection(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public WebSocket Socket { get; } = socket;

        public DateTimeOffset LastPong { get; set; } = DateTimeOffset.MinValue;

        public async Task SendAsync(WsFrame frame, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ApiJson.Options);
            await _send.WaitAsync(cancellationToken);
            try
            {
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }

        public async Task CloseQuietlyAsync(WebSocketCloseStatus status, string description)
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(status, description, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
            }
        }
    }
}
