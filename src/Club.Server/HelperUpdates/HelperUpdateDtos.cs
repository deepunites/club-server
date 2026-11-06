namespace Club.Server.HelperUpdates;

/// <summary>
/// Поручение помощнику в ответе на отчёт G: (spec 2026-11-06, п. 3.4): поставить сборку <c>Version</c> с этим SHA256 и
/// размером. <c>Path</c> — относительный путь скачивания, <c>pxe/v1/files/tools/ClubDisklessHelper-X.Y.Z.exe</c>.
/// </summary>
public sealed record HelperUpdateOffer(Guid Id, string Version, string Sha256, long Size, string Path);

/// <summary>
/// Что помощник 1.8.0+ сообщает о поручении в отчёте G:. Все поля — строки: незнакомое значение пишется в журнал и
/// пропускается, отчёт из-за них никогда не получает 400 (иначе ПК «пропадает» из панели).
/// </summary>
public sealed record HelperUpdateReport(string? Id, string? State, string? Reason, string? Error, string? Message);

/// <summary>Кому отказано при запуске и почему (<see cref="HelperUpdateCodes.Refusals"/>).</summary>
public sealed record LaunchRefusal(int Number, string Reason);

/// <summary>
/// Итог запуска: номера ПК, получивших поручение, — в очереди или, если очередь на паузе (<c>Paused</c>), сразу на паузе;
/// остальные — в <c>Refused</c> с причиной.
/// </summary>
public sealed record LaunchResult(IReadOnlyList<int> Queued, bool Paused, IReadOnlyList<LaunchRefusal> Refused);

/// <summary>Поручение в панели: незавершённое или завершённое за последние сутки.</summary>
public sealed record HelperUpdateRow(
    Guid Id,
    Guid MachineId,
    int Number,
    string File,
    string Version,
    string Sha256,
    string State,
    string? Progress,
    string? Reason,
    string? Error,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Значения столбцов <c>helper_updates</c> и полей отчёта помощника (spec п. 3.2–3.4).</summary>
public static class HelperUpdateCodes
{
    /// <summary><c>helper_updates.state</c>.</summary>
    public static class States
    {
        public const string Queued = "queued";
        public const string Paused = "paused";
        public const string Active = "active";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// <c>helper_updates.progress</c> — последнее, что сообщил помощник; в отчёте это же поле <c>state</c>, кроме
    /// <see cref="Confirmed"/> и <see cref="Failed"/>, которые завершают поручение.
    /// </summary>
    public static class Progress
    {
        public const string Downloading = "downloading";
        public const string Waiting = "waiting";
        public const string Applying = "applying";
        public const string Confirming = "confirming";
        public const string Confirmed = "confirmed";
        public const string Failed = "failed";

        /// <summary>Значения, которые пишутся в <c>progress</c>.</summary>
        public static readonly IReadOnlySet<string> Stored = new HashSet<string>(StringComparer.Ordinal) { Downloading, Waiting, Applying, Confirming };
    }

    /// <summary><c>helper_updates.reason</c> — почему помощник ждёт (при <see cref="Progress.Waiting"/>).</summary>
    public static class Reasons
    {
        public const string Session = "session";
        public const string Command = "command";
        public const string Master = "master";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Session, Command, Master };
    }

    /// <summary><c>helper_updates.error</c>: первые шесть сообщает помощник, остальные ставит сервер.</summary>
    public static class Errors
    {
        public const string DownloadFailed = "downloadFailed";
        public const string HashMismatch = "hashMismatch";
        public const string NoRecovery = "noRecovery";
        public const string SwapFailed = "swapFailed";
        public const string StartFailed = "startFailed";
        public const string NoRollback = "noRollback";
        public const string NoReturn = "noReturn";
        public const string NoProgress = "noProgress";
        public const string BuildChanged = "buildChanged";
        public const string BuildMissing = "buildMissing";
        public const string HelperTooOld = "helperTooOld";
        public const string Diskless = "diskless";

        /// <summary>Ошибки, которые может прислать помощник; другое значение в отчёте — журнал и пропуск.</summary>
        public static readonly IReadOnlySet<string> FromHelper = new HashSet<string>(StringComparer.Ordinal)
        {
            DownloadFailed, HashMismatch, NoRecovery, SwapFailed, StartFailed, NoRollback,
        };
    }

    /// <summary>Причины отказа при запуске — в порядке проверки (spec п. 3.3).</summary>
    public static class Refusals
    {
        public const string Unknown = "unknown";
        public const string NotApproved = "notApproved";
        public const string Diskless = "diskless";
        public const string HelperTooOld = "helperTooOld";
        public const string TargetTooOld = "targetTooOld";
        public const string Busy = "busy";
        public const string Current = "current";
    }
}
