using System.Net;
using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Помощник 1.6.0 против сервера без H: — сервер старше 1.6.0 (голый 404 на неизвестный путь) или с
/// <c>Libraries__solo__Enabled=false</c> (404 notFound{what:library}). Цикл H: засыпает: ни одного обращения к Windows
/// после ответа, одна запись в журнал, новый вопрос через 10 минут; G: работает как прежде.
/// </summary>
public sealed class LibraryDormancyTests
{
    private const string SoloCategory = "Club.Helper.Core.HelperLoop.solo";

    /// <summary>Сервер до 1.6.0: путей библиотек нет — 404 без тела.</summary>
    private sealed class OldServer : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.Contains("/libraries/", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request })
                : base.SendAsync(request, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_missing_on_the_server_sleeps_without_touching_windows(bool oldServer)
    {
        await using var stand = await LibrariesStand.StartAsync(solo: false);
        var recorder = new WireRecorder();
        var http = oldServer ? stand.Server.CreateDefaultClient(recorder.Handler(), new OldServer()) : stand.Http(recorder.Handler());
        var windows = new FakeWindowsStorage();
        var clock = new ManualClock();
        var loggers = new ListLoggerFactory();
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.6.0" };
        var api = new DisklessApiClient(http, options, stand.Credentials, stand.Identity);
        var helper = HelperLoops.Create(api, windows, new FakeProcesses(), stand.Identity, options, clock, loggers, stand.Cache);
        var solo = Assert.Single(helper.Libraries);
        Assert.Equal("solo", solo.Library);
        await stand.PublishAsync(stand.Games, "v1");

        await helper.Games.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", helper.Games.LastReport!.State);
        var hwid = (await stand.Identity.ReadAsync(CancellationToken.None)).Hwid;
        await stand.Cache("solo").SaveAsync(hwid, new VolumeAssignment("v0", "192.168.77.10:3260", LibrariesStand.SoloSeat, false, "H", "solo-seat-01", "S"), CancellationToken.None);
        var queries = windows.SessionQueries;
        var log = windows.Log.Count;
        var lookups = windows.DiskLookups.Count;

        await solo.TickAsync(CancellationToken.None);

        // Один вопрос при старте (с опросом сессий ради attached) — и сон: ни отчёта, ни дисков, ни входов.
        Assert.True(solo.Dormant);
        Assert.Equal(["GET /diskless/v1/machines/{id}/libraries/solo/volume?attached=false -> 404"], recorder.Lines(p => p.Contains("/libraries/", StringComparison.Ordinal)));
        Assert.Equal(queries + 1, windows.SessionQueries);
        Assert.Equal(log, windows.Log.Count);
        Assert.Equal(lookups, windows.DiskLookups.Count);
        Assert.Null(solo.LastReport);
        Assert.Null(solo.LastMasterReport);
        Assert.True(stand.Cache("solo").HasValue);
        Assert.Null(stand.Cache("solo").Current); // без сервера после перезагрузки H: не подключится из assignment.json.solo
        var entry = Assert.Single(loggers.Of(SoloCategory), e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Library solo is not on the server", entry.Message);

        // Девять минут: такты H: молчат совсем — ни запросов, ни Windows.
        for (var i = 0; i < 9; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await solo.TickAsync(CancellationToken.None);
        }

        Assert.Single(recorder.Paths, p => p.Contains("/libraries/", StringComparison.Ordinal));
        Assert.Equal((queries + 1, log, lookups), (windows.SessionQueries, windows.Log.Count, windows.DiskLookups.Count));

        // Такты помощника целиком: G: отчитывается каждый такт, H: спит.
        var gamesReports = recorder.Paths.Count(p => p.EndsWith("/status", StringComparison.Ordinal));
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);
        Assert.Equal(gamesReports + 2, recorder.Paths.Count(p => p.EndsWith("/status", StringComparison.Ordinal)));
        Assert.Single(recorder.Paths, p => p.Contains("/libraries/", StringComparison.Ordinal));
        Assert.DoesNotContain(windows.Log, l => l.Contains(":solo-", StringComparison.Ordinal) || l.Contains("club-master-solo", StringComparison.Ordinal));

        // Через 10 минут — снова вопрос; снова 404 — снова сон, без новой записи в журнал.
        clock.Advance(TimeSpan.FromMinutes(1));
        var before = windows.SessionQueries;
        await solo.TickAsync(CancellationToken.None);
        Assert.Equal(2, recorder.Paths.Count(p => p.Contains("/libraries/", StringComparison.Ordinal)));
        Assert.Equal(before + 1, windows.SessionQueries);
        Assert.True(solo.Dormant);
        Assert.Single(loggers.Of(SoloCategory), e => e.Level >= LogLevel.Information);
        Assert.Equal("mounted", helper.Games.LastReport!.State);
        Assert.DoesNotContain(loggers.Of("Club.Helper.Core.HelperLoop"), e => e.Level >= LogLevel.Warning);
    }
}
