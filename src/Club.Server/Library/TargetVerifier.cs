using Club.TrueNas;

namespace Club.Server.Library;

/// <summary>Итог проверки «таргет виден инициатору».</summary>
public enum TargetCheck
{
    /// <summary>Портал показывает таргет — проверено.</summary>
    Visible,

    /// <summary>
    /// Портал отвечает, но таргета не показывает и после повторных reload-ов. Портал, который ответил хоть раз и потом
    /// замолчал, — тоже сюда: таргет он уже показал скрытым.
    /// </summary>
    Hidden,

    /// <summary>Проверить нельзя: портал ни разу не ответил или не пускает в discovery. Reload-ы сделаны вслепую.</summary>
    Unverified,

    /// <summary>
    /// Группа инициаторов таргета не включает того, от чьего имени идёт проверка: iscsi-scstd ему таргет не покажет,
    /// сколько ни перезагружай. Reload-ов не было — это ошибка настройки, а не TrueNAS.
    /// </summary>
    ProbeDenied,
}

/// <summary>Кого пускают группы инициаторов таргета (TrueNAS сводит их строки в одну security_group SCST).</summary>
public enum InitiatorAccess
{
    /// <summary>Сказать нельзя: таргета нет, у него нет групп или группа инициаторов не читается.</summary>
    Unknown,

    /// <summary>
    /// Таргет открыт всем: у какой-то из его групп список инициаторов не задан или пуст (<c>INITIATOR *</c>), — или имя
    /// пускает строка, которая открывает таргет почти всем: <c>*</c> или строка с ведущим <c>!</c> («все, кроме …»).
    /// Список тогда ни при чём, и подсказка «проверьте в нём имя проверки» только сбила бы с толку.
    /// </summary>
    Open,

    /// <summary>
    /// Таргет открыт только по списку, и имя пускает конкретная строка — не <c>*</c> и без ведущего <c>!</c>, в том числе
    /// шаблон вроде <c>iqn.2026-10.local.clubsrv:*</c> (<see cref="ScstWildcard"/>).
    /// </summary>
    Listed,

    /// <summary>Таргет открыт только по списку, и имя ни под одну строку не подходит: iscsi-scstd таргет ему не покажет.</summary>
    Excluded,
}

/// <summary>
/// Проверка «таргет действительно отдан SCST». Успех <c>iscsi.*.create</c> её не заменяет: middleware не смотрит на код
/// возврата scstadmin (docs/research/truenas-api.md §8.4). На TrueNAS 25.10.7 <c>scstadmin -force</c> срывается, пока в
/// copy_manager_tgt есть read-only устройство (каждый срыв снимает одно такое), — стенд 2026-10-02: таргет новой версии
/// остался <c>enabled 0</c> без LUN, ПК — «target not found or hidden from login». Проверка — SendTargets с сервера
/// клуба (iscsi-scstd отдаёт только включённый таргет, где у инициатора есть LUN); починка — ещё один reload.
/// </summary>
public sealed class TargetVerifier(TrueNasStorage storage, LibraryOptions options, TimeProvider clock, ILogger<TargetVerifier> logger)
{
    /// <summary>
    /// Имя для проверки таргетов версий (<c>Library:ProbeInitiatorIqn</c>). iscsi-scstd применяет список инициаторов и
    /// к SendTargets, поэтому в суженной группе версий это имя должно быть (<see cref="InitiatorAccessAsync"/>).
    /// </summary>
    public string ProbeInitiator => options.ProbeInitiatorIqn;

    public string Portal => string.IsNullOrWhiteSpace(options.DiscoveryAddress) ? options.PortalAddress : options.DiscoveryAddress;

    /// <summary>
    /// IQN, которые портал показывает инициатору; null — проверить нельзя (нет связи, отказ в соединении, discovery с
    /// CHAP, неверный адрес). Отказ в соединении сознательно не считается «таргета нет»: его не отличить от неверного
    /// <c>DiscoveryAddress</c> или фильтра на пути сервер → TrueNAS, а при остановленной службе iSCSI ПК не видят ни
    /// одной версии, и её запуск применяет конфигурацию целиком (без <c>-force</c>) — провал публикации ничего бы не спас.
    /// </summary>
    public async Task<IReadOnlyList<string>?> DiscoverAsync(string initiator, CancellationToken ct)
    {
        try
        {
            return await IscsiDiscovery.SendTargetsAsync(Portal, initiator, TimeSpan.FromMilliseconds(options.DiscoveryTimeoutMs), ct);
        }
        catch (IscsiDiscoveryException ex)
        {
            logger.LogWarning("iSCSI discovery at {Portal} is not available: {Error}", Portal, ex.Message);
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Непредвиденное — тоже «проверить нельзя». Исключение наружу оставило бы операцию в running/verify без
            // попыток и без lastError, а очередь операций — заблокированной.
            logger.LogWarning(ex, "iSCSI discovery at {Portal} failed unexpectedly", Portal);
            return null;
        }
    }

    /// <summary>
    /// Пускают ли группы инициаторов таргета <paramref name="initiator"/> — так, как это решит SCST: строки всех групп
    /// таргета TrueNAS пишет в одну security_group, группа без списка или с пустым списком — <c>INITIATOR *</c>, а каждая
    /// строка сравнивается с IQN шаблоном SCST (<see cref="ScstWildcard"/>: <c>*</c>, <c>?</c>, ведущий <c>!</c>, регистр
    /// не различается). Достаточно одной подходящей строки. Пускает строка <c>*</c> или с ведущим <c>!</c> —
    /// <see cref="InitiatorAccess.Open"/>, иначе подходящая строка — <see cref="InitiatorAccess.Listed"/>. Группа — для
    /// сообщения: для <see cref="InitiatorAccess.Listed"/> та, что пускает, для <see cref="InitiatorAccess.Excluded"/> —
    /// первая из списка.
    /// </summary>
    public async Task<(InitiatorAccess Access, int? Group)> InitiatorAccessAsync(string targetName, string initiator, CancellationToken ct)
    {
        if (await storage.GetTargetAsync(targetName, ct) is not { } target)
        {
            return (InitiatorAccess.Unknown, null);
        }

        int? listed = null;
        int? admitting = null;
        var unknown = false;
        foreach (var group in await storage.TargetGroupsAsync(target.Id, ct))
        {
            if (group.Initiator is not { } groupId)
            {
                return (InitiatorAccess.Open, null);
            }

            if (await storage.GetInitiatorGroupByIdAsync(groupId, ct) is not { } initiators)
            {
                unknown = true;
                continue;
            }

            if (initiators.Initiators.Count == 0)
            {
                return (InitiatorAccess.Open, null);
            }

            listed ??= groupId;
            foreach (var pattern in initiators.Initiators.Select(p => p.Trim()).Where(p => ScstWildcard.Matches(p, initiator)))
            {
                if (pattern == "*" || pattern.StartsWith('!'))
                {
                    return (InitiatorAccess.Open, null);
                }

                admitting ??= groupId;
            }
        }

        return admitting is not null ? (InitiatorAccess.Listed, admitting)
            : unknown || listed is null ? (InitiatorAccess.Unknown, null)
            : (InitiatorAccess.Excluded, listed);
    }

    /// <summary>
    /// Таргет должен быть виден инициатору; если нет — reload и снова проверка. Reload-ов не больше, чем read-only
    /// экстентов плюс два: каждый сорвавшийся scstadmin снимает одно устройство из copy_manager_tgt, после них reload
    /// проходит. Портал, ответивший хоть раз без таргета, потом замолчал — проверка продолжается, молчание считается «всё
    /// ещё не виден» (итог — <see cref="TargetCheck.Hidden"/>, а не переключение версии без проверки). Портал не ответил ни
    /// разу — reload-ы вслепую, столько, сколько нужно в худшем случае (после перезапуска TrueNAS в
    /// copy_manager_tgt все RO-устройства): RO-экстентов плюс один, обычно 3–4 (откатная, текущая, новая версии). Меньше
    /// нельзя — без проверки не узнать, сколько срывов осталось; больше незачем. Каждый лишний успешный reload, возможно,
    /// переназначает LUN RO-устройств и у работающих версий (не проверено, docs/research/truenas-api.md §8.4).
    /// Выполняется на каждом проходе: после рестарта сервера ensure-шаги ничего не создают и reload не вызывают —
    /// применить конфигурацию заново может только этот шаг.
    /// </summary>
    public async Task<TargetCheck> EnsureVisibleAsync(string targetName, string initiator, CancellationToken ct)
    {
        var iqn = $"{await storage.GetIscsiBasenameAsync(ct)}:{targetName}";
        int? readOnlyExtents = null;
        var answered = false;
        for (var reloads = 0; ; reloads++)
        {
            if (options.VerifyDelayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.VerifyDelayMs), clock, ct);
            }

            var visible = await DiscoverAsync(initiator, ct);
            if (visible is not null && visible.Contains(iqn, StringComparer.Ordinal))
            {
                if (reloads > 0)
                {
                    logger.LogWarning("iSCSI target {Iqn} became visible after {Reloads} extra reload(s) of the iSCSI configuration", iqn, reloads);
                }

                return TargetCheck.Visible;
            }

            if (visible is not null && reloads == 0
                && await InitiatorAccessAsync(targetName, initiator, ct) is (InitiatorAccess.Excluded, { } group))
            {
                logger.LogError("iSCSI target {Iqn} cannot be checked: its initiator group {Group} does not include {Initiator}", iqn, group, initiator);
                return TargetCheck.ProbeDenied;
            }

            answered |= visible is not null;
            readOnlyExtents ??= (await storage.ListExtentsAsync(ct)).Count(e => e.ReadOnly && e.Enabled);
            if (!answered)
            {
                for (; reloads <= readOnlyExtents; reloads++)
                {
                    await storage.ReloadIscsiAsync(targetName, ct);
                }

                logger.LogWarning("iSCSI target {Iqn} cannot be verified at {Portal}; re-applied the iSCSI configuration {Reloads} time(s) without checking", iqn, Portal, reloads);
                return TargetCheck.Unverified;
            }

            if (reloads >= readOnlyExtents + 2)
            {
                logger.LogError("iSCSI target {Iqn} is still not visible at {Portal} after {Reloads} reload(s)", iqn, Portal, reloads);
                return TargetCheck.Hidden;
            }

            logger.LogWarning("iSCSI target {Iqn} is not visible at {Portal}; re-applying the iSCSI configuration", iqn, Portal);
            await storage.ReloadIscsiAsync(targetName, ct);
        }
    }
}
