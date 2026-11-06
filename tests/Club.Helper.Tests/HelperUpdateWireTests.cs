using System.Net;
using System.Security.Cryptography;
using System.Text;
using Club.Helper.Core;
using Club.Server.HelperUpdates;
using Club.Server.Imaging;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using HelperUpdateOffer = Club.Helper.Core.HelperUpdateOffer;
using HelperUpdateReport = Club.Helper.Core.HelperUpdateReport;

namespace Club.Helper.Tests;

/// <summary>
/// Самообновление помощника на проводе (spec 2026-11-06, п. 3.4, 4.2) против настоящего сервера клуба: поручение в ответе на
/// отчёт G: разбирается и запоминается циклом (<see cref="HelperLoop.LastAccepted"/>); отчёт G: несёт <c>helperBuild</c> и
/// <c>helperUpdate</c>, регистрация — <c>helperBuild</c>, отчёт H: — ни того ни другого; сборка скачивается без токена
/// машины с SHA256 по ходу и не больше заданного размера. Провод 1.5.1/1.5.2 (GamesWireTests) — без изменений.
/// </summary>
public sealed class HelperUpdateWireTests : IDisposable
{
    private const string Build181 = "ClubDisklessHelper-1.8.1.exe";
    private const string OwnBuild = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] Content181 = Encoding.UTF8.GetBytes("helper 1.8.1 " + new string('x', 200_000));

    private readonly string _pxe = Path.Combine(Path.GetTempPath(), $"club-helperwire-{Guid.NewGuid():N}");

    public HelperUpdateWireTests() => Directory.CreateDirectory(Path.Combine(_pxe, "tools"));

    public void Dispose()
    {
        if (Directory.Exists(_pxe))
        {
            Directory.Delete(_pxe, recursive: true);
        }
    }

    private string Destination => Path.Combine(_pxe, "ClubDisklessHelper.exe.new");

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static HelperOptions Options(IReadOnlyList<string> libraries) => new()
    {
        ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.8.0", HelperBuild = OwnBuild, Libraries = libraries,
    };

    /// <summary>Сервер клуба раздаёт сборки из <c>&lt;Imaging:PxeRoot&gt;/tools</c> этого теста (файл старше 60 с).</summary>
    private async Task<LibrariesStand> StandAsync(bool solo)
    {
        var stand = await LibrariesStand.StartAsync(solo);
        stand.Server.Services.GetRequiredService<ImagingOptions>().PxeRoot = _pxe;
        var path = Path.Combine(_pxe, "tools", Build181);
        await File.WriteAllBytesAsync(path, Content181);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
        return stand;
    }

    private static HelperLoops Loops(LibrariesStand stand, DisklessApiClient api, HelperOptions options, Func<HelperUpdateReport?>? updateReport = null,
        FakeWindowsStorage? windows = null) =>
        HelperLoops.Create(api, windows ?? new FakeWindowsStorage(), new FakeProcesses(), stand.Identity, options, TimeProvider.System, NullLoggerFactory.Instance,
            stand.Cache, updateReport: updateReport);

    private static async Task<int> NumberAsync(LibrariesStand stand) => (await stand.MachineAsync()).Number;

    /// <summary>Считает запросы и запоминает, был ли у них токен машины.</summary>
    private sealed class Requests : DelegatingHandler
    {
        public List<(string Path, bool Authorized)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization is not null));
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Поддельный сервер раздачи: тело — <paramref name="body"/> без Content-Length (поток идёт, пока не кончится), с
    /// <paramref name="length"/> — с заголовком; <paramref name="breakAfter"/> — обрыв соединения после стольких байт;
    /// <paramref name="cancel"/> — отменяется после первого куска (вызывающий бросил загрузку на середине).
    /// </summary>
    private sealed class FakeFiles(
        byte[] body, bool length = false, int? breakAfter = null, HttpStatusCode status = HttpStatusCode.OK, CancellationTokenSource? cancel = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            HttpContent content = length ? new ByteArrayContent(body) : new StreamContent(new Chunks(body, breakAfter, cancel));
            return Task.FromResult(new HttpResponseMessage(status) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>
    /// Поток без длины и перемотки, по 4 КБ за чтение; после <paramref name="breakAfter"/> байт — обрыв сети; после первого
    /// куска — отмена <paramref name="cancel"/>.
    /// </summary>
    private sealed class Chunks(byte[] body, int? breakAfter, CancellationTokenSource? cancel = null) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (breakAfter is { } limit && _position >= limit)
            {
                throw new IOException("connection reset by peer");
            }

            var n = Math.Min(Math.Min(count, 4096), body.Length - _position);
            Array.Copy(body, _position, buffer, offset, n);
            _position += n;
            cancel?.Cancel();
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static DisklessApiClient FakeClient(HttpMessageHandler files) =>
        new(new HttpClient(new FakeFiles([])) { BaseAddress = new Uri("https://club-server/") }, Options([]), new InMemoryCredentialStore(),
            new FakeIdentity("hwid"), downloads: new HttpClient(files) { BaseAddress = new Uri("https://club-server/") });

    [Fact]
    public async Task StatusReply_WithHelperUpdate_Parsed()
    {
        await using var stand = await StandAsync(solo: false);
        var api = new DisklessApiClient(stand.Http(), Options([]), stand.Credentials, stand.Identity);
        var loops = Loops(stand, api, Options([]));

        Assert.Null(loops.Games.LastAccepted);
        Assert.Equal(0, loops.Games.AcceptedReports);
        await loops.TickAsync(CancellationToken.None);
        Assert.NotNull(loops.Games.LastAccepted);
        Assert.Null(loops.Games.LastAccepted!.HelperUpdate);
        var accepted = loops.Games.AcceptedReports;
        Assert.True(accepted >= 1);

        var queue = stand.Server.Services.GetRequiredService<HelperUpdateRepository>();
        Assert.Equal([await NumberAsync(stand)], (await queue.LaunchAsync(Build181, [await NumberAsync(stand)], false, "panel", DateTimeOffset.UtcNow)).Queued);
        await loops.TickAsync(CancellationToken.None);

        var job = (await queue.ListAsync(DateTimeOffset.UtcNow)).Single();
        Assert.Equal(HelperUpdateCodes.States.Active, job.State);
        Assert.Equal(new HelperUpdateOffer(job.Id, "1.8.1", Sha(Content181), Content181.Length, "pxe/v1/files/tools/" + Build181),
            loops.Games.LastAccepted!.HelperUpdate);
        Assert.True(loops.Games.AcceptedReports > accepted);

        // Отменили — следующий принятый ответ без поручения.
        Assert.True(await queue.CancelAsync(job.Id, DateTimeOffset.UtcNow));
        await loops.TickAsync(CancellationToken.None);
        Assert.Null(loops.Games.LastAccepted!.HelperUpdate);
    }

    [Fact]
    public async Task G_Report_CarriesHelperBuildAndUpdate()
    {
        var recorder = new WireRecorder();
        await using var stand = await StandAsync(solo: true);
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var options = Options(HelperOptions.DefaultLibraries);
        var api = new DisklessApiClient(stand.Http(recorder.Handler()), options, stand.Credentials, stand.Identity);
        HelperUpdateReport? report = null;
        var loops = Loops(stand, api, options, () => report);

        await loops.TickAsync(CancellationToken.None);
        report = new HelperUpdateReport(Guid.NewGuid().ToString(), "waiting", Reason: "session");
        await loops.TickAsync(CancellationToken.None);
        report = new HelperUpdateReport(Guid.NewGuid().ToString(), "failed", Error: "hashMismatch", Message: "SHA256 of .new is 00ff");
        await loops.TickAsync(CancellationToken.None);

        var g = recorder.Lines(p => !p.Contains("/libraries/", StringComparison.Ordinal));
        Assert.Equal(
            $$"""  {"hwid":"*","hostname":"PC-TEST","macAddresses":["aa:bb:cc:dd:ee:01"],"helperVersion":"1.8.0","osVersion":"Windows 11 Pro 24H2","helperBuild":"{{OwnBuild}}"}""",
            g[1]);
        var statuses = g.Where(l => l.StartsWith("  {\"helperVersion\"", StringComparison.Ordinal)).ToList();
        Assert.True(statuses.Count >= 3, string.Join("\n", g));
        Assert.All(statuses, s => Assert.Contains($"\"hostname\":\"PC-TEST\",\"helperBuild\":\"{OwnBuild}\"", s));
        Assert.DoesNotContain("helperUpdate", statuses[0]);
        Assert.Contains(statuses, s => s.EndsWith(
            $",\"helperBuild\":\"{OwnBuild}\",\"helperUpdate\":{{\"id\":\"{{id}}\",\"state\":\"waiting\",\"reason\":\"session\"}}}}", StringComparison.Ordinal));
        Assert.EndsWith(
            $",\"helperBuild\":\"{OwnBuild}\",\"helperUpdate\":{{\"id\":\"{{id}}\",\"state\":\"failed\",\"error\":\"hashMismatch\",\"message\":\"SHA256 of .new is 00ff\"}}}}",
            statuses[^1]);

        // Незнакомое поручение сервер пропускает: отчёты приняты.
        Assert.All(g.Where(l => l.StartsWith("PUT ", StringComparison.Ordinal)), l => Assert.EndsWith("-> 200", l));
        Assert.Equal(OwnBuild, (await stand.MachineAsync()).HelperBuild);
    }

    [Fact]
    public async Task Library_Report_NoHelperFields()
    {
        var recorder = new WireRecorder();
        await using var stand = await StandAsync(solo: true);
        await stand.PublishAsync(stand.Games, "v1");
        await stand.PublishAsync(stand.Solo, "v1");
        var options = Options(HelperOptions.DefaultLibraries);
        var api = new DisklessApiClient(stand.Http(recorder.Handler()), options, stand.Credentials, stand.Identity);
        var loops = Loops(stand, api, options, () => new HelperUpdateReport(Guid.NewGuid().ToString(), "downloading"));

        await loops.TickAsync(CancellationToken.None);
        await loops.TickAsync(CancellationToken.None);

        var library = recorder.Lines(p => p.Contains("/libraries/", StringComparison.Ordinal));
        Assert.Contains(library, l => l.StartsWith("PUT /diskless/v1/machines/{id}/libraries/solo/status -> 200", StringComparison.Ordinal));
        Assert.Contains(library, l => l.Contains("\"targetIqn\":\"iqn.2005-10.org.freenas.ctl:solo-seat-01\"", StringComparison.Ordinal));
        Assert.DoesNotContain(library, l => l.Contains("helperBuild", StringComparison.Ordinal) || l.Contains("helperUpdate", StringComparison.Ordinal));
        Assert.Contains(recorder.Lines(p => p.EndsWith("/status", StringComparison.Ordinal) && !p.Contains("/libraries/", StringComparison.Ordinal)),
            l => l.Contains("\"helperUpdate\":{\"id\":\"{id}\",\"state\":\"downloading\"}", StringComparison.Ordinal));
        Assert.Null(loops.Libraries.Single().LastAccepted!.HelperUpdate);
    }

    [Fact]
    public async Task Download_ShaAndSize()
    {
        await using var stand = await StandAsync(solo: false);
        var requests = new Requests();
        var api = new DisklessApiClient(stand.Http(), Options([]), stand.Credentials, stand.Identity, downloads: stand.Http(requests));
        var loops = Loops(stand, api, Options([]));
        await loops.TickAsync(CancellationToken.None);
        var queue = stand.Server.Services.GetRequiredService<HelperUpdateRepository>();
        await queue.LaunchAsync(Build181, [await NumberAsync(stand)], false, "panel", DateTimeOffset.UtcNow);
        await loops.TickAsync(CancellationToken.None);
        var offer = loops.Games.LastAccepted!.HelperUpdate!;

        // Прошлая неудачная загрузка оставила мусор — файл перезаписывается.
        await File.WriteAllTextAsync(Destination, new string('z', 300_000));
        var sha = await api.DownloadAsync(offer.Path, Destination, offer.Size, CancellationToken.None);

        Assert.Equal(offer.Sha256, sha);
        Assert.Equal(Content181, await File.ReadAllBytesAsync(Destination));
        Assert.Equal([("/pxe/v1/files/tools/" + Build181, false)], requests.Seen); // файлы сборок публичны, как файлы PXE: без токена

        // Точно по размеру — можно; файла нет — 404, ServerUnavailableException, файл стёрт.
        Assert.Equal(offer.Sha256, await api.DownloadAsync(offer.Path, Destination, Content181.Length, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<ServerUnavailableException>(
            () => api.DownloadAsync("pxe/v1/files/tools/ClubDisklessHelper-9.9.9.exe", Destination, offer.Size, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task Download_OverMax_DeletesFile()
    {
        var body = Encoding.UTF8.GetBytes(new string('b', 50_000));

        // Сервер шлёт больше, чем обещано (без Content-Length): загрузка обрывается, файл стёрт.
        var files = new FakeFiles(body);
        var error = await Assert.ThrowsAsync<ServerUnavailableException>(
            () => FakeClient(files).DownloadAsync("pxe/v1/files/tools/" + Build181, Destination, body.Length - 1, CancellationToken.None));
        Assert.Contains("more than", error.Message);
        Assert.False(File.Exists(Destination));
        Assert.Null(files.Seen.Single().Headers.Authorization);
        Assert.Equal(new Uri("https://club-server/pxe/v1/files/tools/" + Build181), files.Seen.Single().RequestUri);

        // С Content-Length больше допустимого — сразу, и старый файл тоже стёрт.
        await File.WriteAllTextAsync(Destination, "old");
        await Assert.ThrowsAsync<ServerUnavailableException>(
            () => FakeClient(new FakeFiles(body, length: true)).DownloadAsync("pxe/v1/files/tools/" + Build181, Destination, body.Length - 1, CancellationToken.None));
        Assert.False(File.Exists(Destination));

        // Ровно столько — загрузка проходит, SHA256 — строчными.
        Assert.Equal(Sha(body), await FakeClient(new FakeFiles(body)).DownloadAsync("pxe/v1/files/tools/" + Build181, Destination, body.Length, CancellationToken.None));
        Assert.Equal(body.Length, new FileInfo(Destination).Length);
    }

    [Fact]
    public async Task Download_ConnectionBroken_ServerUnavailable_DeletesFile()
    {
        var body = Encoding.UTF8.GetBytes(new string('c', 50_000));
        await Assert.ThrowsAsync<ServerUnavailableException>(
            () => FakeClient(new FakeFiles(body, breakAfter: 20_000)).DownloadAsync("pxe/v1/files/tools/" + Build181, Destination, body.Length, CancellationToken.None));
        Assert.False(File.Exists(Destination));

        // Абсолютный адрес (не сервер клуба из helper.json) не скачивается вовсе.
        var files = new FakeFiles(body);
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => FakeClient(files).DownloadAsync("https://elsewhere/ClubDisklessHelper-1.8.1.exe", Destination, body.Length, CancellationToken.None));
        Assert.Empty(files.Seen);
    }

    [Fact]
    public async Task Download_Cancelled_NotServerUnavailable_DeletesFile()
    {
        // Отмена вызывающего (поручение отозвали, служба останавливается) — отмена, а не «сервер недоступен»: файл стёрт.
        var body = Encoding.UTF8.GetBytes(new string('d', 50_000));
        using var cancel = new CancellationTokenSource();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FakeClient(new FakeFiles(body, cancel: cancel)).DownloadAsync("pxe/v1/files/tools/" + Build181, Destination, body.Length, cancel.Token));
        Assert.True(cancel.IsCancellationRequested, error.Message);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task MasterBusy_FailedWithLetter_IsBusy()
    {
        await using var stand = await StandAsync(solo: false);
        await stand.PublishAsync(stand.Games, "v1");
        var windows = new FakeWindowsStorage();
        var api = new DisklessApiClient(stand.Http(), Options([]), stand.Credentials, stand.Identity);
        var loops = Loops(stand, api, Options([]), windows: windows);
        await loops.TickAsync(CancellationToken.None);
        await loops.TickAsync(CancellationToken.None);
        Assert.False(loops.Games.MasterBusy);
        Assert.False(loops.MasterBusy());

        await stand.OpenMasterAsync(stand.Games);
        await loops.TickAsync(CancellationToken.None);
        Assert.Equal("mounted", loops.Games.LastMasterReport!.State);
        Assert.True(loops.Games.MasterBusy);
        Assert.True(loops.MasterBusy());

        // Правку закрыли, а на мастер-томе открыты файлы: назначения нет, том на G: не отключился — всё ещё занято.
        windows.BusySessions[LibrariesStand.GamesMaster] = FakeWindowsStorage.SessionBusy;
        await stand.CloseMasterAsync(stand.Games);
        await loops.TickAsync(CancellationToken.None);
        Assert.Equal(("failed", "G"), (loops.Games.LastMasterReport!.State, loops.Games.LastMasterReport.DriveLetter));
        Assert.True(loops.Games.MasterBusy);
        Assert.True(loops.MasterBusy());

        // Файлы закрыли: мастер-том отключён — свободно.
        windows.BusySessions.Remove(LibrariesStand.GamesMaster);
        await loops.TickAsync(CancellationToken.None);
        Assert.False(windows.Sessions.ContainsKey(LibrariesStand.GamesMaster));
        Assert.False(loops.Games.MasterBusy);
        Assert.False(loops.MasterBusy());
    }
}
