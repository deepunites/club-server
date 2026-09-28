using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Club.TestSupport;

namespace Club.Server.Tests;

/// <summary>
/// Скрипт заливки WinPE (club-deploy.ps1) в настоящем PowerShell против настоящего HTTP-сервера клуба. Команды Windows —
/// заглушки (WinPE/harness.ps1). Нужен <c>PWSH</c> (путь к pwsh); без него тест ничего не проверяет.
/// </summary>
public sealed class DeployScriptTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-deploy-{Guid.NewGuid():N}");
    private KeaDatabase _kea = null!;
    private string _baseUrl = "";

    private static string? Pwsh => Environment.GetEnvironmentVariable("PWSH") is { Length: > 0 } p ? p : null;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "images", "incoming"));
        Directory.CreateDirectory(Path.Combine(_root, "run", "X", "System32"));
        _kea = await KeaDatabase.CreateAsync();
        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["Kea:Enabled"] = "true";
        _server.Settings["Kea:ConnectionString"] = _kea.ConnectionString;
        _server.Settings["Network:RunWorker"] = "false";
        _server.Settings["Imaging:Enabled"] = "true";
        _server.Settings["Imaging:Root"] = Path.Combine(_root, "images");
        _server.Settings["Imaging:PxeRoot"] = _root;
        _server.Settings["Imaging:TftpRoot"] = "";
        TestEfi.PxeRoot(_root, ImagingUnitTests.WimFixture);
        _server.Settings["Imaging:PublicBaseUrl"] = _baseUrl;
        _server.Settings["Imaging:RunWorker"] = "false";
        _server.UseKestrel(port);
        await _server.InitializeAsync();
        _server.StartServer();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _kea.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private HttpClient Http(bool panel = false)
    {
        var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (panel)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        }

        return http;
    }

    private async Task<Guid> ArmAsync()
    {
        var panel = Http(panel: true);
        (await panel.PutAsJsonAsync("/panel/api/v1/network/settings", new
        {
            subnet = "192.168.77.0/24", keaSubnetId = 1, @interface = "eth0", dhcpServer = "192.168.77.1", gateway = "192.168.77.1",
            dnsServers = new[] { "192.168.77.1" }, poolStart = "192.168.77.200", poolEnd = "192.168.77.250", reservedStart = "192.168.77.101",
            leaseTimeSec = 43200,
        })).EnsureSuccessStatusCode();
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "images", "incoming", "golden.wim"));
        (await panel.PostAsJsonAsync("/panel/api/v1/images/import", new { file = "golden.wim", label = "win11-2609" })).EnsureSuccessStatusCode();
        await Imaging.ImagingWorker.RunOnceAsync(_server.Services, CancellationToken.None);
        (await panel.PostAsync("/panel/api/v1/images/win11-2609/publish", null)).EnsureSuccessStatusCode();

        var (machine, _) = await TestMachine.RegisterAsync(Http(), "hw-1", "02:00:00:00:00:01");
        using (var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", volume = new { state = "none" },
            systemDisk = new { serial = "S5GXNX0T123456", model = "Samsung 980", sizeBytes = 500L << 30, busType = "NVMe" },
        }))
        {
            report.EnsureSuccessStatusCode();
        }

        await Network.NetworkWorker.RunOnceAsync(_server.Services, TimeProvider.System, CancellationToken.None);
        (await panel.PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { })).EnsureSuccessStatusCode();
        return machine.MachineId;
    }

    /// <summary>Как wimboot: скрипт и clubdeploy.json берутся с сервера и кладутся в X:\Windows\System32.</summary>
    private async Task<(int ExitCode, string Output, string[] Calls)> RunScriptAsync(Dictionary<string, string>? env = null)
    {
        var system32 = Path.Combine(_root, "run", "X", "System32");
        await File.WriteAllBytesAsync(Path.Combine(system32, "club-deploy.ps1"), await Http().GetByteArrayAsync("/pxe/v1/winpe/club-deploy.ps1"));
        await File.WriteAllBytesAsync(Path.Combine(system32, "clubdeploy.json"), await Http().GetByteArrayAsync("/pxe/v1/winpe/clubdeploy.json"));
        var start = new ProcessStartInfo(Pwsh!, ["-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "WinPE", "harness.ps1"),
            "-Script", Path.Combine(system32, "club-deploy.ps1"), "-Root", Path.Combine(_root, "run")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in env ?? [])
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var output = await stdout + await stderr;
        var callsFile = Path.Combine(_root, "run", "calls.json");
        var calls = File.Exists(callsFile) ? JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(callsFile))! : [];
        return (process.ExitCode, output, calls);
    }

    private async Task<JsonElement> ReimageAsync() =>
        (await Http(panel: true).GetFromJsonAsync<JsonElement>("/panel/api/v1/machines")).GetProperty("machines")[0].GetProperty("reimage");

    [Fact]
    public async Task Script_installs_windows_and_reboots_only_after_the_flag_is_cleared()
    {
        if (Pwsh is null)
        {
            return;
        }

        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync();
        Assert.True(exit == 0 && !calls.Contains("Read-Host"), output);

        // Стёрт только системный диск; порядок: разметка → dism → bcdboot → перезагрузка.
        Assert.Contains("Clear-Disk 1", calls);
        Assert.DoesNotContain("Clear-Disk 0", calls);
        var dism = Array.FindIndex(calls, c => c.StartsWith("dism /Apply-Image", StringComparison.Ordinal));
        var bcdboot = Array.FindIndex(calls, c => c.StartsWith("bcdboot", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(calls, "Clear-Disk 1") < dism && dism < bcdboot && bcdboot < Array.IndexOf(calls, "wpeutil reboot"), string.Join('\n', calls));
        Assert.Contains("/Index:1", calls[dism]);
        Assert.Contains("/f UEFI", calls[bcdboot]);

        // Образ скачан целиком и совпал; личность записана на «диск Windows».
        var letters = calls.Where(c => c.StartsWith("Set-Partition ", StringComparison.Ordinal)).Select(c => c[^1..]).ToArray();
        var (system, windows, temp) = (letters[0], letters[1], letters[2]);
        Assert.Equal(await File.ReadAllBytesAsync(ImagingUnitTests.WimFixture), await File.ReadAllBytesAsync(Path.Combine(_root, "run", temp, "install.wim")));
        var unattend = await File.ReadAllTextAsync(Path.Combine(_root, "run", windows, "Windows", "Panther", "unattend.xml"));
        Assert.Contains("<ComputerName>PC-01</ComputerName>", unattend);
        var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "run", windows, "ProgramData", "ClubDiskless", "image.json"))).RootElement;
        Assert.Equal("win11-2609", marker.GetProperty("label").GetString());
        Assert.Contains($"/s {system}:", calls[bcdboot]);

        var reimage = await ReimageAsync();
        Assert.Equal(("booting", false, 1), (reimage.GetProperty("state").GetString(), reimage.GetProperty("pxeArmed").GetBoolean(), reimage.GetProperty("attempts").GetInt32()));
        Assert.Null((await _kea.HostsAsync()).Single().Classes);
    }

    [Fact]
    public async Task Script_failure_is_reported_and_keeps_the_machine_in_pxe()
    {
        if (Pwsh is null)
        {
            return;
        }

        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_BCDBOOT_EXIT"] = "1" });
        Assert.True(exit == 0, output);
        Assert.Contains("Read-Host", calls); // ждёт человека, а не перезагружается по кругу
        Assert.Contains("FAILED at step bcdboot", output);

        var reimage = await ReimageAsync();
        Assert.Equal(("failed", "bcdboot", true), (reimage.GetProperty("state").GetString(), reimage.GetProperty("failure").GetString(), reimage.GetProperty("pxeArmed").GetBoolean()));
        Assert.Equal("club-reimage", (await _kea.HostsAsync()).Single().Classes);

        // Вторая попытка после перезагрузки — с нуля и успешно; докачка не нужна, образ уже на временном разделе.
        (exit, output, _) = await RunScriptAsync();
        Assert.True(exit == 0, output);
        reimage = await ReimageAsync();
        Assert.Equal(("booting", 2), (reimage.GetProperty("state").GetString(), reimage.GetProperty("attempts").GetInt32()));
    }
}
