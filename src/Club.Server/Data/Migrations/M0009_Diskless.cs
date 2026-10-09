using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Полный бездиск (docs/diskless-full.md): режим загрузки машины, эталон Windows с версиями (снапшоты одного zvol)
/// и личные диски мест — записываемые клоны версии эталона со снапшотом <c>@clean</c>, к которому том откатывается
/// перед каждой загрузкой. Режим мастера: одна машина грузится прямо с zvol эталона (без клона и отката).
/// </summary>
[Migration(2026101001, "diskless: boot mode, system image versions, seat disks")]
public sealed class M0009_Diskless : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE machines ADD COLUMN boot_mode text NOT NULL DEFAULT 'local' CHECK (boot_mode IN ('local', 'diskless'));

            CREATE TABLE diskless_image (
                singleton          boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                current_version    text NULL,
                rollback_version   text NULL,
                master_machine_id  uuid NULL REFERENCES machines (id) ON DELETE SET NULL,
                master_install     boolean NOT NULL DEFAULT false,
                updated_at         timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO diskless_image DEFAULT VALUES;

            CREATE TABLE diskless_versions (
                version     text PRIMARY KEY,
                snapshot    text NOT NULL UNIQUE,
                comment     text NULL,
                created_by  text NULL,
                created_at  timestamptz NOT NULL
            );

            CREATE TABLE seat_disks (
                machine_id     uuid PRIMARY KEY REFERENCES machines (id) ON DELETE CASCADE,
                kind           text NOT NULL CHECK (kind IN ('seat', 'master')),
                zvol           text NOT NULL,
                target_name    text NOT NULL UNIQUE,
                initiator_iqn  text NOT NULL,
                base_snapshot  text NULL,
                chap_user      text NOT NULL,
                chap_secret    text NOT NULL,
                auth_tag       integer NULL,
                target_iqn     text NULL,
                state          text NOT NULL DEFAULT 'new' CHECK (state IN ('new', 'ready', 'failed')),
                last_error     text NULL,
                boots          integer NOT NULL DEFAULT 0,
                last_boot_at   timestamptz NULL,
                updated_at     timestamptz NOT NULL DEFAULT now()
            );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE seat_disks;
            DROP TABLE diskless_versions;
            DROP TABLE diskless_image;
            ALTER TABLE machines DROP COLUMN boot_mode;
            """);
    }
}
