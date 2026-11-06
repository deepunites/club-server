namespace Club.Helper.Core.Updates;

/// <summary>
/// Служба Windows помощника для самообновления (spec 2026-11-06, п. 4.1, 4.3). Реализация — в службе
/// (<c>WindowsServiceControl</c>: проверка — значение <c>FailureActions</c> службы в реестре, <see cref="ServiceRecovery.Matches"/>;
/// установка — <c>sc.exe failure</c>); в тестах — подделка.
/// </summary>
public interface IServiceControl
{
    /// <summary>
    /// Действия Windows при сбое службы — те, что нужны самообновлению: <c>restart/5000/restart/5000/restart/30000</c>,
    /// сброс счётчика через сутки. Без них замена не делается (<c>noRecovery</c>): после завершения процесса служба не
    /// поднялась бы.
    /// </summary>
    bool RecoveryConfigured();

    /// <summary>Поставить эти действия, если их нет или они другие (при каждом запуске службы).</summary>
    void EnsureRecovery();

    /// <summary>
    /// Завершить свой процесс так, что Windows считает это сбоем и запускает службу снова (<c>Process.Kill</c>, без
    /// <c>SERVICE_STOPPED</c>), — не <c>Environment.Exit</c> и не остановка хоста.
    /// </summary>
    void TerminateSelf();
}
