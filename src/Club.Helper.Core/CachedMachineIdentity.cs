using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>Факты Windows: полный опрос дорогой (CIM, Secure Boot, диск), IQN и «живые» факты — отдельно и дёшево.</summary>
public interface IMachineFactsSource
{
    /// <summary>
    /// Полный опрос: HWID, MAC, ОС, системный диск, Secure Boot. IQN инициатора сюда не входит: ради него запускается
    /// служба MSiSCSI, и её медленный старт не должен задерживать HWID и MAC, без которых нет регистрации, — поэтому
    /// <see cref="CachedMachineIdentity"/> читает IQN не раньше следующего такта.
    /// </summary>
    Task<MachineFacts> ReadAllAsync(CancellationToken ct);

    /// <summary>Только IQN инициатора iSCSI (служба инициатора запускается перед чтением); <c>null</c> — нет.</summary>
    Task<string?> ReadInitiatorIqnAsync(CancellationToken ct);

    /// <summary>То, что пересчитывается на каждый отчёт: время загрузки, аренды DHCP, версия образа.</summary>
    MachineFacts Refresh(MachineFacts facts);
}

/// <summary>
/// Факты о машине для отчётов: полный опрос — один раз за жизнь службы, «живые» факты — на каждый отчёт.
/// IQN инициатора читается отдельным коротким опросом, первый раз — через такт после полного: чтение, которое сделало
/// полный опрос, возвращается сразу, и регистрация и отчёты первого такта не ждут запуска MSiSCSI (до 10 с, а при
/// зависшем PowerShell — до его таймаута 120 с). Пока IQN неизвестен, он перечитывается не чаще раза за такт, а после
/// каждой неудачи вдвое реже, но не реже <see cref="MaxIqnBackoff"/>; короткий опрос в последующих тактах задерживает
/// свой отчёт, но только пока IQN не найден. При старте службы инициатор iSCSI мог ещё не отдавать порт (служба MSiSCSI
/// остановлена — так в Windows по умолчанию), а запомненный навсегда пустой IQN не давал выбрать ПК для правки
/// мастер-тома до перезапуска помощника (стенд, 1.4.0). Сбой короткого опроса (в том числе таймаут PowerShell) отчёт не
/// срывает: остальные факты уходят, IQN — <c>null</c>. Паузы отсчитываются по монотонным часам
/// (<see cref="TimeProvider.GetTimestamp"/>): перевод системного времени назад после загрузки (NTP поправил RTC)
/// не откладывает опрос IQN на величину перевода.
/// </summary>
public sealed class CachedMachineIdentity(IMachineFactsSource source, HelperOptions options, TimeProvider clock, ILogger<CachedMachineIdentity> logger)
    : IMachineIdentity
{
    /// <summary>Самая длинная пауза между опросами IQN: ПК без инициатора не запускает PowerShell каждый такт.</summary>
    public static readonly TimeSpan MaxIqnBackoff = TimeSpan.FromMinutes(10);

    private MachineFacts? _cached;
    private long _iqnPauseFrom;
    private TimeSpan _iqnPause;
    private int _iqnMisses;

    /// <summary>Такт помощника — как в HelperService.</summary>
    private TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(5, options.PollIntervalSec));

    public async Task<MachineFacts> ReadAsync(CancellationToken ct)
    {
        if (_cached is null)
        {
            var facts = await source.ReadAllAsync(ct);
            _cached = string.IsNullOrWhiteSpace(facts.InitiatorIqn) ? facts with { InitiatorIqn = null } : facts;

            // Первый короткий опрос — на следующем такте, а не сразу: иначе первая регистрация (или первый отчёт) ждала
            // бы запуска MSiSCSI. Следующий такт начинается не раньше чем через интервал после конца этого.
            PauseIqnReads(Poll);
            return source.Refresh(_cached);
        }

        if (_cached.InitiatorIqn is null && clock.GetElapsedTime(_iqnPauseFrom) >= _iqnPause)
        {
            await ReadInitiatorIqnAsync(ct);
        }

        return source.Refresh(_cached);
    }

    private async Task ReadInitiatorIqnAsync(CancellationToken ct)
    {
        string? iqn;
        string? failure = null;
        try
        {
            iqn = await source.ReadInitiatorIqnAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Отмена при живом токене службы (так до 1.4.1 приходил таймаут PowerShell) — тоже сбой опроса, а не
            // остановка: выпущенная наружу, она прошла бы мимо фильтра HelperService и завершила бы службу.
            iqn = null;
            failure = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(iqn))
        {
            logger.LogInformation("iSCSI initiator IQN found: {Iqn}", iqn);
            _cached = _cached! with { InitiatorIqn = iqn };
            return;
        }

        _iqnMisses++;
        var delay = Poll * (1 << Math.Min(_iqnMisses - 1, 10)); // такт, 2 такта, 4 такта…
        delay = delay < MaxIqnBackoff ? delay : MaxIqnBackoff;
        PauseIqnReads(delay);
        var reason = failure ?? "no iSCSI initiator port, is the MSiSCSI service running?";
        if (_iqnMisses == 1)
        {
            // Одно предупреждение в журнал Windows, а не на каждый опрос: оператор увидит, почему ПК нет в выборе для правки.
            logger.LogWarning(
                "iSCSI initiator IQN is not available ({Reason}); this PC cannot be chosen to edit the master volume until it appears; retrying with pauses growing up to {Max} min",
                reason, MaxIqnBackoff.TotalMinutes);
        }
        else
        {
            logger.LogDebug("iSCSI initiator IQN still not available ({Reason}); next read in {Delay}", reason, delay);
        }
    }

    /// <summary>Следующий короткий опрос — не раньше чем через <paramref name="pause"/> от этого момента (монотонно).</summary>
    private void PauseIqnReads(TimeSpan pause)
    {
        _iqnPauseFrom = clock.GetTimestamp();
        _iqnPause = pause;
    }
}
