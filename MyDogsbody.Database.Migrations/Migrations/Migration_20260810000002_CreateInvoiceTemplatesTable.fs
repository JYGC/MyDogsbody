namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260810000002L)>]
type CreateInvoiceTemplatesTable() =
    inherit Migration()

    // Foreign key, so Execute.Sql rather than Create.Table() - same reason as
    // Migration_20260809000002_CreateSupplierMatchersTable: SQLite has no ALTER TABLE ADD
    // CONSTRAINT, a foreign key must be declared inline in CREATE TABLE, and FluentMigrator's
    // SQLite generator refuses CreateForeignKeyExpression outright.
    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE InvoiceTemplates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierId INTEGER NOT NULL,
                Name TEXT(100) NOT NULL,
                DocumentPart TEXT(32) NOT NULL,
                AttachmentFormat TEXT(16) NULL,
                Position INTEGER NOT NULL,
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE
            );"
        )

        this.Create.Index("IX_InvoiceTemplates_SupplierId_Position")
            .OnTable("InvoiceTemplates")
            .OnColumn("SupplierId").Ascending()
            .OnColumn("Position").Ascending()
            |> ignore

    override this.Down() =
        this.Delete.Index("IX_InvoiceTemplates_SupplierId_Position").OnTable("InvoiceTemplates") |> ignore
        this.Delete.Table("InvoiceTemplates") |> ignore
