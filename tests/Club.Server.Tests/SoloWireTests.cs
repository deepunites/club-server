using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Club.Server.Tests;

/// <summary>
/// Провод помощника для H: (<c>…/libraries/solo/volume|status</c>, помощник 1.6.0+): 404 для неизвестной и выключенной
/// библиотеки, 204 и заголовок как у G:, отчёт по H: пишет только <c>solo.machine_library</c>, медленная сборка диска H: не
/// задерживает G:. Ответы сверяются со спецификацией (docs/diskless-api.json).
/// </summary>
public sealed class SoloWireTests : IAsyncLifetime
{
    private SoloStand _stand = null!;

    public async Task InitializeAsync() => _stand = await SoloStand.StartAsync();

    public async Task DisposeAsync() => await _stand.DisposeAsync();

    public static object SoloReport(string state = "none", string? version = null, object? master = null, string[]? contents = null, string helperVersion = "1.6.0") => new
    {
        helperVersion,
        volume = new
        {
            state, libraryVersion = version, targetIqn = version is null ? null : $"{SoloStand.Basename}:solo-seat-01", driveLetter = version is null ? null : "H", contents,
        },
        master,
    };

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Contract.AssertError(body, code);
        return body.GetProperty("error");
    }

    // ---- пути библиотеки ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unknown_g_and_disabled_libraries_are_404_and_another_pc_is_403()
    {
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1);
        var other = await _stand.RegisterAsync(2);

        // games — не ключ: у G: старые пути. Неизвестная библиотека — тот же ответ, что выключенная.
        foreach (var key in new[] { "nope", "games", "Solo" })
        {
            using var boot = await SoloStand.SoloBootAsync(pc, key: key);
            Assert.Equal("library", (await ErrorAsync(boot, HttpStatusCode.NotFound, "notFound")).GetProperty("details").GetProperty("what").GetString());
            using var report = await SoloStand.SoloStatusAsync(pc, SoloReport(), key);
            await ErrorAsync(report, HttpStatusCode.NotFound, "notFound");
        }

        var solo = _stand.Libraries.Extras.Single().Options;
        solo.Enabled = false; // Libraries__solo__Enabled=false или проверка схемы при старте
        try
        {
            using var boot = await SoloStand.SoloBootAsync(pc);
            await ErrorAsync(boot, HttpStatusCode.NotFound, "notFound");
            using var report = await SoloStand.SoloStatusAsync(pc, SoloReport());
            await ErrorAsync(report, HttpStatusCode.NotFound, "notFound");
        }
        finally
        {
            solo.Enabled = true;
        }

        // Чужой машине — 403, как на путях G: (и раньше, чем 404).
        foreach (var key in new[] { "solo", "nope" })
        {
            using var boot = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{other.MachineId}/libraries/{key}/volume");
            await ErrorAsync(boot, HttpStatusCode.Forbidden, "forbidden");
            using var report = await pc.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{other.MachineId}/libraries/{key}/status", SoloReport());
            await ErrorAsync(report, HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.Null(await _stand.Solo.Machines!.FindAsync(other.MachineId));
    }

    [Fact]
    public async Task Boot_point_gives_the_h_disk_once_published_and_never_to_old_helpers()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        var pc = await _stand.RegisterAsync(1);
        var old = await _stand.RegisterAsync(2, helperVersion: "1.5.1");

        using (var nothing = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode); // H: ещё не опубликована
            Assert.False(nothing.Headers.Contains("X-Library-Letter-Reserved"));
        }

        await _stand.PublishAsync(_stand.Solo, "v1");
        using (var boot = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
            var volume = await Contract.ReadAsync(boot, "VolumeAssignment");
            Assert.Equal(("v1", $"{SoloStand.Basename}:solo-seat-01", false, "H", "solo-seat-01"), (
                volume.GetProperty("libraryVersion").GetString(), volume.GetProperty("targetIqn").GetString(), volume.GetProperty("readOnly").GetBoolean(),
                volume.GetProperty("driveLetter").GetString(), volume.GetProperty("chapUser").GetString()));
        }

        using (var report = await SoloStand.SoloStatusAsync(pc, SoloReport("mounted", "v1")))
        {
            var reply = await Contract.ReadAsync(report, "StatusAccepted");
            Assert.Equal($"{SoloStand.Basename}:solo-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
            Assert.False(reply.TryGetProperty("master", out _));
            Assert.False(reply.TryGetProperty("libraryLetterReserved", out _));
        }

        // Помощник 1.5.1 этот путь не зовёт; если позовёт — диска H: нет, и ради него ни одного вызова TrueNAS.
        var calls = _stand.Nas.CallLog.Count;
        using (var refused = await SoloStand.SoloBootAsync(old))
        {
            Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);
        }

        using (var report = await SoloStand.SoloStatusAsync(old, SoloReport(helperVersion: "1.5.1")))
        {
            Assert.False((await Contract.ReadAsync(report, "StatusAccepted")).TryGetProperty("volume", out _));
        }

        Assert.DoesNotContain(_stand.Nas.CallLog.Skip(calls), c => c is not ("core.ping" or "auth.login_ex" or "system.version_short"));
        Assert.Null(await _stand.Solo.SeatsRepository.FindAsync(old.MachineId));
    }

    [Fact]
    public async Task Master_on_the_h_letter_withholds_only_the_h_disk_and_does_not_reset_it()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1);
        using (var boot = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
        }

        var resets = _stand.Nas.Rollbacks("hdd/club/seats/solo-seat-01");
        await _stand.OpenMasterAsync(_stand.Solo, pc);

        using (var boot = await SoloStand.SoloBootAsync(pc, attached: false)) // attached=false разрешил бы сброс
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
            Assert.Equal(["true"], boot.Headers.GetValues("X-Library-Letter-Reserved"));
        }

        Assert.Equal(resets, _stand.Nas.Rollbacks("hdd/club/seats/solo-seat-01"));
        Assert.Equal("ready", (await _stand.Solo.SeatsRepository.FindAsync(pc.MachineId))!.State);

        using (var report = await SoloStand.SoloStatusAsync(pc, SoloReport()))
        {
            var reply = await Contract.ReadAsync(report, "StatusAccepted");
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.True(reply.GetProperty("libraryLetterReserved").GetBoolean());
            var master = reply.GetProperty("master");
            Assert.Equal(($"{SoloStand.Basename}:club-master-solo", "H", "clubsrv-master-solo", "192.168.77.10:3260"), (
                master.GetProperty("targetIqn").GetString(), master.GetProperty("driveLetter").GetString(), master.GetProperty("chapUser").GetString(),
                master.GetProperty("portal").GetString()));
        }

        // G: на этом ПК — как без правки H:.
        using (var games = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            var reply = await Contract.ReadAsync(games, "StatusAccepted");
            Assert.Equal($"{SoloStand.Basename}:games-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
            Assert.False(reply.TryGetProperty("master", out _));
            Assert.False(reply.TryGetProperty("libraryLetterReserved", out _));
        }

        using (var games = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=true"))
        {
            Assert.Equal(HttpStatusCode.OK, games.StatusCode);
            Assert.False(games.Headers.Contains("X-Library-Letter-Reserved"));
        }

        // Правку H: закрыли — личный диск H: снова в ответе.
        await _stand.CloseMasterAsync(_stand.Solo);
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloReport()))
        {
            var reply = await Contract.ReadAsync(report, "StatusAccepted");
            Assert.Equal($"{SoloStand.Basename}:solo-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
            Assert.False(reply.TryGetProperty("libraryLetterReserved", out _));
        }
    }

    [Fact]
    public async Task Unknown_volume_state_or_missing_helper_version_is_rejected()
    {
        var pc = await _stand.RegisterAsync(1);
        using (var response = await SoloStand.SoloStatusAsync(pc, SoloReport("exploded")))
        {
            Assert.Equal("volume.state", (await ErrorAsync(response, HttpStatusCode.BadRequest, "validation")).GetProperty("details").GetProperty("field").GetString());
        }

        using (var response = await SoloStand.SoloStatusAsync(pc, new { volume = new { state = "none" } }))
        {
            Assert.Equal("helperVersion", (await ErrorAsync(response, HttpStatusCode.BadRequest, "validation")).GetProperty("details").GetProperty("field").GetString());
        }

        Assert.Null(await _stand.Solo.Machines!.FindAsync(pc.MachineId));
    }

    [Fact]
    public async Task H_report_writes_only_machine_library()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        var pc = await _stand.RegisterAsync(1);
        using (var games = await pc.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{pc.MachineId}/status", new
        {
            helperVersion = "1.6.0", hostname = "PC-01", initiatorIqn = SoloStand.Iqn(1), dhcpServers = new[] { "192.168.77.1" },
            volume = new { state = "mounted", targetIqn = $"{SoloStand.Basename}:games-seat-01", libraryVersion = "v1", driveLetter = "G" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, games.StatusCode);
        }

        var before = await _stand.MachineAsync(pc);
        await Task.Delay(20); // отчёт H: позже: last_seen сдвинулся бы, если бы он его писал

        // Всё, что пишет отчёт G:, — в теле отчёта H: другое; сервер берёт из него только volume и master.
        using (var report = await SoloStand.SoloStatusAsync(pc, new
        {
            helperVersion = "1.6.0", hostname = "WIN-OTHER", initiatorIqn = "iqn.1991-05.com.microsoft:other", dhcpServers = new[] { "10.0.0.1" },
            imageVersion = "h9", bootTime = DateTimeOffset.UtcNow,
            volume = new { state = "failed", error = "drive letter H: is in use" },
            master = new { state = "failed", error = "no H: for the master" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        var after = await _stand.MachineAsync(pc);
        Assert.Equal(
            (before.LastSeenAt, before.Hostname, before.InitiatorIqn, before.HelperVersion, before.VolumeState, before.VolumeIqn, before.VolumeVersion, before.VolumeError,
                before.MasterState, before.MasterError, before.ImageVersion, before.BootTime, string.Join(",", before.DhcpServers ?? [])),
            (after.LastSeenAt, after.Hostname, after.InitiatorIqn, after.HelperVersion, after.VolumeState, after.VolumeIqn, after.VolumeVersion, after.VolumeError,
                after.MasterState, after.MasterError, after.ImageVersion, after.BootTime, string.Join(",", after.DhcpServers ?? [])));
        Assert.Equal(("mounted", "v1", "PC-01"), (after.VolumeState, after.VolumeVersion, after.Hostname));

        var row = (await _stand.Solo.Machines!.FindAsync(pc.MachineId))!;
        Assert.Equal(("failed", "drive letter H: is in use", "failed", "no H: for the master"), (row.VolumeState, row.VolumeError, row.MasterState, row.MasterError));

        // Мастер-том — правила G:: unknown — без изменений, нет master — сброс.
        using (var unknown = await SoloStand.SoloStatusAsync(pc, SoloReport(master: new { state = "unknown" })))
        {
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        }

        Assert.Equal("failed", (await _stand.Solo.Machines.FindAsync(pc.MachineId))!.MasterState);
        using (var none = await SoloStand.SoloStatusAsync(pc, SoloReport()))
        {
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        }

        row = (await _stand.Solo.Machines.FindAsync(pc.MachineId))!;
        Assert.Equal(("none", null, null), (row.VolumeState, row.MasterState, row.MasterError));
    }

    [Fact]
    public async Task Contents_go_only_to_h_versions()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1);

        // Не смонтирован — состав не принимается.
        using (var mounting = await SoloStand.SoloStatusAsync(pc, SoloReport("mounting", "v1", contents: ["Fake"])))
        {
            Assert.Equal(HttpStatusCode.OK, mounting.StatusCode);
        }

        Assert.Null((await _stand.Solo.Repository.FindVersionAsync("v1"))!.Contents);
        using (var mounted = await SoloStand.SoloStatusAsync(pc, SoloReport("mounted", "v1", contents: ["Witcher 3", "  ", "bad\u0001name", "witcher 3", "SteamLibrary"])))
        {
            Assert.Equal(HttpStatusCode.OK, mounted.StatusCode);
        }

        Assert.Equal(["SteamLibrary", "Witcher 3"], (await _stand.Solo.Repository.FindVersionAsync("v1"))!.Contents);
        Assert.Null((await _stand.Games.Repository.FindVersionAsync("v1"))!.Contents);
    }

    // ---- изоляция ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Сборка личного диска H: на hdd медленная (клон ждёт 15 с — дольше <see cref="Library.SeatGames.AnswerWithin"/>): запрос
    /// при старте по G: на том же ПК в это время отвечает сразу, по H: — 204, а назначение H: приходит потом в ответе на отчёт.
    /// </summary>
    [Fact]
    public async Task Slow_h_seat_build_does_not_delay_the_g_boot_point()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1); // личный диск G: уже собран
        _stand.Nas.Delay("pool.snapshot.clone", "hdd/club/lib@lib-v1", TimeSpan.FromSeconds(15));

        var solo = Stopwatch.StartNew();
        var soloBoot = SoloStand.SoloBootAsync(pc);
        var waited = Stopwatch.StartNew();
        while (!_stand.Nas.CallRecords.Any(c => c.StartsWith("pool.snapshot.clone", StringComparison.Ordinal) && c.Contains("hdd/club/lib@lib-v1", StringComparison.Ordinal)))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "H: seat build did not reach the clone");
            await Task.Delay(50);
        }

        var games = Stopwatch.StartNew();
        using (var boot = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=false"))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
            Assert.Equal($"{SoloStand.Basename}:games-seat-01", (await Contract.ReadAsync(boot, "VolumeAssignment")).GetProperty("targetIqn").GetString());
        }

        using (var status = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        Assert.True(games.Elapsed < TimeSpan.FromSeconds(5), $"G: waited {games.Elapsed} for the H: seat build");
        Assert.False(soloBoot.IsCompleted);

        using (var boot = await soloBoot)
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode); // не готов за 12 с — «пока нет»
            Assert.True(solo.Elapsed >= Library.SeatGames.AnswerWithin - TimeSpan.FromSeconds(1));
        }

        waited.Restart();
        while ((await _stand.Solo.SeatsRepository.FindAsync(pc.MachineId))?.State != "ready")
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "H: seat build did not finish");
            await Task.Delay(200);
        }

        using var report = await SoloStand.SoloStatusAsync(pc, SoloReport());
        Assert.Equal($"{SoloStand.Basename}:solo-seat-01", (await Contract.ReadAsync(report, "StatusAccepted")).GetProperty("volume").GetProperty("targetIqn").GetString());
    }
}
