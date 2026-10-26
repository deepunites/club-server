using Club.Server.Data;
using Club.Server.Diskless;
using Club.TrueNas;
using Dapper;
using Npgsql;

namespace Club.Server.Library;

/// <summary>
/// Одна библиотека игр: настройки и свои экземпляры классов библиотеки. У G: (<see cref="LibrarySet.Main"/>) — те же
/// singleton-ы, что в DI без ключа; у остальных — поверх источника соединений своей схемы. <see cref="Machines"/> —
/// состояние ПК по библиотеке; у G: оно в <c>machines</c> (null).
/// </summary>
public sealed record LibrarySlot(
    LibraryOptions Options,
    LibraryRepository Repository,
    MasterRepository Master,
    MasterEditor Editor,
    LibraryPublisher Publisher,
    SeatGamesRepository SeatsRepository,
    SeatGames Seats,
    LibraryMachineRepository? Machines,
    NpgsqlDataSource Database)
{
    public string Key => Options.Key;

    public string Title => Options.Title;

    public LibraryNames Names => Options.Names;
}

/// <summary>
/// Библиотеки игр сервера: G: (<see cref="Main"/>, секция <c>Library</c>) и следующие (<see cref="Extras"/>, секции
/// <c>Libraries:&lt;ключ&gt;</c>). Сбой следующей библиотеки G: не задевает: своя схема БД, свои имена в TrueNAS, свой
/// проход воркера; при неверной схеме она выключается при старте (<see cref="CheckSchemasAsync"/>).
/// </summary>
public sealed class LibrarySet(LibrarySlot main, IReadOnlyList<LibrarySlot> extras, ILogger<LibrarySet> logger)
{
    /// <summary>Таблицы библиотеки в её схеме (search_path находит их первыми).</summary>
    public static readonly IReadOnlyList<string> LibraryTables = ["library_versions", "library_state", "storage_operations", "library_master", "seat_games", "machine_library"];

    /// <summary>Общие таблицы: у библиотеки search_path должен находить те же, что у G:.</summary>
    public static readonly IReadOnlyList<string> SharedTables = ["machines", "storage_warnings"];

    public LibrarySlot Main { get; } = main;

    /// <summary>Все настроенные библиотеки, кроме G:, в том числе выключенные.</summary>
    public IReadOnlyList<LibrarySlot> Extras { get; } = extras;

    /// <summary>Работающая библиотека с этим ключом (не G:); выключенная или неизвестная — null.</summary>
    public LibrarySlot? Find(string key) => Extras.FirstOrDefault(s => s.Key == key && s.Options.Enabled);

    /// <summary>Экземпляры библиотек: G: — из DI без ключа, остальные — своими руками поверх keyed источника соединений.</summary>
    public static LibrarySet Create(IServiceProvider services, IReadOnlyList<LibraryOptions> extras)
    {
        var main = new LibrarySlot(
            services.GetRequiredService<LibraryOptions>(),
            services.GetRequiredService<LibraryRepository>(),
            services.GetRequiredService<MasterRepository>(),
            services.GetRequiredService<MasterEditor>(),
            services.GetRequiredService<LibraryPublisher>(),
            services.GetRequiredService<SeatGamesRepository>(),
            services.GetRequiredService<SeatGames>(),
            Machines: null,
            services.GetRequiredService<NpgsqlDataSource>());
        var storage = services.GetRequiredService<TrueNasStorage>();
        var machines = services.GetRequiredService<MachineRepository>();
        var diskless = services.GetRequiredService<DisklessOptions>();
        var clock = services.GetRequiredService<TimeProvider>();
        var logs = services.GetRequiredService<ILoggerFactory>();
        var slots = new List<LibrarySlot>();
        foreach (var options in extras)
        {
            var key = options.Key;
            var db = services.GetRequiredKeyedService<NpgsqlDataSource>(key);
            var repository = new LibraryRepository(db);
            var master = new MasterRepository(db, options.Names.MachineStateTable);
            var verifier = new TargetVerifier(storage, options, clock, new LibraryLogger<TargetVerifier>(logs, key));
            var editor = new MasterEditor(master, repository, machines, storage, verifier, options, clock, new LibraryLogger<MasterEditor>(logs, key));
            var publisher = new LibraryPublisher(repository, master, editor, storage, verifier, options, clock, new LibraryLogger<LibraryPublisher>(logs, key));
            var seatsRepository = new SeatGamesRepository(db);
            var seats = new SeatGames(seatsRepository, repository, storage, verifier, options, diskless, clock, new LibraryLogger<SeatGames>(logs, key));
            slots.Add(new LibrarySlot(options, repository, master, editor, publisher, seatsRepository, seats, new LibraryMachineRepository(db), db));
        }

        return new LibrarySet(main, slots, services.GetRequiredService<ILogger<LibrarySet>>());
    }

    /// <summary>
    /// Проверка при старте (после миграций): у каждой работающей библиотеки search_path находит её таблицы в её схеме, а
    /// общие — там же, где G:. Иначе тот же SQL молча пошёл бы в таблицы G:: библиотека выключается до перезапуска, G:
    /// работает.
    /// </summary>
    public async Task CheckSchemasAsync(CancellationToken ct = default)
    {
        foreach (var slot in Extras.Where(s => s.Options.Enabled))
        {
            string? problem;
            try
            {
                problem = await SchemaProblemAsync(Main.Database, slot.Database, slot.Key, ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                problem = ex.Message;
            }

            if (problem is not null)
            {
                slot.Options.Enabled = false;
                logger.LogError("Library {Library} is disabled until the server restarts: {Problem}", slot.Key, problem);
            }
        }
    }

    /// <summary>Что не так со схемой библиотеки <paramref name="key"/> (null — всё на месте).</summary>
    public static async Task<string?> SchemaProblemAsync(NpgsqlDataSource main, NpgsqlDataSource library, string key, CancellationToken ct = default)
    {
        var mine = await ResolveAsync(library, [.. LibraryTables, .. SharedTables], ct);
        var shared = await ResolveAsync(main, SharedTables, ct);
        var problems = new List<string>();
        foreach (var table in LibraryTables)
        {
            if (mine[table].Schema != key)
            {
                problems.Add($"{table} resolves to {mine[table].Schema ?? "nothing"}, expected schema {key} (migration 2026102601 not applied?)");
            }
        }

        foreach (var table in SharedTables)
        {
            if (mine[table].Oid is null || mine[table].Oid != shared[table].Oid)
            {
                problems.Add($"{table} resolves to {mine[table].Schema ?? "nothing"}.{table}, not to the table of the G: library ({shared[table].Schema ?? "nothing"}.{table})");
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static async Task<Dictionary<string, (string? Schema, long? Oid)>> ResolveAsync(NpgsqlDataSource db, IReadOnlyList<string> tables, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<(string Name, string? Schema, long? Oid)>(
            """
            SELECT t.name, n.nspname, c.oid::bigint
            FROM unnest(@tables) AS t(name)
            LEFT JOIN pg_class c ON c.oid = to_regclass(t.name)
            LEFT JOIN pg_namespace n ON n.oid = c.relnamespace
            """,
            new { tables = tables.ToArray() });
        return rows.ToDictionary(r => r.Name, r => (r.Schema, r.Oid));
    }
}

/// <summary>
/// Журнал классов библиотеки не-G: под своей категорией (<c>Club.Server.Library.SeatGames.solo</c>): в журнале сервера
/// сообщения двух библиотек не смешиваются, а тексты те же, что у G:.
/// </summary>
public sealed class LibraryLogger<T>(ILoggerFactory factory, string key) : ILogger<T>
{
    private readonly ILogger _inner = factory.CreateLogger($"{typeof(T).FullName}.{key}");

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
