using System.Net;
using System.Net.Http.Headers;
using Club.Server.Data;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Сервер с двумя библиотеками поверх поддельного TrueNAS, как в клубе: G: — личный слой на games/club/*, мастер-том на
/// букве G:; H: (solo) — на hdd/club/* (<see cref="SoloLibraryTests.SoloSettings"/>). <c>solo: false</c> — тот же сервер
/// без H: (для сравнения провода G:).
/// </summary>
public sealed class SoloStand : IAsyncDisposable
{
    public const string Basename = "iqn.2005-10.org.freenas.ctl";
    public const string PanelToken = "panel-test-token";

    private SoloStand(FakeTrueNas nas, ServerFixture server)
    {
        Nas = nas;
        Server = server;
    }

    public FakeTrueNas Nas { get; }

    public ServerFixture Server { get; }

    public TrueNasStorage Storage => Server.Services.GetRequiredService<TrueNasStorage>();

    public LibrarySet Libraries => Server.Services.GetRequiredService<LibrarySet>();

    public LibrarySlot Games => Libraries.Main;

    public LibrarySlot Solo => Libraries.Find("solo")!;

    public static async Task<SoloStand> StartAsync(bool solo = true, IReadOnlyDictionary<string, string>? settings = null)
    {
        var nas = await FakeTrueNas.StartAsync();
        nas.UniqueChapUsers = true; // CHAP-пользователи двух библиотек не должны совпасть
        foreach (var dataset in new[] { "games/club", "games/club/published", "games/club/seats", "hdd/club", "hdd/club/published", "hdd/club/seats" })
        {
            nas.AddFilesystem(dataset);
        }

        var server = new ServerFixture();
        var options = nas.Options();
        var all = new Dictionary<string, string>
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
            ["Panel:AdminToken"] = PanelToken,
            ["TrueNas:Host"] = options.Host,
            ["TrueNas:Port"] = options.Port.ToString(),
            ["TrueNas:Username"] = options.Username,
            ["TrueNas:ApiKey"] = options.ApiKey,
            ["TrueNas:CaCertificatePath"] = options.CaCertificatePath,
        };
        var soloSettings = solo ? SoloLibraryTests.SoloSettings : new Dictionary<string, string>();
        foreach (var (key, value) in soloSettings.Concat(settings ?? new Dictionary<string, string>()))
        {
            all[key] = value;
        }

        foreach (var (key, value) in all)
        {
            server.Settings[key] = value;
        }

        await server.InitializeAsync();
        var stand = new SoloStand(nas, server);
        await stand.Storage.EnsureZvolAsync("games/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
        if (solo)
        {
            await stand.Storage.EnsureZvolAsync("hdd/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
        }

        return stand;
    }

    public async ValueTask DisposeAsync()
    {
        await ((Xunit.IAsyncLifetime)Server).DisposeAsync();
        await Nas.DisposeAsync();
    }

    public Task PassAsync(LibrarySlot library) => StorageWorker.RunOnceAsync(library, reconcile: true, TimeProvider.System, CancellationToken.None);

    public async Task PublishAsync(LibrarySlot library, string label)
    {
        await library.Publisher.RequestPublishAsync(label, "test");
        await PassAsync(library);
        Assert.Equal("published", (await library.Repository.FindVersionAsync(label))!.State);
    }

    public async Task OpenMasterAsync(LibrarySlot library, TestMachine machine)
    {
        await library.Editor.RequestOpenAsync(machine.MachineId, "test");
        await PassAsync(library);
        Assert.Equal("open", (await library.Master.GetAsync()).State);
    }

    public async Task CloseMasterAsync(LibrarySlot library)
    {
        await library.Editor.RequestCloseAsync(force: false, "test");
        await PassAsync(library);
        Assert.Equal("closed", (await library.Master.GetAsync()).State);
    }

    /// <summary>ПК номер <paramref name="n"/> (по порядку регистрации): отчёт по G: (старый путь) сообщает IQN и версию помощника.</summary>
    public async Task<TestMachine> RegisterAsync(int n, string helperVersion = "1.6.0")
    {
        var (machine, _) = await TestMachine.RegisterAsync(Server.CreateClient(), null, $"02:00:00:00:41:{n:D2}");
        using var response = await GamesStatusAsync(machine, helperVersion, n);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return machine;
    }

    public static string Iqn(int n) => $"iqn.1991-05.com.microsoft:pc-{n:D2}";

    /// <summary>Отчёт помощника по G: (старый путь); <paramref name="master"/> null — поля нет.</summary>
    public static Task<HttpResponseMessage> GamesStatusAsync(TestMachine machine, string helperVersion, int n, object? master = null) =>
        machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion, volume = new { state = "none" }, initiatorIqn = Iqn(n), master,
        });

    /// <summary>Запрос при старте помощника по H: (путь библиотеки).</summary>
    public static Task<HttpResponseMessage> SoloBootAsync(TestMachine machine, bool attached = false, string key = "solo") =>
        machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/libraries/{key}/volume?attached={(attached ? "true" : "false")}");

    /// <summary>Отчёт помощника по H: (путь библиотеки); тело — любое, как у помощника.</summary>
    public static Task<HttpResponseMessage> SoloStatusAsync(TestMachine machine, object body, string key = "solo") =>
        machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/libraries/{key}/status", body);

    public async Task<MachineRow> MachineAsync(TestMachine machine) =>
        (await Server.Services.GetRequiredService<MachineRepository>().FindAsync(machine.MachineId))!;

    public HttpClient Panel()
    {
        var http = Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }
}
