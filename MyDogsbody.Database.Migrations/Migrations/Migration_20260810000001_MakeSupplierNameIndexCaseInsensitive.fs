namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// The original IX_Suppliers_Name (Migration_20260809000001) is case-sensitive because
/// FluentMigrator's fluent index builder cannot express a per-column COLLATE clause - the same
/// gap Create.ForeignKey has on SQLite. Execute.Sql is the established workaround.
[<Migration(20260810000001L)>]
type MakeSupplierNameIndexCaseInsensitive() =
    inherit Migration()

    override this.Up() =
        this.Delete.Index("IX_Suppliers_Name").OnTable("Suppliers") |> ignore
        this.Execute.Sql("CREATE UNIQUE INDEX IX_Suppliers_Name ON Suppliers (Name COLLATE NOCASE)")

    override this.Down() =
        this.Execute.Sql("DROP INDEX IX_Suppliers_Name")

        this.Create.Index("IX_Suppliers_Name")
            .OnTable("Suppliers")
            .OnColumn("Name").Ascending()
            .WithOptions().Unique()
            |> ignore
