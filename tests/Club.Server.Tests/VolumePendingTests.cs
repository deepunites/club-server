using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Club.Server.Tests;

/// <summary>
/// «Диск ещё готовится» против «монтировать нечего» (SHELL_CHANGES п. 86): 204 запроса при старте помощника несёт
/// <c>X-Volume-Pending: preparing</c>, пока сервер сбрасывает или пересобирает личный диск места (не уложился в 12 с, IQN
/// ПК ещё неизвестен), и <c>failed</c> после сбоя сборки; ответ на отчёт без тома — поле <c>volumePending</c> с теми же
/// значениями. Монтировать нечего (не опубликовано, помощник старый) — 204 без заголовка и ответ без поля, как раньше.
/// Обе библиотеки: G: (старые пути) и H: (<c>…/libraries/solo/…</c>). Ответы сверяются со спецификацией.
/// </summary>
public sealed class VolumePendingTests : IAsyncLifetime
{
    private const string Header = "X-Volume-Pending";

    private SoloStand _stand = null!;

    public async Task InitializeAsync() => _stand = await SoloStand.StartAsync();

    public async Task DisposeAsync() => await _stand.DisposeAsync();

    private static Task<HttpResponseMessage> GamesBootAsync(TestMachine pc) =>
        pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=false");

    private static string? Pending(HttpResponseMessage response) =>
        response.Headers.TryGetValues(Header, out var values) ? string.Join(",", values) : null;

    private static async Task<JsonElement> AcceptedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Contract.ReadAsync(response, "StatusAccepted");
    }

    private static string? Field(JsonElement reply, string name) => reply.TryGetProperty(name, out var value) ? value.GetString() : null;

    private async Task WaitReadyAsync(Library.LibrarySlot library, TestMachine pc)
    {
        var waited = Stopwatch.StartNew();
        while ((await library.SeatsRepository.FindAsync(pc.MachineId))?.State != "ready")
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "seat build did not finish");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task G_boot_says_preparing_while_the_seat_disk_is_rebuilt_and_nothing_when_unpublished()
    {
        var pc = await _stand.RegisterAsync(1); // G: не опубликована: отчёт без тома

        using (var nothing = await GamesBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);
            Assert.Null(Pending(nothing));
        }

        using (var report = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            var reply = await AcceptedAsync(report);
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.False(reply.TryGetProperty("volumePending", out _));
        }

        await _stand.PublishAsync(_stand.Games, "v1");
        using (var report = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            Assert.False((await AcceptedAsync(report)).TryGetProperty("volumePending", out _)); // диск собран за отчёт
        }

        // Новая версия: при загрузке диск пересобирается, клон ждёт 15 с — дольше 12 с ответа.
        await _stand.PublishAsync(_stand.Games, "v2");
        _stand.Nas.Delay("pool.snapshot.clone", "games/club/lib@lib-v2", TimeSpan.FromSeconds(15));
        using (var boot = await GamesBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
            Assert.Equal("preparing", Pending(boot));
            Assert.False(boot.Headers.Contains("X-Library-Letter-Reserved"));
        }

        await WaitReadyAsync(_stand.Games, pc);
        using (var report = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            var reply = await AcceptedAsync(report);
            Assert.Equal("v2", reply.GetProperty("volume").GetProperty("libraryVersion").GetString());
            Assert.False(reply.TryGetProperty("volumePending", out _));
        }

        using (var boot = await GamesBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode); // сброс к @clean — быстро
            Assert.Null(Pending(boot));
        }
    }

    [Fact]
    public async Task H_report_says_preparing_while_the_seat_disk_is_built_and_nothing_when_unpublished()
    {
        var pc = await _stand.RegisterAsync(1);

        using (var nothing = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode); // H: не опубликована
            Assert.Null(Pending(nothing));
        }

        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport()))
        {
            var reply = await AcceptedAsync(report);
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.False(reply.TryGetProperty("volumePending", out _));
        }

        await _stand.PublishAsync(_stand.Solo, "v1");
        _stand.Nas.Delay("pool.snapshot.clone", "hdd/club/lib@lib-v1", TimeSpan.FromSeconds(15));
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport()))
        {
            var reply = await AcceptedAsync(report);
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.Equal("preparing", Field(reply, "volumePending"));
        }

        await WaitReadyAsync(_stand.Solo, pc);
        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport()))
        {
            var reply = await AcceptedAsync(report);
            Assert.Equal($"{SoloStand.Basename}:solo-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
            Assert.False(reply.TryGetProperty("volumePending", out _));
        }
    }

    [Fact]
    public async Task Failed_build_is_failed_at_the_h_boot_point_and_in_the_g_report()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var pc = await _stand.RegisterAsync(1); // личный диск G: собран
        using (var boot = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
        }

        // Сборка диска H: от v2 падает: снапшота версии нет.
        await _stand.PublishAsync(_stand.Solo, "v2");
        await _stand.Storage.DeleteSnapshotAsync("hdd/club/lib@lib-v2");
        using (var boot = await SoloStand.SoloBootAsync(pc))
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
            Assert.Equal("failed", Pending(boot));
        }

        using (var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport()))
        {
            Assert.Equal("failed", Field(await AcceptedAsync(report), "volumePending")); // повтор — не раньше RetryAfter
        }

        // G: — сбой записан в его записи места (TrueNAS ответил ошибкой при прошлой подготовке).
        await _stand.Games.SeatsRepository.MarkFailedAsync(pc.MachineId, "TrueNAS hiccup", DateTimeOffset.UtcNow);
        using (var report = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            var reply = await AcceptedAsync(report);
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.Equal("failed", Field(reply, "volumePending"));
        }
    }

    [Fact]
    public async Task Unknown_iqn_is_preparing_at_boot_and_old_helpers_see_the_old_204()
    {
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
        var (pc, _) = await TestMachine.RegisterAsync(_stand.Server.CreateClient(), null, "02:00:00:00:41:01"); // помощник 1.0.0

        foreach (var boot in new[] { await GamesBootAsync(pc), await SoloStand.SoloBootAsync(pc) })
        {
            using (boot)
            {
                Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
                Assert.Null(Pending(boot)); // личного слоя у помощника нет — монтировать нечего
            }
        }

        // Гибрид сообщил версию, но не IQN: диск соберётся, когда IQN придёт в отчёте.
        using (var report = await pc.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{pc.MachineId}/status", new { helperVersion = "1.6.0", volume = new { state = "none" } }))
        {
            var reply = await AcceptedAsync(report);
            Assert.False(reply.TryGetProperty("volume", out _));
            Assert.False(reply.TryGetProperty("volumePending", out _)); // в этом отчёте IQN нет — и не придёт
        }

        foreach (var boot in new[] { await GamesBootAsync(pc), await SoloStand.SoloBootAsync(pc) })
        {
            using (boot)
            {
                Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
                Assert.Equal("preparing", Pending(boot));
            }
        }

        using (var report = await SoloStand.GamesStatusAsync(pc, "1.6.0", 1))
        {
            var reply = await AcceptedAsync(report);
            Assert.Equal($"{SoloStand.Basename}:games-seat-01", reply.GetProperty("volume").GetProperty("targetIqn").GetString());
            Assert.False(reply.TryGetProperty("volumePending", out _));
        }
    }
}
