namespace Club.TrueNas;

/// <summary>
/// Сравнение имени инициатора со строкой группы инициаторов так, как это делает SCST: <c>wildcmp</c> из
/// scst/src/scst_targ.c (и её копия в iscsi-scst/usr/target.c), TS-25.10.7. <c>*</c> — любая строка, в том числе
/// пустая; <c>?</c> — ровно один символ; регистр не различается (ASCII <c>tolower</c>). Первый <c>!</c> обращает
/// результат сравнения остатка шаблона с остатком имени (<c>!iqn.x:*</c> — «все, кроме iqn.x:…»), следующие <c>!</c> —
/// обычные символы. Порт сделан буквально, со всеми особенностями C-кода (например, <c>*!x</c> равносилен <c>!x</c>).
/// <para>
/// Как TrueNAS отдаёт список в SCST (scst.conf.mako): строки всех групп таргета сводятся в одну security_group как
/// <c>INITIATOR строка#адрес-портала</c> (<c>per_portal_acl 1</c>, адрес — <c>*</c> для 0.0.0.0), а сессия ищет группу
/// по <c>IQN#адрес</c>; подходит хотя бы одна строка — LUN группы видны. Здесь сравнивается только IQN: суффикс
/// <c>#адрес</c> совпадает у любого инициатора, пришедшего на этот портал, и для шаблонов из IQN, <c>*</c>, <c>?</c> и
/// <c>!</c> итог тот же. Пустая группа TrueNAS пишется как <c>INITIATOR *#…</c> — это решает вызывающий, не шаблон.
/// </para>
/// </summary>
public static class ScstWildcard
{
    public static bool Matches(string pattern, string name) => Compare(pattern, 0, name, 0, negated: false);

    private static bool Compare(string wild, int w, string text, int s, bool negated)
    {
        var mp = -1;
        var cp = -1;
        while (s < text.Length && At(wild, w) != '*')
        {
            if (At(wild, w) == '!' && !negated)
            {
                return !Compare(wild, w + 1, text, s, negated: true);
            }

            if (Lower(At(wild, w)) != Lower(text[s]) && At(wild, w) != '?')
            {
                return false;
            }

            w++;
            s++;
        }

        while (s < text.Length)
        {
            var c = At(wild, w);
            if (c == '!' && !negated)
            {
                return !Compare(wild, w + 1, text, s, negated: true);
            }

            if (c == '*')
            {
                if (++w >= wild.Length)
                {
                    return true;
                }

                mp = w;
                cp = s + 1;
            }
            else if (Lower(c) == Lower(text[s]) || c == '?')
            {
                w++;
                s++;
            }
            else
            {
                // Откат к последней звёздочке: она забирает ещё один символ имени. До второго цикла доходят только
                // через звёздочку, поэтому mp здесь уже задан.
                w = mp;
                s = cp++;
            }
        }

        while (At(wild, w) == '*')
        {
            w++;
        }

        return w >= wild.Length;
    }

    /// <summary>Символ шаблона или <c>\0</c> за его концом — как строка C.</summary>
    private static char At(string value, int index) => index < value.Length ? value[index] : '\0';

    private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
