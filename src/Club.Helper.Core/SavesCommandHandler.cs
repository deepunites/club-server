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
/// </summary>
public sealed class SavesCommandHandler(SavesService saves, string helperVersion)
{
    /// <summary>Наибольшая длина строки запроса в символах (без <c>\n</c>).</summary>
    public const int MaxRequestLength = 4096;

    /// <summary>Имена команд в поле <c>op</c> (spec п. 4.1).</summary>
    public const string PingOp = "ping";

    public const string AttachOp = "profile.attach";

    public const string DetachOp = "profile.detach";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Выполняет одну команду шелла: <paramref name="requestLine"/> — строка запроса без <c>\n</c>; ответ — тоже.</summary>
    public async Task<string> HandleAsync(string requestLine, CancellationToken ct)
    {
        try
        {
            var (result, version) = await DispatchAsync(requestLine, ct);
            return Answer(result, version);
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

    /// <summary>Одна строка JSON без <c>\n</c> (переводы строк в тексте экранируются).</summary>
    private static string Answer(SavesResult result, string? version)
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

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
