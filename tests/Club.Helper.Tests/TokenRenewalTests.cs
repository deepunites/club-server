using System.Collections.Concurrent;
using Club.Helper.Core;
using Club.TestSupport;

namespace Club.Helper.Tests;

/// <summary>
/// Один <see cref="DisklessApiClient"/> на два потока (1.7.0: команды канала шелла через <see cref="SavesService"/> и такт
/// помощника) против настоящего сервера клуба: два запроса одновременно получили 401 — токены обновляет один, второй
/// повторяет запрос уже с новым токеном. Два refresh одним и тем же refresh-токеном сервер принял бы за кражу и отозвал бы
/// токены машины (помощник регистрировался бы заново).
/// </summary>
public sealed class TokenRenewalTests : IAsyncLifetime
{
    private readonly ServerFixture _server = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeIdentity _identity = new(Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _server.InitializeAsync();

    public async Task DisposeAsync() => await ((IAsyncLifetime)_server).DisposeAsync();

    /// <summary>
    /// Запросы с устаревшим токеном ждут, пока их не придёт <paramref name="expected"/>, — все получают 401 одновременно;
    /// refresh идёт на сервер с задержкой: окно, в котором второй 401 успел бы предъявить тот же refresh-токен.
    /// </summary>
    private sealed class Gate(int expected) : DelegatingHandler
    {
        public const string ExpiredToken = "expired";

        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public ConcurrentQueue<string> Paths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            if (request.Headers.Authorization?.Parameter == ExpiredToken)
            {
                if (Interlocked.Increment(ref _arrived) == expected)
                {
                    _all.TrySetResult();
                }

                await _all.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            else if (path.EndsWith("/machines/refresh", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Concurrent_401s_refresh_tokens_once()
    {
        var options = new HelperOptions { ClubKey = ServerFixture.ClubKey };
        await new DisklessApiClient(_server.CreateClient(), options, _credentials, _identity).GetSavesConfigAsync(CancellationToken.None);
        var registered = _credentials.Current!;

        // Помощник перезапущен с устаревшим токеном доступа; первые два запроса — из канала шелла и из такта, одновременно.
        _credentials.Current = registered with { AccessToken = Gate.ExpiredToken };
        var gate = new Gate(expected: 2);
        var api = new DisklessApiClient(_server.CreateDefaultClient(gate), options, _credentials, _identity);
        var configs = await Task.WhenAll(api.GetSavesConfigAsync(CancellationToken.None), api.GetSavesConfigAsync(CancellationToken.None));

        Assert.All(configs, c => Assert.False(c.Enabled));
        Assert.Single(gate.Paths, p => p.EndsWith("/machines/refresh", StringComparison.Ordinal));
        Assert.DoesNotContain(gate.Paths, p => p.EndsWith("/machines/register", StringComparison.Ordinal));
        Assert.Equal(registered.MachineId, _credentials.Current!.MachineId);
        Assert.NotEqual(registered.RefreshToken, _credentials.Current.RefreshToken);

        // Новые токены действуют: следующий запрос — без refresh и без регистрации.
        await api.GetSavesConfigAsync(CancellationToken.None);
        Assert.Single(gate.Paths, p => p.EndsWith("/machines/refresh", StringComparison.Ordinal));
        Assert.Equal(6, gate.Paths.Count); // два 401, refresh, два повтора, ещё один запрос
    }
}
