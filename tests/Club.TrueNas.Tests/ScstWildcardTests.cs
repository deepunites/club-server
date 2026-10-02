namespace Club.TrueNas.Tests;

/// <summary>Строки групп инициаторов сравниваются с IQN, как в SCST (wildcmp, TS-25.10.7).</summary>
public sealed class ScstWildcardTests
{
    private const string Probe = "iqn.2026-10.local.clubsrv:probe";
    private const string Pc = "iqn.1991-05.com.microsoft:pc-07";

    /// <summary>Первые два — примеры из комментария к wildcmp в SCST; остальные — строки групп, которые встречаются в клубе.</summary>
    [Theory]
    [InlineData("bl?h.*", "blah.jpg", true)]
    [InlineData("bl!?h.*", "blah.jpg", false)]
    [InlineData("*", Probe, true)]
    [InlineData("iqn.1991-05.com.microsoft:*", Probe, false)]
    [InlineData("iqn.1991-05.com.microsoft:*", Pc, true)]
    [InlineData("!iqn.1991-05.com.microsoft:*", Probe, true)]
    [InlineData("!iqn.1991-05.com.microsoft:*", Pc, false)]
    [InlineData("!" + Pc, Probe, true)]
    [InlineData("IQN.2026-10.LOCAL.CLUBSRV:PROBE", Probe, true)] // регистр не различается
    [InlineData(Probe, "IQN.2026-10.LOCAL.CLUBSRV:PROBE", true)]
    [InlineData("iqn.2026-10.local.clubsrv:prob?", Probe, true)]
    [InlineData("iqn.2026-10.local.clubsrv:probe?", Probe, false)]
    [InlineData("iqn.2026-10.local.clubsrv:pro", Probe, false)]
    [InlineData("iqn.*:probe", Probe, true)]
    [InlineData("iqn.*:pc-*", Probe, false)]
    [InlineData("*:p*e", Probe, true)]
    [InlineData("iqn.2026-10.local.clubsrv:probe!", Probe, false)] // как в C: после конца имени «!» — лишний символ
    [InlineData("*!" + Probe, Probe, false)] // как в C: «*» перед «!» ничего не забирает
    [InlineData("", Probe, false)]
    public void Initiator_patterns_match_like_scst(string pattern, string name, bool expected) =>
        Assert.Equal(expected, ScstWildcard.Matches(pattern, name));
}
