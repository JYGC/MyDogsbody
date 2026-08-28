namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// The ledger. An invoice is a stored fact (Q5.7), keyed for scan agreement by supplier +
/// reference (Q5.8) - the unique index below is the backstop that refuses a duplicate even when
/// the code is wrong.
///
/// Execute.Sql rather than Create.Table() for the two foreign keys - SQLite has no ALTER TABLE
/// ADD CONSTRAINT and FluentMigrator's SQLite generator refuses Create.ForeignKey, so a foreign
/// key has to be declared inline (same reason as the InvoiceTemplates migrations).
///
/// Amount is TEXT: SQLite has no decimal type, and a money value stored as REAL loses exactness.
/// The store writes decimal.ToString(InvariantCulture) and parses it back. Dates are TEXT ISO
/// 8601; IssueDate / DueDate are date-only (yyyy-MM-dd) and nullable - Q1.10, an invoice with no
/// due date is stored and listed anyway.
///
/// MessageReceivedAt is the date the mail arrived (Q1.6). LoadInvoices filters on it so a
/// narrowed scan window hides invoices outside it without deleting them - the window is measured
/// on mail-received date, not on the due date.
[<Migration(20260810000004L)>]
type CreateInvoicesTable() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE Invoices (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierId INTEGER NOT NULL,
                TemplateId INTEGER NOT NULL,
                Reference TEXT(200) NOT NULL,
                Amount TEXT(64) NOT NULL,
                Currency TEXT(8) NOT NULL,
                IssueDate TEXT(10) NULL,
                DueDate TEXT(10) NULL,
                SourceMessageId TEXT(998) NOT NULL,
                MessageReceivedAt TEXT(33) NOT NULL,
                ScannedAt TEXT(33) NOT NULL,
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE,
                FOREIGN KEY (TemplateId) REFERENCES InvoiceTemplates (Id) ON DELETE CASCADE
            );"
        )

        this.Create.Index("IX_Invoices_SupplierId_Reference")
            .OnTable("Invoices")
            .OnColumn("SupplierId").Ascending()
            .OnColumn("Reference").Ascending()
            .WithOptions().Unique()
        |> ignore

        this.Create.Index("IX_Invoices_SourceMessageId")
            .OnTable("Invoices")
            .OnColumn("SourceMessageId").Ascending()
        |> ignore

    override this.Down() =
        this.Delete.Index("IX_Invoices_SourceMessageId").OnTable("Invoices") |> ignore
        this.Delete.Index("IX_Invoices_SupplierId_Reference").OnTable("Invoices") |> ignore
        this.Delete.Table("Invoices") |> ignore
