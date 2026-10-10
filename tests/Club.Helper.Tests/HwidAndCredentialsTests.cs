using Club.Helper.Core;

namespace Club.Helper.Tests;

public sealed class HwidAndCredentialsTests
{
    [Theory]
    [InlineData("03000200-0400-0500-0006-000700080009", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", true)]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", true)]
    [InlineData("", true)]
    [InlineData("4C4C4544-0051-3510-8052-B4C04F4B4E32", false)]
    public void Placeholder_uuids_are_recognised(string uuid, bool placeholder) =>
        Assert.Equal(placeholder, MachineHwid.IsPlaceholderUuid(uuid));

    [Fact]
    public void Placeholder_uuid_boards_are_told_apart_by_mac()
    {
        const string ami = "03000200-0400-0500-0006-000700080009";
        Assert.NotEqual(MachineHwid.Compute(ami, "Default string", ["10:ff:e0:ef:86:01"]), MachineHwid.Compute(ami, "Default string", ["10:ff:e0:ef:91:9d"]));
        Assert.Equal(MachineHwid.Compute(ami, "x", ["b", "a"]), MachineHwid.Compute(ami, "x", ["a", "b"])); // порядок карт не важен

        // Настоящий UUID — HWID прежний (без MAC): уже зарегистрированные ПК остаются теми же машинами.
        const string real = "4C4C4544-0051-3510-8052-B4C04F4B4E32";
        Assert.Equal(MachineHwid.Compute(real, "S1", ["aa"]), MachineHwid.Compute(real, "S1", ["bb"]));
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{real.ToLowerInvariant()}|S1"))),
            MachineHwid.Compute(real, "S1", ["aa"]));
    }

    [Fact]
    public async Task Cached_assignment_of_another_pc_is_not_used()
    {
        var cache = new InMemoryAssignmentCache();
        var assignment = new VolumeAssignment("v1", "192.168.77.10:3260", "iqn.2005-10.org.freenas.ctl:games-v1", true, "G");
        await cache.SaveAsync("hwid-master", assignment, CancellationToken.None);
        Assert.Equal(assignment, await cache.LoadAsync("hwid-master", CancellationToken.None));
        Assert.Null(await cache.LoadAsync("hwid-seat-07", CancellationToken.None));
    }
}
