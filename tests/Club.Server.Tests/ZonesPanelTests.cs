using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Club.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Club.Server.Tests;

/// <summary>Зоны мест на экране «Рабочие станции»: создание, переименование, удаление, назначение мест пачкой.</summary>
public sealed class ZonesPanelTests : IAsyncLifetime
{
    private const string PanelToken = "panel-test-token";
    private readonly ServerFixture _server = new();

    public async Task InitializeAsync()
    {
        _server.Settings["Auth:AutoApprovePcs"] = "false";
        _server.Settings["Panel:AdminToken"] = PanelToken;
        await _server.InitializeAsync();
    }

    public Task DisposeAsync() => ((IAsyncLifetime)_server).DisposeAsync();

    private HttpClient Panel()
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PanelToken);
        return http;
    }

    private async Task<JsonElement> OverviewAsync()
    {
        using var response = await Panel().GetAsync("/panel/api/v1/machines");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Zone(JsonElement overview, string id) =>
        Assert.Single(overview.GetProperty("zones").EnumerateArray(), z => z.GetProperty("id").GetString() == id);

    private static string ZoneOf(JsonElement overview, int number) =>
        Assert.Single(overview.GetProperty("machines").EnumerateArray(), m => m.GetProperty("number").GetInt32() == number).GetProperty("zone").GetString()!;

    private async Task<string> CreateZoneAsync(string name)
    {
        using var response = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>ПК по MAC с заданным номером места (как карточка «Добавить ПК по MAC»).</summary>
    private async Task AddSeatAsync(int number)
    {
        using var response = await Panel().PostAsJsonAsync("/panel/api/v1/machines", new { macAddress = $"02:00:00:00:{number / 256:x2}:{number % 256:x2}", number, bootMode = "local" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string reason)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "conflict", reason);
    }

    [Fact]
    public async Task Zone_is_created_with_server_id_and_renamed()
    {
        using var created = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = "  Зона 4 " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;
        Assert.Matches("^zone-[0-9a-f]{12}$", id);
        Assert.Equal("Зона 4", body.GetProperty("name").GetString());

        var zone = Zone(await OverviewAsync(), id);
        Assert.Equal(("Зона 4", 0, false), (zone.GetProperty("name").GetString(), zone.GetProperty("machines").GetInt32(), zone.GetProperty("isDefault").GetBoolean()));
        Assert.True(Zone(await OverviewAsync(), "standard").GetProperty("isDefault").GetBoolean());

        using var renamed = await Panel().PatchAsJsonAsync($"/panel/api/v1/zones/{id}", new { name = "VIP" });
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        // Зону по умолчанию переименовать можно; id остаётся прежним.
        using var renamedDefault = await Panel().PatchAsJsonAsync("/panel/api/v1/zones/standard", new { name = "Зона 1" });
        Assert.Equal(HttpStatusCode.NoContent, renamedDefault.StatusCode);

        var zones = await Panel().GetFromJsonAsync<JsonElement>("/panel/api/v1/zones");
        Assert.Equal(["standard:Зона 1", $"{id}:VIP"],
            zones.EnumerateArray().Select(z => $"{z.GetProperty("id").GetString()}:{z.GetProperty("name").GetString()}").Order().ToArray());

        using var missing = await Panel().PatchAsJsonAsync("/panel/api/v1/zones/zone-000000000000", new { name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Zone_name_is_unique_ignoring_case_and_fits_40_chars()
    {
        var vip = await CreateZoneAsync("VIP");
        using (var duplicate = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = "vip" }))
        {
            await AssertConflictAsync(duplicate, "zoneNameTaken");
        }

        // Регистр не различается и в кириллице, и у зоны по умолчанию («Standart» из M0001).
        await CreateZoneAsync("Зона 4");
        using (var cyrillic = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = "ЗОНА 4" }))
        {
            await AssertConflictAsync(cyrillic, "zoneNameTaken");
        }

        using (var standard = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = " standart" }))
        {
            await AssertConflictAsync(standard, "zoneNameTaken");
        }

        using (var renameTaken = await Panel().PatchAsJsonAsync($"/panel/api/v1/zones/{vip}", new { name = "STANDART" }))
        {
            await AssertConflictAsync(renameTaken, "zoneNameTaken");
        }

        // Своё же название другим регистром — не конфликт.
        using (var ownCase = await Panel().PatchAsJsonAsync($"/panel/api/v1/zones/{vip}", new { name = "Vip" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, ownCase.StatusCode);
        }

        foreach (var bad in new[] { "", "   ", new string('z', 41) })
        {
            using var response = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation", "length");
        }

        using (var longest = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = new string('z', 40) }))
        {
            Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        }

        using var renameEmpty = await Panel().PatchAsJsonAsync($"/panel/api/v1/zones/{vip}", new { name = " " });
        Assert.Equal(HttpStatusCode.BadRequest, renameEmpty.StatusCode);
    }

    /// <summary>
    /// Названия, вставленные из Excel или Telegram: пробелы любого вида — один обычный (и в проверке уникальности);
    /// невидимые символы форматирования и управляющие — 400 <c>format</c>, а не вторая «VIP» на вид.
    /// </summary>
    [Fact]
    public async Task Zone_name_with_odd_spaces_is_normalized_and_invisible_characters_are_refused()
    {
        await CreateZoneAsync("VIP");
        await CreateZoneAsync("Зона 4");
        foreach (var lookalike in new[] { "Зона\u00A04", "Зона \t 4", "ЗОНА\u202F\u00A04" })
        {
            using var response = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = lookalike });
            await AssertConflictAsync(response, "zoneNameTaken");
        }

        foreach (var bad in new[] { "VIP\u200B", "\u200B", "\uFEFFVIP", "Зона\u200E 5", "Зона\u00075" })
        {
            using var response = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Contract.AssertError(await response.Content.ReadFromJsonAsync<JsonElement>(), "validation", "format");
        }

        using (var spaced = await Panel().PostAsJsonAsync("/panel/api/v1/zones", new { name = "\u00A0 Зона \t\u00A0 5 " }))
        {
            Assert.Equal(HttpStatusCode.Created, spaced.StatusCode);
            Assert.Equal("Зона 5", (await spaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        }

        var hall = await CreateZoneAsync("Мой зал");
        using var rename = await Panel().PatchAsJsonAsync($"/panel/api/v1/zones/{hall}", new { name = "VIP\u2060" });
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Contract.AssertError(await rename.Content.ReadFromJsonAsync<JsonElement>(), "validation", "format");
        Assert.Equal(["Standart", "VIP", "Зона 4", "Зона 5", "Мой зал"],
            (await OverviewAsync()).GetProperty("zones").EnumerateArray().Select(z => z.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Only_an_empty_non_default_zone_can_be_deleted()
    {
        var empty = await CreateZoneAsync("Пустая");
        using (var deleted = await Panel().DeleteAsync($"/panel/api/v1/zones/{empty}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.DoesNotContain((await OverviewAsync()).GetProperty("zones").EnumerateArray(), z => z.GetProperty("id").GetString() == empty);
        using (var again = await Panel().DeleteAsync($"/panel/api/v1/zones/{empty}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var standard = await Panel().DeleteAsync("/panel/api/v1/zones/standard"))
        {
            await AssertConflictAsync(standard, "defaultZone");
        }

        await AddSeatAsync(5);
        var used = await CreateZoneAsync("Зона 5");
        (await Panel().PostAsJsonAsync($"/panel/api/v1/zones/{used}/machines", new { numbers = new[] { 5 } })).Dispose();
        using (var inUse = await Panel().DeleteAsync($"/panel/api/v1/zones/{used}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
            var body = await inUse.Content.ReadFromJsonAsync<JsonElement>();
            Contract.AssertError(body, "conflict", "zoneInUse");
            Assert.Equal(1, body.GetProperty("error").GetProperty("details").GetProperty("machines").GetInt32());
        }

        Assert.Equal(used, ZoneOf(await OverviewAsync(), 5));
    }

    [Fact]
    public async Task Seats_are_assigned_in_bulk_and_unknown_numbers_reported()
    {
        foreach (var number in new[] { 11, 12, 13, 25 })
        {
            await AddSeatAsync(number);
        }

        var overview = await OverviewAsync();
        Assert.All(overview.GetProperty("machines").EnumerateArray(), m => Assert.Equal("standard", m.GetProperty("zone").GetString()));
        Assert.Equal(4, Zone(overview, "standard").GetProperty("machines").GetInt32());

        var zone4 = await CreateZoneAsync("Зона 4");
        using var assign = await Panel().PostAsJsonAsync($"/panel/api/v1/zones/{zone4}/machines", new { numbers = new[] { 11, 12, 14, 25, 25, 30 } });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var result = await assign.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, result.GetProperty("assigned").GetInt32());
        Assert.Equal([14, 30], result.GetProperty("unknown").EnumerateArray().Select(n => n.GetInt32()).ToArray());

        overview = await OverviewAsync();
        Assert.Equal((zone4, zone4, "standard", zone4), (ZoneOf(overview, 11), ZoneOf(overview, 12), ZoneOf(overview, 13), ZoneOf(overview, 25)));
        Assert.Equal((3, 1), (Zone(overview, zone4).GetProperty("machines").GetInt32(), Zone(overview, "standard").GetProperty("machines").GetInt32()));

        // Место по одному — как прежде, через PATCH машины; обратно в зону по умолчанию.
        var seat25 = Assert.Single(overview.GetProperty("machines").EnumerateArray(), m => m.GetProperty("number").GetInt32() == 25).GetProperty("id").GetGuid();
        using (var patch = await Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{seat25}", new { zone = "standard" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        }

        Assert.Equal("standard", ZoneOf(await OverviewAsync(), 25));

        using (var unknownZone = await Panel().PostAsJsonAsync("/panel/api/v1/zones/zone-000000000000/machines", new { numbers = new[] { 11 } }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownZone.StatusCode);
        }

        using (var empty = await Panel().PostAsJsonAsync($"/panel/api/v1/zones/{zone4}/machines", new { numbers = Array.Empty<int>() }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Contract.AssertError(await empty.Content.ReadFromJsonAsync<JsonElement>(), "validation", "required");
        }

        using var tooMany = await Panel().PostAsJsonAsync($"/panel/api/v1/zones/{zone4}/machines", new { numbers = Enumerable.Range(1, 1001).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(zone4, ZoneOf(await OverviewAsync(), 11));
    }

    /// <summary>
    /// Места назначают в зону, пока её удаляют: удаление насчитало 0 ПК и ждёт блокировку строки зоны (её держит проверка
    /// внешнего ключа в транзакции назначения). После коммита назначения — 409 <c>zoneInUse</c> с числом ПК на этот момент,
    /// зона и места на месте.
    /// </summary>
    [Fact]
    public async Task Seats_assigned_while_the_zone_is_deleted_keep_the_zone()
    {
        await AddSeatAsync(5);
        await AddSeatAsync(6);
        var zone = await CreateZoneAsync("Зона 5");
        var db = _server.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var c = await db.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await ExecAsync(c, tx, "UPDATE machines SET zone_id = @zone WHERE number IN (5, 6)", zone);
            var delete = Panel().DeleteAsync($"/panel/api/v1/zones/{zone}");
            await WaitForLockWaiterAsync(db, "DELETE FROM zones", delete);
            await tx.CommitAsync();
            using var response = await delete;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Contract.AssertError(body, "conflict", "zoneInUse");
            Assert.Equal(2, body.GetProperty("error").GetProperty("details").GetProperty("machines").GetInt32());
        }

        var overview = await OverviewAsync();
        Assert.Equal((zone, zone, 2), (ZoneOf(overview, 5), ZoneOf(overview, 6), Zone(overview, zone).GetProperty("machines").GetInt32()));
    }

    /// <summary>
    /// Зону удаляют, пока ПК переводят в неё через «Изменить»: PATCH уже проверил, что зона есть, и ждёт блокировку её
    /// строки в проверке внешнего ключа. После коммита удаления — 400 <c>zone/unknown</c> (панель: «Этой зоны уже нет»),
    /// ПК остался в прежней зоне.
    /// </summary>
    [Fact]
    public async Task Machine_moved_into_a_zone_being_deleted_gets_unknown_zone()
    {
        await AddSeatAsync(7);
        var zone = await CreateZoneAsync("Зона 7");
        var seat = Assert.Single((await OverviewAsync()).GetProperty("machines").EnumerateArray(), m => m.GetProperty("number").GetInt32() == 7).GetProperty("id").GetGuid();
        var db = _server.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var c = await db.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await ExecAsync(c, tx, "DELETE FROM zones WHERE id = @zone", zone);
            var patch = Panel().PatchAsJsonAsync($"/panel/api/v1/machines/{seat}", new { zone });
            await WaitForLockWaiterAsync(db, "UPDATE machines SET number", patch);
            await tx.CommitAsync();
            using var response = await patch;
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Contract.AssertError(body, "validation", "unknown");
            Assert.Equal("zone", body.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
        }

        Assert.Equal("standard", ZoneOf(await OverviewAsync(), 7));
    }

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string zone)
    {
        await using var command = new NpgsqlCommand(sql, c, tx);
        command.Parameters.AddWithValue("zone", zone);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Ждёт (до 10 с), пока запрос встанет на блокировке (pg_stat_activity); <paramref name="query"/> — начало его SQL.</summary>
    private static async Task WaitForLockWaiterAsync(NpgsqlDataSource db, string query, Task request)
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.False(request.IsCompleted, "the request finished before the commit: it did not wait for the lock");
            await using var command = db.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE @query");
            command.Parameters.AddWithValue("query", "%" + query + "%");
            if ((long)(await command.ExecuteScalarAsync())! >= 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"the request ({query}) did not wait for a lock");
    }

    [Fact]
    public async Task Zones_require_panel_token()
    {
        var http = _server.CreateClient();
        using var list = await http.GetAsync("/panel/api/v1/zones");
        using var create = await http.PostAsJsonAsync("/panel/api/v1/zones", new { name = "VIP" });
        using var rename = await http.PatchAsJsonAsync("/panel/api/v1/zones/standard", new { name = "VIP" });
        using var delete = await http.DeleteAsync("/panel/api/v1/zones/standard");
        using var assign = await http.PostAsJsonAsync("/panel/api/v1/zones/standard/machines", new { numbers = new[] { 1 } });
        Assert.All(new[] { list, create, rename, delete, assign }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal("Standart", Zone(await OverviewAsync(), "standard").GetProperty("name").GetString());
    }
}
