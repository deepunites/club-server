using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>Состав версии библиотеки: папки верхнего уровня тома по отчёту помощника (сервер внутрь NTFS не заглядывает).</summary>
[Migration(2026092804, "library_versions: contents reported by helpers")]
public sealed class M0007_LibraryContents : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE library_versions ADD COLUMN contents jsonb NULL;
        ALTER TABLE library_versions ADD COLUMN contents_at timestamptz NULL;
        """);

    public override void Down() => Execute.Sql("""
        ALTER TABLE library_versions DROP COLUMN contents_at;
        ALTER TABLE library_versions DROP COLUMN contents;
        """);
}
