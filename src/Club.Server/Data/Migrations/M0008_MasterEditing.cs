using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Режим суперклиента: мастер-том библиотеки открыт на запись одному ПК (CHAP + группа из одного инициатора), пока
/// администратор обновляет игры. Пока мастер-том открыт, публикация запрещена. Операции открытия и закрытия идут
/// через общий журнал хранилища.
/// </summary>
[Migration(2026092805, "library_master: superclient editing of the master volume")]
public sealed class M0008_MasterEditing : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE library_master (
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
            INSERT INTO library_master DEFAULT VALUES;

            ALTER TABLE storage_operations DROP CONSTRAINT storage_operations_kind_check;
            ALTER TABLE storage_operations ADD CONSTRAINT storage_operations_kind_check
                CHECK (kind IN ('publish', 'rollback', 'masterOpen', 'masterClose'));

            ALTER TABLE machines ADD COLUMN initiator_iqn text NULL;
            ALTER TABLE machines ADD COLUMN master_state text NULL;
            ALTER TABLE machines ADD COLUMN master_error text NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE machines DROP COLUMN master_error;
            ALTER TABLE machines DROP COLUMN master_state;
            ALTER TABLE machines DROP COLUMN initiator_iqn;
            DELETE FROM storage_operations WHERE kind IN ('masterOpen', 'masterClose');
            ALTER TABLE storage_operations DROP CONSTRAINT storage_operations_kind_check;
            ALTER TABLE storage_operations ADD CONSTRAINT storage_operations_kind_check CHECK (kind IN ('publish', 'rollback'));
            DROP TABLE library_master;
            """);
    }
}
