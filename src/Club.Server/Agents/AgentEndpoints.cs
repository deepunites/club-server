using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Agents;

/// <summary>Обязательный минимум агента: регистрация, refresh, heartbeat, телеметрия, конфиг, политики, команды.</summary>
public static class AgentEndpoints
{
    /// <summary>
    /// Флаги шелла на сервере v1. Выключено всё, чьи эндпоинты не реализованы: у агента отсутствующий флаг = true
    /// (club-contracts/docs/SHELL_CHANGES.md п. 4), поэтому отправляем полный набор.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, bool> ShellFeatures = new Dictionary<string, bool>
    {
        ["shop"] = false,
        ["chat"] = false,
        ["booking"] = false,
        ["tournaments"] = false,
        ["profile"] = true,
        ["topup"] = false,
        ["apps"] = false,
        ["callAdmin"] = false,
    };

    /// <summary>Операции, которые сервер реализует явно; для остальных операций контракта — 501 (<see cref="ContractStatus"/>).</summary>
    public static readonly string[] Implemented =
    [
        "POST /agents/register",
        "POST /agents/refresh",
        "POST /agents/{pcId}/heartbeat",
        "POST /agents/{pcId}/telemetry",
        "GET /agents/{pcId}/config",
        "GET /agents/{pcId}/policies",
        "GET /agents/{pcId}/commands",
        "POST /agents/{pcId}/commands/{commandId}/ack",
        "GET /pcs/{pcId}",
        "GET /updates/{channel}/manifest",
    ];

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(AgentAuthMiddleware.ApiPrefix);

        api.MapPost("/agents/register", RegisterAsync);
        api.MapPost("/agents/refresh", RefreshAsync);
        api.MapPost("/agents/{pcId:guid}/heartbeat", HeartbeatAsync);
        api.MapPost("/agents/{pcId:guid}/telemetry", (Guid pcId, HttpContext context) =>
        {
            RequireSelf(context, pcId);
            // Телеметрия принимается, но пока не хранится: проверки на вменяемость и хранилище — отдельная задача.
            return Results.NoContent();
        });
        api.MapGet("/agents/{pcId:guid}/config", ConfigAsync);
        api.MapGet("/agents/{pcId:guid}/policies", PoliciesAsync);
        api.MapGet("/agents/{pcId:guid}/commands", async (Guid pcId, HttpContext context, CommandRepository commands, TimeProvider clock) =>
        {
            RequireSelf(context, pcId);
            return Results.Json(new ServerCommandsResponse(await commands.PendingAsync(pcId, clock.GetUtcNow())), ApiJson.Options);
        });
        api.MapPost("/agents/{pcId:guid}/commands/{commandId:guid}/ack", async (Guid pcId, Guid commandId, CommandAck ack, HttpContext context, CommandRepository commands, TimeProvider clock) =>
        {
            RequireSelf(context, pcId);
            if (!await commands.AckAsync(pcId, commandId, ack, clock.GetUtcNow()))
            {
                throw ApiException.NotFound("command");
            }

            return Results.NoContent();
        });
        api.MapGet("/pcs/{pcId:guid}", async (Guid pcId, PcRepository pcs) =>
        {
            var pc = await pcs.FindAsync(pcId) ?? throw ApiException.NotFound("pc");
            var zone = await pcs.GetZoneAsync(pc.ZoneId);
            var view = ToPc(pc, zone);
            return Results.Json(view with { Hwid = null }, ApiJson.Options);
        });

        // Обновления сервер v1 не раздаёт. 204 = «обновлений нет»: 501 агент повторяет и засчитывает в общий
        // circuit breaker (SHELL_CHANGES.md п. 1), а проверку обновлений он делает сам по таймеру.
        api.MapGet("/updates/{channel}/manifest", () => Results.NoContent());
    }

    private static async Task<IResult> RegisterAsync(HttpContext context, AgentRegisterRequest request, PcRepository pcs, TokenService tokens, AuthOptions options, LibraryRepository library, LibraryOptions libraryOptions, TimeProvider clock)
    {
        var clubKey = context.Request.Headers["X-Club-Key"].ToString();
        if (string.IsNullOrEmpty(options.ClubApiKey) || string.IsNullOrEmpty(clubKey) || !Secrets.FixedTimeEquals(clubKey, options.ClubApiKey))
        {
            throw ApiException.Unauthorized("clubKey", "Invalid club key");
        }

        Require(request.Hwid, "hwid");
        Require(request.MachineName, "machineName");
        Require(request.AgentVersion, "agentVersion");
        Require(request.MacAddress, "macAddress");
        Require(request.IpAddress, "ipAddress");
        if (request.Hardware.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("hardware", "required");
        }

        var secret = Secrets.Random(32);
        var existing = await pcs.FindByHwidAsync(request.Hwid);
        var pc = existing is null
            ? await pcs.CreateAsync(request, options.AutoApprovePcs, secret)
            : await pcs.ReissueCredentialsAsync(existing.Id, request, secret);

        if (!pc.Approved)
        {
            // ПК заведён в реестр в режиме обслуживания; администратор одобряет его в панели.
            throw ApiException.Forbidden("pendingApproval", "PC is waiting for administrator approval");
        }

        var (access, expiresAt) = tokens.IssueAccessToken(pc.Id, pc.Hwid, pc.CredentialsVersion);
        var refresh = await IssueRefreshTokenAsync(pcs, pc, options, clock);
        var zone = await pcs.GetZoneAsync(pc.ZoneId);
        return Results.Json(
            new AgentRegisterResponse(pc.Id, ToPc(pc, zone), access, refresh, Convert.ToBase64String(pc.SigningSecret), expiresAt, clock.GetUtcNow(), BuildConfig(pc, zone, await GamesShareAsync(library, libraryOptions))),
            ApiJson.Options);
    }

    private static async Task<IResult> RefreshAsync(AgentRefreshRequest request, PcRepository pcs, TokenService tokens, AuthOptions options, TimeProvider clock)
    {
        Require(request.RefreshToken, "refreshToken");
        Require(request.Hwid, "hwid");
        var now = clock.GetUtcNow();
        var (row, alreadyUsed) = await pcs.ConsumeRefreshTokenAsync(Secrets.HashToken(request.RefreshToken), now);
        if (row is null)
        {
            throw ApiException.Unauthorized("revoked", "Unknown refresh token");
        }

        if (alreadyUsed)
        {
            // Refresh одноразовый: повтор означает, что токен мог утечь. Отзываем всё, агент регистрируется заново.
            await pcs.RevokeCredentialsAsync(row.PcId);
            throw ApiException.Unauthorized("reused", "Refresh token already used");
        }

        if (row.ExpiresAt <= now)
        {
            throw ApiException.Unauthorized("expired", "Refresh token expired");
        }

        var pc = await pcs.FindAsync(row.PcId);
        if (pc is null || pc.CredentialsVersion != row.CredentialsVersion || !Secrets.FixedTimeEquals(pc.Hwid, request.Hwid))
        {
            throw ApiException.Unauthorized("revoked", "Refresh token revoked");
        }

        var (access, expiresAt) = tokens.IssueAccessToken(pc.Id, pc.Hwid, pc.CredentialsVersion);
        var refresh = await IssueRefreshTokenAsync(pcs, pc, options, clock);
        return Results.Json(new AgentRefreshResponse(access, refresh, expiresAt, SigningSecret: null), ApiJson.Options);
    }

    private static async Task<IResult> HeartbeatAsync(Guid pcId, HeartbeatRequest request, HttpContext context, PcRepository pcs, CommandRepository commands, TimeProvider clock)
    {
        var agent = RequireSelf(context, pcId);
        var now = clock.GetUtcNow();
        await pcs.RecordHeartbeatAsync(pcId, request, now);
        var zone = await pcs.GetZoneAsync(agent.Pc.ZoneId);
        return Results.Json(
            new HeartbeatResponse(
                ServerTime: now,
                PcStatus: agent.Pc.Maintenance ? PcStatus.Maintenance : request.Status,
                PolicyVersion: zone.PolicyVersion,
                ConfigVersion: ConfigVersion(agent.Pc, zone),
                CatalogVersion: "0",
                PendingCommands: await commands.PendingCountAsync(pcId, now)),
            ApiJson.Options);
    }

    private static async Task<IResult> ConfigAsync(Guid pcId, HttpContext context, PcRepository pcs, LibraryRepository library, LibraryOptions libraryOptions)
    {
        var agent = RequireSelf(context, pcId);
        var zone = await pcs.GetZoneAsync(agent.Pc.ZoneId);
        var config = BuildConfig(agent.Pc, zone, await GamesShareAsync(library, libraryOptions));
        return WithETag(context, config.Version.ToString(CultureInfo.InvariantCulture), () => Results.Json(config, ApiJson.Options));
    }

    private static async Task<IResult> PoliciesAsync(Guid pcId, HttpContext context, PcRepository pcs)
    {
        var agent = RequireSelf(context, pcId);
        var zone = await pcs.GetZoneAsync(agent.Pc.ZoneId);
        return WithETag(context, $"{zone.Id}-{zone.PolicyVersion}", () =>
        {
            var policy = JsonNode.Parse(zone.Policy)!.AsObject();
            policy["version"] = zone.PolicyVersion;
            policy["updatedAt"] = ApiJson.FormatTime(zone.PolicyUpdatedAt);
            return Results.Text(policy.ToJsonString(), "application/json; charset=utf-8");
        });
    }

    /// <summary>Версия конфига ПК: меняется и при правке ПК, и при правке его зоны.</summary>
    private static int ConfigVersion(PcRow pc, ZoneRow zone) => pc.ConfigVersion * 100_000 + zone.ConfigVersion;

    /// <summary>
    /// Игровой том для агента: текущая опубликованная версия библиотеки. Только iSCSI и всегда readOnly —
    /// по умолчанию агент монтирует SMB и на запись (club-contracts/docs/SHELL_CHANGES.md п. 7).
    /// </summary>
    private static async Task<JsonElement?> GamesShareAsync(LibraryRepository library, LibraryOptions options)
    {
        if (!options.Enabled || (await library.PointersAsync()).Current is not { TargetIqn: { } iqn })
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            gamesShare = new
            {
                enabled = true,
                driveLetter = options.DriveLetter,
                iscsi = new { portal = options.PortalAddress, targetIqn = iqn, readOnly = true },
            },
        }, ApiJson.Options);
    }

    private static AgentServerConfig BuildConfig(PcRow pc, ZoneRow zone, JsonElement? gamesShare) => new(
        Version: ConfigVersion(pc, zone),
        PcName: pc.Name,
        Zone: zone.Id,
        Number: pc.Number,
        // Пул аккаунтов и облачные сейвы — открытый вопрос Q8; у агента они включены по умолчанию (SHELL_CHANGES.md п. 5).
        Games: JsonSerializer.SerializeToElement(new { accountPool = new { enabled = false }, cloudSave = new { enabled = false } }, ApiJson.Options),
        // Игровой том — текущая версия библиотеки; пока её нет, раздел не отправляется и агент ничего не монтирует.
        Storage: gamesShare ?? (zone.Storage is null ? null : JsonDocument.Parse(zone.Storage).RootElement.Clone()),
        Updates: new UpdatesConfigOverride(Channel: null, CheckIntervalSec: 86_400),
        Shell: new ShellConfigOverride(ShellFeatures));

    private static Pc ToPc(PcRow pc, ZoneRow zone) => new(
        pc.Id, pc.Name, zone.Id, pc.Number, pc.Hwid, pc.IpAddress,
        pc.Maintenance ? PcStatus.Maintenance : ParseStatus(pc.ReportedStatus),
        CurrentSessionId: null, pc.AgentVersion, pc.ShellVersion, pc.LastHeartbeatAt ?? pc.CreatedAt);

    private static PcStatus ParseStatus(string value) =>
        Enum.TryParse<PcStatus>(value, ignoreCase: true, out var status) ? status : PcStatus.Offline;

    private static async Task<string> IssueRefreshTokenAsync(PcRepository pcs, PcRow pc, AuthOptions options, TimeProvider clock)
    {
        var refresh = Secrets.NewRefreshToken();
        await pcs.StoreRefreshTokenAsync(Secrets.HashToken(refresh), pc.Id, pc.CredentialsVersion, clock.GetUtcNow().AddDays(options.RefreshTokenDays));
        return refresh;
    }

    private static AgentContext RequireSelf(HttpContext context, Guid pcId)
    {
        var agent = AgentAuthMiddleware.Agent(context);
        if (agent.Pc.Id != pcId)
        {
            throw ApiException.Forbidden("otherPc", "Token belongs to another PC");
        }

        return agent;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ApiException.Validation(field, "required");
        }
    }

    private static IResult WithETag(HttpContext context, string tag, Func<IResult> body)
    {
        var etag = $"\"{tag}\"";
        context.Response.Headers.ETag = etag;
        return context.Request.Headers.IfNoneMatch.ToString() == etag ? Results.StatusCode(StatusCodes.Status304NotModified) : body();
    }
}
