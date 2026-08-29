namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// The ledger. An invoice is a stored fact (Q5.7), keyed for scan agreement by supplier +
/// reference (Q5.8) - the unique index below is the backstop that refuses a duplicate even when
/// the code is wrong.
///
/// Execute.Sql rather than Create.Table() for the foreign key - SQLite has no ALTER TABLE ADD
/// CONSTRAINT and FluentMigrator's SQLite generator refuses Create.ForeignKey, so a foreign key
/// has to be declared inline (same reason as the InvoiceTemplates migrations).
///
/// SupplierId is a relationship and cascades: the domain carries SupplierGone for an invoice whose
/// supplier went away mid-scan, and an invoice with no supplier has no name to render.
///
/// TemplateId is NOT, and deliberately has no foreign key at all. requirements.md asks only that
/// an invoice "record which template produced it" - provenance, not a relationship. Nothing joins
/// the two: InvoiceRecordMappers reads the column straight back into an opaque TemplateId and
/// InvoiceUiType carries no template at all. It used to be a second cascading foreign key, which
/// meant deleting a template took every invoice that template had ever produced with it - silently
/// (no tombstone is written for a cascade) and unrecoverably, against a ledger whose whole purpose
/// is to keep what a scan found. invoice-templates requirements.md ("delete it and its rules")
/// asks for the template and its rules to go, not the ledger rows. Same treatment, and the same
/// stated reason, as ScanProblems.SupplierId in the next migration.
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
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE
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
