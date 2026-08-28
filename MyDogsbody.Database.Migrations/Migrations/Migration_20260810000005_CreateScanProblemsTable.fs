namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// A message that yielded no invoice, kept so incremental scanning does not empty the diagnostic
/// list before it is looked at (Q1.19). Keyed by source message id, cleared when that message
/// later yields an invoice.
///
/// Cause names the ScanProblemCause union case; Detail holds its payload, encoded by
/// InvoiceRecordMappers (SeveralSuppliersMatched carries a list, so the payload cannot be a
/// single foreign key). SupplierId is a nullable convenience column - the "primary" supplier when
/// the cause names exactly one - with no foreign key, because the supplier may legitimately be
/// gone and this row is a diagnostic, not a relationship.
[<Migration(20260810000005L)>]
type CreateScanProblemsTable() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE ScanProblems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceMessageId TEXT(998) NOT NULL,
                SupplierId INTEGER NULL,
                Sender TEXT(998) NOT NULL,
                Subject TEXT(998) NOT NULL,
                ReceivedAt TEXT(33) NOT NULL,
                Cause TEXT(32) NOT NULL,
                Detail TEXT(4000) NULL,
                RecordedAt TEXT(33) NOT NULL
            );"
        )

        // Not unique: SaveScanProblems / ClearScanProblems delete the existing rows for the
        // message ids they touch and re-insert, so a rescan does not need the index to dedupe.
        this.Create.Index("IX_ScanProblems_SourceMessageId")
            .OnTable("ScanProblems")
            .OnColumn("SourceMessageId").Ascending()
        |> ignore

    override this.Down() =
        this.Delete.Index("IX_ScanProblems_SourceMessageId").OnTable("ScanProblems") |> ignore
        this.Delete.Table("ScanProblems") |> ignore
