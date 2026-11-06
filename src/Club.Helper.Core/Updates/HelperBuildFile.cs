using System.Text.Json;

namespace Club.Helper.Core.Updates;

/// <summary>
/// Содержимое <c>helper-build.json</c>: <paramref name="V"/> = <see cref="HelperBuildFile.CurrentVersion"/>,
/// <paramref name="Version"/> — версия, которую помощник сообщает серверу (<see cref="HelperOptions.HelperVersion"/>),
/// <paramref name="Build"/> — SHA256 своего exe строчными (<see cref="HelperOptions.HelperBuild"/>).
/// </summary>
public sealed record HelperBuildInfo(int V, string Version, string Build);

/// <summary>
/// <c>%ProgramData%\ClubDiskless\helper-build.json</c> (spec 2026-11-06, п. 4.5, 5.1): версия и сборка работающего
/// помощника для скриптов. Версия файла exe для них не годится: exe собирается на Linux, и ресурс версии apphost — от
/// среды .NET (10.0.x), а не наш. Скрипт верит файлу, только если SHA256 exe = <c>build</c>; иначе — своя таблица
/// известных сборок. Пишется при каждом обычном старте (после стража, не в ветке отката) целым файлом, как
/// <c>update.json</c>; неудача — <c>false</c> и строка <paramref name="warning"/>, не исключение.
/// </summary>
public static class HelperBuildFile
{
    /// <summary>Версия формата файла; другая — файл не читается.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Записать версию и SHA256 сборки; <c>true</c> — записано.</summary>
    public static bool Write(UpdatePaths paths, string version, string build, Action<string> warning)
    {
        try
        {
            var info = new HelperBuildInfo(CurrentVersion, version, build.ToLowerInvariant());
            UpdateStateStore.WriteWhole(paths.BuildFile, JsonSerializer.Serialize(info, Json));
            return true;
        }
        catch (Exception ex)
        {
            // Файл — подсказка скриптам; служба без него работает.
            warning($"helper-build.json not written ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    /// <summary>Содержимое файла; нет, не читается, не JSON, другой версии или без полей — <c>null</c>.</summary>
    public static HelperBuildInfo? Read(UpdatePaths paths)
    {
        try
        {
            if (File.Exists(paths.BuildFile)
                && JsonSerializer.Deserialize<HelperBuildInfo>(File.ReadAllText(paths.BuildFile), Json) is { V: CurrentVersion, Version: not null, Build: not null } info)
            {
                return info;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }

        return null;
    }
}
