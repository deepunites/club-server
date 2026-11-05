using Club.Server.Data;
using Dapper;
using Npgsql;

namespace Club.Server.Saves;

/// <summary>Общие настройки сохранений: лимит на игрока и исключения (имена папок или маски файлов, не сохраняются).</summary>
public sealed record SaveSettings(int LimitMb, IReadOnlyList<string> Excludes);

/// <summary>Папка из общего списка: <c>Path</c> — шаблон (<c>%APPDATA%\…</c>, <c>H:\…</c>), Club.Saves.SavePathTemplate.</summary>
public sealed record SaveEntry(Guid Id, string Name, string Path, bool Enabled);

/// <summary>
/// Игрок в панели: последняя принятая версия (размер, файлов, когда, с какого ПК) и последняя попытка сохранения.
/// <c>MachineName</c> — имя машины из реестра (null, если машину удалили).
/// </summary>
public sealed record PlayerSaveRow(
    string PlayerId,
    long SizeBytes,
    int FileCount,
    DateTimeOffset? SavedAt,
    Guid? MachineId,
    string? MachineName,
    string? LastResult,
    DateTimeOffset? LastResultAt,
    string? LastError);

/// <summary>
/// Таблицы сохранений (M0012_PlayerSaves). ID игрока сюда приходит уже приведённым (Club.Saves.PlayerIds.Normalize):
/// в нижнем регистре — иначе проверка таблицы его не примет.
/// </summary>
public sealed class SavesRepository(NpgsqlDataSource db)
{
    /// <summary>Значения <c>player_saves.last_result</c>.</summary>
    public static class Results
    {
        public const string Ok = "ok";
        public const string OverLimit = "overLimit";
        public const string Rejected = "rejected";
        public const string Error = "error";
    }

    static SavesRepository() => DapperSetup.Ensure();

    private const string EntryColumns = "id, name, path, enabled";

    public async Task<SaveSettings> SettingsAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        var (limitMb, excludes) = await c.QuerySingleAsync<(int, string[])>("SELECT limit_mb, excludes FROM save_settings");
        return new SaveSettings(limitMb, excludes);
    }

    /// <summary>Лимит и исключения уже проверены панелью; лимит вне 1…2000 база не примет.</summary>
    public async Task UpdateSettingsAsync(SaveSettings settings)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE save_settings SET limit_mb = @LimitMb, excludes = @excludes",
            new { settings.LimitMb, excludes = settings.Excludes.ToArray() });
    }

    /// <summary>Папки по названию; <paramref name="enabledOnly"/> — только включённые (то, что получает помощник).</summary>
    public async Task<IReadOnlyList<SaveEntry>> EntriesAsync(bool enabledOnly)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<(Guid Id, string Name, string Path, bool Enabled)>(
            $"SELECT {EntryColumns} FROM save_entries WHERE enabled OR NOT @enabledOnly ORDER BY name, id", new { enabledOnly });
        return rows.Select(r => new SaveEntry(r.Id, r.Name, r.Path, r.Enabled)).ToList();
    }

    /// <summary>Новая папка; название и путь уже проверены панелью.</summary>
    public async Task<SaveEntry> CreateEntryAsync(string name, string path, bool enabled)
    {
        var entry = new SaveEntry(Guid.NewGuid(), name, path, enabled);
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("INSERT INTO save_entries (id, name, path, enabled) VALUES (@Id, @Name, @Path, @Enabled)", entry);
        return entry;
    }

    /// <summary>Название, путь и вкл/выкл записи <c>entry.Id</c>; false — записи нет.</summary>
    public async Task<bool> UpdateEntryAsync(SaveEntry entry)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("UPDATE save_entries SET name = @Name, path = @Path, enabled = @Enabled WHERE id = @Id", entry) > 0;
    }

    /// <summary>
    /// Удаление записи из списка. Файлы этой папки в сохранениях игроков остаются до их следующего сохранения: выдача на
    /// ПК их пропускает, приём с ПК не принимает (spec 2026-11-05, Review Focus 5).
    /// </summary>
    public async Task<bool> DeleteEntryAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteAsync("DELETE FROM save_entries WHERE id = @id", new { id }) > 0;
    }

    /// <summary>
    /// Блокировка игрока на время выдачи, приёма и удаления из панели (spec п. 3.4.4): advisory-блокировка сеанса
    /// PostgreSQL на своём соединении — держится, пока результат не освобождён, и между процессами сервера тоже. Ключ —
    /// <c>hashtext</c> (int4): с ключами воркеров (bigint больше 2^31) не совпадает; совпадение хешей двух игроков лишь
    /// заставит одного подождать.
    /// </summary>
    public async Task<IAsyncDisposable> LockPlayerAsync(string playerId, CancellationToken cancellationToken = default)
    {
        var connection = await db.OpenConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_lock(hashtext('save:' || @playerId))", new { playerId }, commandTimeout: 0, cancellationToken: cancellationToken));
            return new PlayerLock(connection, playerId);
        }
        catch
        {
            // Отмена могла прийти, когда блокировка уже взята: соединение закрывается, а не уходит в пул с ней.
            NpgsqlConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Аренда «игрок на этой машине»: забирается у другой машины, если была (тот ПК пропал, не сохранив).</summary>
    public async Task TakeLeaseAsync(string playerId, Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO save_leases (player_id, machine_id, attached_at) VALUES (@playerId, @machineId, now())
            ON CONFLICT (player_id) DO UPDATE SET machine_id = excluded.machine_id, attached_at = excluded.attached_at
            """,
            new { playerId, machineId });
    }

    /// <summary>Машина, которой выданы сохранения игрока; null — никому.</summary>
    public async Task<Guid?> LeaseHolderAsync(string playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid?>("SELECT machine_id FROM save_leases WHERE player_id = @playerId", new { playerId });
    }

    /// <summary>Снять аренду, только если она у <paramref name="machineId"/>: опоздавший старый ПК не снимет аренду нового.</summary>
    public async Task ReleaseLeaseAsync(string playerId, Guid machineId)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM save_leases WHERE player_id = @playerId AND machine_id = @machineId", new { playerId, machineId });
    }

    /// <summary>Принята новая версия с машины <paramref name="machineId"/>: размер, файлов, время и результат <c>ok</c>.</summary>
    public async Task RecordSavedAsync(string playerId, Guid machineId, long sizeBytes, int fileCount)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO player_saves (player_id, size_bytes, file_count, saved_at, machine_id, last_result, last_result_at, last_error)
            VALUES (@playerId, @sizeBytes, @fileCount, now(), @machineId, @ok, now(), NULL)
            ON CONFLICT (player_id) DO UPDATE SET
                size_bytes = excluded.size_bytes, file_count = excluded.file_count, saved_at = excluded.saved_at,
                machine_id = excluded.machine_id, last_result = excluded.last_result, last_result_at = excluded.last_result_at,
                last_error = NULL
            """,
            new { playerId, sizeBytes, fileCount, machineId, ok = Results.Ok });
    }

    /// <summary>
    /// Неудачная попытка (<see cref="Results"/>: <c>overLimit</c>, <c>rejected</c>, <c>error</c>): прошлая версия и её
    /// данные не меняются; игрок без версии появляется в списке с этим результатом.
    /// </summary>
    public async Task RecordResultAsync(string playerId, string result, string? error)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO player_saves (player_id, last_result, last_result_at, last_error) VALUES (@playerId, @result, now(), @error)
            ON CONFLICT (player_id) DO UPDATE SET
                last_result = excluded.last_result, last_result_at = excluded.last_result_at, last_error = excluded.last_error
            """,
            new { playerId, result, error });
    }

    /// <summary>
    /// Страница игроков (<paramref name="page"/> с 1), последние по времени попытки — первыми. <paramref name="query"/> —
    /// подстрока ID без учёта регистра (не шаблон LIKE); пусто — все. <c>Total</c> — сколько всего подходит.
    /// </summary>
    public async Task<(IReadOnlyList<PlayerSaveRow> Items, int Total)> PlayersAsync(string? query, int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();
        await using var c = await db.OpenConnectionAsync();
        const string Where = "WHERE @q::text IS NULL OR strpos(p.player_id, @q::text) > 0";
        var total = await c.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM player_saves p {Where}", new { q });
        var rows = await c.QueryAsync<PlayerRow>(
            $"""
            SELECT p.player_id, p.size_bytes, p.file_count, p.saved_at, p.machine_id, m.name AS machine_name,
                   p.last_result, p.last_result_at, p.last_error
            FROM player_saves p LEFT JOIN machines m ON m.id = p.machine_id
            {Where}
            ORDER BY p.last_result_at DESC NULLS LAST, p.player_id
            LIMIT @pageSize OFFSET @offset
            """,
            new { q, pageSize, offset = (long)(page - 1) * pageSize });
        return (rows.Select(r => r.ToRecord()).ToList(), total);
    }

    /// <summary>Строка игрока и его аренда (файлы на диске удаляет панель под <see cref="LockPlayerAsync"/>).</summary>
    public async Task DeletePlayerAsync(string playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("DELETE FROM save_leases WHERE player_id = @playerId", new { playerId }, tx);
        await c.ExecuteAsync("DELETE FROM player_saves WHERE player_id = @playerId", new { playerId }, tx);
        await tx.CommitAsync();
    }

    /// <summary>Строка выборки для Dapper (timestamptz → DateTimeOffset через DapperSetup).</summary>
    private sealed class PlayerRow
    {
        public string PlayerId { get; init; } = "";
        public long SizeBytes { get; init; }
        public int FileCount { get; init; }
        public DateTimeOffset? SavedAt { get; init; }
        public Guid? MachineId { get; init; }
        public string? MachineName { get; init; }
        public string? LastResult { get; init; }
        public DateTimeOffset? LastResultAt { get; init; }
        public string? LastError { get; init; }

        public PlayerSaveRow ToRecord() =>
            new(PlayerId, SizeBytes, FileCount, SavedAt, MachineId, MachineName, LastResult, LastResultAt, LastError);
    }

    /// <summary>Блокировка игрока: освобождение снимает advisory-блокировку и возвращает соединение в пул.</summary>
    private sealed class PlayerLock(NpgsqlConnection connection, string playerId) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtext('save:' || @playerId))", new { playerId });
            }
            catch
            {
                // Не сняли — соединение закрывается, а не уходит в пул: блокировка сеанса снимается вместе с ним.
                NpgsqlConnection.ClearPool(connection);
                throw;
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
