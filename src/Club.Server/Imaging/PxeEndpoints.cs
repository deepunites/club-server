using System.Reflection;
using System.Text;
using System.Text.Json;
using Club.Server.Api;

namespace Club.Server.Imaging;

/// <summary>
/// Загрузка по сети для перезаливки (без авторизации: iPXE и WinPE ничего не знают о токенах).
/// Цепочка: прошивка → Kea (только машинам с PXE-флагом) → ipxe-shim.efi (подписан Microsoft) → ipxe.efi (подписан
/// iPXE) по TFTP → <c>/pxe/v1/boot.ipxe</c> →
/// скрипт машины → wimboot + WinPE + скрипт заливки → <c>/deploy/v1</c>.
/// </summary>
public static class PxeEndpoints
{
    public const string Prefix = "/pxe/v1";

    /// <summary>
    /// Загрузка с локального диска: в UEFI — выход в менеджер загрузки прошивки (следующий пункт порядка загрузки),
    /// в BIOS — первый жёсткий диск. [ГИПОТЕЗА: поведение <c>exit 1</c> зависит от прошивки — проверить на стенде.]
    /// </summary>
    public const string LocalBootScript = """
        #!ipxe
        echo Club server: no reinstall scheduled, booting from local disk
        iseq ${platform} efi && exit 1 || sanboot --no-describe --drive 0x80

        """;

    public static void MapPxeEndpoints(this IEndpointRouteBuilder app)
    {
        var pxe = app.MapGroup(Prefix);

        // Первый скрипт: iPXE узнаёт свой MAC и спрашивает, что делать именно этой машине. Скрипт машины заменяет этот
        // (--replace): его exit 1 — выход в прошивку. Сервер не ответил (перезапуск) — до минуты повторов, потом локальный
        // диск: бездисковый ПК иначе ушёл бы в прошивку с первой же неудачи.
        pxe.MapGet("/boot.ipxe", () => Results.Text("""
            #!ipxe
            set tries:int32 0
            :retry
            chain --autofree --replace /pxe/v1/machines/${netX/mac:hexhyp}/boot.ipxe || goto unreachable
            :unreachable
            inc tries
            iseq ${tries} 12 && goto local ||
            echo Club server unreachable, retrying in 5 s
            sleep 5
            goto retry
            :local
            echo Club server unreachable, booting from local disk
            iseq ${platform} efi && exit 1 || sanboot --no-describe --drive 0x80

            """.Replace("\r\n", "\n", StringComparison.Ordinal), "text/plain"));

        pxe.MapGet("/machines/{mac}/boot.ipxe", async (string mac, HttpContext context, ImageRepository images, Data.MachineRepository machines, ReimageService reimage,
            Diskless.DisklessOptions diskless, Diskless.DisklessRepository disklessRepository, Diskless.SeatDisks seats, ILoggerFactory logs, CancellationToken ct) =>
        {
            var normalized = ReimageService.NormalizeMac(mac);
            var job = normalized is null ? null : await images.ArmedJobForMacsAsync([normalized]);
            if (job is null)
            {
                // Бездиск: личный диск места (или эталон в режиме мастера) — загрузка по iSCSI.
                if (diskless.Enabled && normalized is not null && await machines.FindByMacAsync(normalized) is { Approved: true } candidate
                    && (candidate.BootMode == "diskless" || (await disklessRepository.GetImageAsync()).MasterMachineId == candidate.Id))
                {
                    return Results.Text((await seats.PrepareBootAsync(candidate, context.Connection.RemoteIpAddress?.ToString(), ct)).Script, "text/plain");
                }

                // Флага нет (Kea ещё не обновилась или ПК загрузился в iPXE сам) — только локальная загрузка.
                return Results.Text(LocalBootScript.Replace("\r\n", "\n", StringComparison.Ordinal), "text/plain");
            }

            var machine = (await machines.FindAsync(job.MachineId))!;
            var image = await images.FindImageAsync(job.ImageId);
            // Отозван PCA 2011 — загрузчик Windows с подписью CA 2023 вместо того, что внутри boot.wim.
            var bootManager = reimage.UsesCa2023BootManager(machine)
                ? "initrd /pxe/v1/files/boot/bootx64.efi bootx64.efi || goto failed\n"
                : "";
            logs.CreateLogger("Club.Server.Pxe").LogInformation("PXE: {Machine} ({Mac}) boots WinPE for reinstall", machine.Name, normalized);
            return Results.Text($$"""
                #!ipxe
                echo Club server: reinstalling Windows on seat {{machine.Number}} with image {{image?.Label}}
                kernel /pxe/v1/files/wimboot gui || goto failed
                {{bootManager}}initrd /pxe/v1/files/boot/BCD BCD || goto failed
                initrd /pxe/v1/files/boot/boot.sdi boot.sdi || goto failed
                initrd /pxe/v1/winpe/winpeshl.ini winpeshl.ini || goto failed
                initrd /pxe/v1/winpe/club-deploy.ps1 club-deploy.ps1 || goto failed
                initrd /pxe/v1/winpe/clubdeploy.json clubdeploy.json || goto failed
                initrd /pxe/v1/files/sources/boot.wim boot.wim || goto failed
                boot || goto failed
                :failed
                echo WinPE boot failed, see the server log. Local disk in 30 s
                sleep 30
                iseq ${platform} efi && exit 1 || sanboot --no-describe --drive 0x80

                """.Replace("\r\n", "\n", StringComparison.Ordinal), "text/plain");
        });

        // Файлы, которые wimboot кладёт в X:\Windows\System32 (обновляются вместе с сервером, boot.wim не пересобирать).
        pxe.MapGet("/winpe/{name}", (string name, ImagingOptions options) => name switch
        {
            "clubdeploy.json" => Results.Text(JsonSerializer.Serialize(new { server = options.PublicBaseUrl.TrimEnd('/') }), "application/json"),
            "club-deploy.ps1" or "winpeshl.ini" => Results.Bytes(Resource(name), "application/octet-stream"),
            _ => throw ApiException.NotFound("file"),
        });

        // wimboot и WinPE из ADK: кладёт администратор (docs/imaging.md). Range — для повторов на медленной сети.
        pxe.MapGet("/files/{**path}", (string path, ImagingOptions options) =>
        {
            var root = Path.GetFullPath(options.PxeRoot);
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                throw ApiException.NotFound("file");
            }

            return Results.File(full, "application/octet-stream", enableRangeProcessing: true);
        });
    }

    public static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinPE." + name)
            ?? throw new InvalidOperationException($"embedded {name} missing");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>API скрипта заливки в WinPE (<c>/deploy/v1</c>). Машина определяется по MAC взведённого задания.</summary>
public static class DeployEndpoints
{
    public static void MapDeployEndpoints(this IEndpointRouteBuilder app)
    {
        var deploy = app.MapGroup("/deploy/v1");
        deploy.MapPost("/start", async (DeployStartRequest request, ReimageService reimage, CancellationToken ct) =>
            Results.Json(await reimage.StartAsync(request, ct), ApiJson.Options));

        deploy.MapPut("/jobs/{jobId:guid}/progress", async (Guid jobId, DeployProgress progress, ReimageService reimage) =>
        {
            await reimage.ProgressAsync(jobId, progress);
            return Results.NoContent();
        });

        deploy.MapGet("/jobs/{jobId:guid}/image", async (Guid jobId, ReimageService reimage, ImageRepository images, ImageLibrary library) =>
        {
            var job = await reimage.DeployingJobAsync(jobId);
            var image = await images.FindImageAsync(job.ImageId) ?? throw ApiException.NotFound("image");
            var path = library.FullPath(image);
            return File.Exists(path)
                ? Results.File(path, "application/octet-stream", ImageLibrary.ImageFileName, enableRangeProcessing: true)
                : throw ReimageService.Conflict("imageUnavailable", "Image file is missing on the server");
        });

        deploy.MapPost("/jobs/{jobId:guid}/unattend", async (Guid jobId, DeployUnattendRequest request, ReimageService reimage) =>
            Results.Json(new DeployUnattendResponse(await reimage.UnattendAsync(jobId, request)), ApiJson.Options));

        deploy.MapPost("/jobs/{jobId:guid}/fail", async (Guid jobId, DeployFailure failure, ReimageService reimage) =>
        {
            await reimage.FailAsync(jobId, failure);
            return Results.NoContent();
        });

        deploy.MapPost("/jobs/{jobId:guid}/complete", async (Guid jobId, ReimageService reimage, CancellationToken ct) =>
        {
            await reimage.CompleteAsync(jobId, ct);
            return Results.Json(new { reboot = true }, ApiJson.Options);
        });
    }
}
