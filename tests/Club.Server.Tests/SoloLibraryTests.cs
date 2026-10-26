using System.Net;
using System.Text.Json.Nodes;
using Club.Server.Data;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>
/// Вторая библиотека игр <c>solo</c> (H:, hdd) рядом с G: (личный слой): своя схема БД, свои имена в TrueNAS, свой
/// мастер-том и свои предупреждения; ни публикация, ни сброс, ни разборка, ни мастер-том одной библиотеки не трогают
/// другую. Таргеты H: не попадают под метки помощника G: (LibraryNamesTests).
/// </summary>
public sealed class SoloLibraryTests : IAsyncLifetime
{
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string Iqn = "iqn.1991-05.com.microsoft:pc-01";

    /// <summary>Настройки H: как в клубе (deploy/club-server.env.example); общие — из секции Library.</summary>
    public static readonly IReadOnlyDictionary<string, string> SoloSettings = new Dictionary<string, string>
    {
        ["Libraries:solo:Enabled"] = "true",
        ["Libraries:solo:MasterZvol"] = "hdd/club/lib",
        ["Libraries:solo:PersonalParent"] = "hdd/club/seats",
        ["Libraries:solo:PublishedParent"] = "hdd/club/published",
        ["Libraries:solo:DriveLetter"] = "H",
        ["Libraries:solo:MasterDriveLetter"] = "H",
        ["Libraries:solo:PersonalMinHelper"] = "1.6.0",
    };

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private TrueNasStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.UniqueChapUsers = true; // CHAP-пользователи двух библиотек не должны совпасть
        foreach (var dataset in new[] { "games/club", "games/club/published", "games/club/seats", "hdd/club", "hdd/club/published", "hdd/club/seats" })
        {
            _nas.AddFilesystem(dataset);
        }

        _server = new ServerFixture();
        var options = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>(SoloSettings)
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:PersonalGames"] = "true",
            ["Library:MasterZvol"] = "games/club/lib",
            ["Library:PublishedParent"] = "games/club/published",
            ["Library:PersonalParent"] = "games/club/seats",
            ["Library:MasterDriveLetter"] = "G",
            ["Library:PortalAddress"] = "192.168.77.10:3260",
            ["Library:DiscoveryAddress"] = _nas.IscsiPortal,
            ["Library:VerifyDelayMs"] = "0",
            ["Library:MaxAttempts"] = "1",
            ["TrueNas:Host"] = options.Host,
            ["TrueNas:Port"] = options.Port.ToString(),
            ["TrueNas:Username"] = options.Username,
            ["TrueNas:ApiKey"] = options.ApiKey,
            ["TrueNas:CaCertificatePath"] = options.CaCertificatePath,
        })
        {
            _server.Settings[key] = value;
        }

        await _server.InitializeAsync();
        _storage = _server.Services.GetRequiredService<TrueNasStorage>();
        await _storage.EnsureZvolAsync("games/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
        await _storage.EnsureZvolAsync("hdd/club/lib", 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _nas.DisposeAsync();
    }

    private LibrarySet Libraries => _server.Services.GetRequiredService<LibrarySet>();

    private LibrarySlot Games => Libraries.Main;

    private LibrarySlot Solo => Libraries.Find("solo")!;

    private Task PassAsync(LibrarySlot library) => StorageWorker.RunOnceAsync(library, reconcile: true, TimeProvider.System, CancellationToken.None);

    private async Task PublishAsync(LibrarySlot library, string label)
    {
        await library.Publisher.RequestPublishAsync(label, "test");
        await PassAsync(library);
        Assert.Equal("published", (await library.Repository.FindVersionAsync(label))!.State);
    }

    /// <summary>ПК с помощником 1.6.0: отчёт по G: (старый путь) сообщает IQN и версию помощника.</summary>
    private async Task<TestMachine> RegisterAsync(string helperVersion = "1.6.0", string iqn = Iqn, string mac = "02:00:00:00:40:01", object? master = null)
    {
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, mac);
        await ReportAsync(machine, helperVersion, iqn, master);
        return machine;
    }

    private static async Task ReportAsync(TestMachine machine, string helperVersion = "1.6.0", string iqn = Iqn, object? master = null)
    {
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion, volume = new { state = "none" }, initiatorIqn = iqn, master,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpStatusCode> GamesBootAsync(TestMachine machine, bool attached = false)
    {
        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/volume?attached={(attached ? "true" : "false")}");
        return response.StatusCode;
    }

    private async Task<MachineRow> MachineAsync(TestMachine machine) =>
        (await _server.Services.GetRequiredService<MachineRepository>().FindAsync(machine.MachineId))!;

    /// <summary>Запрос при старте помощника к H: — то, что отдаст путь <c>…/libraries/solo/volume</c>.</summary>
    private async Task<Diskless.VolumeAssignment?> SoloBootAsync(TestMachine machine, bool attached = false) =>
        await Solo.Seats.AssignmentAsync(await MachineAsync(machine), atBoot: true, CancellationToken.None, attached);

    private async Task OpenMasterAsync(LibrarySlot library, TestMachine machine)
    {
        await library.Editor.RequestOpenAsync(machine.MachineId, "test");
        await PassAsync(library);
        Assert.Equal("open", (await library.Master.GetAsync()).State);
    }

    private async Task CloseMasterAsync(LibrarySlot library)
    {
        await library.Editor.RequestCloseAsync(force: false, "test");
        await PassAsync(library);
        Assert.Equal("closed", (await library.Master.GetAsync()).State);
    }

    /// <summary>Вызовы TrueNAS, кроме служебных соединения (ping идёт в фоне каждую секунду).</summary>
    private int StorageCalls() => _nas.CallLog.Count(c => c is not ("core.ping" or "auth.login_ex" or "system.version_short"));

    private JsonObject Find(string kind, string field, string value) => _nas.Find(kind, o => o[field]?.GetValue<string>() == value)
        ?? throw new Xunit.Sdk.XunitException($"{kind} {field}={value} not found");

    [Fact]
    public async Task Same_label_in_both_libraries_gets_disjoint_truenas_objects()
    {
        await PublishAsync(Games, "v1");
        await PublishAsync(Solo, "v1");
        var pc = await RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc));
        var volume = (await SoloBootAsync(pc))!;

        // H: — свой личный диск на своей букве, свой CHAP.
        Assert.Equal(("v1", "192.168.77.10:3260", $"{Basename}:solo-seat-01", false, "H", "solo-seat-01"),
            (volume.LibraryVersion, volume.Portal, volume.TargetIqn, volume.ReadOnly, volume.DriveLetter, volume.ChapUser));

        var g = await Games.Repository.FindVersionAsync("v1");
        var h = await Solo.Repository.FindVersionAsync("v1");
        Assert.Equal(("games/club/lib@lib-v1", "games-v1", "lib-v1"), (g!.SnapshotId, g.TargetName, g.ExtentName));
        Assert.Equal(("hdd/club/lib@lib-v1", "hdd/club/published/lib-v1", "solo-v1", "solo-lib-v1"), (h!.SnapshotId, h.CloneId, h.TargetName, h.ExtentName));
        Assert.Equal("solo", (await _storage.GetSnapshotAsync("hdd/club/lib@lib-v1"))!.Labels["clubsrv:library"]);
        Assert.False((await _storage.GetSnapshotAsync("games/club/lib@lib-v1"))!.Labels.ContainsKey("clubsrv:library"));

        var gClone = (await _storage.GetDatasetAsync("games/club/seats/games-seat-01"))!;
        var hClone = (await _storage.GetDatasetAsync("hdd/club/seats/solo-seat-01"))!;
        Assert.Equal(("games/club/lib@lib-v1", "seat-games"), (gClone.Origin, gClone.Labels["clubsrv:role"]));
        Assert.False(gClone.Labels.ContainsKey("clubsrv:library"));
        Assert.Equal(("hdd/club/lib@lib-v1", "seat-solo", "solo"), (hClone.Origin, hClone.Labels["clubsrv:role"], hClone.Labels["clubsrv:library"]));
        Assert.NotNull(await _storage.GetSnapshotAsync("hdd/club/seats/solo-seat-01@clean"));

        Assert.Equal(("zvol/games/club/seats/games-seat-01", "clubsrv personal games seat 01"),
            (Find("extent", "name", "games-seat-01")["disk"]!.GetValue<string>(), Find("extent", "name", "games-seat-01")["comment"]!.GetValue<string>()));
        Assert.Equal(("zvol/hdd/club/seats/solo-seat-01", "clubsrv personal solo seat 01"),
            (Find("extent", "name", "solo-seat-01")["disk"]!.GetValue<string>(), Find("extent", "name", "solo-seat-01")["comment"]!.GetValue<string>()));
        Assert.Equal("games seat 1", Find("target", "name", "games-seat-01")["alias"]!.GetValue<string>());
        Assert.Equal("solo seat 1", Find("target", "name", "solo-seat-01")["alias"]!.GetValue<string>());
        var gAuth = Find("auth", "user", "games-seat-01");
        var hAuth = Find("auth", "user", "solo-seat-01");
        Assert.NotEqual(gAuth["tag"]!.GetValue<int>(), hAuth["tag"]!.GetValue<int>());
        Assert.Equal(volume.ChapSecret, hAuth["secret"]!.GetValue<string>());
        var hGroup = Find("initiator", "comment", "clubsrv solo-seat-01");
        Assert.Equal([Iqn], hGroup["initiators"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        Assert.NotNull(Find("initiator", "comment", "clubsrv games-seat-01"));
        Assert.Equal(hGroup["id"]!.GetValue<int>(), Find("target", "name", "solo-seat-01")["groups"]![0]!["initiator"]!.GetValue<int>());
        Assert.False(_nas.IsOpenToEveryone("solo-seat-01"));
        Assert.False(_nas.IsOpenToEveryone("games-seat-01"));

        // Записи — в своих схемах.
        Assert.Equal("games-seat-01", (await Games.SeatsRepository.FindAsync(pc.MachineId))!.TargetName);
        Assert.Equal("solo-seat-01", (await Solo.SeatsRepository.FindAsync(pc.MachineId))!.TargetName);
    }

    [Fact]
    public async Task Boot_reset_is_decided_per_library()
    {
        await PublishAsync(Games, "v1");
        await PublishAsync(Solo, "v1");
        var pc = await RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc));
        Assert.NotNull(await SoloBootAsync(pc));
        var g = _nas.Rollbacks("games/club/seats/games-seat-01");
        var h = _nas.Rollbacks("hdd/club/seats/solo-seat-01");

        // Перезапуск службы помощника: G: подключён, H: — нет (буква занята, том отпущен).
        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc, attached: true));
        Assert.NotNull(await SoloBootAsync(pc, attached: false));
        Assert.Equal((g, h + 1), (_nas.Rollbacks("games/club/seats/games-seat-01"), _nas.Rollbacks("hdd/club/seats/solo-seat-01")));

        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc, attached: false));
        Assert.NotNull(await SoloBootAsync(pc, attached: true));
        Assert.Equal((g + 1, h + 1), (_nas.Rollbacks("games/club/seats/games-seat-01"), _nas.Rollbacks("hdd/club/seats/solo-seat-01")));

        // Новая версия H: — пересборка только H:.
        await PublishAsync(Solo, "v2");
        var targets = _nas.Count("target");
        Assert.Equal("v2", (await SoloBootAsync(pc))!.LibraryVersion);
        Assert.Equal("hdd/club/lib@lib-v2", (await _storage.GetDatasetAsync("hdd/club/seats/solo-seat-01"))!.Origin);
        Assert.Equal("games/club/lib@lib-v1", (await _storage.GetDatasetAsync("games/club/seats/games-seat-01"))!.Origin);
        Assert.Equal(targets, _nas.Count("target"));
    }

    [Fact]
    public async Task Old_helper_gets_no_h_disk_and_cannot_edit_h()
    {
        await PublishAsync(Games, "v1");
        await PublishAsync(Solo, "v1");
        var pc = await RegisterAsync(helperVersion: "1.5.1");
        var calls = StorageCalls();

        Assert.Null(await SoloBootAsync(pc));
        Assert.Equal(calls, StorageCalls()); // ни одного вызова TrueNAS ради H:
        var error = await Assert.ThrowsAsync<LibraryRequestException>(() => Solo.Editor.RequestOpenAsync(pc.MachineId, "test"));
        Assert.Equal("helperTooOld", error.Reason);
        Assert.Equal("closed", (await Solo.Master.GetAsync()).State);

        // G: — как прежде: правка открывается и для 1.5.1.
        await OpenMasterAsync(Games, pc);
    }

    [Fact]
    public async Task Master_of_one_library_does_not_block_publishing_the_other()
    {
        var pc = await RegisterAsync();
        await OpenMasterAsync(Games, pc);
        Assert.Equal("masterOpen", (await Assert.ThrowsAsync<LibraryRequestException>(() => Games.Publisher.RequestPublishAsync("v1", "test"))).Reason);
        await PublishAsync(Solo, "v1");
        await CloseMasterAsync(Games);

        await OpenMasterAsync(Solo, pc);
        Assert.Equal("masterOpen", (await Assert.ThrowsAsync<LibraryRequestException>(() => Solo.Publisher.RequestPublishAsync("v2", "test"))).Reason);
        await PublishAsync(Games, "v1");
    }

    [Fact]
    public async Task Both_masters_open_on_one_pc_and_close_independently()
    {
        var pc = await RegisterAsync();
        await OpenMasterAsync(Games, pc);
        await OpenMasterAsync(Solo, pc);

        // Свои таргет, экстент, CHAP (свой tag) и группа у каждого мастер-тома.
        Assert.Equal(("club master", "club master solo"),
            (Find("target", "name", "club-master")["alias"]!.GetValue<string>(), Find("target", "name", "club-master-solo")["alias"]!.GetValue<string>()));
        Assert.Equal(("zvol/games/club/lib", "zvol/hdd/club/lib"),
            (Find("extent", "name", "clubsrv-master")["disk"]!.GetValue<string>(), Find("extent", "name", "clubsrv-master-solo")["disk"]!.GetValue<string>()));
        Assert.Equal("clubsrv master solo (superclient)", Find("extent", "name", "clubsrv-master-solo")["comment"]!.GetValue<string>());
        Assert.NotEqual(Find("auth", "user", "clubsrv-master")["tag"]!.GetValue<int>(), Find("auth", "user", "clubsrv-master-solo")["tag"]!.GetValue<int>());
        Assert.NotNull(Find("initiator", "comment", "clubsrv master"));
        Assert.NotNull(Find("initiator", "comment", "clubsrv master solo"));
        Assert.Equal(($"{Basename}:club-master", $"{Basename}:club-master-solo"), ((await Games.Master.GetAsync()).TargetIqn, (await Solo.Master.GetAsync()).TargetIqn));
        Assert.Equal("clubsrv-master-solo", (await Solo.Master.GetAsync()).ChapUser);

        // Помощник сообщил оба мастер-тома: G: — в machines (старый путь), H: — в machine_library.
        await ReportAsync(pc, master: new { state = "mounted", targetIqn = $"{Basename}:club-master", driveLetter = "G" });
        await Solo.Machines!.RecordAsync(pc.MachineId, new VolumeReport("none", null, null, null, null), "mounted", null, DateTimeOffset.UtcNow);

        await CloseMasterAsync(Solo);
        Assert.Equal("open", (await Games.Master.GetAsync()).State);
        Assert.Equal("mounted", (await MachineAsync(pc)).MasterState);
        Assert.Null((await Solo.Machines.FindAsync(pc.MachineId))!.MasterState);
        Assert.NotNull(Find("initiator", "comment", "clubsrv master"));
        Assert.NotNull(Find("target", "name", "club-master"));
        Assert.False(_nas.IsOpenToEveryone("club-master"));
        Assert.Null(_nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master-solo"));
        Assert.Null(_nas.Find("auth", a => a["user"]!.GetValue<string>() == "clubsrv-master-solo"));

        // И наоборот.
        await OpenMasterAsync(Solo, pc);
        await Solo.Machines.RecordAsync(pc.MachineId, new VolumeReport("none", null, null, null, null), "mounted", null, DateTimeOffset.UtcNow);
        await CloseMasterAsync(Games);
        Assert.Equal("open", (await Solo.Master.GetAsync()).State);
        Assert.Null((await MachineAsync(pc)).MasterState);
        Assert.Equal("mounted", (await Solo.Machines.FindAsync(pc.MachineId))!.MasterState);
        Assert.NotNull(Find("initiator", "comment", "clubsrv master solo"));
        Assert.False(_nas.IsOpenToEveryone("club-master-solo"));
        Assert.Null(_nas.Find("target", t => t["name"]!.GetValue<string>() == "club-master"));
    }

    [Fact]
    public async Task Retire_in_h_removes_only_h_objects()
    {
        await PublishAsync(Games, "v1");
        await PublishAsync(Solo, "v1");
        var pc = await RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc));
        await PublishAsync(Solo, "v2");
        await PublishAsync(Solo, "v3");

        Assert.Equal("retired", (await Solo.Repository.FindVersionAsync("v1"))!.State);
        Assert.Null(await _storage.GetSnapshotAsync("hdd/club/lib@lib-v1"));
        Assert.Equal("published", (await Games.Repository.FindVersionAsync("v1"))!.State);
        Assert.NotNull(await _storage.GetSnapshotAsync("games/club/lib@lib-v1"));
        Assert.NotNull(await _storage.GetDatasetAsync("games/club/seats/games-seat-01"));
        Assert.NotNull(Find("target", "name", "games-seat-01"));
        Assert.NotNull(Find("auth", "user", "games-seat-01"));
        Assert.NotNull(Find("initiator", "comment", "clubsrv games-seat-01"));
    }

    [Fact]
    public async Task Warnings_of_the_two_libraries_do_not_collide_or_resolve_each_other()
    {
        foreach (var label in new[] { "v1", "v2" })
        {
            await PublishAsync(Games, label);
            await PublishAsync(Solo, label);
        }

        // Ручной таргет с именем версии и сессией на нём держит разборку v1 в обеих библиотеках: retireBlocked с
        // одинаковой меткой v1 — две записи, а не одна.
        foreach (var name in new[] { "games-v1", "solo-v1" })
        {
            await _storage.EnsureTargetAsync(name, name, 1, 1);
            _nas.AddSession(Iqn, name);
        }

        await PublishAsync(Games, "v3");
        await PublishAsync(Solo, "v3");

        var games = await Games.Repository.ActiveWarningsAsync("library");
        var solo = await Solo.Repository.ActiveWarningsAsync("solo");
        Assert.Equal([("retireBlocked", "v1")], games.Select(w => (w.Kind, w.Subject)).ToArray());
        Assert.Equal([("retireBlocked", "solo:v1")], solo.Select(w => (w.Kind, w.Subject)).ToArray());

        // G: разобрала свою версию — её сверка не закрывает предупреждение H:.
        _nas.ClearSessions();
        await PassAsync(Games);
        Assert.Empty(await Games.Repository.ActiveWarningsAsync("library"));
        Assert.Single(await Solo.Repository.ActiveWarningsAsync("solo"));
        await PassAsync(Solo);
        Assert.Empty(await Solo.Repository.ActiveWarningsAsync("solo"));
    }

    [Fact]
    public async Task Retired_snapshot_held_by_seats_is_reported_until_they_boot()
    {
        await PublishAsync(Solo, "v1");
        var pc = await RegisterAsync();
        Assert.NotNull(await SoloBootAsync(pc));
        await PublishAsync(Solo, "v2");
        await PublishAsync(Solo, "v3"); // v1 разобрана, но её снапшот держит клон места (удаление с defer)

        Assert.Equal("retired", (await Solo.Repository.FindVersionAsync("v1"))!.State);
        Assert.NotNull(await _storage.GetSnapshotAsync("hdd/club/lib@lib-v1"));
        var held = Assert.Single(await Solo.Repository.ActiveWarningsAsync("solo"));
        Assert.Equal(("retiredHeld", "solo:hdd/club/lib@lib-v1"), (held.Kind, held.Subject));
        Assert.Contains("PC-01", held.Message);
        Assert.Empty(await Games.Repository.ActiveWarningsAsync("library"));

        Assert.Equal("v3", (await SoloBootAsync(pc))!.LibraryVersion); // загрузка ПК: диск на v3, снапшот v1 освобождён
        await PassAsync(Solo);
        Assert.Null(await _storage.GetSnapshotAsync("hdd/club/lib@lib-v1"));
        Assert.Empty(await Solo.Repository.ActiveWarningsAsync("solo"));
    }

    [Fact]
    public async Task H_report_writes_only_machine_library_with_the_g_master_rules()
    {
        var pc = await RegisterAsync();
        var before = await MachineAsync(pc);
        var repository = Solo.Machines!;
        var now = DateTimeOffset.UtcNow;
        await repository.RecordAsync(pc.MachineId, new VolumeReport("mounted", $"{Basename}:solo-seat-01", "v1", null, null), "mounted", null, now);
        Assert.Equal(("mounted", "v1", "mounted"), ((await repository.FindAsync(pc.MachineId))!.VolumeState, (await repository.FindAsync(pc.MachineId))!.VolumeVersion,
            (await repository.FindAsync(pc.MachineId))!.MasterState));

        // unknown и незнакомое состояние — прежнее остаётся; нет master — сброс.
        await repository.RecordAsync(pc.MachineId, new VolumeReport("mounted", null, "v1", null, null), "unknown", null, now);
        Assert.Equal("mounted", (await repository.FindAsync(pc.MachineId))!.MasterState);
        await repository.RecordAsync(pc.MachineId, new VolumeReport("mounted", null, "v1", null, null), "hibernating", null, now);
        Assert.Equal("mounted", (await repository.FindAsync(pc.MachineId))!.MasterState);
        await repository.RecordAsync(pc.MachineId, new VolumeReport("failed", null, null, null, new string('x', 3000)), "failed", "drive letter H is in use", now);
        var row = (await repository.FindAsync(pc.MachineId))!;
        Assert.Equal(("failed", 2000, "failed", "drive letter H is in use"), (row.VolumeState, row.VolumeError!.Length, row.MasterState, row.MasterError));
        await repository.RecordAsync(pc.MachineId, new VolumeReport("none", null, null, null, null), null, null, now);
        row = (await repository.FindAsync(pc.MachineId))!;
        Assert.Null(row.MasterState);
        Assert.Null(row.MasterError);

        // Столбцы G: не тронуты.
        var after = await MachineAsync(pc);
        Assert.Equal((before.VolumeState, before.VolumeVersion, before.MasterState, before.LastSeenAt), (after.VolumeState, after.VolumeVersion, after.MasterState, after.LastSeenAt));
        Assert.Single(await repository.AllAsync());
    }

    [Fact]
    public async Task Schema_check_disables_only_a_library_whose_tables_are_not_in_its_schema()
    {
        var main = _server.Services.GetRequiredService<NpgsqlDataSource>();
        Assert.Null(await LibrarySet.SchemaProblemAsync(main, Solo.Database, "solo"));

        // search_path без схемы solo: тот же SQL пошёл бы в таблицы G:.
        await using (var broken = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(main.ConnectionString) { SearchPath = "public" }.ConnectionString))
        {
            Assert.Contains("library_versions resolves to public", await LibrarySet.SchemaProblemAsync(main, broken, "solo"));
        }

        // Схемы нет (миграция откатена): при старте библиотека выключается, G: работает.
        using (var scope = _server.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(2026102301);
        }

        await Libraries.CheckSchemasAsync();
        Assert.Null(Libraries.Find("solo"));
        Assert.False(Libraries.Extras.Single().Options.Enabled);
        var pc = await RegisterAsync();
        Assert.Null(await Libraries.Extras.Single().Seats.AssignmentAsync(await MachineAsync(pc), atBoot: true, CancellationToken.None, attached: false));
        await PublishAsync(Games, "v1");
        Assert.Equal(HttpStatusCode.OK, await GamesBootAsync(pc));
    }

    [Fact]
    public async Task Fake_truenas_refuses_a_second_target_alias_and_guards_chap_users()
    {
        // Без этих проверок совпавшие имена двух библиотек прошли бы в тестах и упали бы только на TrueNAS.
        await _storage.EnsureTargetAsync("solo-v9", "solo v9", 1, 1);
        var alias = await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.EnsureTargetAsync("games-v9", "solo v9", 1, 1));
        Assert.Contains(((TrueNasRpcException)alias.InnerException!).Validation, v => v.Message == "Alias already exists");
        await _storage.EnsureChapAsync(5, "solo-seat-01", "0123456789abcdef");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.EnsureChapAsync(6, "solo-seat-01", "0123456789abcdef"));
    }

    [Fact]
    public async Task Idle_h_pass_makes_no_truenas_calls()
    {
        var calls = StorageCalls();
        await PassAsync(Solo);
        await StorageWorker.RunExtrasAsync(Libraries, reconcile: true, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal(calls, StorageCalls());
    }
}
