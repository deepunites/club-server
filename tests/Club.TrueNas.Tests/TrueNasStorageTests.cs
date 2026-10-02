using Club.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.TrueNas.Tests;

public sealed class TrueNasStorageTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Published = "tank/club/published";
    private static readonly Dictionary<string, string> Labels = new() { ["clubsrv:managed"] = "1", ["clubsrv:libver"] = "2026-09" };

    private FakeTrueNas _nas = null!;
    private TrueNasClient _client = null!;
    private TrueNasStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem(Published);
        _client = new TrueNasClient(_nas.Options(), NullLogger<TrueNasClient>.Instance);
        _storage = new TrueNasStorage(_client);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _nas.DisposeAsync();
    }

    [Fact]
    public async Task Connects_over_tls_and_picks_highest_allowed_api_version()
    {
        var info = await _client.EnsureConnectedAsync();
        Assert.Equal("v25.10.5", info.ApiVersion); // v26.0.0 предлагается, но не в белом списке
        Assert.Equal("25.10.7", info.ReleaseVersion);
    }

    [Fact]
    public async Task Wrong_api_key_is_reported_as_unavailable()
    {
        await using var client = new TrueNasClient(_nas.Options(apiKey: "1-wrong"), NullLogger<TrueNasClient>.Instance);
        var error = await Assert.ThrowsAsync<TrueNasUnavailableException>(() => client.EnsureConnectedAsync());
        Assert.Contains("AUTH_ERR", error.Message);
    }

    [Fact]
    public async Task Certificate_not_signed_by_club_ca_is_rejected()
    {
        await using var other = await FakeTrueNas.StartAsync();
        await using var client = new TrueNasClient(_nas.Options(caPath: other.CaPath), NullLogger<TrueNasClient>.Instance);
        await Assert.ThrowsAsync<TrueNasUnavailableException>(() => client.EnsureConnectedAsync());
        Assert.Equal(0, _nas.Connections);
    }

    [Fact]
    public async Task No_allowed_api_version_means_unavailable()
    {
        _nas.OfferedVersions = ["v25.04.2"];
        var error = await Assert.ThrowsAsync<TrueNasUnavailableException>(() => _client.EnsureConnectedAsync());
        Assert.Contains("No allowed TrueNAS API version", error.Message);
    }

    [Fact]
    public async Task Release_older_than_25_10_is_refused()
    {
        _nas.Release = "25.04.2.6";
        var error = await Assert.ThrowsAsync<TrueNasUnavailableException>(() => _client.EnsureConnectedAsync());
        Assert.Contains("25.10 or newer", error.Message);
    }

    [Fact]
    public async Task Publish_chain_is_idempotent()
    {
        for (var round = 0; round < 2; round++)
        {
            var zvol = await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
            Assert.Equal("VOLUME", zvol.Type);
            Assert.Equal("2026-09", zvol.Labels["clubsrv:libver"]);

            var snapshot = await _storage.EnsureSnapshotAsync(Master, "v2026-09", Labels);
            var clone = await _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-2026-09", Labels);
            Assert.True(clone.ReadOnly);
            Assert.Equal(snapshot.Id, clone.Origin);

            var extent = await _storage.EnsureReadOnlyExtentAsync("lib-2026-09", clone.Id, "clubsrv 2026-09");
            Assert.True(extent.ReadOnly);
            var target = await _storage.EnsureTargetAsync("games-2026-09", "games 2026-09", portalId: 1, initiatorGroupId: 1);
            var lun = await _storage.EnsureLunAsync(target.Id, extent.Id);
            Assert.Equal(0, lun.LunId);
        }

        // Второй проход ничего не создал заново.
        Assert.Equal(1, _nas.Count("snapshot"));
        Assert.Equal(1, _nas.Count("extent"));
        Assert.Equal(1, _nas.Count("target"));
        Assert.Equal(1, _nas.Count("targetextent"));
    }

    [Fact]
    public async Task Lost_response_after_clone_is_recovered_by_reconnect_and_requery()
    {
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
        var snapshot = await _storage.EnsureSnapshotAsync(Master, "v1", Labels);

        _nas.DropResponseAfterExecuting("pool.snapshot.clone");
        await Assert.ThrowsAsync<TrueNasUnavailableException>(() => _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-v1", Labels));

        // Клон создан, ответ потерян. Повтор той же операции видит его по имени и не падает на «already exists».
        var clone = await _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-v1", Labels);
        Assert.Equal(snapshot.Id, clone.Origin);
        Assert.True(_nas.Connections >= 2);
    }

    [Fact]
    public async Task Clone_of_another_snapshot_under_the_same_name_is_not_accepted()
    {
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
        var a = await _storage.EnsureSnapshotAsync(Master, "a", Labels);
        var b = await _storage.EnsureSnapshotAsync(Master, "b", Labels);
        await _storage.EnsureReadOnlyCloneAsync(a.Id, $"{Published}/lib-x", Labels);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.EnsureReadOnlyCloneAsync(b.Id, $"{Published}/lib-x", Labels));
    }

    [Fact]
    public async Task Target_with_sessions_is_not_deleted()
    {
        await PublishAsync("v1");
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-01", "games-v1");

        var blocked = await _storage.DeleteTargetAsync("games-v1");
        Assert.Equal(DeleteOutcome.Blocked, blocked.Outcome);
        Assert.Contains("in use", blocked.Reason);
        Assert.NotNull(await _storage.GetTargetAsync("games-v1"));

        _nas.ClearSessions();
        Assert.Equal(DeleteOutcome.Deleted, (await _storage.DeleteTargetAsync("games-v1")).Outcome);
        Assert.Equal(DeleteOutcome.AlreadyAbsent, (await _storage.DeleteTargetAsync("games-v1")).Outcome);
    }

    [Fact]
    public async Task Target_delete_removes_each_lun_with_its_own_reload_then_reloads_again()
    {
        // targets.py do_delete (TS-25.10.7): iscsi.targetextent.delete на каждую связку (у каждой свой reload),
        // затем scstadmin -rem_target и ещё один reload.
        _nas.ReadOnlyCopyManagerBug = false;
        await PublishAsync("v1");
        Assert.True(_nas.IsLive("games-v1"));

        var before = _nas.Reloads;
        Assert.Equal(DeleteOutcome.Deleted, (await _storage.DeleteTargetAsync("games-v1")).Outcome);
        Assert.Equal(2, _nas.Reloads - before);
        Assert.Equal(0, _nas.Count("targetextent"));
        Assert.False(_nas.IsLive("games-v1"));
        Assert.NotNull(await _storage.GetExtentAsync("lib-v1")); // экстенты target.delete не трогает (delete_extents=false)
    }

    [Fact]
    public async Task Lun_of_a_target_in_use_is_not_removed_without_force()
    {
        await PublishAsync("v1");
        _nas.AddSession("iqn.1991-05.com.microsoft:pc-01", "games-v1");
        var lun = (await _storage.GetTargetExtentAsync((await _storage.GetTargetAsync("games-v1"))!.Id, (await _storage.GetExtentAsync("lib-v1"))!.Id))!;

        var error = await Assert.ThrowsAsync<TrueNasRpcException>(() => _client.CallAsync("iscsi.targetextent.delete", [lun.Id, false]));
        Assert.Contains("is in use", error.Message);
        Assert.Equal(1, _nas.Count("targetextent"));

        await _client.CallAsync("iscsi.targetextent.delete", [lun.Id, true]);
        Assert.Equal(0, _nas.Count("targetextent"));
    }

    [Fact]
    public async Task Dataset_with_iscsi_attachments_is_never_deleted()
    {
        await PublishAsync("v1");
        var result = await _storage.DeleteDatasetAsync($"{Published}/lib-v1");
        Assert.Equal(DeleteOutcome.Blocked, result.Outcome);

        // Каскад middleware не запускался: экстент и LUN на месте.
        Assert.NotNull(await _storage.GetExtentAsync("lib-v1"));
        Assert.Equal(1, _nas.Count("targetextent"));
        Assert.DoesNotContain("pool.dataset.delete", _nas.Calls);
    }

    [Fact]
    public async Task Retiring_a_version_in_order_removes_everything()
    {
        await PublishAsync("v1");
        Assert.Equal(DeleteOutcome.Deleted, (await _storage.DeleteTargetAsync("games-v1")).Outcome);
        Assert.Equal(DeleteOutcome.Deleted, (await _storage.DeleteExtentAsync("lib-v1")).Outcome);

        // Снапшот с клоном: defer — ZFS удалит его сам после ухода клона.
        var snapshot = await _storage.DeleteSnapshotAsync($"{Master}@v1");
        Assert.Equal(DeleteOutcome.Blocked, snapshot.Outcome);

        Assert.Equal(DeleteOutcome.Deleted, (await _storage.DeleteDatasetAsync($"{Published}/lib-v1")).Outcome);
        Assert.Null(await _storage.GetSnapshotAsync($"{Master}@v1"));
    }

    [Fact]
    public async Task Not_found_is_detected_by_errno_not_text()
    {
        var error = await Assert.ThrowsAsync<TrueNasRpcException>(() => _client.CallAsync("pool.snapshot.delete", ["tank/none@x", new { defer = false }]));
        Assert.True(error.IsNotFound);
        Assert.Equal(TrueNasRpcException.InvalidParams, error.Code);
    }

    [Fact]
    public async Task Labels_outside_our_namespace_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.EnsureZvolAsync(Master, 1L << 30, "64K", new Dictionary<string, string> { ["org.freenas:description"] = "x" }));
    }

    [Fact]
    public async Task Keepalive_pings_keep_the_connection()
    {
        await _client.EnsureConnectedAsync();
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Contains("core.ping", _nas.Calls);
        await _storage.GetDatasetAsync(Master);
        Assert.Equal(1, _nas.Connections);
    }

    [Fact]
    public async Task Extent_before_target_leaves_a_read_only_target_disabled_as_on_the_stand()
    {
        // Порядок публикации до 2026-10-02: экстент → таргет → LUN. Все вызовы успешны, конфиг верный (*.query)...
        await PublishAsync("v1");
        Assert.NotNull(await _storage.GetTargetExtentAsync((await _storage.GetTargetAsync("games-v1"))!.Id, (await _storage.GetExtentAsync("lib-v1"))!.Id));

        // ...но reload от target.create открыл read-only устройство, SCST добавил его в copy_manager_tgt, и reload от
        // targetextent.create сорвался: таргет enabled 0 без LUN — «target not found or hidden from login».
        Assert.False(_nas.IsLive("games-v1"));
        Assert.Equal(1, _nas.FailedReloads);
        Assert.DoesNotContain($"{Basename}:games-v1", await DiscoverAsync());

        // Стоп/старт службы в интерфейсе TrueNAS (без -force) чинил стенд.
        _nas.RestartIscsiService();
        Assert.True(_nas.IsLive("games-v1"));
        Assert.Contains($"{Basename}:games-v1", await DiscoverAsync());
    }

    [Fact]
    public async Task Reload_through_an_unchanged_target_update_applies_the_configuration_again()
    {
        await PublishAsync("v1");
        Assert.False(_nas.IsLive("games-v1"));
        var groups = await _storage.TargetGroupsAsync((await _storage.GetTargetAsync("games-v1"))!.Id);

        await _storage.ReloadIscsiAsync("games-v1");

        Assert.True(_nas.IsLive("games-v1"));
        Assert.Contains($"{Basename}:games-v1", await DiscoverAsync());
        Assert.Equal(groups, await _storage.TargetGroupsAsync((await _storage.GetTargetAsync("games-v1"))!.Id));
        Assert.Equal(1, _nas.Count("target"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.ReloadIscsiAsync("games-none"));
    }

    [Fact]
    public async Task Extent_create_fails_until_the_clone_device_appears()
    {
        _nas.CloneDeviceDelay = 2;
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
        var snapshot = await _storage.EnsureSnapshotAsync(Master, "v1", Labels);
        var clone = await _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-v1", Labels);

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.EnsureReadOnlyExtentAsync("lib-v1", clone.Id, "test"));
            Assert.IsType<TrueNasRpcException>(error.InnerException);
        }

        Assert.True((await _storage.EnsureReadOnlyExtentAsync("lib-v1", clone.Id, "test")).ReadOnly);
        Assert.Single(await _storage.ListExtentsAsync());
    }

    private const string Basename = "iqn.2005-10.org.freenas.ctl";

    private Task<IReadOnlyList<string>> DiscoverAsync() =>
        IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, "iqn.2026-10.test:probe", TimeSpan.FromSeconds(5));

    private async Task PublishAsync(string version)
    {
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
        var snapshot = await _storage.EnsureSnapshotAsync(Master, version, Labels);
        var clone = await _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-{version}", Labels);
        var extent = await _storage.EnsureReadOnlyExtentAsync($"lib-{version}", clone.Id, "test");
        var target = await _storage.EnsureTargetAsync($"games-{version}", $"games {version}", 1, 1);
        await _storage.EnsureLunAsync(target.Id, extent.Id);
    }
}
