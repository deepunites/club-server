using Club.TrueNas;

namespace Club.Server.Library;

/// <summary>
/// Раскладка библиотеки на TrueNAS и параметры раздачи агентам (секция <c>Library</c> — библиотека G:). Следующие
/// библиотеки — секции <c>Libraries:&lt;ключ&gt;</c> (<see cref="BindExtras"/>): общие настройки берутся из <c>Library</c>,
/// имена объектов — из ключа (<see cref="LibraryNames"/>).
/// </summary>
public sealed class LibraryOptions
{
    /// <summary>Модуль хранилища выключен, пока TrueNAS не настроен: операции копятся в журнале, конфиг без тома.</summary>
    public bool Enabled { get; set; }

    /// <summary>Ключ библиотеки: <c>games</c> — G:, иначе ключ секции <c>Libraries</c>.</summary>
    public string Key { get; private set; } = LibraryNames.Games.Key;

    /// <summary>Секция конфигурации — для сообщений об ошибках настройки.</summary>
    public string Section { get; private set; } = "Library";

    /// <summary>Имена объектов в TrueNAS и БД. В конфигурации не задаются.</summary>
    public LibraryNames Names { get; private set; } = LibraryNames.Games;

    /// <summary>Название библиотеки в панели.</summary>
    public string Title { get; set; } = LibraryNames.Games.DefaultTitle;

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

    /// <summary>
    /// Буква мастер-тома на ПК суперклиента. По умолчанию своя (библиотека остаётся на своей букве); может совпадать с
    /// <see cref="DriveLetter"/> — см. <see cref="MasterOnLibraryLetter"/>.
    /// </summary>
    public string MasterDriveLetter { get; set; } = "M";

    /// <summary>
    /// Мастер-том на букве библиотеки (<see cref="MasterDriveLetter"/> = <see cref="DriveLetter"/> без учёта регистра):
    /// лаунчеры на ПК правки записывают пути установки <c>G:\…</c> так, как их видят места. Пока правка открыта, этот ПК
    /// тома библиотеки не получает — на его букве мастер-том (docs/master-editing.md, помощник 1.5.1+). Буквы приводит к
    /// одному виду <see cref="Validate"/>: сравнивается и уходит помощнику одно и то же.
    /// </summary>
    public bool MasterOnLibraryLetter => string.Equals(MasterDriveLetter, DriveLetter, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Самый старый помощник, который умеет вторую и следующие библиотеки (свои пути API, свои метки).</summary>
    public const string ExtraMinHelper = "1.6.0";

    /// <summary>Помощник этой версии умеет личный слой (<see cref="PersonalMinHelper"/>); суффиксы -beta, +build не считаются.</summary>
    public bool SupportsHelper(string helperVersion) =>
        Version.TryParse(helperVersion.Split('-', '+')[0], out var have) && Version.TryParse(PersonalMinHelper, out var need) && have >= need;

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

        // Буква — одна, D…Z (контракт: ^[D-Z]$; помощник берёт первый символ). Пробелы и регистр из конфигурации
        // убираются здесь, а «G:» или « » — ошибка при старте, а не том на непонятной букве.
        DriveLetter = CheckLetter(Section, "DriveLetter", DriveLetter);
        MasterDriveLetter = CheckLetter(Section, "MasterDriveLetter", MasterDriveLetter);
        CheckPortal(Section, "PortalAddress", PortalAddress);
        if (!string.IsNullOrWhiteSpace(DiscoveryAddress))
        {
            CheckPortal(Section, "DiscoveryAddress", DiscoveryAddress); // пусто — проверка идёт к PortalAddress
        }

        if (DiscoveryTimeoutMs < 1 || VerifyDelayMs < 0)
        {
            throw new InvalidOperationException($"{Section}:DiscoveryTimeoutMs must be positive and {Section}:VerifyDelayMs must not be negative");
        }

        if (PersonalGames && (string.IsNullOrWhiteSpace(PersonalParent) || !PersonalParent.Contains('/') || PersonalParent.Contains('@')
            || PersonalParent.StartsWith('/') || PersonalParent.EndsWith('/') || !Version.TryParse(PersonalMinHelper, out _)))
        {
            throw new InvalidOperationException($"{Section}:PersonalParent must be a dataset path like 'ssd/club/seats' and {Section}:PersonalMinHelper a version like 1.5.0");
        }

        if (PersonalGames && PersonalParent.Split('/')[0] != MasterZvol.Split('/')[0])
        {
            // Клон ZFS живёт только в пуле своего снапшота.
            throw new InvalidOperationException($"{Section}:PersonalParent ({PersonalParent}) must be in the pool of {Section}:MasterZvol ({MasterZvol})");
        }

        if (!Diskless.DisklessEndpoints.IsInitiatorIqn(ProbeInitiatorIqn))
        {
            throw new InvalidOperationException($"{Section}:ProbeInitiatorIqn must be a lowercase iqn.yyyy-mm.domain[:name] name, got '{ProbeInitiatorIqn}'");
        }

        static string CheckLetter(string section, string key, string? value)
        {
            var letter = value?.Trim().ToUpperInvariant() ?? "";
            return letter is [>= 'D' and <= 'Z'] ? letter : throw new InvalidOperationException($"{section}:{key} must be a single drive letter D..Z, got '{value}'");
        }

        static void CheckPortal(string section, string key, string value)
        {
            try
            {
                IscsiDiscovery.ParsePortal(value);
            }
            catch (IscsiDiscoveryException ex)
            {
                throw new InvalidOperationException($"{section}:{key} must be host[:port] with port 1..65535 ({ex.Message})");
            }
        }
    }

    // ---- вторая и следующие библиотеки ---------------------------------------------------------------------------

    /// <summary>
    /// Библиотеки из секции <c>Libraries</c> (env <c>Libraries__solo__…</c>) поверх общих настроек <paramref name="main"/>
    /// (после его <see cref="Validate"/>). Работает библиотека, только если включены и она, и <c>Library</c>; ошибки
    /// настройки работающей — при старте сервера, неизвестный ключ — всегда.
    /// </summary>
    public static IReadOnlyList<LibraryOptions> BindExtras(IConfigurationSection libraries, LibraryOptions main)
    {
        var extras = new List<LibraryOptions>();
        foreach (var section in libraries.GetChildren())
        {
            var options = main.CloneShared();
            section.Bind(options);
            options.ApplyKey(section.Key);
            options.Enabled = options.Enabled && main.Enabled;
            extras.Add(options);
        }

        foreach (var extra in extras.Where(e => e.Enabled))
        {
            extra.Validate();
            extra.ValidateExtra(main, extras.Where(e => e != extra && e.Enabled).ToList());
        }

        return extras;
    }

    /// <summary>
    /// Общие настройки (портал, группа инициаторов, проверка, повторы) — копия этих; свои у библиотеки пусты, их задаёт
    /// её секция. Имена мастер-тома пусты нарочно: <see cref="ApplyKey"/> отличит заданные в конфигурации.
    /// </summary>
    public LibraryOptions CloneShared() => new()
    {
        PortalId = PortalId,
        InitiatorGroupId = InitiatorGroupId,
        PortalAddress = PortalAddress,
        DiscoveryAddress = DiscoveryAddress,
        DiscoveryTimeoutMs = DiscoveryTimeoutMs,
        VerifyDelayMs = VerifyDelayMs,
        ProbeInitiatorIqn = ProbeInitiatorIqn,
        MaxAttempts = MaxAttempts,
        RunWorker = RunWorker,
        WorkerIntervalSec = WorkerIntervalSec,
        ReconcileIntervalSec = ReconcileIntervalSec,
        ExtentAttempts = ExtentAttempts,
        ExtentRetryDelayMs = ExtentRetryDelayMs,
        Title = "",
        MasterZvol = "",
        PublishedParent = "",
        PersonalParent = "",
        DriveLetter = "",
        MasterDriveLetter = "",
        MasterTargetName = "",
        MasterExtentName = "",
        PersonalGames = true,
        PersonalMinHelper = ExtraMinHelper,
    };

    /// <summary>
    /// Ключ библиотеки из <see cref="CloneShared"/>: имена — из ключа, название по умолчанию, буква мастер-тома по
    /// умолчанию — буква библиотеки (правка на месте тома, как у G: в клубе).
    /// </summary>
    public void ApplyKey(string key)
    {
        var names = LibraryNames.For(key);
        if (!LibrarySchemas.Known.Contains(key))
        {
            throw new InvalidOperationException(
                $"Libraries:{key}: unknown library (known: {string.Join(", ", LibrarySchemas.Known)}); every library needs its own database schema");
        }

        Key = key;
        Section = $"Libraries:{key}";
        Names = names;
        if (MasterTargetName.Length > 0 || MasterExtentName.Length > 0)
        {
            throw new InvalidOperationException($"{Section}:MasterTargetName and {Section}:MasterExtentName come from the library key and must not be set");
        }

        MasterTargetName = names.MasterTargetName;
        MasterExtentName = names.MasterExtentName;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = names.DefaultTitle;
        }

        if (string.IsNullOrWhiteSpace(MasterDriveLetter))
        {
            MasterDriveLetter = DriveLetter;
        }
    }

    /// <summary>
    /// Проверки второй и следующих библиотек (после <see cref="Validate"/>): только личный слой и только помощники
    /// <see cref="ExtraMinHelper"/>+; буквы не заняты G: и другими библиотеками; датасеты не совпадают и не вложены в
    /// чужие; имена не попадают под метки G:, бездиска и других библиотек.
    /// </summary>
    public void ValidateExtra(LibraryOptions main, IReadOnlyCollection<LibraryOptions> others)
    {
        if (!PersonalGames)
        {
            // Общий read-only том — таргет на версию для всех ПК; вторая библиотека его не умеет и не должна.
            throw new InvalidOperationException($"{Section}:PersonalGames must be true: a second library is personal games only");
        }

        if (!Version.TryParse(PersonalMinHelper, out var min) || min < Version.Parse(ExtraMinHelper))
        {
            throw new InvalidOperationException($"{Section}:PersonalMinHelper must be {ExtraMinHelper} or newer: older helpers know only the G: library, got '{PersonalMinHelper}'");
        }

        foreach (var (name, value) in new[] { ("MasterZvol", MasterZvol), ("PersonalParent", PersonalParent), ("PublishedParent", PublishedParent) })
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Contains('/') || value.Contains('@') || value.StartsWith('/') || value.EndsWith('/'))
            {
                throw new InvalidOperationException($"{Section}:{name} must be a dataset path like 'hdd/club/lib', got '{value}'");
            }
        }

        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in others.Prepend(main))
        {
            taken.TryAdd(library.DriveLetter, library.Section);
            taken.TryAdd(library.MasterDriveLetter, library.Section);
        }

        foreach (var letter in new[] { DriveLetter, MasterDriveLetter }.Distinct())
        {
            if (taken.TryGetValue(letter, out var owner))
            {
                // Мастер-том вытесняет на ПК правки том своей библиотеки — на чужой букве он отнял бы чужой том.
                throw new InvalidOperationException($"{Section}: drive letter {letter} is already used by {owner} (DriveLetter or MasterDriveLetter)");
            }
        }

        var mine = new[] { MasterZvol, PersonalParent, PublishedParent };
        foreach (var library in others.Prepend(main))
        {
            foreach (var theirs in library.UsedDatasets())
            {
                if (mine.FirstOrDefault(m => m == theirs || m.StartsWith(theirs + "/", StringComparison.Ordinal) || theirs.StartsWith(m + "/", StringComparison.Ordinal)) is { } clash)
                {
                    throw new InvalidOperationException($"{Section}: dataset {clash} overlaps {theirs} of {library.Section}");
                }
            }
        }

        if (Names.Problems() is { Count: > 0 } problems)
        {
            throw new InvalidOperationException($"{Section}: unsafe names: {string.Join("; ", problems)}");
        }
    }

    /// <summary>Датасеты, с которыми библиотека работает: мастер-том и родитель клонов своего режима.</summary>
    private IEnumerable<string> UsedDatasets() =>
        PersonalGames ? [MasterZvol, PersonalParent] : [MasterZvol, PublishedParent];
}
