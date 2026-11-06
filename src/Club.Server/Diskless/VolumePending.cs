using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Diskless;

/// <summary>
/// Почему в ответе помощнику нет личного диска места (SHELL_CHANGES п. 86): «сервер ещё готовит диск» — не то же, что
/// «монтировать нечего», и помощник не должен считать G:/H: недоступными, пока диск сбрасывается или пересобирается.
/// 204 на <c>GET …/volume</c> и <c>GET …/libraries/{key}/volume</c> получает заголовок <see cref="Header"/>, ответ на
/// отчёт без тома — поле <c>volumePending</c> (<see cref="StatusAccepted.VolumePending"/>); значения —
/// <see cref="Preparing"/> и <see cref="Failed"/>. Монтировать нечего (библиотека не опубликована, режим выключен, помощник
/// без личного слоя) — ни заголовка, ни поля, как раньше: старые помощники не видят разницы.
/// <see cref="SeatGames"/> о ходе подготовки не сообщает, поэтому признаки берутся снаружи: запрос ждал подготовки весь
/// <see cref="SeatGames.AnswerWithin"/> (значит, она ещё идёт), запись места — <c>failed</c>, или IQN ПК ещё неизвестен
/// (при старте: диск соберётся по отчёту с IQN). Сбой сборки до записи места (нет датасета PersonalParent, имя занято
/// чужим ПК) виден только <see cref="SeatGames"/> — такой ответ остаётся без признака.
/// </summary>
public static class VolumePending
{
    /// <summary>Заголовок 204 запроса при старте помощника: тома нет, но он будет (или сборка упала).</summary>
    public const string Header = "X-Volume-Pending";

    /// <summary>Сервер сбрасывает или пересобирает личный диск места: назначение придёт в ответе на отчёт.</summary>
    public const string Preparing = "preparing";

    /// <summary>Последняя сборка личного диска места упала; отчёты повторят её не раньше <see cref="SeatGames.RetryAfter"/>.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// Запас к <see cref="SeatGames.AnswerWithin"/>: таймер ожидания может сработать на миллисекунды раньше, чем насчитает
    /// <see cref="TimeProvider.GetElapsedTime(long)"/>. Сборка, упавшая на последней секунде ожидания, один раз сойдёт за
    /// «готовится» — следующий отчёт скажет <see cref="Failed"/>.
    /// </summary>
    private static readonly TimeSpan HoldSlack = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <see cref="SeatGames.AssignmentAsync"/> и, если тома нет, — почему: <see cref="Preparing"/>, <see cref="Failed"/> или
    /// null (монтировать нечего). <paramref name="repository"/> и <paramref name="library"/> — той же библиотеки, что
    /// <paramref name="seats"/>.
    /// </summary>
    public static async Task<(VolumeAssignment? Volume, string? Pending)> SeatAssignmentAsync(
        SeatGames seats, SeatGamesRepository repository, LibraryRepository library, LibraryOptions options, TimeProvider clock,
        MachineRow machine, bool atBoot, CancellationToken ct, bool? attached = null)
    {
        var started = clock.GetTimestamp();
        var volume = await seats.AssignmentAsync(machine, atBoot, ct, attached);
        if (volume is not null)
        {
            return (volume, null);
        }

        var waited = clock.GetElapsedTime(started);
        if (!options.Enabled || !options.PersonalGames || !seats.Supports(machine.HelperVersion) || (await library.PointersAsync()).Current is null)
        {
            return (null, null);
        }

        if (waited >= SeatGames.AnswerWithin - HoldSlack)
        {
            return (null, Preparing);
        }

        if (await repository.FindAsync(machine.Id) is { State: "failed" })
        {
            return (null, Failed);
        }

        // IQN гибрида сервер узнаёт из отчёта (у бездискового есть имя места из iBFT): по первому же отчёту с IQN диск
        // соберётся. В самом отчёте IQN уже записан — нет его там, значит, и не придёт.
        return atBoot && string.IsNullOrWhiteSpace(machine.InitiatorIqn) && machine.BootMode != "diskless" ? (null, Preparing) : (null, null);
    }
}
