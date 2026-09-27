using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Api;
using Json.Schema;
using Club.TestSupport;

namespace Club.Server.Tests;

/// <summary>Зарегистрированная машина — как помощник бездиска на ПК.</summary>
public sealed class TestMachine(HttpClient http, Guid machineId, string hwid, string accessToken, string refreshToken)
{
    public Guid MachineId { get; } = machineId;
    public string Hwid { get; } = hwid;
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;

    public static object RegisterBody(string hwid, params string[] macs) => new
    {
        hwid,
        hostname = "PC-TEST",
        macAddresses = macs.Length > 0 ? macs : ["aa:bb:cc:dd:ee:ff"],
        helperVersion = "1.0.0",
        osVersion = "Windows 11 Pro 24H2",
    };

    public static async Task<(TestMachine Machine, JsonElement Response)> RegisterAsync(HttpClient http, string? hwid = null)
    {
        hwid ??= Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/diskless/v1/machines/register") { Content = JsonContent.Create(RegisterBody(hwid)) };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return (new TestMachine(http, body.GetProperty("machineId").GetGuid(), hwid, body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!), body);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return http.SendAsync(request);
    }

    public static object Status(string state = "none", string? iqn = null, string? version = null, bool? readOnly = null) => new
    {
        helperVersion = "1.0.0",
        bootTime = DateTimeOffset.UtcNow.AddMinutes(-5),
        volume = new { state, targetIqn = iqn, libraryVersion = version, driveLetter = iqn is null ? null : "G", readOnlyVerified = readOnly },
    };
}

/// <summary>Проверка тела ответа по схеме из спецификации API бездиска (docs/diskless-api.json).</summary>
public static class Contract
{
    private static readonly Uri BaseUri = new("https://club.local/diskless-api.json");

    // Расширения и служебные ключи OpenAPI — не ключевые слова JSON Schema.
    private static readonly Dialect Dialect = Dialect.Draft202012.With([], allowUnknownKeywords: true);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonSchema> Schemas = new();

    private static readonly Lazy<SchemaRegistry> Registry = new(() =>
    {
        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spec", "diskless-api.json"))).RootElement.Clone();
        var registry = new SchemaRegistry();
        registry.Register(BaseUri, new JsonElementBaseDocument(document, BaseUri));
        return registry;
    });

    public static void AssertMatches(string schemaName, JsonElement instance)
    {
        var schema = Schemas.GetOrAdd(schemaName, name =>
        {
            var options = new BuildOptions { SchemaRegistry = Registry.Value, Dialect = Dialect };
            var reference = JsonDocument.Parse($$"""{ "$ref": "{{BaseUri}}#/components/schemas/{{name}}" }""").RootElement;
            return JsonSchema.Build(reference, options, new Uri($"https://club.local/check/{name}"));
        });
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}");
            Assert.Fail($"{schemaName} does not match spec:\n{string.Join("\n", errors)}\n{instance}");
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
        AssertMatches("ErrorEnvelope", body);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        }
    }
}
