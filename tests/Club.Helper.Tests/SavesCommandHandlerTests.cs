using System.Text.Json;
using Club.Helper.Core;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using SavesOptions = Club.Server.Saves.SavesOptions;
using SavesRepository = Club.Server.Saves.SavesRepository;

namespace Club.Helper.Tests;

/// <summary>
/// Протокол канала шелла (spec 2026-11-05, п. 4.1 и 5): строка JSON запроса → строка JSON ответа, разбор и отказы
/// <c>badRequest</c>, передача команд в <see cref="SavesService"/> и их выполнение по одной. Один сервер клуба на класс,
/// у каждого теста свои «ПК» (машина, профиль, папка состояния).
/// </summary>
public sealed class SavesCommandHandlerTests(SavesCommandHandlerTests.Stand stand) : IClassFixture<SavesCommandHandlerTests.Stand>, IDisposable
{
    private const string Version = "1.7.0";
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"club-saves-pipe-{Guid.NewGuid():N}");

    /// <summary>Сервер клуба с включёнными сохранениями и своим корнем хранилища (с файлом-меткой).</summary>
    public sealed class Stand : IAsyncLifetime
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"club-saves-pipe-root-{Guid.NewGuid():N}");

        public ServerFixture Server { get; } = new();

        public SavesRepository Repository => Server.Services.GetRequiredService<SavesRepository>();

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, SavesOptions.MarkerFile), "");
            Server.Settings["Saves:Enabled"] = "true";
            Server.Settings["Saves:Root"] = Root;
            await Server.InitializeAsync();
        }

        public async Task DisposeAsync()
        {
            await ((IAsyncLifetime)Server).DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    /// <summary>Один ПК: профиль игрока по <see cref="Sid"/>, папка состояния помощника и обработчик канала над ними.</summary>
    private sealed record Pc(SavesCommandHandler Handler, string Profile);

    private Pc NewPc(string name, DelegatingHandler? transfers = null)
    {
        var directory = Path.Combine(_work, name);
        var profile = Path.Combine(directory, "Users", "player");
        Directory.CreateDirectory(profile);
        var profiles = new FakeProfiles();
        profiles.Paths[Sid] = profile;
        var api = new DisklessApiClient(stand.Server.CreateClient(), new HelperOptions { ClubKey = ServerFixture.ClubKey }, new InMemoryCredentialStore(),
            new FakeIdentity($"hwid-{name}-{Guid.NewGuid():N}"), transfers is null ? null : stand.Server.CreateDefaultClient(transfers));
        var saves = new SavesService(api, profiles, new FakeDrives(), () => false, Path.Combine(directory, "ProgramData", "ClubDiskless"), new ManualClock(),
            new ListLogger<SavesService>());
        return new Pc(new SavesCommandHandler(saves, Version), profile);
    }

    private static string NewPlayer() => Guid.NewGuid().ToString("D");

    private static string Attach(string playerId) => JsonSerializer.Serialize(new { op = "profile.attach", playerId, windowsUserSid = Sid });

    private static string Detach(string playerId) => JsonSerializer.Serialize(new { op = "profile.detach", playerId });

    /// <summary>Ответ — одна строка JSON-объекта; возвращает (ok, error).</summary>
    private static (bool Ok, string? Error) Parse(string response)
    {
        Assert.DoesNotContain('\n', response);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var ok = root.GetProperty("ok").GetBoolean();
        if (ok)
        {
            return (true, null);
        }

        Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()));
        return (false, root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Ping()
    {
        var pc = NewPc("A");
        Assert.Equal("""{"ok":true,"version":"1.7.0"}""", await pc.Handler.HandleAsync("""{"op":"ping"}""", CancellationToken.None));
    }

    [Fact]
    public async Task Attach_and_detach_dispatch()
    {
        await stand.Repository.CreateEntryAsync("Игра", @"%SAVEDGAMES%\Игра", enabled: true);
        var pc = NewPc("A");
        var player = NewPlayer();

        // Шелл присылает ID в любом регистре: выдача и сохранение — одному игроку.
        Assert.Equal("""{"ok":true}""", await pc.Handler.HandleAsync(Attach(player.ToUpperInvariant()), CancellationToken.None));
        var save = Path.Combine(pc.Profile, "Saved Games", "Игра", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllText(save, "progress");
        Assert.Equal("""{"ok":true}""", await pc.Handler.HandleAsync(Detach(player), CancellationToken.None));
        var row = Assert.Single((await stand.Repository.PlayersAsync(player, 1, 50)).Items);
        Assert.Equal(1, row.FileCount);

        // Ошибки SavesService доходят до шелла кодом из п. 4.1.
        Assert.Equal((false, SavesErrors.NotAttached), Parse(await pc.Handler.HandleAsync(Detach(player), CancellationToken.None)));
        var unknownSid = JsonSerializer.Serialize(new { op = "profile.attach", playerId = player, windowsUserSid = "S-1-5-21-1-2-3-500" });
        Assert.Equal((false, SavesErrors.ProfileNotFound), Parse(await pc.Handler.HandleAsync(unknownSid, CancellationToken.None)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"op":"ping" """)]
    [InlineData("[]")]
    [InlineData("\"ping\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"op":1}""")]
    [InlineData("""{"op":""}""")]
    [InlineData("""{"op":"reboot"}""")]
    [InlineData("""{"op":"PING"}""")]
    [InlineData("""{"op":"profile.attach","playerId":"0f8fad5b-d9cb-469f-a165-70867728950e"}""")]
    [InlineData("""{"op":"profile.attach","playerId":"0f8fad5b-d9cb-469f-a165-70867728950e","windowsUserSid":""}""")]
    [InlineData("""{"op":"profile.attach","playerId":"0f8fad5b-d9cb-469f-a165-70867728950e","windowsUserSid":5}""")]
    [InlineData("""{"op":"profile.attach","windowsUserSid":"S-1-5-21-1004336348-1177238915-682003330-1001"}""")]
    [InlineData("""{"op":"profile.detach"}""")]
    [InlineData("""{"op":"profile.detach","playerId":""}""")]
    [InlineData("""{"op":"profile.detach","playerId":null}""")]
    [InlineData("""{"op":"profile.detach","playerId":42}""")]
    [InlineData("""{"op":"profile.detach","playerId":"../etc"}""")]
    [InlineData("""{"op":"profile.detach","playerId":"player one"}""")]
    public async Task Bad_json_is_badRequest(string request)
    {
        var pc = NewPc("A");
        Assert.Equal((false, SavesErrors.BadRequest), Parse(await pc.Handler.HandleAsync(request, CancellationToken.None)));
    }

    [Fact]
    public async Task Line_longer_than_limit_is_badRequest()
    {
        var pc = NewPc("A");
        const string Prefix = "{\"op\":\"ping\",\"pad\":\"";
        const string Suffix = "\"}";
        var request = Prefix + new string('x', SavesCommandHandler.MaxRequestLength) + Suffix;
        Assert.Equal((false, SavesErrors.BadRequest), Parse(await pc.Handler.HandleAsync(request, CancellationToken.None)));

        // Ровно на пределе — ещё запрос.
        var exact = Prefix + new string('x', SavesCommandHandler.MaxRequestLength - Prefix.Length - Suffix.Length) + Suffix;
        Assert.Equal(SavesCommandHandler.MaxRequestLength, exact.Length);
        Assert.Equal((true, null), Parse(await pc.Handler.HandleAsync(exact, CancellationToken.None)));
    }

    [Fact]
    public async Task Unknown_fields_ignored()
    {
        var pc = NewPc("A");
        var player = NewPlayer();

        // rememberLaunchers — из старого текста п. 23 SHELL_CHANGES.
        var attach = JsonSerializer.Serialize(new
        {
            op = "profile.attach", playerId = player, windowsUserSid = Sid, rememberLaunchers = new[] { "steam", "epic" }, extra = new { nested = 1 },
        });
        Assert.Equal("""{"ok":true}""", await pc.Handler.HandleAsync(attach, CancellationToken.None));
        Assert.Equal("""{"ok":true}""", await pc.Handler.HandleAsync(
            JsonSerializer.Serialize(new { op = "profile.detach", playerId = player, reason = "sessionEnded" }), CancellationToken.None));
        Assert.Equal("""{"ok":true,"version":"1.7.0"}""", await pc.Handler.HandleAsync("""{"op":"ping","from":"shell"}""", CancellationToken.None));
    }

    [Fact]
    public async Task Commands_run_one_at_a_time()
    {
        var hold = new HoldFirstAttach();
        var pc = NewPc("A", hold);
        var player = NewPlayer();

        var attach = pc.Handler.HandleAsync(Attach(player), CancellationToken.None);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Пока идёт attach, detach ждёт своей очереди: иначе он ответил бы notAttached сразу.
        var detach = pc.Handler.HandleAsync(Detach(player), CancellationToken.None);
        await Task.Delay(300);
        Assert.False(detach.IsCompleted);
        Assert.False(attach.IsCompleted);

        // ping состояния не трогает и отвечает сразу: проверка «помощник жив» не ждёт выдачу в 60 с.
        Assert.Equal("""{"ok":true,"version":"1.7.0"}""", await pc.Handler.HandleAsync("""{"op":"ping"}""", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        hold.Release.SetResult();
        Assert.Equal("""{"ok":true}""", await attach.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("""{"ok":true}""", await detach.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Exception_is_internal()
    {
        var pc = NewPc("A");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal((false, SavesErrors.Internal), Parse(await pc.Handler.HandleAsync(Detach(NewPlayer()), cancelled.Token)));
        Assert.Equal((false, SavesErrors.Internal), Parse(await pc.Handler.HandleAsync(Attach(NewPlayer()), cancelled.Token)));
    }

    /// <summary>Задерживает первый запрос выдачи (<c>POST …/attach</c>), пока тест не отпустит.</summary>
    private sealed class HoldFirstAttach : DelegatingHandler
    {
        private int _attaches;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attach", StringComparison.Ordinal) && Interlocked.Increment(ref _attaches) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
