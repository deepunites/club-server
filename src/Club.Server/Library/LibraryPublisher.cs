using System.Text.RegularExpressions;
using Club.TrueNas;

namespace Club.Server.Library;

public sealed class LibraryRequestException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Публикация версий библиотеки игр. Каждый шаг — ensure по детерминированному имени, поэтому операцию можно
/// выполнять заново после падения на любом шаге. БД хранит намерения; факт перечитывается из TrueNAS перед
/// каждым шагом (<see cref="TrueNasStorage"/>). Сверка только сообщает о расхождениях и ничего не чинит.
/// </summary>
public sealed partial class LibraryPublisher(
    LibraryRepository repository,
    MasterRepository master,
    MasterEditor masterEditor,
    TrueNasStorage storage,
    LibraryOptions options,
    TimeProvider clock,
    ILogger<LibraryPublisher> logger)
{
    public const string ManagedLabel = "clubsrv:managed";
    public const string VersionLabel = "clubsrv:libver";
    public const string RoleLabel = "clubsrv:role";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex LabelPattern();

    // ---- запросы администратора ------------------------------------------------------------------------------

    /// <summary>Ставит публикацию версии в очередь. Повторный запрос той же версии возвращает существующую операцию.</summary>
    public async Task<Guid> RequestPublishAsync(string label, string? requestedBy, bool allowDirtyMaster = false)
    {
        if (!LabelPattern().IsMatch(label))
        {
            throw new LibraryRequestException("label", "Version label must match ^[a-z0-9][a-z0-9-]{0,39}$");
        }

        // Снапшот мастер-тома, открытого на запись, — снимок посреди правки (NTFS может быть недописана).
        var masterState = await master.GetAsync();
        if (masterState.State != "closed")
        {
            throw new LibraryRequestException("masterOpen", "The master volume is open for editing; close it before publishing");
        }

        if (masterState.Dirty && !allowDirtyMaster)
        {
            throw new LibraryRequestException("masterDirty", "The master volume was force-closed and may be inconsistent; open and close it cleanly, or confirm publishing anyway");
        }

        if (await repository.FindVersionAsync(label) is { } existing)
        {
            var open = (await repository.OpenOperationsAsync()).FirstOrDefault(o => o.VersionId == existing.Id);
            return open?.Id ?? throw new LibraryRequestException("exists", $"Version {label} already exists ({existing.State})");
        }

        var version = new LibraryVersion
        {
            Id = Guid.NewGuid(),
            Label = label,
            SnapshotId = $"{options.MasterZvol}@lib-{label}",
            CloneId = $"{options.PublishedParent}/lib-{label}",
            ExtentName = $"lib-{label}",
            TargetName = $"games-{label}",
        };
        var (_, operationId) = await repository.CreatePublishAsync(version, requestedBy, clock.GetUtcNow());
        logger.LogInformation("Publish of library {Label} requested by {User}", label, requestedBy);
        return operationId;
    }

    /// <summary>Откат на предыдущую версию. Таргет откатной версии не удалялся, поэтому переключение мгновенное.</summary>
    public async Task<Guid> RequestRollbackAsync(string? requestedBy)
    {
        var pointers = await repository.PointersAsync();
        if (pointers.Rollback is null)
        {
            throw new LibraryRequestException("noRollback", "There is no rollback version");
        }

        return await repository.CreateOperationAsync("rollback", pointers.Rollback.Id, requestedBy, clock.GetUtcNow());
    }

    // ---- выполнение ------------------------------------------------------------------------------------------

    /// <summary>Выполняет все незавершённые операции по порядку. Вызывается только из одного воркера.</summary>
    public async Task ProcessOperationsAsync(CancellationToken ct)
    {
        foreach (var operation in await repository.OpenOperationsAsync())
        {
            ct.ThrowIfCancellationRequested();
            await ProcessAsync(operation, ct);
        }
    }

    private async Task ProcessAsync(StorageOperation operation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await repository.MarkOperationAsync(operation.Id, "running", null, null, countAttempt: false, now);
        try
        {
            switch (operation.Kind)
            {
                case "publish":
                    await PublishAsync(operation, ct);
                    break;
                case "rollback":
                    await RollbackAsync(operation);
                    break;
                case "masterOpen":
                    await masterEditor.OpenAsync(step => Step(operation, step), ct);
                    break;
                case "masterClose":
                    await masterEditor.CloseAsync(step => Step(operation, step), ct);
                    break;
                default:
                    throw new InvalidOperationException($"unknown operation kind {operation.Kind}");
            }

            await repository.MarkOperationAsync(operation.Id, "done", "done", null, countAttempt: false, clock.GetUtcNow());
        }
        catch (StorageWaitException ex)
        {
            // Мастер-том ещё подключён на ПК суперклиента — ждём, попытки не тратятся.
            await repository.MarkOperationAsync(operation.Id, "pending", null, ex.Message, countAttempt: false, clock.GetUtcNow());
        }
        catch (TrueNasUnavailableException ex)
        {
            // Нет связи с TrueNAS — не ошибка операции: остаётся в очереди, попытки не тратятся.
            logger.LogWarning(ex, "Storage operation {Id} postponed: TrueNAS unavailable", operation.Id);
            await repository.MarkOperationAsync(operation.Id, "pending", null, ex.Message, countAttempt: false, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is TrueNasRpcException or InvalidOperationException)
        {
            var attempts = operation.Attempts + 1;
            var failed = attempts >= options.MaxAttempts;
            logger.LogError(ex, "Storage operation {Id} failed (attempt {Attempt})", operation.Id, attempts);
            await repository.MarkOperationAsync(operation.Id, failed ? "failed" : "pending", null, ex.Message, countAttempt: true, clock.GetUtcNow());
            if (failed && operation.VersionId is { } versionId && operation.Kind == "publish")
            {
                // Частично созданные объекты не удаляются автоматически: их увидит сверка, решение за администратором.
                await repository.SetVersionStateAsync(versionId, "failed", ex.Message, clock.GetUtcNow());
            }

            if (failed && operation.Kind == "masterOpen")
            {
                // Созданное открытием разберёт закрытие (кнопка «Закрыть» в панели).
                await masterEditor.OpenFailedAsync(ex.Message);
            }
        }
    }

    private async Task PublishAsync(StorageOperation operation, CancellationToken ct)
    {
        var version = await repository.FindVersionAsync(operation.VersionId!.Value)
            ?? throw new InvalidOperationException($"version {operation.VersionId} not found");
        await Step(operation, "snapshot");
        var snapshotName = version.SnapshotId[(version.SnapshotId.IndexOf('@') + 1)..];
        var snapshot = await storage.EnsureSnapshotAsync(options.MasterZvol, snapshotName, Labels(version, operation.Id, "snapshot"), ct);

        await Step(operation, "clone");
        var clone = await storage.EnsureReadOnlyCloneAsync(snapshot.Id, version.CloneId, Labels(version, operation.Id, "published"), ct);

        await Step(operation, "extent");
        var extent = await EnsureExtentWithRetryAsync(version, clone.Id, ct);

        await Step(operation, "target");
        var target = await storage.EnsureTargetAsync(version.TargetName, $"games {version.Label}", options.PortalId, options.InitiatorGroupId, ct);
        await storage.EnsureLunAsync(target.Id, extent.Id, ct);
        var basename = await storage.GetIscsiBasenameAsync(ct);
        await repository.SetVersionIqnAsync(version.Id, $"{basename}:{version.TargetName}");

        await Step(operation, "promote");
        var retiring = await repository.PromoteAsync(version.Id, clock.GetUtcNow());
        logger.LogInformation("Library {Label} published as current; retiring {Retiring}", version.Label, retiring);
    }

    private async Task<IscsiExtent> EnsureExtentWithRetryAsync(LibraryVersion version, string zvol, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await storage.EnsureReadOnlyExtentAsync(version.ExtentName, zvol, $"clubsrv library {version.Label}", ct);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is TrueNasRpcException && attempt < options.ExtentAttempts)
            {
                // Узел /dev/zvol после clone появляется асинхронно (udev); разбирать текст ошибки не нужно — просто повтор.
                logger.LogInformation("Extent {Name} not created yet (attempt {Attempt}), retrying", version.ExtentName, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(options.ExtentRetryDelayMs), clock, ct);
            }
        }
    }

    private async Task RollbackAsync(StorageOperation operation)
    {
        var pointers = await repository.PointersAsync();
        if (pointers.Rollback?.Id != operation.VersionId)
        {
            // Указатели уже поменялись (повтор после сбоя или новая публикация) — откат выполнен или неактуален.
            return;
        }

        await repository.SwapAsync(clock.GetUtcNow());
        logger.LogInformation("Library rolled back to {Label}", pointers.Rollback!.Label);
    }

    /// <summary>
    /// Разборка версий в состоянии retiring: таргет (без force; пока ПК подключены — ждём), экстент, клон,
    /// снапшот (defer). Блокировка — не ошибка: повторяется на следующем цикле.
    /// </summary>
    public async Task<IReadOnlyList<(string Label, string Reason)>> RetireAsync(CancellationToken ct)
    {
        var blocked = new List<(string, string)>();
        foreach (var version in await repository.VersionsInStateAsync("retiring"))
        {
            var steps = new Func<Task<DeleteResult>>[]
            {
                () => storage.DeleteTargetAsync(version.TargetName, ct),
                () => storage.DeleteExtentAsync(version.ExtentName, ct),
                () => storage.DeleteDatasetAsync(version.CloneId, ct),
                () => storage.DeleteSnapshotAsync(version.SnapshotId, ct),
            };

            string? reason = null;
            foreach (var step in steps)
            {
                var result = await step();
                if (result.Outcome == DeleteOutcome.Blocked)
                {
                    reason = result.Reason ?? "blocked";
                    break;
                }
            }

            // Снапшот с defer может остаться до ухода клона — это ожидаемо и уже не мешает.
            if (reason is null || reason.StartsWith("deferred", StringComparison.Ordinal))
            {
                await repository.SetVersionStateAsync(version.Id, "retired", null, clock.GetUtcNow());
                logger.LogInformation("Library {Label} retired", version.Label);
            }
            else
            {
                blocked.Add((version.Label, reason));
            }
        }

        return blocked;
    }

    // ---- сверка ------------------------------------------------------------------------------------------------

    /// <summary>Сравнивает намерения с фактом в TrueNAS. Только сообщает: автоисправления нет.</summary>
    public async Task<IReadOnlyList<(string Kind, string Subject, string Message)>> ReconcileAsync(CancellationToken ct)
    {
        var warnings = new List<(string Kind, string Subject, string Message)>();
        var pointers = await repository.PointersAsync();
        foreach (var version in new[] { pointers.Current, pointers.Rollback }.OfType<LibraryVersion>())
        {
            var role = version.Id == pointers.Current?.Id ? "current" : "rollback";
            var snapshot = await storage.GetSnapshotAsync(version.SnapshotId, ct);
            if (snapshot is null)
            {
                warnings.Add(("missingSnapshot", version.SnapshotId, $"Снапшот {role}-версии {version.Label} отсутствует в TrueNAS"));
            }

            var clone = await storage.GetDatasetAsync(version.CloneId, ct);
            if (clone is null)
            {
                warnings.Add(("missingClone", version.CloneId, $"Клон {role}-версии {version.Label} отсутствует в TrueNAS"));
            }
            else
            {
                if (!clone.ReadOnly)
                {
                    warnings.Add(("cloneWritable", version.CloneId, $"Клон {version.Label} не read-only"));
                }

                if (clone.Origin != version.SnapshotId)
                {
                    warnings.Add(("cloneOrigin", version.CloneId, $"Клон {version.Label} сделан не из {version.SnapshotId} (origin '{clone.Origin}')"));
                }
            }

            var extent = await storage.GetExtentAsync(version.ExtentName, ct);
            if (extent is null)
            {
                warnings.Add(("missingExtent", version.ExtentName, $"iSCSI-экстент {role}-версии {version.Label} отсутствует"));
            }
            else if (!extent.ReadOnly || extent.Disk != "zvol/" + version.CloneId || !extent.Enabled)
            {
                warnings.Add(("extentMismatch", version.ExtentName, $"Экстент {version.Label}: disk '{extent.Disk}', ro={extent.ReadOnly}, enabled={extent.Enabled}"));
            }

            var target = await storage.GetTargetAsync(version.TargetName, ct);
            if (target is null)
            {
                warnings.Add(("missingTarget", version.TargetName, $"iSCSI-таргет {role}-версии {version.Label} отсутствует"));
            }
            else if (extent is not null && await storage.GetTargetExtentAsync(target.Id, extent.Id, ct) is null)
            {
                warnings.Add(("missingLun", version.TargetName, $"У таргета {version.TargetName} нет LUN с экстентом {version.ExtentName}"));
            }
        }

        // Объекты с нашей меткой, которых нет в намерениях: остатки сбоев или ручных действий.
        var known = (await repository.VersionsInStateAsync("publishing", "published", "retiring", "failed")).Select(v => v.CloneId).ToHashSet();
        foreach (var dataset in await storage.ListDatasetsAsync(options.PublishedParent + "/", ct))
        {
            if (dataset.Labels.ContainsKey(ManagedLabel) && !known.Contains(dataset.Id))
            {
                warnings.Add(("orphanClone", dataset.Id, $"Клон {dataset.Id} с меткой сервера не числится ни в одной версии"));
            }
        }

        return warnings;
    }

    private async Task Step(StorageOperation operation, string step) =>
        await repository.MarkOperationAsync(operation.Id, "running", step, null, countAttempt: false, clock.GetUtcNow());

    /// <summary>Метки clubsrv:* на каждом объекте версии.</summary>
    private static Dictionary<string, string> Labels(LibraryVersion version, Guid operationId, string role) => new()
    {
        [ManagedLabel] = "1",
        [VersionLabel] = version.Label,
        [RoleLabel] = role,
        ["clubsrv:opid"] = operationId.ToString("N"),
    };
}
