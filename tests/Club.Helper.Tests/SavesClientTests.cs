using System.Security.Cryptography;
using System.Text;
using Club.Helper.Core;
using Club.Saves;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using SavesOptions = Club.Server.Saves.SavesOptions;
using SavesRepository = Club.Server.Saves.SavesRepository;
using ServerSaveSettings = Club.Server.Saves.SaveSettings;

namespace Club.Helper.Tests;

/// <summary>
/// Методы API сохранений в <see cref="DisklessApiClient"/> (spec 2026-11-05, п. 3.3) против настоящего сервера клуба:
/// выдача, приём, ошибки сервера как <see cref="SavesRejectedException"/>, заголовок настроек с кириллицей. Своя база и
/// свой корень сохранений (с меткой) на каждый тест.
/// </summary>
public sealed class SavesClientTests : IAsyncLifetime
{
    private const string PlayerId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"club-saves-client-{Guid.NewGuid():N}");
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"club-saves-client-work-{Guid.NewGuid():N}");
    private readonly ServerFixture _server = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeIdentity _identity = new(Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_root, SavesOptions.MarkerFile), "");
        _server.Settings["Saves:Enabled"] = "true";
        _server.Settings["Saves:Root"] = _root;
        await _server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        foreach (var directory in new[] { _root, _work })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private SavesRepository Repository => _server.Services.GetRequiredService<SavesRepository>();

    /// <summary>Клиент помощника; <paramref name="transfers"/> — отдельный HttpClient для архивов (attach и PUT).</summary>
    private DisklessApiClient Client(HttpClient? http = null, HttpClient? transfers = null) =>
        new(http ?? _server.CreateClient(), new HelperOptions { ClubKey = ServerFixture.ClubKey }, _credentials, _identity, transfers);

    /// <summary>Zip сохранений из папки записи (как упакует помощник), во временном файле.</summary>
    private FileStream Pack(Guid entryId, string directory)
    {
        var stream = new FileStream(Path.Combine(_work, $"{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose);
        SaveArchive.Pack(stream, [(entryId, directory)], []);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Считает запросы, прошедшие через этот HttpClient.</summary>
    private sealed class Counter : DelegatingHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Подменяет заголовок настроек в ответе attach (сломанный прокси, чужой сервер).</summary>
    private sealed class HeaderRewriter(string value) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.Remove(DisklessApiClient.SavesConfigHeader))
            {
                response.Headers.TryAddWithoutValidation(DisklessApiClient.SavesConfigHeader, value);
            }

            return response;
        }
    }

    [Fact]
    public async Task Attach_put_attach_roundtrip()
    {
        var entry = await Repository.CreateEntryAsync("Игра", @"%SAVEDGAMES%\Игра", enabled: true);
        var counter = new Counter();
        var api = Client(transfers: _server.CreateDefaultClient(counter));

        // У игрока ничего нет: 204, архива нет, настройки — из заголовка.
        await using (var empty = await api.AttachSavesAsync(PlayerId, CancellationToken.None))
        {
            Assert.Null(empty.Archive);
            Assert.True(empty.Config.Enabled);
            Assert.Equal(200, empty.Config.LimitMb);
            Assert.Equal(["Logs", "Crashes", "CrashDumps", "*.dmp"], empty.Config.Excludes);
            Assert.Equal([new SaveEntryConfig(entry.Id, entry.Path)], empty.Config.Entries);
        }

        var source = Path.Combine(_work, "source");
        Directory.CreateDirectory(Path.Combine(source, "Профиль"));
        var big = RandomNumberGenerator.GetBytes(300_000);
        File.WriteAllBytes(Path.Combine(source, "Профиль", "Сохранение 1.sav"), big);
        File.WriteAllText(Path.Combine(source, "options.ini"), "fov=90");
        await using (var archive = Pack(entry.Id, source))
        {
            await api.PutSavesAsync(PlayerId, archive, CancellationToken.None);
        }

        // ID от шелла в другом регистре — тот же игрок (аренда снята приёмом, выдача берёт её заново).
        await using (var attach = await api.AttachSavesAsync(PlayerId.ToUpperInvariant(), CancellationToken.None))
        {
            Assert.NotNull(attach.Archive);
            var target = Path.Combine(_work, "target");
            var result = SaveArchive.Unpack(attach.Archive, id => id == entry.Id ? target : null, 1L << 30, unknownIsError: true);
            Assert.Equal(2, result.Files);
            Assert.Equal(big, File.ReadAllBytes(Path.Combine(target, "Профиль", "Сохранение 1.sav")));
            Assert.Equal("fov=90", File.ReadAllText(Path.Combine(target, "options.ini")));
        }

        // Архивы — через отдельный HttpClient, токены и прочее — через основной.
        Assert.Equal(
            [
                $"POST /diskless/v1/machines/{api.MachineId}/saves/{PlayerId}/attach",
                $"PUT /diskless/v1/machines/{api.MachineId}/saves/{PlayerId}",
                $"POST /diskless/v1/machines/{api.MachineId}/saves/{PlayerId.ToUpperInvariant()}/attach",
            ],
            counter.Requests);
        var player = (await Repository.PlayersAsync(PlayerId, 1, 50)).Items.Single();
        Assert.Equal(big.Length + "fov=90".Length, player.SizeBytes);

        // Снятие аренды без сохранения: следующий PUT с этого ПК — уже не принят.
        await api.ReleaseSavesLeaseAsync(PlayerId, CancellationToken.None);
        await using var again = Pack(entry.Id, source);
        var rejected = await Assert.ThrowsAsync<SavesRejectedException>(() => api.PutSavesAsync(PlayerId, again, CancellationToken.None));
        Assert.Equal("notAttached", rejected.Reason);
    }

    [Fact]
    public async Task Put_without_attach_is_notAttached()
    {
        var entry = await Repository.CreateEntryAsync("Игра", @"%APPDATA%\Game", enabled: true);
        var source = Path.Combine(_work, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "save.dat"), "late");
        var api = Client();

        await using var archive = Pack(entry.Id, source);
        var error = await Assert.ThrowsAsync<SavesRejectedException>(() => api.PutSavesAsync(PlayerId, archive, CancellationToken.None));
        Assert.Equal("notAttached", error.Reason);
        Assert.Equal(SavesRepository.Results.Rejected, (await Repository.PlayersAsync(PlayerId, 1, 50)).Items.Single().LastResult);
    }

    [Fact]
    public async Task Put_after_401_resends_the_whole_archive()
    {
        var entry = await Repository.CreateEntryAsync("Игра", @"%APPDATA%\Game", enabled: true);
        var source = Path.Combine(_work, "source");
        Directory.CreateDirectory(source);
        var content = RandomNumberGenerator.GetBytes(200_000);
        File.WriteAllBytes(Path.Combine(source, "save.dat"), content);
        await (await Client().AttachSavesAsync(PlayerId, CancellationToken.None)).DisposeAsync();

        // Токен доступа устарел: первый PUT — 401, после refresh тот же архив уходит заново, с начала.
        _credentials.Current = _credentials.Current! with { AccessToken = "expired" };
        var counter = new Counter();
        await using (var archive = Pack(entry.Id, source))
        {
            await Client(transfers: _server.CreateDefaultClient(counter)).PutSavesAsync(PlayerId, archive, CancellationToken.None);
        }

        Assert.Equal(2, counter.Requests.Count);
        Assert.Equal(content.Length, (await Repository.PlayersAsync(PlayerId, 1, 50)).Items.Single().SizeBytes);
    }

    [Fact]
    public async Task Put_over_limit_is_overLimit()
    {
        await Repository.UpdateSettingsAsync(new ServerSaveSettings(1, ["Logs"]));
        var entry = await Repository.CreateEntryAsync("Игра", @"%APPDATA%\Game", enabled: true);
        var source = Path.Combine(_work, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "huge.sav"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        var api = Client();
        await (await api.AttachSavesAsync(PlayerId, CancellationToken.None)).DisposeAsync();

        await using var archive = Pack(entry.Id, source);
        var error = await Assert.ThrowsAsync<SavesRejectedException>(() => api.PutSavesAsync(PlayerId, archive, CancellationToken.None));
        Assert.Equal("overLimit", error.Reason);
    }

    [Fact]
    public async Task Disabled_server_is_disabled()
    {
        await Repository.CreateEntryAsync("Игра", @"%APPDATA%\Game", enabled: true);
        File.Delete(Path.Combine(_root, SavesOptions.MarkerFile)); // диск сохранений не смонтирован
        var api = Client();

        // Список отвечает и так — с enabled=false; команды игрока — disabled.
        var config = await api.GetSavesConfigAsync(CancellationToken.None);
        Assert.False(config.Enabled);
        Assert.Single(config.Entries);
        Assert.Equal("disabled", (await Assert.ThrowsAsync<SavesRejectedException>(() => api.AttachSavesAsync(PlayerId, CancellationToken.None))).Reason);
        Assert.Equal("disabled", (await Assert.ThrowsAsync<SavesRejectedException>(() => api.ReleaseSavesLeaseAsync(PlayerId, CancellationToken.None))).Reason);
        using var archive = new MemoryStream();
        Assert.Equal("disabled", (await Assert.ThrowsAsync<SavesRejectedException>(() => api.PutSavesAsync(PlayerId, archive, CancellationToken.None))).Reason);
    }

    [Fact]
    public async Task Config_header_with_cyrillic_path()
    {
        var entry = await Repository.CreateEntryAsync("Игры", @"H:\Игры\Сохранения «Ведьмак»", enabled: true);
        var api = Client();

        var config = await api.GetSavesConfigAsync(CancellationToken.None);
        Assert.Equal([new SaveEntryConfig(entry.Id, @"H:\Игры\Сохранения «Ведьмак»")], config.Entries);
        await using var attach = await api.AttachSavesAsync(PlayerId, CancellationToken.None);
        Assert.Equal(config.Entries, attach.Config.Entries);
        Assert.Equal(config.Excludes, attach.Config.Excludes);
        Assert.Equal((config.Enabled, config.LimitMb), (attach.Config.Enabled, attach.Config.LimitMb));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("/w==")] // байт 0xFF — не UTF-8
    [InlineData("bnVsbA==")] // "null"
    [InlineData("eyJlbmFibGVkIjp0cnVlfQ==")] // {"enabled":true} — без списков
    public async Task Malformed_config_header_is_serverUnavailable(string header)
    {
        await Repository.CreateEntryAsync("Игра", @"%APPDATA%\Game", enabled: true);
        var api = Client(transfers: _server.CreateDefaultClient(new HeaderRewriter(header)));

        await Assert.ThrowsAsync<ServerUnavailableException>(() => api.AttachSavesAsync(PlayerId, CancellationToken.None));
    }

    [Fact]
    public async Task Server_down_is_serverUnavailable()
    {
        var api = Client();
        await api.GetSavesConfigAsync(CancellationToken.None); // зарегистрировался
        var down = Client(transfers: new HttpClient(new ServerDown()) { BaseAddress = new Uri("http://localhost/") });

        var error = await Assert.ThrowsAsync<ServerUnavailableException>(() => down.AttachSavesAsync(PlayerId, CancellationToken.None));
        Assert.True(error.Unreachable);
    }

    private sealed class ServerDown : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
