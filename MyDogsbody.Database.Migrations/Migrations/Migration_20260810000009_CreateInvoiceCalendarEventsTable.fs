namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// This table is history, not truth (design.md) - a fact about *an invoice*, so it belongs on
/// this side rather than in the Google integration's store. The calendar itself remains the
/// source of truth for DiffInvoicesAgainstCalendarWorkflow; this table's only job is diagnostic:
/// when did we last touch this event, and on whose calendar?
///
/// GoogleAccountId / CalendarId / EventId are TEXT: all three are opaque strings at Google, the
/// same way the domain's GoogleAccountId / CalendarId / CalendarEventId types treat them.
///
/// Deleting an invoice cascades to its sync record - by the time
/// SyncInvoicesToCalendarWorkflow ever produces a DeleteEvent for an event, the invoice it
/// belonged to is already gone from the Invoices table (that is the whole reason it is a
/// DeleteEvent), so this row is already gone too before the workflow runs. Raw SQL for the same
/// reason every other foreign key in this database is raw SQL: SQLite has no
/// ALTER TABLE ADD CONSTRAINT, so a foreign key must be declared inline in CREATE TABLE, which
/// FluentMigrator's SQLite generator refuses to express through the fluent builder.
[<Migration(20260810000009L)>]
type CreateInvoiceCalendarEventsTable() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE InvoiceCalendarEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                InvoiceId INTEGER NOT NULL,
                GoogleAccountId TEXT NOT NULL,
                CalendarId TEXT NOT NULL,
                EventId TEXT NOT NULL,
                LastSyncedAt TEXT NOT NULL,
                FOREIGN KEY (InvoiceId) REFERENCES Invoices (Id) ON DELETE CASCADE
            );"
        )

        // Unique on InvoiceId, not on EventId: one invoice syncs to at most one event, which is
        // what makes markSynced an upsert on the natural target (task 6.2).
        this.Create.Index("IX_InvoiceCalendarEvents_InvoiceId")
            .OnTable("InvoiceCalendarEvents")
            .OnColumn("InvoiceId").Ascending()
            .WithOptions().Unique()
            |> ignore

    override this.Down() =
        this.Delete.Index("IX_InvoiceCalendarEvents_InvoiceId").OnTable("InvoiceCalendarEvents") |> ignore
        this.Delete.Table("InvoiceCalendarEvents") |> ignore
