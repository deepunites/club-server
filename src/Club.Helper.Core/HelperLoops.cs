using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>
/// Помощник целиком: цикл G: и по циклу на каждую библиотеку из <see cref="HelperOptions.Libraries"/> (1.6.0: H: — solo).
/// Такт — по очереди, не параллельно: сначала G: (его отчёт сообщает серверу версию помощника, а по ней сервер решает,
/// давать ли ПК диск H:), потом остальные, каждый в своём try/catch — сбой одного цикла не отменяет другие. Windows не
/// ответила в такте G: (таймаут PowerShell, 120 с) — остальные в этом такте пропускаются: их опросы Windows ждали бы ещё
/// столько же, а с ними и отчёт G: следующего такта. Клиент API у всех циклов один (<see cref="DisklessApiClient"/>:
/// одноразовый refresh-токен, обновляет его тот, чей запрос первым получил 401).
/// </summary>
public sealed class HelperLoops(HelperLoop games, IReadOnlyList<HelperLoop> libraries, ILogger logger)
{
    public HelperLoop Games => games;

    public IReadOnlyList<HelperLoop> Libraries => libraries;

    public async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await games.TickAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Любой сбой такта — в журнал; том остаётся как есть, следующий такт попробует снова.
            logger.LogError(ex, "Helper tick failed");
        }

        foreach (var library in libraries)
        {
            if (games.WindowsTimedOut)
            {
                logger.LogDebug("Libraries not ticked this time: Windows did not answer while applying the G: volume");
                return;
            }

            try
            {
                await library.TickAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Helper tick of library {Library} failed", library.Library);
            }
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

        return new HelperLoops(Loop(LibraryMarkers.Games), libraries, logger);

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
