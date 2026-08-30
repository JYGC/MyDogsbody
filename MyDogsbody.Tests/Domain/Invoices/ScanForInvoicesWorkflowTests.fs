module MyDogsbody.Tests.Domain.Invoices.ScanForInvoicesWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Invoices.ScanForInvoicesWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private window (d: int) = ScanWindowDays.create d |> orFail
let private clock (instant: DateTime) : GetCurrentTime = fun () -> instant

// ============================ shared scan test kit ============================

let private accountId = MailAccountId.create "acct-1" |> orFail

let private matcher kind value = SupplierMatcher.create kind value |> orFail

/// A supplier that matches anything from acme.test, net 0.
let private acme =
    { Id = SupplierId.create "acme" |> orFail
      Name = SupplierName.create "Acme Pty Ltd" |> orFail
      PaymentTermDays = PaymentTermDays.create 0 |> orFail
      Matchers = [ matcher Domain "acme.test" ] }

/// A template: Reference from "Invoice:", Amount from "Total:", Currency fixed AUD.
let private acmeTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = "acme"
          Name = "Acme default"
          Part = AnyPart
          Position = 0
          Rules =
            [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
              { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ] }

    { Id = TemplateId.create "acme-t1" |> orFail
      Template = ValidateTemplateWorkflow.validateTemplate unvalidated |> orFail }

/// A mail message from Acme whose plain-text body carries the two labels.
let private acmeMessage (id: string) (reference: string) : MailMessage =
    { SourceMessageId = id
      Sender = "billing@acme.test"
      Subject = $"Invoice {reference}"
      ReceivedAt = DateTime(2026, 6, 1, 10, 0, 0)
      BodyText = Some $"Invoice: {reference}\nTotal: 100.00"
      BodyHtml = None
      Attachments = [] }

/// A reader that decodes bytes and splits on newlines - format-agnostic, enough for body text.
let private textReader: ReadDocumentText =
    fun source ->
        Text.Encoding.UTF8.GetString source.Content
        |> fun s -> s.Replace("\r\n", "\n").Split('\n')
        |> Array.toList
        |> List.mapi (fun i t -> { Text = t; BlockIndex = i })
        |> Ok

/// Records every ValidInvoice upserted and assigns it a StoredInvoice id, upserting on the
/// natural key so a rescan updates rather than adds.
type private FakeLedger() =
    let mutable rows: (string * string * ValidInvoice) list = []
    member _.Rows = rows

    member _.Upsert: UpsertInvoice =
        fun invoice ->
            let key = SupplierId.value invoice.SupplierId, InvoiceReference.value invoice.Reference

            rows <-
                (rows |> List.filter (fun (s, r, _) -> (s, r) <> key))
                @ [ fst key, snd key, invoice ]

            let index = rows |> List.findIndex (fun (s, r, _) -> (s, r) = key)

            Ok
                { Id = InvoiceId.create $"row-{index}" |> orFail
                  Invoice = invoice
                  ScannedAt = DateTime(2026, 6, 1) }

/// Records problem batches saved and message-id batches cleared.
type private FakeProblemLog() =
    member val Saved: ScanProblem list list = [] with get, set
    member val Cleared: SourceMessageId list list = [] with get, set

    member this.Save: SaveScanProblems =
        fun problems ->
            this.Saved <- this.Saved @ [ problems ]
            Ok()

    member this.Clear: ClearScanProblems =
        fun ids ->
            this.Cleared <- this.Cleared @ [ ids ]
            Ok()

/// The default happy-path wiring; individual tests override one dependency.
type private Deps =
    { GetCurrentTime: GetCurrentTime
      LoadSelectedMailAccount: LoadSelectedMailAccount
      ClearWatermarks: ClearWatermarks
      ReadMailFolder: ReadMailFolder
      ReadDocumentText: ReadDocumentText
      LoadSuppliers: LoadSuppliers
      LoadTemplatesForSupplier: LoadTemplatesForSupplier
      LoadTombstones: LoadTombstones
      UpsertInvoice: UpsertInvoice
      SaveScanProblems: SaveScanProblems
      ClearScanProblems: ClearScanProblems }

let private runWith (mode: ScanMode) (deps: Deps) (windowDays: int) =
    scanForInvoices
        deps.GetCurrentTime
        deps.LoadSelectedMailAccount
        deps.ClearWatermarks
        deps.ReadMailFolder
        deps.ReadDocumentText
        deps.LoadSuppliers
        deps.LoadTemplatesForSupplier
        deps.LoadTombstones
        deps.UpsertInvoice
        deps.SaveScanProblems
        deps.ClearScanProblems
        mode
        (window windowDays)

/// The ordinary path: resume each folder from its watermark.
let private run (deps: Deps) (windowDays: int) = runWith IncrementalScan deps windowDays

/// Records every MailAccountId handed to clearWatermarks, so a test can prove FullRescan clears
/// and IncrementalScan does not, and that a fatal scan resets on the way out.
type private ClearWatermarksSpy() =
    member val Calls: MailAccountId list = [] with get, set
    member val Result: Result<unit, MailAccountError> = Ok() with get, set

    member this.Dependency: ClearWatermarks =
        fun id ->
            this.Calls <- this.Calls @ [ id ]
            this.Result

let private baseDeps (messages: MailMessage list) (ledger: FakeLedger) (log: FakeProblemLog) : Deps =
    { GetCurrentTime = clock (DateTime(2026, 6, 15, 12, 0, 0))
      LoadSelectedMailAccount = fun () -> Ok(Some accountId)
      ClearWatermarks = fun _ -> Ok()
      ReadMailFolder = fun _ _ -> Ok messages
      ReadDocumentText = textReader
      LoadSuppliers = fun () -> Ok [ acme ]
      LoadTemplatesForSupplier = fun _ -> Ok [ acmeTemplate ]
      LoadTombstones = fun () -> Ok []
      UpsertInvoice = ledger.Upsert
      SaveScanProblems = log.Save
      ClearScanProblems = log.Clear }

// ---- task 3.1: cutoff arithmetic, fixed clock, exact dates ----

[<Fact; Trait("Level", "Unit")>]
let ``14 days back from a fixed date is an exact date at the start of the day`` () =
    let cutoff = computeCutoff (clock (DateTime(2026, 1, 20, 13, 45, 0))) (window 14)
    Assert.Equal(DateTime(2026, 1, 6), ScanCutoff.value cutoff)

[<Fact; Trait("Level", "Unit")>]
let ``the same window at 09:00 and 17:00 on one day gives the same cutoff`` () =
    let morning = computeCutoff (clock (DateTime(2026, 6, 10, 9, 0, 0))) (window 30)
    let evening = computeCutoff (clock (DateTime(2026, 6, 10, 17, 0, 0))) (window 30)
    Assert.Equal(ScanCutoff.value morning, ScanCutoff.value evening)
    Assert.Equal(DateTime(2026, 5, 11), ScanCutoff.value morning)

[<Fact; Trait("Level", "Unit")>]
let ``180 days back crosses a year boundary correctly`` () =
    let cutoff = computeCutoff (clock (DateTime(2026, 3, 1))) (window 180)
    // 2026-03-01 minus 180 days
    Assert.Equal(DateTime(2026, 3, 1).AddDays(-180.0), ScanCutoff.value cutoff)
    Assert.Equal(2025, (ScanCutoff.value cutoff).Year)

[<Theory; Trait("Level", "Unit")>]
[<InlineData(1)>]
[<InlineData(3650)>]
let ``a one-day and a ten-year window both land on the start of the day that many days back`` (d: int) =
    let now = DateTime(2026, 7, 4, 23, 59, 0)
    let cutoff = computeCutoff (clock now) (window d)
    Assert.Equal(now.Date.AddDays(float -d), ScanCutoff.value cutoff)
    Assert.Equal(TimeSpan.Zero, (ScanCutoff.value cutoff).TimeOfDay)

// ============================ task 4.1: the scan ============================

[<Fact; Trait("Level", "Unit")>]
let ``a message matching a supplier and template becomes a stored invoice, every field asserted`` () =
    let ledger = FakeLedger()
    let log = FakeProblemLog()

    match run (baseDeps [ acmeMessage "m1" "INV-900" ] ledger log) 30 with
    | Ok result ->
        Assert.Empty result.Problems
        let stored = Assert.Single result.Invoices
        Assert.Equal("row-0", InvoiceId.value stored.Id)
        let invoice = stored.Invoice
        Assert.Equal("acme", SupplierId.value invoice.SupplierId)
        Assert.Equal("acme-t1", TemplateId.value invoice.TemplateId)
        Assert.Equal("m1", SourceMessageId.value invoice.SourceMessageId)
        Assert.Equal("INV-900", InvoiceReference.value invoice.Reference)
        Assert.Equal(100.00m, Money.amount invoice.Amount)
        Assert.Equal("AUD", Money.currency invoice.Amount)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``no account selected short-circuits with the mail reader never called`` () =
    let mutable readCalled = false

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            LoadSelectedMailAccount = fun () -> Ok None
            ReadMailFolder =
                fun _ _ ->
                    readCalled <- true
                    Ok [] }

    match run deps 14 with
    | Error NoAccountSelected -> Assert.False(readCalled, "readMailFolder must not be called")
    | other -> Assert.Fail($"Expected Error NoAccountSelected, got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``the cutoff handed to the mail reader is the one computeCutoff produces`` () =
    let mutable seen: ScanCutoff option = None
    let now = DateTime(2026, 6, 15, 12, 0, 0)

    let deps =
        { baseDeps [] (FakeLedger()) (FakeProblemLog()) with
            GetCurrentTime = clock now
            ReadMailFolder =
                fun _ cutoff ->
                    seen <- Some cutoff
                    Ok [] }

    run deps 90 |> ignore

    Assert.Equal(
        Some(ScanCutoff.value (computeCutoff (clock now) (window 90))),
        seen |> Option.map ScanCutoff.value
    )

[<Fact; Trait("Level", "Unit")>]
let ``a message that yields nothing produces a problem and the scan continues`` () =
    let ledger = FakeLedger()
    let log = FakeProblemLog()
    let stranger = { acmeMessage "m-stranger" "X" with Sender = "noreply@unknown.test" }
    let deps = baseDeps [ stranger; acmeMessage "m-good" "INV-2" ] ledger log

    match run deps 30 with
    | Ok result ->
        Assert.Single result.Invoices |> ignore
        let problem = Assert.Single result.Problems
        Assert.Equal("m-stranger", SourceMessageId.value problem.SourceMessageId)
        Assert.Equal("noreply@unknown.test", problem.Sender)
        Assert.Equal(NoSupplierMatched, problem.Cause)
        Assert.Equal<ScanProblem list list>([ result.Problems ], log.Saved)
        Assert.Equal<SourceMessageId list list>([ [ SourceMessageId.create "m-good" |> orFail ] ], log.Cleared)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``two suppliers matching one message is a problem naming all of them`` () =
    let acme2 = { acme with Id = SupplierId.create "acme-2" |> orFail }

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            LoadSuppliers = fun () -> Ok [ acme; acme2 ] }

    match run deps 30 with
    | Ok result ->
        Assert.Empty result.Invoices

        match (Assert.Single result.Problems).Cause with
        | SeveralSuppliersMatched ids ->
            Assert.Equal<string list>([ "acme"; "acme-2" ], ids |> List.map SupplierId.value |> List.sort)
        | other -> Assert.Fail($"Expected SeveralSuppliersMatched, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``no template for the matched supplier is a NoTemplateMatched problem`` () =
    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            LoadTemplatesForSupplier = fun _ -> Ok [] }

    match run deps 30 with
    | Ok result ->
        match (Assert.Single result.Problems).Cause with
        | NoTemplateMatched sid -> Assert.Equal("acme", SupplierId.value sid)
        | other -> Assert.Fail($"Expected NoTemplateMatched, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``a rule that finds nothing is RuleFoundNothing naming supplier, template and field`` () =
    let missingRef = { acmeMessage "m1" "X" with BodyText = Some "Total: 100.00" }

    match run (baseDeps [ missingRef ] (FakeLedger()) (FakeProblemLog())) 30 with
    | Ok result ->
        match (Assert.Single result.Problems).Cause with
        | RuleFoundNothing(sid, tid, field) ->
            Assert.Equal("acme", SupplierId.value sid)
            Assert.Equal("acme-t1", TemplateId.value tid)
            Assert.Equal("Reference", field)
        | other -> Assert.Fail($"Expected RuleFoundNothing, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``an unreadable attachment on an unmatched message is an AttachmentUnreadable problem`` () =
    let brokenReader: ReadDocumentText =
        fun source ->
            if source.Name.EndsWith ".pdf" then Error(DocumentUnreadable "corrupt") else textReader source

    let stranger =
        { acmeMessage "m1" "X" with
            Sender = "noreply@unknown.test"
            Attachments = [ { FileName = "invoice.pdf"; DeclaredContentType = "application/pdf"; Content = [| 1uy |] } ] }

    let deps =
        { baseDeps [ stranger ] (FakeLedger()) (FakeProblemLog()) with ReadDocumentText = brokenReader }

    match run deps 30 with
    | Ok result ->
        match (Assert.Single result.Problems).Cause with
        | AttachmentUnreadable("invoice.pdf", reason) -> Assert.Equal("corrupt", reason)
        | other -> Assert.Fail($"Expected AttachmentUnreadable, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

// ---- the same two causes on a message whose supplier IS recognised ----
//
// requirements.md asks for eight DISTINGUISHABLE causes, two of which are about the attachment
// itself: "an attachment was unreadable" and "the attachment's format is unsupported" - the latter
// so "the question of whether to build a reader for it can later be answered from data", and a
// legacy .doc explicitly "SHALL NOT" be skipped silently. Those two facts are produced by
// scanMessage for every message, but were consulted only when NO supplier matched - the one case
// where the attachment matters least, since the message may not even be an invoice.
//
// For a configured supplier - the case the feature exists for - they were dropped, and the message
// was reported under whatever the template concluded instead: "Acme's template found nothing for
// the Reference field" when the truth is "invoice.pdf could not be read". Nothing anywhere else
// records the attachment fact, so it was lost.

/// A message from Acme (so the supplier matches) whose subject and body carry neither label, so
/// the template can only fail - leaving which cause is reported as the thing under test.
let private acmeMessageCarryingOnly (attachment: MailAttachment) (id: string) : MailMessage =
    { acmeMessage id "X" with
        Subject = "Statement attached"
        BodyText = Some "Thanks for your business."
        Attachments = [ attachment ] }

[<Fact; Trait("Level", "Unit")>]
let ``an unreadable attachment on a matched supplier's message is an AttachmentUnreadable problem, not the template's`` () =
    let brokenReader: ReadDocumentText =
        fun source ->
            if source.Name.EndsWith ".pdf" then Error(DocumentUnreadable "corrupt") else textReader source

    let message =
        acmeMessageCarryingOnly
            { FileName = "invoice.pdf"; DeclaredContentType = "application/pdf"; Content = [| 1uy |] }
            "m-broken"

    let deps =
        { baseDeps [ message ] (FakeLedger()) (FakeProblemLog()) with ReadDocumentText = brokenReader }

    match run deps 30 with
    | Ok result ->
        Assert.Empty result.Invoices
        let problem = Assert.Single result.Problems
        Assert.Equal(AttachmentUnreadable("invoice.pdf", "corrupt"), problem.Cause)
        Assert.Equal("m-broken", SourceMessageId.value problem.SourceMessageId)
        Assert.Equal("billing@acme.test", problem.Sender)
        Assert.Equal("Statement attached", problem.Subject)
        Assert.Equal(DateTime(2026, 6, 1, 10, 0, 0), problem.ReceivedAt)
        Assert.Equal(DateTime(2026, 6, 15, 12, 0, 0), problem.RecordedAt)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``an unsupported attachment format on a matched supplier's message names the format`` () =
    let message =
        acmeMessageCarryingOnly
            { FileName = "statement.xlsx"
              DeclaredContentType = "application/octet-stream"
              Content = [| 1uy |] }
            "m-xlsx"

    match run (baseDeps [ message ] (FakeLedger()) (FakeProblemLog())) 30 with
    | Ok result ->
        Assert.Empty result.Invoices
        Assert.Equal(FormatUnsupported("statement.xlsx", "xlsx"), (Assert.Single result.Problems).Cause)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

/// The masked cause that matters most in practice - and the one the measured run actually produced
/// (outcome.md 12.5: NoTemplateMatched, 2 messages). SelectTemplateWorkflow filters out every
/// template whose DocumentPart the message does not carry, and an attachment that failed to read is
/// not among the message's parts. So a supplier that HAS a PDF template, sent an unreadable PDF, was
/// filed as NoTemplateMatched - the opposite of the truth, and it sends the user to the template
/// editor to add a template that is already there. This is why the preference covers the whole
/// selectTemplate branch and not only the value-extraction cases.
[<Fact; Trait("Level", "Unit")>]
let ``a supplier whose PDF template cannot see an unreadable PDF reports the attachment, not a missing template`` () =
    let pdfTemplate =
        let unvalidated: UnvalidatedTemplate =
            { SupplierId = "acme"
              Name = "Acme PDF"
              Part = Attachment Pdf
              Position = 0
              Rules =
                [ { Field = Reference; Rule = AfterLabel "Invoice:"; Hint = AsText }
                  { Field = Amount; Rule = AfterLabel "Total:"; Hint = AsMoney '.' }
                  { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ] }

        { Id = TemplateId.create "acme-pdf" |> orFail
          Template = ValidateTemplateWorkflow.validateTemplate unvalidated |> orFail }

    let brokenReader: ReadDocumentText =
        fun source ->
            if source.Name.EndsWith ".pdf" then Error(DocumentUnreadable "corrupt") else textReader source

    let message =
        acmeMessageCarryingOnly
            { FileName = "invoice.pdf"; DeclaredContentType = "application/pdf"; Content = [| 1uy |] }
            "m-pdf-template"

    let deps =
        { baseDeps [ message ] (FakeLedger()) (FakeProblemLog()) with
            LoadTemplatesForSupplier = fun _ -> Ok [ pdfTemplate ]
            ReadDocumentText = brokenReader }

    match run deps 30 with
    | Ok result ->
        Assert.Empty result.Invoices
        Assert.Equal(AttachmentUnreadable("invoice.pdf", "corrupt"), (Assert.Single result.Problems).Cause)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

/// The other side of the rule, so it cannot degrade into "always blame the attachment": when every
/// attachment read cleanly there is no attachment fact to report, and the template's own conclusion
/// stands. Passes before the fix as well as after - a guard, not a demonstration.
[<Fact; Trait("Level", "Unit")>]
let ``a matched supplier's message whose attachments all read is still reported as the template's own cause`` () =
    let message =
        acmeMessageCarryingOnly
            { FileName = "notes.txt"
              DeclaredContentType = "text/plain"
              Content = Text.Encoding.UTF8.GetBytes "nothing useful here" }
            "m-readable"

    match run (baseDeps [ message ] (FakeLedger()) (FakeProblemLog())) 30 with
    | Ok result ->
        match (Assert.Single result.Problems).Cause with
        | RuleFoundNothing(sid, tid, field) ->
            Assert.Equal("acme", SupplierId.value sid)
            Assert.Equal("acme-t1", TemplateId.value tid)
            Assert.Equal("Reference", field)
        | other -> Assert.Fail($"Expected RuleFoundNothing, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

/// The boundary of the rule, pinned deliberately: "two suppliers matched" is decided BEFORE any
/// template is tried, so the attachment is not the diagnostic - the user has to narrow the matchers
/// whatever the attachment turned out to be. Passes before the fix as well as after.
[<Fact; Trait("Level", "Unit")>]
let ``two suppliers matching stays SeveralSuppliersMatched even when an attachment could not be read`` () =
    let acme2 = { acme with Id = SupplierId.create "acme-2" |> orFail }

    let brokenReader: ReadDocumentText =
        fun source ->
            if source.Name.EndsWith ".pdf" then Error(DocumentUnreadable "corrupt") else textReader source

    let message =
        acmeMessageCarryingOnly
            { FileName = "invoice.pdf"; DeclaredContentType = "application/pdf"; Content = [| 1uy |] }
            "m-two"

    let deps =
        { baseDeps [ message ] (FakeLedger()) (FakeProblemLog()) with
            LoadSuppliers = fun () -> Ok [ acme; acme2 ]
            ReadDocumentText = brokenReader }

    match run deps 30 with
    | Ok result ->
        match (Assert.Single result.Problems).Cause with
        | SeveralSuppliersMatched ids ->
            Assert.Equal<string list>([ "acme"; "acme-2" ], ids |> List.map SupplierId.value |> List.sort)
        | other -> Assert.Fail($"Expected SeveralSuppliersMatched, got {other}")
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

// ============================ task 4.2: upsert and the natural key ============================

[<Fact; Trait("Level", "Unit")>]
let ``rescanning an overlapping window updates rather than duplicates`` () =
    let ledger = FakeLedger()

    run (baseDeps [ acmeMessage "m1" "INV-5" ] ledger (FakeProblemLog())) 30 |> ignore

    let updated = { acmeMessage "m1" "INV-5" with BodyText = Some "Invoice: INV-5\nTotal: 250.00" }
    let result = run (baseDeps [ updated ] ledger (FakeProblemLog())) 30 |> orFail

    Assert.Equal(1, List.length result.Invoices)
    Assert.Equal(1, List.length ledger.Rows)
    let _, _, stored = List.head ledger.Rows
    Assert.Equal(250.00m, Money.amount stored.Amount)

[<Fact; Trait("Level", "Unit")>]
let ``a supplier deleted at upsert time becomes a problem, not a stored row`` () =
    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            UpsertInvoice = fun invoice -> Error(SupplierGone invoice.SupplierId) }

    match run deps 30 with
    | Ok result ->
        Assert.Empty result.Invoices
        Assert.Equal(NoSupplierMatched, (Assert.Single result.Problems).Cause)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``a store failure at upsert aborts the scan with that error`` () =
    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            UpsertInvoice = fun _ -> Error(InvoiceStoreFailed "disk full") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "disk full") -> ()
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed), got {other}")

// ============================ task 4.3: tombstones ============================

[<Fact; Trait("Level", "Unit")>]
let ``a tombstoned key is skipped - no invoice, no problem`` () =
    let ledger = FakeLedger()

    let tombstone: InvoiceTombstone =
        { SupplierId = SupplierId.create "acme" |> orFail
          Reference = InvoiceReference.create "INV-7" |> orFail
          DeletedAt = DateTime(2026, 5, 1) }

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-7" ] ledger (FakeProblemLog()) with
            LoadTombstones = fun () -> Ok [ tombstone ] }

    let result = run deps 30 |> orFail
    Assert.Empty result.Invoices
    Assert.Empty result.Problems
    Assert.Empty ledger.Rows

[<Fact; Trait("Level", "Unit")>]
let ``removing the tombstone lets the next scan store the invoice again`` () =
    let ledger = FakeLedger()
    let result = run (baseDeps [ acmeMessage "m1" "INV-7" ] ledger (FakeProblemLog())) 30 |> orFail
    Assert.Equal(1, List.length result.Invoices)

// ============================ task 4.4: problem lifecycle ============================

[<Fact; Trait("Level", "Unit")>]
let ``a scan clears problems only for the messages it processed and which now succeed`` () =
    let log = FakeProblemLog()
    run (baseDeps [ acmeMessage "only-this-one" "INV-1" ] (FakeLedger()) log) 30 |> ignore

    Assert.Equal<SourceMessageId list list>(
        [ [ SourceMessageId.create "only-this-one" |> orFail ] ],
        log.Cleared
    )

[<Fact; Trait("Level", "Unit")>]
let ``a scan with only failures saves problems and clears nothing`` () =
    let log = FakeProblemLog()
    let stranger = { acmeMessage "m1" "X" with Sender = "noreply@unknown.test" }
    run (baseDeps [ stranger ] (FakeLedger()) log) 30 |> ignore

    Assert.Equal(1, List.length log.Saved)
    Assert.Empty log.Cleared

// ============================ Phase 16: FullRescan clears the watermarks ============================
//
// A folder's watermark records how far it was read, and resumeOffset skips a message older than the
// cutoff before its body is parsed - so a folder scanned once with no supplier configured advanced
// to EOF having extracted nothing, and every IncrementalScan after that sees none of that mail.
// FullRescan ("Rescan everything") clears the selected account's watermarks first.

[<Fact; Trait("Level", "Unit")>]
let ``FullRescan clears the resolved account's watermarks once, before the mail reader runs`` () =
    let spy = ClearWatermarksSpy()
    let events = ResizeArray<string>()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks =
                fun id ->
                    events.Add "clear"
                    spy.Dependency id
            ReadMailFolder =
                fun _ _ ->
                    events.Add "read"
                    Ok [ acmeMessage "m1" "INV-1" ] }

    match runWith FullRescan deps 30 with
    | Ok _ ->
        Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
        Assert.Equal<string list>([ "clear"; "read" ], List.ofSeq events)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``an IncrementalScan never clears the watermarks`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency }

    runWith IncrementalScan deps 30 |> ignore
    Assert.Empty spy.Calls

[<Fact; Trait("Level", "Unit")>]
let ``a clearWatermarks failure on a FullRescan aborts the scan and the mail reader is never called`` () =
    let spy = ClearWatermarksSpy(Result = Error(MailStoreFailed "watermark store is unreachable"))
    let mutable readCalled = false

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            ReadMailFolder =
                fun _ _ ->
                    readCalled <- true
                    Ok [] }

    match runWith FullRescan deps 30 with
    | Error(InvoiceStoreFailed msg) ->
        Assert.Contains("watermark store is unreachable", msg)
        Assert.False(readCalled, "readMailFolder must not run after the pre-clear failed")
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed), got {other}")

// ============================ Phase 17: a fatal scan resets the watermarks ============================
//
// readFolder saves each folder's watermark as part of `read`, which the workflow calls BEFORE it
// processes a message. A fatal error mid-processing would otherwise strand every unprocessed
// message behind a watermark at EOF - no invoice, no problem, nothing on screen.

[<Fact; Trait("Level", "Unit")>]
let ``a fatal store error at upsert resets the account's watermarks and still returns that error`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            UpsertInvoice = fun _ -> Error(InvoiceStoreFailed "disk full") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "disk full") -> Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed \"disk full\"), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a scan that completes does not reset the watermarks`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency }

    match run deps 30 with
    | Ok _ -> Assert.Empty spy.Calls
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

[<Fact; Trait("Level", "Unit")>]
let ``a clearWatermarks failure on the fatal path does not replace the fatal error`` () =
    let spy = ClearWatermarksSpy(Result = Error(MailStoreFailed "the reset failed too"))

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            UpsertInvoice = fun _ -> Error(InvoiceStoreFailed "disk full") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "disk full") -> ()
    | other -> Assert.Fail($"Expected the original Error (InvoiceStoreFailed \"disk full\"), got {other}")

// ---- every OTHER abort after the mailbox was read resets them too ----
//
// requirements.md: "WHEN a scan aborts on a fatal error (a store or reader failure, not a
// per-message problem) THE SYSTEM SHALL leave the selected account's scan watermarks such that the
// next scan re-reads every message this scan did not finish processing, and SHALL NOT advance them
// past mail it read but never turned into an invoice or a problem."
//
// `readMailFolder` advances every folder's watermark to EOF as part of reading, so the four steps
// AFTER it - loadSuppliers, loadTombstones, saveScanProblems, clearScanProblems - abort over mail
// that is already marked "already read". Each is a store failure of exactly the class the
// ScanAcc.Fatal branch resets for.

[<Fact; Trait("Level", "Unit")>]
let ``a loadSuppliers failure after the mailbox was read resets the watermarks and returns that error`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            LoadSuppliers = fun () -> Error(SupplierStoreFailed "suppliers unreachable") }

    match run deps 30 with
    | Error(InvoiceStoreFailed msg) ->
        Assert.Contains("suppliers unreachable", msg)
        Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a loadTombstones failure after the mailbox was read resets the watermarks and returns that error`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            LoadTombstones = fun () -> Error(InvoiceStoreFailed "tombstones unreachable") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "tombstones unreachable") ->
        Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed \"tombstones unreachable\"), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a saveScanProblems failure resets the watermarks and returns that error`` () =
    let spy = ClearWatermarksSpy()
    let stranger = { acmeMessage "m1" "X" with Sender = "noreply@unknown.test" }

    let deps =
        { baseDeps [ stranger ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            SaveScanProblems = fun _ -> Error(InvoiceStoreFailed "problem store unreachable") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "problem store unreachable") ->
        Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed \"problem store unreachable\"), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a clearScanProblems failure resets the watermarks and returns that error`` () =
    let spy = ClearWatermarksSpy()

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            ClearScanProblems = fun _ -> Error(InvoiceStoreFailed "problem store unreachable") }

    match run deps 30 with
    | Error(InvoiceStoreFailed "problem store unreachable") ->
        Assert.Equal<MailAccountId list>([ accountId ], spy.Calls)
    | other -> Assert.Fail($"Expected Error (InvoiceStoreFailed \"problem store unreachable\"), got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a clearWatermarks failure on the loadSuppliers abort does not replace that error`` () =
    let spy = ClearWatermarksSpy(Result = Error(MailStoreFailed "the reset failed too"))

    let deps =
        { baseDeps [ acmeMessage "m1" "INV-1" ] (FakeLedger()) (FakeProblemLog()) with
            ClearWatermarks = spy.Dependency
            LoadSuppliers = fun () -> Error(SupplierStoreFailed "suppliers unreachable") }

    match run deps 30 with
    | Error(InvoiceStoreFailed msg) ->
        Assert.Contains("suppliers unreachable", msg)
        Assert.DoesNotContain("the reset failed too", msg)
    | other -> Assert.Fail($"Expected the original Error (InvoiceStoreFailed), got {other}")

