using System.Text.Json;

namespace Club.TrueNas;

/// <summary>Одна ошибка валидации из <c>error.data.extra</c>: <c>[attribute, errmsg, errno]</c>.</summary>
public sealed record TrueNasValidationItem(string? Attribute, string Message, int Errno);

/// <summary>
/// Ошибка JSON-RPC от middleware. Решения принимаются по коду и errno, а не по тексту: тексты различаются между
/// методами и версиями (docs/research/truenas-api.md §4, §7.6).
/// </summary>
public sealed class TrueNasRpcException(string method, int code, string message, int? errno, string? errname, string? reason, IReadOnlyList<TrueNasValidationItem> validation)
    : Exception($"{method}: {code} {reason ?? message}")
{
    public const int InvalidParams = -32602;
    public const int MethodCallError = -32001;
    public const int MethodNotFound = -32601;
    public const int TooManyConcurrentCalls = -32000;
    public const int ErrnoNotFound = 2;
    public const int ErrnoNoMethod = 201;

    public string Method { get; } = method;
    public int Code { get; } = code;
    public int? Errno { get; } = errno;
    public string? Errname { get; } = errname;
    public string? Reason { get; } = reason;
    public IReadOnlyList<TrueNasValidationItem> Validation { get; } = validation;

    /// <summary>«Не найдено»: у -32602 data.error всегда 22, ENOENT лежит в extra[i][2].</summary>
    public bool IsNotFound => Code == InvalidParams && Validation.Any(v => v.Errno == ErrnoNotFound);

    /// <summary>Метода нет в этой версии API (-32601 или -32001 с ENOMETHOD при закреплённой старой версии).</summary>
    public bool IsMethodMissing => Code == MethodNotFound || (Code == MethodCallError && Errno == ErrnoNoMethod);

    internal static TrueNasRpcException From(string method, JsonElement error)
    {
        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        int? errno = null;
        string? errname = null, reason = null;
        var validation = new List<TrueNasValidationItem>();
        if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Number)
            {
                errno = e.GetInt32();
            }

            errname = data.TryGetProperty("errname", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() : null;
            reason = data.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (data.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in extra.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 3)
                    {
                        validation.Add(new TrueNasValidationItem(
                            item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null,
                            item[1].ValueKind == JsonValueKind.String ? item[1].GetString() ?? "" : item[1].ToString(),
                            item[2].ValueKind == JsonValueKind.Number ? item[2].GetInt32() : 0));
                    }
                }
            }
        }

        return new TrueNasRpcException(method, code, message, errno, errname, reason, validation);
    }
}

/// <summary>TrueNAS недоступен: нет связи, TLS не прошёл, версия API не из белого списка, логин отклонён.</summary>
public sealed class TrueNasUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
