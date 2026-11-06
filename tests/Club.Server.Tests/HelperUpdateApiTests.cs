using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Club.Server.Data;
using Club.Server.HelperUpdates;
using Club.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Club.Server.HelperUpdates.HelperUpdateCodes;

namespace Club.Server.Tests;

/// <summary>
/// Самообновление помощника на проводе G: (spec 2026-11-06, п. 3.4): <c>helperBuild</c> в отчёте и регистрации,
/// <c>helperUpdate</c> в отчёте и в ответе. Незнакомые значения — 200 и запись отчёта; поле в ответе — только у активного
/// поручения; предел активных при одновременных отчётах. Сервер на тест — свой (своя база и каталог <c>tools</c>), работник
/// очереди выключен (ServerFixture): тест сам решает, когда что происходит.
/// </summary>
public sealed class HelperUpdateApiTests : IAsyncLifetime
{
    private const string Build181 = "ClubDisklessHelper-1.8.1.exe";
    private readonly ServerFixture _server = new();
    private readonly string _tools = Path.Combine(Path.GetTempPath(), $"club-helperapi-{Guid.NewGuid():N}");
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tools);
        var path = Path.Combine(_tools, Build181);
        await File.WriteAllTextAsync(path, "helper 1.8.1");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
        _server.Settings["HelperUpdates:ToolsDir"] = _tools;
        await _server.InitializeAsync();
        _http = _server.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await ((IAsyncLifetime)_server).DisposeAsync();
        Directory.Delete(_tools, recursive: true);
    }

    private static string Sha181 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("helper 1.8.1")));

    private HelperUpdateRepository Queue => _server.Services.GetRequiredService<HelperUpdateRepository>();

    /// <summary>Регистрация, как у помощника 1.8.0: с версией и, если задано, сборкой (<c>helperBuild</c>).</summary>
    private async Task<(TestMachine Machine, int Number)> RegisterAsync(string helperVersion = "1.8.0", string? helperBuild = null, string? hwid = null, string? mac = null)
    {
        hwid ??= Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/diskless/v1/machines/register")
        {
            Content = JsonContent.Create(
                new { hwid, hostname = "PC-HU", macAddresses = new[] { mac ?? Mac() }, helperVersion, osVersion = "Windows 11 Pro 24H2", helperBuild },
                options: Api.ApiJson.Options),
        };
        request.Headers.Add("X-Club-Key", ServerFixture.ClubKey);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        Contract.AssertMatches("MachineRegisterResponse", body);
        var machine = new TestMachine(_http, body.GetProperty("machineId").GetGuid(), hwid, body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
        return (machine, body.GetProperty("number").GetInt32());
    }

    private static string Mac()
    {
        var b = Guid.NewGuid().ToByteArray();
        return $"02:00:00:{b[0]:x2}:{b[1]:x2}:{b[2]:x2}";
    }

    /// <summary>Отчёт G:; null-поля не передаются (как у помощника, который их не знает).</summary>
    private static object Report(string helperVersion = "1.8.0", string? helperBuild = null, object? helperUpdate = null) => new
    {
        helperVersion,
        bootTime = DateTimeOffset.UtcNow.AddMinutes(-5),
        volume = new { state = "none" },
        helperBuild,
        helperUpdate,
    };

    private static async Task<JsonElement> StatusAsync(TestMachine machine, object report)
    {
        using var response = await machine.SendAsync(HttpMethod.Put, $"/diskless/v1/machines/{machine.MachineId}/status", report);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Contract.ReadAsync(response, "StatusAccepted");
    }

    private async Task<string?> HelperBuildAsync(Guid machineId) =>
        (await _server.Services.GetRequiredService<MachineRepository>().FindAsync(machineId))!.HelperBuild;

    private async Task<int> ActiveCountAsync()
    {
        await using var c = await _server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM helper_updates WHERE state = 'active'");
    }

    private async Task<(string State, string? Progress)> JobAsync(Guid machineId)
    {
        await using var c = await _server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<(string, string?)>("SELECT state, progress FROM helper_updates WHERE machine_id = @machineId", new { machineId });
    }

    /// <summary>Без поручения в ответе нет ключа <c>helperUpdate</c> — ни для 1.8.0, ни для отчёта старого помощника.</summary>
    [Fact]
    public async Task Status_WithoutUpdate_NoHelperUpdateField()
    {
        var (machine, _) = await RegisterAsync();
        var body = await StatusAsync(machine, Report(helperBuild: Sha181));
        Assert.False(body.TryGetProperty("helperUpdate", out _), body.ToString());

        body = await StatusAsync(machine, Report(helperVersion: "1.5.1"));
        Assert.False(body.TryGetProperty("helperUpdate", out _), body.ToString());
    }

    /// <summary>Активное поручение — в ответе: <c>id</c>, <c>version</c>, <c>sha256</c>, <c>size</c>, относительный <c>path</c>.</summary>
    [Fact]
    public async Task Status_ActiveUpdate_OfferInReply()
    {
        var (machine, number) = await RegisterAsync();
        Assert.Equal([number], (await Queue.LaunchAsync(Build181, [number], false, "panel", DateTimeOffset.UtcNow)).Queued);

        var offer = (await StatusAsync(machine, Report())).GetProperty("helperUpdate");
        var job = (await Queue.ListAsync(DateTimeOffset.UtcNow)).Single();
        Assert.Equal(job.Id, offer.GetProperty("id").GetGuid());
        Assert.Equal("1.8.1", offer.GetProperty("version").GetString());
        Assert.Equal(Sha181, offer.GetProperty("sha256").GetString());
        Assert.Equal("helper 1.8.1".Length, offer.GetProperty("size").GetInt64());
        Assert.Equal("pxe/v1/files/tools/" + Build181, offer.GetProperty("path").GetString());
        Assert.Equal(5, offer.EnumerateObject().Count());

        // Прогресс от помощника принят, поручение по-прежнему в ответе.
        var again = await StatusAsync(machine, Report(helperUpdate: new { id = job.Id.ToString(), state = "downloading" }));
        Assert.Equal(job.Id, again.GetProperty("helperUpdate").GetProperty("id").GetGuid());
        Assert.Equal((States.Active, Progress.Downloading), await JobAsync(machine.MachineId));
    }

    /// <summary>
    /// Незнакомые <c>state</c>/<c>error</c>, кривой <c>id</c> и <c>helperBuild</c> — 200, отчёт записан, значения
    /// пропущены (Review Focus 4): ПК не «пропадает» из панели.
    /// </summary>
    [Fact]
    public async Task Status_UnknownValues_Return200()
    {
        var (machine, number) = await RegisterAsync(helperBuild: Sha181.Replace('a', 'b'));
        await Queue.LaunchAsync(Build181, [number], false, "panel", DateTimeOffset.UtcNow);
        Assert.True((await StatusAsync(machine, Report())).TryGetProperty("helperUpdate", out var offer));
        var id = offer.GetProperty("id").GetString()!;

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var body = await StatusAsync(machine, Report(helperVersion: "1.8.2", helperBuild: "zz",
            helperUpdate: new { id = "not-guid", state = "weird", error = "x", reason = "?", message = "m" }));
        Assert.True(body.TryGetProperty("helperUpdate", out _));
        var row = (await _server.Services.GetRequiredService<MachineRepository>().FindAsync(machine.MachineId))!;
        Assert.Null(row.HelperBuild);
        Assert.Equal("1.8.2", row.HelperVersion);
        Assert.True(row.LastSeenAt >= before);

        body = await StatusAsync(machine, Report(helperBuild: new string('g', 64), helperUpdate: new { id, state = "weird", error = "x" }));
        Assert.True(body.TryGetProperty("helperUpdate", out _));
        Assert.Null(await HelperBuildAsync(machine.MachineId));
        Assert.Equal((States.Active, (string?)null), await JobAsync(machine.MachineId));

        body = await StatusAsync(machine, Report(helperUpdate: new { id, state = "waiting", reason = "weird" }));
        Assert.True(body.TryGetProperty("helperUpdate", out _));
        Assert.Equal((States.Active, Progress.Waiting), await JobAsync(machine.MachineId));
    }

    /// <summary><c>helper_build</c> — как есть из каждого отчёта (без «оставить прежнее»): отчёт без поля его обнуляет.</summary>
    [Fact]
    public async Task Status_HelperBuildCleared_WhenFieldAbsent()
    {
        var (machine, _) = await RegisterAsync();
        await StatusAsync(machine, Report(helperBuild: "  " + Sha181.ToUpperInvariant() + " "));
        Assert.Equal(Sha181, await HelperBuildAsync(machine.MachineId));

        await StatusAsync(machine, Report(helperVersion: "1.7.0"));
        Assert.Null(await HelperBuildAsync(machine.MachineId));
    }

    /// <summary>Регистрация передаёт <c>helperBuild</c>: новая машина, повторная регистрация и заготовка из панели по MAC.</summary>
    [Fact]
    public async Task Register_StoresHelperBuild()
    {
        var hwid = Guid.NewGuid().ToString("N");
        var (created, _) = await RegisterAsync(helperBuild: Sha181, hwid: hwid);
        Assert.Equal(Sha181, await HelperBuildAsync(created.MachineId));

        var (again, _) = await RegisterAsync(helperVersion: "1.7.0", hwid: hwid);
        Assert.Equal(created.MachineId, again.MachineId);
        Assert.Null(await HelperBuildAsync(created.MachineId));

        await RegisterAsync(helperBuild: "not-a-sha", hwid: hwid);
        Assert.Null(await HelperBuildAsync(created.MachineId));

        var mac = Mac();
        var (result, placeholder) = await _server.Services.GetRequiredService<MachineRepository>().AddByMacAsync(mac, null, null, "local");
        Assert.Equal(MachineRepository.AddResult.Added, result);
        var (adopted, _) = await RegisterAsync(helperBuild: Sha181.ToUpperInvariant(), mac: mac);
        Assert.Equal(placeholder!.Id, adopted.MachineId);
        Assert.Equal(Sha181, await HelperBuildAsync(adopted.MachineId));
    }

    /// <summary>Десять ПК отчитываются одновременно при двух активных — активных ровно три (Review Focus 1).</summary>
    [Fact]
    public async Task Status_ConcurrentReports_MaxActiveHeld()
    {
        var pcs = new List<(TestMachine Machine, int Number)>();
        for (var i = 0; i < 12; i++)
        {
            pcs.Add(await RegisterAsync());
        }

        Assert.Equal(12, (await Queue.LaunchAsync(Build181, pcs.Select(p => p.Number).ToList(), false, "panel", DateTimeOffset.UtcNow)).Queued.Count);
        Assert.True((await StatusAsync(pcs[0].Machine, Report())).TryGetProperty("helperUpdate", out _));
        Assert.True((await StatusAsync(pcs[1].Machine, Report())).TryGetProperty("helperUpdate", out _));
        Assert.Equal(2, await ActiveCountAsync());

        var replies = await Task.WhenAll(pcs.Skip(2).Select(p => StatusAsync(p.Machine, Report())));
        Assert.Equal(1, replies.Count(r => r.TryGetProperty("helperUpdate", out _)));
        Assert.Equal(3, await ActiveCountAsync());
    }
}
