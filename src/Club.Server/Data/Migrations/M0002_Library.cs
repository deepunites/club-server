using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Библиотека игр: намерения по версиям (что должно существовать), указатели текущей и откатной версии,
/// журнал операций с хранилищем и предупреждения сверки. Факт всегда перечитывается из TrueNAS.
/// </summary>
[Migration(2026092702, "library: versions, state, storage operations, storage warnings")]
public sealed class M0002_Library : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE library_versions (
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
                last_error    text NULL
            );

            -- Ровно две версии в работе: текущая и откатная.
            CREATE TABLE library_state (
                singleton            boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                current_version_id   uuid NULL REFERENCES library_versions(id),
                rollback_version_id  uuid NULL REFERENCES library_versions(id),
                updated_at           timestamptz NOT NULL DEFAULT now(),
                CHECK (current_version_id IS DISTINCT FROM rollback_version_id OR current_version_id IS NULL)
            );
            INSERT INTO library_state DEFAULT VALUES;

            CREATE TABLE storage_operations (
                id            uuid PRIMARY KEY,
                kind          text NOT NULL CHECK (kind IN ('publish', 'rollback')),
                version_id    uuid NULL REFERENCES library_versions(id),
                status        text NOT NULL CHECK (status IN ('pending', 'running', 'done', 'failed')),
                step          text NULL,
                attempts      integer NOT NULL DEFAULT 0,
                requested_by  text NULL,
                created_at    timestamptz NOT NULL DEFAULT now(),
                updated_at    timestamptz NOT NULL DEFAULT now(),
                last_error    text NULL
            );
            CREATE INDEX storage_operations_open ON storage_operations (created_at) WHERE status IN ('pending', 'running');

            CREATE TABLE storage_warnings (
                kind         text NOT NULL,
                subject      text NOT NULL,
                message      text NOT NULL,
                first_seen   timestamptz NOT NULL,
                last_seen    timestamptz NOT NULL,
                resolved_at  timestamptz NULL,
                PRIMARY KEY (kind, subject)
            );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE storage_warnings;
            DROP TABLE storage_operations;
            DROP TABLE library_state;
            DROP TABLE library_versions;
            """);
    }
}
