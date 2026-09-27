using Club.Helper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

public sealed class VolumeManagerTests
{
    private const string V1 = "iqn.2005-10.org.freenas.ctl:games-v1";
    private const string V2 = "iqn.2005-10.org.freenas.ctl:games-v2";

    private readonly FakeWindowsStorage _storage = new();
    private readonly FakeProcesses _processes = new();
    private readonly VolumeManager _volumes;

    public VolumeManagerTests() =>
        _volumes = new VolumeManager(_storage, _processes, new HelperOptions { DiskWaitSec = 1 }, TimeProvider.System, NullLogger<VolumeManager>.Instance);

    private static VolumeAssignment Assign(string iqn, string version) => new(version, "192.168.77.10:3260", iqn, ReadOnly: true, "G");

    [Fact]
    public async Task Mount_is_fail_closed_san_then_readonly_then_online_then_letter()
    {
        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);

        Assert.Equal("mounted", report.State);
        Assert.True(report.ReadOnlyVerified);
        Assert.Equal(["san", $"connect {V1} 192.168.77.10:3260", "ro 2", "online 2", "letter 2 G"], _storage.Log);
        Assert.False(_storage.EverWritableOnline);
        var disk = _storage.Sessions[V1];
        Assert.True(disk is { ReadOnly: true, Offline: false, Letter: 'G' });
    }

    [Fact]
    public async Task Disk_that_refuses_read_only_is_never_brought_online()
    {
        _storage.RefuseReadOnly = true;
        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);

        Assert.Equal("failed", report.State);
        Assert.False(report.ReadOnlyVerified);
        Assert.DoesNotContain(_storage.Log, l => l.StartsWith("online", StringComparison.Ordinal));
        Assert.Contains($"disconnect {V1}", _storage.Log);
        Assert.Empty(_storage.Sessions);
        Assert.False(_storage.EverWritableOnline);
    }

    [Fact]
    public async Task Repeated_apply_changes_nothing()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.Log.Clear();

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("mounted", report.State);
        Assert.Empty(_storage.Log);
    }

    [Fact]
    public async Task New_version_waits_while_a_game_runs_from_the_volume()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _processes.Running['G'] = [@"G:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"];

        var waiting = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("switchPending", waiting.State);
        Assert.Contains("cs2.exe", waiting.Error);
        Assert.True(_storage.Sessions.ContainsKey(V1));
        Assert.False(_storage.Sessions.ContainsKey(V2));

        _processes.Running.Clear(); // игру закрыли
        var switched = await _volumes.ApplyAsync(Assign(V2, "v2"), CancellationToken.None);
        Assert.Equal("mounted", switched.State);
        Assert.Equal("v2", switched.LibraryVersion);
        Assert.False(_storage.Sessions.ContainsKey(V1));
        Assert.Equal('G', _storage.Sessions[V2].Letter);
        Assert.False(_storage.EverWritableOnline);
    }

    [Fact]
    public async Task No_assignment_releases_the_volume_when_idle()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _processes.Running['G'] = [@"G:\game.exe"];
        Assert.Equal("switchPending", (await _volumes.ApplyAsync(null, CancellationToken.None)).State);

        _processes.Running.Clear();
        Assert.Equal("none", (await _volumes.ApplyAsync(null, CancellationToken.None)).State);
        Assert.Empty(_storage.Sessions);
    }

    [Fact]
    public async Task Drift_to_writable_is_repaired_back_to_read_only()
    {
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        _storage.MakeWritable(V1);

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("mounted", report.State);
        Assert.True(_storage.Sessions[V1].ReadOnly);
    }

    [Fact]
    public async Task Foreign_iscsi_sessions_are_left_alone()
    {
        _storage.AddForeignSession("iqn.2000-01.com.vendor:backup-disk");
        await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        await _volumes.ApplyAsync(null, CancellationToken.None);
        Assert.True(_storage.Sessions.ContainsKey("iqn.2000-01.com.vendor:backup-disk"));
    }

    [Fact]
    public async Task Writable_assignment_is_refused()
    {
        var report = await _volumes.ApplyAsync(Assign(V1, "v1") with { ReadOnly = false }, CancellationToken.None);
        Assert.Equal("failed", report.State);
        Assert.Empty(_storage.Log);
    }

    [Fact]
    public async Task Letter_taken_by_another_volume_fails_and_disconnects()
    {
        _storage.AddForeignSession("iqn.2000-01.com.vendor:other");
        var foreign = _storage.Sessions["iqn.2000-01.com.vendor:other"];
        foreign.Letter = 'G';

        var report = await _volumes.ApplyAsync(Assign(V1, "v1"), CancellationToken.None);
        Assert.Equal("failed", report.State);
        Assert.Contains("in use", report.Error);
        Assert.False(_storage.Sessions.ContainsKey(V1));
    }

    [Theory]
    [InlineData("192.168.77.10:3260", "192.168.77.10", 3260)]
    [InlineData("nas.club.lan:3261", "nas.club.lan", 3261)]
    [InlineData("192.168.77.10", "192.168.77.10", 3260)]
    public void Portal_is_parsed(string portal, string host, int port) =>
        Assert.Equal((host, port), VolumeManager.ParsePortal(portal));
}
