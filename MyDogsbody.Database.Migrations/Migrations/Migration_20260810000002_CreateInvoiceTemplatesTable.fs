namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260810000002L)>]
type CreateInvoiceTemplatesTable() =
    inherit Migration()

    // Foreign key, so Execute.Sql rather than Create.Table() - same reason as
    // Migration_20260809000002_CreateSupplierMatchersTable: SQLite has no ALTER TABLE ADD
    // CONSTRAINT, a foreign key must be declared inline in CREATE TABLE, and FluentMigrator's
    // SQLite generator refuses CreateForeignKeyExpression outright ("Foreign keys are not
    // supported in SQLite").
    //
    // TEXT(n) lengths (Name TEXT(100), DocumentPart TEXT(32), AttachmentFormat TEXT(16)) are
    // documentation/budget only - SQLite's type-affinity system ignores the parenthesized length,
    // so nothing here truncates or rejects a longer string. TemplateName.MaximumLength in
    // InvoiceTemplatesTypes.fs is the only actual enforcement of the Name budget.
    //
    // The CHECK below is the schema-level half of TemplateRecordMappers.fromDocumentPartColumns:
    // AttachmentFormat is populated only when DocumentPart = 'Attachment' (split-column encoding,
    // see design.md). The domain mapper already refuses the mismatched shape on read, but nothing
    // stopped a row in that shape from being written until now - this closes that gap for any
    // write that goes around TemplateStore.fs, not just the ones that go through it.
    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE InvoiceTemplates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SupplierId INTEGER NOT NULL,
                Name TEXT(100) NOT NULL,
                DocumentPart TEXT(32) NOT NULL,
                AttachmentFormat TEXT(16) NULL,
                Position INTEGER NOT NULL,
                FOREIGN KEY (SupplierId) REFERENCES Suppliers (Id) ON DELETE CASCADE,
                CHECK (
                    (DocumentPart = 'Attachment' AND AttachmentFormat IS NOT NULL)
                    OR (DocumentPart IN ('Body', 'AnyPart') AND AttachmentFormat IS NULL)
                )
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
