using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// Отчёт о ПК. <paramref name="Hostname"/> (1.5.2) — текущее имя Windows в каждом отчёте: гибрид регистрируется в
/// specialize, до переименования ПК, а повторной регистрации нет. Старый сервер поле пропускает.
/// <paramref name="HelperBuild"/> и <paramref name="HelperUpdate"/> (1.8.0, только отчёт G:) — SHA256 своего exe строчными и
/// ход поручения самообновления; <c>null</c> не передаётся, провод старых помощников не меняется.
/// </summary>
public sealed record MachineStatus(
    string HelperVersion, DateTimeOffset? BootTime, MountedVolume Volume, IReadOnlyList<string>? DhcpServers = null,
    string? ImageVersion = null, SystemDiskFacts? SystemDisk = null, SecureBootFacts? SecureBoot = null,
    string? InitiatorIqn = null, MasterReport? Master = null, string? Hostname = null,
    string? HelperBuild = null, HelperUpdateReport? HelperUpdate = null);

/// <summary>
/// Поручение самообновления в ответе на отчёт G: (1.8.0, spec 2026-11-06, п. 3.4): поставить сборку <paramref name="Version"/>
/// с этим SHA256 (строчными) и размером. <paramref name="Path"/> — относительный путь скачивания на сервере клуба,
/// <c>pxe/v1/files/tools/ClubDisklessHelper-X.Y.Z.exe</c>; правдоподобие поручения проверяет помощник, а не разбор ответа.
/// </summary>
public sealed record HelperUpdateOffer(Guid Id, string Version, string Sha256, long Size, string Path);

/// <summary>
/// Ход поручения самообновления в отчёте G: (1.8.0): <paramref name="Id"/> — id поручения из <see cref="HelperUpdateOffer"/>;
/// <paramref name="State"/> — <c>downloading</c>, <c>waiting</c> (с <paramref name="Reason"/>: <c>session</c>,
/// <c>command</c>, <c>master</c>), <c>applying</c>, <c>confirming</c>, <c>confirmed</c>, <c>failed</c> (с
/// <paramref name="Error"/> и текстом <paramref name="Message"/>).
/// </summary>
public sealed record HelperUpdateReport(string Id, string State, string? Reason = null, string? Error = null, string? Message = null);

public sealed record MasterAssignment(string TargetIqn, string Portal, string ChapUser, string ChapSecret, string DriveLetter);

public sealed record MasterReport(string State, string? TargetIqn = null, string? DriveLetter = null, string? Error = null);

/// <summary>
/// Ответ на отчёт. <paramref name="LibraryLetterReserved"/> (сервер с Library:MasterDriveLetter = DriveLetter, помощник
/// 1.5.1+) — буква тома библиотеки отдана мастер-тому этого ПК: <paramref name="Volume"/> нет намеренно, и том
/// библиотеки отпускается, а не сохраняется, как при пустом ответе. <paramref name="HelperUpdate"/> (сервер с
/// самообновлением, только ответ G:) — поручение поставить другую сборку помощника; нет — поручения нет (принятый ответ
/// без него отменяет начатое).
/// </summary>
public sealed record StatusAccepted(DateTimeOffset ServerTime, VolumeAssignment? Volume, MasterAssignment? Master = null, bool LibraryLetterReserved = false,
    HelperUpdateOffer? HelperUpdate = null);

/// <summary>
/// Ответ на запрос назначения при старте. <paramref name="LibraryLetterReserved"/> (сервер с Library:MasterDriveLetter =
/// DriveLetter, заголовок <see cref="DisklessApiClient.LibraryLetterReservedHeader"/> у 204) — тома нет, потому что его
/// буква отдана мастер-тому этого ПК, а не потому, что монтировать нечего.
/// </summary>
public sealed record BootAssignment(VolumeAssignment? Volume, bool LibraryLetterReserved = false);

/// <summary>Регистрация; <paramref name="HelperBuild"/> (1.8.0) — SHA256 своего exe, как в отчёте G:.</summary>
internal sealed record RegisterRequest(string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string HelperVersion, string? OsVersion,
    string? HelperBuild = null);

internal sealed record RegisterResponse(Guid MachineId, int Number, string Name, string Zone, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime);

internal sealed record RefreshRequest(string RefreshToken, string Hwid);

internal sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Сервер недоступен или ответил ошибкой: том не трогаем, повторим на следующем такте.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null, HttpStatusCode? status = null) : Exception(message, inner)
{
    /// <summary>До сервера не дошли (сеть, отказ соединения), а не «ответил ошибкой» или «не успел».</summary>
    public bool Unreachable => InnerException is HttpRequestException;

    /// <summary>
    /// Код ответа сервера, если он ответил ошибкой (<c>null</c> — не ответил). 404 на пути библиотеки — её на сервере нет
    /// (выключена, неизвестна, сервер старше 1.6.0): цикл этой библиотеки засыпает (<see cref="HelperLoop"/>).
    /// </summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Папка из списка сохранений: ID записи (имя папки в архиве) и шаблон пути (<c>%APPDATA%\…</c>, <c>H:\…</c>).</summary>
public sealed record SaveEntryConfig(Guid Id, string Path);

/// <summary>
/// Настройки сохранений (<c>GET …/saves/config</c> и заголовок attach, spec 2026-11-05, п. 3.3): <paramref name="Enabled"/> —
/// сохранения включены на сервере и диск сохранений смонтирован; только включённые папки.
/// </summary>
public sealed record SavesConfig(bool Enabled, int LimitMb, IReadOnlyList<string> Excludes, IReadOnlyList<SaveEntryConfig> Entries);

/// <summary>
/// Ответ выдачи сохранений: настройки из заголовка и архив игрока (<c>null</c> — у игрока ничего нет, 204). Архив —
/// zip во временном файле (с перемоткой: распаковка не копирует его в память), ответ сервера к этому времени уже закрыт;
/// файл удаляется при <see cref="DisposeAsync"/>.
/// </summary>
public sealed class SavesAttach(SavesConfig config, Stream? archive) : IAsyncDisposable
{
    public SavesConfig Config { get; } = config;

    public Stream? Archive { get; } = archive;

    public ValueTask DisposeAsync() => Archive?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Сервер ответил на команду сохранений отказом по существу (а не «недоступен»): <see cref="Reason"/> — <c>notAttached</c>
/// (409: сохранения игрока выданы не этому ПК), <c>overLimit</c> (413), <c>badArchive</c> (400), <c>disabled</c> (503
/// <c>savesUnavailable</c>: сохранения выключены или диск сохранений не смонтирован).
/// </summary>
public sealed class SavesRejectedException(string reason) : Exception($"Player saves rejected by the server: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>Машина в реестре, но ждёт одобрения администратора.</summary>
public sealed class PendingApprovalException() : Exception("Machine is waiting for administrator approval");

/// <summary>
/// Клиент API бездиска. Держит токены машины: при 401 обновляет их refresh-токеном, при отказе refresh
/// регистрируется заново по ключу клуба (номер места сохраняется — сервер узнаёт машину по HWID).
/// Один экземпляр на все библиотеки (циклы G: и H: идут по очереди, <see cref="HelperLoops"/>): refresh-токен одноразовый,
/// второй клиент с той же парой предъявил бы уже использованный — сервер принял бы это за кражу и отозвал бы токены машины.
/// С 1.7.0 клиентом пользуются два потока (такт помощника и команды канала шелла, <see cref="SavesService"/>): загрузка,
/// обновление и регистрация токенов — по одной (<see cref="_renewal"/>); запрос, получивший 401 со старым токеном, после
/// чужого обновления берёт новый, а не обновляет снова.
/// <paramref name="transfers"/> — отдельный HttpClient для архивов сохранений (attach и PUT: десятки и сотни мегабайт, свой
/// тайм-аут); <c>null</c> — тот же <paramref name="http"/>. <paramref name="downloads"/> (1.8.0) — HttpClient для больших
/// файлов (сборки помощника, <see cref="DownloadAsync(string, string, long, CancellationToken)"/>): тот же адрес сервера и
/// сертификат клуба, свой долгий тайм-аут; <c>null</c> — <paramref name="transfers"/>.
/// </summary>
public sealed class DisklessApiClient(
    HttpClient http, HelperOptions options, ICredentialStore store, IMachineIdentity identity, HttpClient? transfers = null, HttpClient? downloads = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>UTF-8 без замены битых байт: сломанный заголовок настроек — ошибка, а не «�» в пути.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const int BufferSize = 81920;

    private readonly HttpClient _transfers = transfers ?? http;

    private readonly HttpClient _downloads = downloads ?? transfers ?? http;

    /// <summary>Загрузка, обновление и регистрация токенов — по одной на оба потока (такт и канал шелла).</summary>
    private readonly SemaphoreSlim _renewal = new(1, 1);

    private volatile StoredCredentials? _credentials;

    public Guid? MachineId => _credentials?.MachineId;

    /// <summary>Заголовок 204 на запрос назначения при старте: тома нет, потому что его буква отдана мастер-тому.</summary>
    public const string LibraryLetterReservedHeader = "X-Library-Letter-Reserved";

    /// <summary>
    /// Назначение при старте помощника. <paramref name="attached"/> — подключён ли на ПК личный диск игр (<c>null</c> —
    /// неизвестно): <c>false</c> разрешает серверу сбросить диск, даже если на нём висит сессия прошлой загрузки.
    /// <paramref name="library"/> — ключ библиотеки не-G: (путь <c>machines/{id}/libraries/{key}/volume</c>, сервер 1.6.0+);
    /// <c>null</c> — G:, прежний путь.
    /// </summary>
    public async Task<BootAssignment> GetVolumeAsync(bool? attached, CancellationToken ct, string? library = null)
    {
        var query = attached is { } a ? $"?attached={(a ? "true" : "false")}" : "";
        using var response = await SendAuthorizedAsync(id => new HttpRequestMessage(HttpMethod.Get, $"{MachinePath(id, library)}/volume{query}"), ct);
        return response.StatusCode == HttpStatusCode.NoContent
            ? new BootAssignment(null, response.Headers.TryGetValues(LibraryLetterReservedHeader, out var reserved)
                && reserved.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
            : new BootAssignment(await ReadAsync<VolumeAssignment>(response, ct));
    }

    /// <summary>Отчёт о ПК; <paramref name="library"/> — как у <see cref="GetVolumeAsync"/> (<c>…/libraries/{key}/status</c>).</summary>
    public async Task<StatusAccepted> ReportStatusAsync(MachineStatus status, CancellationToken ct, string? library = null)
    {
        using var response = await SendAuthorizedAsync(
            id => new HttpRequestMessage(HttpMethod.Put, $"{MachinePath(id, library)}/status") { Content = JsonContent.Create(status, options: Json) },
            ct);
        return await ReadAsync<StatusAccepted>(response, ct);
    }

    /// <summary>Заголовок ответа attach (200 и 204): <see cref="SavesConfig"/> — base64 от UTF-8 JSON (в путях бывает кириллица).</summary>
    public const string SavesConfigHeader = "X-Club-Saves-Config";

    /// <summary>
    /// Список папок сохранений (при старте и раз в 5 минут). Сервер отвечает и когда сохранения недоступны — с
    /// <c>enabled=false</c>; ошибки — <see cref="ServerUnavailableException"/>.
    /// </summary>
    public async Task<SavesConfig> GetSavesConfigAsync(CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(id => new HttpRequestMessage(HttpMethod.Get, $"{SavesPath(id)}/config"), ct);
        return Checked(await ReadAsync<SavesConfig>(response, ct), "saves config");
    }

    /// <summary>
    /// Выдача сохранений игрока этому ПК (сервер записывает аренду, забирая её у другого ПК): архив скачивается через
    /// <c>transfers</c> во временный файл (<see cref="Path.GetTempPath"/>; у SYSTEM — <c>C:\Windows\SystemTemp</c>, доступ
    /// только SYSTEM и администраторам), тело ответа читается не дольше тайм-аута этого HttpClient. Отказ —
    /// <see cref="SavesRejectedException"/> (<c>disabled</c>); сеть, прочие коды, сломанный заголовок настроек —
    /// <see cref="ServerUnavailableException"/> (аренда тогда, возможно, уже взята: следующая выдача её перезапишет).
    /// </summary>
    public async Task<SavesAttach> AttachSavesAsync(string playerId, CancellationToken ct)
    {
        using var response = await SendSavesAsync(_transfers, id => new HttpRequestMessage(HttpMethod.Post, $"{SavesPath(id, playerId)}/attach"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        var config = DecodeConfig(response);
        return new SavesAttach(config, response.StatusCode == HttpStatusCode.NoContent ? null : await DownloadAsync(response, ct));
    }

    /// <summary>
    /// Новая версия сохранений игрока (zip, записи <c>&lt;entryId&gt;/&lt;путь&gt;</c>) через <c>transfers</c>.
    /// <paramref name="archive"/> — поток с перемоткой (временный файл) с текущей позиции до конца; не закрывается. Нужна
    /// перемотка: после 401 запрос повторяется с новым токеном с той же позиции. Отказ — <see cref="SavesRejectedException"/>
    /// (<c>notAttached</c>, <c>overLimit</c>, <c>badArchive</c>, <c>disabled</c>).
    /// </summary>
    public async Task PutSavesAsync(string playerId, Stream archive, CancellationToken ct)
    {
        if (!archive.CanSeek)
        {
            throw new ArgumentException("Saves archive stream must be seekable (a temp file)", nameof(archive));
        }

        var start = archive.Position;
        using var response = await SendSavesAsync(_transfers,
            id => new HttpRequestMessage(HttpMethod.Put, SavesPath(id, playerId)) { Content = new ArchiveContent(archive, start) },
            HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>Снять аренду без сохранения (выдача на ПК не удалась после ответа сервера). Отказ — как у <see cref="PutSavesAsync"/>.</summary>
    public Task ReleaseSavesLeaseAsync(string playerId, CancellationToken ct) => ReleaseLeaseAsync(playerId, "", ct);

    /// <summary>
    /// Снять аренду, не отправив сохранения больше лимита (spec п. 4.3): сервер ставит игроку отметку <c>overLimit</c>
    /// (п. 6), прошлая версия остаётся. <paramref name="bytes"/> — сколько набралось (<c>null</c> — неизвестно, например
    /// больше 50 000 файлов). Отказ — как у <see cref="PutSavesAsync"/>.
    /// </summary>
    public Task ReleaseSavesLeaseOverLimitAsync(string playerId, long? bytes, CancellationToken ct) =>
        ReleaseLeaseAsync(playerId, bytes is { } size ? $"?reason=overLimit&bytes={size.ToString(CultureInfo.InvariantCulture)}" : "?reason=overLimit", ct);

    private async Task ReleaseLeaseAsync(string playerId, string query, CancellationToken ct)
    {
        using var response = await SendSavesAsync(http, id => new HttpRequestMessage(HttpMethod.Delete, $"{SavesPath(id, playerId)}/lease{query}"),
            HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>
    /// Скачать файл сервера клуба (1.8.0: сборка помощника, <see cref="HelperUpdateOffer.Path"/>) в
    /// <paramref name="destination"/> через <c>downloads</c>: без токена машины (файлы <c>pxe/v1/files</c> публичны, как файлы
    /// PXE), только по относительному пути — с адреса сервера из helper.json. Файл открывается монопольно и перезаписывается;
    /// SHA256 считается по ходу; в конце — сброс на диск (<c>Flush(true)</c>). Возвращает SHA256 строчными (сверяет
    /// вызывающий). Больше <paramref name="maxBytes"/> байт (по Content-Length или по ходу), сеть, тайм-аут <c>downloads</c>,
    /// код ответа не 2xx — <see cref="ServerUnavailableException"/>; ошибка записи на диск — как есть (<see cref="IOException"/>);
    /// при любой ошибке и отмене файл стёрт. Путь абсолютный или от корня другого хоста (<c>//…</c>) — <see cref="ArgumentException"/>,
    /// запроса нет.
    /// </summary>
    public async Task<string> DownloadAsync(string relativePath, string destination, long maxBytes, CancellationToken ct)
    {
        if (!Uri.TryCreate(relativePath, UriKind.Relative, out var uri) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new ArgumentException($"Not a path on the club server: {relativePath}", nameof(relativePath));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_downloads.Timeout != Timeout.InfiniteTimeSpan)
            {
                timeout.CancelAfter(_downloads.Timeout);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await ReceiveAsync(() => _downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token), request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ServerUnavailableException($"{Describe(response)}: {(int)response.StatusCode}", status: response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new ServerUnavailableException($"{Describe(response)}: {response.Content.Headers.ContentLength} bytes, more than {maxBytes}");
            }

            await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var body = await ReceiveAsync(() => response.Content.ReadAsStreamAsync(timeout.Token), request, ct);
            var buffer = new byte[BufferSize];
            long total = 0;
            while (await ReceiveAsync(() => body.ReadAsync(buffer, timeout.Token).AsTask(), request, ct) is var read and > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new ServerUnavailableException($"{Describe(response)}: more than {maxBytes} bytes");
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch
        {
            DeleteQuietly(destination);
            throw;
        }
    }

    /// <summary>Недокачанный файл — стереть; не стёрся (антивирус держит) — его сотрёт следующая загрузка, ошибка — прежняя.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Шаг загрузки (ответ, тело, чтение): сеть оборвалась или истёк тайм-аут <c>downloads</c> (не отмена вызывающего) —
    /// <see cref="ServerUnavailableException"/>.
    /// </summary>
    private static async Task<T> ReceiveAsync<T>(Func<Task<T>> step, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ServerUnavailableException($"{request.Method} {request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static string MachinePath(Guid id, string? library) =>
        library is null ? $"diskless/v1/machines/{id}" : $"diskless/v1/machines/{id}/libraries/{library}";

    private static string SavesPath(Guid id) => $"diskless/v1/machines/{id}/saves";

    private static string SavesPath(Guid id, string playerId) => $"{SavesPath(id)}/{Uri.EscapeDataString(playerId)}";

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<Guid, HttpRequestMessage> build, CancellationToken ct)
    {
        var response = await SendAuthorizedAsync(build, http, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new ServerUnavailableException($"{Describe(response)}: {(int)response.StatusCode} {text}", status: response.StatusCode);
        }

        return response;
    }

    /// <summary>
    /// Запрос с токеном машины через <paramref name="client"/>: при 401 — обновление токенов и один повтор. Ответ отдаётся
    /// с любым кодом (кроме первого 401) — разбирает вызывающий.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<Guid, HttpRequestMessage> build, HttpClient client, HttpCompletionOption completion,
        CancellationToken ct)
    {
        var credentials = await EnsureCredentialsAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build(credentials.MachineId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            var response = await SendAsync(request, ct, client, completion);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            credentials = await RenewAsync(credentials, ct);
        }
    }

    /// <summary>
    /// Команда сохранений: 2xx — ответ; отказы сервера по существу — <see cref="SavesRejectedException"/> (409 —
    /// <c>notAttached</c>, 413 — <c>overLimit</c>, 400 с <c>reason=badArchive</c> — <c>badArchive</c>, 503 с
    /// <c>reason=savesUnavailable</c> — <c>disabled</c>); прочие коды — <see cref="ServerUnavailableException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendSavesAsync(HttpClient client, Func<Guid, HttpRequestMessage> build, HttpCompletionOption completion,
        CancellationToken ct)
    {
        var response = await SendAuthorizedAsync(build, client, completion, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                text = "";
            }

            Exception error = (response.StatusCode, ErrorReason(text)) switch
            {
                (HttpStatusCode.Conflict, _) => new SavesRejectedException("notAttached"),
                (HttpStatusCode.RequestEntityTooLarge, _) => new SavesRejectedException("overLimit"),
                (HttpStatusCode.BadRequest, "badArchive") => new SavesRejectedException("badArchive"),
                (HttpStatusCode.ServiceUnavailable, "savesUnavailable") => new SavesRejectedException("disabled"),
                _ => new ServerUnavailableException($"{Describe(response)}: {(int)response.StatusCode} {text}", status: response.StatusCode),
            };
            throw error;
        }
    }

    private static string Describe(HttpResponseMessage response) =>
        response.RequestMessage is { } request ? $"{request.Method} {request.RequestUri}" : "request";

    /// <summary><c>error.details.reason</c> из конверта ошибки сервера; <c>null</c> — нет или тело не JSON.</summary>
    private static string? ErrorReason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Настройки из заголовка attach: строго base64 → UTF-8 → JSON; иначе — <see cref="ServerUnavailableException"/>.</summary>
    private static SavesConfig DecodeConfig(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(SavesConfigHeader, out var values) || values.ToList() is not [var value])
        {
            throw new ServerUnavailableException($"saves attach: no single {SavesConfigHeader} header");
        }

        SavesConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SavesConfig>(StrictUtf8.GetString(Convert.FromBase64String(value)), Json);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or JsonException)
        {
            throw new ServerUnavailableException($"saves attach: malformed {SavesConfigHeader} header: {ex.Message}", ex);
        }

        return Checked(config, $"{SavesConfigHeader} header");
    }

    /// <summary>Настройки без списков или с папкой без пути — ответ не того сервера: <see cref="ServerUnavailableException"/>.</summary>
    private static SavesConfig Checked(SavesConfig? config, string source) =>
        config is { Excludes: not null, Entries: not null } && config.Excludes.All(e => e is not null) && config.Entries.All(e => e is { Path: not null })
            ? config
            : throw new ServerUnavailableException($"{source}: incomplete saves config");

    /// <summary>
    /// Тело ответа attach — во временный файл (удаляется при закрытии); сеть оборвалась или тело шло дольше тайм-аута
    /// <c>transfers</c> — <see cref="ServerUnavailableException"/>, файл удаляется.
    /// </summary>
    private async Task<Stream> DownloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"club-saves-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_transfers.Timeout != Timeout.InfiniteTimeSpan)
            {
                timeout.CancelAfter(_transfers.Timeout);
            }

            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                await body.CopyToAsync(file, BufferSize, timeout.Token);
            }

            file.Position = 0;
            return file;
        }
        catch (Exception ex)
        {
            await file.DisposeAsync();
            if (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServerUnavailableException($"saves attach: archive download failed: {ex.Message}", ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Тело PUT: архив с позиции <c>start</c> до конца, с Content-Length (сервер сразу отвечает 413 на слишком большое).
    /// Поток вызывающего не закрывает; каждая отправка (повтор после 401) — снова с <c>start</c>.
    /// </summary>
    private sealed class ArchiveContent : HttpContent
    {
        private readonly Stream _archive;
        private readonly long _start;

        public ArchiveContent(Stream archive, long start)
        {
            _archive = archive;
            _start = start;
            Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _archive.Position = _start;
            await _archive.CopyToAsync(stream, BufferSize, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Math.Max(0, _archive.Length - _start);
            return true;
        }
    }

    /// <summary>Токены машины: сохранённые (этого ПК) или новые по регистрации; под <see cref="_renewal"/>.</summary>
    private async Task<StoredCredentials> EnsureCredentialsAsync(CancellationToken ct)
    {
        if (_credentials is { } current)
        {
            return current;
        }

        await _renewal.WaitAsync(ct);
        try
        {
            if (_credentials is null && await store.LoadAsync(ct) is { } stored)
            {
                if (stored.Hwid == (await identity.ReadAsync(ct)).Hwid)
                {
                    _credentials = stored;
                }
                else
                {
                    // Токены другого ПК: файл приехал с диском (эталон бездиска снят с ПК мастера, клон диска). С ними этот ПК
                    // выдавал бы себя за чужую машину — регистрируемся сами (сервер узнает ПК по HWID).
                    await store.ClearAsync(ct);
                }
            }

            return _credentials ?? await RegisterAsync(ct);
        }
        finally
        {
            _renewal.Release();
        }
    }

    /// <summary>
    /// Токены после 401 на запрос с <paramref name="stale"/>: другой поток их уже обновил (или зарегистрировался) — его
    /// токены, без второго refresh; иначе refresh, а если он отклонён (истёк, отозван, повтор) — регистрация заново.
    /// Под <see cref="_renewal"/>: одноразовый refresh-токен предъявляется один раз.
    /// </summary>
    private async Task<StoredCredentials> RenewAsync(StoredCredentials stale, CancellationToken ct)
    {
        await _renewal.WaitAsync(ct);
        try
        {
            if (_credentials is not { } current)
            {
                // Предыдущий refresh отклонён, а регистрация не удалась: токенов нет — регистрируемся.
                return await RegisterAsync(ct);
            }

            if (!ReferenceEquals(current, stale))
            {
                return current;
            }

            var facts = await identity.ReadAsync(ct);
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "diskless/v1/machines/refresh")
            {
                Content = JsonContent.Create(new RefreshRequest(current.RefreshToken, facts.Hwid), options: Json),
            }, ct);
            if (response.IsSuccessStatusCode)
            {
                var refreshed = await ReadAsync<RefreshResponse>(response, ct);
                var renewed = current with { AccessToken = refreshed.AccessToken, RefreshToken = refreshed.RefreshToken, ExpiresAt = refreshed.ExpiresAt };
                _credentials = renewed;
                await store.SaveAsync(renewed, ct);
                return renewed;
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                throw new ServerUnavailableException($"refresh: {(int)response.StatusCode}");
            }

            await store.ClearAsync(ct);
            _credentials = null;
            return await RegisterAsync(ct);
        }
        finally
        {
            _renewal.Release();
        }
    }

    /// <summary>Регистрация по ключу клуба; вызывается только под <see cref="_renewal"/>.</summary>
    private async Task<StoredCredentials> RegisterAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ClubKey))
        {
            throw new ServerUnavailableException("Not registered and no club key configured");
        }

        var facts = await identity.ReadAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "diskless/v1/machines/register")
        {
            Content = JsonContent.Create(
                new RegisterRequest(facts.Hwid, facts.Hostname, facts.MacAddresses, options.HelperVersion, facts.OsVersion, options.HelperBuild), options: Json),
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
        var credentials = new StoredCredentials(registered.MachineId, registered.AccessToken, registered.RefreshToken, registered.ExpiresAt, facts.Hwid);
        _credentials = credentials;
        await store.SaveAsync(credentials, ct);
        return credentials;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, HttpClient? client = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await (client ?? http).SendAsync(request, completion, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ServerUnavailableException($"{request.Method} {request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ServerUnavailableException("empty response");
}
