using Club.Helper.Core;
using Club.Server.Data;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Helper.Tests;

/// <summary>
/// Сервер клуба с двумя библиотеками поверх поддельного TrueNAS, как в клубе: G: — личный слой на <c>games/club/*</c>,
/// мастер-том на букве G:; H: (solo) — на <c>hdd/club/*</c>, мастер-том на H:. <c>solo: false</c> — тот же сервер без H:
/// (сервер до 1.6.0 или с <c>Libraries__solo__Enabled=false</c>).
/// </summary>
public sealed class LibrariesStand : IAsyncDisposable
{
    public const string Basename = "iqn.2005-10.org.freenas.ctl";
    public const string GamesSeat = $"{Basename}:games-seat-01";
    public const string SoloSeat = $"{Basename}:solo-seat-01";
    public const string GamesMaster = $"{Basename}:club-master";
    public const string SoloMaster = $"{Basename}:club-master-solo";

    private LibrariesStand(FakeTrueNas nas, ServerFixture server)
    {
        Nas = nas;
        Server = server;
    }

    public FakeTrueNas Nas { get; }

    public ServerFixture Server { get; }

    public InMemoryCredentialStore Credentials { get; } = new();

    public FakeIdentity Identity { get; } = new(Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, InMemoryAssignmentCache> _caches = [];

    /// <summary>assignment.json библиотеки (<c>null</c> — G:) на диске ПК: переживает перезапуск помощника и перезагрузку.</summary>
    public InMemoryAssignmentCache Cache(string? library)
    {
        lock (_caches)
        {
            var key = library ?? "games";
            return _caches.TryGetValue(key, out var cache) ? cache : _caches[key] = new InMemoryAssignmentCache();
        }
    }

    public LibrarySet Libraries => Server.Services.GetRequiredService<LibrarySet>();

    public LibrarySlot Games => Libraries.Main;

    public LibrarySlot Solo => Libraries.Find("solo")!;

    public static async Task<LibrariesStand> StartAsync(bool solo = true)
    {
        var nas = await FakeTrueNas.StartAsync();
        foreach (var dataset in new[] { "games/club", "games/club/published", "games/club/seats", "hdd/club", "hdd/club/published", "hdd/club/seats" })
        {
            nas.AddFilesystem(dataset);
        }

        var server = new ServerFixture();
        var options = nas.Options();
        var settings = new Dictionary<string, string>
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
            ["Libraries:solo:Enabled"] = solo ? "true" : "false",
            ["Libraries:solo:MasterZvol"] = "hdd/club/lib",
            ["Libraries:solo:PersonalParent"] = "hdd/club/seats",
            ["Libraries:solo:PublishedParent"] = "hdd/club/published",
            ["Libraries:solo:DriveLetter"] = "H",
            ["Libraries:solo:MasterDriveLetter"] = "H",
            ["Libraries:solo:PersonalMinHelper"] = "1.6.0",
            ["TrueNas:Host"] = options.Host,
            ["TrueNas:Port"] = options.Port.ToString(),
            ["TrueNas:Username"] = options.Username,
            ["TrueNas:ApiKey"] = options.ApiKey,
            ["TrueNas:CaCertificatePath"] = options.CaCertificatePath,
        };
        foreach (var (key, value) in settings)
        {
            server.Settings[key] = value;
        }

        await server.InitializeAsync();
        var stand = new LibrariesStand(nas, server);
        var storage = server.Services.GetRequiredService<TrueNasStorage>();
        await storage.EnsureZvolAsync("games/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
        if (solo)
        {
            await storage.EnsureZvolAsync("hdd/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
        }

        return stand;
    }

    public async ValueTask DisposeAsync()
    {
        await ((Xunit.IAsyncLifetime)Server).DisposeAsync();
        await Nas.DisposeAsync();
    }

    /// <summary>Клиент API помощника: с <paramref name="handler"/> — через него (запись или подмена запросов).</summary>
    public HttpClient Http(DelegatingHandler? handler = null) => handler is null ? Server.CreateClient() : Server.CreateDefaultClient(handler);

    public Task PassAsync(LibrarySlot library) => StorageWorker.RunOnceAsync(library, reconcile: false, TimeProvider.System, CancellationToken.None);

    public async Task PublishAsync(LibrarySlot library, string label)
    {
        await library.Publisher.RequestPublishAsync(label, "test");
        await PassAsync(library);
        Assert.Equal("published", (await library.Repository.FindVersionAsync(label))!.State);
    }

    public Guid MachineId => Credentials.Current!.MachineId;

    public async Task OpenMasterAsync(LibrarySlot library)
    {
        await library.Editor.RequestOpenAsync(MachineId, "test");
        await PassAsync(library);
        Assert.Equal("open", (await library.Master.GetAsync()).State);
    }

    public async Task CloseMasterAsync(LibrarySlot library)
    {
        await library.Editor.RequestCloseAsync(force: false, "test");
        await PassAsync(library);
        Assert.Equal("closed", (await library.Master.GetAsync()).State);
    }

    public async Task<MachineRow> MachineAsync() => (await Server.Services.GetRequiredService<MachineRepository>().FindAsync(MachineId))!;

    /// <summary>Сбросы личного диска места библиотеки (rollback к @clean) на TrueNAS.</summary>
    public int GamesResets => Nas.Rollbacks("games/club/seats/games-seat-01");

    public int SoloResets => Nas.Rollbacks("hdd/club/seats/solo-seat-01");
}
