using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Вторая библиотека игр <c>solo</c> (H:, docs/master-editing.md): своя схема с копиями таблиц библиотеки на день
/// миграции (M0002 + M0007 + M0008 + M0009 — те же столбцы, проверки, имена ограничений и индексы) и состояние ПК по ней
/// (<c>machine_library</c>; у G: оно в <c>machines</c>). Репозитории библиотеки работают с ней тем же SQL через
/// search_path <c>solo, …</c> (Database.AddLibraryDataSource); <c>machines</c> и <c>storage_warnings</c> — общие, в public.
/// Для следующей библиотеки — скопировать эту DDL в новую миграцию со своим ключом (эту не вызывать). Любое изменение
/// таблиц библиотеки отныне делается в каждой схеме библиотеки: MigrationTests сверяет их с public.
/// </summary>
[Migration(2026102601, "solo: second games library (H:)")]
public sealed class M0011_SoloLibrary : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE SCHEMA solo;

            CREATE TABLE solo.library_versions (
                id            uuid PRIMARY KEY,
                label         text NOT NULL UNIQUE CHECK (label ~ '^[a-z0-9][a-z0-9-]{0,39}$'),
                -- publishing → published → retiring → retired; failed — публикация остановлена с ошибкой
                state         text NOT NULL CHECK (state IN ('publishing', 'published', 'retiring', 'retired', 'failed')),
                snapshot_id   text NOT NULL,
                clone_id      text NOT NULL,
                extent_name   text NOT NULL,
                target_name   text NOT NULL,
                target_iqn    text NULL,
                created_at    timestamptz NOT NULL DEFAULT now(),
                published_at  timestamptz NULL,
                retired_at    timestamptz NULL,
                last_error    text NULL,
                contents      jsonb NULL,
                contents_at   timestamptz NULL
            );

            CREATE TABLE solo.library_state (
                singleton            boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                current_version_id   uuid NULL REFERENCES solo.library_versions(id),
                rollback_version_id  uuid NULL REFERENCES solo.library_versions(id),
                updated_at           timestamptz NOT NULL DEFAULT now(),
                CHECK (current_version_id IS DISTINCT FROM rollback_version_id OR current_version_id IS NULL)
            );
            INSERT INTO solo.library_state DEFAULT VALUES;

            CREATE TABLE solo.storage_operations (
                id            uuid PRIMARY KEY,
                kind          text NOT NULL CONSTRAINT storage_operations_kind_check CHECK (kind IN ('publish', 'rollback', 'masterOpen', 'masterClose')),
                version_id    uuid NULL REFERENCES solo.library_versions(id),
                status        text NOT NULL CHECK (status IN ('pending', 'running', 'done', 'failed')),
                step          text NULL,
                attempts      integer NOT NULL DEFAULT 0,
                requested_by  text NULL,
                created_at    timestamptz NOT NULL DEFAULT now(),
                updated_at    timestamptz NOT NULL DEFAULT now(),
                last_error    text NULL
            );
            CREATE INDEX storage_operations_open ON solo.storage_operations (created_at) WHERE status IN ('pending', 'running');

            CREATE TABLE solo.library_master (
                singleton          boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                state              text NOT NULL DEFAULT 'closed' CHECK (state IN ('closed', 'opening', 'open', 'closing', 'failed')),
                machine_id         uuid NULL REFERENCES machines (id) ON DELETE SET NULL,
                initiator_iqn      text NULL,
                chap_user          text NULL,
                chap_secret        text NULL,
                auth_tag           integer NULL,
                target_iqn         text NULL,
                force_close        boolean NOT NULL DEFAULT false,
                dirty              boolean NOT NULL DEFAULT false,
                opened_at          timestamptz NULL,
                close_requested_at timestamptz NULL,
                closed_at          timestamptz NULL,
                last_error         text NULL,
                updated_at         timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO solo.library_master DEFAULT VALUES;

            CREATE TABLE solo.seat_games (
                machine_id       uuid PRIMARY KEY REFERENCES machines (id) ON DELETE CASCADE,
                zvol             text NOT NULL,
                target_name      text NOT NULL UNIQUE,
                initiator_iqn    text NOT NULL,
                base_snapshot    text NULL,
                library_version  text NULL,
                chap_user        text NOT NULL,
                chap_secret      text NOT NULL,
                auth_tag         integer NULL,
                target_iqn       text NULL,
                state            text NOT NULL DEFAULT 'new' CHECK (state IN ('new', 'ready', 'failed')),
                last_error       text NULL,
                resets           integer NOT NULL DEFAULT 0,
                last_reset_at    timestamptz NULL,
                updated_at       timestamptz NOT NULL DEFAULT now()
            );

            -- Отчёт помощника 1.6+ об этой библиотеке на ПК (у G: — столбцы volume_*/master_* в machines).
            CREATE TABLE solo.machine_library (
                machine_id      uuid PRIMARY KEY REFERENCES machines (id) ON DELETE CASCADE,
                volume_state    text NOT NULL DEFAULT 'none',
                volume_iqn      text NULL,
                volume_version  text NULL,
                volume_error    text NULL,
                master_state    text NULL,
                master_error    text NULL,
                reported_at     timestamptz NOT NULL DEFAULT now()
            );
            """);
    }

    public override void Down()
    {
        // Объекты в TrueNAS остаются (solo-*, club-master-solo); их записи пропадают вместе со схемой.
        Execute.Sql("""
            DELETE FROM storage_warnings WHERE source = 'solo';
            DROP SCHEMA solo CASCADE;
            """);
    }
}
