namespace Club.Server.Imaging;

public sealed class ImagingOptions
{
    /// <summary>Образы Windows и перезаливка по PXE включены.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Каталог образов на машине сервера: <c>incoming/</c> — сюда администратор кладёт install.wim (scp/SMB),
    /// <c>images/&lt;версия&gt;/install.wim</c> — импортированные. Может быть смонтированным датасетом TrueNAS.
    /// </summary>
    public string Root { get; set; } = "/srv/club/images";

    /// <summary>Файлы загрузки WinPE для wimboot: <c>wimboot</c>, <c>boot/BCD</c>, <c>boot/boot.sdi</c>, <c>sources/boot.wim</c>.</summary>
    public string PxeRoot { get; set; } = "/srv/club/pxe";

    /// <summary>
    /// Каталог TFTP (tftpd-hpa) на этой машине: ipxe-shim.efi, ipxe.efi, undionly.kpxe. Пусто — сервер его не проверяет
    /// (TFTP на другой машине).
    /// </summary>
    public string TftpRoot { get; set; } = "/srv/tftp";

    /// <summary>Адрес этого сервера для ПК по HTTP (iPXE и WinPE), например <c>http://192.168.77.1:5080</c>.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public bool RunWorker { get; set; } = true;

    public int WorkerIntervalSec { get; set; } = 5;
}
