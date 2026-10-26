using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Helper.Tests;

/// <summary>
/// Провод G: помощника 1.6.0 тот же, что у 1.5.2: запросы G: (путь, запрос, тело, порядок) на загрузке, личном диске,
/// мастер-томе на G:, закрытии правки и перезагрузке совпадают с эталоном Golden/g-helper-wire.txt, записанным помощником
/// 1.5.2 (одна HelperLoop, сервер без H:) — и когда рядом работает цикл H: (с правкой H: в те же такты), и когда он спит
/// (сервер без H:), и когда H: выключена в helper.json.
/// </summary>
public sealed class GamesWireTests
{
    private const string Golden = "g-helper-wire.txt";

    private delegate Func<Task> Start(LibrariesStand stand, FakeWindowsStorage windows, WireRecorder recorder);

    private static Task<string[]> GoldenAsync() => File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Golden", Golden));

    private static bool IsLibraryPath(string path) => path.Contains("/libraries/", StringComparison.Ordinal);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G_requests_are_those_of_helper_1_5_2_with_the_h_loop_beside(bool solo)
    {
        var recorder = new WireRecorder();
        Assert.Equal(await GoldenAsync(), await ScenarioAsync(solo, Helper(HelperOptions.DefaultLibraries), recorder));

        var library = recorder.Lines(IsLibraryPath);
        if (solo)
        {
            // H: работала рядом: личный диск H: со сбросом при загрузке, правка H:, отчёты по H:.
            Assert.Contains(library, l => l.Contains("\"targetIqn\":\"iqn.2005-10.org.freenas.ctl:solo-seat-01\"", StringComparison.Ordinal));
            Assert.Contains(library, l => l.Contains("\"master\":{\"state\":\"mounted\",\"targetIqn\":\"iqn.2005-10.org.freenas.ctl:club-master-solo\"", StringComparison.Ordinal));
        }
        else
        {
            // Сервер без H: — один вопрос при старте каждого помощника, 404, сон.
            Assert.Equal(
                ["GET /diskless/v1/machines/{id}/libraries/solo/volume?attached=false -> 404", "GET /diskless/v1/machines/{id}/libraries/solo/volume?attached=false -> 404"],
                library);
        }
    }

    [Fact]
    public async Task G_requests_are_those_of_helper_1_5_2_with_h_turned_off_in_helper_json()
    {
        var recorder = new WireRecorder();
        Assert.Equal(await GoldenAsync(), await ScenarioAsync(solo: true, Helper([]), recorder));
        Assert.DoesNotContain(recorder.Paths, IsLibraryPath);
    }

    private static Start Helper(IReadOnlyList<string> libraries) => (stand, windows, recorder) =>
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.6.0", Libraries = libraries };
        var api = new DisklessApiClient(stand.Http(recorder.Handler()), options, stand.Credentials, stand.Identity);
        var loops = HelperLoops.Create(api, windows, new FakeProcesses(), stand.Identity, options, TimeProvider.System, NullLoggerFactory.Instance,
            stand.Cache);
        return () => loops.TickAsync(CancellationToken.None);
    };

    private static FakeWindowsStorage Windows()
    {
        var windows = new FakeWindowsStorage();
        windows.Folders['G'] = ["Counter-Strike 2", "Steam"];
        windows.Folders['H'] = ["SteamLibrary", "Witcher 3"];
        return windows;
    }

    private static async Task<IReadOnlyList<string>> ScenarioAsync(bool solo, Start start, WireRecorder recorder)
    {
        await using var stand = await LibrariesStand.StartAsync(solo);
        await stand.PublishAsync(stand.Games, "v1");
        if (solo)
        {
            await stand.PublishAsync(stand.Solo, "v1");
        }

        var tick = start(stand, Windows(), recorder);
        await tick(); // загрузка ПК: регистрация, запрос при старте, отчёт, личный диск со сбросом
        await tick();

        await stand.OpenMasterAsync(stand.Games);
        if (solo)
        {
            await stand.OpenMasterAsync(stand.Solo);
        }

        await tick(); // мастер-том на G: вместо личного диска
        await tick();

        await stand.CloseMasterAsync(stand.Games);
        if (solo)
        {
            await stand.CloseMasterAsync(stand.Solo);
        }

        await tick(); // личный диск снова на G:

        tick = start(stand, Windows(), recorder); // перезагрузка
        await tick();
        await tick();
        return recorder.Lines(p => !IsLibraryPath(p));
    }
}
