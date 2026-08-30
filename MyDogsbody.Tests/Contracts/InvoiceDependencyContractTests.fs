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
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

/// Everything a workflow gets, bundled so one suite can drive either implementation.
type Deps =
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

let private toInvoiceError (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) = InvoiceStoreFailed ex.Message

// ---------- real ----------

let private withReal (test: Deps -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    MigrationSetup.setupMigrations $"Data Source={path}"

    use seed = new SqliteConnection($"Data Source={path}")
    seed.Open()
    use cmd = seed.CreateCommand()
    cmd.CommandText <-
        "INSERT INTO Suppliers (Id, Name, PaymentTermDays) VALUES (1, 'Acme', 30);
         INSERT INTO InvoiceTemplates (Id, SupplierId, Name, DocumentPart, AttachmentFormat, Position)
         VALUES (1, 1, 'T', 'AnyPart', NULL, 0);"
    cmd.ExecuteNonQuery() |> ignore
    seed.Close()

    let ctx = DatabaseContextSetup.createDatabaseContext path
    let conn = ctx.GetDatabaseConnection
    let clock () = DateTime(2026, 6, 1, 12, 0, 0)

    let deps: Deps =
        { LoadInvoices = fun c -> InvoiceStore.getInvoices handleError conn ctx.GetInvoices c |> Result.mapError toInvoiceError
          // The same two-way translation InvoiceApiFactory binds, so the suite exercises what
          // production actually hands the workflow rather than a simplification of it.
          UpsertInvoice =
            fun i ->
                InvoiceStore.upsertInvoice handleError conn clock i
                |> Result.mapError (fun ex ->
                    if InvoiceStore.isMissingSupplier ex then SupplierGone i.SupplierId else toInvoiceError ex)
          LoadTombstones = fun () -> InvoiceStore.getTombstones handleError conn ctx.GetInvoiceTombstones () |> Result.mapError toInvoiceError
          SaveTombstone = fun t -> InvoiceStore.saveTombstone handleError conn t |> Result.mapError toInvoiceError
          RemoveTombstone = fun s r -> InvoiceStore.removeTombstone handleError conn s r |> Result.mapError toInvoiceError
          LoadScanProblems = fun () -> InvoiceStore.getScanProblems handleError conn ctx.GetScanProblems () |> Result.mapError toInvoiceError
          SaveScanProblems = fun p -> InvoiceStore.saveScanProblems handleError conn p |> Result.mapError toInvoiceError
          ClearScanProblems = fun ids -> InvoiceStore.clearScanProblems handleError conn ids |> Result.mapError toInvoiceError
          LoadScanWindows = fun () -> ScanWindowStore.getScanWindows handleError conn ctx.GetScanWindows () |> Result.mapError toInvoiceError
          SaveScanWindow = fun d -> ScanWindowStore.saveScanWindow handleError conn d |> Result.mapError toInvoiceError
          LoadSelectedScanWindow = fun () -> ScanWindowStore.getSelectedScanWindow handleError conn () |> Result.mapError toInvoiceError
          SaveSelectedScanWindow = fun d -> ScanWindowStore.saveSelectedScanWindow handleError conn d |> Result.mapError toInvoiceError }

    try
        test deps
    finally
        ctx.Dispose()
        try File.Delete path with _ -> ()

// ---------- fake ----------

let private withFake (test: Deps -> unit) =
    let invoices = ResizeArray<StoredInvoice>()
    let tombstones = ResizeArray<InvoiceTombstone>()
    let problems = ResizeArray<ScanProblem>()
    let mutable windows = [ for d in [ 7; 14; 30; 90; 180 ] -> { Id = ScanWindowId.create $"w{d}" |> orFail; Days = ScanWindowDays.create d |> orFail } ]
    let mutable selected: ScanWindowDays option = None
    let mutable nextId = 100

    let key (i: ValidInvoice) = SupplierId.value i.SupplierId, InvoiceReference.value i.Reference

    // withReal seeds exactly one supplier, and the Invoices foreign key refuses a write naming any
    // other. A fake that accepted every supplier id would be returning Ok where the store returns
    // SupplierGone - the drift this suite exists to catch.
    let knownSuppliers = set [ "1" ]

    let deps: Deps =
        { LoadInvoices =
            fun cutoff ->
                let all = List.ofSeq invoices
                match cutoff with
                | None -> Ok all
                | Some c -> Ok(all |> List.filter (fun i -> i.Invoice.MessageReceivedAt >= ScanCutoff.value c))
          UpsertInvoice =
            fun invoice ->
                if not (Set.contains (SupplierId.value invoice.SupplierId) knownSuppliers) then
                    Error(SupplierGone invoice.SupplierId)
                else
                    invoices.RemoveAll(fun i -> key i.Invoice = key invoice) |> ignore
                    nextId <- nextId + 1
                    let stored = { Id = InvoiceId.create (string nextId) |> orFail; Invoice = invoice; ScannedAt = DateTime(2026, 6, 1) }
                    invoices.Add stored
                    Ok stored
          LoadTombstones = fun () -> Ok(List.ofSeq tombstones)
          SaveTombstone =
            fun t ->
                if not (tombstones |> Seq.exists (fun x -> x.SupplierId = t.SupplierId && x.Reference = t.Reference)) then
                    tombstones.Add t
                Ok()
          RemoveTombstone =
            fun s r ->
                let removed = tombstones.RemoveAll(fun x -> x.SupplierId = s && InvoiceReference.value x.Reference = InvoiceReference.value r)
                Ok(removed > 0)
          LoadScanProblems = fun () -> Ok(List.ofSeq problems)
          SaveScanProblems =
            fun ps ->
                for p in ps do
                    problems.RemoveAll(fun x -> x.SourceMessageId = p.SourceMessageId) |> ignore
                    problems.Add p
                Ok()
          ClearScanProblems =
            fun ids ->
                for id in ids do
                    problems.RemoveAll(fun x -> SourceMessageId.value x.SourceMessageId = SourceMessageId.value id) |> ignore
                Ok()
          LoadScanWindows = fun () -> Ok windows
          SaveScanWindow =
            fun d ->
                nextId <- nextId + 1
                let w = { Id = ScanWindowId.create (string nextId) |> orFail; Days = d }
                windows <- windows @ [ w ]
                Ok w
          LoadSelectedScanWindow = fun () -> Ok selected
          SaveSelectedScanWindow = fun d -> selected <- Some d; Ok() }

    test deps

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
    run name (fun d ->
        d.UpsertInvoice(invoice "INV-1") |> orFail |> ignore
        d.UpsertInvoice(invoice "INV-1") |> orFail |> ignore
        Assert.Equal(1, d.LoadInvoices None |> orFail |> List.length))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``the cutoff filter hides an invoice whose message is older`` (name: string) =
    run name (fun d ->
        d.UpsertInvoice({ invoice "OLD" with MessageReceivedAt = DateTime(2026, 1, 1) }) |> orFail |> ignore
        d.UpsertInvoice({ invoice "NEW" with MessageReceivedAt = DateTime(2026, 5, 30) }) |> orFail |> ignore
        let inWindow = d.LoadInvoices(Some(ScanCutoff.ofStartOfDay (DateTime(2026, 5, 1)))) |> orFail
        Assert.Equal<string list>([ "NEW" ], inWindow |> List.map (fun i -> InvoiceReference.value i.Invoice.Reference)))

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
    run name (fun d ->
        // supplier 2 is not in the store: withReal seeds only supplier 1, and so does withFake.
        let orphan = { invoice "INV-ORPHAN" with SupplierId = SupplierId.create "2" |> orFail }

        match d.UpsertInvoice orphan with
        | Error(SupplierGone id) -> Assert.Equal("2", SupplierId.value id)
        | Error other -> Assert.Fail($"expected SupplierGone, got {other}")
        | Ok _ -> Assert.Fail("expected the write to be refused - there is no supplier 2")

        // and nothing was stored for it
        Assert.Empty(d.LoadInvoices None |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a tombstone is saved once, loaded, and removed exactly once`` (name: string) =
    run name (fun d ->
        let t: InvoiceTombstone =
            { SupplierId = SupplierId.create "1" |> orFail
              Reference = InvoiceReference.create "INV-7" |> orFail
              DeletedAt = DateTime(2026, 5, 1) }

        d.SaveTombstone t |> orFail
        d.SaveTombstone t |> orFail
        Assert.Equal(1, d.LoadTombstones() |> orFail |> List.length)
        Assert.True(d.RemoveTombstone t.SupplierId t.Reference |> orFail)
        Assert.False(d.RemoveTombstone t.SupplierId t.Reference |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``scan problems are replaced per message and cleared by id`` (name: string) =
    run name (fun d ->
        let problem cause : ScanProblem =
            { SourceMessageId = SourceMessageId.create "m1" |> orFail
              Sender = "a@b.test"
              Subject = "s"
              ReceivedAt = DateTime(2026, 5, 20)
              Cause = cause
              RecordedAt = DateTime(2026, 6, 1) }

        d.SaveScanProblems [ problem NoSupplierMatched ] |> orFail
        d.SaveScanProblems [ problem (NoTemplateMatched(SupplierId.create "1" |> orFail)) ] |> orFail
        let after = d.LoadScanProblems() |> orFail
        Assert.Equal(1, List.length after)
        Assert.Equal(NoTemplateMatched(SupplierId.create "1" |> orFail), (List.head after).Cause)
        d.ClearScanProblems [ SourceMessageId.create "m1" |> orFail ] |> orFail
        Assert.Empty(d.LoadScanProblems() |> orFail))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``scan windows: seeded five present, add appends, the selected number persists`` (name: string) =
    run name (fun d ->
        Assert.Equal<int list>(
            [ 7; 14; 30; 90; 180 ],
            d.LoadScanWindows() |> orFail |> List.map (fun w -> ScanWindowDays.value w.Days) |> List.sort
        )

        d.SaveScanWindow(ScanWindowDays.create 45 |> orFail) |> orFail |> ignore
        Assert.Contains(45, d.LoadScanWindows() |> orFail |> List.map (fun w -> ScanWindowDays.value w.Days))

        Assert.Equal(None, d.LoadSelectedScanWindow() |> orFail)
        d.SaveSelectedScanWindow(ScanWindowDays.create 90 |> orFail) |> orFail
        Assert.Equal(Some 90, d.LoadSelectedScanWindow() |> orFail |> Option.map ScanWindowDays.value))
