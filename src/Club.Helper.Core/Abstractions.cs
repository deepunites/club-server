namespace Club.Helper.Core;

public sealed class HelperOptions
{
    /// <summary>Адрес сервера клуба, например <c>https://club-server</c>.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Ключ клуба — только для регистрации; после неё помощник живёт на своих токенах.</summary>
    public string ClubKey { get; set; } = "";

    /// <summary>PEM внутреннего CA клуба для проверки сертификата сервера (пусто — системное хранилище).</summary>
    public string CaCertificatePath { get; set; } = "";

    public int PollIntervalSec { get; set; } = 30;

    /// <summary>Сколько ждать появления диска после входа в таргет.</summary>
    public int DiskWaitSec { get; set; } = 30;

    public string HelperVersion { get; set; } = "1.0.0";

    /// <summary>Библиотеки, кроме G:, по умолчанию (1.6.0): H: — <c>solo</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultLibraries = ["solo"];

    /// <summary>
    /// Библиотеки игр кроме G:, ключи сервера (<c>Libraries:&lt;ключ&gt;</c>): у каждой свой цикл (<see cref="HelperLoops"/>).
    /// В helper.json — <c>"Libraries": []</c>, чтобы на этом ПК была только G:. Нет ключа — <see cref="DefaultLibraries"/>.
    /// Читается <see cref="LibrariesFrom"/>, а не привязкой конфигурации: та дописала бы список к умолчанию, а пустой
    /// <c>[]</c> не отличила бы от отсутствующего.
    /// </summary>
    public IReadOnlyList<string> Libraries { get; set; } = DefaultLibraries;

    /// <summary>
    /// Список библиотек из настроек (<c>configuration.AsEnumerable()</c>): ключа <c>&lt;section&gt;:Libraries</c> нет —
    /// <see cref="DefaultLibraries"/>; пустой список (<c>[]</c> или <c>""</c>) — только G:; строка — ключи через запятую.
    /// </summary>
    public static IReadOnlyList<string> LibrariesFrom(IEnumerable<KeyValuePair<string, string?>> settings, string section = "Helper")
    {
        var key = $"{section}:Libraries";
        var found = false;
        var items = new SortedList<int, string>();
        string? text = null;
        foreach (var (path, value) in settings)
        {
            if (path.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                text = value;
            }
            else if (path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) && int.TryParse(path[(key.Length + 1)..], out var index))
            {
                found = true;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    items[index] = value.Trim();
                }
            }
        }

        if (!found)
        {
            return DefaultLibraries;
        }

        return items.Count > 0 ? [.. items.Values]
            : [.. (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }
}

/// <summary>Диск Windows, пришедший из iSCSI-сессии таргета.</summary>
public sealed record DiskInfo(int Number, bool IsReadOnly, bool IsOffline, char? DriveLetter);

/// <summary>
/// Операции Windows с iSCSI и дисками. Реализация — в службе (командлеты Storage/iSCSI); в тестах — подделка.
/// Порядок вызовов задаёт <see cref="VolumeManager"/>: том никогда не бывает online и доступен на запись одновременно.
/// </summary>
public interface IWindowsStorage
{
    /// <summary>Политика SAN OfflineShared: новые общие диски приходят offline, Windows сама не монтирует NTFS на запись.</summary>
    Task EnsureSanPolicyOfflineSharedAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ConnectedTargetsAsync(CancellationToken ct);

    /// <summary>
    /// Таргеты всех сессий, и подключённых, и восстанавливающих связь (IsConnected = false): такая сессия всё ещё держит
    /// смонтированный том, переподключать или сбрасывать его диск нельзя.
    /// </summary>
    Task<IReadOnlyList<string>> SessionTargetsAsync(CancellationToken ct);

    /// <summary>Портал и вход в таргет (без persistent: при загрузке подключает сам помощник, по актуальной версии).</summary>
    Task ConnectAsync(string targetIqn, string portalHost, int portalPort, CancellationToken ct);

    Task DisconnectAsync(string targetIqn, CancellationToken ct);

    /// <summary>Диск, пришедший из сессии этого таргета; <c>null</c>, если ещё не появился.</summary>
    Task<DiskInfo?> FindDiskAsync(string targetIqn, CancellationToken ct);

    Task SetDiskReadOnlyAsync(int diskNumber, CancellationToken ct);

    Task SetDiskOnlineAsync(int diskNumber, CancellationToken ct);

    /// <summary>Назначает букву основному разделу диска. Буква занята другим томом — исключение.</summary>
    Task AssignDriveLetterAsync(int diskNumber, char letter, CancellationToken ct);

    /// <summary>
    /// Буква занята каким-то томом (та же проверка, что в <see cref="AssignDriveLetterAsync"/>): у библиотек не-G: —
    /// перед входом в таргет, чтобы не подключать диск, которому букву всё равно не дать (картридер, DVD на H:).
    /// </summary>
    Task<bool> DriveLetterInUseAsync(char letter, CancellationToken ct);

    /// <summary>Папки верхнего уровня тома (без скрытых и системных) — «состав» версии библиотеки для панели.</summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(char driveLetter, CancellationToken ct);

    /// <summary>Вход в таргет с односторонним CHAP (мастер-том суперклиента).</summary>
    Task ConnectChapAsync(string targetIqn, string portalHost, int portalPort, string chapUser, string chapSecret, CancellationToken ct);

    /// <summary>Снять read-only с диска — только для мастер-тома, который администратор открыл на запись.</summary>
    Task SetDiskWritableAsync(int diskNumber, CancellationToken ct);

    /// <summary>Сбросить кэш тома на диск и перевести диск offline (перед отключением таргета).</summary>
    Task FlushAndOfflineAsync(int diskNumber, char? driveLetter, CancellationToken ct);
}

/// <summary>Процессы, запущенные с тома (исполняемый файл на томе — по пути Win32 или NT, см. <see cref="VolumeImagePaths"/>).</summary>
public interface IProcessInspector
{
    /// <summary>Пути образов (в форме <c>G:\…</c>) процессов, запущенных с тома.</summary>
    Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct);

    /// <summary>
    /// Подробный проход по процессам для журнала. Только когда Windows отказалась отключить старую версию, а процесса с
    /// её тома не нашлось, и один раз на смену причины — не в каждом такте.
    /// </summary>
    Task<ProcessScanReport> DiagnoseAsync(char driveLetter, CancellationToken ct);
}

/// <summary>
/// Факты о машине. <c>MacAddresses</c> — первым MAC основной карты (с IPv4-шлюзом): по нему сервер делает резервацию DHCP.
/// <c>DhcpServers</c> — кто выдал текущие аренды; чужой адрес здесь — чужой DHCP в сети клуба.
/// </summary>
public sealed record MachineFacts(
    string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string OsVersion, DateTimeOffset BootTime, IReadOnlyList<string>? DhcpServers = null,
    string? ImageVersion = null, SystemDiskFacts? SystemDisk = null, SecureBootFacts? SecureBoot = null, string? InitiatorIqn = null);

/// <summary>
/// Secure Boot на ПК (для перезаливки по сети): включён ли; доверяет ли прошивка стороннему Microsoft UEFI CA 2011
/// (им подписан shim iPXE) и Windows UEFI CA 2023; отозван ли в dbx загрузчик Windows PCA 2011.
/// </summary>
public sealed record SecureBootFacts(bool? Enabled, bool? ThirdPartyCa2011, bool? WindowsCa2023, bool? Pca2011Revoked);

/// <summary>Диск, с которого загружена Windows: по серийному номеру WinPE при перезаливке стирает именно его.</summary>
public sealed record SystemDiskFacts(string? Serial, string? Model, long SizeBytes, string BusType);

public interface IMachineIdentity
{
    Task<MachineFacts> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Токены машины. <paramref name="Hwid"/> — чьи: файл на диске мог приехать с другого ПК (эталон бездиска снят с ПК
/// мастера, клон диска), такие токены не используются.
/// </summary>
public sealed record StoredCredentials(Guid MachineId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? Hwid = null);

/// <summary>HWID машины из SMBIOS: UUID и серийник платы, а при заводской заглушке вместо UUID — ещё и MAC.</summary>
public static class MachineHwid
{
    /// <summary>
    /// Заглушки UUID, одинаковые у множества плат (AMI по умолчанию, нули, FF): с ними два ПК получили бы один HWID и
    /// слились бы в одну машину на сервере.
    /// </summary>
    private static readonly HashSet<string> PlaceholderUuids = new(StringComparer.Ordinal)
    {
        "03000200040005000006000700080009",
        "00020003000400050006000700080009",
        "12345678123456781234567812345678",
    };

    public static bool IsPlaceholderUuid(string uuid)
    {
        var hex = new string(uuid.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        return hex.Length != 32 || hex.All(c => c == hex[0]) || PlaceholderUuids.Contains(hex);
    }

    /// <summary>SHA-256 от «uuid|серийник» (как раньше); при заглушке UUID — «uuid|серийник|наименьший MAC».</summary>
    public static string Compute(string uuid, string boardSerial, IEnumerable<string> macs)
    {
        var key = $"{uuid.Trim().ToLowerInvariant()}|{boardSerial.Trim()}";
        if (IsPlaceholderUuid(uuid) && macs.Select(m => m.ToLowerInvariant()).Order(StringComparer.Ordinal).FirstOrDefault() is { } mac)
        {
            key += $"|{mac}";
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }
}

/// <summary>Хранилище токенов машины (в службе — DPAPI, доступно только SYSTEM).</summary>
public interface ICredentialStore
{
    Task<StoredCredentials?> LoadAsync(CancellationToken ct);

    Task SaveAsync(StoredCredentials credentials, CancellationToken ct);

    Task ClearAsync(CancellationToken ct);
}

public sealed class InMemoryCredentialStore : ICredentialStore
{
    public StoredCredentials? Current { get; set; }

    public Task<StoredCredentials?> LoadAsync(CancellationToken ct) => Task.FromResult(Current);

    public Task SaveAsync(StoredCredentials credentials, CancellationToken ct)
    {
        Current = credentials;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        Current = null;
        return Task.CompletedTask;
    }
}
