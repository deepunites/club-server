namespace Club.Helper.Core;

/// <summary>
/// Профиль Windows пользователя по SID (spec 2026-11-05, п. 4.1): на ПК — <c>ProfileImagePath</c> из
/// <c>HKLM\…\ProfileList\&lt;SID&gt;</c> (после неудачного удаления профиль бывает <c>player.&lt;ПК&gt;</c>, а не
/// <c>C:\Users\player</c>); в тестах — подделка на временные каталоги.
/// </summary>
public interface IProfileLocator
{
    /// <summary>Корень профиля или <c>null</c>, если профиля с таким SID на ПК нет.</summary>
    string? ProfilePath(string sid);
}

/// <summary>Корень буквы библиотеки (<c>G:\</c>, <c>H:\</c>); в тестах — временный каталог.</summary>
public interface IDriveRoots
{
    string RootOf(char letter);
}

/// <summary>
/// Итог команды сохранений для шелла: <c>{ok:true}</c> или код ошибки из <see cref="SavesErrors"/> и текст для журнала
/// шелла (spec п. 4.1).
/// </summary>
public sealed record SavesResult(bool Ok, string? Error = null, string? Message = null)
{
    public static readonly SavesResult Success = new(true);

    public static SavesResult Fail(string error, string message) => new(false, error, message);
}

/// <summary>Коды ошибок канала шелла (spec п. 4.1).</summary>
public static class SavesErrors
{
    /// <summary>Сохранения выключены на сервере или диск сохранений не смонтирован.</summary>
    public const string Disabled = "disabled";

    public const string ServerUnavailable = "serverUnavailable";

    /// <summary>Сохранения игрока больше лимита: не отправлены, на сервере осталась прошлая версия.</summary>
    public const string OverLimit = "overLimit";

    /// <summary>Сохранения этого игрока на этот ПК не выданы (выдачи не было или она не удалась): ничего не отправлено.</summary>
    public const string NotAttached = "notAttached";

    /// <summary>Библиотека на ПК в режиме мастера: ничего не выдаётся и не сохраняется.</summary>
    public const string MasterMode = "masterMode";

    public const string ProfileNotFound = "profileNotFound";

    public const string BadRequest = "badRequest";

    /// <summary>Помощник меняет себя на новую сборку (1.8.0, самообновление): повторить через 5 с.</summary>
    public const string Updating = "updating";

    public const string Internal = "internal";
}

/// <summary>
/// Библиотека на ПК для сохранений: буква, версия подключённого тома (<c>null</c> — неизвестна) и подключён ли он сейчас.
/// По версии снимается исходное содержимое папок сохранений на этой букве (spec п. 4.4).
/// </summary>
public sealed record LibraryMount(char Letter, string? Version, bool Mounted);
