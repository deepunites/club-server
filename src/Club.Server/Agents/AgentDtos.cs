using System.Text.Json;

namespace Club.Server.Agents;

// DTO минимального набора агента. Имена и обязательность — по club-contracts (схемы с тем же именем);
// соответствие схеме проверяют контрактные тесты.

public enum PcStatus
{
    Offline,
    Free,
    Busy,
    Locked,
    Maintenance,
    Booked,
}

public sealed record AgentRegisterRequest(
    string Hwid,
    string MachineName,
    string AgentVersion,
    JsonElement Hardware,
    string IpAddress,
    string MacAddress,
    Guid? PreviousPcId);

public sealed record AgentRegisterResponse(
    Guid PcId,
    Pc Pc,
    string AccessToken,
    string RefreshToken,
    string SigningSecret,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerTime,
    AgentServerConfig Config);

public sealed record AgentRefreshRequest(string RefreshToken, string Hwid);

public sealed record AgentRefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? SigningSecret);

public sealed record Pc(
    Guid Id,
    string Name,
    string Zone,
    int Number,
    string? Hwid,
    string IpAddress,
    PcStatus Status,
    Guid? CurrentSessionId,
    string AgentVersion,
    string ShellVersion,
    DateTimeOffset LastHeartbeatAt);

public sealed record RunningGame(Guid GameId, int Pid, DateTimeOffset StartedAt);

public sealed record HeartbeatRequest(
    PcStatus Status,
    Guid? CurrentSessionId,
    string AgentVersion,
    string ShellVersion,
    long UptimeSec,
    string IpAddress,
    int PolicyVersion,
    IReadOnlyList<JsonElement> RunningGames,
    int OfflineQueue,
    bool ShellConnected);

public sealed record HeartbeatResponse(
    DateTimeOffset ServerTime,
    PcStatus PcStatus,
    int PolicyVersion,
    int ConfigVersion,
    string CatalogVersion,
    int PendingCommands);

/// <summary>
/// Серверные переопределения поверх agent.json. Разделы, которые сервер v1 не заполняет, не отправляются
/// (null не пишется), и агент оставляет свои значения.
/// </summary>
public sealed record AgentServerConfig(
    int Version,
    string PcName,
    string Zone,
    int Number,
    JsonElement? Games,
    JsonElement? Storage,
    UpdatesConfigOverride? Updates,
    ShellConfigOverride? Shell);

public sealed record UpdatesConfigOverride(string? Channel, int? CheckIntervalSec);

public sealed record ShellConfigOverride(IReadOnlyDictionary<string, bool>? Features);

public sealed record ServerCommandEnvelope(
    Guid Id,
    DateTimeOffset Ts,
    string Name,
    JsonElement? Payload,
    string? IssuedBy,
    Guid? Supersedes,
    DateTimeOffset? ExpiresAt);

public sealed record ServerCommandsResponse(IReadOnlyList<ServerCommandEnvelope> Items);

public sealed record CommandAck(bool Ok, JsonElement? Error, JsonElement? Result);
