namespace Club.Helper.Core.Updates;

/// <summary>
/// Состояние замены — <c>update.json</c> (spec 2026-11-06, п. 4.3–4.4), версия файла <paramref name="V"/> =
/// <see cref="CurrentVersion"/>. Пишет помощник перед переименованиями (<see cref="UpdatePhases.Prepared"/>) и после них
/// (<see cref="UpdatePhases.Swapped"/>), читает страж в начале <c>Main</c> (<see cref="UpdateGuard"/>).
/// <paramref name="Id"/> — поручение сервера (<see cref="Guid.Empty"/> — неизвестно: состояние восстановлено стражем по
/// <c>.old</c>, потому что <c>update.json</c> не читался); <paramref name="FromSha"/>/<paramref name="ToSha"/> — SHA256
/// прежней и новой сборки строчными; <paramref name="Attempts"/> — сколько раз запускалась новая сборка без «чистого
/// выхода» прошлого запуска (первый запуск — всегда попытка 1); <paramref name="CleanExit"/> — прошлый запуск остановлен
/// командой Windows (пишет наследник <c>WindowsServiceLifetime</c>), страж сбрасывает его при каждом запуске;
/// <paramref name="StartedAt"/> — начало замены, затем — время последнего запуска новой сборки; <paramref name="Error"/> —
/// код ошибки для отчёта (<see cref="UpdateErrors"/>), <paramref name="Message"/> — текст: при ненормальном конце хоста —
/// текст исключения, после отката — он же в отчёте <c>startFailed</c>.
/// </summary>
public sealed record UpdateState(
    int V,
    Guid Id,
    string FromSha,
    string ToSha,
    string ToVersion,
    string Phase,
    int Attempts,
    bool CleanExit,
    DateTimeOffset StartedAt,
    string? Error,
    string? Message)
{
    /// <summary>Версия формата <c>update.json</c>; другая — файл не читается (как испорченный).</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Фазы замены в <c>update.json</c>.</summary>
public static class UpdatePhases
{
    /// <summary>Записано перед переименованиями: <c>.new</c> проверен и открыт, имена ещё прежние.</summary>
    public const string Prepared = "prepared";

    /// <summary>Переименования прошли: exe — новая сборка, <c>.old</c> — прежняя.</summary>
    public const string Swapped = "swapped";

    /// <summary>Страж вернул прежнюю сборку: exe — прежняя, <c>.failed</c> — новая.</summary>
    public const string RolledBack = "rolledBack";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Prepared, Swapped, RolledBack };
}

/// <summary>
/// Ошибки, которые помощник сообщает в <c>helperUpdate.error</c> отчёта G: (как <c>HelperUpdateCodes.Errors</c> сервера,
/// spec п. 3.4).
/// </summary>
public static class UpdateErrors
{
    public const string DownloadFailed = "downloadFailed";
    public const string HashMismatch = "hashMismatch";
    public const string NoRecovery = "noRecovery";
    public const string SwapFailed = "swapFailed";
    public const string StartFailed = "startFailed";
    public const string NoRollback = "noRollback";
}

/// <summary>
/// Поручение, по которому на этом ПК была ошибка (<c>update-failed.json</c>): снова не выполняется, в каждом отчёте по нему
/// — <c>failed</c> с <paramref name="Error"/>. <paramref name="Sha256"/> — сборка поручения.
/// </summary>
public sealed record FailedUpdate(Guid Id, string Sha256, string Error);
