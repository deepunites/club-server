using System.Text;
using System.Text.Json;
using Club.Saves;
using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Diskless;
using Microsoft.AspNetCore.Http.Features;

namespace Club.Server.Saves;

/// <summary>Папка из списка для помощника: ID записи (имя папки в архиве) и шаблон пути (<c>%APPDATA%\…</c>, <c>H:\…</c>).</summary>
public sealed record SaveEntryConfig(Guid Id, string Path);

/// <summary>
/// Настройки сохранений для помощника: <paramref name="Enabled"/> — сохранения включены и диск сохранений смонтирован;
/// только включённые папки, по названию.
/// </summary>
public sealed record SavesConfig(bool Enabled, int LimitMb, IReadOnlyList<string> Excludes, IReadOnlyList<SaveEntryConfig> Entries);

/// <summary>
/// API сохранений игроков для помощника на ПК (spec 2026-11-05, п. 3.3 и 3.4; docs/diskless-api.yaml): список папок,
/// выдача сохранений игрока на ПК (аренда «игрок X на ПК M»), приём новой версии только с ПК, у которого аренда, и
/// снятие аренды без сохранения. Кто такой игрок, сервер не знает: ID от шелла приводится к нижнему регистру
/// (<see cref="PlayerIds.Normalize"/>). Выдача, приём — под блокировкой игрока (<see cref="SavesRepository.LockPlayerAsync"/>).
/// </summary>
public static class SavesEndpoints
{
    /// <summary>Заголовок ответа attach (200 и 204): <see cref="SavesConfig"/> — base64 от UTF-8 JSON (в путях бывает кириллица).</summary>
    public const string ConfigHeader = "X-Club-Saves-Config";

    /// <summary>Мегабайт лимита: <c>limit_mb</c> × 1 МиБ.</summary>
    public const long BytesPerMb = 1024 * 1024;

    private const int BufferSize = 81920;

    /// <summary>Лимит распакованных сохранений игрока в байтах.</summary>
    public static long LimitBytes(int limitMb) => limitMb * BytesPerMb;

    /// <summary>Тело PUT (zip) — не больше лимита + 10 % (spec п. 3.3).</summary>
    public static long MaxBodyBytes(int limitMb) => LimitBytes(limitMb) + LimitBytes(limitMb) / 10;

    public static void MapSavesEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(MachineAuthMiddleware.Prefix + "/machines/{machineId:guid}/saves");
        api.MapGet("/config", ConfigAsync);
        api.MapPost("/{playerId}/attach", AttachAsync);
        api.MapPut("/{playerId}", SaveAsync);
        api.MapDelete("/{playerId}/lease", ReleaseAsync);
    }

    /// <summary>Список папок (помощник: при старте и раз в 5 минут). Отвечает и когда сохранения недоступны — с <c>enabled=false</c>.</summary>
    private static async Task<IResult> ConfigAsync(Guid machineId, HttpContext context, SavesOptions options, SaveStore store, SavesRepository saves)
    {
        DisklessEndpoints.RequireSelf(context, machineId);
        return Results.Json(await BuildConfigAsync(options, store, saves), ApiJson.Options);
    }

    /// <summary>
    /// Выдача: архив текущей папки игрока (или <c>.prev</c>, п. 3.3) и аренда этой машине — забирается у другой, если
    /// была. У игрока ничего нет — 204. Архив сначала собирается во временный файл под блокировкой (параллельный приём
    /// мог бы переименовать папку посреди чтения), отдаётся уже после неё; аренда берётся, когда архив готов.
    /// </summary>
    private static async Task<IResult> AttachAsync(Guid machineId, string playerId, HttpContext context, SavesOptions options, SaveStore store,
        SavesRepository saves, ILoggerFactory logs)
    {
        var id = RequirePlayer(context, machineId, playerId, options, store, logs);
        string? zipPath = null;
        await using (await saves.LockPlayerAsync(id, context.RequestAborted))
        {
            var config = await BuildConfigAsync(options, store, saves);
            if (store.CurrentDirectory(id) is { } directory)
            {
                zipPath = store.NewTempZipPath();
                try
                {
                    using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    store.WriteZip(directory, file);
                }
                catch
                {
                    TryDeleteFile(zipPath);
                    throw;
                }
            }

            await saves.TakeLeaseAsync(id, machineId);
            context.Response.Headers[ConfigHeader] = EncodeConfig(config);
        }

        if (zipPath is null)
        {
            return Results.NoContent();
        }

        // Файл удаляется, когда ответ отправлен (или оборван).
        var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return Results.Stream(stream, "application/zip");
    }

    /// <summary>
    /// Приём новой версии (п. 3.4): только с машины, у которой аренда (иначе 409 <c>notAttached</c>, отметка
    /// <c>rejected</c>); тело — во временный файл, не больше <see cref="MaxBodyBytes"/>; распаковка с проверкой имён,
    /// записей списка и лимита во временную папку; замена с <c>.prev</c>; аренда снимается. Больше лимита — 413
    /// <c>overLimit</c> (аренда снимается, прошлая версия остаётся), плохой архив — 400 <c>badArchive</c> (аренда остаётся).
    /// </summary>
    private static async Task<IResult> SaveAsync(Guid machineId, string playerId, HttpContext context, SavesOptions options, SaveStore store,
        SavesRepository saves, ILoggerFactory logs)
    {
        var id = RequirePlayer(context, machineId, playerId, options, store, logs);
        var log = logs.CreateLogger("Club.Server.Saves");
        await using var playerLock = await saves.LockPlayerAsync(id, context.RequestAborted);
        if (await saves.LeaseHolderAsync(id) != machineId)
        {
            await saves.RecordResultAsync(id, SavesRepository.Results.Rejected, $"Saves from machine {machineId} without the lease");
            log.LogWarning("Saves of player {PlayerId} from machine {MachineId} rejected: not attached to this machine", id, machineId);
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Player saves are not attached to this machine", new { reason = "notAttached" });
        }

        var settings = await saves.SettingsAsync();
        var entries = (await saves.EntriesAsync(enabledOnly: true)).Select(e => e.Id).ToHashSet();
        var maxBody = MaxBodyBytes(settings.LimitMb);
        var zipPath = store.NewTempZipPath();
        string? temp = null;
        try
        {
            if (context.Request.ContentLength > maxBody || !await CopyBodyAsync(context, zipPath, maxBody))
            {
                throw await OverLimitAsync(saves, id, machineId, $"Saves archive is larger than {maxBody} bytes (limit {settings.LimitMb} MB + 10%)", log);
            }

            temp = store.NewTempDirectory();
            var incoming = temp;
            SaveUnpackResult result;
            try
            {
                using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                result = SaveArchive.Unpack(zip, entryId => entries.Contains(entryId) ? Path.Combine(incoming, entryId.ToString("D")) : null,
                    LimitBytes(settings.LimitMb), unknownIsError: true);
            }
            catch (SaveArchiveException ex) when (ex.Reason == SaveArchiveException.OverLimit)
            {
                throw await OverLimitAsync(saves, id, machineId, ex.Message, log);
            }
            catch (SaveArchiveException ex)
            {
                await saves.RecordResultAsync(id, SavesRepository.Results.Error, ex.Message);
                log.LogWarning("Saves of player {PlayerId} from machine {MachineId} rejected: {Reason}", id, machineId, ex.Message);
                throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, ex.Message, new { reason = SaveArchiveException.BadArchive });
            }

            store.Promote(id, temp);
            temp = null;
            await saves.RecordSavedAsync(id, machineId, result.Bytes, result.Files);
            await saves.ReleaseLeaseAsync(id, machineId);
            log.LogInformation("Saves of player {PlayerId} accepted from machine {MachineId}: {Bytes} bytes, {Files} files", id, machineId, result.Bytes, result.Files);
            return Results.NoContent();
        }
        finally
        {
            TryDeleteFile(zipPath);
            if (temp is not null)
            {
                TryDeleteDirectory(temp);
            }
        }
    }

    /// <summary>Снять аренду без сохранения (выдача на ПК не удалась после ответа сервера). Аренду другой машины не трогает.</summary>
    private static async Task<IResult> ReleaseAsync(Guid machineId, string playerId, HttpContext context, SavesOptions options, SaveStore store,
        SavesRepository saves, ILoggerFactory logs)
    {
        var id = RequirePlayer(context, machineId, playerId, options, store, logs);
        await saves.ReleaseLeaseAsync(id, machineId);
        return Results.NoContent();
    }

    /// <summary>
    /// Общие проверки путей игрока: токен этой машины (403), сохранения включены и диск смонтирован (503
    /// <c>savesUnavailable</c>), ID игрока по формату (400). Возвращает ID в нижнем регистре.
    /// </summary>
    private static string RequirePlayer(HttpContext context, Guid machineId, string playerId, SavesOptions options, SaveStore store, ILoggerFactory logs)
    {
        DisklessEndpoints.RequireSelf(context, machineId);
        if (!options.Enabled || !store.Available)
        {
            if (options.Enabled)
            {
                logs.CreateLogger("Club.Server.Saves").LogWarning(
                    "Saves root {Root} has no {Marker}: saves disk not mounted, answering 503", options.Root, SavesOptions.MarkerFile);
            }

            throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServerUnavailable,
                "Player saves are unavailable (disabled or the saves disk is not mounted)", new { reason = "savesUnavailable" });
        }

        return PlayerIds.Normalize(playerId) ?? throw ApiException.Validation("playerId", "format");
    }

    private static async Task<SavesConfig> BuildConfigAsync(SavesOptions options, SaveStore store, SavesRepository saves)
    {
        var settings = await saves.SettingsAsync();
        var entries = await saves.EntriesAsync(enabledOnly: true);
        return new SavesConfig(options.Enabled && store.Available, settings.LimitMb, settings.Excludes,
            entries.Select(e => new SaveEntryConfig(e.Id, e.Path)).ToList());
    }

    private static string EncodeConfig(SavesConfig config) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, ApiJson.Options)));

    /// <summary>
    /// Тело запроса — в файл. false — тело больше <paramref name="maxBody"/> (остаток не читается). Лимит Kestrel на
    /// тело (30 МБ по умолчанию) поднимается до него только здесь; TestServer такой настройки не даёт — тогда считаем сами.
    /// </summary>
    private static async Task<bool> CopyBodyAsync(HttpContext context, string path, long maxBody)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBody;
        }

        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        var buffer = new byte[BufferSize];
        long total = 0;
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
                if (total > maxBody)
                {
                    return false;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return false;
        }

        return true;
    }

    /// <summary>Больше лимита: отметка <c>overLimit</c> у игрока, аренда снимается (spec п. 3.3), прошлая версия не тронута.</summary>
    private static async Task<ApiException> OverLimitAsync(SavesRepository saves, string playerId, Guid machineId, string message, ILogger log)
    {
        await saves.RecordResultAsync(playerId, SavesRepository.Results.OverLimit, message);
        await saves.ReleaseLeaseAsync(playerId, machineId);
        log.LogWarning("Saves of player {PlayerId} from machine {MachineId} over the limit: {Reason}", playerId, machineId, message);
        return new ApiException(StatusCodes.Status413PayloadTooLarge, ErrorCodes.Validation, message, new { reason = SaveArchiveException.OverLimit });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Остаток в .tmp уберёт SaveStore.CleanTemp при старте.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Остаток в .tmp уберёт SaveStore.CleanTemp при старте.
        }
    }
}
