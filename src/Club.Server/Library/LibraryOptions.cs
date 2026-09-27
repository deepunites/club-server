namespace Club.Server.Library;

/// <summary>Раскладка библиотеки на TrueNAS и параметры раздачи агентам (секция <c>Library</c>).</summary>
public sealed class LibraryOptions
{
    /// <summary>Модуль хранилища выключен, пока TrueNAS не настроен: операции копятся в журнале, конфиг без тома.</summary>
    public bool Enabled { get; set; }

    /// <summary>Мастер-zvol, в который пишет суперклиент; с него снимаются снапшоты версий.</summary>
    public string MasterZvol { get; set; } = "tank/club/lib";

    /// <summary>Родитель RO-клонов (filesystem в том же пуле, без readonly=on, не под ix-apps/.system).</summary>
    public string PublishedParent { get; set; } = "tank/club/published";

    /// <summary>Id портала и группы инициаторов iSCSI в TrueNAS. Группа — явная: пустая открывает таргет всем.</summary>
    public int PortalId { get; set; } = 1;

    public int InitiatorGroupId { get; set; } = 1;

    /// <summary>Портал для агентов, <c>host:port</c> (agent.json → storage.gamesShare.iscsi.portal).</summary>
    public string PortalAddress { get; set; } = "";

    public string DriveLetter { get; set; } = "G";

    /// <summary>Сразу после clone узел /dev/zvol может ещё не появиться — extent.create повторяется.</summary>
    public int ExtentAttempts { get; set; } = 5;

    public int ExtentRetryDelayMs { get; set; } = 2000;

    /// <summary>Сколько раз повторять публикацию после ошибки TrueNAS (кроме недоступности), прежде чем пометить failed.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Фоновый исполнитель (в тестах выключается, проход вызывается явно).</summary>
    public bool RunWorker { get; set; } = true;

    public int WorkerIntervalSec { get; set; } = 15;

    public int ReconcileIntervalSec { get; set; } = 300;
}
