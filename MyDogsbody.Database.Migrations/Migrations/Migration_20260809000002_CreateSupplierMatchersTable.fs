namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260809000002L)>]
type CreateSupplierMatchersTable() =
    inherit Migration()

    // SQLite has no ALTER TABLE ADD CONSTRAINT, so a foreign key must be declared inline in the
    // CREATE TABLE statement - the fluent Create.Table()/Create.ForeignKey() pair cannot express
    // that, and FluentMigrator's SQLite generator refuses CreateForeignKeyExpression outright
    // ("Foreign keys are not supported in SQLite"). Raw SQL is the documented way around that.
    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE SupplierMatchers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierId INTEGER NOT NULL,
                Kind TEXT(16) NOT NULL,
                Value TEXT(400) NOT NULL,
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE
            );"
        )

        this.Create.Index("IX_SupplierMatchers_SupplierId")
            .OnTable("SupplierMatchers")
            .OnColumn("SupplierId").Ascending()
            |> ignore

    override this.Down() =
        this.Delete.Index("IX_SupplierMatchers_SupplierId").OnTable("SupplierMatchers") |> ignore
        this.Delete.Table("SupplierMatchers") |> ignore
