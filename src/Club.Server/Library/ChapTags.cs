using Club.TrueNas;

namespace Club.Server.Library;

/// <summary>
/// CHAP в TrueNAS — группы по tag: таргет с CHAP пускает любого пользователя своей группы. Поэтому у каждого диска
/// (место, личный слой игр, мастер-том) свой tag, а выбор «следующего свободного» и создание идут под одной блокировкой
/// процесса: иначе два места, создаваемые одновременно, получили бы один tag и пускали бы друг друга.
/// </summary>
public static class ChapTags
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>CHAP пользователя в группе <paramref name="tag"/> (или в новой свободной группе); возвращает tag.</summary>
    public static async Task<int> EnsureAsync(TrueNasStorage storage, int? tag, string user, string secret, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var chosen = tag ?? await FreeAsync(storage, ct);
            await storage.EnsureChapAsync(chosen, user, secret, ct);
            return chosen;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<int> FreeAsync(TrueNasStorage storage, CancellationToken ct)
    {
        var tags = (await storage.ListAuthAsync(ct)).Select(a => a.Tag).ToList();
        return tags.Count == 0 ? 1 : tags.Max() + 1;
    }
}
