using System.Text.RegularExpressions;
using Club.Server.Data;
using Club.Server.Library;
using Club.TrueNas;

namespace Club.Server.Diskless;

/// <summary>
/// Эталон Windows бездиска: версии — снапшоты zvol эталона. Публикация разрешена, только когда машина режима мастера
/// не подключена к эталону (Windows выключена): иначе снапшот поймает недописанную файловую систему.
/// Места получают новую версию при следующей загрузке (клон пересоздаётся), откат — то же самое.
/// </summary>
public sealed partial class DisklessImages(
    DisklessRepository repository,
    MachineRepository machines,
    TrueNasStorage storage,
    DisklessOptions options,
    TimeProvider clock,
    ILogger<DisklessImages> logger)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex LabelPattern();

    public async Task PublishAsync(string label, string? comment, string? requestedBy, CancellationToken ct)
    {
        if (!LabelPattern().IsMatch(label))
        {
            throw new LibraryRequestException("label", "Version label must match ^[a-z0-9][a-z0-9-]{0,39}$");
        }

        if (await repository.FindVersionAsync(label) is not null)
        {
            throw new LibraryRequestException("exists", $"Version {label} already exists");
        }

        if (await storage.GetDatasetAsync(options.ImageZvol, ct) is null)
        {
            throw new LibraryRequestException("noImage", $"{options.ImageZvol} does not exist in TrueNAS");
        }

        var sessions = await storage.GetSessionsAsync(ct);
        if (sessions.Any(s => s.Target.EndsWith($":{options.MasterTargetName}", StringComparison.Ordinal)))
        {
            throw new LibraryRequestException("masterConnected", "The master PC is still connected to the system image; shut it down before publishing");
        }

        var labels = new Dictionary<string, string> { ["clubsrv:role"] = "diskless-image", ["clubsrv:version"] = label };
        var snapshot = await storage.EnsureSnapshotAsync(options.ImageZvol, DisklessOptions.SnapshotPrefix + label, labels, ct);
        await repository.AddCurrentVersionAsync(label, snapshot.Id, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), requestedBy, clock.GetUtcNow());
        logger.LogInformation("Diskless system image {Version} published by {User}", label, requestedBy);
    }

    public async Task RollbackAsync(string? requestedBy)
    {
        if (!await repository.SwapRollbackAsync(clock.GetUtcNow()))
        {
            throw new LibraryRequestException("noRollback", "There is no previous system image version");
        }

        logger.LogInformation("Diskless system image rolled back by {User}", requestedBy);
    }

    public async Task SetMasterAsync(Guid? machineId, bool install, string? requestedBy)
    {
        if (machineId is { } id)
        {
            var machine = await machines.FindAsync(id) ?? throw new LibraryRequestException("noMachine", "Machine not found");
            if (!machine.Approved)
            {
                throw new LibraryRequestException("notApproved", "Machine is not approved");
            }
        }

        await repository.SetMasterAsync(machineId, install, clock.GetUtcNow());
        logger.LogInformation("Diskless master mode: {Machine} (install: {Install}) by {User}", machineId?.ToString() ?? "off", install, requestedBy);
    }

    public async Task SetBootModeAsync(Guid machineId, string mode, string? requestedBy)
    {
        if (mode is not ("local" or "diskless"))
        {
            throw new LibraryRequestException("mode", "Boot mode must be 'local' or 'diskless'");
        }

        var machine = await machines.FindAsync(machineId) ?? throw new LibraryRequestException("noMachine", "Machine not found");
        if (!machine.Approved)
        {
            throw new LibraryRequestException("notApproved", "Machine is not approved");
        }

        await repository.SetBootModeAsync(machineId, mode);
        logger.LogInformation("Machine {Machine} boot mode set to {Mode} by {User}", machine.Name, mode, requestedBy);
    }
}
