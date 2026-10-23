using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Скрипт заливки WinPE (club-deploy.ps1) в настоящем PowerShell против настоящего HTTP-сервера клуба. Команды Windows —
/// заглушки (WinPE/harness.ps1). Нужен <c>PWSH</c> (путь к pwsh); без него тесты пропускаются (Skip), а не проходят
/// впустую.
/// </summary>
public sealed class DeployScriptTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-deploy-{Guid.NewGuid():N}");
    private KeaDatabase _kea = null!;
    private string _baseUrl = "";

    internal static string? Pwsh => Environment.GetEnvironmentVariable("PWSH") is { Length: > 0 } p ? p : null;

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

    private async Task<Guid> ArmAsync(object? request = null)
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
        (await panel.PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", request ?? new { })).EnsureSuccessStatusCode();
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

    private async Task<JsonElement> ImageAsync() =>
        (await Http(panel: true).GetFromJsonAsync<JsonElement>("/panel/api/v1/images")).GetProperty("images")[0];

    /// <summary>Unattend, который sysprep оставил в Windows\Panther (настройки OOBE эталона).</summary>
    private const string SysprepUnattend = """
        <?xml version="1.0" encoding="utf-8"?>
        <unattend xmlns="urn:schemas-microsoft-com:unattend"><settings pass="oobeSystem"><component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><UILanguage>ru-RU</UILanguage></component></settings></unattend>
        """;

    [PwshFact]
    public async Task Script_installs_windows_and_reboots_only_after_the_flag_is_cleared()
    {
        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_SYSPREP_UNATTEND"] = SysprepUnattend });
        Assert.True(exit == 0 && !calls.Contains("Read-Host"), output);

        // Стёрт только системный диск; порядок: разметка → dism → bcdboot → (записи прошивки) → перезагрузка.
        Assert.Contains("Clear-Disk 1", calls);
        Assert.DoesNotContain("Clear-Disk 0", calls);
        var dism = Array.FindIndex(calls, c => c.StartsWith("dism /Apply-Image", StringComparison.Ordinal));
        var bcdboot = Array.FindIndex(calls, c => c.StartsWith("bcdboot", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(calls, "Clear-Disk 1") < dism && dism < bcdboot && bcdboot < Array.IndexOf(calls, "wpeutil reboot"), string.Join('\n', calls));
        Assert.True(Array.IndexOf(calls, "bcdedit /enum firmware") > bcdboot, string.Join('\n', calls));
        Assert.Contains("/Index:1", calls[dism]);
        Assert.Contains("/f UEFI", calls[bcdboot]);
        Assert.DoesNotContain("/addlast", calls[bcdboot]); // Imaging:BcdbootAddLast по умолчанию выключен
        Assert.Contains(@"reg load HKLM\ClubOfflineSOFTWARE", calls); // ImageState — из куста SOFTWARE залитой Windows
        Assert.Contains("ImageState=IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE, GeneralizationState=7", output);

        // Образ скачан целиком и совпал; личность записана на «диск Windows».
        var letters = calls.Where(c => c.StartsWith("Set-Partition ", StringComparison.Ordinal)).Select(c => c[^1..]).ToArray();
        var (system, windows, temp) = (letters[0], letters[1], letters[2]);
        Assert.Equal(await File.ReadAllBytesAsync(ImagingUnitTests.WimFixture), await File.ReadAllBytesAsync(Path.Combine(_root, "run", temp, "install.wim")));
        var unattend = await File.ReadAllTextAsync(Path.Combine(_root, "run", windows, "Windows", "Panther", "unattend.xml"));
        Assert.Contains("<ComputerName>PC-01</ComputerName>", unattend);
        Assert.Contains("<UILanguage>ru-RU</UILanguage>", unattend); // unattend от sysprep дописан, а не заменён
        var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "run", windows, "ProgramData", "ClubDiskless", "image.json"))).RootElement;
        Assert.Equal("win11-2609", marker.GetProperty("label").GetString());
        Assert.Contains($"/s {system}:", calls[bcdboot]);

        var reimage = await ReimageAsync();
        Assert.Equal(("booting", false, 1), (reimage.GetProperty("state").GetString(), reimage.GetProperty("pxeArmed").GetBoolean(), reimage.GetProperty("attempts").GetInt32()));
        Assert.Null((await _kea.HostsAsync()).Single().Classes);
        Assert.True((await ImageAsync()).GetProperty("generalized").GetBoolean());
    }

    [PwshTheory]
    [InlineData("IMAGE_STATE_COMPLETE")]
    [InlineData("none")]
    public async Task Script_refuses_an_image_without_sysprep(string imageState)
    {
        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_IMAGE_STATE"] = imageState });
        Assert.True(exit == 0, output);
        Assert.Contains("FAILED at step identity (notGeneralized)", output);
        Assert.Contains($"ImageState={imageState}, GeneralizationState=7", output);
        Assert.Contains("Read-Host", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("bcdboot", StringComparison.Ordinal));

        var reimage = await ReimageAsync();
        Assert.Equal(("failed", "notGeneralized", true), (reimage.GetProperty("state").GetString(), reimage.GetProperty("failure").GetString(), reimage.GetProperty("pxeArmed").GetBoolean()));
        Assert.Equal("club-reimage", (await _kea.HostsAsync()).Single().Classes);
        Assert.False((await ImageAsync()).GetProperty("generalized").GetBoolean());
    }

    [PwshFact]
    public async Task Unreadable_registry_refuses_but_does_not_mark_the_image()
    {
        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_REG_LOAD_FAIL"] = "SOFTWARE" });
        Assert.True(exit == 0, output);
        Assert.Contains("FAILED at step identity (notGeneralized)", output);
        Assert.Contains("ImageState=unreadable, GeneralizationState=7", output);
        Assert.DoesNotContain(calls, c => c.StartsWith("bcdboot", StringComparison.Ordinal));

        var reimage = await ReimageAsync();
        Assert.Equal(("failed", "notGeneralized"), (reimage.GetProperty("state").GetString(), reimage.GetProperty("failure").GetString()));
        Assert.Equal("ImageState=unknown, GeneralizationState=7", reimage.GetProperty("message").GetString());
        Assert.False((await ImageAsync()).TryGetProperty("generalized", out _)); // разовый сбой reg load не клеймит образ
    }

    /// <summary>GeneralizationState в сообщении задания: none — куст SYSTEM прочитан, значения нет; unknown — куст не прочитан.</summary>
    [PwshTheory]
    [InlineData("MOCK_GENERALIZATION_STATE", "none", "none", "none")]
    [InlineData("MOCK_REG_LOAD_FAIL", "SYSTEM", "unreadable", "unknown")]
    public async Task Job_message_tells_a_missing_value_from_an_unreadable_system_hive(string mock, string value, string onScreen, string inJob)
    {
        await ArmAsync();
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_IMAGE_STATE"] = "IMAGE_STATE_COMPLETE", [mock] = value });
        Assert.True(exit == 0, output);
        Assert.Contains("FAILED at step identity (notGeneralized)", output);
        Assert.Contains($"ImageState=IMAGE_STATE_COMPLETE, GeneralizationState={onScreen}", output);
        Assert.DoesNotContain(calls, c => c.StartsWith("bcdboot", StringComparison.Ordinal));

        var reimage = await ReimageAsync();
        Assert.Equal(("failed", "notGeneralized"), (reimage.GetProperty("state").GetString(), reimage.GetProperty("failure").GetString()));
        Assert.Equal($"ImageState=IMAGE_STATE_COMPLETE, GeneralizationState={inJob}", reimage.GetProperty("message").GetString());
        Assert.False((await ImageAsync()).GetProperty("generalized").GetBoolean()); // SOFTWARE прочитан: образ помечен
    }

    [PwshFact]
    public async Task Reference_deploy_allows_an_image_without_sysprep_for_its_job_only()
    {
        _server.Services.GetRequiredService<Imaging.ImagingOptions>().BcdbootAddLast = true;
        await ArmAsync(new { allowNotGeneralized = true });
        var (exit, output, calls) = await RunScriptAsync(new() { ["MOCK_IMAGE_STATE"] = "IMAGE_STATE_COMPLETE", ["MOCK_BCDEDIT_EXIT"] = "1" });
        Assert.True(exit == 0 && !calls.Contains("Read-Host"), output);
        Assert.Contains("WARNING: image is not generalized", output);
        var bcdboot = calls.Single(c => c.StartsWith("bcdboot", StringComparison.Ordinal));
        Assert.EndsWith("/f UEFI /addlast", bcdboot);
        Assert.Contains("bcdedit exited with code 1 (ignored)", output); // диагностика заливку не останавливает

        var reimage = await ReimageAsync();
        Assert.Equal(("booting", true), (reimage.GetProperty("state").GetString(), reimage.GetProperty("allowNotGeneralized").GetBoolean()));
        Assert.False((await ImageAsync()).GetProperty("generalized").GetBoolean());
    }

    [PwshFact]
    public async Task Script_failure_is_reported_and_keeps_the_machine_in_pxe()
    {
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

/// <summary>Тест скрипта WinPE в pwsh: без <c>PWSH</c> — пропущен.</summary>
public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute()
    {
        if (DeployScriptTests.Pwsh is null)
        {
            Skip = "PWSH (path to pwsh 7) is not set";
        }
    }
}

/// <summary>То же для теста с данными.</summary>
public sealed class PwshTheoryAttribute : TheoryAttribute
{
    public PwshTheoryAttribute()
    {
        if (DeployScriptTests.Pwsh is null)
        {
            Skip = "PWSH (path to pwsh 7) is not set";
        }
    }
}
