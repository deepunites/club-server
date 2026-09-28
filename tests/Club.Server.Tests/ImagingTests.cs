using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Club.Server.Imaging;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Образы Windows и перезаливка по PXE: импорт WIM, публикация и откат, PXE-флаг в резервации Kea (временная база
/// по схеме Kea 3.0), скрипты iPXE, API скрипта заливки WinPE — вся последовательность шагов, сбои и безопасность.
/// </summary>
public sealed class ImagingTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private const string Mac = "02:00:00:00:00:01";
    private const long GB = 1L << 30;
    private readonly ServerFixture _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-images-{Guid.NewGuid():N}");
    private readonly string _pxeRoot = Path.Combine(Path.GetTempPath(), $"club-pxe-{Guid.NewGuid():N}");
    private KeaDatabase _kea = null!;

    private static readonly object NetworkSettings = new
    {
        subnet = "192.168.77.0/24", keaSubnetId = 1, @interface = "eth0", dhcpServer = "192.168.77.1", gateway = "192.168.77.1",
        dnsServers = new[] { "192.168.77.1" }, poolStart = "192.168.77.200", poolEnd = "192.168.77.250", reservedStart = "192.168.77.101",
        leaseTimeSec = 43200,
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "incoming"));
        Directory.CreateDirectory(_pxeRoot);
        _kea = await KeaDatabase.CreateAsync();
        _server.Settings["Panel:AdminToken"] = PanelToken;
        _server.Settings["Kea:Enabled"] = "true";
        _server.Settings["Kea:ConnectionString"] = _kea.ConnectionString;
        _server.Settings["Network:RunWorker"] = "false";
        _server.Settings["Imaging:Enabled"] = "true";
        _server.Settings["Imaging:Root"] = _root;
        _server.Settings["Imaging:PxeRoot"] = _pxeRoot;
        _server.Settings["Imaging:TftpRoot"] = ""; // TFTP проверяется в SecureBootTests
        TestEfi.PxeRoot(_pxeRoot, ImagingUnitTests.WimFixture);
        _server.Settings["Imaging:PublicBaseUrl"] = "http://192.168.77.1:5080";
        _server.Settings["Imaging:RunWorker"] = "false";
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _kea.DisposeAsync();
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_pxeRoot, recursive: true);
    }

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private Task WorkerAsync() => ImagingWorker.RunOnceAsync(_server.Services, CancellationToken.None);

    private async Task<JsonElement> ImagesAsync() =>
        await (await Panel().GetAsync("/panel/api/v1/images")).Content.ReadFromJsonAsync<JsonElement>();

    private async Task ImportAsync(string label, int index = 1, bool publish = true)
    {
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "incoming", $"{label}.wim"));
        using (var import = await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = $"{label}.wim", label, index }))
        {
            Assert.Equal(HttpStatusCode.Accepted, import.StatusCode);
        }

        await WorkerAsync();
        if (publish)
        {
            using var response = await Panel().PostAsync($"/panel/api/v1/images/{label}/publish", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    private async Task<TestMachine> MachineAsync(string hwid = "hw-1", string mac = Mac, object? systemDisk = null)
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), hwid, mac);
        using var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", volume = new { state = "none" },
            systemDisk = systemDisk ?? new { serial = "S5GXNX0T123456", model = "Samsung 980", sizeBytes = 500 * GB, busType = "NVMe" },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        await Network.NetworkWorker.RunOnceAsync(_server.Services, TimeProvider.System, CancellationToken.None); // фоновая синхронизация Kea
        return machine;
    }

    private async Task PrepareAsync()
    {
        (await Panel().PutAsJsonAsync("/panel/api/v1/network/settings", NetworkSettings)).EnsureSuccessStatusCode();
        await ImportAsync("win11-2609");
    }

    private static object Disks(params object[] extra) => new object[]
    {
        new { number = 0, serial = "USB0001", model = "Flash", sizeBytes = 64 * GB, busType = "USB" },
        new { number = 1, serial = "S5GXNX0T123456", model = "Samsung 980", sizeBytes = 500 * GB, busType = "NVMe" },
        new { number = 2, serial = "WD-WX11", model = "WD Blue", sizeBytes = 1000 * GB, busType = "SATA" },
    }.Concat(extra).ToArray();

    private async Task<(HttpStatusCode Status, JsonElement Body)> DeployAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await _server.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> StartAsync(string firmware = "uefi", object? disks = null) =>
        DeployAsync(HttpMethod.Post, "/deploy/v1/start", new { macs = new[] { "02-00-00-00-00-01", "AA:BB:CC:00:00:09" }, firmware, disks = disks ?? Disks() });

    private async Task<string?> KeaClassAsync() => (await _kea.HostsAsync()).Single(h => h.Mac == "020000000001").Classes;

    private async Task<string> PxeScriptAsync(string mac = "02-00-00-00-00-01") =>
        await _server.CreateClient().GetStringAsync($"/pxe/v1/machines/{mac}/boot.ipxe");

    private async Task<JsonElement> MachineViewAsync()
    {
        var overview = await (await Panel().GetAsync("/panel/api/v1/machines")).Content.ReadFromJsonAsync<JsonElement>();
        return overview.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("macAddresses")[0].GetString() == Mac);
    }

    [Fact]
    public async Task Import_moves_hashes_and_describes_the_wim()
    {
        await ImportAsync("win11-2609", publish: false);

        Assert.False(File.Exists(Path.Combine(_root, "incoming", "win11-2609.wim")));
        var stored = Path.Combine(_root, "images", "win11-2609", "install.wim");
        var images = await ImagesAsync();
        var image = Assert.Single(images.GetProperty("images").EnumerateArray());
        Assert.Equal("ready", image.GetProperty("state").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stored))), image.GetProperty("sha256").GetString());
        Assert.Equal("Windows 11 Pro", image.GetProperty("wimImages")[0].GetProperty("name").GetString());
        Assert.False(images.TryGetProperty("current", out _)); // загружен, но не опубликован
        Assert.Empty(images.GetProperty("incoming").EnumerateArray());

        // Та же метка второй раз — конфликт; неизвестный файл и плохая метка — ошибки проверки.
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "incoming", "again.wim"));
        Assert.Equal(HttpStatusCode.Conflict, (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "again.wim", label = "win11-2609" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "../etc/passwd", label = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "again.wim", label = "Win 11!" })).StatusCode);
    }

    [Fact]
    public async Task Import_survives_a_crash_between_move_and_rename()
    {
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "incoming", "a.wim"));
        (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "a.wim", label = "a" })).EnsureSuccessStatusCode();

        // Сервер упал сразу после переноса во временный файл.
        Directory.CreateDirectory(Path.Combine(_root, "images", "a"));
        File.Move(Path.Combine(_root, "incoming", "a.wim"), Path.Combine(_root, "images", "a", "install.wim.partial"));
        await WorkerAsync();

        Assert.Equal("ready", (await ImagesAsync()).GetProperty("images")[0].GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "images", "a", "install.wim")));
    }

    [Fact]
    public async Task Broken_file_or_missing_index_fails_the_import()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "incoming", "junk.wim"), new byte[1024]);
        (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "junk.wim", label = "junk" })).EnsureSuccessStatusCode();
        File.Copy(ImagingUnitTests.WimFixture, Path.Combine(_root, "incoming", "idx.wim"));
        (await Panel().PostAsJsonAsync("/panel/api/v1/images/import", new { file = "idx.wim", label = "idx", index = 5 })).EnsureSuccessStatusCode();
        await WorkerAsync();

        var images = (await ImagesAsync()).GetProperty("images").EnumerateArray().ToDictionary(i => i.GetProperty("label").GetString()!);
        Assert.Equal("failed", images["junk"].GetProperty("state").GetString());
        Assert.Equal("failed", images["idx"].GetProperty("state").GetString());
        Assert.Contains("5", images["idx"].GetProperty("lastError").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Panel().PostAsync("/panel/api/v1/images/idx/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Panel().DeleteAsync("/panel/api/v1/images/idx")).StatusCode);
    }

    [Fact]
    public async Task Only_current_and_rollback_versions_are_kept()
    {
        await ImportAsync("v1");
        await ImportAsync("v2");
        var images = await ImagesAsync();
        Assert.Equal(("v2", "v1"), (images.GetProperty("current").GetString(), images.GetProperty("rollback").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await Panel().PostAsync("/panel/api/v1/images/rollback", null)).StatusCode);
        images = await ImagesAsync();
        Assert.Equal(("v1", "v2"), (images.GetProperty("current").GetString(), images.GetProperty("rollback").GetString()));

        await ImportAsync("v3"); // v1 → откатная, v2 вытеснена и удаляется
        await WorkerAsync();
        Assert.False(Directory.Exists(Path.Combine(_root, "images", "v2")));
        Assert.True(Directory.Exists(Path.Combine(_root, "images", "v1")));
        Assert.Equal(["v3", "v1"], (await ImagesAsync()).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToArray());
    }

    [Fact]
    public async Task Evicted_image_is_kept_while_a_reinstall_uses_it()
    {
        await PrepareAsync();
        var machine = await MachineAsync();
        (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { })).EnsureSuccessStatusCode();
        await ImportAsync("v2");
        await ImportAsync("v3");
        await WorkerAsync();

        Assert.True(Directory.Exists(Path.Combine(_root, "images", "win11-2609")));
        var warnings = (await ImagesAsync()).GetProperty("warnings").EnumerateArray().ToList();
        Assert.Contains(warnings, w => w.GetProperty("kind").GetString() == "imageRetireBlocked");
    }

    [Fact]
    public async Task Full_reinstall_clears_the_pxe_flag_only_after_bcdboot()
    {
        await PrepareAsync();
        var machine = await MachineAsync();

        // Без задания: Kea без класса, iPXE — локальная загрузка.
        Assert.Null(await KeaClassAsync());
        Assert.Contains("exit 1", await PxeScriptAsync());

        using (var request = await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        }

        Assert.Equal("club-reimage", await KeaClassAsync());
        var view = await MachineViewAsync();
        Assert.Equal("reimaging", view.GetProperty("status").GetString());
        Assert.Equal(("requested", "win11-2609"), (view.GetProperty("reimage").GetProperty("state").GetString(), view.GetProperty("reimage").GetProperty("image").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { })).StatusCode);

        var script = await PxeScriptAsync();
        Assert.Contains("kernel /pxe/v1/files/wimboot", script);
        Assert.Contains("initrd /pxe/v1/winpe/club-deploy.ps1 club-deploy.ps1", script);
        Assert.Contains("exit 1", await PxeScriptAsync("02-00-00-00-00-99"));
        var config = await _server.CreateClient().GetFromJsonAsync<JsonElement>("/pxe/v1/winpe/clubdeploy.json");
        Assert.Equal("http://192.168.77.1:5080", config.GetProperty("server").GetString());
        var deployScript = await _server.CreateClient().GetByteArrayAsync("/pxe/v1/winpe/club-deploy.ps1");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, deployScript[..3]); // PowerShell 5.1 читает UTF-8 только с BOM
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_pxeRoot, "wimboot")), await _server.CreateClient().GetByteArrayAsync("/pxe/v1/files/wimboot"));
        Assert.Equal(HttpStatusCode.NotFound, (await _server.CreateClient().GetAsync("/pxe/v1/files/..%2F..%2Fetc%2Fpasswd")).StatusCode);

        // WinPE: план — системный диск по серийному номеру (не флешка и не второй диск).
        var (status, plan) = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, plan.GetProperty("targetDisk").GetProperty("number").GetInt32());
        Assert.Equal(("PC-01", 1, 1), (plan.GetProperty("computerName").GetString(), plan.GetProperty("seat").GetInt32(), plan.GetProperty("attempt").GetInt32()));
        var jobId = plan.GetProperty("jobId").GetGuid();
        var jobBase = $"/deploy/v1/jobs/{jobId}";

        Assert.Equal(HttpStatusCode.NoContent, (await DeployAsync(HttpMethod.Put, $"{jobBase}/progress", new { step = "partition" })).Status);
        // Диск уже стёрт — отмена оставила бы машину без загрузчика.
        Assert.Equal(HttpStatusCode.Conflict, (await Panel().PostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage/cancel", null)).StatusCode);

        // Образ по HTTP с докачкой.
        var wim = await File.ReadAllBytesAsync(ImagingUnitTests.WimFixture);
        using (var range = new HttpRequestMessage(HttpMethod.Get, plan.GetProperty("image").GetProperty("url").GetString()))
        {
            range.Headers.Range = new RangeHeaderValue(1000, null);
            using var partial = await _server.CreateClient().SendAsync(range);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(wim[1000..], await partial.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(plan.GetProperty("image").GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(wim)));

        // Сбой при применении: флаг остаётся, после перезагрузки — вторая попытка с нуля.
        Assert.Equal(HttpStatusCode.NoContent, (await DeployAsync(HttpMethod.Post, $"{jobBase}/fail", new { step = "apply", message = "dism: 0x80070070" })).Status);
        Assert.Equal("club-reimage", await KeaClassAsync());
        Assert.Contains("wimboot", await PxeScriptAsync());
        view = await MachineViewAsync();
        Assert.Equal(("failed", "apply", true), (view.GetProperty("reimage").GetProperty("state").GetString(), view.GetProperty("reimage").GetProperty("failure").GetString(), view.GetProperty("reimage").GetProperty("diskTouched").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await DeployAsync(HttpMethod.Put, $"{jobBase}/progress", new { step = "apply" })).Status);

        // Повторный запуск после сбоя (например, другой версией) вытесняет упавшее задание; диск по-прежнему «тронут»,
        // поэтому отменить нельзя — флаг остаётся.
        using (var again = await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        }

        view = await MachineViewAsync();
        Assert.Equal(("requested", true), (view.GetProperty("reimage").GetProperty("state").GetString(), view.GetProperty("reimage").GetProperty("diskTouched").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await Panel().PostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage/cancel", null)).StatusCode);
        Assert.Equal("club-reimage", await KeaClassAsync());

        (status, plan) = await StartAsync();
        Assert.Equal((HttpStatusCode.OK, 1), (status, plan.GetProperty("attempt").GetInt32()));
        jobBase = $"/deploy/v1/jobs/{plan.GetProperty("jobId").GetGuid()}";
        foreach (var step in new[] { "partition", "download", "verify", "apply", "identity" })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await DeployAsync(HttpMethod.Put, $"{jobBase}/progress", new { step, percent = 50 })).Status);
        }

        var (unattendStatus, unattend) = await DeployAsync(HttpMethod.Post, $"{jobBase}/unattend", new
        {
            existing = """<?xml version="1.0"?><unattend xmlns="urn:schemas-microsoft-com:unattend"><settings pass="oobeSystem"/></unattend>""",
            generalized = true,
        });
        Assert.Equal(HttpStatusCode.OK, unattendStatus);
        Assert.Contains("<ComputerName>PC-01</ComputerName>", unattend.GetProperty("xml").GetString());
        Assert.Contains("oobeSystem", unattend.GetProperty("xml").GetString());

        // До bcdboot флаг на месте.
        Assert.Equal(HttpStatusCode.NoContent, (await DeployAsync(HttpMethod.Put, $"{jobBase}/progress", new { step = "bcdboot" })).Status);
        Assert.Equal("club-reimage", await KeaClassAsync());

        Assert.Equal(HttpStatusCode.OK, (await DeployAsync(HttpMethod.Post, $"{jobBase}/complete")).Status);
        Assert.Null(await KeaClassAsync());
        Assert.Contains("exit 1", await PxeScriptAsync());
        Assert.Equal(HttpStatusCode.OK, (await DeployAsync(HttpMethod.Post, $"{jobBase}/complete")).Status); // повтор после обрыва — не ошибка
        Assert.Equal("booting", (await MachineViewAsync()).GetProperty("reimage").GetProperty("state").GetString());

        // Windows загрузилась, помощник сообщает версию образа — задание выполнено.
        using (var report = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.0.0", volume = new { state = "none" }, imageVersion = "win11-2609",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        view = await MachineViewAsync();
        Assert.Equal(("done", "win11-2609", "online"), (view.GetProperty("reimage").GetProperty("state").GetString(), view.GetProperty("imageVersion").GetString(), view.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Unknown_system_disk_is_never_wiped_without_permission()
    {
        await PrepareAsync();
        var machine = await MachineAsync(systemDisk: new { serial = "OLD-SSD-1", model = "Old", sizeBytes = 256 * GB, busType = "SATA" });
        (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { })).EnsureSuccessStatusCode();

        var (status, body) = await StartAsync();
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("systemDiskNotFound", body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("club-reimage", await KeaClassAsync()); // машина остаётся в PXE, диск не тронут
        var view = await MachineViewAsync();
        Assert.Equal(("failed", false), (view.GetProperty("reimage").GetProperty("state").GetString(), view.GetProperty("reimage").GetProperty("diskTouched").GetBoolean()));

        // Отмена возможна (диск не тронут); повтор с разрешением нового диска: единственный — но их два, отказ.
        Assert.Equal(HttpStatusCode.NoContent, (await Panel().PostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage/cancel", null)).StatusCode);
        Assert.Null(await KeaClassAsync());
        (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { allowNewDisk = true })).EnsureSuccessStatusCode();
        (status, body) = await StartAsync();
        Assert.Equal("ambiguousDisks", body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());

        (status, body) = await StartAsync(disks: new object[] { new { number = 3, serial = "NEW-SSD", model = "New", sizeBytes = 500 * GB, busType = "NVMe" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.GetProperty("targetDisk").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Bios_boot_and_ungeneralized_image_are_refused()
    {
        await PrepareAsync();
        var machine = await MachineAsync();
        (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage", new { })).EnsureSuccessStatusCode();

        var (status, body) = await StartAsync(firmware: "bios");
        Assert.Equal("biosNotSupported", body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());

        (status, body) = await StartAsync();
        var jobBase = $"/deploy/v1/jobs/{body.GetProperty("jobId").GetGuid()}";
        (status, body) = await DeployAsync(HttpMethod.Post, $"{jobBase}/unattend", new { existing = (string?)null, generalized = false });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("notGeneralized", body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        Assert.False((await ImagesAsync()).GetProperty("images")[0].GetProperty("generalized").GetBoolean());
        Assert.Equal("club-reimage", await KeaClassAsync());
    }

    [Fact]
    public async Task Reinstall_needs_a_dhcp_reservation_and_an_image()
    {
        var machine = await MachineAsync();
        var (response, body) = await PanelPostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage");
        Assert.Equal((HttpStatusCode.Conflict, "noImage"), (response, body));

        await ImportAsync("win11-2609");
        (response, body) = await PanelPostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage");
        Assert.Equal((HttpStatusCode.Conflict, "noReservation"), (response, body));

        (await Panel().PutAsJsonAsync("/panel/api/v1/network/settings", NetworkSettings)).EnsureSuccessStatusCode();
        (response, body) = await PanelPostAsync($"/panel/api/v1/machines/{machine.MachineId}/reimage");
        Assert.Equal(HttpStatusCode.Accepted, response);

        // Незнакомому ПК WinPE ничего не делает.
        var (status, error) = await DeployAsync(HttpMethod.Post, "/deploy/v1/start", new { macs = new[] { "02:00:00:00:00:77" }, firmware = "uefi", disks = Disks() });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("notArmed", error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task<(HttpStatusCode, string?)> PanelPostAsync(string path)
    {
        using var response = await Panel().PostAsJsonAsync(path, new { });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? null : JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Kea_config_offers_ipxe_only_through_reservation_class()
    {
        await PrepareAsync();
        var text = await Panel().GetStringAsync("/panel/api/v1/network/kea-dhcp4.conf");
        var dhcp4 = JsonDocument.Parse(text).RootElement.GetProperty("Dhcp4");
        var classes = dhcp4.GetProperty("client-classes").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("ipxe-shim.efi", classes["club-reimage-uefi"].GetProperty("boot-file-name").GetString()); // подписанный shim
        Assert.Equal("192.168.77.1", classes["club-reimage-uefi"].GetProperty("next-server").GetString());
        Assert.Equal("http://192.168.77.1:5080/pxe/v1/boot.ipxe", classes["club-reimage-ipxe"].GetProperty("boot-file-name").GetString());
        Assert.Equal(3, dhcp4.GetProperty("subnet4")[0].GetProperty("evaluate-additional-classes").GetArrayLength());
        Assert.False(dhcp4.TryGetProperty("boot-file-name", out _)); // глобально загрузчик не раздаётся никому
    }

    /// <summary>
    /// Настоящая Kea 3.0 в своём сетевом пространстве (без root): только машина с PXE-флагом получает загрузчик, iPXE —
    /// HTTP-скрипт, остальные — адрес без загрузчика. Нужны KEA_DHCP4 (и LD_LIBRARY_PATH/KEA_HOOKS_PATH для
    /// распакованного пакета) и разрешённые непривилегированные user namespaces; иначе тест ничего не проверяет.
    /// </summary>
    [Fact]
    public async Task Kea_gives_boot_files_only_to_armed_machines()
    {
        if (Environment.GetEnvironmentVariable("KEA_DHCP4") is not { Length: > 0 })
        {
            return;
        }

        await PrepareAsync();
        var armed = await MachineAsync("hw-1", Mac);
        await MachineAsync("hw-2", "02:00:00:00:00:02");
        (await Panel().PostAsJsonAsync($"/panel/api/v1/machines/{armed.MachineId}/reimage", new { })).EnsureSuccessStatusCode();

        var probes = new[] { $"{Mac},7", $"{Mac},7,iPXE", $"{Mac},0", "02:00:00:00:00:02,7", "02:00:00:00:00:09,7" };
        var offers = await ProbeKeaAsync(probes);
        Assert.Equal(("192.168.77.101", "192.168.77.1", "ipxe-shim.efi"), Offer(offers[0]));
        Assert.Equal(("192.168.77.101", "http://192.168.77.1:5080/pxe/v1/boot.ipxe"), (Offer(offers[1]).Ip, Offer(offers[1]).File));
        Assert.Equal("undionly.kpxe", Offer(offers[2]).File);
        Assert.Equal(("192.168.77.102", "0.0.0.0", ""), Offer(offers[3])); // без флага: адрес есть, загрузчика нет
        Assert.Equal(("192.168.77.200", ""), (Offer(offers[4]).Ip, Offer(offers[4]).File)); // незнакомый ПК — из пула

        // Флаг снят (отмена до начала работы с диском) — загрузчика больше нет.
        (await Panel().PostAsync($"/panel/api/v1/machines/{armed.MachineId}/reimage/cancel", null)).EnsureSuccessStatusCode();
        offers = await ProbeKeaAsync([$"{Mac},7"]);
        Assert.Equal(("192.168.77.101", "0.0.0.0", ""), Offer(offers[0]));
    }

    private static (string Ip, string NextServer, string File) Offer(JsonElement offer)
    {
        Assert.True(offer.GetProperty("offer").GetBoolean(), offer.ToString());
        return (offer.GetProperty("yiaddr").GetString()!, offer.GetProperty("siaddr").GetString()!, offer.GetProperty("file").GetString()!);
    }

    private async Task<JsonElement[]> ProbeKeaAsync(string[] probes)
    {
        // Конфиг из панели, подогнанный под тестовое окружение: интерфейс veth, тестовая база Kea, текущий пользователь.
        var config = JsonNode.Parse(await Panel().GetStringAsync("/panel/api/v1/network/kea-dhcp4.conf"))!;
        var dhcp4 = config["Dhcp4"]!;
        dhcp4["interfaces-config"]!["interfaces"] = new JsonArray("v0");
        dhcp4["subnet4"]![0]!["interface"] = "v0";
        dhcp4["hosts-database"]!["name"] = new Npgsql.NpgsqlConnectionStringBuilder(_kea.ConnectionString).Database;
        dhcp4["hosts-database"]!["user"] = Environment.UserName;
        dhcp4["lease-database"] = new JsonObject { ["type"] = "memfile", ["persist"] = false }; // kea-lfc на машине теста нет
        if (Environment.GetEnvironmentVariable("KEA_HOOKS_PATH") is { Length: > 0 } hooks)
        {
            dhcp4["hooks-libraries"]![0]!["library"] = Path.Combine(hooks, "libdhcp_pgsql.so");
        }

        var file = Path.Combine(_root, "kea-dhcp4.conf");
        await File.WriteAllTextAsync(file, config.ToJsonString());
        Assert.True(config.ToJsonString().All(char.IsAscii)); // Kea не принимает \u-escape не-ASCII
        var start = new ProcessStartInfo("unshare", ["-r", "-n", "sh", Path.Combine(AppContext.BaseDirectory, "KeaNetns", "run.sh"), file, .. probes])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {await stderr}\n{await stdout}");
        var results = JsonDocument.Parse(await stdout).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        Assert.True(results.All(r => r.GetProperty("offer").GetBoolean()), await stderr);
        return results;
    }
}
