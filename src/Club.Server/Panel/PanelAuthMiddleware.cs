using Club.Server.Api;
using Club.Server.Auth;

namespace Club.Server.Panel;

public sealed class PanelOptions
{
    /// <summary>
    /// ВРЕМЕННО: один токен администратора панели (Bearer). Пустой — панель закрыта. Заменить входом владельца,
    /// когда появятся учётные записи персонала (бэкенд кассы /admin/*).
    /// </summary>
    public string AdminToken { get; set; } = "";
}

/// <summary>Доступ к <c>/panel/api/*</c> только с токеном администратора; сравнение в постоянном времени.</summary>
public sealed class PanelAuthMiddleware(RequestDelegate next, PanelOptions options)
{
    public const string Prefix = "/panel/api";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Prefix))
        {
            await next(context);
            return;
        }

        var token = MachineAuthenticator.BearerToken(context);
        if (string.IsNullOrEmpty(options.AdminToken) || string.IsNullOrEmpty(token) || !Secrets.FixedTimeEquals(token, options.AdminToken))
        {
            throw ApiException.Unauthorized("panelToken", "Panel access token required");
        }

        await next(context);
    }
}
