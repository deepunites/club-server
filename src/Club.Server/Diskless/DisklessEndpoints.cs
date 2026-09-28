using System.Text.RegularExpressions;
using Club.Server.Api;
using Club.Server.Auth;
using Club.Server.Data;
using Club.Server.Library;

namespace Club.Server.Diskless;

public sealed record MachineRegisterRequest(string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string HelperVersion, string? OsVersion);

public sealed record MachineRegisterResponse(
    Guid MachineId, int Number, string Name, string Zone, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime);

public sealed record RefreshRequest(string RefreshToken, string Hwid);

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record VolumeAssignment(string LibraryVersion, string Portal, string TargetIqn, bool ReadOnly, string DriveLetter);

public sealed record MountedVolume(
    string State, string? TargetIqn, string? LibraryVersion, string? DriveLetter, bool? ReadOnlyVerified, string? Error,
    IReadOnlyList<string>? Contents = null);

public sealed record SystemDiskReport(string? Serial, string? Model, long SizeBytes, string BusType);

/// <summary>Secure Boot на ПК: включён; в db — Microsoft UEFI CA 2011 (сторонний) и Windows UEFI CA 2023; в dbx — отозван PCA 2011.</summary>
public sealed record SecureBootReport(bool? Enabled, bool? ThirdPartyCa2011, bool? WindowsCa2023, bool? Pca2011Revoked);

public sealed record MachineStatus(
    string HelperVersion, DateTimeOffset? BootTime, MountedVolume Volume, IReadOnlyList<string>? DhcpServers,
    string? ImageVersion = null, SystemDiskReport? SystemDisk = null, SecureBootReport? SecureBoot = null);

public sealed record StatusAccepted(DateTimeOffset ServerTime, VolumeAssignment? Volume);

/// <summary>
/// API бездиска для помощника на ПК (docs/diskless-api.yaml). Не зависит от шелла: только машины и том библиотеки.
/// Помощник сам опрашивает состояние и сообщает факты; сервер ничего не делает на ПК.
/// </summary>
public static partial class DisklessEndpoints
{
    private static readonly HashSet<string> VolumeStates = ["none", "mounting", "mounted", "switchPending", "failed"];

    [GeneratedRegex("^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")]
    private static partial Regex MacPattern();

    public static void MapDisklessEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(MachineAuthMiddleware.Prefix);
        api.MapPost("/machines/register", RegisterAsync);
        api.MapPost("/machines/refresh", RefreshAsync);
        api.MapGet("/machines/{machineId:guid}/volume", async (Guid machineId, HttpContext context, LibraryRepository library, LibraryOptions options) =>
        {
            RequireSelf(context, machineId);
            return await AssignmentAsync(library, options) is { } volume ? Results.Json(volume, ApiJson.Options) : Results.NoContent();
        });
        api.MapPut("/machines/{machineId:guid}/status", StatusAsync);
    }

    /// <summary>Текущая опубликованная версия библиотеки. Том всегда read-only: он общий для всех ПК.</summary>
    public static async Task<VolumeAssignment?> AssignmentAsync(LibraryRepository library, LibraryOptions options)
    {
        if (!options.Enabled || (await library.PointersAsync()).Current is not { TargetIqn: { } iqn } current)
        {
            return null;
        }

        return new VolumeAssignment(current.Label, options.PortalAddress, iqn, ReadOnly: true, options.DriveLetter);
    }

    private static async Task<IResult> RegisterAsync(HttpContext context, MachineRegisterRequest request, MachineRepository machines, TokenService tokens, AuthOptions options, TimeProvider clock)
    {
        var clubKey = context.Request.Headers["X-Club-Key"].ToString();
        if (string.IsNullOrEmpty(options.ClubApiKey) || string.IsNullOrEmpty(clubKey) || !Secrets.FixedTimeEquals(clubKey, options.ClubApiKey))
        {
            throw ApiException.Unauthorized("clubKey", "Invalid club key");
        }

        Require(request.Hwid, "hwid");
        Require(request.Hostname, "hostname");
        Require(request.HelperVersion, "helperVersion");
        var macs = (request.MacAddresses ?? []).Select(m => m.Trim().ToLowerInvariant()).Distinct().ToList();
        if (macs.Count == 0 || macs.Any(m => !MacPattern().IsMatch(m)))
        {
            throw ApiException.Validation("macAddresses", "format");
        }

        var registration = new MachineRegistration(request.Hwid, request.Hostname, macs, ClientIp(context), request.HelperVersion, request.OsVersion);
        var existing = await machines.FindByHwidAsync(request.Hwid);
        var machine = existing is null
            ? await machines.CreateAsync(registration, options.AutoApprovePcs)
            : await machines.ReregisterAsync(existing.Id, registration);

        if (!machine.Approved)
        {
            // Машина в реестре в режиме обслуживания; администратор одобряет её в панели.
            throw ApiException.Forbidden("pendingApproval", "Machine is waiting for administrator approval");
        }

        var (access, expiresAt) = tokens.IssueAccessToken(machine.Id, machine.Hwid, machine.CredentialsVersion);
        var refresh = await IssueRefreshTokenAsync(machines, machine, options, clock);
        return Results.Json(
            new MachineRegisterResponse(machine.Id, machine.Number, machine.Name, machine.ZoneId, access, refresh, expiresAt, clock.GetUtcNow()),
            ApiJson.Options);
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, MachineRepository machines, TokenService tokens, AuthOptions options, TimeProvider clock)
    {
        Require(request.RefreshToken, "refreshToken");
        Require(request.Hwid, "hwid");
        var now = clock.GetUtcNow();
        var (row, alreadyUsed) = await machines.ConsumeRefreshTokenAsync(Secrets.HashToken(request.RefreshToken), now);
        if (row is null)
        {
            throw ApiException.Unauthorized("revoked", "Unknown refresh token");
        }

        if (alreadyUsed)
        {
            // Refresh одноразовый: повтор означает, что токен мог утечь. Отзываем всё, помощник регистрируется заново.
            await machines.RevokeCredentialsAsync(row.MachineId);
            throw ApiException.Unauthorized("reused", "Refresh token already used");
        }

        if (row.ExpiresAt <= now)
        {
            throw ApiException.Unauthorized("expired", "Refresh token expired");
        }

        var machine = await machines.FindAsync(row.MachineId);
        if (machine is null || machine.CredentialsVersion != row.CredentialsVersion || !Secrets.FixedTimeEquals(machine.Hwid, request.Hwid))
        {
            throw ApiException.Unauthorized("revoked", "Refresh token revoked");
        }

        var (access, expiresAt) = tokens.IssueAccessToken(machine.Id, machine.Hwid, machine.CredentialsVersion);
        var refresh = await IssueRefreshTokenAsync(machines, machine, options, clock);
        return Results.Json(new RefreshResponse(access, refresh, expiresAt), ApiJson.Options);
    }

    private static async Task<IResult> StatusAsync(Guid machineId, MachineStatus request, HttpContext context, MachineRepository machines, LibraryRepository library, LibraryOptions options, Imaging.ImageRepository images, TimeProvider clock)
    {
        RequireSelf(context, machineId);
        Require(request.HelperVersion, "helperVersion");
        if (request.Volume is null || !VolumeStates.Contains(request.Volume.State))
        {
            throw ApiException.Validation("volume.state", "unknown");
        }

        var v = request.Volume;
        var dhcp = request.DhcpServers?.Where(d => Network.IpPlan.TryParseIp(d, out _)).Distinct().Take(8).ToList();
        await machines.RecordStatusAsync(
            machineId, request.HelperVersion, ClientIp(context), request.BootTime,
            new VolumeReport(v.State, v.TargetIqn, v.LibraryVersion, v.ReadOnlyVerified, Truncate(v.Error, 2000)), dhcp, clock.GetUtcNow(),
            ImageVersionOrNull(request.ImageVersion), SystemDiskJson(request.SystemDisk),
            request.SecureBoot is { } sb ? System.Text.Json.JsonSerializer.Serialize(sb, System.Text.Json.JsonSerializerOptions.Web) : null);
        if (v is { State: "mounted", ReadOnlyVerified: true, LibraryVersion: { } mountedVersion, Contents: { } contents })
        {
            await library.SetContentsAsync(mountedVersion, CleanFolders(contents), clock.GetUtcNow());
        }

        if (ImageVersionOrNull(request.ImageVersion) is { } imageVersion)
        {
            // Windows после перезаливки вышла на связь с нужной версией образа — задание перезаливки выполнено.
            await images.CompleteBootedAsync(machineId, imageVersion, clock.GetUtcNow());
        }

        return Results.Json(new StatusAccepted(clock.GetUtcNow(), await AssignmentAsync(library, options)), ApiJson.Options);
    }

    private static async Task<string> IssueRefreshTokenAsync(MachineRepository machines, MachineRow machine, AuthOptions options, TimeProvider clock)
    {
        var refresh = Secrets.NewRefreshToken();
        await machines.StoreRefreshTokenAsync(Secrets.HashToken(refresh), machine.Id, machine.CredentialsVersion, clock.GetUtcNow().AddDays(options.RefreshTokenDays));
        return refresh;
    }

    private static MachineContext RequireSelf(HttpContext context, Guid machineId)
    {
        var machine = MachineAuthMiddleware.Machine(context);
        if (machine.Machine.Id != machineId)
        {
            throw ApiException.Forbidden("otherMachine", "Token belongs to another machine");
        }

        return machine;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ApiException.Validation(field, "required");
        }
    }

    private static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "";

    /// <summary>Имена папок с ПК — только правдоподобные: без управляющих символов, до 255 символов, не больше 1000.</summary>
    private static List<string> CleanFolders(IReadOnlyList<string> folders) =>
        folders.Select(f => f?.Trim() ?? "")
            .Where(f => f.Length is > 0 and <= 255 && !f.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList();

    private static string? ImageVersionOrNull(string? value) =>
        value is not null && Imaging.ImageLibrary.LabelPattern().IsMatch(value) ? value : null;

    /// <summary>Системный диск — только с правдоподобными значениями (иначе WinPE выбрал бы диск по мусору).</summary>
    private static string? SystemDiskJson(SystemDiskReport? disk) =>
        disk is { SizeBytes: > 0 } && !string.IsNullOrWhiteSpace(disk.BusType) && (disk.Serial?.Length ?? 0) <= 128
            ? System.Text.Json.JsonSerializer.Serialize(
                new Imaging.SystemDisk(disk.Serial?.Trim(), Truncate(disk.Model, 128), disk.SizeBytes, Truncate(disk.BusType, 32)!),
                System.Text.Json.JsonSerializerOptions.Web)
            : null;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
