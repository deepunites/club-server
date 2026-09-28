using FluentMigrator;

namespace Club.Server.Data.Migrations;

/// <summary>
/// Secure Boot на ПК по отчёту помощника: включён ли, есть ли в db сторонний Microsoft UEFI CA 2011 (им подписан
/// shim iPXE) и Windows UEFI CA 2023, отозван ли в dbx загрузчик Windows PCA 2011. По этим фактам сервер выбирает
/// загрузчик WinPE и заранее отказывает там, где подписанная цепочка всё равно не загрузится.
/// </summary>
[Migration(2026092803, "machines: Secure Boot facts reported by the helper")]
public sealed class M0006_SecureBoot : Migration
{
    public override void Up() => Execute.Sql("ALTER TABLE machines ADD COLUMN secure_boot jsonb NULL;");

    public override void Down() => Execute.Sql("ALTER TABLE machines DROP COLUMN secure_boot;");
}
