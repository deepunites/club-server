using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace Club.Server.Data;

/// <summary>Зона клуба: группа мест («Зона 4», «VIP»). <c>Machines</c> — сколько машин реестра в ней (и неодобренные).</summary>
public sealed class ZoneRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Machines { get; init; }
}

/// <summary>
/// Зоны создаёт и называет оператор в панели. Id выдаёт сервер (<c>zone-</c> и 12 hex): не меняется при
/// переименовании и годится в URL. Названия уникальны без учёта регистра — проверка под блокировкой таблицы, сравнение
/// в .NET: <c>lower()</c> PostgreSQL зависит от локали базы (в локали C кириллицу не трогает, «vip» ≠ «VIP» поймал бы, а
/// «зона 4» ≠ «Зона 4» — нет).
/// </summary>
public sealed class ZoneRepository(NpgsqlDataSource db)
{
    /// <summary>Зона по умолчанию (M0001): в неё попадают новые машины. Удалить нельзя, переименовать можно.</summary>
    public const string DefaultId = "standard";

    public const int MaxNameLength = 40;

    /// <summary>Сколько мест можно назначить одним запросом (в клубе 65 ПК).</summary>
    public const int MaxAssign = 1000;

    static ZoneRepository() => DapperSetup.Ensure();

    public enum ChangeResult
    {
        Done,
        NotFound,
        NameTaken,
        DefaultZone,
        InUse,
    }

    public async Task<IReadOnlyList<ZoneRow>> AllAsync()
    {
        await using var c = await db.OpenConnectionAsync();
        return (await c.QueryAsync<ZoneRow>(
            "SELECT z.id, z.name, (SELECT count(*) FROM machines m WHERE m.zone_id = z.id)::int AS machines FROM zones z ORDER BY z.name, z.id")).ToList();
    }

    /// <summary>Новая зона; <paramref name="name"/> уже обрезан и проверен на длину.</summary>
    public async Task<(ChangeResult Result, ZoneRow? Zone)> CreateAsync(string name)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await LockAsync(c, tx);
        var existing = (await c.QueryAsync<(string Id, string Name)>("SELECT id, name FROM zones", transaction: tx)).ToList();
        if (existing.Any(z => Same(z.Name, name)))
        {
            return (ChangeResult.NameTaken, null);
        }

        string id;
        do
        {
            id = "zone-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        }
        while (existing.Any(z => z.Id == id));

        await c.ExecuteAsync("INSERT INTO zones (id, name) VALUES (@id, @name)", new { id, name }, tx);
        await tx.CommitAsync();
        return (ChangeResult.Done, new ZoneRow { Id = id, Name = name });
    }

    /// <summary>Переименование (и зоны по умолчанию). Смена регистра своего же названия — не конфликт.</summary>
    public async Task<ChangeResult> RenameAsync(string id, string name)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await LockAsync(c, tx);
        var existing = (await c.QueryAsync<(string Id, string Name)>("SELECT id, name FROM zones", transaction: tx)).ToList();
        if (existing.All(z => z.Id != id))
        {
            return ChangeResult.NotFound;
        }

        if (existing.Any(z => z.Id != id && Same(z.Name, name)))
        {
            return ChangeResult.NameTaken;
        }

        await c.ExecuteAsync("UPDATE zones SET name = @name WHERE id = @id", new { id, name }, tx);
        await tx.CommitAsync();
        return ChangeResult.Done;
    }

    /// <summary>
    /// Удаление пустой зоны. <c>Machines</c> — сколько машин в ней, если не пуста. Машину, которую назначили в зону
    /// одновременно с удалением, не теряем: внешний ключ machines.zone_id не даст удалить зону.
    /// </summary>
    public async Task<(ChangeResult Result, int Machines)> DeleteAsync(string id)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await LockAsync(c, tx);
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM zones WHERE id = @id)", new { id }, tx))
        {
            return (ChangeResult.NotFound, 0);
        }

        if (id == DefaultId)
        {
            return (ChangeResult.DefaultZone, 0);
        }

        var count = await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM machines WHERE zone_id = @id", new { id }, tx);
        if (count > 0)
        {
            return (ChangeResult.InUse, count);
        }

        try
        {
            await c.ExecuteAsync("DELETE FROM zones WHERE id = @id", new { id }, tx);
            await tx.CommitAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Места назначили в зону, пока удаление ждало блокировку её строки: в ответе — сколько их теперь.
            await tx.RollbackAsync();
            var now = await c.ExecuteScalarAsync<int>("SELECT count(*)::int FROM machines WHERE zone_id = @id", new { id });
            return (ChangeResult.InUse, Math.Max(now, 1));
        }

        return (ChangeResult.Done, 0);
    }

    /// <summary>
    /// Места <paramref name="numbers"/> — в зону, одной транзакцией. <c>Unknown</c> — номера, которых нет в реестре
    /// (по возрастанию). null — зоны нет.
    /// </summary>
    public async Task<(int Assigned, IReadOnlyList<int> Unknown)?> AssignAsync(string id, IReadOnlyCollection<int> numbers)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        // FOR KEY SHARE: зону не удалят, пока места переходят в неё.
        if (await c.ExecuteScalarAsync<string?>("SELECT id FROM zones WHERE id = @id FOR KEY SHARE", new { id }, tx) is null)
        {
            return null;
        }

        var found = (await c.QueryAsync<int>(
            "UPDATE machines SET zone_id = @id WHERE number = ANY(@numbers) RETURNING number", new { id, numbers = numbers.ToArray() }, tx)).ToHashSet();
        await tx.CommitAsync();
        return (found.Count, numbers.Where(n => !found.Contains(n)).Distinct().Order().ToList());
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Создание и переименование — по одному: два «VIP» одновременно не пройдут проверку оба.</summary>
    private static Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx) =>
        c.ExecuteAsync("LOCK TABLE zones IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
}
