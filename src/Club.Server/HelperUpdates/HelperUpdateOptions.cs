namespace Club.Server.HelperUpdates;

/// <summary>
/// Самообновление помощника на ПК по кнопке в панели (секция <c>HelperUpdates</c>,
/// docs/superpowers/specs/2026-11-06-helper-self-update-design.md). Сборки кладёт администратор в <see cref="ToolsDir"/>,
/// сервер раздаёт поручения не больше чем <see cref="MaxActive"/> ПК сразу.
/// </summary>
public sealed class HelperUpdateOptions
{
    /// <summary>Сколько поручений может быть активно одновременно (п. 3.3).</summary>
    public int MaxActive { get; set; } = 3;

    /// <summary>
    /// Каталог сборок <c>ClubDisklessHelper-X.Y.Z.exe</c>. Пусто — <c>&lt;Imaging:PxeRoot&gt;/tools</c>: оттуда же ПК
    /// скачивают файл по <c>pxe/v1/files/tools/&lt;имя&gt;</c>.
    /// </summary>
    public string? ToolsDir { get; set; }

    /// <summary>Как часто работник очереди проверяет поручения и файлы сборок.</summary>
    public int WorkerIntervalSec { get; set; } = 30;

    /// <summary>ПК молчит столько минут — активное поручение без замены возвращается в очередь; после замены — <c>noReturn</c>.</summary>
    public int SilentMinutes { get; set; } = 10;

    /// <summary>Поручение ждёт свободного момента (<c>waiting</c>) дольше — место отдаётся другому ПК.</summary>
    public int WaitingMinutes { get; set; } = 30;

    /// <summary>Файл сборки моложе стольких секунд не берётся: его, возможно, ещё копируют.</summary>
    public int MinFileAgeSec { get; set; } = 60;

    /// <summary>
    /// Самая старая сборка, которую можно поставить кнопкой: начиная с неё помощник умеет подтверждение и откат. Старше —
    /// «только вручную».
    /// </summary>
    public string MinTargetVersion { get; set; } = "1.8.0";
}
