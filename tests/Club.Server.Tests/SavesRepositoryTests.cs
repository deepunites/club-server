using Club.Server.Data;
using Club.Server.Saves;
using Club.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>Таблицы сохранений игроков (M0012) и <see cref="SavesRepository"/>: настройки, папки, аренда, блокировка, список игроков.</summary>
public sealed class SavesRepositoryTests : IAsyncLifetime
{
    private readonly ServerFixture _server = new();

    public Task InitializeAsync() => _server.InitializeAsync();

    public Task DisposeAsync() => ((IAsyncLifetime)_server).DisposeAsync();

    private SavesRepository Repository => _server.Services.GetRequiredService<SavesRepository>();

    private async Task<MachineRow> MachineAsync() =>
        await _server.Services.GetRequiredService<MachineRepository>().CreateAsync(
            new MachineRegistration(Guid.NewGuid().ToString("N"), "PC-TEST", ["aa:bb:cc:dd:ee:ff"], "10.0.0.2", "1.7.0", null), approved: true);

    [Fact]
    public async Task Defaults_after_migration()
    {
        var settings = await Repository.SettingsAsync();
        Assert.Equal(200, settings.LimitMb);
        Assert.Equal(["Logs", "Crashes", "CrashDumps", "*.dmp"], settings.Excludes);
        Assert.Empty(await Repository.EntriesAsync(enabledOnly: false));

        await Repository.UpdateSettingsAsync(new SaveSettings(500, ["Logs", "Кэш"]));
        var updated = await Repository.SettingsAsync();
        Assert.Equal(500, updated.LimitMb);
        Assert.Equal(["Logs", "Кэш"], updated.Excludes);

        // Лимит вне 1…2000 база не примет, даже если проверку в панели обошли.
        await Assert.ThrowsAsync<PostgresException>(() => Repository.UpdateSettingsAsync(new SaveSettings(0, [])));
        await Assert.ThrowsAsync<PostgresException>(() => Repository.UpdateSettingsAsync(new SaveSettings(2001, [])));
        Assert.Equal(500, (await Repository.SettingsAsync()).LimitMb);
    }

    [Fact]
    public async Task Entries_crud()
    {
        var witcher = await Repository.CreateEntryAsync("Witcher 3", @"%DOCUMENTS%\The Witcher 3", enabled: true);
        var cs = await Repository.CreateEntryAsync("CS 1.6", @"H:\Игры\CS 1.6\cstrike\config", enabled: false);
        Assert.NotEqual(Guid.Empty, witcher.Id);
        Assert.Equal(new SaveEntry(witcher.Id, "Witcher 3", @"%DOCUMENTS%\The Witcher 3", true), witcher);

        // Порядок — по названию; enabledOnly — только включённые.
        Assert.Equal([cs, witcher], await Repository.EntriesAsync(enabledOnly: false));
        Assert.Equal([witcher], await Repository.EntriesAsync(enabledOnly: true));

        var changed = cs with { Name = "Counter-Strike 1.6", Enabled = true };
        Assert.True(await Repository.UpdateEntryAsync(changed));
        Assert.Equal([changed, witcher], await Repository.EntriesAsync(enabledOnly: true));
        Assert.False(await Repository.UpdateEntryAsync(changed with { Id = Guid.NewGuid() }));

        Assert.True(await Repository.DeleteEntryAsync(witcher.Id));
        Assert.False(await Repository.DeleteEntryAsync(witcher.Id));
        Assert.Equal([changed], await Repository.EntriesAsync(enabledOnly: false));

        // Название 1…60 символов, путь до 260 — проверки в базе.
        await Assert.ThrowsAsync<PostgresException>(() => Repository.CreateEntryAsync("", @"%APPDATA%\Game", true));
        await Assert.ThrowsAsync<PostgresException>(() => Repository.CreateEntryAsync(new string('x', 61), @"%APPDATA%\Game", true));
        await Assert.ThrowsAsync<PostgresException>(() => Repository.CreateEntryAsync("Game", @"%APPDATA%\" + new string('x', 260), true));
    }

    [Fact]
    public async Task Lease_take_over_and_release()
    {
        var a = await MachineAsync();
        var b = await MachineAsync();
        const string player = "0f8fad5b-d9cb-469f-a165-70867728950e";
        Assert.Null(await Repository.LeaseHolderAsync(player));

        await Repository.TakeLeaseAsync(player, a.Id);
        Assert.Equal(a.Id, await Repository.LeaseHolderAsync(player));

        // ПК A пропал, не сохранив: игрок вошёл на B — аренда переходит к B, опоздавший A её не снимет.
        await Repository.TakeLeaseAsync(player, b.Id);
        Assert.Equal(b.Id, await Repository.LeaseHolderAsync(player));
        await Repository.ReleaseLeaseAsync(player, a.Id);
        Assert.Equal(b.Id, await Repository.LeaseHolderAsync(player));

        await Repository.ReleaseLeaseAsync(player, b.Id);
        Assert.Null(await Repository.LeaseHolderAsync(player));

        // Машину удалили из реестра — её аренды уходят вместе с ней.
        await Repository.TakeLeaseAsync(player, a.Id);
        await using (var c = await _server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("DELETE FROM machines WHERE id = @Id", new { a.Id });
        }

        Assert.Null(await Repository.LeaseHolderAsync(player));
    }

    [Fact]
    public async Task Lock_serializes_same_player()
    {
        const string player = "player-1";
        var first = await Repository.LockPlayerAsync(player);
        var same = Repository.LockPlayerAsync(player);
        var other = Repository.LockPlayerAsync("player-2");

        await using (await other.WaitAsync(TimeSpan.FromSeconds(10)))
        {
        }

        await Task.Delay(300);
        Assert.False(same.IsCompleted);

        await first.DisposeAsync();
        await using (await same.WaitAsync(TimeSpan.FromSeconds(10)))
        {
        }

        // Освобождённую блокировку снова можно взять.
        await using (await Repository.LockPlayerAsync(player).WaitAsync(TimeSpan.FromSeconds(10)))
        {
        }
    }

    [Fact]
    public async Task Players_paging_and_search()
    {
        var a = await MachineAsync();
        var b = await MachineAsync();
        var ids = Enumerable.Range(1, 7).Select(i => $"abc-{i:D2}").Append("zzz-01").ToList();
        foreach (var id in ids)
        {
            await Repository.RecordSavedAsync(id, a.Id, 1000, 3);
        }

        await Repository.RecordSavedAsync("zzz-01", b.Id, 2048, 5);
        await Repository.RecordResultAsync("abc-02", "overLimit", "250 MB > 200 MB");
        await Repository.RecordResultAsync("new-01", "rejected", null);

        var (page1, total) = await Repository.PlayersAsync(null, 1, 5);
        var (page2, total2) = await Repository.PlayersAsync(null, 2, 5);
        Assert.Equal((9, 9), (total, total2));
        Assert.Equal(5, page1.Count);
        Assert.Equal(4, page2.Count);
        Assert.Equal(ids.Append("new-01").Order(StringComparer.Ordinal), page1.Concat(page2).Select(p => p.PlayerId).Order(StringComparer.Ordinal));
        Assert.Empty((await Repository.PlayersAsync(null, 3, 5)).Items);

        var zzz = Assert.Single((await Repository.PlayersAsync("ZZZ", 1, 50)).Items);
        Assert.Equal(("zzz-01", 2048L, 5, b.Id, b.Name, "ok"), (zzz.PlayerId, zzz.SizeBytes, zzz.FileCount, zzz.MachineId, zzz.MachineName, zzz.LastResult));
        Assert.NotNull(zzz.SavedAt);
        Assert.NotNull(zzz.LastResultAt);
        Assert.Null(zzz.LastError);

        // Отклонённое сохранение не трогает прошлую версию — меняется только последний результат.
        var abc02 = Assert.Single((await Repository.PlayersAsync("abc-02", 1, 50)).Items);
        Assert.Equal((1000L, 3, "overLimit", "250 MB > 200 MB"), (abc02.SizeBytes, abc02.FileCount, abc02.LastResult, abc02.LastError));
        Assert.NotNull(abc02.SavedAt);

        // Игрок без сохранённой версии — только результат.
        var fresh = Assert.Single((await Repository.PlayersAsync("new", 1, 50)).Items);
        Assert.Equal((0L, 0, (DateTimeOffset?)null, (Guid?)null, (string?)null, "rejected"), (fresh.SizeBytes, fresh.FileCount, fresh.SavedAt, fresh.MachineId, fresh.MachineName, fresh.LastResult));

        var (abc, abcTotal) = await Repository.PlayersAsync("abc", 1, 50);
        Assert.Equal((7, 7), (abc.Count, abcTotal));
        // Поиск — подстрока, а не шаблон LIKE.
        Assert.Equal(0, (await Repository.PlayersAsync("_", 1, 50)).Total);

        await Repository.TakeLeaseAsync("zzz-01", b.Id);
        await Repository.DeletePlayerAsync("zzz-01");
        Assert.Empty((await Repository.PlayersAsync("zzz", 1, 50)).Items);
        Assert.Null(await Repository.LeaseHolderAsync("zzz-01"));

        // Машину удалили из реестра — строка игрока остаётся, без ПК.
        await using (var c = await _server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("DELETE FROM machines WHERE id = @Id", new { a.Id });
        }

        var orphan = Assert.Single((await Repository.PlayersAsync("abc-01", 1, 50)).Items);
        Assert.Equal(((Guid?)null, (string?)null, 1000L), (orphan.MachineId, orphan.MachineName, orphan.SizeBytes));
    }
}
