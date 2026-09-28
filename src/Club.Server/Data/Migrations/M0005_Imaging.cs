using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Образы Windows (один install.wim — и для заливки по сети, и для USB) и перезаливка машин. Хранятся текущая и
/// откатная версии. Перезаливка — задание на машину; PXE-флаг (<c>pxe_armed</c>) снимается только после успешного
/// bcdboot. От помощника — какая версия образа стоит на ПК и системный диск (чтобы WinPE не стёр чужой диск).
/// </summary>
[Migration(2026092802, "imaging: Windows images, reimage jobs, image version and system disk from helpers")]
public sealed class M0005_Imaging : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE windows_images (
                id            uuid PRIMARY KEY,
                label         text NOT NULL UNIQUE CHECK (label ~ '^[a-z0-9][a-z0-9._-]{0,39}$'),
                state         text NOT NULL CHECK (state IN ('importing', 'ready', 'failed', 'retired')),
                source_file   text NOT NULL,
                file_path     text NOT NULL,
                image_index   integer NOT NULL DEFAULT 1 CHECK (image_index >= 1),
                size_bytes    bigint NULL,
                sha256        text NULL,
                wim_images    jsonb NULL,
                generalized   boolean NULL,
                requested_by  text NULL,
                created_at    timestamptz NOT NULL DEFAULT now(),
                imported_at   timestamptz NULL,
                published_at  timestamptz NULL,
                retired_at    timestamptz NULL,
                last_error    text NULL
            );

            CREATE TABLE image_pointers (
                singleton   boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                current_id  uuid NULL REFERENCES windows_images (id),
                rollback_id uuid NULL REFERENCES windows_images (id)
            );
            INSERT INTO image_pointers DEFAULT VALUES;

            CREATE TABLE reimage_jobs (
                id              uuid PRIMARY KEY,
                machine_id      uuid NOT NULL REFERENCES machines (id) ON DELETE CASCADE,
                image_id        uuid NOT NULL REFERENCES windows_images (id),
                state           text NOT NULL CHECK (state IN ('requested', 'deploying', 'failed', 'booting', 'done', 'cancelled')),
                pxe_armed       boolean NOT NULL,
                disk_touched    boolean NOT NULL DEFAULT false,
                allow_new_disk  boolean NOT NULL DEFAULT false,
                step            text NULL,
                percent         smallint NULL CHECK (percent BETWEEN 0 AND 100),
                message         text NULL,
                failure         text NULL,
                target_disk     jsonb NULL,
                attempts        integer NOT NULL DEFAULT 0,
                requested_by    text NULL,
                created_at      timestamptz NOT NULL DEFAULT now(),
                updated_at      timestamptz NOT NULL DEFAULT now(),
                bcdboot_at      timestamptz NULL,
                finished_at     timestamptz NULL
            );
            -- Одно незавершённое задание на машину.
            CREATE UNIQUE INDEX reimage_jobs_active ON reimage_jobs (machine_id) WHERE state IN ('requested', 'deploying', 'failed', 'booting');

            ALTER TABLE machines ADD COLUMN image_version text NULL;
            ALTER TABLE machines ADD COLUMN system_disk jsonb NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE machines DROP COLUMN system_disk;
            ALTER TABLE machines DROP COLUMN image_version;
            DROP TABLE reimage_jobs;
            DROP TABLE image_pointers;
            DROP TABLE windows_images;
            """);
    }
}
