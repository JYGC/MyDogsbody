module MyDogsbody.Tests.Contracts.InvoicePersistedShapeTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Database.Migrations

// SQLite is not schemaless, but a Dapper.FSharp record field renamed without a migration fails
// only at run time - so the persisted column names are asserted here against the table schema
// the migrations produce (task 10.3), not just by round-tripping an object.

let private withSchema (test: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let connectionString = $"Data Source={path}"
    MigrationSetup.setupMigrations connectionString

    try
        test connectionString
    finally
        try File.Delete path with _ -> ()

let private columns (connectionString: string) (table: string) : string list =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA table_info('{table}')"
    use reader = command.ExecuteReader()

    [ while reader.Read() do
          yield reader.GetString 1 ]

[<Theory; Trait("Level", "Contract")>]
[<InlineData("Invoices", "Id,SupplierId,TemplateId,Reference,Amount,Currency,IssueDate,DueDate,SourceMessageId,MessageReceivedAt,ScannedAt")>]
[<InlineData("ScanProblems", "Id,SourceMessageId,SupplierId,Sender,Subject,ReceivedAt,Cause,Detail,RecordedAt")>]
[<InlineData("InvoiceTombstones", "Id,SupplierId,Reference,DeletedAt")>]
[<InlineData("ScanWindows", "Id,Days")>]
[<InlineData("InvoiceSettings", "Id,SelectedScanWindowDays")>]
let ``the persisted columns for each change #4 table are exactly as documented`` (table: string) (expected: string) =
    withSchema (fun connectionString ->
        Assert.Equal<string list>(
            expected.Split(',') |> Array.toList,
            columns connectionString table
        ))

[<Fact; Trait("Level", "Contract")>]
let ``the change #4 record types have a field for every persisted column`` () =
    // A field renamed on the F# record without a migration would leave the column orphaned; a
    // column added without touching the record would never be read.
    let recordFields (t: Type) =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields t
        |> Array.map (fun p -> p.Name)
        |> Array.toList
        |> List.sort

    withSchema (fun connectionString ->
        let check (table: string) (t: Type) =
            Assert.Equal<string list>(columns connectionString table |> List.sort, recordFields t)

        check "Invoices" typeof<MyDogsbody.Database.Models.InvoiceRecord>
        check "ScanProblems" typeof<MyDogsbody.Database.Models.ScanProblemRecord>
        check "InvoiceTombstones" typeof<MyDogsbody.Database.Models.InvoiceTombstoneRecord>
        check "ScanWindows" typeof<MyDogsbody.Database.Models.ScanWindowRecord>
        check "InvoiceSettings" typeof<MyDogsbody.Database.Models.InvoiceSettingsRecord>)
