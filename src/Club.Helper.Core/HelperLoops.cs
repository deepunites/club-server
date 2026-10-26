using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Помощник целиком: цикл G: и по циклу на каждую библиотеку из <see cref="HelperOptions.Libraries"/> (1.6.0: H: — solo).
/// Такт — по очереди, не параллельно: сначала G: (его отчёт сообщает серверу версию помощника, а по ней сервер решает,
/// давать ли ПК диск H:), потом остальные, каждый в своём try/catch — сбой одного цикла не отменяет другие. Windows не
/// ответила в такте G: (таймаут PowerShell, 120 с) — остальные в этом такте пропускаются: их опросы Windows ждали бы ещё
/// столько же, а с ними и отчёт G: следующего такта. Клиент API у всех циклов один (<see cref="DisklessApiClient"/>:
/// одноразовый refresh-токен, обновляет его тот, чей запрос первым получил 401).
/// Отчёт G: не должен ждать библиотек: сервер считает ПК «не на связи», когда отчёта G: нет 90 с (панель, приём версии,
/// кандидаты бездиска). Поэтому пауза до следующего такта короче на время тактов библиотек (<see cref="DelayAfterTick"/>):
/// такт H: до <see cref="SlowTick"/> (ожидание диска, запрос при старте, пока сервер собирает диск) сдвигает отчёт G: не
/// дальше обычного. Такт библиотеки дольше или с таймаутом Windows — она пропускает следующие такты
/// (<see cref="FirstBackoff"/>, подряд — вдвое дольше, до <see cref="MaxBackoff"/>): зависший диск H: задерживает отчёт
/// G: один раз за паузу, а не в каждом такте.
/// </summary>
public sealed class HelperLoops(HelperLoop games, IReadOnlyList<HelperLoop> libraries, ILogger logger, TimeProvider? clock = null)
{
    /// <summary>Такт библиотеки не дольше этого не сдвигает отчёт G: (пауза до такта короче на столько же).</summary>
    public static readonly TimeSpan SlowTick = TimeSpan.FromSeconds(40);

    /// <summary>Сколько библиотека пропускает такты после медленного такта; подряд — вдвое дольше.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Дольше этого библиотека такты не пропускает (как сон цикла без библиотеки на сервере).</summary>
    public static readonly TimeSpan MaxBackoff = HelperLoop.DormantFor;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Пауза библиотеки после медленного такта: с какой отметки и на сколько (<see cref="TimeSpan.Zero"/> — паузы нет).</summary>
    private readonly (long Since, TimeSpan Wait)[] _backoff = new (long, TimeSpan)[libraries.Count];

    public HelperLoop Games => games;

    public IReadOnlyList<HelperLoop> Libraries => libraries;

    /// <summary>Сколько в последнем такте шли такты библиотек (после такта G:).</summary>
    public TimeSpan LibrariesElapsed { get; private set; }

    /// <summary>
    /// Пауза до следующего такта: <paramref name="interval"/> минус время тактов библиотек — отчёт G: идёт с прежним шагом
    /// (такт G: + интервал), сколько бы ни шли такты библиотек до <see cref="SlowTick"/>. Чаще, чем раз в интервал, такт
    /// G: не начинается.
    /// </summary>
    public TimeSpan DelayAfterTick(TimeSpan interval) => interval > LibrariesElapsed ? interval - LibrariesElapsed : TimeSpan.Zero;

    public async Task TickAsync(CancellationToken ct)
    {
        LibrariesElapsed = TimeSpan.Zero;
        try
        {
            await games.TickAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Любой сбой такта — в журнал; том остаётся как есть, следующий такт попробует снова.
            logger.LogError(ex, "Helper tick failed");
        }

        if (libraries.Count == 0)
        {
            return;
        }

        if (games.WindowsTimedOut)
        {
            logger.LogDebug("Libraries not ticked this time: Windows did not answer while applying the G: volume");
            return;
        }

        var started = _clock.GetTimestamp();
        for (var i = 0; i < libraries.Count; i++)
        {
            var library = libraries[i];
            var (since, wait) = _backoff[i];
            if (wait > TimeSpan.Zero && _clock.GetElapsedTime(since) < wait)
            {
                logger.LogDebug("Library {Library} not ticked: its last tick was slow; next try in {Left}", library.Library, wait - _clock.GetElapsedTime(since));
                continue;
            }

            var tick = _clock.GetTimestamp();
            try
            {
                await library.TickAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Helper tick of library {Library} failed", library.Library);
            }

            Measure(i, library, _clock.GetElapsedTime(tick));
        }

        LibrariesElapsed = _clock.GetElapsedTime(started);
    }

    /// <summary>Такт библиотеки шёл <paramref name="took"/>: медленный (или Windows не ответила) — пауза, быстрый — паузы больше нет.</summary>
    private void Measure(int i, HelperLoop library, TimeSpan took)
    {
        var previous = _backoff[i].Wait;
        if (took >= SlowTick || library.WindowsTimedOut)
        {
            var wait = previous == TimeSpan.Zero ? FirstBackoff : previous * 2 < MaxBackoff ? previous * 2 : MaxBackoff;
            _backoff[i] = (_clock.GetTimestamp(), wait);
            if (previous == TimeSpan.Zero)
            {
                logger.LogWarning(
                    "Library {Library} tick took {Took:g} (Windows timed out: {TimedOut}); not ticking it for {Wait} so that G: keeps reporting on time",
                    library.Library, took, library.WindowsTimedOut, wait);
            }
            else
            {
                logger.LogDebug("Library {Library} tick was slow again ({Took:g}); not ticking it for {Wait}", library.Library, took, wait);
            }
        }
        else if (previous > TimeSpan.Zero)
        {
            _backoff[i] = (0, TimeSpan.Zero);
            logger.LogInformation("Library {Library} ticks on time again ({Took:g})", library.Library, took);
        }
    }

    /// <summary>
    /// Циклы помощника над одной Windows и одним клиентом API. У каждой библиотеки — свои VolumeManager и MasterManager с
    /// её метками, свой файл назначения (<paramref name="cache"/> по ключу; <c>null</c> — G:) и свои категории журнала
    /// (<c>Club.Helper.Core.HelperLoop.solo</c> и т. п.; у G: — прежние). Ключ, который сервер не принял бы (см.
    /// <see cref="LibraryMarkers.IsValidKey"/>), пропускается с предупреждением: G: из-за него не останавливается.
    /// </summary>
    public static HelperLoops Create(
        DisklessApiClient api, IWindowsStorage storage, IProcessInspector processes, IMachineIdentity identity, HelperOptions options, TimeProvider clock,
        ILoggerFactory loggers, Func<string?, IAssignmentCache> cache, ILogger? logger = null)
    {
        logger ??= loggers.CreateLogger<HelperLoops>();
        var libraries = new List<HelperLoop>();
        foreach (var key in options.Libraries.Distinct(StringComparer.Ordinal))
        {
            if (!LibraryMarkers.IsValidKey(key))
            {
                logger.LogWarning("Library {Library} in helper.json skipped: a key is 2-12 lowercase letters and digits, not one of {Reserved}",
                    key, string.Join(", ", LibraryMarkers.ReservedKeys));
                continue;
            }

            libraries.Add(Loop(LibraryMarkers.For(key)));
        }

        return new HelperLoops(Loop(LibraryMarkers.Games), libraries, logger, clock);

        HelperLoop Loop(LibraryMarkers markers)
        {
            var key = markers.Key;
            var volumes = new VolumeManager(storage, processes, options, clock, Category<VolumeManager>(key), markers);
            var masters = new MasterManager(storage, options, clock, Category<MasterManager>(key), markers);
            return new HelperLoop(api, volumes, cache(key), identity, options, Category<HelperLoop>(key), masters, clock, key);
        }

        ILogger Category<T>(string? key) => key is null ? loggers.CreateLogger<T>() : loggers.CreateLogger($"{typeof(T).FullName}.{key}");
    }
}
