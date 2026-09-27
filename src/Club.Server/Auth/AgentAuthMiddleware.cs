using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Club.Server.Api;
using Club.Server.Data;
using Microsoft.AspNetCore.Http.Features;

namespace Club.Server.Auth;

/// <summary>ПК, от имени которого пришёл запрос; кладётся в <see cref="HttpContext.Items"/>.</summary>
public sealed record AgentContext(AgentPrincipal Principal, PcRow Pc)
{
    public const string ItemKey = "club.agent";
}

/// <summary>
/// Аутентификация агента на <c>/api/v1/*</c>: Bearer JWT и подпись
/// <c>hex(HMAC-SHA256(signingSecret, X-Timestamp + METHOD + PATH + hex(SHA256(body))))</c>, где PATH — путь с
/// <c>/api/v1</c> и query ровно как отправлен (raw target). Окно ±300 с, <c>401 clockSkew</c> с <c>X-Server-Time</c>,
/// защита от повторов изменяющих запросов — <see cref="ReplayGuard"/>.
/// </summary>
public sealed class AgentAuthMiddleware(RequestDelegate next, AgentAuthenticator authenticator, ReplayGuard replay, AuthOptions options, TimeProvider clock)
{
    public const string ApiPrefix = "/api/v1";

    /// <summary>Маршруты без токена агента: регистрация (ключ клуба), обновление токена (тело), касса (свой токен).</summary>
    private static readonly string[] Anonymous = [ApiPrefix + "/agents/register", ApiPrefix + "/agents/refresh"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(ApiPrefix + "/", StringComparison.Ordinal)
            || Anonymous.Contains(path, StringComparer.Ordinal)
            || path.StartsWith(ApiPrefix + "/admin/", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var agent = await authenticator.AuthenticateAsync(AgentAuthenticator.BearerToken(context));
        context.Items[AgentContext.ItemKey] = agent;

        var (key, window) = await VerifySignatureAsync(context, agent);
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            // Чтение не меняет состояние, а X-Timestamp с точностью до секунды: два одинаковых GET за секунду
            // дают одну и ту же тройку. Для чтения достаточно подписи и окна ±300 с.
            await next(context);
            return;
        }

        if (!replay.TryBegin(key, window))
        {
            throw ApiException.Unauthorized("replay", "Request already processed");
        }

        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        catch (ApiException ex)
        {
            status = ex.Status;
            throw;
        }
        finally
        {
            replay.Complete(key, status);
        }
    }

    public static AgentContext Agent(HttpContext context) =>
        context.Items[AgentContext.ItemKey] as AgentContext ?? throw ApiException.Unauthorized("invalid");

    private async Task<(string Key, TimeSpan Window)> VerifySignatureAsync(HttpContext context, AgentContext agent)
    {
        var timestampHeader = context.Request.Headers["X-Timestamp"].ToString();
        var signature = context.Request.Headers["X-Signature"].ToString();
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || signature.Length != 64)
        {
            throw ApiException.Unauthorized("signature", "Missing or malformed request signature");
        }

        var window = TimeSpan.FromSeconds(options.SignatureWindowSec);
        var now = clock.GetUtcNow();
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > window.TotalSeconds)
        {
            var ex = ApiException.Unauthorized("clockSkew", "Request timestamp outside the allowed window");
            ex.Headers["X-Server-Time"] = ApiJson.FormatTime(now);
            throw ex;
        }

        context.Request.EnableBuffering();
        byte[] bodyHash;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }

            bodyHash = sha.GetHashAndReset();
        }

        context.Request.Body.Position = 0;

        var rawTarget = RequestTarget(context);
        var message = timestampHeader + context.Request.Method.ToUpperInvariant() + rawTarget + Convert.ToHexStringLower(bodyHash);
        var expected = HMACSHA256.HashData(agent.Pc.SigningSecret, Encoding.UTF8.GetBytes(message));

        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            throw ApiException.Unauthorized("signature", "Malformed request signature");
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw ApiException.Unauthorized("signature", "Request signature mismatch");
        }

        return (ReplayGuard.Key(agent.Pc.Id, timestampHeader, signature), window);
    }

    /// <summary>
    /// PATH для подписи — request-target ровно как пришёл, без нормализации. Kestrel отдаёт его в RawTarget;
    /// absolute-form (через прокси) сводится к пути с query; если транспорт RawTarget не заполняет — собираем из запроса.
    /// </summary>
    private static string RequestTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
        {
            return context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        }

        if (!raw.StartsWith('/') && Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            var authority = absolute.GetLeftPart(UriPartial.Authority);
            return raw.StartsWith(authority, StringComparison.OrdinalIgnoreCase) ? raw[authority.Length..] : absolute.PathAndQuery;
        }

        return raw;
    }
}
