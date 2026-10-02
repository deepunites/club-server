using System.Net;
using System.Net.Sockets;
using Club.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.TrueNas.Tests;

/// <summary>SendTargets с сервера клуба против портала поддельного TrueNAS (своя реализация протокола в TestSupport).</summary>
public sealed class IscsiDiscoveryTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Published = "tank/club/published";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string Probe = "iqn.2026-10.test:probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, string> Labels = new() { ["clubsrv:managed"] = "1" };

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
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", Labels);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _nas.DisposeAsync();
    }

    [Fact]
    public async Task Lists_only_enabled_targets_with_a_lun_for_this_initiator()
    {
        var target = await _storage.EnsureTargetAsync("games-v1", "games v1", 1, 1);
        Assert.Empty(await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout)); // таргет без LUN — enabled 0

        var extent = await ExtentAsync("v1");
        await _storage.EnsureLunAsync(target.Id, extent.Id);
        Assert.Equal([$"{Basename}:games-v1"], (await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout)).ToArray());

        // Таргет для одного IQN виден только ему.
        var group = await _storage.EnsureInitiatorGroupAsync("pc", "iqn.1991-05.com.microsoft:pc-01");
        var single = await _storage.EnsureTargetAsync("only-pc", "only pc", 1, group.Id);
        await _storage.EnsureLunAsync(single.Id, (await ExtentAsync("v2")).Id);
        Assert.DoesNotContain($"{Basename}:only-pc", await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout));
        Assert.Contains($"{Basename}:only-pc", await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, "iqn.1991-05.com.microsoft:pc-01", Timeout));
        Assert.Contains("iqn.1991-05.com.microsoft:pc-01", _nas.DiscoveryInitiators);
    }

    [Fact]
    public async Task Long_answer_is_read_across_continuation_pdus()
    {
        _nas.ReadOnlyCopyManagerBug = false;
        _nas.DiscoveryTextChunk = 7;
        foreach (var version in new[] { "v1", "v2", "v3" })
        {
            var target = await _storage.EnsureTargetAsync($"games-{version}", $"games {version}", 1, 1);
            await _storage.EnsureLunAsync(target.Id, (await ExtentAsync(version)).Id);
        }

        Assert.Equal(
            [$"{Basename}:games-v1", $"{Basename}:games-v2", $"{Basename}:games-v3"],
            (await IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout)).ToArray());
    }

    [Fact]
    public async Task Discovery_that_requires_chap_cannot_be_checked()
    {
        _nas.DiscoveryAuthRequired = true;
        var error = await Assert.ThrowsAsync<IscsiDiscoveryException>(() => IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout));
        Assert.Contains("0x0201", error.Message);
    }

    [Fact]
    public async Task Stopped_service_or_closed_port_cannot_be_checked()
    {
        _nas.IscsiServiceRunning = false;
        await Assert.ThrowsAsync<IscsiDiscoveryException>(() => IscsiDiscovery.SendTargetsAsync(_nas.IscsiPortal, Probe, Timeout));

        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        await Assert.ThrowsAsync<IscsiDiscoveryException>(() => IscsiDiscovery.SendTargetsAsync($"127.0.0.1:{port}", Probe, Timeout));
    }

    [Fact]
    public async Task Silent_portal_times_out()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var started = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<IscsiDiscoveryException>(() =>
            IscsiDiscovery.SendTargetsAsync($"127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", Probe, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("no answer", error.Message);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        silent.Stop();
    }

    [Theory]
    [InlineData("192.168.1.50:3260", "192.168.1.50", 3260)]
    [InlineData("192.168.1.50", "192.168.1.50", 3260)]
    [InlineData(" truenas.club:3261 ", "truenas.club", 3261)]
    [InlineData("[fd00::5]:3262", "fd00::5", 3262)]
    public void Portal_address_is_parsed(string portal, string host, int port) =>
        Assert.Equal((host, port), IscsiDiscovery.ParsePortal(portal));

    [Theory]
    [InlineData("[fd00::5]", "fd00::5", 3260)]
    [InlineData("fd00::5", "fd00::5", 3260)]
    [InlineData("192.168.1.50:65535", "192.168.1.50", 65535)]
    public void Portal_address_edge_cases_are_parsed(string portal, string host, int port) =>
        Assert.Equal((host, port), IscsiDiscovery.ParsePortal(portal));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.50:70000")]
    [InlineData("192.168.1.50:0")]
    [InlineData("192.168.1.50:-1")]
    [InlineData("192.168.1.50:abc")]
    [InlineData("192.168.1.50:")]
    [InlineData(":3260")]
    [InlineData("[fd00::5]:99999")]
    [InlineData("[fd00::5")]
    [InlineData("[fd00::5]3260")]
    [InlineData("http://192.168.1.50:3260")] // два двоеточия, но не IPv6
    [InlineData("192.168.1.50:3260:3260")]
    [InlineData("[192.168.1.50]:3260")] // в скобках — только IPv6
    [InlineData("[truenas.club]")]
    [InlineData("[]:3260")]
    public void Bad_portal_address_cannot_be_checked(string portal) =>
        Assert.Throws<IscsiDiscoveryException>(() => IscsiDiscovery.ParsePortal(portal));

    [Fact]
    public async Task Out_of_range_port_is_a_discovery_error_not_a_socket_exception() =>
        await Assert.ThrowsAsync<IscsiDiscoveryException>(() => IscsiDiscovery.SendTargetsAsync("127.0.0.1:70000", Probe, Timeout));

    private async Task<IscsiExtent> ExtentAsync(string version)
    {
        var snapshot = await _storage.EnsureSnapshotAsync(Master, version, Labels);
        var clone = await _storage.EnsureReadOnlyCloneAsync(snapshot.Id, $"{Published}/lib-{version}", Labels);
        return await _storage.EnsureReadOnlyExtentAsync($"lib-{version}", clone.Id, "test");
    }
}
