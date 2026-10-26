namespace Club.Helper.Core;

/// <summary>
/// По каким частям имени таргета помощник узнаёт тома своей библиотеки (сервер, LibraryNames). G: (<see cref="Games"/>) —
/// метки до второй библиотеки: версии <c>games-&lt;версия&gt;</c>, личный диск <c>games-seat-NN</c>, мастер-том
/// <c>club-master</c> (конец имени). Остальные выводятся из ключа (<see cref="For"/>): у <c>solo</c> — <c>solo-</c>,
/// <c>solo-seat-</c> и <c>club-master-solo</c>. Метки не пересекаются: <c>club-master-solo</c> не кончается на
/// <c>:club-master</c> и не содержит <c>:solo-</c>, поэтому ни цикл G:, ни цикл H: чужие таргеты не трогают и не отключают.
/// <paramref name="Key"/> — ключ библиотеки в путях API (<c>machines/{id}/libraries/{key}/…</c>); у G: <c>null</c>: её пути
/// прежние.
/// </summary>
public sealed record LibraryMarkers(string? Key, string Managed, string Personal, string Master)
{
    /// <summary>Ключи, имена которых совпали бы с метками G:, системных дисков бездиска или схемой public на сервере.</summary>
    public static readonly IReadOnlyList<string> ReservedKeys = ["games", "club", "seat", "diskless", "master", "lib", "public"];

    /// <summary>Библиотека G: — метки помощника 1.5.x без изменений.</summary>
    public static LibraryMarkers Games { get; } = new(null, VolumeManager.ManagedTargetMarker, VolumeManager.PersonalTargetMarker, MasterManager.TargetMarker);

    /// <summary>Метки библиотеки с ключом <paramref name="key"/>; ключ не годится (см. <see cref="IsValidKey"/>) — исключение.</summary>
    public static LibraryMarkers For(string key) =>
        IsValidKey(key)
            ? new LibraryMarkers(key, $":{key}-", $":{key}-seat-", $":club-master-{key}")
            : throw new ArgumentException($"Library key '{key}' must match ^[a-z][a-z0-9]{{1,11}}$ and must not be one of {string.Join(", ", ReservedKeys)}", nameof(key));

    /// <summary>Ключ как на сервере (<c>^[a-z][a-z0-9]{1,11}$</c>, не из <see cref="ReservedKeys"/>).</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: >= 2 and <= 12 } && key[0] is >= 'a' and <= 'z' && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
        && !ReservedKeys.Contains(key);

    /// <summary>Таргет — том этой библиотеки (версия или личный диск места), не мастер-том и не чужой.</summary>
    public bool IsManaged(string iqn) => iqn.Contains(Managed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Таргет — личный диск места этой библиотеки.</summary>
    public bool IsPersonal(string iqn) => iqn.Contains(Personal, StringComparison.OrdinalIgnoreCase);

    /// <summary>Таргет — мастер-том этой библиотеки (по концу имени, как у помощника 1.4).</summary>
    public bool IsMaster(string iqn) => iqn.EndsWith(Master, StringComparison.Ordinal);
}
