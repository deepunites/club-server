using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Club.Server.Api;
using Json.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>Сервер в памяти поверх отдельной временной базы в локальном PostgreSQL.</summary>
public class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Дополнительные настройки (например, модуль библиотеки поверх поддельного TrueNAS).</summary>
    public Dictionary<string, string> Settings { get; } = new();

    public const string ClubKey = "test-club-key";
    private const string AdminConnection = "Host=/var/run/postgresql;Database=postgres";
    private readonly string _database = "club_test_" + Guid.NewGuid().ToString("N");
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"club-test-{Guid.NewGuid():N}.pem");

    public virtual async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {_database}", connection);
        await command.ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        File.Delete(_keyPath);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Club", $"Host=/var/run/postgresql;Database={_database}");
        builder.UseSetting("Auth:ClubApiKey", ClubKey);
        builder.UseSetting("Auth:AutoApprovePcs", "true");
        builder.UseSetting("Auth:SigningKeyPath", _keyPath);
        builder.UseSetting("Realtime:PingIntervalSec", "2");
        builder.UseSetting("Realtime:PongTimeoutSec", "1");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>Зарегистрированный агент: подписывает запросы так же, как club-shell (ServerClient.Authorize).</summary>
public sealed class TestAgent(HttpClient http, Guid pcId, string accessToken, string refreshToken, byte[] secret)
{
    public Guid PcId { get; } = pcId;
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public HttpClient Http { get; } = http;

    public static object RegisterBody(string hwid) => new
    {
        hwid,
        machineName = "PC-TEST",
        agentVersion = "1.4.2",
        hardware = new { cpu = new { name = "Test CPU" } },
        ipAddress = "10.0.1.12",
        macAddress = "aa:bb:cc:dd:ee:ff",
    };

    public static async Task<(TestAgent Agent, JsonElement Response)> RegisterAsync(HttpClient http, string? hwid = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/register") { Content = JsonContent.Create(RegisterBody(hwid ?? Guid.NewGuid().ToString("N"))) };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return (new TestAgent(
            http,
            body.GetProperty("pcId").GetGuid(),
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            Convert.FromBase64String(body.GetProperty("signingSecret").GetString()!)), body);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, long? timestamp = null, string? etag = null) =>
        Http.SendAsync(Signed(method, path, body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body), timestamp, etag));

    public HttpRequestMessage Signed(HttpMethod method, string path, byte[]? body, long? timestamp = null, string? etag = null)
    {
        var ts = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body ?? []));
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(ts + method.Method + path + bodyHash)));
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        request.Headers.Add("X-Timestamp", ts);
        request.Headers.Add("X-Signature", signature);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        return request;
    }

    public static object Heartbeat() => new
    {
        status = "free",
        agentVersion = "1.4.2",
        shellVersion = "1.4.2",
        uptimeSec = 8123,
        ipAddress = "10.0.1.12",
        policyVersion = 0,
        runningGames = Array.Empty<object>(),
        offlineQueue = 0,
        shellConnected = true,
    };
}

/// <summary>Проверка тела ответа по схеме из собранного контракта (contracts/openapi.json).</summary>
public static class Contract
{
    private static readonly Uri BaseUri = new("https://club.local/openapi.json");
    private static readonly Lazy<(SchemaRegistry Registry, JsonElement Document)> Loaded = new(() =>
    {
        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "openapi.json"))).RootElement.Clone();
        var registry = new SchemaRegistry();
        registry.Register(BaseUri, new JsonElementBaseDocument(document, BaseUri));
        return (registry, document);
    });

    public static JsonElement Document => Loaded.Value.Document;

    // Расширения контракта (x-csharp, x-source, discriminator и т. п.) — не ключевые слова JSON Schema.
    private static readonly Dialect Dialect = Dialect.Draft202012.With([], allowUnknownKeywords: true);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonSchema> Schemas = new();

    public static void AssertMatches(string schemaName, JsonElement instance)
    {
        var schema = Schemas.GetOrAdd(schemaName, name =>
        {
            var options = new BuildOptions { SchemaRegistry = Loaded.Value.Registry, Dialect = Dialect };
            var reference = JsonDocument.Parse($$"""{ "$ref": "{{BaseUri}}#/components/schemas/{{name}}" }""").RootElement;
            return JsonSchema.Build(reference, options, new Uri($"https://club.local/check/{name}"));
        });
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}");
            Assert.Fail($"{schemaName} does not match contract:\n{string.Join("\n", errors)}\n{instance}");
        }
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string schemaName)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertMatches(schemaName, body);
        return body;
    }

    public static void AssertError(JsonElement body, string code, string? reason = null)
    {
        AssertMatches("ServerErrorEnvelope", body);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        }
    }

    public static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());

    public static string Format(DateTimeOffset value) => ApiJson.FormatTime(value);
}
