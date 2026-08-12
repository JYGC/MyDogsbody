namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260810000003L)>]
type CreateTemplateFieldRulesTable() =
    inherit Migration()

    // Foreign key, so Execute.Sql rather than Create.Table() - same reason as
    // Migration_20260810000002_CreateInvoiceTemplatesTable: SQLite has no ALTER TABLE ADD
    // CONSTRAINT, a foreign key must be declared inline in CREATE TABLE, and FluentMigrator's
    // SQLite generator refuses CreateForeignKeyExpression outright ("Foreign keys are not
    // supported in SQLite").
    //
    // Split-column encoding: FieldRule and ParseHint are discriminated unions with per-case
    // payloads (design.md). RuleKind/HintKind name the case; RuleText/RuleOffset/RuleSourceField/
    // HintText hold whichever payload that case has and are NULL otherwise. Columns, not a
    // serialised blob - a template is relational rows, so an export is a read and a write.
    //
    // TEXT(n) lengths (TargetField TEXT(16), RuleKind TEXT(32), RuleText TEXT(1000),
    // RuleSourceField TEXT(16), HintKind TEXT(16), HintText TEXT(64)) are documentation/budget
    // only - SQLite's type-affinity system ignores the parenthesized length, so nothing here
    // truncates or rejects a longer string. No domain-level length guard exists for these columns
    // today.
    //
    // The two CHECKs below are the schema-level half of TemplateRecordMappers.fromFieldRuleColumns
    // / fromParseHintColumns: which of RuleText/RuleOffset/RuleSourceField is populated, and
    // whether HintText is populated, both depend on the sibling *Kind column (split-column
    // encoding above). The domain mappers already refuse a mismatched shape on read, but nothing
    // stopped a row in that shape from being written until now - this closes that gap for any
    // write that goes around TemplateStore.fs, not just the ones that go through it.
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
                FOREIGN KEY (TemplateId) REFERENCES InvoiceTemplates (Id) ON DELETE CASCADE,
                CHECK (
                    (RuleKind IN ('AfterLabel', 'RegexCapture', 'FixedValue', 'SubjectCapture', 'AttachmentName')
                        AND RuleText IS NOT NULL AND RuleOffset IS NULL AND RuleSourceField IS NULL)
                    OR (RuleKind = 'LinesAfterLabel'
                        AND RuleText IS NOT NULL AND RuleOffset IS NOT NULL AND RuleSourceField IS NULL)
                    OR (RuleKind = 'DateFromField'
                        AND RuleText IS NULL AND RuleOffset IS NULL AND RuleSourceField IS NOT NULL)
                ),
                CHECK (
                    (HintKind = 'AsText' AND HintText IS NULL)
                    OR (HintKind IN ('AsMoney', 'AsDate') AND HintText IS NOT NULL)
                )
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
