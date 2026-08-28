/// The scan-window adapter: satisfies LoadScanWindows, SaveScanWindow, DeleteScanWindow,
/// LoadSelectedScanWindow and SaveSelectedScanWindow.
///
/// The selected window is a NUMBER in the single InvoiceSettings row (design decision 6), not a
/// foreign key - it survives its ScanWindows row being deleted.
module MyDogsbody.Database.ScanWindowStore

open System
open Microsoft.Data.Sqlite
open Dapper
open Dapper.FSharp.SQLite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database.Models

let private runSync (task: System.Threading.Tasks.Task<'T>) : 'T =
    task |> Async.AwaitTask |> Async.RunSynchronously

let private orRaise (what: string) (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error reason -> raise (InvalidOperationException $"Stored {what} is unusable: {reason}")

let private withConnection (connection: SqliteConnection) (work: unit -> 'T) : 'T =
    connection.Open()
    try work () finally connection.Close()

let getScanWindows
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (getScanWindowsQ: unit -> QuerySource<ScanWindowRecord>)
    ()
    : Result<StoredScanWindow list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.ScanWindowStore.getScanWindows

    handleError {
        try
            let connection = getConnection ()

            let rows =
                withConnection connection (fun () ->
                    select {
                        for w in getScanWindowsQ () do
                        selectAll
                    }
                    |> connection.SelectAsync<ScanWindowRecord>
                    |> runSync
                    |> Seq.toList)

            return rows |> List.map (InvoiceRecordMappers.toStoredScanWindow >> orRaise "scan window")
        with ex ->
            return! MyDogsbodyException(action, "Failed to retrieve scan windows.", ex)
    }

let saveScanWindow
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (days: ScanWindowDays)
    : Result<StoredScanWindow, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.ScanWindowStore.saveScanWindow

    handleError {
        try
            let connection = getConnection ()
            let dayCount = ScanWindowDays.value days

            let assignedId =
                withConnection connection (fun () ->
                    connection.ExecuteScalarAsync<int64>(
                        "INSERT INTO ScanWindows (Days) VALUES (@Days); SELECT last_insert_rowid();",
                        {| Days = dayCount |}
                    )
                    |> runSync)

            return
                { Id = int assignedId; Days = dayCount }
                |> InvoiceRecordMappers.toStoredScanWindow
                |> orRaise "scan window"
        with ex ->
            return! MyDogsbodyException(action, "Failed to add scan window.", ex)
    }

let deleteScanWindow
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (id: ScanWindowId)
    : Result<bool, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.ScanWindowStore.deleteScanWindow

    handleError {
        try
            let connection = getConnection ()
            let rowId = int (ScanWindowId.value id)

            let affected =
                withConnection connection (fun () ->
                    connection.ExecuteAsync("DELETE FROM ScanWindows WHERE Id = @Id;", {| Id = rowId |})
                    |> runSync)

            return affected > 0
        with ex ->
            return! MyDogsbodyException(action, "Failed to delete scan window.", ex)
    }

let getSelectedScanWindow
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    ()
    : Result<ScanWindowDays option, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.ScanWindowStore.getSelectedScanWindow

    handleError {
        try
            let connection = getConnection ()

            let raw =
                withConnection connection (fun () ->
                    connection.QueryAsync<Nullable<int>>(
                        "SELECT SelectedScanWindowDays FROM InvoiceSettings WHERE Id = 1;"
                    )
                    |> runSync
                    |> Seq.tryHead)

            return
                match raw with
                | Some value when value.HasValue ->
                    match ScanWindowDays.create value.Value with
                    | Ok window -> Some window
                    | Error reason -> raise (InvalidOperationException $"Stored selected scan window is unusable: {reason}")
                | _ -> None
        with ex ->
            return! MyDogsbodyException(action, "Failed to read the selected scan window.", ex)
    }

let saveSelectedScanWindow
    (handleError: HandleErrorBuilder)
    (getConnection: unit -> SqliteConnection)
    (days: ScanWindowDays)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Database.ScanWindowStore.saveSelectedScanWindow

    handleError {
        try
            let connection = getConnection ()

            withConnection connection (fun () ->
                connection.ExecuteAsync(
                    "INSERT INTO InvoiceSettings (Id, SelectedScanWindowDays) VALUES (1, @Days)
                     ON CONFLICT (Id) DO UPDATE SET SelectedScanWindowDays = excluded.SelectedScanWindowDays;",
                    {| Days = ScanWindowDays.value days |}
                )
                |> runSync
                |> ignore)

            return ()
        with ex ->
            return! MyDogsbodyException(action, "Failed to save the selected scan window.", ex)
    }
