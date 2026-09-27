using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>Реестр ПК, учётные данные агентов, очередь команд, зоны с политиками.</summary>
[Migration(2026092701, "agents: zones, pcs, refresh tokens, commands")]
public sealed class M0001_Agents : Migration
{
    // Политика зоны по умолчанию. Веб-фильтр выключен: блокировка по IP задевает CDN лаунчеров
    // (club-contracts/docs/SHELL_CHANGES.md п. 13). Список обязательных античитов пуст: агент применяет его
    // к любой игре с античитом, а каталог игр ещё не заполнен.
    private const string DefaultPolicy = """
        {
          "shellReplacement": { "enabled": true, "shellExe": "C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe" },
          "processAllowlist": { "mode": "deny", "patterns": ["cmd.exe", "powershell.exe", "pwsh.exe", "regedit.exe", "taskmgr.exe", "mmc.exe", "control.exe", "msconfig.exe", "wscript.exe", "cscript.exe", "mshta.exe"] },
          "usb": { "allowStorage": false, "allowHid": true },
          "webFilter": { "enabled": false, "blockedDomains": [], "allowedDomains": [], "dnsServers": [] },
          "explorer": { "disableTaskManager": true, "disableRun": true, "disableSettings": true, "hideTaskbar": true, "disableAltTab": true, "disableWinKey": true, "blockedKeyCombos": ["Ctrl+Shift+Esc", "Ctrl+Esc", "Win+R", "Win+E", "Win+I", "Win+D", "Win+L", "Win+Tab", "Alt+Esc", "Ctrl+Alt+Delete"] },
          "power": { "idleShutdownMin": null, "scheduledShutdown": null },
          "updates": { "channel": "stable", "autoInstall": false },
          "anticheat": { "required": [], "blockOnViolation": true },
          "kiosk": { "idleTimeoutSec": 300, "adsIntervalSec": 900, "allowVirtualKeyboard": true }
        }
        """;

    public override void Up()
    {
        Execute.Sql($"""
            CREATE TABLE zones (
                id                 text PRIMARY KEY,
                name               text NOT NULL,
                policy             jsonb NOT NULL,
                policy_version     integer NOT NULL DEFAULT 1,
                policy_updated_at  timestamptz NOT NULL DEFAULT now(),
                storage            jsonb NULL,
                config_version     integer NOT NULL DEFAULT 1
            );

            INSERT INTO zones (id, name, policy) VALUES ('standard', 'Standart', '{DefaultPolicy.Replace("'", "''")}'::jsonb);

            CREATE TABLE pcs (
                id                   uuid PRIMARY KEY,
                number               integer NOT NULL UNIQUE CHECK (number > 0),
                name                 text NOT NULL,
                zone_id              text NOT NULL REFERENCES zones(id),
                hwid                 text NOT NULL UNIQUE,
                machine_name         text NOT NULL,
                mac_address          text NOT NULL,
                ip_address           text NOT NULL,
                hardware             jsonb NOT NULL,
                approved             boolean NOT NULL,
                maintenance          boolean NOT NULL DEFAULT false,
                reported_status      text NOT NULL DEFAULT 'offline',
                agent_version        text NOT NULL,
                shell_version        text NOT NULL DEFAULT '',
                last_heartbeat_at    timestamptz NULL,
                config_version       integer NOT NULL DEFAULT 1,
                signing_secret       bytea NOT NULL,
                credentials_version  integer NOT NULL DEFAULT 1,
                created_at           timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE agent_refresh_tokens (
                token_hash           bytea PRIMARY KEY,
                pc_id                uuid NOT NULL REFERENCES pcs(id) ON DELETE CASCADE,
                credentials_version  integer NOT NULL,
                expires_at           timestamptz NOT NULL,
                used_at              timestamptz NULL,
                created_at           timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX agent_refresh_tokens_pc ON agent_refresh_tokens (pc_id);

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

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE agent_commands;
            DROP TABLE agent_refresh_tokens;
            DROP TABLE pcs;
            DROP TABLE zones;
            """);
    }
}
