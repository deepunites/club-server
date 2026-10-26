using System.Net;
using System.Text.RegularExpressions;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Server.Tests;

/// <summary>
/// Библиотека G: с появлением второй не изменилась: последовательность вызовов TrueNAS (с параметрами) на публикации,
/// сборке, сбросе и пересборке личного диска, мастер-томе и разборке версий — та же, что записана до второй
/// библиотеки (Golden/g-library-truenas-calls.txt, сервер на 4ea35b1), и с выключенной, и с включённой H:.
/// </summary>
public sealed partial class GamesLibraryCallLogTests
{
    private const string Golden = "g-library-truenas-calls.txt";
    private const string Iqn = "iqn.1991-05.com.microsoft:pc-01";

    /// <summary>Вызовы соединения (вход с ключом API, ping) от сценария не зависят и в эталон не пишутся.</summary>
    private static readonly HashSet<string> ConnectionCalls = ["auth.login_ex", "core.ping", "system.version_short"];

    [GeneratedRegex("\"secret\":\"[^\"]*\"")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("\"[0-9a-f]{32}\"")]
    private static partial Regex OperationIdPattern();

    [Fact]
    public async Task Truenas_calls_of_the_g_library_are_unchanged_without_a_second_library() =>
        Assert.Equal(await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Golden", Golden)), await ScenarioAsync(solo: false));

    [Fact]
    public async Task Truenas_calls_of_the_g_library_are_unchanged_with_the_second_library_on() =>
        Assert.Equal(await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Golden", Golden)), await ScenarioAsync(solo: true));

    private static async Task<IReadOnlyList<string>> ScenarioAsync(bool solo)
    {
        await using var nas = await FakeTrueNas.StartAsync();
        nas.AddFilesystem("games/club");
        nas.AddFilesystem("games/club/published");
        nas.AddFilesystem("games/club/seats");
        var server = new ServerFixture();
        var trueNas = nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:PersonalGames"] = "true",
            ["Library:MasterZvol"] = "games/club/lib",
            ["Library:PublishedParent"] = "games/club/published",
            ["Library:PersonalParent"] = "games/club/seats",
            ["Library:MasterDriveLetter"] = "G",
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["Library:DiscoveryAddress"] = nas.IscsiPortal,
            ["Library:VerifyDelayMs"] = "0",
            ["Library:MaxAttempts"] = "1",
            ["TrueNas:Host"] = trueNas.Host,
            ["TrueNas:Port"] = trueNas.Port.ToString(),
            ["TrueNas:Username"] = trueNas.Username,
            ["TrueNas:ApiKey"] = trueNas.ApiKey,
            ["TrueNas:CaCertificatePath"] = trueNas.CaCertificatePath,
        })
        {
            server.Settings[key] = value;
        }

        if (solo)
        {
            nas.AddFilesystem("hdd/club");
            nas.AddFilesystem("hdd/club/published");
            nas.AddFilesystem("hdd/club/seats");
            foreach (var (key, value) in SoloLibraryTests.SoloSettings)
            {
                server.Settings[key] = value;
            }
        }

        await server.InitializeAsync();
        try
        {
            var storage = server.Services.GetRequiredService<TrueNasStorage>();
            await storage.EnsureZvolAsync("games/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
            if (solo)
            {
                await storage.EnsureZvolAsync("hdd/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
            }

            var start = nas.CallRecords.Count;
            await RunAsync(server);
            return nas.CallRecords.Skip(start)
                .Where(call => !ConnectionCalls.Contains(call[..call.IndexOf(' ')]))
                .Select(call => OperationIdPattern().Replace(SecretPattern().Replace(call, "\"secret\":\"*\""), "\"*\""))
                .ToList();
        }
        finally
        {
            await ((IAsyncLifetime)server).DisposeAsync();
        }
    }

    /// <summary>Проход воркера хранилища, как в StorageWorker.ExecuteAsync: G:, затем остальные библиотеки.</summary>
    private static async Task PassAsync(ServerFixture server)
    {
        await StorageWorker.RunOnceAsync(
            server.Services.GetRequiredService<LibraryPublisher>(), server.Services.GetRequiredService<LibraryRepository>(), reconcile: true, TimeProvider.System, CancellationToken.None);
        await StorageWorker.RunExtrasAsync(server.Services.GetRequiredService<LibrarySet>(), reconcile: true, TimeProvider.System, NullLogger.Instance, CancellationToken.None);
    }

    private static async Task RunAsync(ServerFixture server)
    {
        var publisher = server.Services.GetRequiredService<LibraryPublisher>();
        var editor = server.Services.GetRequiredService<MasterEditor>();
        var (pc, _) = await TestMachine.RegisterAsync(server.CreateClient());

        async Task StatusAsync()
        {
            using var response = await pc.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{pc.MachineId}/status", new
            {
                helperVersion = "1.5.1", volume = new { state = "none" }, initiatorIqn = Iqn,
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        async Task<HttpStatusCode> BootAsync()
        {
            using var response = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=false");
            return response.StatusCode;
        }

        async Task PublishAsync(string label)
        {
            await publisher.RequestPublishAsync(label, "test");
            await PassAsync(server);
        }

        await StatusAsync();
        await PublishAsync("v1");
        Assert.Equal(HttpStatusCode.OK, await BootAsync()); // сборка личного диска
        await StatusAsync();
        Assert.Equal(HttpStatusCode.OK, await BootAsync()); // сброс к @clean
        await PublishAsync("v2");
        Assert.Equal(HttpStatusCode.OK, await BootAsync()); // пересборка на v2

        await editor.RequestOpenAsync(pc.MachineId, "test");
        await PassAsync(server);
        await StatusAsync();
        Assert.Equal(HttpStatusCode.NoContent, await BootAsync()); // буква G: у мастер-тома
        await editor.RequestCloseAsync(force: false, "test");
        await PassAsync(server);

        await PublishAsync("v3"); // v1 разбирается
        Assert.Equal(HttpStatusCode.OK, await BootAsync());
        await PublishAsync("v4");
        await PublishAsync("v5"); // из снапшота v3 сделан диск места: удаление с defer
        Assert.Equal(HttpStatusCode.OK, await BootAsync()); // пересборка на v5 освобождает снапшот v3
        await PassAsync(server);
    }
}
