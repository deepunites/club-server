namespace Club.Server.Diskless;

/// <summary>
/// Полный бездиск (секция <c>Diskless</c>, docs/diskless-full.md). Портал iSCSI — общий с библиотекой
/// (<c>Library:PortalId</c>, <c>Library:PortalAddress</c>).
/// </summary>
public sealed class DisklessOptions
{
    public bool Enabled { get; set; }

    /// <summary>zvol эталона Windows; версии — его снапшоты <c>@img-&lt;версия&gt;</c>.</summary>
    public string ImageZvol { get; set; } = "ssd/club/diskless/win11";

    /// <summary>Родитель личных дисков мест (файловая система; создаётся администратором в TrueNAS).</summary>
    public string SeatsParent { get; set; } = "ssd/club/diskless/seats";

    /// <summary>IQN инициатора бездискового ПК = префикс + <c>:seat-NN</c>; его задаёт iPXE, Windows берёт из iBFT.</summary>
    public string InitiatorPrefix { get; set; } = "iqn.2026-10.local.club";

    /// <summary>Таргет режима мастера: zvol эталона открыт на запись одной машине.</summary>
    public string MasterTargetName { get; set; } = "diskless-master";

    /// <summary>Установщик Windows для режима мастера с флагом «установка»: файлы в <c>Imaging:PxeRoot</c>/этот каталог.</summary>
    public string SetupDirectory { get; set; } = "winsetup";

    /// <summary>
    /// Сколько секунд после последнего отчёта помощника машина считается работающей: пока она работает и подключена к
    /// своему диску, откат диска запрещён (защита от чужого запроса загрузки с тем же MAC).
    /// </summary>
    public int RunningWindowSec { get; set; } = 60;

    public const string SnapshotPrefix = "img-";
    public const string CleanSnapshot = "clean";

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var (name, value) in new[] { ("ImageZvol", ImageZvol), ("SeatsParent", SeatsParent) })
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Contains('/') || value.StartsWith('/') || value.EndsWith('/') || value.Contains('@'))
            {
                throw new InvalidOperationException($"Diskless:{name} must be a dataset path like 'ssd/club/diskless/...'");
            }
        }

        if (!InitiatorPrefix.StartsWith("iqn.", StringComparison.Ordinal) || InitiatorPrefix.Contains(':'))
        {
            throw new InvalidOperationException("Diskless:InitiatorPrefix must look like 'iqn.2026-10.local.club' (no ':')");
        }

        if (RunningWindowSec < 10)
        {
            throw new InvalidOperationException("Diskless:RunningWindowSec must be at least 10");
        }
    }

    /// <summary>Имена места: таргет/экстент, CHAP-пользователь и группа — <c>seat-07</c>.</summary>
    public static string SeatLabel(int number) => $"seat-{number:D2}";

    public string SeatZvol(int number) => $"{SeatsParent}/{SeatLabel(number)}";

    public string SeatInitiator(int number) => $"{InitiatorPrefix}:{SeatLabel(number)}";

    public string MasterInitiator => $"{InitiatorPrefix}:master";

    public string VersionSnapshot(string version) => $"{ImageZvol}@{SnapshotPrefix}{version}";
}
