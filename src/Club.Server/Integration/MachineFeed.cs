using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Club.Server.Api;
using Club.Server.Data;

namespace Club.Server.Integration;

/// <summary>
/// Отправка списка ПК внешней системе (шеллу, кассе) — секция <c>MachineFeed</c>. Пустой <see cref="Url"/> — выключено.
/// Сервер сам отправляет снимок наружу: получатель может быть в облаке, а сервер клуба — за NAT.
/// </summary>
public sealed class MachineFeedOptions
{
    /// <summary>Куда POST-ить снимок (http/https). Пусто — отправка выключена.</summary>
    public string Url { get; set; } = "";

    /// <summary>Общий секрет подписи HMAC-SHA256 (не короче <see cref="MinSecretLength"/> символов).</summary>
    public string Secret { get; set; } = "";

    /// <summary>PEM корня, которым проверять HTTPS получателя вместо системного хранилища (свой CA клуба).</summary>
    public string CaCertificatePath { get; set; } = "";

    /// <summary>Как часто сверять список с отправленным: изменения уходят не позже чем через этот интервал.</summary>
    public int IntervalSec { get; set; } = 10;

    /// <summary>Повтор неизменного снимка: получатель, потерявший данные, восстановится сам.</summary>
    public int ResendMinutes { get; set; } = 10;

    public bool RunWorker { get; set; } = true;

    public const int MinSecretLength = 32;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Ошибки настройки — при старте сервера, а не при первой отправке.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("MachineFeed:Url must be an absolute http(s) URL");
        }

        if (Secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException($"MachineFeed:Secret must be at least {MinSecretLength} characters (openssl rand -hex 32)");
        }

        if (!string.IsNullOrWhiteSpace(CaCertificatePath) && !File.Exists(CaCertificatePath))
        {
            throw new InvalidOperationException($"MachineFeed:CaCertificatePath not found: '{CaCertificatePath}'");
        }

        if (IntervalSec < 1 || ResendMinutes < 1)
        {
            throw new InvalidOperationException("MachineFeed:IntervalSec and MachineFeed:ResendMinutes must be positive");
        }
    }
}

/// <summary>ПК в снимке: только одобренные машины. MAC — нижний регистр с двоеточиями.</summary>
public sealed record MachineFeedEntry(Guid Id, int Number, string Name, string Hostname, IReadOnlyList<string> MacAddresses);

/// <summary>
/// Полный список ПК на момент <see cref="GeneratedAt"/>. ПК, которого нет в снимке, удалён или ещё не одобрен.
/// <see cref="ContentHash"/> — sha256 списка машин: одинаковый у одинаковых списков.
/// </summary>
public sealed record MachinesSnapshot(string Schema, DateTimeOffset GeneratedAt, string ContentHash, IReadOnlyList<MachineFeedEntry> Machines)
{
    public const string SchemaId = "club-server.machines/1";
}

/// <summary>Состояние отправки для панели (в памяти процесса).</summary>
public sealed class MachineFeedState
{
    private readonly object _lock = new();

    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastDeliveredAt { get; private set; }
    public string? LastError { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int Machines { get; private set; }
    internal string? DeliveredHash { get; private set; }
    internal DateTimeOffset NextAttemptAt { get; private set; }

    internal void Delivered(DateTimeOffset now, string hash, int machines)
    {
        lock (_lock)
        {
            LastAttemptAt = LastDeliveredAt = now;
            DeliveredHash = hash;
            Machines = machines;
            LastError = null;
            ConsecutiveFailures = 0;
            NextAttemptAt = now;
        }
    }

    internal void Failed(DateTimeOffset now, string error)
    {
        lock (_lock)
        {
            LastAttemptAt = now;
            LastError = error;
            ConsecutiveFailures++;
            NextAttemptAt = now + MachineFeed.Backoff(ConsecutiveFailures);
        }
    }
}

/// <summary>
/// Снимок реестра ПК → POST на <see cref="MachineFeedOptions.Url"/> с подписью. Отправляет при изменении списка и раз в
/// <see cref="MachineFeedOptions.ResendMinutes"/>; при отказе — повтор с нарастающей паузой (10 с … 5 мин).
/// Подпись: <c>X-Club-Signature: v1=hex(HMAC-SHA256(secret, "{X-Club-Timestamp}.{тело}"))</c>.
/// </summary>
public sealed class MachineFeed(MachineFeedOptions options, MachineFeedState state, MachineRepository machines, TimeProvider clock, ILogger<MachineFeed> logger)
{
    public const string TimestampHeader = "X-Club-Timestamp";
    public const string SignatureHeader = "X-Club-Signature";
    public const string EventHeader = "X-Club-Event";
    public const string EventName = "machines.snapshot";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly string UserAgent = "club-server/" + (typeof(MachineFeed).Assembly.GetName().Version?.ToString(3) ?? "0");

    private readonly Lazy<HttpClient> _http = new(() => CreateClient(options));

    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 10 * Math.Pow(2, Math.Min(failures, 16) - 1)));

    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    public async Task<MachinesSnapshot> BuildSnapshotAsync()
    {
        var entries = (await machines.AllAsync())
            .Where(m => m.Approved)
            .OrderBy(m => m.Number).ThenBy(m => m.Id)
            .Select(m => new MachineFeedEntry(m.Id, m.Number, m.Name, m.Hostname, m.MacAddresses.Order(StringComparer.Ordinal).ToList()))
            .ToList();
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries, ApiJson.Options)));
        return new MachinesSnapshot(MachinesSnapshot.SchemaId, clock.GetUtcNow(), hash, entries);
    }

    /// <summary>Один проход: отправить, если список изменился, пора повторить или прошлая попытка не удалась.</summary>
    /// <returns><see langword="true"/>, если была попытка отправки.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!options.Enabled || now < state.NextAttemptAt)
        {
            return false;
        }

        var snapshot = await BuildSnapshotAsync();
        var fresh = state.LastDeliveredAt is { } delivered && now - delivered < TimeSpan.FromMinutes(options.ResendMinutes);
        if (snapshot.ContentHash == state.DeliveredHash && fresh && state.LastError is null)
        {
            return false;
        }

        try
        {
            await SendAsync(snapshot, ct);
            if (state.LastError is not null)
            {
                logger.LogInformation("Machine feed delivered again after {Failures} failed attempts", state.ConsecutiveFailures);
            }

            state.Delivered(now, snapshot.ContentHash, snapshot.Machines.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MachineFeedRejectedException && !ct.IsCancellationRequested)
        {
            state.Failed(now, Describe(ex));
            logger.LogWarning("Machine feed to {Host} failed ({Failures} in a row): {Error}", Host(options.Url), state.ConsecutiveFailures, state.LastError);
        }

        return true;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException { InnerException: { } inner } => $"{ex.Message} {inner.Message}",
        _ => ex.Message,
    };

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "";

    private async Task SendAsync(MachinesSnapshot snapshot, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(snapshot, ApiJson.Options);
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url.Trim()) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add(TimestampHeader, timestamp);
        request.Headers.Add(SignatureHeader, Sign(options.Secret, timestamp, body));
        request.Headers.Add(EventHeader, EventName);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Редиректы не выполняются: POST со списком уходит только на заданный адрес.
            throw new MachineFeedRejectedException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }
    }

    private static HttpClient CreateClient(MachineFeedOptions options)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(options.CaCertificatePath))
        {
            handler.SslOptions.RemoteCertificateValidationCallback = CustomRoot(options.CaCertificatePath);
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static RemoteCertificateValidationCallback CustomRoot(string caCertificatePath)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caCertificatePath);
        return (_, certificate, _, errors) =>
        {
            // Имя хоста проверяет платформа; неизвестный корень ожидаем и проверяем сами по заданному CA.
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        };
    }
}

public sealed class MachineFeedRejectedException(string message) : Exception(message);

public sealed class MachineFeedWorker(MachineFeed feed, MachineFeedOptions options, TimeProvider clock, ILogger<MachineFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await feed.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Machine feed pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSec), clock, stoppingToken);
        }
    }
}
