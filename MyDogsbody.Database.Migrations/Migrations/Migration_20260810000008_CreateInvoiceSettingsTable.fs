namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// The app's first real user setting (Q1.7 / Q5.13): a TYPED single-row table, one column per
/// setting, extended by migration - not a key/value store whose every value is a string parsed
/// at the point of use. This is the first setting, so whatever it does becomes the habit.
///
/// The primary key is fixed at a single row: Id INTEGER PRIMARY KEY CHECK (Id = 1). A second
/// insert of Id = 1 fails the primary key; an insert of any other Id fails the check. Execute.Sql
/// because the fluent builder cannot express the CHECK.
///
/// SelectedScanWindowDays is a NUMBER, nullable - not a foreign key to a ScanWindows row (design
/// decision 6): the remembered choice survives its window being deleted and is simply not offered
/// by the picker any more. NULL means "nothing chosen yet" - the page opens on 14
/// (ResolveScanWindowWorkflow).
[<Migration(20260810000008L)>]
type CreateInvoiceSettingsTable() =
    inherit Migration()

    override this.Up() =
        this.Execute.Sql(
            "CREATE TABLE InvoiceSettings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SelectedScanWindowDays INTEGER NULL
            );"
        )

    override this.Down() = this.Delete.Table("InvoiceSettings") |> ignore
