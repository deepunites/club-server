using Club.TrueNas;

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

    /// <summary>
    /// extent.create сам проверяет узел /dev/zvol клона («Device … does not exist»); если udev ещё не успел, вызов
    /// повторяется в том же проходе.
    /// </summary>
    public int ExtentAttempts { get; set; } = 5;

    public int ExtentRetryDelayMs { get; set; } = 2000;

    /// <summary>
    /// Портал для проверки публикации с сервера (iSCSI SendTargets), <c>host:port</c>; пусто — <see cref="PortalAddress"/>.
    /// </summary>
    public string DiscoveryAddress { get; set; } = "";

    public int DiscoveryTimeoutMs { get; set; } = 5000;

    /// <summary>Пауза перед каждой проверкой: iscsi-scstd узнаёт о включённом таргете от ядра не мгновенно.</summary>
    public int VerifyDelayMs { get; set; } = 1000;

    /// <summary>
    /// Имя, от которого сервер проверяет таргеты версий. iscsi-scstd применяет список инициаторов и к SendTargets: если
    /// группа <see cref="InitiatorGroupId"/> сужена списком IQN, это имя должно в ней быть, иначе публикация
    /// остановится на шаге verify с ошибкой о группе. Входить по нему никто не будет — только discovery.
    /// </summary>
    public string ProbeInitiatorIqn { get; set; } = "iqn.2026-10.local.clubsrv:probe";

    /// <summary>
    /// Сколько раз повторять публикацию после ошибки TrueNAS (кроме недоступности), прежде чем пометить failed. Провал
    /// шага verify (таргет не виден и после reload-ов, группа не пускает проверку) не повторяется — failed сразу.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Фоновый исполнитель (в тестах выключается, проход вызывается явно).</summary>
    public bool RunWorker { get; set; } = true;

    public int WorkerIntervalSec { get; set; } = 15;

    public int ReconcileIntervalSec { get; set; } = 300;

    /// <summary>Таргет мастер-тома для суперклиента. Без «games-» в имени: помощники не принимают его за версию библиотеки.</summary>
    public string MasterTargetName { get; set; } = "club-master";

    public string MasterExtentName { get; set; } = "clubsrv-master";

    /// <summary>Буква мастер-тома на ПК суперклиента (библиотека остаётся на своей букве, только для чтения).</summary>
    public string MasterDriveLetter { get; set; } = "M";

    /// <summary>
    /// Личный слой игр (docs/diskless-full.md §2): каждое место получает записываемый клон текущей версии со своим
    /// CHAP; при старте помощника (загрузка ПК) клон сбрасывается к <c>@clean</c>, новая версия — новый клон.
    /// Публикация — только снапшот: общих read-only томов нет. Помощники старше <see cref="PersonalMinHelper"/>
    /// назначения не получают (они не подключают том на запись).
    /// </summary>
    public bool PersonalGames { get; set; }

    /// <summary>Родитель личных клонов игр (файловая система; создаётся администратором в TrueNAS).</summary>
    public string PersonalParent { get; set; } = "tank/club/seats";

    public string PersonalMinHelper { get; set; } = "1.5.0";

    /// <summary>
    /// Ошибки настройки — при старте сервера: иначе неверный адрес портала всплыл бы только на шаге verify первой
    /// публикации. Выключенный модуль не проверяется.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        CheckPortal("PortalAddress", PortalAddress);
        if (!string.IsNullOrWhiteSpace(DiscoveryAddress))
        {
            CheckPortal("DiscoveryAddress", DiscoveryAddress); // пусто — проверка идёт к PortalAddress
        }

        if (DiscoveryTimeoutMs < 1 || VerifyDelayMs < 0)
        {
            throw new InvalidOperationException("Library:DiscoveryTimeoutMs must be positive and Library:VerifyDelayMs must not be negative");
        }

        if (PersonalGames && (string.IsNullOrWhiteSpace(PersonalParent) || !PersonalParent.Contains('/') || PersonalParent.Contains('@')
            || PersonalParent.StartsWith('/') || PersonalParent.EndsWith('/') || !Version.TryParse(PersonalMinHelper, out _)))
        {
            throw new InvalidOperationException("Library:PersonalParent must be a dataset path like 'ssd/club/seats' and Library:PersonalMinHelper a version like 1.5.0");
        }

        if (PersonalGames && PersonalParent.Split('/')[0] != MasterZvol.Split('/')[0])
        {
            // Клон ZFS живёт только в пуле своего снапшота.
            throw new InvalidOperationException($"Library:PersonalParent ({PersonalParent}) must be in the pool of Library:MasterZvol ({MasterZvol})");
        }

        if (!Diskless.DisklessEndpoints.IsInitiatorIqn(ProbeInitiatorIqn))
        {
            throw new InvalidOperationException($"Library:ProbeInitiatorIqn must be a lowercase iqn.yyyy-mm.domain[:name] name, got '{ProbeInitiatorIqn}'");
        }

        static void CheckPortal(string key, string value)
        {
            try
            {
                IscsiDiscovery.ParsePortal(value);
            }
            catch (IscsiDiscoveryException ex)
            {
                throw new InvalidOperationException($"Library:{key} must be host[:port] with port 1..65535 ({ex.Message})");
            }
        }
    }
}
