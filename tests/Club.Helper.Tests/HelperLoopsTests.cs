using System.Net.Http.Headers;
using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Такт помощника 1.6.0 (служба: <see cref="HelperLoops"/>): сначала G:, потом H:, по очереди и каждый в своём try/catch;
/// Windows не ответила в такте G: — H: этот такт пропускает; клиент API и его refresh-токен — один на оба цикла.
/// </summary>
public sealed class HelperLoopsTests : IAsyncLifetime
{
    private const string LoopsCategory = "Club.Helper.Core.HelperLoops";

    private LibrariesStand _stand = null!;
    private readonly WireRecorder _recorder = new();
    private readonly ListLoggerFactory _loggers = new();
    private readonly FakeWindowsStorage _windows = new();
    private Tokens _tokens = null!;

    public async Task InitializeAsync()
    {
        _stand = await LibrariesStand.StartAsync();
        await _stand.PublishAsync(_stand.Games, "v1");
        await _stand.PublishAsync(_stand.Solo, "v1");
    }

    public async Task DisposeAsync() => await _stand.DisposeAsync();

    private HelperLoops Helper(Func<string?, IAssignmentCache>? cache = null)
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey, DiskWaitSec = 1, HelperVersion = "1.6.0" };
        _tokens = new Tokens();
        var api = new DisklessApiClient(_stand.Server.CreateDefaultClient(_recorder.Handler(), _tokens), options, _stand.Credentials, _stand.Identity);
        return HelperLoops.Create(api, _windows, new FakeProcesses(), _stand.Identity, options, TimeProvider.System, _loggers, cache ?? _stand.Cache);
    }

    private static bool IsLibraryPath(string path) => path.Contains("/libraries/", StringComparison.Ordinal);

    /// <summary>assignment.json, который не записать (диск переполнен).</summary>
    private sealed class BrokenCache : IAssignmentCache
    {
        public Task<VolumeAssignment?> LoadAsync(string hwid, CancellationToken ct) => Task.FromResult<VolumeAssignment?>(null);

        public Task SaveAsync(string hwid, VolumeAssignment? assignment, CancellationToken ct) => throw new IOException("There is not enough space on the disk.");
    }

    /// <summary>
    /// Токен доступа «истекает» на следующем запросе (<see cref="ExpireNext"/>): сервер отвечает 401, клиент обновляет токены.
    /// Считает запросы, идущие одновременно.
    /// </summary>
    private sealed class Tokens : DelegatingHandler
    {
        private int _inFlight;

        public bool ExpireNext { get; set; }

        public int MaxInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                if (ExpireNext && request.Headers.Authorization is not null)
                {
                    ExpireNext = false;
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "expired");
                }

                await Task.Yield();
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Fact]
    public async Task Both_libraries_are_mounted_in_one_tick_g_first()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);

        Assert.Equal(("mounted", "G"), (helper.Games.LastReport!.State, helper.Games.LastReport.DriveLetter));
        var solo = Assert.Single(helper.Libraries);
        Assert.Equal(("mounted", "H", LibrariesStand.SoloSeat), (solo.LastReport!.State, solo.LastReport.DriveLetter, solo.LastReport.TargetIqn));
        Assert.True(_windows.Sessions[LibrariesStand.GamesSeat] is { ReadOnly: false, Offline: false, Letter: 'G' });
        Assert.True(_windows.Sessions[LibrariesStand.SoloSeat] is { ReadOnly: false, Offline: false, Letter: 'H' });

        // Все запросы G: такта — раньше всех запросов H:, ни одного одновременного.
        var paths = _recorder.Paths.ToList();
        Assert.True(paths.FindLastIndex(p => !IsLibraryPath(p)) < paths.FindIndex(IsLibraryPath), string.Join(" | ", paths));
        Assert.Equal(1, _tokens.MaxInFlight);
    }

    [Fact]
    public async Task Failure_of_one_library_tick_does_not_skip_the_other()
    {
        var helper = Helper(key => key is null ? _stand.Cache(null) : new BrokenCache());
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        // Такт H: упал (assignment.json.solo не записать) — G: отчитался в обоих тактах, H: во втором такте поднялся сам.
        Assert.Equal("mounted", helper.Games.LastReport!.State);
        Assert.Equal(3, _recorder.Paths.Count(p => p.EndsWith("/status", StringComparison.Ordinal) && !IsLibraryPath(p))); // 2 в первом такте
        var error = Assert.Single(_loggers.Of(LoopsCategory), e => e.Level == LogLevel.Error);
        Assert.Equal("Helper tick of library solo failed", error.Message);
        Assert.Equal("mounted", helper.Libraries[0].LastReport!.State);

        // И наоборот: падает такт G: — H: всё равно идёт.
        var reversed = Helper(key => key is null ? new BrokenCache() : _stand.Cache(key));
        var before = _recorder.Paths.Count(IsLibraryPath);
        await reversed.TickAsync(CancellationToken.None);
        Assert.Single(_loggers.Of(LoopsCategory), e => e.Message == "Helper tick failed");
        Assert.True(_recorder.Paths.Count(IsLibraryPath) > before);
    }

    [Fact]
    public async Task H_is_skipped_in_a_tick_where_windows_did_not_answer_for_g()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var solo = helper.Libraries[0];
        Assert.Equal("mounted", solo.LastReport!.State);

        _windows.FindDiskErrors[LibrariesStand.GamesSeat] = new TimeoutException("PowerShell timed out after 120 s");
        var requests = _recorder.Paths.Count(IsLibraryPath);
        var lookups = _windows.DiskLookups.Count(t => t == LibrariesStand.SoloSeat);
        await helper.TickAsync(CancellationToken.None);

        Assert.True(helper.Games.WindowsTimedOut);
        Assert.Equal(("failed", "not checked: timeout"), (helper.Games.LastReport!.State, helper.Games.LastReport.Error));
        Assert.Equal(requests, _recorder.Paths.Count(IsLibraryPath));
        Assert.Equal(lookups, _windows.DiskLookups.Count(t => t == LibrariesStand.SoloSeat));

        _windows.FindDiskErrors.Clear();
        await helper.TickAsync(CancellationToken.None);
        Assert.False(helper.Games.WindowsTimedOut);
        Assert.Equal(requests + 1, _recorder.Paths.Count(IsLibraryPath));
        Assert.Equal("mounted", solo.LastReport!.State);
    }

    [Fact]
    public async Task One_token_refresh_serves_both_loops()
    {
        var helper = Helper();
        await helper.TickAsync(CancellationToken.None);
        var tokens = _stand.Credentials.Current!;

        _tokens.ExpireNext = true; // токен доступа истёк к первому запросу такта (отчёт G:)
        await helper.TickAsync(CancellationToken.None);
        await helper.TickAsync(CancellationToken.None);

        Assert.Single(_recorder.Paths, p => p.EndsWith("/machines/refresh", StringComparison.Ordinal));
        Assert.Single(_recorder.Paths, p => p.EndsWith("/machines/register", StringComparison.Ordinal));
        Assert.NotEqual(tokens.RefreshToken, _stand.Credentials.Current!.RefreshToken);
        Assert.Equal(tokens.MachineId, _stand.Credentials.Current.MachineId);
        Assert.Equal(("mounted", "mounted"), (helper.Games.LastReport!.State, helper.Libraries[0].LastReport!.State));
        Assert.DoesNotContain(_recorder.Lines(p => true), l => l.EndsWith("-> 403", StringComparison.Ordinal) || l.EndsWith("-> 500", StringComparison.Ordinal));
        Assert.Equal(1, _tokens.MaxInFlight);
    }
}
