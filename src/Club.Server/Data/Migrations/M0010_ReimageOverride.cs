using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Явное разрешение залить негенерализованный образ — только для одного задания перезаливки (эталонный ПК гибрида:
/// на него ставится копия эталона бездиска без sysprep, docs/imaging.md). По умолчанию выключено.
/// </summary>
[Migration(2026102301, "reimage_jobs: allow_not_generalized override for the reference PC")]
public sealed class M0010_ReimageOverride : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE reimage_jobs ADD COLUMN allow_not_generalized boolean NOT NULL DEFAULT false;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE reimage_jobs DROP COLUMN allow_not_generalized;
            """);
    }
}
