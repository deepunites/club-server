using Club.Helper.Core;

namespace Club.Helper.Tests;

/// <summary>Сопоставление путей образов процессов с томом: Win32 (<c>G:\…</c>) и NT (<c>\Device\HarddiskVolume12\…</c>).</summary>
public sealed class VolumeImagePathsTests
{
    private const string Volume12 = @"\Device\HarddiskVolume12";

    [Theory]
    [InlineData(@"G:\Counter Strike 1.6 PRO\cstrike.exe", @"G:\", true)]
    [InlineData(@"g:\steam\steam.exe", @"G:\", true)] // регистр не важен
    [InlineData(@"G:\steam\steam.exe", "G:", true)] // корень без косой черты
    [InlineData(@"GX:\steam.exe", "G:", false)]
    [InlineData(@"C:\Windows\explorer.exe", @"G:\", false)]
    [InlineData(@"\Device\HarddiskVolume12\steam\steam.exe", Volume12, true)]
    [InlineData(@"\device\harddiskvolume12\steam\steam.exe", Volume12, true)]
    [InlineData(@"\Device\HarddiskVolume12\steam\steam.exe", @"\Device\HarddiskVolume1", false)] // Volume1 — не Volume12
    [InlineData(@"\Device\HarddiskVolume1\steam.exe", Volume12, false)]
    [InlineData(@"\Device\HarddiskVolume12", Volume12, false)] // сам корень — не файл под ним
    [InlineData(@"G:\x.exe", "", false)]
    [InlineData(@"G:\x.exe", @"\", false)]
    public void Path_is_under_a_root_only_by_whole_component(string path, string root, bool under) =>
        Assert.Equal(under, VolumeImagePaths.IsUnder(path, root));

    [Theory]
    [InlineData(@"\\?\G:\steam\steam.exe", @"G:\steam\steam.exe")]
    [InlineData(@"\??\G:\steam\steam.exe", @"G:\steam\steam.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume12\steam.exe", @"\Device\HarddiskVolume12\steam.exe")]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume12", Volume12)]
    [InlineData(@"\GLOBAL??\G:", "G:")]
    [InlineData(@"\Device\HarddiskVolume12\steam.exe", @"\Device\HarddiskVolume12\steam.exe")]
    [InlineData(@"G:\steam.exe", @"G:\steam.exe")]
    public void Namespace_prefixes_are_removed(string path, string plain) =>
        Assert.Equal(plain, VolumeImagePaths.Unprefix(path));

    [Theory]
    [InlineData(Volume12, Volume12)]
    [InlineData(@"\Device\HarddiskVolume12\", Volume12)]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume12", Volume12)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume12", Volume12)]
    [InlineData(@"\??\D:\Games", @"\Device\HarddiskVolume3\Games")] // subst G: D:\Games
    [InlineData(@"\\?\Volume{6f1d2c3a-0000-0000-0000-100000000000}\", Volume12)] // ссылка на том по GUID
    public void Nt_device_of_a_drive_letter_is_resolved(string answer, string device)
    {
        var dos = new Dictionary<string, string>
        {
            ["G:"] = answer,
            ["D:"] = @"\Device\HarddiskVolume3",
            ["Volume{6f1d2c3a-0000-0000-0000-100000000000}"] = Volume12,
        };
        Assert.Equal(device, VolumeImagePaths.ResolveNtDevice('g', name => dos.GetValueOrDefault(name)));
    }

    [Fact]
    public void Unresolvable_or_looping_names_give_no_device()
    {
        Assert.Null(VolumeImagePaths.ResolveNtDevice('G', _ => null));
        Assert.Null(VolumeImagePaths.ResolveNtDevice('G', _ => "   "));
        Assert.Null(VolumeImagePaths.ResolveNtDevice('G', name => name == "G:" ? @"\??\H:" : @"\??\G:")); // G: → H: → G: …
    }

    [Theory]
    [InlineData(@"G:\Counter Strike 1.6 PRO\cstrike.exe", @"\Device\HarddiskVolume12\Counter Strike 1.6 PRO\cstrike.exe", @"G:\Counter Strike 1.6 PRO\cstrike.exe")]
    [InlineData(null, @"\Device\HarddiskVolume12\steam\steam.exe", @"G:\steam\steam.exe")] // Win32-путь не прочитался — хватает NT
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume12\steam\steam.exe", null, @"G:\steam\steam.exe")] // Win32 без буквы
    [InlineData(@"\\?\G:\steam\steam.exe", null, @"G:\steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"\Device\HarddiskVolume3\Program Files (x86)\Steam\steam.exe", null)]
    [InlineData(@"H:\steam.exe", @"\Device\HarddiskVolume1\steam.exe", null)] // Volume1 — чужой том
    [InlineData(null, null, null)]
    public void Process_matches_the_volume_by_either_path(string? win32, string? native, string? reported) =>
        Assert.Equal(reported, VolumeImagePaths.Match(win32, native, 'G', Volume12));

    [Theory]
    [InlineData(@"G:\cs2.exe", @"\Device\HarddiskVolume12\cs2.exe", @"G:\cs2.exe", null)] // нашёлся бы и в 1.4.1
    [InlineData(null, @"\Device\HarddiskVolume12\steam\steam.exe", @"G:\steam\steam.exe", "Win32 path not read")]
    [InlineData(@"\\?\G:\steam\steam.exe", null, @"G:\steam\steam.exe", @"Win32 path not under G:\")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume12\steam.exe", @"\Device\HarddiskVolume12\steam.exe", @"G:\steam.exe", @"Win32 path not under G:\")]
    public void Match_tells_why_a_plain_win32_path_check_would_miss_the_process(string? win32, string? native, string path, string? miss) =>
        Assert.Equal(new VolumeImageMatch(path, miss), VolumeImagePaths.MatchImage(win32, native, 'G', Volume12));

    [Fact]
    public void Process_not_from_the_volume_gives_no_match() =>
        Assert.Null(VolumeImagePaths.MatchImage(@"C:\Program Files (x86)\Steam\steam.exe", @"\Device\HarddiskVolume3\Program Files (x86)\Steam\steam.exe", 'G', Volume12));

    [Fact]
    public void Without_an_nt_device_only_the_drive_letter_matches()
    {
        Assert.Equal(@"G:\cs2.exe", VolumeImagePaths.Match(@"G:\cs2.exe", null, 'g', null));
        Assert.Null(VolumeImagePaths.Match(null, @"\Device\HarddiskVolume12\cs2.exe", 'G', null));
    }

    [Fact]
    public void Diagnostic_samples_skip_windows_and_unreadable_images_and_put_other_drives_first()
    {
        ProcessImage[] images =
        [
            new(4, null, null), // System — путей нет
            new(700, @"C:\Windows\System32\svchost.exe", @"\Device\HarddiskVolume3\Windows\System32\svchost.exe"),
            new(710, null, @"\Device\HarddiskVolume3\Windows\explorer.exe"), // Windows по NT-пути
            new(800, @"C:\Program Files\NVIDIA Corporation\nvcontainer.exe", @"\Device\HarddiskVolume3\Program Files\NVIDIA Corporation\nvcontainer.exe"),
            new(4120, @"G:\steam\steam.exe", @"\Device\HarddiskVolume12\steam\steam.exe"),
            new(5004, null, @"\Device\HarddiskVolume12\Counter Strike 1.6 PRO\cstrike.exe"),
        ];

        var samples = VolumeImagePaths.OutsideWindows(images, @"C:\Windows", @"\Device\HarddiskVolume3\Windows");
        Assert.Equal([4120, 5004, 800], samples.Select(i => i.Pid));
    }

    [Fact]
    public void Scan_report_names_device_failures_and_paths()
    {
        var open = new ErrorTally();
        open.Add(5);
        open.Add(87);
        open.Add(5);
        var win32 = new ErrorTally();
        win32.Add(31);

        // Согласованный пример: устройства тома нет — путь NT с тома не сопоставить, путь Win32 не прочитался.
        var report = new ProcessScanReport(
            'g', null, "QueryDosDevice(G:) error 2", 214, 0, open, win32, new ErrorTally(), @"C:\Windows", 48,
            [new(3312, @"C:\Program Files (x86)\Steam\steam.exe", @"\Device\HarddiskVolume3\Program Files (x86)\Steam\steam.exe"), new(5004, null, @"\Device\HarddiskVolume12\cstrike.exe")]);

        Assert.Equal(
            @"drive G: = no NT device (QueryDosDevice(G:) error 2); processes 214, from the volume 0; OpenProcess failed 3 (most often error 5); "
            + @"Win32 path failed 1 (most often error 31); NT path failed 0; outside C:\Windows: 48, first 2: "
            + @"[3312] C:\Program Files (x86)\Steam\steam.exe | \Device\HarddiskVolume3\Program Files (x86)\Steam\steam.exe; "
            + @"[5004] ? | \Device\HarddiskVolume12\cstrike.exe",
            report.ToString());
    }

    [Fact]
    public void Most_common_error_wins_ties_by_the_smaller_code()
    {
        var tally = new ErrorTally();
        Assert.Equal(("0", (int?)null), (tally.ToString(), tally.MostCommon));
        tally.Add(31);
        tally.Add(5);
        Assert.Equal((2, (int?)5), (tally.Count, tally.MostCommon));
    }
}
