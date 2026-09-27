using System.Collections.Concurrent;

namespace Club.Server.Auth;

/// <summary>
/// Защита от повторов по тройке <c>(pcId, X-Timestamp, X-Signature)</c>.
/// Тройка запоминается только за запросом, который сервер принял; если сервер ответил 5xx, 408 или 429,
/// повтор с той же подписью разрешён — текущий агент повторяет запросы, не подписывая их заново
/// (club-contracts/docs/SHELL_CHANGES.md п. 2). Одновременный второй запрос с той же тройкой отклоняется.
/// Хранилище в памяти: после перезапуска сервера окно повторов открыто не дольше окна подписи (±300 с).
/// </summary>
public sealed class ReplayGuard(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private long _lastSweep;

    public static string Key(Guid pcId, string timestamp, string signature) => $"{pcId:N}|{timestamp}|{signature.ToLowerInvariant()}";

    /// <summary>Резервирует тройку на время обработки. <c>false</c> — это повтор уже принятого или выполняемого запроса.</summary>
    public bool TryBegin(string key, TimeSpan window)
    {
        Sweep();
        var entry = new Entry(InFlight: true, clock.GetUtcNow() + window + window);
        return _entries.TryAdd(key, entry);
    }

    /// <summary>Завершает обработку: запомнить тройку или отпустить её для повтора.</summary>
    public void Complete(string key, int statusCode)
    {
        if (IsRetryableFailure(statusCode))
        {
            _entries.TryRemove(key, out _);
            return;
        }

        if (_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry with { InFlight = false };
        }
    }

    public static bool IsRetryableFailure(int statusCode) => statusCode is >= 500 or 408 or 429;

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweep);
        if (now.ToUnixTimeSeconds() - last < 60 || Interlocked.CompareExchange(ref _lastSweep, now.ToUnixTimeSeconds(), last) != last)
        {
            return;
        }

        foreach (var (key, entry) in _entries)
        {
            if (!entry.InFlight && entry.ExpiresAt < now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(bool InFlight, DateTimeOffset ExpiresAt);
}
