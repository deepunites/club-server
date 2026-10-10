using System.Text;

namespace Club.Server.Diskless;

/// <summary>
/// Скрипты iPXE для бездиска. Только ASCII и без <c>${</c> в пользовательском тексте: iPXE подставляет настройки
/// в любом месте строки. Диск подключается по iSCSI с CHAP места; iPXE описывает его в iBFT — оттуда Windows
/// берёт адрес таргета, имя инициатора и CHAP, чтобы продолжить загрузку с того же диска.
/// </summary>
public static class DisklessScripts
{
    /// <summary>
    /// Повтор — заменой текущего скрипта (<c>--replace</c>): без него каждый повтор вкладывается в предыдущий, стек iPXE
    /// мал, и после десятка повторов (ожидание публикации, подготовка диска) iPXE падает или зависает.
    /// </summary>
    private const string Retry = "chain --autofree --replace /pxe/v1/machines/${netX/mac:hexhyp}/boot.ipxe";

    public static string SanBoot(int seat, string initiatorIqn, SeatDiskRow disk, string portalAddress, string what) => Script($"""
        echo Club server: seat {seat}, {Ascii(what)}
        {Connection(initiatorIqn, disk)}
        sanboot {Uri(portalAddress, disk.TargetIqn!)} || goto failed
        :failed
        echo Boot from the network disk failed. Retrying in 10 s
        sleep 10
        {Retry}
        """);

    /// <summary>
    /// Установка Windows прямо на zvol эталона: диск подключается (<c>sanhook</c>, описан в iBFT), затем установщик
    /// Windows через wimboot из <c>Imaging:PxeRoot/&lt;каталог&gt;</c> (boot/BCD, boot/boot.sdi, sources/boot.wim из ISO)
    /// с двумя файлами, которые wimboot кладёт в X:\Windows\System32 (ipxe.org/howto/winpe): winpeshl.ini запускает
    /// install.cmd — сеть, SMB-шара с ISO (в boot.wim нет install.wim), setup.exe. Всё готовит extract-winsetup.sh.
    /// </summary>
    public static string Install(int seat, string initiatorIqn, SeatDiskRow disk, string portalAddress, string setupDirectory) => Script($"""
        echo Club server: seat {seat}, installing Windows onto the system image (master mode)
        {Connection(initiatorIqn, disk)}
        sanhook --drive 0x80 {Uri(portalAddress, disk.TargetIqn!)} || goto failed
        kernel /pxe/v1/files/wimboot gui || goto failed
        initrd /pxe/v1/files/{setupDirectory}/install.cmd install.cmd || goto failed
        initrd /pxe/v1/files/{setupDirectory}/winpeshl.ini winpeshl.ini || goto failed
        initrd /pxe/v1/files/{setupDirectory}/boot/bcd BCD || goto failed
        initrd /pxe/v1/files/{setupDirectory}/boot/boot.sdi boot.sdi || goto failed
        initrd /pxe/v1/files/{setupDirectory}/sources/boot.wim boot.wim || goto failed
        boot || goto failed
        :failed
        echo Windows setup boot failed, see the server log. Retrying in 15 s
        sleep 15
        {Retry}
        """);

    public static string Wait(int seat, string reason) => Script($"""
        echo Club server: seat {seat}, {Ascii(reason)}. Retrying in 5 s
        sleep 5
        {Retry}
        """);

    public static string Failed(int seat, string error) => Script($"""
        echo Club server: seat {seat}, diskless boot failed: {Ascii(error)}
        echo Retrying in 15 s
        sleep 15
        {Retry}
        """);

    private static string Connection(string initiatorIqn, SeatDiskRow disk) => $"""
        set initiator-iqn {initiatorIqn}
        set username {disk.ChapUser}
        set password {disk.ChapSecret}
        """;

    /// <summary><c>iscsi:&lt;сервер&gt;:&lt;протокол&gt;:&lt;порт&gt;:&lt;LUN&gt;:&lt;таргет&gt;</c>, протокол и LUN 0 — по умолчанию.</summary>
    public static string Uri(string portalAddress, string targetIqn)
    {
        var colon = portalAddress.LastIndexOf(':');
        var (host, port) = colon > 0 && !portalAddress.EndsWith(']') ? (portalAddress[..colon], portalAddress[(colon + 1)..]) : (portalAddress, "3260");
        return $"iscsi:{host}::{port}::{targetIqn}";
    }

    private static string Script(string body) => "#!ipxe\n" + body.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    /// <summary>Для <c>echo</c>: только печатаемый ASCII, без <c>$</c>, не длиннее 160 символов.</summary>
    private static string Ascii(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 160));
        foreach (var ch in text)
        {
            if (sb.Length >= 160)
            {
                break;
            }

            sb.Append(ch is >= ' ' and <= '~' && ch != '$' ? ch : '?');
        }

        return sb.ToString();
    }
}
