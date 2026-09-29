using Microsoft.Extensions.Logging;

namespace Club.Helper.Core;

/// <summary>Последнее назначение тома, сохранённое на ПК: сервер недоступен при загрузке — игры всё равно есть.</summary>
public interface IAssignmentCache
{
    Task<VolumeAssignment?> LoadAsync(CancellationToken ct);

    Task SaveAsync(VolumeAssignment? assignment, CancellationToken ct);
}

public sealed class InMemoryAssignmentCache : IAssignmentCache
{
    public VolumeAssignment? Current { get; set; }

    public bool HasValue { get; private set; }

    public Task<VolumeAssignment?> LoadAsync(CancellationToken ct) => Task.FromResult(HasValue ? Current : null);

    public Task SaveAsync(VolumeAssignment? assignment, CancellationToken ct)
    {
        Current = assignment;
        HasValue = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Один такт помощника: привести том к назначению → сообщить факты серверу → в ответе узнать актуальное назначение.
/// Сервер недоступен — действует последнее известное назначение (из памяти или с диска), том не отключается.
/// </summary>
public sealed class HelperLoop(
    DisklessApiClient api, VolumeManager volumes, IAssignmentCache cache, IMachineIdentity identity, HelperOptions options, ILogger<HelperLoop> logger,
    MasterManager? masters = null)
{
    private VolumeAssignment? _desired;
    private bool _known;
    private MasterAssignment? _master;
    private bool _masterKnown;
    private string? _contentsReportedFor;

    public MountedVolume? LastReport { get; private set; }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!_known)
        {
            await LearnAssignmentAsync(ct);
        }

        var report = await ApplyAsync(ct);
        var master = await ApplyMasterAsync(ct);
        var reply = await ReportAsync(report, master, ct);
        if (reply is null)
        {
            return;
        }

        var changed = false;
        if (!Equals(reply.Volume, _desired))
        {
            // Новая версия или откат: применяем сразу, не дожидаясь следующего такта.
            logger.LogInformation("Assignment changed: {Old} -> {New}", _desired?.LibraryVersion, reply.Volume?.LibraryVersion);
            _desired = reply.Volume;
            _known = true;
            await cache.SaveAsync(_desired, ct);
            report = await ApplyAsync(ct);
            changed = true;
        }

        var masterKnownBefore = _masterKnown;
        _masterKnown = true;
        if (!Equals(reply.Master, _master) || !masterKnownBefore)
        {
            // Администратор открыл или закрыл правку мастер-тома.
            _master = reply.Master;
            master = await ApplyMasterAsync(ct);
            changed = true;
        }

        if (changed)
        {
            await ReportAsync(report, master, ct);
        }
    }

    public MasterReport? LastMasterReport { get; private set; }

    private async Task<MasterReport?> ApplyMasterAsync(CancellationToken ct) =>
        LastMasterReport = masters is null ? null : await masters.ApplyAsync(_master, _masterKnown, ct);

    private async Task LearnAssignmentAsync(CancellationToken ct)
    {
        try
        {
            _desired = await api.GetVolumeAsync(ct);
            _known = true;
            await cache.SaveAsync(_desired, ct);
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            // Сервер клуба недоступен (или машина не одобрена): монтируем то, что было назначено в прошлый раз.
            _desired = await cache.LoadAsync(ct);
            _known = _desired is not null;
            logger.LogWarning("Server unavailable ({Reason}); using cached assignment {Version}", ex.Message, _desired?.LibraryVersion);
        }
    }

    private async Task<MountedVolume> ApplyAsync(CancellationToken ct)
    {
        if (!_known)
        {
            // Ни сервера, ни сохранённого назначения — ничего не трогаем (и ничего не отключаем).
            return LastReport = new MountedVolume("none", Error: "no assignment known yet");
        }

        return LastReport = await volumes.ApplyAsync(_desired, ct);
    }

    private async Task<StatusAccepted?> ReportAsync(MountedVolume report, MasterReport? master, CancellationToken ct)
    {
        try
        {
            // Состав версии (папки на томе) — один раз после подключения версии, а не в каждом отчёте.
            if (report.State == "mounted" && report.LibraryVersion != _contentsReportedFor)
            {
                report = report with { Contents = await volumes.ContentsAsync(report, ct) };
            }

            var facts = await identity.ReadAsync(ct);
            var reply = await api.ReportStatusAsync(
                new MachineStatus(options.HelperVersion, facts.BootTime, report, facts.DhcpServers, facts.ImageVersion, facts.SystemDisk, facts.SecureBoot,
                    facts.InitiatorIqn, master),
                ct);
            if (report.Contents is not null)
            {
                _contentsReportedFor = report.LibraryVersion;
            }

            return reply;
        }
        catch (Exception ex) when (ex is ServerUnavailableException or PendingApprovalException)
        {
            logger.LogWarning("Status report failed: {Reason}", ex.Message);
            return null;
        }
    }
}
