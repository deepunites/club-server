using System.Text.RegularExpressions;

namespace Club.Server.Library;

/// <summary>
/// Библиотеки игр со своей схемой БД (копия таблиц библиотеки, M0011_SoloLibrary). Ключ без схемы не запустится: её
/// таблиц нет, и запросы ушли бы в таблицы G: (search_path). Новая библиотека — новая миграция с копией DDL и ключ здесь.
/// </summary>
public static class LibrarySchemas
{
    public static readonly IReadOnlyList<string> Known = ["solo"];
}

/// <summary>
/// Имена объектов библиотеки в TrueNAS, БД и предупреждениях. G: (<see cref="Games"/>) — сегодняшние литералы, их видят
/// помощники 1.5.1 и скрипты эталона; остальные выводятся из ключа (<see cref="For"/>), чтобы ни одно имя не попало под
/// метки чужой библиотеки: помощник отличает свои таргеты по <c>:games-</c>, <c>:games-seat-</c> и концу
/// <c>:club-master</c> (у второй — <c>:&lt;ключ&gt;-</c>, <c>:&lt;ключ&gt;-seat-</c>, <c>:club-master-&lt;ключ&gt;</c>), скрипты —
/// системные диски бездиска по <c>:(diskless-master|seat-NN)$</c>. Без сеттеров: из конфигурации имена не меняются.
/// </summary>
public sealed partial class LibraryNames
{
    /// <summary>Ключи, которые совпали бы с метками и именами G:, бездиска или схемой public.</summary>
    public static readonly IReadOnlyList<string> ReservedKeys = ["games", "club", "seat", "diskless", "master", "lib", "public"];

    private LibraryNames(string key, bool isMain, string defaultTitle, string seatTargetPrefix, string seatAlias, string seatExtentComment, string seatRole,
        string versionTargetPrefix, string versionTargetAlias, string versionExtentPrefix, string masterTargetName, string masterAlias, string masterExtentName,
        string masterExtentComment, string masterChapUser, string masterGroupComment, string warningSource, string warningSubjectPrefix, string machineStateTable,
        string? libraryLabel)
    {
        Key = key;
        IsMain = isMain;
        DefaultTitle = defaultTitle;
        SeatTargetPrefix = seatTargetPrefix;
        SeatAlias = seatAlias;
        SeatExtentComment = seatExtentComment;
        SeatRole = seatRole;
        VersionTargetPrefix = versionTargetPrefix;
        VersionTargetAlias = versionTargetAlias;
        VersionExtentPrefix = versionExtentPrefix;
        MasterTargetName = masterTargetName;
        MasterAlias = masterAlias;
        MasterExtentName = masterExtentName;
        MasterExtentComment = masterExtentComment;
        MasterChapUser = masterChapUser;
        MasterGroupComment = masterGroupComment;
        WarningSource = warningSource;
        WarningSubjectPrefix = warningSubjectPrefix;
        MachineStateTable = machineStateTable;
        LibraryLabel = libraryLabel;
    }

    /// <summary>Библиотека G: — имена до появления второй библиотеки, без изменений.</summary>
    public static LibraryNames Games { get; } = new(
        "games", isMain: true, "Online Games", "games-seat-", "games seat", "clubsrv personal games seat", "seat-games",
        "games-", "games", "lib-", "club-master", "club master", "clubsrv-master",
        "clubsrv master (superclient)", "clubsrv-master", "clubsrv master", "library", "", "machines",
        libraryLabel: null);

    public string Key { get; }

    /// <summary>G: — состояние ПК в <c>machines</c>, предупреждения источника <c>library</c>, без метки <c>clubsrv:library</c>.</summary>
    public bool IsMain { get; }

    /// <summary>Название в панели, если <c>Title</c> не задан.</summary>
    public string DefaultTitle { get; }

    /// <summary>Личный диск места: таргет, экстент и CHAP-пользователь <c>&lt;префикс&gt;NN</c>, группа <c>clubsrv &lt;таргет&gt;</c>.</summary>
    public string SeatTargetPrefix { get; }

    /// <summary>Alias таргета места (+ « N»): TrueNAS требует уникальный alias.</summary>
    public string SeatAlias { get; }

    public string SeatExtentComment { get; }

    /// <summary>Метка <c>clubsrv:role</c> клона места.</summary>
    public string SeatRole { get; }

    /// <summary>Таргет версии <c>&lt;префикс&gt;&lt;метка&gt;</c> (в личном режиме не создаётся, но хранится в записи версии).</summary>
    public string VersionTargetPrefix { get; }

    public string VersionTargetAlias { get; }

    public string VersionExtentPrefix { get; }

    public string MasterTargetName { get; }

    public string MasterAlias { get; }

    public string MasterExtentName { get; }

    public string MasterExtentComment { get; }

    public string MasterChapUser { get; }

    /// <summary>Комментарий группы инициаторов мастер-тома — по нему группа и находится.</summary>
    public string MasterGroupComment { get; }

    /// <summary>Источник предупреждений сверки (<c>storage_warnings.source</c>).</summary>
    public string WarningSource { get; }

    /// <summary>Приставка к subject предупреждений: ключ (kind, subject) общий для всех библиотек.</summary>
    public string WarningSubjectPrefix { get; }

    /// <summary>Таблица состояния ПК по этой библиотеке (мастер-том, том): у G: — <c>machines</c>, у остальных — своя.</summary>
    public string MachineStateTable { get; }

    /// <summary>Значение метки <c>clubsrv:library</c> на снапшотах и клонах; у G: метки нет.</summary>
    public string? LibraryLabel { get; }

    public string SeatTarget(int number) => $"{SeatTargetPrefix}{number:D2}";

    [GeneratedRegex("^[a-z][a-z0-9]{1,11}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@":(diskless-master|seat-\d+)$")]
    private static partial Regex DisklessTargetPattern();

    /// <summary>Имена библиотеки с ключом <paramref name="key"/> (не G:): <c>solo</c> → <c>solo-seat-NN</c>, <c>club-master-solo</c>, …</summary>
    public static LibraryNames For(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new InvalidOperationException($"Library key '{key}' must match ^[a-z][a-z0-9]{{1,11}}$");
        }

        if (ReservedKeys.Contains(key))
        {
            throw new InvalidOperationException($"Library key '{key}' is reserved: its names would collide with the markers of G:, diskless seats or the public schema");
        }

        return new LibraryNames(
            key, isMain: false, key == "solo" ? "Offline Games" : key, $"{key}-seat-", $"{key} seat", $"clubsrv personal {key} seat", $"seat-{key}",
            $"{key}-", key, $"{key}-lib-", $"club-master-{key}", $"club master {key}", $"clubsrv-master-{key}",
            $"clubsrv master {key} (superclient)", $"clubsrv-master-{key}", $"clubsrv master {key}", key, $"{key}:", "machine_library",
            libraryLabel: key);
    }

    /// <summary>
    /// Нарушения правил меток (пусто — имена безопасны): таргеты этой библиотеки под метками помощника G: 1.5.1 и
    /// скриптов, мастер-том под меткой своих томов, таргеты G: под метками этой библиотеки.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        if (IsMain)
        {
            return [];
        }

        var problems = new List<string>();
        var seat = $":{SeatTarget(7)}";
        var version = $":{VersionTargetPrefix}2026-10-28";
        var master = $":{MasterTargetName}";
        foreach (var target in new[] { seat, version, master })
        {
            if (target.Contains(':' + Games.VersionTargetPrefix, StringComparison.Ordinal) || target.EndsWith(':' + Games.MasterTargetName, StringComparison.Ordinal))
            {
                problems.Add($"target {target[1..]} would be taken for a G: target by helpers");
            }

            if (DisklessTargetPattern().IsMatch(target))
            {
                problems.Add($"target {target[1..]} would be taken for a diskless system disk by the image scripts");
            }
        }

        if (!seat.Contains($":{Key}-seat-", StringComparison.Ordinal) || !version.Contains($":{Key}-", StringComparison.Ordinal))
        {
            problems.Add("seat and version targets must carry the library marker");
        }

        if (master.Contains($":{Key}-", StringComparison.Ordinal) || !master.EndsWith($":club-master-{Key}", StringComparison.Ordinal))
        {
            problems.Add($"master target {MasterTargetName} must end with :club-master-{Key} and must not carry the volume marker :{Key}-");
        }

        foreach (var gTarget in new[] { $":{Games.SeatTarget(7)}", $":{Games.VersionTargetPrefix}2026-10-28", $":{Games.MasterTargetName}", ":seat-07", ":diskless-master" })
        {
            if (gTarget.Contains($":{Key}-", StringComparison.Ordinal) || gTarget.EndsWith($":club-master-{Key}", StringComparison.Ordinal))
            {
                problems.Add($"target {gTarget[1..]} would be taken for a target of library {Key}");
            }
        }

        foreach (var (mine, theirs) in new[]
        {
            (SeatTargetPrefix, Games.SeatTargetPrefix), (VersionExtentPrefix, Games.VersionExtentPrefix), (MasterExtentName, Games.MasterExtentName),
            (MasterChapUser, Games.MasterChapUser), (MasterGroupComment, Games.MasterGroupComment), (SeatAlias, Games.SeatAlias), (MasterAlias, Games.MasterAlias),
            (WarningSource, Games.WarningSource),
        })
        {
            if (mine == theirs)
            {
                problems.Add($"name {mine} is the same as in G:");
            }
        }

        return problems;
    }
}
