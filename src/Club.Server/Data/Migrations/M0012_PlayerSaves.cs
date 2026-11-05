using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Сохранения игр игроков (spec 2026-11-05, п. 3.2): настройки (одна строка), общий список папок, последняя версия
/// каждого игрока и аренда «игрок X на ПК M». Сами файлы — на диске сохранений (<c>Saves:Root</c>), не в базе.
/// </summary>
[Migration(2026110501, "player saves")]
public sealed class M0012_PlayerSaves : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE save_settings (
                singleton  boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                limit_mb   integer NOT NULL DEFAULT 200 CHECK (limit_mb BETWEEN 1 AND 2000),
                excludes   text[] NOT NULL DEFAULT ARRAY['Logs', 'Crashes', 'CrashDumps', '*.dmp']::text[]
            );
            INSERT INTO save_settings DEFAULT VALUES;

            -- Шаблон пути (%APPDATA%\…, H:\…) проверяет панель (Club.Saves.SavePathTemplate); здесь — только длины.
            CREATE TABLE save_entries (
                id          uuid PRIMARY KEY,
                name        text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 60),
                path        text NOT NULL CHECK (char_length(path) BETWEEN 1 AND 260),
                enabled     boolean NOT NULL DEFAULT true,
                created_at  timestamptz NOT NULL DEFAULT now()
            );

            -- ID игрока — в нижнем регистре (Club.Saves.PlayerIds). saved_at и machine_id — последней принятой версии,
            -- last_result — последней попытки (отклонённая не трогает версию).
            CREATE TABLE player_saves (
                player_id       text PRIMARY KEY CHECK (player_id ~ '^[a-z0-9-]{1,64}$'),
                size_bytes      bigint NOT NULL DEFAULT 0,
                file_count      integer NOT NULL DEFAULT 0,
                saved_at        timestamptz NULL,
                machine_id      uuid NULL REFERENCES machines (id) ON DELETE SET NULL,
                last_result     text NULL CHECK (last_result IN ('ok', 'overLimit', 'rejected', 'error')),
                last_result_at  timestamptz NULL,
                last_error      text NULL
            );

            -- Сохранение принимается только с машины, которой выданы сохранения игрока.
            CREATE TABLE save_leases (
                player_id    text PRIMARY KEY CHECK (player_id ~ '^[a-z0-9-]{1,64}$'),
                machine_id   uuid NOT NULL REFERENCES machines (id) ON DELETE CASCADE,
                attached_at  timestamptz NOT NULL DEFAULT now()
            );
            """);
    }

    public override void Down()
    {
        // Файлы на диске сохранений остаются; их строки пропадают вместе с таблицами.
        Execute.Sql("""
            DROP TABLE save_leases;
            DROP TABLE player_saves;
            DROP TABLE save_entries;
            DROP TABLE save_settings;
            """);
    }
}
