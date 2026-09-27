using System.Text.Json;

namespace Club.Server.Api;

/// <summary>Значения <c>ErrorCode</c> из контракта. Других агент не разберёт (enum без запасного значения).</summary>
public static class ErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "notFound";
    public const string Validation = "validation";
    public const string Conflict = "conflict";
    public const string ServerUnavailable = "serverUnavailable";
    public const string RateLimited = "rateLimited";
    public const string Internal = "internal";
}

public sealed record ServerError(string Code, string Message, object? Details, string TraceId);

public sealed record ServerErrorEnvelope(ServerError Error);

/// <summary>Ошибка API: превращается в конверт <c>{ error: { code, message, details, traceId } }</c>.</summary>
public sealed class ApiException(int status, string code, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Details { get; } = details;
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public static ApiException Unauthorized(string reason, string message = "Unauthorized") =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, message, new { reason });

    public static ApiException Forbidden(string reason, string message = "Forbidden") =>
        new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, message, new { reason });

    public static ApiException Validation(string field, string reason) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.Validation, $"Invalid {field}", new { field, reason });

    public static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{what} not found", new { what });
}

public static class ApiErrorWriter
{
    public const string TraceHeader = "X-Trace-Id";

    public static string TraceId(HttpContext context)
    {
        if (context.Items.TryGetValue(TraceHeader, out var existing) && existing is string id)
        {
            return id;
        }

        var header = context.Request.Headers[TraceHeader].ToString();
        id = Guid.TryParse(header, out var parsed) ? parsed.ToString() : Guid.NewGuid().ToString();
        context.Items[TraceHeader] = id;
        return id;
    }

    public static async Task WriteAsync(HttpContext context, int status, string code, string message, object? details)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var envelope = new ServerErrorEnvelope(new ServerError(code, message, details, TraceId(context)));
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, ApiJson.Options, context.RequestAborted);
    }
}

/// <summary>Трассировка, <c>X-Server-Time</c> на каждом ответе и перевод исключений в конверт ошибки.</summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger, TimeProvider clock)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = ApiErrorWriter.TraceId(context);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiErrorWriter.TraceHeader] = traceId;
            context.Response.Headers["X-Server-Time"] = ApiJson.FormatTime(clock.GetUtcNow());
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        catch (ApiException ex) when (!context.Response.HasStarted)
        {
            foreach (var (name, value) in ex.Headers)
            {
                context.Response.Headers[name] = value;
            }

            await ApiErrorWriter.WriteAsync(context, ex.Status, ex.Code, ex.Message, ex.Details);
        }
        catch (JsonException ex) when (!context.Response.HasStarted)
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Malformed JSON body", new { field = ex.Path ?? "body", reason = "json" });
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await ApiErrorWriter.WriteAsync(context, ex.StatusCode, ErrorCodes.Validation, ex.Message, new { field = "body", reason = "request" });
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error, trace {TraceId}", traceId);
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status500InternalServerError, ErrorCodes.Internal, "Internal server error", null);
        }
    }
}
