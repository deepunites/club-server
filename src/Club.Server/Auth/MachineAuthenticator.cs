using Club.Server.Api;
using Club.Server.Data;

namespace Club.Server.Auth;

/// <summary>Машина, от имени которой пришёл запрос; кладётся в <see cref="HttpContext.Items"/>.</summary>
public sealed record MachineContext(MachinePrincipal Principal, MachineRow Machine)
{
    public const string ItemKey = "club.machine";
}

/// <summary>Проверка Bearer-токена помощника бездиска.</summary>
public sealed class MachineAuthenticator(TokenService tokens, MachineRepository machines)
{
    public async Task<MachineContext> AuthenticateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw ApiException.Unauthorized("invalid", "Missing bearer token");
        }

        var (principal, reason) = await tokens.ValidateAsync(token);
        if (principal is null)
        {
            throw ApiException.Unauthorized(reason!, reason == "expired" ? "Access token expired" : "Invalid access token");
        }

        var machine = await machines.FindAsync(principal.MachineId);
        if (machine is null || machine.CredentialsVersion != principal.CredentialsVersion)
        {
            // Удалённая машина или отозванные токены: помощник обновит токен или зарегистрируется заново.
            throw ApiException.Unauthorized("revoked", "Access token revoked");
        }

        return new MachineContext(principal, machine);
    }

    public static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

/// <summary>Аутентификация на <c>/diskless/v1/*</c>, кроме регистрации и refresh.</summary>
public sealed class MachineAuthMiddleware(RequestDelegate next, MachineAuthenticator authenticator)
{
    public const string Prefix = "/diskless/v1";
    private static readonly string[] Anonymous = [Prefix + "/machines/register", Prefix + "/machines/refresh"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith(Prefix + "/", StringComparison.Ordinal) && !Anonymous.Contains(path, StringComparer.Ordinal))
        {
            context.Items[MachineContext.ItemKey] = await authenticator.AuthenticateAsync(MachineAuthenticator.BearerToken(context));
        }

        await next(context);
    }

    public static MachineContext Machine(HttpContext context) =>
        context.Items[MachineContext.ItemKey] as MachineContext ?? throw ApiException.Unauthorized("invalid");
}
