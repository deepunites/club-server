using Club.Server.Api;
using Club.Server.Data;

namespace Club.Server.Auth;

/// <summary>Проверка токена агента: общая для REST (<see cref="AgentAuthMiddleware"/>) и WebSocket.</summary>
public sealed class AgentAuthenticator(TokenService tokens, PcRepository pcs)
{
    public async Task<AgentContext> AuthenticateAsync(string? token)
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

        var pc = await pcs.FindAsync(principal.PcId);
        if (pc is null || pc.CredentialsVersion != principal.CredentialsVersion)
        {
            // Удалённый ПК или отозванные токены: агент обновит токен или зарегистрируется заново.
            throw ApiException.Unauthorized("revoked", "Access token revoked");
        }

        return new AgentContext(principal, pc);
    }

    public static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}
