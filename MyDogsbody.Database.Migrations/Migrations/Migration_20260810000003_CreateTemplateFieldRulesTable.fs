namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260810000003L)>]
type CreateTemplateFieldRulesTable() =
    inherit Migration()

    // Foreign key, so Execute.Sql rather than Create.Table() - same reason as
    // Migration_20260810000002_CreateInvoiceTemplatesTable.
    //
    // Split-column encoding: FieldRule and ParseHint are discriminated unions with per-case
    // payloads (design.md). RuleKind/HintKind name the case; RuleText/RuleOffset/RuleSourceField/
    // HintText hold whichever payload that case has and are NULL otherwise. Columns, not a
    // serialised blob - a template is relational rows, so an export is a read and a write.
    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE TemplateFieldRules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TemplateId INTEGER NOT NULL,
                TargetField TEXT(16) NOT NULL,
                RuleKind TEXT(32) NOT NULL,
                RuleText TEXT(1000) NULL,
                RuleOffset INTEGER NULL,
                RuleSourceField TEXT(16) NULL,
                HintKind TEXT(16) NOT NULL,
                HintText TEXT(64) NULL,
                FOREIGN KEY (TemplateId) REFERENCES InvoiceTemplates (Id) ON DELETE CASCADE
            );"
        )

        this.Create.Index("IX_TemplateFieldRules_TemplateId_TargetField")
            .OnTable("TemplateFieldRules")
            .OnColumn("TemplateId").Ascending()
            .OnColumn("TargetField").Ascending()
            .WithOptions().Unique()
            |> ignore

    override this.Down() =
        this.Delete.Index("IX_TemplateFieldRules_TemplateId_TargetField").OnTable("TemplateFieldRules") |> ignore
        this.Delete.Table("TemplateFieldRules") |> ignore
