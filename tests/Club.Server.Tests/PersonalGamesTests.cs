using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server.Library;
using Club.TestSupport;
using Club.TrueNas;
using Microsoft.Extensions.DependencyInjection;

namespace Club.Server.Tests;

/// <summary>
/// Личный слой игр (Library:PersonalGames): версия — только снапшот; место получает записываемый клон со своим CHAP,
/// сбрасываемый при старте помощника; новая версия — со следующей загрузки; старые помощники назначения не получают.
/// </summary>
public sealed class PersonalGamesTests : IAsyncLifetime
{
    private const string Master = "tank/club/lib";
    private const string Seats = "tank/club/seats";
    private const string Basename = "iqn.2005-10.org.freenas.ctl";
    private const string Iqn = "iqn.1991-05.com.microsoft:pc-01";

    private FakeTrueNas _nas = null!;
    private ServerFixture _server = null!;
    private LibraryPublisher _publisher = null!;
    private LibraryRepository _repository = null!;
    private TrueNasStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _nas = await FakeTrueNas.StartAsync();
        _nas.AddFilesystem("tank/club");
        _nas.AddFilesystem("tank/club/published");
        _nas.AddFilesystem(Seats);

        _server = new ServerFixture();
        var options = _nas.Options();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Library:Enabled"] = "true",
            ["Library:RunWorker"] = "false",
            ["Library:PersonalGames"] = "true",
            ["Library:PersonalParent"] = Seats,
            ["Library:MasterZvol"] = Master,
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
        _publisher = _server.Services.GetRequiredService<LibraryPublisher>();
        _repository = _server.Services.GetRequiredService<LibraryRepository>();
        _storage = _server.Services.GetRequiredService<TrueNasStorage>();
        await _storage.EnsureZvolAsync(Master, 1L << 40, "64K", new Dictionary<string, string> { ["clubsrv:role"] = "master" });
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        await _nas.DisposeAsync();
    }

    private async Task PublishAsync(string label)
    {
        await _publisher.RequestPublishAsync(label, "test");
        await StorageWorker.RunOnceAsync(_publisher, _repository, reconcile: true, TimeProvider.System, CancellationToken.None);
        Assert.Equal("published", (await _repository.FindVersionAsync(label))!.State);
    }

    private static async Task<JsonElement?> ReportAsync(TestMachine machine, string helperVersion = "1.5.0", string? iqn = Iqn, DateTimeOffset? bootTime = null)
    {
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion, volume = new { state = "none" }, initiatorIqn = iqn, bootTime,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("volume", out var volume) ? volume : null;
    }

    private static async Task<JsonElement?> VolumeAtBootAsync(TestMachine machine)
    {
        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/volume");
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Publish_makes_only_a_snapshot()
    {
        await PublishAsync("v1");
        Assert.NotNull(await _storage.GetSnapshotAsync($"{Master}@lib-v1"));
        Assert.Null(await _storage.GetDatasetAsync("tank/club/published/lib-v1"));
        Assert.Null(await _storage.GetTargetAsync("games-v1"));
        Assert.Equal(0, _nas.Count("extent"));
    }

    [Fact]
    public async Task Seat_labels_are_reserved_for_personal_disks() =>
        await Assert.ThrowsAsync<LibraryRequestException>(() => _publisher.RequestPublishAsync("seat-01", "test"));

    [Fact]
    public async Task Seat_gets_its_own_writable_clone_behind_chap()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        var volume = (await ReportAsync(machine))!.Value;

        Assert.Equal($"{Basename}:games-seat-01", volume.GetProperty("targetIqn").GetString());
        Assert.False(volume.GetProperty("readOnly").GetBoolean());
        Assert.Equal(("games-seat-01", "v1", "G"), (volume.GetProperty("chapUser").GetString(), volume.GetProperty("libraryVersion").GetString(), volume.GetProperty("driveLetter").GetString()));
        var clone = await _storage.GetDatasetAsync($"{Seats}/games-seat-01");
        Assert.Equal(($"{Master}@lib-v1", false), (clone!.Origin, clone.ReadOnly));
        Assert.NotNull(await _storage.GetSnapshotAsync($"{Seats}/games-seat-01@clean"));
        var auth = _nas.Find("auth", a => a["user"]!.GetValue<string>() == "games-seat-01")!;
        Assert.Equal(volume.GetProperty("chapSecret").GetString(), auth["secret"]!.GetValue<string>());
        var group = _nas.Find("initiator", g => g["comment"]!.GetValue<string>() == "clubsrv games-seat-01")!;
        Assert.Equal([Iqn], group["initiators"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        Assert.False(_nas.Find("extent", e => e["name"]!.GetValue<string>() == "games-seat-01")!["ro"]!.GetValue<bool>());
        Assert.False(_nas.IsOpenToEveryone("games-seat-01"));
    }

    [Fact]
    public async Task Boot_resets_the_disk_unless_it_is_in_use()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine);
        var reloads = _nas.Reloads;

        Assert.NotNull(await VolumeAtBootAsync(machine));
        Assert.Equal(1, _nas.Rollbacks($"{Seats}/games-seat-01"));
        Assert.Equal(reloads, _nas.Reloads); // сброс не трогает настройки iSCSI

        _nas.AddSession(Iqn, "games-seat-01"); // служба помощника перезапущена, ПК работает с диском
        Assert.NotNull(await VolumeAtBootAsync(machine));
        Assert.Equal(1, _nas.Rollbacks($"{Seats}/games-seat-01"));
    }

    [Fact]
    public async Task Disk_the_pc_says_it_has_is_not_reset_even_if_truenas_lost_the_session()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine);

        // Служба помощника перезапущена, диск смонтирован, а TrueNAS сессии не показывает (перезапуск iSCSI).
        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/volume?attached=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _nas.Rollbacks($"{Seats}/games-seat-01"));
    }

    [Fact]
    public async Task Connected_disk_is_still_assigned_after_a_failed_preparation()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine);
        _nas.AddSession(Iqn, "games-seat-01");
        await _server.Services.GetRequiredService<SeatGamesRepository>().MarkFailedAsync(machine.MachineId, "TrueNAS hiccup", DateTimeOffset.UtcNow);

        // Пустой ответ заставил бы помощник отпустить диск игрока посреди сеанса.
        Assert.NotNull(await ReportAsync(machine));
        Assert.Equal(0, _nas.Rollbacks($"{Seats}/games-seat-01"));
    }

    [Fact]
    public async Task Disk_connected_from_another_initiator_is_not_reset_or_shared()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine);
        _nas.AddSession("iqn.1991-05.com.microsoft:someone-else", "games-seat-01");

        using var response = await machine.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{machine.MachineId}/volume?attached=false");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, _nas.Rollbacks($"{Seats}/games-seat-01"));
    }

    [Fact]
    public async Task New_version_reaches_the_seat_on_its_next_boot()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine);
        await PublishAsync("v2");

        // ПК работает — назначение то же.
        Assert.Equal("v1", (await ReportAsync(machine))!.Value.GetProperty("libraryVersion").GetString());

        var volume = (await VolumeAtBootAsync(machine))!.Value;
        Assert.Equal("v2", volume.GetProperty("libraryVersion").GetString());
        Assert.Equal($"{Master}@lib-v2", (await _storage.GetDatasetAsync($"{Seats}/games-seat-01"))!.Origin);
        Assert.Equal(1, _nas.Count("target"));
    }

    [Fact]
    public async Task Retired_snapshot_held_by_a_seat_is_reported_until_that_pc_boots()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(machine); // личный диск на v1
        await PublishAsync("v2");
        await PublishAsync("v3"); // v1 разобрана; снапшот удалён с defer — его держит клон места

        Assert.NotNull(await _storage.GetSnapshotAsync($"{Master}@lib-v1"));
        var held = Assert.Single(await _repository.ActiveWarningsAsync(), w => w.Kind == "retiredHeld");
        Assert.Equal($"{Master}@lib-v1", held.Subject);
        Assert.Contains("PC-01", held.Message);

        Assert.Equal("v3", (await VolumeAtBootAsync(machine))!.Value.GetProperty("libraryVersion").GetString());
        await StorageWorker.RunOnceAsync(_publisher, _repository, reconcile: true, TimeProvider.System, CancellationToken.None);
        Assert.Null(await _storage.GetSnapshotAsync($"{Master}@lib-v1"));
        Assert.DoesNotContain(await _repository.ActiveWarningsAsync(), w => w.Kind == "retiredHeld");
    }

    [Fact]
    public async Task Old_helpers_and_unknown_initiators_get_no_personal_disk()
    {
        await PublishAsync("v1");
        var (old, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, "02:00:00:00:30:01");
        Assert.Null(await ReportAsync(old, helperVersion: "1.4.2"));
        Assert.Null(await VolumeAtBootAsync(old));

        var (fresh, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, "02:00:00:00:30:02");
        Assert.Null(await ReportAsync(fresh, iqn: null)); // IQN ещё не прочитан
        Assert.NotNull(await ReportAsync(fresh, iqn: "iqn.1991-05.com.microsoft:pc-02"));
        Assert.Equal(1, _nas.Count("target"));
    }

    private LibraryOptions Options => _server.Services.GetRequiredService<LibraryOptions>();

    /// <summary>Ответ на отчёт целиком (по контракту): том, мастер-том, libraryLetterReserved.</summary>
    private static async Task<JsonElement> StatusReplyAsync(TestMachine machine, string iqn = Iqn)
    {
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", new
        {
            helperVersion = "1.5.1", volume = new { state = "none" }, initiatorIqn = iqn,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Contract.ReadAsync(response, "StatusAccepted");
    }

    private async Task OpenMasterAsync(TestMachine machine)
    {
        await _server.Services.GetRequiredService<MasterEditor>().RequestOpenAsync(machine.MachineId, "test");
        await StorageWorker.RunOnceAsync(_publisher, _repository, reconcile: false, TimeProvider.System, CancellationToken.None);
        Assert.Equal("open", (await _server.Services.GetRequiredService<MasterRepository>().GetAsync()).State);
    }

    [Fact]
    public async Task Master_on_the_library_letter_withholds_the_editing_pcs_games_disk_without_resetting_it()
    {
        Options.MasterDriveLetter = "G"; // = DriveLetter: лаунчеры на ПК правки пишут пути G:\… как на местах
        await PublishAsync("v1");
        var (pc, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        var (other, _) = await TestMachine.RegisterAsync(_server.CreateClient(), null, "02:00:00:00:30:02");
        const string otherIqn = "iqn.1991-05.com.microsoft:pc-02";
        Assert.NotNull(await ReportAsync(pc));
        Assert.NotNull(await ReportAsync(other, iqn: otherIqn));
        var resets = _nas.Rollbacks($"{Seats}/games-seat-01");
        await OpenMasterAsync(pc);

        // ПК правки: мастер-том на G: и явный знак, что тома библиотеки нет намеренно.
        var reply = await StatusReplyAsync(pc);
        Assert.False(reply.TryGetProperty("volume", out _));
        Assert.True(reply.GetProperty("libraryLetterReserved").GetBoolean());
        var master = reply.GetProperty("master");
        Assert.Equal(($"{Basename}:club-master", "G"), (master.GetProperty("targetIqn").GetString(), master.GetProperty("driveLetter").GetString()));

        // Запрос при старте — тоже без тома, и личный диск при этом не сбрасывается (attached=false разрешил бы сброс).
        using (var boot = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=false"))
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
            Assert.Equal(["true"], boot.Headers.GetValues("X-Library-Letter-Reserved"));
        }

        Assert.Equal(resets, _nas.Rollbacks($"{Seats}/games-seat-01"));
        Assert.NotNull(await _storage.GetDatasetAsync($"{Seats}/games-seat-01"));
        Assert.Equal("ready", (await _server.Services.GetRequiredService<SeatGamesRepository>().FindAsync(pc.MachineId))!.State);

        // Другие ПК не затронуты: свой личный диск, ни мастер-тома, ни знака.
        var otherReply = await StatusReplyAsync(other, otherIqn);
        Assert.Equal($"{Basename}:games-seat-02", otherReply.GetProperty("volume").GetProperty("targetIqn").GetString());
        Assert.False(otherReply.TryGetProperty("master", out _));
        Assert.False(otherReply.TryGetProperty("libraryLetterReserved", out _));
        Assert.NotNull(await VolumeAtBootAsync(other));

        // Правку закрыли — ПК правки снова получает свой личный диск.
        await _server.Services.GetRequiredService<MasterEditor>().RequestCloseAsync(force: false, "test");
        await StorageWorker.RunOnceAsync(_publisher, _repository, reconcile: false, TimeProvider.System, CancellationToken.None);
        var after = await StatusReplyAsync(pc);
        Assert.Equal($"{Basename}:games-seat-01", after.GetProperty("volume").GetProperty("targetIqn").GetString());
        Assert.False(after.TryGetProperty("master", out _));
        Assert.False(after.TryGetProperty("libraryLetterReserved", out _));
    }

    [Fact]
    public async Task Master_on_its_own_letter_leaves_the_games_disk_assigned()
    {
        Assert.True(new LibraryOptions { DriveLetter = "G", MasterDriveLetter = "g" }.MasterOnLibraryLetter); // без учёта регистра
        Assert.False(Options.MasterOnLibraryLetter); // по умолчанию M:
        await PublishAsync("v1");
        var (pc, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await ReportAsync(pc);
        await OpenMasterAsync(pc);

        var reply = await StatusReplyAsync(pc);
        Assert.Equal($"{Basename}:games-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
        Assert.Equal("M", reply.GetProperty("master").GetProperty("driveLetter").GetString());
        Assert.False(reply.TryGetProperty("libraryLetterReserved", out _));
        Assert.NotNull(await VolumeAtBootAsync(pc));
    }

    [Fact]
    public async Task Diskless_seat_admits_its_boot_initiator_too()
    {
        await PublishAsync("v1");
        var (machine, _) = await TestMachine.RegisterAsync(_server.CreateClient());
        await _server.Services.GetRequiredService<Diskless.DisklessRepository>().SetBootModeAsync(machine.MachineId, "diskless");

        await ReportAsync(machine);
        var group = _nas.Find("initiator", g => g["comment"]!.GetValue<string>() == "clubsrv games-seat-01")!;
        Assert.Equal(["iqn.1991-05.com.microsoft:pc-01", "iqn.2026-10.local.club:seat-01"],
            group["initiators"]!.AsArray().Select(i => i!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
    }
}
