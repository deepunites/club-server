namespace Club.Helper.Core.Updates;

/// <summary>
/// Текст ненормального конца процесса — в <c>update.json</c> (spec 2026-11-06, п. 4.1, 4.4): необработанное исключение,
/// ошибка при старте хоста, хост остановился не по команде Windows. Пишется, только пока работает новая сборка на проверке
/// (фаза <c>swapped</c>, своя сборка = <c>toSha</c>, ошибки нет): после отката стражем он уходит в отчёт <c>startFailed</c>
/// как текст последнего исключения. Без замены, у прежней сборки и после ошибки замены <c>update.json</c> не трогается.
/// <paramref name="ownSha"/> — SHA256 своего exe (<see cref="HelperOptions.HelperBuild"/>; <c>null</c> — неизвестна,
/// ничего не пишется). Вызывается на пути к завершению процесса: не бросает.
/// </summary>
public sealed class CrashRecorder(UpdateStateStore store, string? ownSha)
{
    /// <summary>Записать <paramref name="message"/> (до <see cref="SelfUpdater.MessageLength"/> символов); <c>true</c> — записан.</summary>
    public bool Record(string message)
    {
        try
        {
            if (ownSha is null
                || store.Read(out _) is not { Error: null, Phase: UpdatePhases.Swapped } state
                || !string.Equals(state.ToSha, ownSha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var text = message.Length > SelfUpdater.MessageLength ? message[..SelfUpdater.MessageLength] : message;
            store.Write(state with { Message = text });
            return true;
        }
        catch (Exception)
        {
            // Процесс всё равно завершается: попытка засчитается и без текста.
            return false;
        }
    }

    /// <summary>Тип и текст исключения (у <see cref="AggregateException"/> и обёрток — самого внутреннего).</summary>
    public static string Describe(Exception exception)
    {
        var inner = exception.GetBaseException();
        return $"{inner.GetType().FullName}: {inner.Message}";
    }
}
