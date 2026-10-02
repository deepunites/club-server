using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Club.TestSupport;

/// <summary>
/// Получатель списка ПК (MachineFeed) — как касса шелла: настоящий Kestrel, запоминает запросы с сырыми байтами тела,
/// отвечает заданным кодом или редиректом. HTTPS — с сертификатом тестового CA (<see cref="CaPath"/>).
/// </summary>
public sealed class FakeFeedReceiver : IAsyncDisposable
{
    public sealed record Received(string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Received> _requests = new();

    private FakeFeedReceiver(WebApplication app, string? caPath)
    {
        _app = app;
        CaPath = caPath;
    }

    public IReadOnlyList<Received> Requests => _requests.ToArray();

    public int StatusCode { get; set; } = StatusCodes.Status204NoContent;

    /// <summary>Ответить 307 на этот адрес вместо <see cref="StatusCode"/>.</summary>
    public string? RedirectTo { get; set; }

    public string BaseUrl { get; private set; } = "";

    public string? CaPath { get; }

    public static async Task<FakeFeedReceiver> StartAsync(bool https = false)
    {
        string? caPath = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        if (https)
        {
            var (ca, server) = FakeTrueNas.CreateCertificates();
            caPath = Path.Combine(Path.GetTempPath(), $"fake-feed-ca-{Guid.NewGuid():N}.pem");
            await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem());
            certificate = server;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l =>
        {
            if (certificate is not null)
            {
                l.UseHttps(certificate);
            }
        }));
        var app = builder.Build();
        var receiver = new FakeFeedReceiver(app, caPath);
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            receiver._requests.Enqueue(new Received(context.Request.Path, headers, body.ToArray()));
            if (receiver.RedirectTo is { } location)
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = location;
                return;
            }

            context.Response.StatusCode = receiver.StatusCode;
        });
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        receiver.BaseUrl = https ? $"https://localhost:{port}" : $"http://127.0.0.1:{port}";
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (CaPath is not null)
        {
            File.Delete(CaPath);
        }
    }
}
