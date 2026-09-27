using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Сеть клуба для DHCP (Kea): подсеть, пул, диапазон резерваций по номерам мест, наш DHCP-сервер. От помощников —
/// какие DHCP-серверы видит каждый ПК (детект чужого DHCP). Предупреждения разделены по источнику.
/// </summary>
[Migration(2026092801, "network: DHCP settings, reported DHCP servers, warning sources")]
public sealed class M0004_Network : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE network_settings (
                singleton        boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                -- Сеть не настроена, пока администратор не сохранил параметры: Kea-резервации не пишутся.
                configured       boolean NOT NULL DEFAULT false,
                subnet           cidr NOT NULL DEFAULT '192.168.77.0/24',
                kea_subnet_id    integer NOT NULL DEFAULT 1 CHECK (kea_subnet_id > 0),
                interface        text NOT NULL DEFAULT 'eth0',
                dhcp_server      inet NOT NULL DEFAULT '192.168.77.1',
                gateway          inet NOT NULL DEFAULT '192.168.77.1',
                dns_servers      inet[] NOT NULL DEFAULT '{192.168.77.1}',
                pool_start       inet NOT NULL DEFAULT '192.168.77.200',
                pool_end         inet NOT NULL DEFAULT '192.168.77.250',
                reserved_start   inet NOT NULL DEFAULT '192.168.77.101',
                lease_time_sec   integer NOT NULL DEFAULT 43200 CHECK (lease_time_sec BETWEEN 300 AND 604800),
                updated_at       timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO network_settings DEFAULT VALUES;

            ALTER TABLE machines ADD COLUMN dhcp_servers text[] NULL;

            ALTER TABLE storage_warnings ADD COLUMN source text NOT NULL DEFAULT 'library';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE storage_warnings DROP COLUMN source;
            ALTER TABLE machines DROP COLUMN dhcp_servers;
            DROP TABLE network_settings;
            """);
    }
}
