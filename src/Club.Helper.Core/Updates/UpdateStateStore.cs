using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Club.Helper.Core.Updates;

/// <summary>
/// <c>update.json</c> (<see cref="UpdateState"/>) и <c>update-failed.json</c> (<see cref="FailedUpdate"/>) в
/// <see cref="UpdatePaths.StateDir"/>; папку создаёт служба (StateDirectory, права только SYSTEM и администраторов).
/// Запись — целым файлом: <c>.tmp</c>, сброс на диск (<c>Flush(true)</c>), перенос на место с заменой, — пропавшее питание
/// оставляет прежний файл или новый, но не половину. Только BCL: хранилище читает страж в начале <c>Main</c>, до хоста.
/// </summary>
public sealed class UpdateStateStore(UpdatePaths paths)
{
    /// <summary>Сколько последних поручений с ошибкой хранит <c>update-failed.json</c> (spec п. 4.2).</summary>
    public const int FailedKept = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Состояние замены; файла нет — <c>null</c> и <paramref name="unreadable"/> = <c>false</c>. Файл есть, но не читается,
    /// не JSON, другой версии (<see cref="UpdateState.V"/>), без сборок или с незнакомой фазой — <c>null</c> и
    /// <paramref name="unreadable"/> = <c>true</c> (страж тогда бережёт <c>.old</c>).
    /// </summary>
    public UpdateState? Read(out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(paths.StateFile))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(paths.StateFile), Json);
            if (state is { V: UpdateState.CurrentVersion } && !string.IsNullOrEmpty(state.FromSha) && !string.IsNullOrEmpty(state.ToSha)
                && state.ToVersion is not null && state.Phase is not null && UpdatePhases.All.Contains(state.Phase))
            {
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }

        unreadable = true;
        return null;
    }

    /// <summary>Записать состояние целым файлом (через <c>.tmp</c> со сбросом на диск).</summary>
    public void Write(UpdateState state) => WriteWhole(paths.StateFile, JsonSerializer.Serialize(state, Json));

    /// <summary>Стереть <c>update.json</c> (и <c>.tmp</c> недописанной записи, если остался).</summary>
    public void Delete()
    {
        File.Delete(paths.StateFile);
        File.Delete(paths.StateFile + ".tmp");
    }

    /// <summary>Поручения с ошибкой, старые первыми; файла нет или он испорчен — пусто.</summary>
    public IReadOnlyList<FailedUpdate> Failed()
    {
        try
        {
            if (File.Exists(paths.FailedFile) && JsonSerializer.Deserialize<List<FailedUpdate>>(File.ReadAllText(paths.FailedFile), Json) is { } list)
            {
                return [.. list.Where(f => f is { Sha256: not null, Error: not null })];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }

        return [];
    }

    /// <summary>
    /// Добавить поручение с ошибкой в конец списка: запись с тем же <see cref="FailedUpdate.Id"/> заменяется, хранятся
    /// последние <see cref="FailedKept"/>.
    /// </summary>
    public void AddFailed(FailedUpdate failed)
    {
        List<FailedUpdate> list = [.. Failed().Where(f => f.Id != failed.Id), failed];
        WriteWhole(paths.FailedFile, JsonSerializer.Serialize(list.TakeLast(FailedKept), Json));
    }

    private static void WriteWhole(string file, string text)
    {
        var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, file, overwrite: true);
    }
}
