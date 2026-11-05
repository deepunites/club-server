namespace Club.Server.Saves;

/// <summary>
/// Сохранения игр игроков (секция <c>Saves</c>, docs/superpowers/specs/2026-11-05-player-saves-design.md). Корень —
/// общий диск сохранений, смонтированный на ВМ; без файла-метки в нём API сохранений отвечает 503: на системный диск
/// ВМ ничего не пишется.
/// </summary>
public sealed class SavesOptions
{
    /// <summary>Файл-метка в корне: есть — диск сохранений смонтирован.</summary>
    public const string MarkerFile = ".club-saves";

    public bool Enabled { get; set; }

    /// <summary>Точка монтирования диска сохранений: <c>&lt;playerId&gt;/</c>, <c>&lt;playerId&gt;.prev/</c>, <c>.tmp/</c>.</summary>
    public string Root { get; set; } = "/srv/club/saves";
}
