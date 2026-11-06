using System.Buffers.Binary;

namespace Club.Helper.Core.Updates;

/// <summary>
/// Действия Windows при сбое службы помощника, нужные самообновлению (spec 2026-11-06, п. 4.1): после завершения процесса
/// (<see cref="IServiceControl.TerminateSelf"/>) Windows запускает службу снова — <c>restart/5000/restart/5000/restart/30000</c>,
/// счётчик сбоев сбрасывается через сутки (<see cref="ResetSeconds"/>). Ставит их <c>sc.exe failure</c>
/// (<see cref="FailureArguments"/>); проверяются по значению <c>FailureActions</c> раздела службы в реестре
/// (<see cref="Matches"/>), а не по выводу <c>sc.exe qfailure</c>: тот переведён на язык Windows.
/// </summary>
public static class ServiceRecovery
{
    /// <summary>Через сколько секунд без сбоев Windows сбрасывает счётчик сбоев службы.</summary>
    public const int ResetSeconds = 86400;

    /// <summary>Задержки перезапуска после первого, второго и следующих сбоев, мс.</summary>
    public static readonly IReadOnlyList<int> RestartDelaysMs = [5000, 5000, 30000];

    /// <summary>SC_ACTION_RESTART.</summary>
    private const uint RestartAction = 1;

    /// <summary>
    /// Неизменная часть SERVICE_FAILURE_ACTIONS в реестре: сброс, сообщение перезагрузки и команда (смещения), число
    /// действий, смещение массива действий — по 4 байта.
    /// </summary>
    private const int FixedPart = 20;

    private const int ActionSize = 8;

    /// <summary>Аргументы <c>sc.exe</c> (по одному в <c>ProcessStartInfo.ArgumentList</c>), которые ставят эти действия.</summary>
    public static IReadOnlyList<string> FailureArguments(string serviceName) =>
        ["failure", serviceName, "reset=", ResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "actions=",
            string.Join("/", RestartDelaysMs.Select(delay => $"restart/{delay}"))];

    /// <summary>
    /// Значение <c>FailureActions</c> (REG_BINARY) службы — ровно эти действия: сброс <see cref="ResetSeconds"/> и три
    /// перезапуска с задержками <see cref="RestartDelaysMs"/>. Нет значения, оно короче или другое — <c>false</c>.
    /// </summary>
    public static bool Matches(byte[]? failureActions)
    {
        if (failureActions is not { Length: >= FixedPart } value)
        {
            return false;
        }

        var reset = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(12));
        if (reset != ResetSeconds || count != RestartDelaysMs.Count)
        {
            return false;
        }

        // Массив действий — по смещению из поля указателя; Windows пишет его сразу за неизменной частью.
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(16));
        if (offset < FixedPart || offset > value.Length)
        {
            offset = FixedPart;
        }

        if (value.Length - offset < count * ActionSize)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var action = value.AsSpan((int)offset + i * ActionSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(action) != RestartAction
                || BinaryPrimitives.ReadUInt32LittleEndian(action[4..]) != RestartDelaysMs[i])
            {
                return false;
            }
        }

        return true;
    }
}
