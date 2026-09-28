using Club.Server.Imaging;

namespace Club.Server.Tests;

/// <summary>Разбор WIM, выбор диска для стирания, unattend — без базы.</summary>
public sealed class ImagingUnitTests
{
    public static string WimFixture => Path.Combine(AppContext.BaseDirectory, "Imaging", "two-images.wim");

    [Fact]
    public void Reads_images_from_a_real_wim()
    {
        var images = WimFile.ReadImages(WimFixture);
        Assert.Equal(2, images.Count);
        var pro = images[0];
        Assert.Equal((1, "Windows 11 Pro", "Клуб: золотой образ"), (pro.Index, pro.Name, pro.Description));
        Assert.Equal(("Professional", "10.0.26100.4652", "x64", "ru-RU", 20006L), (pro.EditionId, pro.Build, pro.Architecture, pro.DefaultLanguage, pro.TotalBytes));
        Assert.Equal((2, "Windows 11 Home", (string?)null), (images[1].Index, images[1].Name, images[1].EditionId));
    }

    [Fact]
    public void Rejects_files_that_are_not_wim()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[4096]);
            Assert.Throws<InvalidDataException>(() => WimFile.ReadImages(file));
            File.WriteAllBytes(file, File.ReadAllBytes(WimFixture)[..300]); // обрезан: XML за концом файла
            Assert.Throws<InvalidDataException>(() => WimFile.ReadImages(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private const long GB = 1L << 30;
    private static readonly DiskInfo Usb = new(0, "USB123", "Flash", 64 * GB, "USB");
    private static readonly DiskInfo Nvme = new(1, "S5GXNX0T123456 ", "Samsung 980", 500 * GB, "NVMe");
    private static readonly DiskInfo Sata = new(2, "WD-WX11", "WD Blue", 1000 * GB, "SATA");
    private static readonly long Need = TargetDisk.RequiredBytes(8 * GB, 30 * GB);

    [Fact]
    public void Wipes_only_the_reported_system_disk()
    {
        var system = new SystemDisk("S5GXNX0T123456.", "Samsung 980", 500 * GB, "NVMe");
        Assert.Equal(1, TargetDisk.Choose([Usb, Nvme, Sata], system, allowNewDisk: false, Need).Disk?.Number);

        // Системный диск пропал (заменили): другой диск стираем только с явного разрешения.
        Assert.Equal("systemDiskNotFound", TargetDisk.Choose([Usb, Sata], system, false, Need).Failure);
        Assert.Equal(2, TargetDisk.Choose([Usb, Sata], system, allowNewDisk: true, Need).Disk?.Number);
    }

    [Fact]
    public void Without_a_report_only_an_unambiguous_disk_is_wiped()
    {
        Assert.Equal(1, TargetDisk.Choose([Usb, Nvme], null, false, Need).Disk?.Number);
        Assert.Equal("ambiguousDisks", TargetDisk.Choose([Usb, Nvme, Sata], null, false, Need).Failure);
        Assert.Equal("noInternalDisk", TargetDisk.Choose([Usb], null, false, Need).Failure);
        Assert.Equal("diskTooSmall", TargetDisk.Choose([Nvme with { SizeBytes = 32 * GB }], null, false, Need).Failure);

        // Маленький второй диск (например, 32 ГБ под кэш) не мешает выбрать единственный подходящий.
        Assert.Equal(1, TargetDisk.Choose([Nvme, Sata with { SizeBytes = 32 * GB }], null, false, Need).Disk?.Number);
    }

    [Fact]
    public void Required_size_covers_image_and_its_temporary_copy()
    {
        Assert.Equal(TargetDisk.MinDiskBytes, TargetDisk.RequiredBytes(1 * GB, 10 * GB));
        Assert.True(TargetDisk.RequiredBytes(20 * GB, 60 * GB) > 20 * GB + 60 * GB);
    }

    private const string SysprepUnattend = """
        <?xml version="1.0" encoding="utf-8"?>
        <unattend xmlns="urn:schemas-microsoft-com:unattend">
          <settings pass="specialize">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <ComputerName>*</ComputerName>
              <TimeZone>West Asia Standard Time</TimeZone>
            </component>
          </settings>
          <settings pass="oobeSystem">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <OOBE><HideEULAPage>true</HideEULAPage></OOBE>
            </component>
          </settings>
        </unattend>
        """;

    [Fact]
    public void Unattend_gets_computer_name_and_keeps_golden_image_settings()
    {
        var merged = System.Xml.Linq.XDocument.Parse(Unattend.Merge(SysprepUnattend, "PC-07"));
        var ns = Unattend.Ns;
        var specialize = merged.Root!.Elements(ns + "settings").Single(s => (string?)s.Attribute("pass") == "specialize");
        Assert.Equal("PC-07", specialize.Descendants(ns + "ComputerName").Single().Value);
        Assert.Equal("West Asia Standard Time", specialize.Descendants(ns + "TimeZone").Single().Value);
        Assert.Equal("true", merged.Descendants(ns + "HideEULAPage").Single().Value);
    }

    [Fact]
    public void Unattend_is_created_when_image_has_none()
    {
        var merged = System.Xml.Linq.XDocument.Parse(Unattend.Merge(null, "PC-01"));
        Assert.Equal("PC-01", merged.Descendants(Unattend.Ns + "ComputerName").Single().Value);
        Assert.Throws<System.Xml.XmlException>(() => Unattend.Merge("<html/>", "PC-01"));
    }

    [Theory]
    [InlineData("PC-01", 1, "PC-01")]
    [InlineData("vip station 12345678", 3, "VIP-STATION-123")]
    [InlineData("Ноутбук", 7, "PC-07")]
    [InlineData("12345", 12, "PC-12")]
    public void Computer_name_is_a_valid_netbios_name(string name, int seat, string expected) =>
        Assert.Equal(expected, Unattend.ComputerName(name, seat));

    [Theory]
    [InlineData("02-11-22-33-44-0A", "02:11:22:33:44:0a")]
    [InlineData("02:11:22:33:44:0a", "02:11:22:33:44:0a")]
    [InlineData("0211223344", null)]
    public void Mac_from_ipxe_is_normalized(string mac, string? expected) =>
        Assert.Equal(expected, ReimageService.NormalizeMac(mac));
}
