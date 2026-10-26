using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Club.Server.Tests;

/// <summary>
/// Провод G: для помощника 1.5.1 рядом со второй библиотекой (план §5 п. 4, «никогда не вырезать»): старые пути
/// <c>…/volume</c> и <c>…/status</c> отвечают байт в байт так же, как без H:.
/// </summary>
public sealed partial class GamesWireTests
{
    [GeneratedRegex("\"(chapSecret|serverTime)\":\"[^\"]*\"")]
    private static partial Regex Varying();

    /// <summary>Тело ответа как есть, секреты CHAP и время сервера заменены на <c>*</c> (они разные при каждом запуске).</summary>
    private static async Task<string> RawAsync(HttpResponseMessage response) => Varying().Replace(await response.Content.ReadAsStringAsync(), "\"$1\":\"*\"");

    /// <summary>
    /// Старые пути G: для помощника 1.5.1 отвечают байт в байт так же с H: и без неё — даже когда на этом же ПК есть личный
    /// диск H: и открыта правка H: (откат помощника с 1.6.0 на 1.5.1 при открытой правке): ни тома, ни мастер-тома, ни
    /// libraryLetterReserved, ни заголовка от H:. Ответы сверяются с записанными строками целиком.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_g_wire_for_helper_1_5_1_is_byte_identical_with_h_on_and_an_h_master_open(bool solo)
    {
        await using var stand = await SoloStand.StartAsync(solo);
        await stand.PublishAsync(stand.Games, "v1");
        var pc = await stand.RegisterAsync(1); // помощник 1.6.0: личный диск G: собран в ответе на отчёт
        if (solo)
        {
            await stand.PublishAsync(stand.Solo, "v1");
            using (var boot = await SoloStand.SoloBootAsync(pc))
            {
                Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
            }

            await stand.OpenMasterAsync(stand.Solo, pc);
            using var report = await SoloStand.SoloStatusAsync(pc, SoloWireTests.SoloReport(master: new { state = "mounted", targetIqn = $"{SoloStand.Basename}:club-master-solo", driveLetter = "H" }));
            Assert.True((await report.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraryLetterReserved").GetBoolean());
        }

        const string Volume = """{"libraryVersion":"v1","portal":"192.168.77.10:3260","targetIqn":"iqn.2005-10.org.freenas.ctl:games-seat-01","readOnly":false,"driveLetter":"G","chapUser":"games-seat-01","chapSecret":"*"}""";
        using (var status = await SoloStand.GamesStatusAsync(pc, "1.5.1", 1))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal($$"""{"serverTime":"*","volume":{{Volume}}}""", await RawAsync(status));
            Assert.Equal("application/json; charset=utf-8", status.Content.Headers.ContentType?.ToString());
        }

        using (var boot = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=false"))
        {
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
            Assert.False(boot.Headers.Contains("X-Library-Letter-Reserved"));
            Assert.Equal(Volume, await RawAsync(boot));
        }

        // Правка G: на этом ПК (мастер-том на G:, как в клубе): тот же ответ с H: и без.
        await stand.OpenMasterAsync(stand.Games, pc);
        using (var status = await SoloStand.GamesStatusAsync(pc, "1.5.1", 1))
        {
            Assert.Equal(
                """{"serverTime":"*","master":{"targetIqn":"iqn.2005-10.org.freenas.ctl:club-master","portal":"192.168.77.10:3260","chapUser":"clubsrv-master","chapSecret":"*","driveLetter":"G"},"libraryLetterReserved":true}""",
                await RawAsync(status));
        }

        using (var boot = await pc.SendAsync(HttpMethod.Get, $"/diskless/v1/machines/{pc.MachineId}/volume?attached=true"))
        {
            Assert.Equal(HttpStatusCode.NoContent, boot.StatusCode);
            Assert.Equal(["true"], boot.Headers.GetValues("X-Library-Letter-Reserved"));
            Assert.Equal("", await boot.Content.ReadAsStringAsync());
        }
    }
}
