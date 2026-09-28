using System.Text.Json;
using Club.Server.Api;
using Club.Server.Data;
using Club.Server.Network;

namespace Club.Server.Imaging;

/// <summary>
/// Перезаливка — только по команде администратора. Задание взводит PXE-флаг (класс в резервации Kea); WinPE заливает
/// образ и докладывает шаги; флаг снимается только после успешного bcdboot. Сбой на любом шаге — машина при
/// следующей загрузке снова попадает в WinPE: «застряла в PXE» — безопасное состояние.
/// </summary>
public sealed class ReimageService(
    ImagingOptions options,
    KeaOptions kea,
    ImageRepository images,
    MachineRepository machines,
    NetworkRepository network,
    KeaHostSync keaSync,
    BootFiles bootFiles,
    TimeProvider clock,
    ILogger<ReimageService> logger)
{
    public static ApiException Conflict(string reason, string message) =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message, new { reason });

    public async Task<ReimageJob> RequestAsync(Guid machineId, string? imageLabel, bool allowNewDisk, string? requestedBy, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            throw Conflict("imagingDisabled", "Windows imaging is disabled (Imaging:Enabled)");
        }

        if (!kea.Enabled)
        {
            throw Conflict("keaDisabled", "The PXE flag lives in Kea, but writing to Kea is disabled (Kea:Enabled)");
        }

        var machine = await machines.FindAsync(machineId) ?? throw ApiException.NotFound("machine");
        if (!machine.Approved)
        {
            throw Conflict("notApproved", "Machine is not approved");
        }

        var image = imageLabel is null ? (await images.PointersAsync()).Current : await images.FindImageAsync(imageLabel);
        if (image is null)
        {
            throw Conflict("noImage", "No published Windows image");
        }

        if (image.State != "ready")
        {
            throw Conflict("imageNotReady", $"Image {image.Label} is not ready ({image.State})");
        }

        var settings = await network.GetAsync();
        if (!settings.Configured || KeaHostSync.Plan([machine], new IpPlan(settings)).Hosts.Count == 0)
        {
            // Без резервации в Kea негде поставить PXE-флаг.
            throw Conflict("noReservation", "Machine has no DHCP reservation (see the Network screen)");
        }

        CheckBootChain(machine);

        var job = await images.CreateJobAsync(machineId, image.Id, allowNewDisk, requestedBy, clock.GetUtcNow())
            ?? throw Conflict("alreadyRequested", "This machine is already being reinstalled");
        logger.LogInformation("Reimage of {Machine} with {Image} requested by {By}", machine.Name, image.Label, requestedBy);
        await TrySyncAsync(ct);
        return job;
    }

    public static Diskless.SecureBootReport? SecureBoot(MachineRow machine) =>
        machine.SecureBootJson is null ? null : JsonSerializer.Deserialize<Diskless.SecureBootReport>(machine.SecureBootJson, JsonSerializerOptions.Web);

    /// <summary>
    /// Заранее отказываем там, где загрузка по сети всё равно не пройдёт: иначе ПК молча загрузится с диска, а задание
    /// повиснет в «ждёт загрузки по сети». Сведения о Secure Boot — из последнего отчёта помощника (обновляются при
    /// каждой загрузке Windows); нет отчёта — проверяются только файлы.
    /// </summary>
    private void CheckBootChain(MachineRow machine)
    {
        var chain = bootFiles.Check();
        if (chain.Missing.Count > 0)
        {
            throw Conflict("bootFilesMissing", $"PXE boot files are missing or broken: {string.Join(", ", chain.Missing)}");
        }

        if (SecureBoot(machine) is not { Enabled: true } sb)
        {
            return;
        }

        if (sb.ThirdPartyCa2011 == false)
        {
            // shim iPXE подписан только сторонним CA Microsoft 2011; без него в db прошивка откажется его запускать.
            throw Conflict("secureBootThirdPartyCa",
                "Secure Boot is on, but this PC does not trust the Microsoft UEFI CA 2011 (third-party): the iPXE shim will not start. "
                + "Enable \"Microsoft 3rd-party UEFI CA\" in the firmware settings or turn Secure Boot off for the reinstall.");
        }

        if (chain.NotSecureBootReady.Count > 0)
        {
            throw Conflict("bootFilesNotSigned", $"Secure Boot is on, but these boot files are not properly signed: {string.Join(", ", chain.NotSecureBootReady)}");
        }

        if (sb.Pca2011Revoked == true && chain.File(BootFiles.BootManager2023)?.Status != "ok")
        {
            throw Conflict("needsCa2023BootManager",
                "This PC has the Windows Production PCA 2011 revoked (dbx): WinPE needs a boot manager signed with Windows UEFI CA 2023 (boot/bootx64.efi, see docs/imaging.md).");
        }
    }

    /// <summary>Загрузчик Windows с подписью CA 2023 — только ПК, где старый (PCA 2011) отозван; остальным — из boot.wim.</summary>
    public bool UsesCa2023BootManager(MachineRow machine) =>
        SecureBoot(machine)?.Pca2011Revoked == true && bootFiles.Check().File(BootFiles.BootManager2023)?.Status == "ok";

    public async Task CancelAsync(Guid machineId, CancellationToken ct)
    {
        var job = await images.ActiveJobAsync(machineId) ?? throw ApiException.NotFound("reimage");
        if (!await images.CancelAsync(job.Id, clock.GetUtcNow()))
        {
            throw job.DiskTouched || job.State == "deploying"
                ? Conflict("diskTouched", "WinPE has already touched the disk; cancelling would leave the machine without a bootloader. Let it finish or retry.")
                : Conflict("notCancellable", $"Job is {job.State}");
        }

        await TrySyncAsync(ct);
    }

    private async Task TrySyncAsync(CancellationToken ct)
    {
        try
        {
            await keaSync.SyncAsync(ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // Фоновая синхронизация сети повторит через 30 с; предупреждение появится там же.
            logger.LogWarning(ex, "Immediate Kea sync after reimage change failed");
        }
    }

    // ---------- WinPE ----------

    public async Task<DeployPlan> StartAsync(DeployStartRequest request, CancellationToken ct)
    {
        var macs = (request.Macs ?? []).Select(NormalizeMac).OfType<string>().Distinct().ToList();
        var job = await images.ArmedJobForMacsAsync(macs) ?? throw Conflict("notArmed", "No reinstall is scheduled for this PC");
        var machine = (await machines.FindAsync(job.MachineId))!;
        if (!string.Equals(request.Firmware, "uefi", StringComparison.OrdinalIgnoreCase))
        {
            await images.FailAsync(job.Id, "biosNotSupported", null, clock.GetUtcNow());
            throw Conflict("biosNotSupported", "Booted in BIOS (legacy) mode; only UEFI is supported. Switch the board to UEFI.");
        }

        var image = await images.FindImageAsync(job.ImageId);
        if (image is not { State: "ready", SizeBytes: { } size, Sha256: { } sha256 })
        {
            await images.FailAsync(job.Id, "imageUnavailable", null, clock.GetUtcNow());
            throw Conflict("imageUnavailable", "Image is not available on the server");
        }

        var systemDisk = machine.SystemDiskJson is null ? null : JsonSerializer.Deserialize<SystemDisk>(machine.SystemDiskJson, JsonSerializerOptions.Web);
        var choice = TargetDisk.Choose(request.Disks ?? [], systemDisk, job.AllowNewDisk, TargetDisk.RequiredBytes(size, image.ExpandedBytes));
        if (choice.Disk is null)
        {
            await images.FailAsync(job.Id, choice.Failure!, DiskText(choice.Failure!), clock.GetUtcNow());
            logger.LogWarning("Reimage of {Machine}: no target disk ({Reason})", machine.Name, choice.Failure);
            throw Conflict(choice.Failure!, DiskText(choice.Failure!));
        }

        await images.StartAttemptAsync(job.Id, JsonSerializer.Serialize(choice.Disk, JsonSerializerOptions.Web), clock.GetUtcNow());
        var attempt = (await images.FindJobAsync(job.Id))!.Attempts;
        logger.LogInformation("Reimage of {Machine}: WinPE started attempt {Attempt} on disk {Disk} ({Serial})", machine.Name, attempt, choice.Disk.Number, choice.Disk.Serial);
        var marker = JsonSerializer.Serialize(new { label = image.Label, sha256, jobId = job.Id }, JsonSerializerOptions.Web);
        return new DeployPlan(
            job.Id, attempt, machine.Number, Unattend.ComputerName(machine.Name, machine.Number),
            new DeployImage(image.Label, size, sha256, image.ImageIndex, $"/deploy/v1/jobs/{job.Id}/image"),
            choice.Disk, size + (1L << 30), marker);
    }

    public async Task<ReimageJob> DeployingJobAsync(Guid jobId)
    {
        var job = await images.FindJobAsync(jobId) ?? throw ApiException.NotFound("job");
        if (job.State != "deploying")
        {
            throw Conflict("notDeploying", $"Job is {job.State}; reboot to start over");
        }

        return job;
    }

    private static readonly HashSet<string> DiskSteps = ["partition", "download", "verify", "apply", "identity", "bcdboot"];

    public async Task ProgressAsync(Guid jobId, DeployProgress progress)
    {
        if (!DiskSteps.Contains(progress.Step ?? ""))
        {
            throw ApiException.Validation("step", "unknown");
        }

        var percent = progress.Percent is >= 0 and <= 100 ? (short?)progress.Percent : null;
        if (!await images.ProgressAsync(jobId, progress.Step!, percent, Truncate(progress.Message, 500), touchesDisk: true, clock.GetUtcNow()))
        {
            await DeployingJobAsync(jobId);
        }
    }

    public async Task FailAsync(Guid jobId, DeployFailure failure)
    {
        var step = DiskSteps.Contains(failure.Step ?? "") ? failure.Step! : "start";
        if (!await images.FailAsync(jobId, step, Truncate(failure.Message, 2000), clock.GetUtcNow()))
        {
            throw Conflict("notDeploying", "Job is no longer running");
        }

        logger.LogWarning("Reimage job {Job} failed at {Step}: {Message}", jobId, step, failure.Message);
    }

    /// <summary>Unattend с именем компьютера; заодно фиксируем, генерализован ли образ (без sysprep заливать нельзя).</summary>
    public async Task<string> UnattendAsync(Guid jobId, DeployUnattendRequest request)
    {
        var job = await DeployingJobAsync(jobId);
        if (request.Generalized is { } generalized)
        {
            await images.SetGeneralizedAsync(job.ImageId, generalized);
            if (!generalized)
            {
                await images.FailAsync(jobId, "identity", "notGeneralized", clock.GetUtcNow());
                throw Conflict("notGeneralized", "Image was not generalized with sysprep; identical MachineGUIDs on all PCs are not allowed (anti-cheat links the machines). Rebuild the image.");
            }
        }

        var machine = (await machines.FindAsync(job.MachineId))!;
        try
        {
            return Unattend.Merge(request.Existing, Unattend.ComputerName(machine.Name, machine.Number));
        }
        catch (System.Xml.XmlException ex)
        {
            await images.FailAsync(jobId, "identity", "badUnattend", clock.GetUtcNow());
            throw Conflict("badUnattend", $"Image unattend.xml is unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// bcdboot прошёл: снимаем флаг и сразу убираем класс из резервации Kea. Ответ «можно перезагружаться» — только
    /// когда Kea уже не отдаст загрузчик; иначе WinPE повторяет запрос (перезагрузка раньше вернула бы его в WinPE).
    /// </summary>
    public async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        var job = await images.FindJobAsync(jobId) ?? throw ApiException.NotFound("job");
        if (job.State == "deploying")
        {
            await images.MarkBootableAsync(jobId, clock.GetUtcNow());
        }
        else if (job.State is not ("booting" or "done"))
        {
            throw Conflict("notDeploying", $"Job is {job.State}");
        }

        try
        {
            await keaSync.SyncAsync(ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Kea sync after bcdboot failed for job {Job}", jobId);
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServerUnavailable, "Kea is unavailable, retry", new { reason = "keaUnavailable" });
        }

        logger.LogInformation("Reimage job {Job}: bootloader written, PXE flag cleared", jobId);
    }

    public static string? NormalizeMac(string? mac)
    {
        var hex = new string((mac ?? "").Where(char.IsAsciiHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }

    private static string DiskText(string reason) => reason switch
    {
        "systemDiskNotFound" => "The system disk reported by the helper is not present. If the disk was replaced, request the reinstall with a new disk allowed.",
        "ambiguousDisks" => "Several suitable disks; which one to wipe is unknown. Disconnect extra disks or wait for the helper to report the system disk.",
        "diskTooSmall" => "Disk is smaller than the image needs.",
        "noInternalDisk" => "No internal disk found (USB and network disks are never used).",
        _ => reason,
    };

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

public sealed record DeployStartRequest(IReadOnlyList<string>? Macs, string? Firmware, IReadOnlyList<DiskInfo>? Disks);

public sealed record DeployImage(string Label, long SizeBytes, string Sha256, int Index, string Url);

public sealed record DeployPlan(Guid JobId, int Attempt, int Seat, string ComputerName, DeployImage Image, DiskInfo TargetDisk, long TempPartitionBytes, string Marker);

public sealed record DeployProgress(string? Step, int? Percent, string? Message);

public sealed record DeployFailure(string? Step, string? Message);

public sealed record DeployUnattendRequest(string? Existing, bool? Generalized);

public sealed record DeployUnattendResponse(string Xml);
