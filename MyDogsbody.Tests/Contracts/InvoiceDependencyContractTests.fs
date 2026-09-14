module MyDogsbody.Tests.Contracts.InvoiceDependencyContractTests

open System
open System.IO
open Xunit
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.Database.Migrations

// A dependency function type is this architecture's published interface (CLAUDE.md), so the
// store-backed ones get a shared suite run against the REAL adapter and against the in-memory
// fake a workflow unit test uses - so the fake cannot return a shape the store never produces.
//
// (ReadDocumentText / ReadDocumentContent / GetCurrentTime have their own suites elsewhere.)

let private handleError = HandleErrorBuilder ignore

let private orFail =
    function
    | Ok value -> value
    | Error error -> failwith $"test setup: {error}"

/// Everything a workflow gets, bundled so one suite can drive either implementation.
type Dependencies =
    { LoadInvoices: LoadInvoices
      UpsertInvoice: UpsertInvoice
      LoadTombstones: LoadTombstones
      SaveTombstone: SaveTombstone
      RemoveTombstone: RemoveTombstone
      LoadScanProblems: LoadScanProblems
      SaveScanProblems: SaveScanProblems
      ClearScanProblems: ClearScanProblems
      LoadScanWindows: LoadScanWindows
      SaveScanWindow: SaveScanWindow
      LoadSelectedScanWindow: LoadSelectedScanWindow
      SaveSelectedScanWindow: SaveSelectedScanWindow }

let private toInvoiceError (capturedException: MyDogsbody.Exceptions.Types.MyDogsbodyException) =
    InvoiceStoreFailed capturedException.Message

// ---------- real ----------

let private withReal (test: Dependencies -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={path}"

    use seedConnection = new SqliteConnection($"Data Source={path}")
    seedConnection.Open()
    use seedCommand = seedConnection.CreateCommand()
    seedCommand.CommandText <-
        "INSERT INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme', 30);
         INSERT INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"
    seedCommand.ExecuteNonQuery() |> ignore
    seedConnection.Close()

    let databaseContext = DatabaseContextSetup.createDatabaseContext path
    let databaseConnection = databaseContext.GetDatabaseConnection
    let clock () = DateTime(2026, 6, 1, 12, 0, 0)

    let dependencies: Dependencies =
        { LoadInvoices =
            fun cutoff ->
                InvoiceStore.getInvoices handleError databaseConnection databaseContext.GetInvoices cutoff
                |> Result.mapError toInvoiceError
          // The same two-way translation InvoiceApiFactory binds, so the suite exercises what
          // production actually hands the workflow rather than a simplification of it.
          UpsertInvoice =
            fun invoiceToUpsert ->
                InvoiceStore.upsertInvoice handleError databaseConnection clock invoiceToUpsert
                |> Result.mapError (fun capturedException ->
                    if InvoiceStore.isMissingSupplier capturedException then
                        SupplierGone invoiceToUpsert.SupplierId
                    else
                        toInvoiceError capturedException)
          LoadTombstones =
            fun () ->
                InvoiceStore.getTombstones handleError databaseConnection databaseContext.GetInvoiceTombstones ()
                |> Result.mapError toInvoiceError
          SaveTombstone = fun tombstone -> InvoiceStore.saveTombstone handleError databaseConnection tombstone |> Result.mapError toInvoiceError
          RemoveTombstone =
            fun supplierId reference ->
                InvoiceStore.removeTombstone handleError databaseConnection supplierId reference
                |> Result.mapError toInvoiceError
          LoadScanProblems =
            fun () ->
                InvoiceStore.getScanProblems handleError databaseConnection databaseContext.GetScanProblems ()
                |> Result.mapError toInvoiceError
          SaveScanProblems = fun scanProblems -> InvoiceStore.saveScanProblems handleError databaseConnection scanProblems |> Result.mapError toInvoiceError
          ClearScanProblems = fun ids -> InvoiceStore.clearScanProblems handleError databaseConnection ids |> Result.mapError toInvoiceError
          LoadScanWindows =
            fun () ->
                ScanWindowStore.getScanWindows handleError databaseConnection databaseContext.GetScanWindows ()
                |> Result.mapError toInvoiceError
          SaveScanWindow = fun scanWindowDays -> ScanWindowStore.saveScanWindow handleError databaseConnection scanWindowDays |> Result.mapError toInvoiceError
          LoadSelectedScanWindow = fun () -> ScanWindowStore.getSelectedScanWindow handleError databaseConnection () |> Result.mapError toInvoiceError
          SaveSelectedScanWindow =
            fun scanWindowDays -> ScanWindowStore.saveSelectedScanWindow handleError databaseConnection scanWindowDays |> Result.mapError toInvoiceError }

    try
        test dependencies
    finally
        databaseContext.Dispose()
        try File.Delete path with _ -> ()

// ---------- fake ----------

let private withFake (test: Dependencies -> unit) =
    let invoices = ResizeArray<StoredInvoice>()
    let tombstones = ResizeArray<InvoiceTombstone>()
    let problems = ResizeArray<ScanProblem>()

    let mutable windows =
        [ for days in [ 7; 14; 30; 90; 180 ] ->
              { Id = ScanWindowId.create $"w{days}" |> orFail; Days = ScanWindowDays.create days |> orFail } ]

    let mutable selected: ScanWindowDays option = None
    let mutable nextId = 100

    let key (invoice: ValidInvoice) = SupplierId.value invoice.SupplierId, InvoiceReference.value invoice.Reference

    // withReal seeds exactly one supplier, and the Invoices foreign key refuses a write naming any
    // other. A fake that accepted every supplier id would be returning Ok where the store returns
    // SupplierGone - the drift this suite exists to catch.
    let knownSuppliers = set [ "1" ]

    let dependencies: Dependencies =
        { LoadInvoices =
            fun cutoff ->
                let all = List.ofSeq invoices
                match cutoff with
                | None -> Ok all
                | Some cutoffValue -> Ok(all |> List.filter (fun storedInvoice -> storedInvoice.Invoice.MessageReceivedAt >= ScanCutoff.value cutoffValue))
          UpsertInvoice =
            fun invoiceToUpsert ->
                if not (Set.contains (SupplierId.value invoiceToUpsert.SupplierId) knownSuppliers) then
                    Error(SupplierGone invoiceToUpsert.SupplierId)
                else
                    invoices.RemoveAll(fun existingInvoice -> key existingInvoice.Invoice = key invoiceToUpsert) |> ignore
                    nextId <- nextId + 1
                    let stored = { Id = InvoiceId.create (string nextId) |> orFail; Invoice = invoiceToUpsert; ScannedAt = DateTime(2026, 6, 1) }
                    invoices.Add stored
                    Ok stored
          LoadTombstones = fun () -> Ok(List.ofSeq tombstones)
          SaveTombstone =
            fun tombstone ->
                if not (tombstones |> Seq.exists (fun existingTombstone -> existingTombstone.SupplierId = tombstone.SupplierId && existingTombstone.Reference = tombstone.Reference)) then
                    tombstones.Add tombstone
                Ok()
          RemoveTombstone =
            fun supplierId reference ->
                let removed =
                    tombstones.RemoveAll(fun existingTombstone ->
                        existingTombstone.SupplierId = supplierId
                        && InvoiceReference.value existingTombstone.Reference = InvoiceReference.value reference)
                Ok(removed > 0)
          LoadScanProblems = fun () -> Ok(List.ofSeq problems)
          SaveScanProblems =
            fun scanProblems ->
                for scanProblem in scanProblems do
                    problems.RemoveAll(fun existingProblem -> existingProblem.SourceMessageId = scanProblem.SourceMessageId) |> ignore
                    problems.Add scanProblem
                Ok()
          ClearScanProblems =
            fun ids ->
                for id in ids do
                    problems.RemoveAll(fun existingProblem -> SourceMessageId.value existingProblem.SourceMessageId = SourceMessageId.value id) |> ignore
                Ok()
          LoadScanWindows = fun () -> Ok windows
          SaveScanWindow =
            fun scanWindowDays ->
                nextId <- nextId + 1
                let newScanWindow = { Id = ScanWindowId.create (string nextId) |> orFail; Days = scanWindowDays }
                windows <- windows @ [ newScanWindow ]
                Ok newScanWindow
          LoadSelectedScanWindow = fun () -> Ok selected
          SaveSelectedScanWindow = fun scanWindowDays -> selected <- Some scanWindowDays; Ok() }

    test dependencies

// ---------- the shared suite ----------

let implementations: obj[] seq = [ [| box "real" |]; [| box "fake" |] ]

let private run name test =
    match name with
    | "real" -> withReal test
    | "fake" -> withFake test
    | other -> failwith $"unknown '{other}'"

let private invoice reference : ValidInvoice =
    { SupplierId = SupplierId.create "1" |> orFail
      TemplateId = TemplateId.create "1" |> orFail
      SourceMessageId = SourceMessageId.create "m1" |> orFail
      Reference = InvoiceReference.create reference |> orFail
      Amount = Money.create 10m "AUD" |> orFail
      IssueDate = None
      DueDate = None
      MessageReceivedAt = DateTime(2026, 5, 20) }

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``upsert then load returns the invoice; a second upsert on the natural key does not duplicate`` (name: string) =
    run name (fun dependencies ->
        dependencies.UpsertInvoice(invoice "INV-1") |> orFail |> ignore
        dependencies.UpsertInvoice(invoice "INV-1") |> orFail |> ignore
        Assert.Equal(1, dependencies.LoadInvoices None |> orFail |> List.length))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``the cutoff filter hides an invoice whose message is older`` (name: string) =
    run name (fun dependencies ->
        dependencies.UpsertInvoice({ invoice "OLD" with MessageReceivedAt = DateTime(2026, 1, 1) }) |> orFail |> ignore
        dependencies.UpsertInvoice({ invoice "NEW" with MessageReceivedAt = DateTime(2026, 5, 30) }) |> orFail |> ignore
        let inWindow = dependencies.LoadInvoices(Some(ScanCutoff.ofStartOfDay (DateTime(2026, 5, 1)))) |> orFail
        Assert.Equal<string list>([ "NEW" ], inWindow |> List.map (fun storedInvoice -> InvoiceReference.value storedInvoice.Invoice.Reference)))

/// requirements.md: "WHEN a scan finds an invoice whose supplier has since been deleted THE SYSTEM
/// SHALL report it as a problem rather than storing an invoice with no supplier."
///
/// ScanForInvoicesWorkflow.step has a dedicated non-fatal branch for `Error (SupplierGone _)` -
/// record one problem for that message and carry on - and every other InvoiceError from the upsert
/// is fatal to the whole scan. So which of the two the dependency returns decides whether one
/// deleted supplier costs one row or the entire run, and it is exactly the shape a fake must not
/// invent: this case is what stops the workflow's unit suite being green over a binding that
/// cannot produce SupplierGone at all.
[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an upsert whose supplier row is gone reports SupplierGone, not an undifferentiated store failure`` (name: string) =
    run name (fun dependencies ->
        // supplier 2 is not in the store: withReal seeds only supplier 1, and so does withFake.
        let orphan = { invoice "INV-ORPHAN" with SupplierId = SupplierId.create "2" |> orFail }

        match dependencies.UpsertInvoice orphan with
        | Error(SupplierGone id) -> Assert.Equal("2", SupplierId.value id)
        | Error other -> Assert.Fail($"expected SupplierGone, got {other}")
        | Ok _ -> Assert.Fail("expected the write to be refused - there is no supplier 2")

        // and nothing was stored for it
        Assert.Empty(dependencies.LoadInvoices None |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a tombstone is saved once, loaded, and removed exactly once`` (name: string) =
    run name (fun dependencies ->
        let tombstone: InvoiceTombstone =
            { SupplierId = SupplierId.create "1" |> orFail
              Reference = InvoiceReference.create "INV-7" |> orFail
              DeletedAt = DateTime(2026, 5, 1) }

        dependencies.SaveTombstone tombstone |> orFail
        dependencies.SaveTombstone tombstone |> orFail
        Assert.Equal(1, dependencies.LoadTombstones() |> orFail |> List.length)
        Assert.True(dependencies.RemoveTombstone tombstone.SupplierId tombstone.Reference |> orFail)
        Assert.False(dependencies.RemoveTombstone tombstone.SupplierId tombstone.Reference |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``scan problems are replaced per message and cleared by id`` (name: string) =
    run name (fun dependencies ->
        let problem cause : ScanProblem =
            { SourceMessageId = SourceMessageId.create "m1" |> orFail
              Sender = "a@b.test"
              Subject = "s"
              ReceivedAt = DateTime(2026, 5, 20)
              Cause = cause
              RecordedAt = DateTime(2026, 6, 1) }

        dependencies.SaveScanProblems [ problem NoSupplierMatched ] |> orFail
        dependencies.SaveScanProblems [ problem (NoTemplateMatched(SupplierId.create "1" |> orFail)) ] |> orFail
        let after = dependencies.LoadScanProblems() |> orFail
        Assert.Equal(1, List.length after)
        Assert.Equal(NoTemplateMatched(SupplierId.create "1" |> orFail), (List.head after).Cause)
        dependencies.ClearScanProblems [ SourceMessageId.create "m1" |> orFail ] |> orFail
        Assert.Empty(dependencies.LoadScanProblems() |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``scan windows: seeded five present, add appends, the selected number persists`` (name: string) =
    run name (fun dependencies ->
        Assert.Equal<int list>(
            [ 7; 14; 30; 90; 180 ],
            dependencies.LoadScanWindows() |> orFail |> List.map (fun scanWindow -> ScanWindowDays.value scanWindow.Days) |> List.sort
        )

        dependencies.SaveScanWindow(ScanWindowDays.create 45 |> orFail) |> orFail |> ignore
        Assert.Contains(45, dependencies.LoadScanWindows() |> orFail |> List.map (fun scanWindow -> ScanWindowDays.value scanWindow.Days))

        Assert.Equal(None, dependencies.LoadSelectedScanWindow() |> orFail)
        dependencies.SaveSelectedScanWindow(ScanWindowDays.create 90 |> orFail) |> orFail
        Assert.Equal(Some 90, dependencies.LoadSelectedScanWindow() |> orFail |> Option.map ScanWindowDays.value))
