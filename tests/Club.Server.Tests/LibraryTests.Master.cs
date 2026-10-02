using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.TestSupport;

namespace Club.Server.Tests;

/// <summary>
/// Режим суперклиента: мастер-том на запись только одному ПК (CHAP + группа из его IQN), публикация запрещена, пока
/// том открыт; закрытие ждёт отключения ПК и убирает доступ в безопасном порядке.
/// </summary>
public sealed partial class LibraryTests
{
    private const string IqnA = "iqn.1991-05.com.microsoft:pc-01";
    private const string IqnB = "iqn.1991-05.com.microsoft:pc-02";

    private async Task<(TestMachine Machine, JsonElement Reply)> ReportMasterAsync(string hwid, string? iqn, object? master = null)
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), hwid, $"02:00:00:00:10:0{hwid[^1]}");
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.4.0", volume = new { state = "none" }, initiatorIqn = iqn, master,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (machine, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<JsonElement> StatusReplyAsync(TestMachine machine, object? master = null)
    {
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.4.0", volume = new { state = "none" }, master,
        });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpStatusCode Status, string? Reason)> PanelPostAsync(string path, object body)
    {
        using var response = await Panel().PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? null
            : JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
    }

    private async Task<JsonElement> MasterAsync() => (await OverviewAsync()).GetProperty("master");

    private async Task<TestMachine> OpenMasterAsync()
    {
        var (a, _) = await ReportMasterAsync("hw-1", IqnA);
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = a.MachineId }));
        await PassAsync();
        Assert.Equal("open", (await MasterAsync()).GetProperty("state").GetString());
        return a;
    }

    [Fact]
    public async Task Master_is_writable_only_for_the_superclient_pc()
    {
        await PublishAsync("v1");
        var (b, _) = await ReportMasterAsync("hw-2", IqnB);
        var a = await OpenMasterAsync();

        // TrueNAS: RW-экстент мастер-тома, CHAP, группа из одного IQN; таргет не открыт всем.
        var extent = _nas.Find("extent", e => e["name"]!.GetValue<string>() == "clubsrv-master")!;
        Assert.Equal(($"zvol/{Master}", false), (extent["disk"]!.GetValue<string>(), extent["ro"]!.GetValue<bool>()));
        var group = _nas.Find("initiator", g => g["comment"]!.GetValue<string>() == "clubsrv master")!;
        Assert.Equal([IqnA], group["initiators"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        var auth = _nas.Find("auth", x => x["user"]!.GetValue<string>() == "clubsrv-master")!;
        Assert.Equal(16, auth["secret"]!.GetValue<string>().Length);
        var target = _nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master")!;
        var targetGroup = target["groups"]![0]!;
        Assert.Equal(("CHAP", auth["tag"]!.GetValue<int>(), group["id"]!.GetValue<int>()),
            (targetGroup["authmethod"]!.GetValue<string>(), targetGroup["auth"]!.GetValue<int>(), targetGroup["initiator"]!.GetValue<int>()));
        Assert.False(_nas.IsOpenToEveryone("club-master"));

        // Параметры подключения — только суперклиенту.
        var reply = await StatusReplyAsync(a);
        var master = reply.GetProperty("master");
        Assert.Equal($"{Basename}:club-master", master.GetProperty("targetIqn").GetString());
        Assert.Equal(("clubsrv-master", auth["secret"]!.GetValue<string>(), "M", "192.168.77.10:3260"), (
            master.GetProperty("chapUser").GetString(), master.GetProperty("chapSecret").GetString(),
            master.GetProperty("driveLetter").GetString(), master.GetProperty("portal").GetString()));
        Assert.False((await StatusReplyAsync(b)).TryGetProperty("master", out _));

        // Пока мастер-том открыт — ни публикации, ни второго открытия.
        Assert.Equal((HttpStatusCode.Conflict, "masterOpen"), await PanelPostAsync("/panel/api/v1/library/versions", new { label = "v2" }));
        Assert.Equal((HttpStatusCode.Conflict, "masterBusy"), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = b.MachineId }));

        var view = await MasterAsync();
        Assert.Equal(("open", a.MachineId), (view.GetProperty("state").GetString(), view.GetProperty("machineId").GetGuid()));
        var candidates = (await OverviewAsync()).GetProperty("masterCandidates").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
        Assert.Equal(["PC-01", "PC-02"], candidates.Order().ToArray());
    }

    [Fact]
    public async Task Close_waits_for_the_pc_and_removes_access_in_a_safe_order()
    {
        var a = await OpenMasterAsync();
        await StatusReplyAsync(a, new { state = "mounted", targetIqn = $"{Basename}:club-master", driveLetter = "M" });
        Assert.Equal("mounted", (await MasterAsync()).GetProperty("machineState").GetString());
        _nas.AddSession(IqnA, "club-master");

        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/close", new { }));
        Assert.False((await StatusReplyAsync(a)).TryGetProperty("master", out _)); // помощник отключит том

        // ПК ещё подключён — таргет не удаляется, операция ждёт без траты попыток.
        await PassAsync();
        var waiting = await MasterAsync();
        Assert.Equal("closing", waiting.GetProperty("state").GetString());
        Assert.Contains("Waiting", waiting.GetProperty("lastError").GetString());
        Assert.NotNull(_nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master"));
        var closeOp = (await OverviewAsync()).GetProperty("openOperations").EnumerateArray().Single(o => o.GetProperty("kind").GetString() == "masterClose");
        Assert.Equal(0, closeOp.GetProperty("attempts").GetInt32());

        _nas.ClearSessions();
        await StatusReplyAsync(a, new { state = "none" });
        await PassAsync();
        var closed = await MasterAsync();
        Assert.Equal(("closed", false), (closed.GetProperty("state").GetString(), closed.GetProperty("dirty").GetBoolean()));
        Assert.Null(_nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master"));
        Assert.Null(_nas.Find("extent", e => e["name"]!.GetValue<string>() == "clubsrv-master"));
        Assert.Null(_nas.Find("initiator", g => g["comment"]!.GetValue<string>() == "clubsrv master"));
        Assert.Null(_nas.Find("auth", x => x["user"]!.GetValue<string>() == "clubsrv-master"));
        Assert.NotNull(await _storage.GetDatasetAsync(Master)); // сам мастер-том на месте

        // Таргет удалён раньше группы инициаторов и CHAP (иначе TrueNAS открыл бы таргет всем).
        var log = _nas.CallLog.ToList();
        var targetDeleted = log.LastIndexOf("iscsi.target.delete");
        Assert.True(targetDeleted < log.LastIndexOf("iscsi.initiator.delete") && targetDeleted < log.LastIndexOf("iscsi.auth.delete"), string.Join(", ", log));

        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/versions", new { label = "v2" }));
    }

    [Fact]
    public async Task Force_close_leaves_the_master_marked_dirty()
    {
        await OpenMasterAsync();
        _nas.AddSession(IqnA, "club-master"); // ПК пропал, сессия висит

        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/close", new { force = true }));
        await PassAsync();
        var closed = await MasterAsync();
        Assert.Equal(("closed", true), (closed.GetProperty("state").GetString(), closed.GetProperty("dirty").GetBoolean()));

        Assert.Equal((HttpStatusCode.Conflict, "masterDirty"), await PanelPostAsync("/panel/api/v1/library/versions", new { label = "v2" }));
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/versions", new { label = "v2", allowDirtyMaster = true }));
        await PassAsync();

        // Чистое открытие и закрытие снимает пометку.
        _nas.ClearSessions();
        var a = (await ReportMasterAsync("hw-1", IqnA)).Machine;
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = a.MachineId }));
        await PassAsync();
        var firstSecret = (await StatusReplyAsync(a)).GetProperty("master").GetProperty("chapSecret").GetString();
        await PanelPostAsync("/panel/api/v1/library/master/close", new { });
        await PassAsync();
        Assert.False((await MasterAsync()).GetProperty("dirty").GetBoolean());

        // Каждое открытие — новый секрет CHAP.
        await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = a.MachineId });
        await PassAsync();
        Assert.NotEqual(firstSecret, (await StatusReplyAsync(a)).GetProperty("master").GetProperty("chapSecret").GetString());
    }

    [Fact]
    public async Task Master_opens_only_for_a_known_initiator_and_not_during_publish()
    {
        var (noIqn, _) = await ReportMasterAsync("hw-3", iqn: null);
        Assert.Equal((HttpStatusCode.Conflict, "noInitiator"), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = noIqn.MachineId }));

        var (bad, _) = await ReportMasterAsync("hw-4", iqn: "not an iqn; rm -rf");
        Assert.Equal((HttpStatusCode.Conflict, "noInitiator"), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = bad.MachineId }));

        var (a, _) = await ReportMasterAsync("hw-1", IqnA);
        await Panel().PostAsJsonAsync("/panel/api/v1/library/versions", new { label = "v1" }); // публикация ещё не выполнена
        Assert.Equal((HttpStatusCode.Conflict, "publishPending"), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = a.MachineId }));
        await PassAsync();
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/open", new { machineId = a.MachineId }));

        // Закрыть можно и незавершённое открытие — закрытие разберёт то, что успели создать.
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/close", new { }));
        await PassAsync();
        Assert.Equal("closed", (await MasterAsync()).GetProperty("state").GetString());
        Assert.Null(_nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master"));
        Assert.Equal((HttpStatusCode.Conflict, "masterClosed"), await PanelPostAsync("/panel/api/v1/library/master/close", new { }));
    }

    [Fact]
    public async Task Mounted_mark_on_the_pc_does_not_outlive_the_master_volume()
    {
        // Стенд 2026-10-02: мастер-том закрыт, таргета club-master нет, а у ПК суперклиента в панели всё ещё «M: mounted».
        var a = await OpenMasterAsync();
        await StatusReplyAsync(a, new { state = "mounted", targetIqn = $"{Basename}:club-master", driveLetter = "M" });
        Assert.Equal("mounted", await MachineMasterStateAsync(a));

        // Незнакомое состояние (более новый помощник) отметку не меняет.
        await StatusReplyAsync(a, new { state = "flushing" });
        Assert.Equal("mounted", await MachineMasterStateAsync(a));

        // Закрытие; «none» помощник так и не прислал (отчёт потерялся или сессию оборвали без него).
        Assert.Equal((HttpStatusCode.Accepted, null), await PanelPostAsync("/panel/api/v1/library/master/close", new { }));
        await PassAsync();
        Assert.Equal("closed", (await MasterAsync()).GetProperty("state").GetString());
        Assert.Null(await MachineMasterStateAsync(a)); // таргета больше нет — отметка снята сразу, ПК может быть выключен

        // Запоздавший отчёт «mounted» (отправлен ещё до отключения), а дальше — обычные отчёты помощника: без master.
        await StatusReplyAsync(a, new { state = "mounted", targetIqn = $"{Basename}:club-master", driveLetter = "M" });
        Assert.Equal("mounted", await MachineMasterStateAsync(a));
        await StatusReplyAsync(a);
        Assert.Null(await MachineMasterStateAsync(a));
        await StatusReplyAsync(a);
        Assert.Null(await MachineMasterStateAsync(a));
        Assert.False((await MasterAsync()).TryGetProperty("machineState", out _));
    }

    /// <summary>Отметка «мастер-том на ПК» в списке рабочих станций панели.</summary>
    private async Task<string?> MachineMasterStateAsync(TestMachine machine)
    {
        var overview = await Panel().GetFromJsonAsync<JsonElement>("/panel/api/v1/machines");
        var row = overview.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == machine.MachineId);
        return row.TryGetProperty("masterState", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
    }
}
