using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Club.Helper.Core;

// DTO по docs/diskless-api.yaml.
/// <summary>
/// Назначение тома игр. Общий том версии — read-only, без CHAP. Личный диск места (сервер с Library:PersonalGames,
/// помощник 1.5+) — таргет <c>games-seat-NN</c>, на запись, с CHAP.
/// </summary>
public sealed record VolumeAssignment(
    string LibraryVersion, string Portal, string TargetIqn, bool ReadOnly, string DriveLetter, string? ChapUser = null, string? ChapSecret = null);

public sealed record MountedVolume(
    string State, string? TargetIqn = null, string? LibraryVersion = null, string? DriveLetter = null, bool? ReadOnlyVerified = null, string? Error = null,
    IReadOnlyList<string>? Contents = null);

public sealed record MachineStatus(
    string HelperVersion, DateTimeOffset? BootTime, MountedVolume Volume, IReadOnlyList<string>? DhcpServers = null,
    string? ImageVersion = null, SystemDiskFacts? SystemDisk = null, SecureBootFacts? SecureBoot = null,
    string? InitiatorIqn = null, MasterReport? Master = null);

public sealed record MasterAssignment(string TargetIqn, string Portal, string ChapUser, string ChapSecret, string DriveLetter);

public sealed record MasterReport(string State, string? TargetIqn = null, string? DriveLetter = null, string? Error = null);

public sealed record StatusAccepted(DateTimeOffset ServerTime, VolumeAssignment? Volume, MasterAssignment? Master = null);

internal sealed record RegisterRequest(string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string HelperVersion, string? OsVersion);

internal sealed record RegisterResponse(Guid MachineId, int Number, string Name, string Zone, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime);

internal sealed record RefreshRequest(string RefreshToken, string Hwid);

internal sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Сервер недоступен или ответил ошибкой: том не трогаем, повторим на следующем такте.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Машина в реестре, но ждёт одобрения администратора.</summary>
public sealed class PendingApprovalException() : Exception("Machine is waiting for administrator approval");

/// <summary>
/// Клиент API бездиска. Держит токены машины: при 401 обновляет их refresh-токеном, при отказе refresh
/// регистрируется заново по ключу клуба (номер места сохраняется — сервер узнаёт машину по HWID).
/// </summary>
public sealed class DisklessApiClient(HttpClient http, HelperOptions options, ICredentialStore store, IMachineIdentity identity)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private StoredCredentials? _credentials;

    public Guid? MachineId => _credentials?.MachineId;

    /// <summary>
    /// Назначение при старте помощника. <paramref name="attached"/> — подключён ли на ПК личный диск игр (<c>null</c> —
    /// неизвестно): <c>false</c> разрешает серверу сбросить диск, даже если на нём висит сессия прошлой загрузки.
    /// </summary>
    public async Task<VolumeAssignment?> GetVolumeAsync(bool? attached, CancellationToken ct)
    {
        var query = attached is { } a ? $"?attached={(a ? "true" : "false")}" : "";
        using var response = await SendAuthorizedAsync(id => new HttpRequestMessage(HttpMethod.Get, $"diskless/v1/machines/{id}/volume{query}"), ct);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await ReadAsync<VolumeAssignment>(response, ct);
    }

    public async Task<StatusAccepted> ReportStatusAsync(MachineStatus status, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(
            id => new HttpRequestMessage(HttpMethod.Put, $"diskless/v1/machines/{id}/status") { Content = JsonContent.Create(status, options: Json) },
            ct);
        return await ReadAsync<StatusAccepted>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<Guid, HttpRequestMessage> build, CancellationToken ct)
    {
        await EnsureCredentialsAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build(_credentials!.MachineId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.AccessToken);
            var response = await SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    response.Dispose();
                    throw new ServerUnavailableException($"{request.Method} {request.RequestUri}: {(int)response.StatusCode} {text}");
                }

                return response;
            }

            response.Dispose();
            await RenewAsync(ct);
        }
    }

    private async Task EnsureCredentialsAsync(CancellationToken ct)
    {
        _credentials ??= await store.LoadAsync(ct);
        if (_credentials is null)
        {
            await RegisterAsync(ct);
        }
    }

    /// <summary>Refresh; если он отклонён (истёк, отозван, повтор) — регистрация заново.</summary>
    private async Task RenewAsync(CancellationToken ct)
    {
        var facts = await identity.ReadAsync(ct);
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "diskless/v1/machines/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(_credentials!.RefreshToken, facts.Hwid), options: Json),
        }, ct);
        if (response.IsSuccessStatusCode)
        {
            var refreshed = await ReadAsync<RefreshResponse>(response, ct);
            _credentials = _credentials with { AccessToken = refreshed.AccessToken, RefreshToken = refreshed.RefreshToken, ExpiresAt = refreshed.ExpiresAt };
            await store.SaveAsync(_credentials, ct);
            return;
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw new ServerUnavailableException($"refresh: {(int)response.StatusCode}");
        }

        await store.ClearAsync(ct);
        _credentials = null;
        await RegisterAsync(ct);
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ClubKey))
        {
            throw new ServerUnavailableException("Not registered and no club key configured");
        }

        var facts = await identity.ReadAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "diskless/v1/machines/register")
        {
            Content = JsonContent.Create(new RegisterRequest(facts.Hwid, facts.Hostname, facts.MacAddresses, options.HelperVersion, facts.OsVersion), options: Json),
        };
        request.Headers.Add("X-Club-Key", options.ClubKey);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new PendingApprovalException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ServerUnavailableException($"register: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }

        var registered = await ReadAsync<RegisterResponse>(response, ct);
        _credentials = new StoredCredentials(registered.MachineId, registered.AccessToken, registered.RefreshToken, registered.ExpiresAt);
        await store.SaveAsync(_credentials, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ServerUnavailableException($"{request.Method} {request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ServerUnavailableException("empty response");
}
