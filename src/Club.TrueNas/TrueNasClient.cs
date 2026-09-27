using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Club.TrueNas;

/// <summary>Сведения о подключённом TrueNAS: фактическая версия API и релиза (хранить в БД для диагностики Q7).</summary>
public sealed record TrueNasInfo(string ApiVersion, string ReleaseVersion);

/// <summary>
/// Долгоживущий клиент: выбирает версию API из белого списка, держит одно соединение и переподключается при обрыве
/// (с повторным логином). Поддерживается только линия 25.10+: на 25.04 клон снапшота требует FULL_ADMIN, а в 26 нет
/// <c>zfs.snapshot</c> (решение владельца 2026-09-27).
/// </summary>
public sealed class TrueNasClient(TrueNasOptions options, ILogger<TrueNasClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connect = new(1, 1);
    private TrueNasConnection? _connection;
    private TrueNasInfo? _info;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private int _failures;

    public TrueNasInfo? Info => _info;

    public async Task<JsonElement> CallAsync(string method, IReadOnlyList<object?> args, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectedAsync(cancellationToken);
        return await connection.CallAsync(method, args, cancellationToken);
    }

    public async Task<TrueNasInfo> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await ConnectedAsync(cancellationToken);
        return _info!;
    }

    private async Task<TrueNasConnection> ConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _connect.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } reopened)
            {
                return reopened;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            // Экспоненциальная пауза между попытками: rate limit TrueNAS — 20 логинов в минуту с IP.
            if (DateTimeOffset.UtcNow < _nextAttempt)
            {
                throw new TrueNasUnavailableException($"TrueNAS reconnect backoff until {_nextAttempt:O}");
            }

            try
            {
                var version = await SelectApiVersionAsync(cancellationToken);
                var connection = await TrueNasConnection.OpenAsync(options, version, logger, cancellationToken);
                var release = (await connection.CallAsync("system.version_short", [], cancellationToken)).GetString() ?? "";
                if (!IsSupportedRelease(release))
                {
                    await connection.DisposeAsync();
                    throw new TrueNasUnavailableException($"TrueNAS {release} is not supported: 25.10 or newer is required");
                }

                _connection = connection;
                _info = new TrueNasInfo(version, release);
                _failures = 0;
                logger.LogInformation("Connected to TrueNAS {Release} via API {ApiVersion}", release, version);
                return connection;
            }
            catch (Exception ex) when (ex is TrueNasUnavailableException or TrueNasRpcException or HttpRequestException or JsonException)
            {
                _failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(_failures, 9))));
                _nextAttempt = DateTimeOffset.UtcNow + delay;
                throw ex as TrueNasUnavailableException ?? new TrueNasUnavailableException($"TrueNAS connection failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _connect.Release();
        }
    }

    /// <summary>Максимальная версия из /api/versions, входящая в белый список. /api/current не используется.</summary>
    private async Task<string> SelectApiVersionAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = TrueNasTls.Validator(options.CaCertificatePath);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var uri = new UriBuilder("https", options.Host, options.Port, "/api/versions").Uri;
        var available = await http.GetFromJsonAsync<List<string>>(uri, cancellationToken) ?? [];
        var chosen = available
            .Where(v => options.AllowedApiVersions.Contains(v, StringComparer.Ordinal))
            .OrderByDescending(ParseVersion)
            .FirstOrDefault();
        return chosen ?? throw new TrueNasUnavailableException(
            $"No allowed TrueNAS API version: server offers [{string.Join(", ", available)}], allowed [{string.Join(", ", options.AllowedApiVersions)}]");
    }

    internal static Version ParseVersion(string apiVersion) =>
        Version.TryParse(apiVersion.TrimStart('v'), out var v) ? v : new Version(0, 0);

    internal static bool IsSupportedRelease(string release)
    {
        var parts = release.Split('.', '-');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            && (major > 25 || (major == 25 && minor >= 10));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connect.Dispose();
    }
}
