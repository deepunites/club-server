using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Imaging;
using Club.TestSupport;

namespace Club.Server.Tests;

/// <summary>
/// Подписанная цепочка загрузки для Secure Boot: проверка файлов (TFTP и PxeRoot), отказы там, где загрузка по сети
/// всё равно не пройдёт, и загрузчик Windows с подписью CA 2023 для ПК с отозванным PCA 2011.
/// </summary>
public sealed class SecureBootTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-sb-{Guid.NewGuid():N}");
    private KeaDatabase _kea = null!;

    private string Tftp => Path.Combine(_root, "tftp");

    private string Pxe => Path.Combine(_root, "pxe");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "images", "incoming"));
        Directory.CreateDirectory(Tftp);
        TestEfi.PxeRoot(Pxe, ImagingUnitTests.WimFixture);
        await File.WriteAllBytesAsync(Path.Combine(Tftp, "ipxe-shim.efi"), TestEfi.Signed(Authenticode.MicrosoftUefiCa2011, "sbat,1,SBAT Version,sbat,1,x\nshim.ipxe,1,iPXE,shim,1,x"));
        await File.WriteAllBytesAsync(Path.Combine(Tftp, "ipxe.efi"), TestEfi.Signed(Authenticode.IpxeCa, "sbat,1,SBAT Version,sbat,1,x\nipxe,1,iPXE,ipxe.efi,2.0.0,x"));
        _kea = await KeaDatabase.CreateAsync();
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["Kea:Enabled"] = "true";
        _server.Settings["Kea:ConnectionString"] = _kea.ConnectionString;
        _server.Settings["Network:RunWorker"] = "false";
        _server.Settings["Imaging:Enabled"] = "true";
        _server.Settings["Imaging:Root"] = Path.Combine(_root, "images");
        _server.Settings["Imaging:PxeRoot"] = Pxe;
        _server.Settings["Imaging:TftpRoot"] = Tftp;
        _server.Settings["Imaging:PublicBaseUrl"] = "http://192.168.77.1:5080";
        _server.Settings["Imaging:RunWorker"] = "false";
        await _server.InitializeAsync();

        (await Panel().PutAsJsonAsync("/panel/api/v1/network/settings", new
        {
            subnet = "192.168.77.0/24", keaSubnetId = 1, @interface = "eth0", dhcpServer = "192.168.77.1", gateway = "192.168.77.1",
            dnsServers = new[] { "192.168.77.1" }, poolStart = "192.168.77.200", poolEnd = "192.168.77.250", reservedStart = "192.168.77.101",
            leaseTimeSec = 43200,
        })).EnsureSuccessStatusCode();
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "images", "incoming", "golden.wim"));
        (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "golden.wim", label = "win11-2609" })).EnsureSuccessStatusCode();
        await ImagingWorker.RunOnceAsync(_server.Services, CancellationToken.None);
        (await Panel().PostAsync("/panel/api/v1/images/win11-2609/publish", null)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _kea.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private async Task<Guid> MachineAsync(string hwid, string mac, object? secureBoot)
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), hwid, mac);
        using var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", volume = new { state = "none" }, secureBoot,
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        await Network.NetworkWorker.RunOnceAsync(_server.Services, TimeProvider.System, CancellationToken.None);
        return machine.MachineId;
    }

    private static object SecureBoot(bool enabled = true, bool thirdParty = true, bool ca2023 = true, bool revoked = false) =>
        new { enabled, thirdPartyCa2011 = thirdParty, windowsCa2023 = ca2023, pca2011Revoked = revoked };

    private async Task<(HttpStatusCode Status, string? Reason)> ReimageAsync(Guid machine)
    {
        using var response = await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine}/reimage", new { });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? null
            : JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task<Dictionary<string, JsonElement>> BootFilesAsync()
    {
        var overview = await Panel().GetFromJsonAsync<JsonElement>("/panel/api/v1/images");
        return overview.GetProperty("bootFiles").GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
    }

    [Fact]
    public async Task Boot_files_report_shows_who_signed_what()
    {
        await File.WriteAllBytesAsync(Path.Combine(Pxe, "wimboot"), TestEfi.Signed(Authenticode.IpxeCa)); // не Microsoft
        var files = await BootFilesAsync();

        Assert.Equal("ok", files["ipxe-shim.efi"].GetProperty("status").GetString());
        Assert.Equal([Authenticode.MicrosoftUefiCa2011], files["ipxe-shim.efi"].GetProperty("signedBy").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("ok", files["ipxe.efi"].GetProperty("status").GetString());
        Assert.Equal("ipxe.efi 2.0.0", files["ipxe.efi"].GetProperty("component").GetString());
        Assert.Equal("wrongSigner", files["wimboot"].GetProperty("status").GetString());
        Assert.Equal(("missing", false), (files["undionly.kpxe"].GetProperty("status").GetString(), files["undionly.kpxe"].GetProperty("required").GetBoolean()));
        Assert.Equal("missing", files["boot/bootx64.efi"].GetProperty("status").GetString());
        Assert.Equal("Windows 11 Pro 10.0.26100.4652", files["sources/boot.wim"].GetProperty("component").GetString());

        // Испорченный shim (один байт) — подпись не сходится.
        var shim = await File.ReadAllBytesAsync(Path.Combine(Tftp, "ipxe-shim.efi"));
        shim[0x210] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(Tftp, "ipxe-shim.efi"), shim);
        Assert.Equal("badSignature", (await BootFilesAsync())["ipxe-shim.efi"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_boot_files_block_every_reinstall()
    {
        File.Delete(Path.Combine(Pxe, "sources", "boot.wim"));
        var machine = await MachineAsync("hw-1", "02:00:00:00:00:01", secureBoot: null);
        Assert.Equal((HttpStatusCode.Conflict, "bootFilesMissing"), await ReimageAsync(machine));
    }

    [Fact]
    public async Task Secure_boot_without_the_third_party_ca_is_refused()
    {
        var machine = await MachineAsync("hw-1", "02:00:00:00:00:01", SecureBoot(thirdParty: false));
        Assert.Equal((HttpStatusCode.Conflict, "secureBootThirdPartyCa"), await ReimageAsync(machine));
    }

    [Fact]
    public async Task Unsigned_ipxe_blocks_only_secure_boot_machines()
    {
        // Типичная ошибка: ipxe.efi из пакета Ubuntu (не подписан) вместо подписанного из релиза iPXE.
        await File.WriteAllBytesAsync(Path.Combine(Tftp, "ipxe.efi"), TestEfi.Pe());
        Assert.Equal("unsigned", (await BootFilesAsync())["ipxe.efi"].GetProperty("status").GetString());

        var secure = await MachineAsync("hw-1", "02:00:00:00:00:01", SecureBoot());
        Assert.Equal((HttpStatusCode.Conflict, "bootFilesNotSigned"), await ReimageAsync(secure));

        var legacy = await MachineAsync("hw-2", "02:00:00:00:00:02", SecureBoot(enabled: false));
        Assert.Equal((HttpStatusCode.Accepted, null), await ReimageAsync(legacy));
        var unknown = await MachineAsync("hw-3", "02:00:00:00:00:03", secureBoot: null); // старый помощник: только файлы
        Assert.Equal((HttpStatusCode.Accepted, null), await ReimageAsync(unknown));
    }

    [Fact]
    public async Task Revoked_pca2011_gets_the_ca2023_boot_manager()
    {
        var revoked = await MachineAsync("hw-1", "02:00:00:00:00:01", SecureBoot(revoked: true));
        var current = await MachineAsync("hw-2", "02:00:00:00:00:02", SecureBoot());
        Assert.Equal((HttpStatusCode.Conflict, "needsCa2023BootManager"), await ReimageAsync(revoked));

        // Загрузчик, подписанный старым PCA 2011, не годится — нужен Windows UEFI CA 2023.
        await File.WriteAllBytesAsync(Path.Combine(Pxe, "boot", "bootx64.efi"), TestEfi.Signed(Authenticode.WindowsPca2011));
        Assert.Equal("wrongSigner", (await BootFilesAsync())["boot/bootx64.efi"].GetProperty("status").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "needsCa2023BootManager"), await ReimageAsync(revoked));

        await File.WriteAllBytesAsync(Path.Combine(Pxe, "boot", "bootx64.efi"), TestEfi.Signed(Authenticode.WindowsUefiCa2023));
        Assert.Equal((HttpStatusCode.Accepted, null), await ReimageAsync(revoked));
        Assert.Equal((HttpStatusCode.Accepted, null), await ReimageAsync(current));

        var revokedScript = await _server.CreateClient().GetStringAsync("/pxe/v1/machines/02-00-00-00-00-01/boot.ipxe");
        Assert.Contains("initrd /pxe/v1/files/boot/bootx64.efi bootx64.efi", revokedScript);
        Assert.True(revokedScript.IndexOf("bootx64.efi", StringComparison.Ordinal) < revokedScript.IndexOf("boot.wim", StringComparison.Ordinal));
        var currentScript = await _server.CreateClient().GetStringAsync("/pxe/v1/machines/02-00-00-00-00-02/boot.ipxe");
        Assert.DoesNotContain("bootx64.efi", currentScript);
        Assert.Contains("initrd /pxe/v1/files/sources/boot.wim boot.wim", currentScript);

        var machines = await Panel().GetFromJsonAsync<JsonElement>("/panel/api/v1/machines");
        var view = machines.GetProperty("machines").EnumerateArray().First(m => m.GetProperty("macAddresses")[0].GetString() == "02:00:00:00:00:01");
        Assert.True(view.GetProperty("secureBoot").GetProperty("pca2011Revoked").GetBoolean());
    }

    /// <summary>
    /// Настоящие файлы через scripts/pxe/install-boot-files.sh (закреплённые версии и sha256): после установки вся
    /// цепочка зелёная. Нужны IPXE_BUNDLE_TGZ (ipxeboot.tar.gz релиза iPXE v2.0.0) и WIMBOOT (wimboot v2.9.0).
    /// </summary>
    [Fact]
    public async Task Install_script_puts_a_fully_signed_chain_in_place()
    {
        if (Environment.GetEnvironmentVariable("IPXE_BUNDLE_TGZ") is not { Length: > 0 } bundle || Environment.GetEnvironmentVariable("WIMBOOT") is not { Length: > 0 } wimboot)
        {
            return;
        }

        foreach (var file in Directory.GetFiles(Tftp))
        {
            File.Delete(file);
        }

        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "pxe", "install-boot-files.sh"));
        var start = new ProcessStartInfo("sh", [script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["TFTP_ROOT"] = Tftp;
        start.Environment["PXE_ROOT"] = Pxe;
        start.Environment["IPXE_BUNDLE_URL"] = "file://" + bundle;
        start.Environment["WIMBOOT_URL"] = "file://" + wimboot;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output);

        var files = await BootFilesAsync();
        foreach (var name in new[] { "ipxe-shim.efi", "ipxe.efi", "undionly.kpxe", "wimboot" })
        {
            Assert.True(files[name].GetProperty("status").GetString() == "ok", $"{name}: {files[name]}");
        }

        Assert.Equal("wimboot v2.9.0", files["wimboot"].GetProperty("component").GetString());
        Assert.Contains(Authenticode.MicrosoftUefiCa2023, files["wimboot"].GetProperty("signedBy").EnumerateArray().Select(e => e.GetString()));
        var machine = await MachineAsync("hw-1", "02:00:00:00:00:01", SecureBoot());
        Assert.Equal((HttpStatusCode.Accepted, null), await ReimageAsync(machine));
    }
}
