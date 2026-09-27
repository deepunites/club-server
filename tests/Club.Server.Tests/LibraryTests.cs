using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>Оркестрация публикации версий библиотеки: своя база и свой поддельный TrueNAS на каждый тест.</summary>
public sealed class LibraryTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Published = "tank/club/published";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private LibraryPublisher _publisher = null!;
    private LibraryRepository _repository = null!;
    private TrueNasStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem(Published);

        _server = new ServerFixture();
        var options = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:MasterZvol"] = Master,
            ["Library:PublishedParent"] = Published,
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["Library:ExtentRetryDelayMs"] = "10",
            ["TrueNas:Host"] = options.Host,
            ["TrueNas:Port"] = options.Port.ToString(),
            ["TrueNas:Username"] = options.Username,
            ["TrueNas:ApiKey"] = options.ApiKey,
            ["TrueNas:CaCertificatePath"] = options.CaCertificatePath,
        })
        {
            _server.Settings[key] = value;
        }

        await _server.InitializeAsync();
        _publisher = _server.Services.GetRequiredService<LibraryPublisher>();
        _repository = _server.Services.GetRequiredService<LibraryRepository>();
        _storage = _server.Services.GetRequiredService<TrueNasStorage>();
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:managed"] = "1", ["clubsrv:role"] = "master" });
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _nas.DisposeAsync();
    }

    private Task PassAsync(bool reconcile = true) =>
        StorageWorker.RunOnceAsync(_publisher, _repository, reconcile, TimeProvider.System, CancellationToken.None);

    private async Task PublishAsync(string label)
    {
        await _publisher.RequestPublishAsync(label, "test");
        await PassAsync();
        Assert.Equal("published", (await _repository.FindVersionAsync(label))!.State);
    }

    [Fact]
    public async Task Published_version_reaches_agent_config_as_read_only_iscsi()
    {
        var (agent, register) = await TestAgent.RegisterAsync(_server.CreateClient());
        Assert.False(register.GetProperty("config").TryGetProperty("storage", out _)); // до публикации тома нет
        var before = register.GetProperty("config").GetProperty("version").GetInt32();

        await PublishAsync("2026-09");

        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/config");
        var config = await Contract.ReadAsync(response, "AgentServerConfig");
        Assert.True(config.GetProperty("version").GetInt32() > before);
        var share = config.GetProperty("storage").GetProperty("gamesShare");
        Assert.True(share.GetProperty("enabled").GetBoolean());
        var iscsi = share.GetProperty("iscsi");
        Assert.Equal($"{Basename}:games-2026-09", iscsi.GetProperty("targetIqn").GetString());
        Assert.Equal("192.168.77.10:3260", iscsi.GetProperty("portal").GetString());
        Assert.True(iscsi.GetProperty("readOnly").GetBoolean());

        var clone = await _storage.GetDatasetAsync($"{Published}/lib-2026-09");
        Assert.True(clone!.ReadOnly);
        Assert.Equal("2026-09", clone.Labels["clubsrv:libver"]);
        Assert.True((await _storage.GetExtentAsync("lib-2026-09"))!.ReadOnly);
    }

    [Fact]
    public async Task Only_current_and_rollback_versions_are_kept()
    {
        await PublishAsync("v1");
        await PublishAsync("v2");
        var pointers = await _repository.PointersAsync();
        Assert.Equal(("v2", "v1"), (pointers.Current!.Label, pointers.Rollback!.Label));

        await PublishAsync("v3");
        pointers = await _repository.PointersAsync();
        Assert.Equal(("v3", "v2"), (pointers.Current!.Label, pointers.Rollback!.Label));
        Assert.Equal("retired", (await _repository.FindVersionAsync("v1"))!.State);

        Assert.Null(await _storage.GetTargetAsync("games-v1"));
        Assert.Null(await _storage.GetExtentAsync("lib-v1"));
        Assert.Null(await _storage.GetDatasetAsync($"{Published}/lib-v1"));
        Assert.Null(await _storage.GetSnapshotAsync($"{Master}@lib-v1"));
        Assert.Equal(2, _nas.Count("target"));
    }

    [Fact]
    public async Task Version_in_use_is_not_torn_down_until_clients_leave()
    {
        await PublishAsync("v1");
        await PublishAsync("v2");
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-07", "games-v1");

        await PublishAsync("v3");
        Assert.Equal("retiring", (await _repository.FindVersionAsync("v1"))!.State);
        Assert.NotNull(await _storage.GetTargetAsync("games-v1"));
        var warning = Assert.Single(await _repository.ActiveWarningsAsync(), w => w.Kind == "retireBlocked");
        Assert.Equal("v1", warning.Subject);

        _nas.ClearSessions();
        await PassAsync();
        Assert.Equal("retired", (await _repository.FindVersionAsync("v1"))!.State);
        Assert.DoesNotContain(await _repository.ActiveWarningsAsync(), w => w.Kind == "retireBlocked");
    }

    [Fact]
    public async Task Rollback_swaps_current_and_rollback_and_updates_config()
    {
        var (agent, _) = await TestAgent.RegisterAsync(_server.CreateClient());
        await PublishAsync("v1");
        await PublishAsync("v2");

        await _publisher.RequestRollbackAsync("test");
        await PassAsync();
        var pointers = await _repository.PointersAsync();
        Assert.Equal(("v1", "v2"), (pointers.Current!.Label, pointers.Rollback!.Label));

        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/agents/{agent.PcId}/config");
        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{Basename}:games-v1", config.GetProperty("storage").GetProperty("gamesShare").GetProperty("iscsi").GetProperty("targetIqn").GetString());
        Assert.Equal(2, _nas.Count("target")); // откат не пересоздаёт и не удаляет объекты
    }

    [Fact]
    public async Task Rollback_without_previous_version_is_refused()
    {
        await PublishAsync("v1");
        var error = await Assert.ThrowsAsync<LibraryRequestException>(() => _publisher.RequestRollbackAsync("test"));
        Assert.Equal("noRollback", error.Reason);
    }

    [Fact]
    public async Task Crash_in_the_middle_of_publish_is_resumed_without_duplicates()
    {
        await _publisher.RequestPublishAsync("v1", "test");
        _nas.DropResponseAfterExecuting("pool.snapshot.clone");

        await PassAsync(); // клон создан, ответ потерян — операция остаётся в очереди
        var operation = Assert.Single(await _repository.OpenOperationsAsync());
        Assert.Equal("pending", operation.Status);
        Assert.Equal("publishing", (await _repository.FindVersionAsync("v1"))!.State);

        await PassAsync();
        Assert.Equal("published", (await _repository.FindVersionAsync("v1"))!.State);
        Assert.Empty(await _repository.OpenOperationsAsync());
        Assert.Equal(1, _nas.Count("snapshot"));
        Assert.Equal(1, _nas.Count("target"));
    }

    [Fact]
    public async Task TrueNas_outage_postpones_without_spending_attempts()
    {
        _nas.Down = true;
        await _publisher.RequestPublishAsync("v1", "test");
        await PassAsync(reconcile: false);
        var operation = Assert.Single(await _repository.OpenOperationsAsync());
        Assert.Equal(0, operation.Attempts);
        Assert.Equal("publishing", (await _repository.FindVersionAsync("v1"))!.State);

        _nas.Down = false;
        await Task.Delay(TimeSpan.FromSeconds(2.2)); // пауза переподключения адаптера после отказа
        await PassAsync();
        Assert.Equal("published", (await _repository.FindVersionAsync("v1"))!.State);
    }

    [Fact]
    public async Task Reconcile_reports_drift_and_orphans_but_changes_nothing()
    {
        await PublishAsync("v1");
        Assert.Empty(await _repository.ActiveWarningsAsync());

        _nas.MakeWritable($"{Published}/lib-v1");
        var orphanSnapshot = await _storage.EnsureSnapshotAsync(Master, "manual", new Dictionary<string, string> { ["clubsrv:managed"] = "1" });
        await _storage.EnsureReadOnlyCloneAsync(orphanSnapshot.Id, $"{Published}/lib-manual", new Dictionary<string, string> { ["clubsrv:managed"] = "1" });
        _nas.MakeWritable($"{Published}/lib-v1"); // clone выше снова выставил бы ro у своего клона, не у v1

        await PassAsync();
        var warnings = await _repository.ActiveWarningsAsync();
        Assert.Contains(warnings, w => w.Kind == "cloneWritable" && w.Subject == $"{Published}/lib-v1");
        Assert.Contains(warnings, w => w.Kind == "orphanClone" && w.Subject == $"{Published}/lib-manual");

        // Автоисправления нет: клон по-прежнему доступен на запись, сирота на месте.
        Assert.False((await _storage.GetDatasetAsync($"{Published}/lib-v1"))!.ReadOnly);
        Assert.NotNull(await _storage.GetDatasetAsync($"{Published}/lib-manual"));
    }

    [Fact]
    public async Task Invalid_or_duplicate_labels_are_refused()
    {
        Assert.Equal("label", (await Assert.ThrowsAsync<LibraryRequestException>(() => _publisher.RequestPublishAsync("Bad Label", "t"))).Reason);
        await PublishAsync("v1");
        Assert.Equal("exists", (await Assert.ThrowsAsync<LibraryRequestException>(() => _publisher.RequestPublishAsync("v1", "t"))).Reason);
    }
}
