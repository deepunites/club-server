using System.Text.Json;
using System.Text.Json.Nodes;

namespace Club.Server.Network;

/// <summary>
/// <c>/etc/kea/kea-dhcp4.conf</c> из настроек сети (Kea 3.0, пакет Ubuntu). Резервации — в базе Kea (hosts backend
/// pgsql через хук <c>libdhcp_pgsql.so</c>), доступ по peer-аутентификации роли <c>_kea</c> через сокет PostgreSQL,
/// поэтому пароля в файле нет. Ставит файл администратор (нужен root); сервер клуба только генерирует.
/// [ГИПОТЕЗА: peer-доступ Kea к PostgreSQL через каталог сокета проверить на стенде.]
/// </summary>
public static class KeaConfig
{
    public const string HooksDirectory = "/usr/lib/x86_64-linux-gnu/kea/hooks";

    public static string Render(NetworkSettings s, string keaDatabase = "kea", string hooksDirectory = HooksDirectory)
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
                    ["option-data"] = new JsonArray(
                        new JsonObject { ["name"] = "routers", ["data"] = s.Gateway },
                        new JsonObject { ["name"] = "domain-name-servers", ["data"] = string.Join(", ", s.DnsServers) }),
                    ["user-context"] = new JsonObject
                    {
                        ["clubsrv"] = new JsonObject
                        {
                            ["generatedBy"] = "club-server",
                            ["reservedRange"] = $"{s.ReservedStart} + номер места − 1",
                            ["mask"] = plan.Mask,
                        },
                    },
                }),
                ["loggers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "kea-dhcp4",
                    ["output-options"] = new JsonArray(new JsonObject { ["output"] = "stdout" }),
                    ["severity"] = "INFO",
                }),
            },
        };

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}
