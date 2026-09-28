using System.Text.Json;
using System.Text.Json.Nodes;

namespace Club.Server.Network;

/// <summary>
/// <c>/etc/kea/kea-dhcp4.conf</c> из настроек сети (Kea 3.0, пакет Ubuntu). Резервации — в базе Kea (hosts backend
/// pgsql через хук <c>libdhcp_pgsql.so</c>), доступ по peer-аутентификации роли <c>_kea</c> через сокет PostgreSQL,
/// поэтому пароля в файле нет. Ставит файл администратор (нужен root); сервер клуба только генерирует.
/// [ГИПОТЕЗА: peer-доступ Kea к PostgreSQL через каталог сокета проверить на стенде.]
/// </summary>
/// <summary>Загрузка по сети для перезаливки: TFTP с ipxe-shim.efi/ipxe.efi/undionly.kpxe и HTTP-скрипт iPXE этого сервера.</summary>
public sealed record PxeBoot(string TftpServer, string ScriptUrl);

public static class KeaConfig
{
    public const string HooksDirectory = "/usr/lib/x86_64-linux-gnu/kea/hooks";

    public static string Render(NetworkSettings s, string keaDatabase = "kea", string hooksDirectory = HooksDirectory, PxeBoot? pxe = null)
    {
        var plan = new IpPlan(s);
        var config = new JsonObject
        {
            ["Dhcp4"] = new JsonObject
            {
                ["interfaces-config"] = new JsonObject { ["interfaces"] = new JsonArray(s.Interface) },
                ["control-socket"] = new JsonObject { ["socket-type"] = "unix", ["socket-name"] = "kea4-ctrl-socket" },
                ["lease-database"] = new JsonObject { ["type"] = "memfile", ["lfc-interval"] = 3600 },
                ["hosts-database"] = new JsonObject
                {
                    ["type"] = "postgresql",
                    ["name"] = keaDatabase,
                    ["user"] = "_kea",
                    ["host"] = "/var/run/postgresql",
                },
                ["hooks-libraries"] = new JsonArray(new JsonObject { ["library"] = $"{hooksDirectory}/libdhcp_pgsql.so" }),
                // Резервации ищутся по MAC; глобальных резерваций нет — только в подсети клуба.
                ["host-reservation-identifiers"] = new JsonArray("hw-address"),
                ["reservations-global"] = false,
                ["reservations-in-subnet"] = true,
                ["valid-lifetime"] = s.LeaseTimeSec,
                ["renew-timer"] = s.LeaseTimeSec / 2,
                ["rebind-timer"] = s.LeaseTimeSec * 7 / 8,
                // Ответ «я авторитетный сервер этой сети»: чужой DHCP в клубе — ошибка, её ловит детект.
                ["authoritative"] = true,
                ["subnet4"] = new JsonArray(new JsonObject
                {
                    ["id"] = s.KeaSubnetId,
                    ["subnet"] = s.Subnet,
                    ["interface"] = s.Interface,
                    ["pools"] = new JsonArray(new JsonObject { ["pool"] = $"{s.PoolStart} - {s.PoolEnd}" }),
                    ["evaluate-additional-classes"] = pxe is null ? null : new JsonArray(ReimageUefi, ReimageBios, ReimageIpxe),
                    ["option-data"] = new JsonArray(
                        new JsonObject { ["name"] = "routers", ["data"] = s.Gateway },
                        new JsonObject { ["name"] = "domain-name-servers", ["data"] = string.Join(", ", s.DnsServers) }),
                    ["user-context"] = new JsonObject
                    {
                        ["clubsrv"] = new JsonObject
                        {
                            ["generatedBy"] = "club-server",
                            // Только ASCII: парсер Kea не понимает \u-escape не-ASCII символов (проверено на 3.0.3).
                            ["reservedRange"] = $"{s.ReservedStart} + seat - 1",
                            ["mask"] = plan.Mask,
                        },
                    },
                }),
                ["client-classes"] = pxe is null ? null : PxeClasses(pxe),
                ["loggers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "kea-dhcp4",
                    ["output-options"] = new JsonArray(new JsonObject { ["output"] = "stdout" }),
                    ["severity"] = "INFO",
                }),
            },
        };

        RemoveNulls(config);
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private const string ReimageUefi = "club-reimage-uefi";
    private const string ReimageBios = "club-reimage-bios";
    private const string ReimageIpxe = "club-reimage-ipxe";

    /// <summary>
    /// PXE только для машин, поставленных на перезаливку: их резервация несёт класс <c>club-reimage</c>
    /// (<see cref="KeaHostSync.ReimageClass"/>). Прошивка UEFI x64 (option 93 = 7 или 9) получает по TFTP ipxe-shim.efi —
    /// shim, подписанный Microsoft (UEFI CA 2011), который грузит подписанный iPXE ipxe.efi (и с Secure Boot, и без);
    /// BIOS (0) — undionly.kpxe, сам iPXE (user class «iPXE») — HTTP-скрипт. Классы из резервации видны только
    /// «дополнительным» классам (<c>only-in-additional-list</c>), поэтому они перечислены в подсети.
    /// Имя файла в самой резервации не годится: оно перекрывает класс, и iPXE грузил бы сам себя по кругу
    /// (проверено на Kea 3.0.3, ImagingTests.Kea_gives_boot_files_only_to_armed_machines).
    /// </summary>
    private static JsonArray PxeClasses(PxeBoot pxe) => new(
        new JsonObject { ["name"] = KeaHostSync.ReimageClass },
        new JsonObject { ["name"] = "club-ipxe", ["test"] = "substring(option[77].hex,0,4) == 'iPXE'" },
        new JsonObject
        {
            ["name"] = ReimageUefi,
            ["test"] = $"member('{KeaHostSync.ReimageClass}') and not member('club-ipxe') and (option[93].hex == 0x0007 or option[93].hex == 0x0009)",
            ["only-in-additional-list"] = true,
            ["next-server"] = pxe.TftpServer,
            ["boot-file-name"] = Imaging.BootFiles.Shim,
        },
        new JsonObject
        {
            ["name"] = ReimageBios,
            ["test"] = $"member('{KeaHostSync.ReimageClass}') and not member('club-ipxe') and option[93].hex == 0x0000",
            ["only-in-additional-list"] = true,
            ["next-server"] = pxe.TftpServer,
            ["boot-file-name"] = "undionly.kpxe",
        },
        new JsonObject
        {
            ["name"] = ReimageIpxe,
            ["test"] = $"member('{KeaHostSync.ReimageClass}') and member('club-ipxe')",
            ["only-in-additional-list"] = true,
            ["boot-file-name"] = pxe.ScriptUrl,
        });

    private static void RemoveNulls(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                obj.Remove(key);
            }

            foreach (var child in obj.Select(p => p.Value).ToList())
            {
                RemoveNulls(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                RemoveNulls(child);
            }
        }
    }
}
