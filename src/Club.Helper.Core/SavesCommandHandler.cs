using System.Text;
using System.Text.Json;
using Club.Saves;

namespace Club.Helper.Core;

/// <summary>
/// Протокол канала шелла <c>\\.\pipe\club-diskless-helper</c> (spec 2026-11-05, п. 4.1 и 5): одна строка JSON запроса —
/// одна строка JSON ответа, без транспорта (канал — <c>SavesPipeServer</c> в Club.Helper).
/// Запросы: <c>{"op":"ping"}</c> → <c>{"ok":true,"version":"…"}</c>;
/// <c>{"op":"profile.attach","playerId":"…","windowsUserSid":"S-1-5-…"}</c>; <c>{"op":"profile.detach","playerId":"…"}</c>.
/// Ответ: <c>{"ok":true}</c> или <c>{"ok":false,"error":"&lt;код&gt;","message":"&lt;текст для журнала&gt;"}</c>, коды —
/// <see cref="SavesErrors"/>. Строка длиннее <see cref="MaxRequestLength"/>, не JSON-объект, неизвестная команда, пустые или
/// не строковые обязательные поля, ID игрока не по формату — <c>badRequest</c>; неизвестные поля игнорируются
/// (<c>rememberLaunchers</c> из старого текста п. 23). Команды attach и detach выполняются по одной
/// (<see cref="SemaphoreSlim"/>): вторая ждёт, пока закончится первая. <c>ping</c> состояния не трогает и отвечает сразу,
/// даже во время долгой выдачи. Исключение не выходит наружу — <c>internal</c>.
/// Самообновление (1.8.0, spec 2026-11-06, п. 4.3, 4.5): <c>ping</c> отвечает и сборкой — <c>"build"</c>, SHA256 exe
/// (<paramref name="helperBuild"/>, нет — поля нет), а пока замок сохранений у замены exe (<see cref="SavesService.Updating"/>) —
/// и <c>"updating":true</c>; attach и detach тогда — <c>updating</c>.
/// Диски игр (1.9.0, SHELL_CHANGES п. 86): <c>ping</c> после прежних полей отвечает <c>"ready"</c> и <c>"disks"</c>
/// (<paramref name="readiness"/>, снимки циклов; нет — полей нет):
/// <c>{"ok":true,"version":"…","build":"…","ready":false,"disks":[{"library":"games","letter":"G","state":"mounted"}]}</c>;
/// у диска буквы, версии библиотеки и причины может не быть — тогда нет и поля.
/// </summary>
public sealed class SavesCommandHandler(SavesService saves, string helperVersion, string? helperBuild = null, Func<HelperReadiness>? readiness = null)
{
    /// <summary>Наибольшая длина строки запроса в символах (без <c>\n</c>).</summary>
    public const int MaxRequestLength = 4096;

    /// <summary>Команда в поле <c>op</c> (spec п. 4.1): проверка «помощник жив», ответ с версией помощника.</summary>
    public const string PingOp = "ping";

    /// <summary>Команда в поле <c>op</c>: выдать сохранения игрока при входе (<c>playerId</c>, <c>windowsUserSid</c>).</summary>
    public const string AttachOp = "profile.attach";

    /// <summary>Команда в поле <c>op</c>: забрать сохранения игрока в конце сеанса (<c>playerId</c>).</summary>
    public const string DetachOp = "profile.detach";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Выполняет одну команду шелла: <paramref name="requestLine"/> — строка запроса без <c>\n</c>; ответ — тоже.</summary>
    public async Task<string> HandleAsync(string requestLine, CancellationToken ct)
    {
        try
        {
            var (result, version) = await DispatchAsync(requestLine, ct);
            return version is null
                ? Answer(result, version)
                : Answer(result, version, helperBuild, saves.Updating, readiness?.Invoke());
        }
        catch (Exception ex)
        {
            return Answer(SavesResult.Fail(SavesErrors.Internal, ex is OperationCanceledException ? "Command cancelled" : $"{ex.GetType().Name}: {ex.Message}"),
                version: null);
        }
    }

    /// <summary>Итог команды и версия помощника для ответа (только у <c>ping</c>).</summary>
    private async Task<(SavesResult Result, string? Version)> DispatchAsync(string? requestLine, CancellationToken ct)
    {
        if (requestLine is null)
        {
            return BadRequest("Request is empty");
        }

        if (requestLine.Length > MaxRequestLength)
        {
            return BadRequest($"Request line is longer than {MaxRequestLength} characters");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestLine);
        }
        catch (JsonException)
        {
            return BadRequest("Request is not valid JSON");
        }

        using (document)
        {
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object)
            {
                return BadRequest("Request must be a JSON object");
            }

            switch (Text(request, "op"))
            {
                case PingOp:
                    return (SavesResult.Success, helperVersion);
                case AttachOp:
                {
                    var playerId = PlayerIds.Normalize(Text(request, "playerId"));
                    var sid = Text(request, "windowsUserSid");
                    if (playerId is null || string.IsNullOrWhiteSpace(sid))
                    {
                        return BadRequest("profile.attach needs playerId ([A-Za-z0-9-]{1,64}) and windowsUserSid");
                    }

                    return (await OneAtATimeAsync(() => saves.AttachAsync(playerId, sid, ct), ct), null);
                }

                case DetachOp:
                {
                    var playerId = PlayerIds.Normalize(Text(request, "playerId"));
                    if (playerId is null)
                    {
                        return BadRequest("profile.detach needs playerId ([A-Za-z0-9-]{1,64})");
                    }

                    return (await OneAtATimeAsync(() => saves.DetachAsync(playerId, ct), ct), null);
                }

                case null:
                    return BadRequest("Request needs op");
                default:
                    return BadRequest("Unknown op");
            }
        }
    }

    private async Task<SavesResult> OneAtATimeAsync(Func<Task<SavesResult>> command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await command();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Строковое поле запроса; <c>null</c> — поля нет, оно не строка или пустое.</summary>
    private static string? Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static (SavesResult, string?) BadRequest(string message) => (SavesResult.Fail(SavesErrors.BadRequest, message), null);

    /// <summary>
    /// Одна строка JSON без <c>\n</c> (переводы строк в тексте экранируются). <paramref name="version"/>,
    /// <paramref name="build"/>, <paramref name="updating"/> и <paramref name="readiness"/> — только у <c>ping</c>; новые поля —
    /// после прежних, прежние байт в байт как у 1.8.x.
    /// </summary>
    private static string Answer(SavesResult result, string? version, string? build = null, bool updating = false, HelperReadiness? readiness = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", result.Ok);
            if (!result.Ok)
            {
                writer.WriteString("error", result.Error ?? SavesErrors.Internal);
                writer.WriteString("message", result.Message ?? "");
            }

            if (version is not null)
            {
                writer.WriteString("version", version);
            }

            if (build is not null)
            {
                writer.WriteString("build", build);
            }

            if (updating)
            {
                writer.WriteBoolean("updating", true);
            }

            if (readiness is not null)
            {
                writer.WriteBoolean("ready", readiness.Ready);
                writer.WriteStartArray("disks");
                foreach (var disk in readiness.Disks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("library", disk.Library);
                    WriteOptional(writer, "letter", disk.Letter);
                    writer.WriteString("state", disk.State);
                    WriteOptional(writer, "libraryVersion", disk.LibraryVersion);
                    WriteOptional(writer, "error", disk.Error);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Строковое поле, только если значение есть.</summary>
    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
