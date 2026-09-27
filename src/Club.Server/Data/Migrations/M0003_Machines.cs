using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Сервер стал платформой бездиска, не зависящей от шелла (решение владельца 2026-09-27): реестр ПК агента ClubShell
/// становится реестром машин бездиска, очередь команд и политики киоска уходят к шеллу.
/// </summary>
[Migration(2026092703, "machines: diskless machine registry instead of ClubShell agent registry")]
public sealed class M0003_Machines : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            DROP TABLE agent_commands;

            ALTER TABLE pcs RENAME TO machines;
            ALTER TABLE machines RENAME COLUMN machine_name TO hostname;
            ALTER TABLE machines RENAME COLUMN agent_version TO helper_version;
            ALTER TABLE machines RENAME COLUMN last_heartbeat_at TO last_seen_at;
            ALTER TABLE machines ADD COLUMN mac_addresses text[] NOT NULL DEFAULT '{}';
            UPDATE machines SET mac_addresses = ARRAY[lower(mac_address)];
            ALTER TABLE machines DROP COLUMN mac_address;
            ALTER TABLE machines DROP COLUMN hardware;
            ALTER TABLE machines DROP COLUMN reported_status;
            ALTER TABLE machines DROP COLUMN shell_version;
            ALTER TABLE machines DROP COLUMN config_version;
            ALTER TABLE machines DROP COLUMN signing_secret;
            ALTER TABLE machines ADD COLUMN os_version text NULL;
            ALTER TABLE machines ADD COLUMN boot_time timestamptz NULL;
            -- Факты о томе с ПК (последний отчёт помощника).
            ALTER TABLE machines ADD COLUMN volume_state text NOT NULL DEFAULT 'none';
            ALTER TABLE machines ADD COLUMN volume_iqn text NULL;
            ALTER TABLE machines ADD COLUMN volume_version text NULL;
            ALTER TABLE machines ADD COLUMN volume_ro_verified boolean NULL;
            ALTER TABLE machines ADD COLUMN volume_error text NULL;
            CREATE INDEX machines_mac ON machines USING gin (mac_addresses);

            ALTER TABLE agent_refresh_tokens RENAME TO machine_refresh_tokens;
            ALTER TABLE machine_refresh_tokens RENAME COLUMN pc_id TO machine_id;

            ALTER TABLE zones DROP COLUMN policy;
            ALTER TABLE zones DROP COLUMN policy_version;
            ALTER TABLE zones DROP COLUMN policy_updated_at;
            ALTER TABLE zones DROP COLUMN storage;
            ALTER TABLE zones DROP COLUMN config_version;
            """);
    }

    public override void Down()
    {
        // Структура восстанавливается; данные удалённых колонок (политики, секреты подписи, железо) — нет:
        // после отката агенты ClubShell перерегистрируются.
        Execute.Sql("""
            ALTER TABLE zones ADD COLUMN policy jsonb NOT NULL DEFAULT '{}';
            ALTER TABLE zones ADD COLUMN policy_version integer NOT NULL DEFAULT 1;
            ALTER TABLE zones ADD COLUMN policy_updated_at timestamptz NOT NULL DEFAULT now();
            ALTER TABLE zones ADD COLUMN storage jsonb NULL;
            ALTER TABLE zones ADD COLUMN config_version integer NOT NULL DEFAULT 1;

            ALTER TABLE machine_refresh_tokens RENAME COLUMN machine_id TO pc_id;
            ALTER TABLE machine_refresh_tokens RENAME TO agent_refresh_tokens;

            DROP INDEX machines_mac;
            ALTER TABLE machines DROP COLUMN volume_error;
            ALTER TABLE machines DROP COLUMN volume_ro_verified;
            ALTER TABLE machines DROP COLUMN volume_version;
            ALTER TABLE machines DROP COLUMN volume_iqn;
            ALTER TABLE machines DROP COLUMN volume_state;
            ALTER TABLE machines DROP COLUMN boot_time;
            ALTER TABLE machines DROP COLUMN os_version;
            ALTER TABLE machines ADD COLUMN signing_secret bytea NOT NULL DEFAULT '\x00';
            ALTER TABLE machines ADD COLUMN config_version integer NOT NULL DEFAULT 1;
            ALTER TABLE machines ADD COLUMN shell_version text NOT NULL DEFAULT '';
            ALTER TABLE machines ADD COLUMN reported_status text NOT NULL DEFAULT 'offline';
            ALTER TABLE machines ADD COLUMN hardware jsonb NOT NULL DEFAULT '{}';
            ALTER TABLE machines ADD COLUMN mac_address text NOT NULL DEFAULT '';
            UPDATE machines SET mac_address = COALESCE(mac_addresses[1], '');
            ALTER TABLE machines DROP COLUMN mac_addresses;
            ALTER TABLE machines RENAME COLUMN last_seen_at TO last_heartbeat_at;
            ALTER TABLE machines RENAME COLUMN helper_version TO agent_version;
            ALTER TABLE machines RENAME COLUMN hostname TO machine_name;
            ALTER TABLE machines RENAME TO pcs;

            CREATE TABLE agent_commands (
                id          uuid PRIMARY KEY,
                pc_id       uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
                name        text NOT NULL,
                payload     jsonb NULL,
                issued_by   text NULL,
                supersedes  uuid NULL,
                created_at  timestamptz NOT NULL DEFAULT now(),
                expires_at  timestamptz NULL,
                acked_at    timestamptz NULL,
                ack         jsonb NULL
            );
            CREATE INDEX agent_commands_pending ON agent_commands (pc_id, created_at) WHERE acked_at IS NULL;
            """);
    }
}
