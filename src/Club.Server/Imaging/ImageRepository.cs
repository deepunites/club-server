using System.Text.Json;
using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Imaging;

public sealed class WindowsImage
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
    public string State { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int ImageIndex { get; init; }
    public long? SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? WimImagesJson { get; init; }
    public bool? Generalized { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ImportedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? RetiredAt { get; init; }
    public string? LastError { get; init; }

    public IReadOnlyList<WimImage> WimImages =>
        WimImagesJson is null ? [] : JsonSerializer.Deserialize<List<WimImage>>(WimImagesJson, JsonSerializerOptions.Web) ?? [];

    /// <summary>Объём выбранного образа после распаковки (для проверки размера диска).</summary>
    public long ExpandedBytes => WimImages.FirstOrDefault(i => i.Index == ImageIndex)?.TotalBytes ?? 0;
}

public sealed record ImagePointers(WindowsImage? Current, WindowsImage? Rollback);

public sealed class ReimageJob
{
    public Guid Id { get; init; }
    public Guid MachineId { get; init; }
    public Guid ImageId { get; init; }
    public string State { get; init; } = "";
    public bool PxeArmed { get; init; }
    public bool DiskTouched { get; init; }
    public bool AllowNewDisk { get; init; }
    public string? Step { get; init; }
    public short? Percent { get; init; }
    public string? Message { get; init; }
    public string? Failure { get; init; }
    public string? TargetDiskJson { get; init; }
    public int Attempts { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? BcdbootAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public static readonly string[] ActiveStates = ["requested", "deploying", "failed", "booting"];
}

public sealed class ImageRepository(NpgsqlDataSource db)
{
    private const string ImageColumns = """
        id, label, state, source_file AS SourceFile, file_path AS FilePath, image_index AS ImageIndex, size_bytes AS SizeBytes,
        sha256, wim_images::text AS WimImagesJson, generalized, requested_by AS RequestedBy, created_at AS CreatedAt,
        imported_at AS ImportedAt, published_at AS PublishedAt, retired_at AS RetiredAt, last_error AS LastError
        """;

    private const string JobColumns = """
        id, machine_id AS MachineId, image_id AS ImageId, state, pxe_armed AS PxeArmed, disk_touched AS DiskTouched,
        allow_new_disk AS AllowNewDisk, step, percent, message, failure, target_disk::text AS TargetDiskJson, attempts,
        requested_by AS RequestedBy, created_at AS CreatedAt, updated_at AS UpdatedAt, bcdboot_at AS BcdbootAt,
        finished_at AS FinishedAt
        """;

    static ImageRepository() => DapperSetup.Ensure();

    // ---------- Образы ----------

    public async Task<IReadOnlyList<WindowsImage>> ImagesAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<WindowsImage>($"SELECT {ImageColumns} FROM windows_images ORDER BY created_at DESC")).ToList();
    }

    public async Task<WindowsImage?> FindImageAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<WindowsImage>($"SELECT {ImageColumns} FROM windows_images WHERE id = @id", new { id });
    }

    public async Task<WindowsImage?> FindImageAsync(string label)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<WindowsImage>($"SELECT {ImageColumns} FROM windows_images WHERE label = @label", new { label });
    }

    /// <summary>Намерение импорта. <c>false</c> — версия с такой меткой уже есть.</summary>
    public async Task<bool> CreateImportAsync(Guid id, string label, string sourceFile, string filePath, int imageIndex, string? requestedBy)
    {
        await using var c = await db.OpenConnectionAsync();
        try
        {
            await c.ExecuteAsync(
                """
                INSERT INTO windows_images (id, label, state, source_file, file_path, image_index, requested_by)
                VALUES (@id, @label, 'importing', @sourceFile, @filePath, @imageIndex, @requestedBy)
                """,
                new { id, label, sourceFile, filePath, imageIndex, requestedBy });
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<WindowsImage>> ImagesInStateAsync(string state)
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<WindowsImage>($"SELECT {ImageColumns} FROM windows_images WHERE state = @state ORDER BY created_at", new { state })).ToList();
    }

    public async Task MarkImportedAsync(Guid id, long size, string sha256, IReadOnlyList<WimImage> images, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE windows_images SET state = 'ready', size_bytes = @size, sha256 = @sha256, wim_images = @json::jsonb,
                   imported_at = @now, last_error = NULL
            WHERE id = @id AND state = 'importing'
            """,
            new { id, size, sha256, json = JsonSerializer.Serialize(images, JsonSerializerOptions.Web), now });
    }

    public async Task MarkImportFailedAsync(Guid id, string error)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE windows_images SET state = 'failed', last_error = @error WHERE id = @id AND state = 'importing'", new { id, error });
    }

    public async Task RecordImportErrorAsync(Guid id, string error)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE windows_images SET last_error = @error WHERE id = @id", new { id, error });
    }

    public async Task MarkRetiredAsync(Guid id, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE windows_images SET state = 'retired', retired_at = @now WHERE id = @id", new { id, now });
    }

    public async Task SetGeneralizedAsync(Guid id, bool generalized)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE windows_images SET generalized = @generalized WHERE id = @id", new { id, generalized });
    }

    public async Task<ImagePointers> PointersAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var ids = await c.QuerySingleAsync<(Guid? Current, Guid? Rollback)>("SELECT current_id, rollback_id FROM image_pointers");
        return new ImagePointers(
            ids.Current is { } cur ? await FindImageAsync(cur) : null,
            ids.Rollback is { } rb ? await FindImageAsync(rb) : null);
    }

    /// <summary>Публикация: новая версия — текущая, прежняя текущая — откатная. Возвращает вытесненную откатную.</summary>
    public async Task<Guid?> PublishAsync(Guid id, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var (current, rollback) = await c.QuerySingleAsync<(Guid?, Guid?)>("SELECT current_id, rollback_id FROM image_pointers FOR UPDATE", transaction: tx);
        if (current == id)
        {
            await tx.CommitAsync();
            return null;
        }

        var evicted = rollback == id ? null : rollback;
        await c.ExecuteAsync("UPDATE image_pointers SET current_id = @id, rollback_id = @current", new { id, current }, tx);
        await c.ExecuteAsync("UPDATE windows_images SET published_at = @now WHERE id = @id", new { id, now }, tx);
        await tx.CommitAsync();
        return evicted;
    }

    /// <summary>Откат: откатная становится текущей, текущая — откатной (повторный откат возвращает обратно).</summary>
    public async Task<bool> RollbackAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync(
            "UPDATE image_pointers SET current_id = rollback_id, rollback_id = current_id WHERE rollback_id IS NOT NULL AND current_id IS NOT NULL") == 1;
    }

    // ---------- Перезаливка ----------

    public async Task<ReimageJob?> ActiveJobAsync(Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<ReimageJob>(
            $"SELECT {JobColumns} FROM reimage_jobs WHERE machine_id = @machineId AND state = ANY(@states)",
            new { machineId, states = ReimageJob.ActiveStates });
    }

    public async Task<ReimageJob?> FindJobAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<ReimageJob>($"SELECT {JobColumns} FROM reimage_jobs WHERE id = @id", new { id });
    }

    /// <summary>Последнее задание каждой машины (для экрана «Рабочие станции»).</summary>
    public async Task<IReadOnlyDictionary<Guid, ReimageJob>> LatestJobsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var jobs = await c.QueryAsync<ReimageJob>($"SELECT DISTINCT ON (machine_id) {JobColumns} FROM reimage_jobs ORDER BY machine_id, created_at DESC");
        return jobs.ToDictionary(j => j.MachineId);
    }

    public async Task<IReadOnlySet<Guid>> ArmedMachinesAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<Guid>("SELECT machine_id FROM reimage_jobs WHERE pxe_armed")).ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> ImagesInUseAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<Guid>("SELECT DISTINCT image_id FROM reimage_jobs WHERE state IN ('requested', 'deploying', 'failed')")).ToHashSet();
    }

    /// <summary>
    /// Новое задание. Вытесняет задание в <c>booting</c> (Windows ещё не отчиталась) и упавшее (<c>failed</c>, например
    /// чтобы залить другой версией). Признак «диск тронут» наследуется от упавшего: иначе отмена нового задания сняла
    /// бы PXE-флаг с машины, у которой уже нет загрузчика.
    /// </summary>
    public async Task<ReimageJob?> CreateJobAsync(Guid machineId, Guid imageId, bool allowNewDisk, string? requestedBy, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SELECT 1 FROM machines WHERE id = @machineId FOR UPDATE", new { machineId }, tx);
        var diskTouched = await c.ExecuteScalarAsync<bool>(
            "SELECT COALESCE(bool_or(disk_touched), false) FROM reimage_jobs WHERE machine_id = @machineId AND state = 'failed'",
            new { machineId }, tx);
        await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET state = 'cancelled', pxe_armed = false, message = 'superseded', finished_at = @now, updated_at = @now
            WHERE machine_id = @machineId AND state IN ('booting', 'failed')
            """,
            new { machineId, now }, tx);
        var id = Guid.NewGuid();
        try
        {
            await c.ExecuteAsync(
                """
                INSERT INTO reimage_jobs (id, machine_id, image_id, state, pxe_armed, disk_touched, allow_new_disk, requested_by, created_at, updated_at)
                VALUES (@id, @machineId, @imageId, 'requested', true, @diskTouched, @allowNewDisk, @requestedBy, @now, @now)
                """,
                new { id, machineId, imageId, diskTouched, allowNewDisk, requestedBy, now }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }

        await tx.CommitAsync();
        return await FindJobAsync(id);
    }

    /// <summary>Отмена возможна, только пока WinPE не тронул диск: иначе на диске нет рабочего загрузчика.</summary>
    public async Task<bool> CancelAsync(Guid jobId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET state = 'cancelled', pxe_armed = false, finished_at = @now, updated_at = @now
            WHERE id = @jobId AND state IN ('requested', 'failed') AND NOT disk_touched
            """,
            new { jobId, now }) == 1;
    }

    public async Task<ReimageJob?> ArmedJobForMacsAsync(IReadOnlyList<string> macs)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<ReimageJob>(
            $"""
            SELECT {JobColumns} FROM reimage_jobs
            WHERE id = (SELECT j.id FROM reimage_jobs j JOIN machines m ON m.id = j.machine_id
                        WHERE j.pxe_armed AND m.mac_addresses && @macs LIMIT 1)
            """,
            new { macs = macs.ToArray() });
    }

    public async Task StartAttemptAsync(Guid jobId, string? targetDiskJson, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET state = 'deploying', attempts = attempts + 1, step = 'start', percent = NULL, message = NULL,
                   failure = NULL, target_disk = @targetDiskJson::jsonb, updated_at = @now
            WHERE id = @jobId AND pxe_armed
            """,
            new { jobId, targetDiskJson, now });
    }

    public async Task<bool> ProgressAsync(Guid jobId, string step, short? percent, string? message, bool touchesDisk, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET step = @step, percent = @percent, message = @message,
                   disk_touched = disk_touched OR @touchesDisk, updated_at = @now
            WHERE id = @jobId AND state = 'deploying'
            """,
            new { jobId, step, percent, message, touchesDisk, now }) == 1;
    }

    /// <summary>Сбой в WinPE: PXE-флаг остаётся — после перезагрузки машина снова попадёт в WinPE.</summary>
    public async Task<bool> FailAsync(Guid jobId, string failure, string? message, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET state = 'failed', failure = @failure, message = @message, updated_at = @now
            WHERE id = @jobId AND state IN ('deploying', 'requested') AND pxe_armed
            """,
            new { jobId, failure, message, now }) == 1;
    }

    /// <summary>bcdboot прошёл: снимаем PXE-флаг. Только после этого машина грузится с диска.</summary>
    public async Task<bool> MarkBootableAsync(Guid jobId, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync(
            """
            UPDATE reimage_jobs SET state = 'booting', pxe_armed = false, step = 'bcdboot', percent = 100, bcdboot_at = @now, updated_at = @now
            WHERE id = @jobId AND state = 'deploying'
            """,
            new { jobId, now }) == 1;
    }

    /// <summary>Windows после заливки отчиталась нужной версией образа — задание выполнено.</summary>
    public async Task CompleteBootedAsync(Guid machineId, string imageLabel, DateTimeOffset now)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            UPDATE reimage_jobs j SET state = 'done', finished_at = @now, updated_at = @now
            FROM windows_images i
            WHERE j.image_id = i.id AND j.machine_id = @machineId AND j.state = 'booting' AND i.label = @imageLabel
            """,
            new { machineId, imageLabel, now });
    }
}
