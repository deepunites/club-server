using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Самообновление помощника (spec 2026-11-06, п. 3.2): SHA256 сборки помощника из последнего отчёта G: или регистрации и
/// поручения «поставить сборку на ПК». <c>progress</c> — последнее, что сообщил помощник; <c>reason</c> — почему он ждёт.
/// Не больше одного незавершённого поручения на ПК.
/// </summary>
[Migration(2026110601, "helper self-update: helper build of machines, helper update jobs")]
public sealed class M0013_HelperUpdates : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- Пишется как есть из каждого отчёта: помощник старше 1.8.0 поля не шлёт — значение обнуляется.
            ALTER TABLE machines ADD COLUMN helper_build text NULL;

            CREATE TABLE helper_updates (
                id            uuid PRIMARY KEY,
                machine_id    uuid NOT NULL REFERENCES machines (id) ON DELETE CASCADE,
                file          text NOT NULL,
                version       text NOT NULL,
                sha256        text NOT NULL,
                size          bigint NOT NULL,
                state         text NOT NULL CHECK (state IN ('queued', 'paused', 'active', 'done', 'failed', 'cancelled')),
                progress      text NULL CHECK (progress IN ('downloading', 'waiting', 'applying', 'confirming')),
                reason        text NULL CHECK (reason IN ('session', 'command', 'master')),
                error         text NULL,
                message       text NULL,
                requested_by  text NULL,
                created_at    timestamptz NOT NULL DEFAULT now(),
                activated_at  timestamptz NULL,
                progress_at   timestamptz NULL,
                updated_at    timestamptz NOT NULL DEFAULT now(),
                finished_at   timestamptz NULL
            );
            -- Одно незавершённое поручение на ПК.
            CREATE UNIQUE INDEX helper_updates_open ON helper_updates (machine_id) WHERE state IN ('queued', 'paused', 'active');
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE helper_updates;
            ALTER TABLE machines DROP COLUMN helper_build;
            """);
    }
}
