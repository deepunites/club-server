using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Club.TrueNas;

/// <summary>
/// Одно WebSocket-соединение JSON-RPC 2.0 с middleware TrueNAS по закреплённой версии API
/// (<c>wss://host/api/vNN.NN.N</c>). Вызовы мультиплексируются по строковому id, <c>params</c> — всегда массив.
/// </summary>
public sealed class TrueNasConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly TrueNasOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _calls;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _receive = Task.CompletedTask;
    private Task _ping = Task.CompletedTask;

    private TrueNasConnection(ClientWebSocket socket, TrueNasOptions options, string apiVersion, ILogger logger)
    {
        _socket = socket;
        _options = options;
        _logger = logger;
        ApiVersion = apiVersion;
        _calls = new SemaphoreSlim(Math.Clamp(options.MaxConcurrentCalls, 1, 10));
    }

    public string ApiVersion { get; }

    public bool IsOpen => _socket.State == WebSocketState.Open && !_lifetime.IsCancellationRequested;

    /// <summary>Подключается к <c>wss://host/api/{apiVersion}</c> и входит по API-ключу (<c>auth.login_ex</c>, API_KEY_PLAIN).</summary>
    public static async Task<TrueNasConnection> OpenAsync(TrueNasOptions options, string apiVersion, ILogger logger, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = TrueNasTls.Validator(options.CaCertificatePath);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        var uri = new UriBuilder("wss", options.Host, options.Port, $"/api/{apiVersion}").Uri;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            socket.Dispose();
            throw new TrueNasUnavailableException($"Cannot connect to {uri}", ex);
        }

        var connection = new TrueNasConnection(socket, options, apiVersion, logger);
        connection._receive = connection.ReceiveLoopAsync();
        try
        {
            await connection.LoginAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        connection._ping = connection.PingLoopAsync();
        return connection;
    }

    /// <summary>Вызов метода; возвращает <c>result</c>. Ошибка JSON-RPC — <see cref="TrueNasRpcException"/>.</summary>
    public async Task<JsonElement> CallAsync(string method, IReadOnlyList<object?> args, CancellationToken cancellationToken)
    {
        await _calls.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = JsonSerializer.SerializeToNode(args, TrueNasJson.Options),
            };
            await SendAsync(request, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.CallTimeoutSec));
            try
            {
                var response = await completion.Task.WaitAsync(timeout.Token);
                if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    throw TrueNasRpcException.From(method, error);
                }

                return response.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TrueNasUnavailableException(_lifetime.IsCancellationRequested ? $"{method}: connection closed" : $"{method}: timed out");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _calls.Release();
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("auth.login_ex", [new
        {
            mechanism = "API_KEY_PLAIN",
            username = _options.Username,
            api_key = _options.ApiKey,
            login_options = new { user_info = false },
        }], cancellationToken);

        // Неверный ключ не бросает исключение: смотрим response_type (EXPIRED — в т.ч. ключ отозван).
        var responseType = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("response_type", out var rt) ? rt.GetString() : null;
        if (responseType != "SUCCESS")
        {
            throw new TrueNasUnavailableException($"TrueNAS login rejected: {responseType ?? "unknown response"}");
        }
    }

    private async Task PingLoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PingIntervalSec));
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(interval, _lifetime.Token);
                await CallAsync("core.ping", [], _lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TrueNasUnavailableException or WebSocketException or TrueNasRpcException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "TrueNAS keepalive failed; closing connection");
                await _lifetime.CancelAsync();
            }
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, TrueNasJson.Options);
        await _send.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            await _lifetime.CancelAsync();
            throw new TrueNasUnavailableException("TrueNAS connection lost while sending", ex);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                buffer.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.GetMemory(16 * 1024), _lifetime.Token);
                    buffer.Advance(result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("TrueNAS closed the connection: {Status} {Description}", _socket.CloseStatus, _socket.CloseStatusDescription);
                    break;
                }

                using var document = JsonDocument.Parse(buffer.WrittenMemory);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && _pending.TryGetValue(id.GetString()!, out var waiter))
                {
                    waiter.TrySetResult(root.Clone());
                }

                // Уведомления (collection_update, notify_unsubscribed) пока не используются: состояние всегда перечитывается запросами.
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or ObjectDisposedException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "TrueNAS connection receive loop stopped");
            }
        }
        finally
        {
            await _lifetime.CancelAsync();
            foreach (var waiter in _pending.Values)
            {
                waiter.TrySetCanceled();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(Quiet(_receive), Quiet(_ping));
        _socket.Dispose();
        _lifetime.Dispose();
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }
}

public static class TrueNasJson
{
    /// <summary>Имена полей middleware — snake_case, задаём их явно в анонимных объектах; null не пишем.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
