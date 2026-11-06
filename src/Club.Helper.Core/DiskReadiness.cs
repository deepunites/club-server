namespace Club.Helper.Core;

/// <summary>
/// Диск одной библиотеки игр для шелла (SHELL_CHANGES п. 86, помощник 1.9.0) — элемент <c>disks</c> ответа <c>ping</c>:
/// ключ библиотеки (<c>games</c> — G:, остальные — как в helper.json, H: — <c>solo</c>), состояние из
/// <see cref="DiskStates"/>, буква без двоеточия (неизвестна — <c>null</c>, в ответе поля нет), версия библиотеки и
/// причина — только для журнала шелла.
/// </summary>
public sealed record LibraryDisk(string Library, string State, string? Letter = null, string? LibraryVersion = null, string? Error = null);

/// <summary>Значения <see cref="LibraryDisk.State"/> — ровно как в SHELL_CHANGES п. 86.</summary>
public static class DiskStates
{
    /// <summary>Диск на букве; сюда же — смена версии, пока старая версия ещё на букве (switchPending).</summary>
    public const string Mounted = "mounted";

    /// <summary>
    /// Подключается: первый такт после старта службы не закончен; назначения от сервера ещё нет (меньше 3 минут); идут
    /// вход iSCSI, ожидание диска или буква; сессия личного диска восстанавливает связь.
    /// </summary>
    public const string Attaching = "attaching";

    /// <summary>Сервер клуба сбрасывает или пересобирает личный диск места (X-Volume-Pending / volumePending: preparing).</summary>
    public const string Preparing = "preparing";

    /// <summary>
    /// Попытка не удалась (буква занята, диск не появился, Windows не ответила, сервер не собрал диск); помощник повторяет
    /// в следующем такте, причина — в <see cref="LibraryDisk.Error"/>.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>3 минуты нет назначения от сервера (недоступен, ошибки, ПК не одобрен), а на ПК подключить нечего.</summary>
    public const string NoServer = "noServer";

    /// <summary>Монтировать нечего: сервер ответил, что тома нет, и не сказал, что он готовится.</summary>
    public const string Unpublished = "unpublished";

    /// <summary>Библиотеки нет на сервере (404: выключена или сервер старше): цикл спит 10 минут.</summary>
    public const string Disabled = "disabled";

    /// <summary>Буква отдана мастер-тому: правка библиотеки на этом ПК.</summary>
    public const string Master = "master";

    /// <summary>
    /// Диск ещё подключается или готовится (<see cref="Attaching"/>, <see cref="Preparing"/>): помощник сделал не всё, что
    /// мог. Неизвестное значение — тоже (шелл читает его как <c>attaching</c>).
    /// </summary>
    public static bool InProgress(string state) => state is not (Mounted or Failed or NoServer or Unpublished or Disabled or Master);
}

/// <summary>
/// Готовность дисков игр для <c>ping</c> (SHELL_CHANGES п. 86): <paramref name="Ready"/> — помощник сделал всё, что мог: ни
/// один диск не подключается и не готовится сервером (что можно — подключено, остальное определённо недоступно). Позже
/// может снова стать <c>false</c> (смена версии, обрыв сессии). <paramref name="Disks"/> — по диску на цикл, в порядке
/// тактов: G:, потом библиотеки из helper.json; выключенной на ПК библиотеки в списке нет, пустой — дисков ПК не положено.
/// </summary>
public sealed record HelperReadiness(bool Ready, IReadOnlyList<LibraryDisk> Disks)
{
    public static HelperReadiness Of(IReadOnlyList<LibraryDisk> disks) => new(disks.All(disk => !DiskStates.InProgress(disk.State)), disks);
}
