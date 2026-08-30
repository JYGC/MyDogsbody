namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// A hand-deleted invoice stays deleted (Q5.14): a tombstone on the NATURAL key that the scan
/// skips. Visible and reversible. Keyed on supplier + reference, not the database id, so
/// rebuilding the ledger does not resurrect what the user removed.
///
/// Execute.Sql for the foreign key - same SQLite reason as the other tables here.
[<Migration(20260810000006L)>]
type CreateInvoiceTombstonesTable() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE InvoiceTombstones (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierId INTEGER NOT NULL,
                Reference TEXT(200) NOT NULL,
                DeletedAt TEXT(33) NOT NULL,
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE
            );"
        )

        this.Create.Index("IX_InvoiceTombstones_SupplierId_Reference")
            .OnTable("InvoiceTombstones")
            .OnColumn("SupplierId").Ascending()
            .OnColumn("Reference").Ascending()
            .WithOptions().Unique()
        |> ignore

    override this.Down() =
        this.Delete.Index("IX_InvoiceTombstones_SupplierId_Reference").OnTable("InvoiceTombstones")
        |> ignore

        this.Delete.Table("InvoiceTombstones") |> ignore
