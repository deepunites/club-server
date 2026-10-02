using Club.Helper.Core;
using Microsoft.Extensions.Logging;

namespace Club.Helper.Tests;

/// <summary>
/// Кэш фактов машины: полный опрос — один раз, IQN инициатора — коротким опросом со следующего такта и, пока IQN нет,
/// не чаще раза за такт с нарастающей паузой. Стенд 2026-10-02 (помощник 1.4.0): MSiSCSI была остановлена при старте
/// службы, пустой IQN запомнился до перезапуска помощника.
/// </summary>
public sealed class MachineIdentityTests
{
    private const string Iqn = "iqn.1991-05.com.microsoft:pc-test";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private readonly FakeFactsSource _source = new("hwid-1");
    private readonly ManualClock _clock = new();
    private readonly ListLogger<CachedMachineIdentity> _log = new();
    private readonly CachedMachineIdentity _identity;

    public MachineIdentityTests() =>
        _identity = new CachedMachineIdentity(_source, new HelperOptions { PollIntervalSec = (int)Poll.TotalSeconds }, _clock, _log);

    private async Task<string?> ReadIqnAsync() => (await _identity.ReadAsync(CancellationToken.None)).InitiatorIqn;

    private (LogLevel Level, string Message) SingleWarning() => Assert.Single(_log.Entries, e => e.Level >= LogLevel.Warning);

    /// <summary>Первое чтение (полный опрос) и следующий такт, на котором идёт первый короткий опрос IQN.</summary>
    private async Task<string?> FirstTickThenNextAsync()
    {
        Assert.Null(await ReadIqnAsync());
        _clock.Advance(Poll);
        return await ReadIqnAsync();
    }

    [Fact]
    public async Task First_read_returns_right_after_the_full_read_without_waiting_for_the_iqn()
    {
        // Короткий опрос запускает MSiSCSI (до 10 с, при зависшем PowerShell — до 120 с): регистрация и отчёты первого
        // такта его не ждут, IQN уходит со следующего такта.
        _source.InitiatorIqn = Iqn;
        _source.IqnReadError = new InvalidOperationException("must not be called in the first tick");

        var facts = await _identity.ReadAsync(CancellationToken.None);
        Assert.Equal(("hwid-1", (string?)null), (facts.Hwid, facts.InitiatorIqn));
        Assert.Null(await ReadIqnAsync()); // регистрация, отчёт, повторный отчёт — тот же такт
        Assert.Null(await ReadIqnAsync());
        Assert.Equal((1, 0), (_source.FullReads, _source.IqnReads));

        _source.IqnReadError = null;
        _clock.Advance(Poll);
        Assert.Equal(Iqn, await ReadIqnAsync());
        Assert.Equal((1, 1), (_source.FullReads, _source.IqnReads));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Full_read_happens_once_and_live_facts_are_refreshed_on_every_read()
    {
        _source.InitiatorIqn = Iqn;
        Assert.Equal(Iqn, await FirstTickThenNextAsync());

        _source.DhcpServers.Add("192.168.77.1"); // аренда сменилась без перезапуска службы
        _clock.Advance(Poll);
        var facts = await _identity.ReadAsync(CancellationToken.None);

        Assert.Equal(["192.168.77.1"], facts.DhcpServers ?? []);
        Assert.Equal(("hwid-1", Iqn), (facts.Hwid, facts.InitiatorIqn));
        Assert.Equal((1, 1), (_source.FullReads, _source.IqnReads)); // IQN известен — больше не перечитывается
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Missing_iqn_is_reread_once_per_poll_interval_until_found_then_cached()
    {
        Assert.Null(await FirstTickThenNextAsync());
        Assert.Equal((1, 1), (_source.FullReads, _source.IqnReads)); // короткий опрос — на такте после полного

        Assert.Null(await ReadIqnAsync()); // в том же такте (отчёт, повторный отчёт) — без опроса
        Assert.Null(await ReadIqnAsync());
        Assert.Equal(1, _source.IqnReads);

        _clock.Advance(Poll);
        Assert.Null(await ReadIqnAsync());
        Assert.Equal(2, _source.IqnReads);

        _source.InitiatorIqn = Iqn; // служба инициатора запустилась и отдала порт
        _clock.Advance(2 * Poll);
        Assert.Equal(Iqn, await ReadIqnAsync());
        _clock.Advance(100 * Poll);
        Assert.Equal(Iqn, await ReadIqnAsync());

        Assert.Equal((1, 3), (_source.FullReads, _source.IqnReads)); // найден — больше не перечитывается
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(Iqn));
    }

    [Fact]
    public async Task Pause_between_iqn_reads_doubles_after_each_miss_up_to_ten_minutes()
    {
        await FirstTickThenNextAsync(); // промах 1 — следующий опрос через такт

        var pauses = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            var reads = _source.IqnReads;
            var waited = TimeSpan.Zero;
            while (_source.IqnReads == reads)
            {
                _clock.Advance(TimeSpan.FromSeconds(5));
                waited += TimeSpan.FromSeconds(5);
                await ReadIqnAsync();
            }

            pauses.Add(waited);
        }

        Assert.Equal(new[] { 30, 60, 120, 240, 480, 600, 600, 600 }.Select(s => TimeSpan.FromSeconds(s)), pauses);
        Assert.Equal(TimeSpan.FromMinutes(10), CachedMachineIdentity.MaxIqnBackoff);
    }

    [Fact]
    public async Task Missing_iqn_is_logged_as_a_warning_once()
    {
        Assert.Null(await ReadIqnAsync()); // полный опрос, IQN — со следующего такта
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(CachedMachineIdentity.MaxIqnBackoff);
            Assert.Null(await ReadIqnAsync());
        }

        Assert.Equal(5, _source.IqnReads);
        Assert.Contains("MSiSCSI", SingleWarning().Message);
    }

    [Fact]
    public async Task Failed_iqn_read_does_not_break_the_report_and_backs_off_too()
    {
        _source.IqnReadError = new InvalidOperationException("PowerShell failed (1)");

        await _identity.ReadAsync(CancellationToken.None);
        _clock.Advance(Poll);
        var facts = await _identity.ReadAsync(CancellationToken.None);
        Assert.Equal(("hwid-1", (string?)null), (facts.Hwid, facts.InitiatorIqn));
        Assert.Contains("PowerShell failed (1)", SingleWarning().Message);

        _clock.Advance(Poll);
        Assert.Null(await ReadIqnAsync());
        _clock.Advance(Poll); // после второго промаха пауза — два такта: опроса нет
        Assert.Null(await ReadIqnAsync());
        Assert.Equal(2, _source.IqnReads);

        _source.IqnReadError = null;
        _source.InitiatorIqn = Iqn;
        _clock.Advance(Poll);
        Assert.Equal(Iqn, await ReadIqnAsync());
    }

    [Fact]
    public async Task Powershell_timeout_while_the_service_runs_is_a_failed_read_not_a_stop()
    {
        // PowerShell.RunAsync снимает зависший powershell.exe по своему таймауту; токен службы при этом жив.
        _source.IqnReadError = new OperationCanceledException("PowerShell timed out");

        await _identity.ReadAsync(CancellationToken.None);
        _clock.Advance(Poll);
        var facts = await _identity.ReadAsync(CancellationToken.None);

        Assert.Equal(("hwid-1", (string?)null), (facts.Hwid, facts.InitiatorIqn));
        Assert.Contains("PowerShell timed out", SingleWarning().Message);
    }

    [Fact]
    public async Task Service_stop_during_iqn_read_propagates()
    {
        using var stopping = new CancellationTokenSource();
        await _identity.ReadAsync(stopping.Token); // полный опрос; IQN — на следующем такте
        _clock.Advance(Poll);
        await stopping.CancelAsync();
        _source.IqnReadError = new OperationCanceledException(stopping.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _identity.ReadAsync(stopping.Token));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Wall_clock_step_back_after_boot_does_not_delay_the_iqn_read()
    {
        // Windows стартовала с часами RTC впереди, NTP после старта службы перевёл время на часы назад: паузы опроса IQN
        // считаются по монотонным часам, иначе первый опрос отложился бы на величину перевода.
        Assert.Null(await ReadIqnAsync()); // полный опрос
        _clock.StepWallClock(TimeSpan.FromHours(-5));
        _clock.Advance(Poll);
        Assert.Null(await ReadIqnAsync()); // первый короткий опрос — вовремя; промах
        Assert.Equal(1, _source.IqnReads);

        _source.InitiatorIqn = Iqn;
        _clock.StepWallClock(TimeSpan.FromHours(-5));
        _clock.Advance(Poll); // пауза после первого промаха — такт
        Assert.Equal(Iqn, await ReadIqnAsync());
        Assert.Equal(2, _source.IqnReads);
    }

    [Fact]
    public async Task Blank_iqn_counts_as_missing()
    {
        _source.InitiatorIqn = "  ";
        Assert.Null(await FirstTickThenNextAsync());
        _clock.Advance(Poll);
        Assert.Null(await ReadIqnAsync());
        Assert.Equal(2, _source.IqnReads);
        SingleWarning();
    }
}
