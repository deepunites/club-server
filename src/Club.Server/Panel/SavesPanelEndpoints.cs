using Club.Saves;
using Club.Server.Api;
using Club.Server.Library;
using Club.Server.Saves;

namespace Club.Server.Panel;

/// <summary>Общие настройки сохранений; поле без значения не меняется.</summary>
public sealed record SaveSettingsRequest(int? LimitMb, IReadOnlyList<string>? Excludes);

/// <summary>Папка из списка; при изменении поле без значения не меняется, при создании <c>enabled</c> по умолчанию — да.</summary>
public sealed record SaveEntryRequest(string? Name, string? Path, bool? Enabled);

/// <summary>Страница игроков: <c>total</c> — сколько всего подходит под поиск.</summary>
public sealed record SavePlayersView(IReadOnlyList<PlayerSaveRow> Items, int Total, int PageSize);

/// <summary>
/// Состояние сохранений: <c>enabled</c> — <c>Saves:Enabled</c>, <c>available</c> — диск смонтирован (есть файл-метка),
/// <c>usedBytes</c> — сколько занимают файлы в корне (текущие версии, <c>.prev</c>, временные; без диска — 0).
/// </summary>
public sealed record SavesStatusView(bool Enabled, bool Available, string Root, long UsedBytes);

/// <summary>
/// Вкладка «Сохранения» (spec 2026-11-05, п. 3.5): лимит и исключения, список папок (шаблон пути проверяется так же,
/// как в помощнике, — <see cref="SavePathTemplate.Validate"/> с буквами библиотек сервера), игроки и удаление игрока
/// (папка, <c>.prev</c>, аренда, строка — под блокировкой игрока, как выдача и приём).
/// </summary>
public static class SavesPanelEndpoints
{
    public const int MinLimitMb = 1;

    public const int MaxLimitMb = 2000;

    public const int MaxExcludes = 50;

    public const int MaxExcludeLength = 100;

    /// <summary>Как проверка <c>save_entries.name</c> в M0012_PlayerSaves.</summary>
    public const int MaxEntryNameLength = 60;

    public const int PlayersPageSize = 50;

    public static void MapSavesPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup(PanelAuthMiddleware.Prefix + "/v1/saves");

        panel.MapGet("/settings", async (SavesRepository saves) => Results.Json(await saves.SettingsAsync(), ApiJson.Options));

        panel.MapPut("/settings", async (SaveSettingsRequest request, SavesRepository saves, ILoggerFactory logs) =>
        {
            if (request.LimitMb is < MinLimitMb or > MaxLimitMb)
            {
                throw ApiException.Validation("limitMb", "range");
            }

            var excludes = request.Excludes is null ? null : ValidExcludes(request.Excludes);
            var current = await saves.SettingsAsync();
            var settings = new SaveSettings(request.LimitMb ?? current.LimitMb, excludes ?? current.Excludes);
            await saves.UpdateSettingsAsync(settings);
            Log(logs).LogInformation("Saves settings changed in the panel: limit {LimitMb} MB, excludes {Excludes}",
                settings.LimitMb, settings.Excludes.Count == 0 ? "-" : string.Join(", ", settings.Excludes));
            return Results.Json(settings, ApiJson.Options);
        });

        panel.MapGet("/entries", async (SavesRepository saves) => Results.Json(await saves.EntriesAsync(enabledOnly: false), ApiJson.Options));

        panel.MapPost("/entries", async (SaveEntryRequest request, SavesRepository saves, LibrarySet libraries, ILoggerFactory logs) =>
        {
            var name = ValidName(request.Name);
            var path = ValidPath(request.Path, libraries);
            var entry = await saves.CreateEntryAsync(name, path, request.Enabled ?? true);
            Log(logs).LogInformation("Saves folder {Id} «{Name}» {Path} added in the panel", entry.Id, entry.Name, entry.Path);
            return Results.Json(entry, ApiJson.Options, statusCode: StatusCodes.Status201Created);
        });

        panel.MapPut("/entries/{id:guid}", async (Guid id, SaveEntryRequest request, SavesRepository saves, LibrarySet libraries, ILoggerFactory logs) =>
        {
            var name = request.Name is null ? null : ValidName(request.Name);
            var path = request.Path is null ? null : ValidPath(request.Path, libraries);
            var current = (await saves.EntriesAsync(enabledOnly: false)).FirstOrDefault(e => e.Id == id) ?? throw ApiException.NotFound("saveEntry");
            var entry = new SaveEntry(id, name ?? current.Name, path ?? current.Path, request.Enabled ?? current.Enabled);
            if (!await saves.UpdateEntryAsync(entry))
            {
                throw ApiException.NotFound("saveEntry");
            }

            Log(logs).LogInformation("Saves folder {Id} changed in the panel: «{Name}» {Path}, enabled {Enabled}", id, entry.Name, entry.Path, entry.Enabled);
            return Results.Json(entry, ApiJson.Options);
        });

        panel.MapDelete("/entries/{id:guid}", async (Guid id, SavesRepository saves, ILoggerFactory logs) =>
        {
            if (!await saves.DeleteEntryAsync(id))
            {
                throw ApiException.NotFound("saveEntry");
            }

            Log(logs).LogInformation("Saves folder {Id} deleted in the panel", id);
            return Results.NoContent();
        });

        panel.MapGet("/players", async (string? query, int? page, SavesRepository saves) =>
        {
            var (items, total) = await saves.PlayersAsync(query, Math.Max(page ?? 1, 1), PlayersPageSize);
            return Results.Json(new SavePlayersView(items, total, PlayersPageSize), ApiJson.Options);
        });

        panel.MapDelete("/players/{playerId}", async (string playerId, HttpContext context, SaveStore store, SavesRepository saves, ILoggerFactory logs) =>
        {
            var id = PlayerIds.Normalize(playerId) ?? throw ApiException.Validation("playerId", "format");

            // Без диска сохранений папку не удалить, а строка без неё потеряла бы связь с файлами на диске.
            if (!store.Available)
            {
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServerUnavailable,
                    "The saves disk is not mounted", new { reason = "savesUnavailable" });
            }

            await using (await saves.LockPlayerAsync(id, context.RequestAborted))
            {
                store.Delete(id);
                await saves.DeletePlayerAsync(id);
            }

            Log(logs).LogInformation("Saves of player {PlayerId} deleted in the panel", id);
            return Results.NoContent();
        });

        panel.MapGet("/status", (SavesOptions options, SaveStore store) =>
        {
            var available = store.Available;
            return Results.Json(new SavesStatusView(options.Enabled, available, options.Root, available ? store.Measure(options.Root).Bytes : 0), ApiJson.Options);
        });
    }

    /// <summary>
    /// Буквы библиотек сервера для шаблонов на диске игры: G: (<c>Library:DriveLetter</c>) и буквы всех библиотек из
    /// <c>Libraries:*</c>, в том числе выключенных — запись в списке не должна становиться негодной от выключения
    /// библиотеки. Буквы уже проверены при старте (одна буква D…Z).
    /// </summary>
    public static IReadOnlyCollection<char> LibraryLetters(LibrarySet libraries) =>
        libraries.Extras.Prepend(libraries.Main).Select(s => s.Options.DriveLetter[0]).Distinct().ToList();

    /// <summary>Название: по краям обрезается, 1…<see cref="MaxEntryNameLength"/> символов.</summary>
    private static string ValidName(string? raw)
    {
        var name = raw?.Trim() ?? "";
        return name.Length is 0 or > MaxEntryNameLength ? throw ApiException.Validation("name", "length") : name;
    }

    /// <summary>Шаблон пути (по краям обрезается): код ошибки — из <see cref="SavePathTemplate.Validate"/>.</summary>
    private static string ValidPath(string? raw, LibrarySet libraries)
    {
        var path = raw?.Trim();
        return SavePathTemplate.Validate(path, LibraryLetters(libraries)) is { } problem ? throw ApiException.Validation("path", problem) : path!;
    }

    /// <summary>Исключения: по краям обрезаются, не больше <see cref="MaxExcludes"/>, каждое 1…<see cref="MaxExcludeLength"/> символов.</summary>
    private static List<string> ValidExcludes(IReadOnlyList<string> raw)
    {
        if (raw.Count > MaxExcludes)
        {
            throw ApiException.Validation("excludes", "tooMany");
        }

        var excludes = raw.Select(e => e?.Trim() ?? "").ToList();
        return excludes.Any(e => e.Length is 0 or > MaxExcludeLength) ? throw ApiException.Validation("excludes", "length") : excludes;
    }

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger("Club.Server.Panel");
}
