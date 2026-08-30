namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

/// The set of scan windows the picker offers. A window is a ROW, not a union case (Q1.8
/// superseded) - so the set is runtime data and adding a sixth is done on screen without a
/// rebuild.
///
/// ****  This is the first migration in this repository that carries DATA as well as structure
/// (friction #17).  ****  Up() seeds 7, 14, 30, 90 and 180 with Insert.IntoTable; Down() removes
/// exactly those five with Delete.FromTable. Every migration before this one created schema and
/// nothing else - the next person will copy whichever migration they open first, so this is said
/// plainly here and in the change description.
///
/// Consequence accepted knowingly: if a user deletes a seeded window, re-running migrations does
/// NOT bring it back - FluentMigrator never re-runs a migration already in VersionInfo. That is
/// correct for a value the user chose to remove, and is why ScanWindowDays.fallback exists rather
/// than the code assuming 14 is present.
///
/// No foreign key, so the fluent Create.Table() builder is fine here.
[<Migration(20260810000007L)>]
type CreateScanWindowsTable() =
    inherit Migration()

    override this.Up() =
        this.Create.Table("ScanWindows")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("Days").AsInt32().NotNullable()
        |> ignore

        this.Create.Index("IX_ScanWindows_Days")
            .OnTable("ScanWindows")
            .OnColumn("Days").Ascending()
            .WithOptions().Unique()
        |> ignore

        for days in [ 7; 14; 30; 90; 180 ] do
            this.Insert.IntoTable("ScanWindows").Row(box {| Days = days |}) |> ignore

    override this.Down() =
        for days in [ 7; 14; 30; 90; 180 ] do
            this.Delete.FromTable("ScanWindows").Row(box {| Days = days |}) |> ignore

        this.Delete.Index("IX_ScanWindows_Days").OnTable("ScanWindows") |> ignore
        this.Delete.Table("ScanWindows") |> ignore
