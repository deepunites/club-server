namespace Club.Saves;

/// <summary>
/// ID игрока от шелла (ID аккаунта в кассе, обычно GUID) — непрозрачная строка. Сервер и помощник принимают только
/// <c>[A-Za-z0-9-]{1,64}</c> и приводят к нижнему регистру: <c>ABC…</c> и <c>abc…</c> — одна папка и одна аренда
/// (spec 2026-11-05, п. 3.1).
/// </summary>
public static class PlayerIds
{
    public const int MaxLength = 64;

    /// <summary>ID в нижнем регистре или <c>null</c>, если строка не подходит (пусто, длиннее 64, другие символы).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return null;
            }
        }

        return value.ToLowerInvariant();
    }
}
