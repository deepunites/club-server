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

    /// <summary>Портал и вход в таргет (без persistent: при загрузке подключает сам помощник, по актуальной версии).</summary>
    Task ConnectAsync(string targetIqn, string portalHost, int portalPort, CancellationToken ct);

    Task DisconnectAsync(string targetIqn, CancellationToken ct);

    /// <summary>Диск, пришедший из сессии этого таргета; <c>null</c>, если ещё не появился.</summary>
    Task<DiskInfo?> FindDiskAsync(string targetIqn, CancellationToken ct);

    Task SetDiskReadOnlyAsync(int diskNumber, CancellationToken ct);

    Task SetDiskOnlineAsync(int diskNumber, CancellationToken ct);

    /// <summary>Назначает букву основному разделу диска. Буква занята другим томом — исключение.</summary>
    Task AssignDriveLetterAsync(int diskNumber, char letter, CancellationToken ct);
}

/// <summary>Процессы, запущенные с тома (исполняемый файл на букве тома).</summary>
public interface IProcessInspector
{
    Task<IReadOnlyList<string>> ProcessesRunningFromAsync(char driveLetter, CancellationToken ct);
}

/// <summary>
/// Факты о машине. <c>MacAddresses</c> — первым MAC основной карты (с IPv4-шлюзом): по нему сервер делает резервацию DHCP.
/// <c>DhcpServers</c> — кто выдал текущие аренды; чужой адрес здесь — чужой DHCP в сети клуба.
/// </summary>
public sealed record MachineFacts(
    string Hwid, string Hostname, IReadOnlyList<string> MacAddresses, string OsVersion, DateTimeOffset BootTime, IReadOnlyList<string>? DhcpServers = null,
    string? ImageVersion = null, SystemDiskFacts? SystemDisk = null, SecureBootFacts? SecureBoot = null);

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

public sealed record StoredCredentials(Guid MachineId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

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
