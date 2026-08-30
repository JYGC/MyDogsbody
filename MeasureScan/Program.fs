module MeasureScan.Program

open System
open System.Diagnostics
open System.IO
open Microsoft.Data.Sqlite
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Database
open MyDogsbody.Database.Migrations
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// ======================================================================================
//  CONFIG
// ======================================================================================

/// The folder the app's folder picker points at.
let profileRoot =
    @"C:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default"

/// How far back to scan for the measurement. Every account is scanned; a scan-window row is what
/// the app's picker offers, but the harness just passes this int (Scan accepts 1..3650).
/// 730 = 2 years, to catch quarterly / annual billers (IODM, Xero, OC Energy) that a 180-day
/// window missed - the 2026-08-29 discovery run at 180 saw only YVW once.
let windowDays = 730

type SupplierConfig =
    { Name: string
      /// "Domain" (match on the part after the last @ of the From header) or "Subject" (substring).
      MatcherKind: string
      MatcherValue: string
      /// Only load-bearing for a supplier that prints no due date - due = issue + this many days.
      PaymentTermDays: int
      /// The date format the supplier's PDF actually uses. AsDate takes it literally, no culture
      /// fallback. Common: "d MMM yyyy", "d/M/yyyy", "d/M/yy", "MMM d, yyyy".
      DateFormat: string }

/// Leave any MatcherValue as "REPLACE..." and the harness runs in DISCOVERY mode: it scans every
/// account with no suppliers and prints the sender-domain frequency table, so you can see which
/// domains your invoices actually come from. Fill these in, run again, get the coverage numbers.
///
/// 2026-08-29: three suppliers that appear as direct biller mail in the 730-day discovery run.
/// The templates are best-effort - the real PDF/body layouts are unknown - so a low extraction
/// count here IS the friction-#19 finding, not a template bug.
let suppliers: SupplierConfig list =
    [ { Name = "InkStation"
        MatcherKind = "Domain"
        MatcherValue = "inkstation.com.au"
        PaymentTermDays = 0
        DateFormat = "d/MM/yyyy" }
      { Name = "Plumbing Bros"
        MatcherKind = "Subject"
        MatcherValue = "Invoice I"
        PaymentTermDays = 7
        DateFormat = "d MMM yyyy" }
      { Name = "HCF"
        MatcherKind = "Domain"
        MatcherValue = "hcf.com.au"
        PaymentTermDays = 14
        DateFormat = "d MMMM yyyy" } ]

// ======================================================================================

let private mainDb = "measure.db"
let private thunderbirdDb = "Thunderbird.db"
let private suppliersConfigured = not (suppliers |> List.exists (fun s -> s.MatcherValue.StartsWith "REPLACE"))

let private rule field kind text offset sourceField hintKind hintText : TemplateFieldRuleUiType =
    { Field = field; RuleKind = kind; RuleText = text; RuleOffset = offset
      RuleSourceField = sourceField; HintKind = hintKind; HintText = hintText }

let private templatesFor (s: SupplierConfig) (supplierId: string) (deriveDueDates: bool) : TemplateUiTypeWithoutId list =
    let make part attFmt name position rules : TemplateUiTypeWithoutId =
        { SupplierId = supplierId; Name = name; DocumentPart = part
          AttachmentFormat = attFmt; Position = position; Rules = rules }

    let pdf = make "Attachment" "Pdf"
    let body = make "AnyPart" "" // AttachmentFormat is ignored unless DocumentPart = "Attachment"

    let derived =
        if deriveDueDates then [ rule "DueDate" "DateFromField" "" 0 "IssueDate" "AsText" "" ] else []

    match s.Name with
    | "InkStation" ->
        // e-commerce tax invoice, PDF attachment; "paid on order" so due = issue.
        [ pdf "InkStation tax invoice" 0 (
            [ rule "Reference" "SubjectCapture" "(A\\d+)" 0 "" "AsText" ""
              rule "Amount" "AfterLabel" "Total (inc GST)" 0 "" "AsMoney" "."
              rule "Currency" "FixedValue" "AUD" 0 "" "AsText" ""
              rule "IssueDate" "AfterLabel" "Invoice Date" 0 "" "AsDate" s.DateFormat ]
            @ derived) ]
    | "Plumbing Bros" ->
        // Xero-generated ("Invoice I6386"), PDF attachment.
        [ pdf "Xero-style invoice" 0 [
            rule "Reference" "RegexCapture" "Invoice\\s+(I\\d+)" 0 "" "AsText" ""
            rule "Amount" "LinesAfterLabel" "Amount Due" 1 "" "AsMoney" "."
            rule "Currency" "FixedValue" "AUD" 0 "" "AsText" ""
            rule "IssueDate" "AfterLabel" "Invoice Date" 0 "" "AsDate" s.DateFormat
            rule "DueDate" "AfterLabel" "Due Date" 0 "" "AsDate" s.DateFormat ] ]
    | _ -> // HCF - premium notice, details in the email body
        [ body "HCF premium notice" 0 (
            [ rule "Reference" "RegexCapture" "([Mm]embership\\s+(?:number|no\\.?)\\s*:?\\s*\\d+)" 0 "" "AsText" ""
              rule "Amount" "AfterLabel" "Amount due" 0 "" "AsMoney" "."
              rule "Currency" "FixedValue" "AUD" 0 "" "AsText" ""
              rule "IssueDate" "AfterLabel" "Date of issue" 0 "" "AsDate" s.DateFormat ]
            @ derived) ]

let private expect label (result: Result<'T, MyDogsbodyException>) : 'T =
    match result with
    | Ok value -> value
    | Error ex -> failwith $"{label} failed: {ex.Message}"

let private query (sql: string) (read: SqliteDataReader -> 'a) : 'a list =
    use c = new SqliteConnection($"Data Source={mainDb}")
    c.Open()
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield read r ]

let private scalar (sql: string) : int64 =
    match query sql (fun r -> r.GetValue 0) with
    | v :: _ -> Convert.ToInt64 v
    | [] -> 0L

let private exec (sql: string) =
    use c = new SqliteConnection($"Data Source={mainDb}")
    c.Open()
    use cmd = c.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// The domain out of a raw From header, exactly as MatchSupplierWorkflow.senderDomain does it:
/// strip a "Display Name <addr>" wrapper, take the part after the LAST @.
let private senderDomain (raw: string) : string =
    let trimmed = (if isNull raw then "" else raw).Trim()
    let addr =
        let o = trimmed.LastIndexOf '<'
        let c = trimmed.LastIndexOf '>'
        if o >= 0 && c > o then trimmed.Substring(o + 1, c - o - 1).Trim() else trimmed
    match addr.LastIndexOf '@' with
    | -1 -> "(no @)"
    | i -> addr.Substring(i + 1).Trim().Trim('>').ToLowerInvariant()

[<EntryPoint>]
let main _ =
    if not (Directory.Exists profileRoot) then
        eprintfn "profileRoot does not exist: %s" profileRoot
        1
    else

    for f in [ mainDb; thunderbirdDb ] do
        try File.Delete f with _ -> ()

    let logged = ResizeArray<MyDogsbodyException>()
    let handleError = HandleErrorBuilder logged.Add

    MigrationSetup.setupMigrations $"Data Source={mainDb}"
    use mainContext = DatabaseContextSetup.createDatabaseContext mainDb
    use tbContext = ThunderbirdDatabaseContextModule.getDatabaseContext thunderbirdDb "shared"

    let mailApi = MailAccountApiFactory.createMailAccountApi handleError tbContext
    let supplierApi = SupplierApiFactory.createSupplierApi handleError mainContext
    let templateApi = TemplateApiFactory.createTemplateApi handleError mainContext
    let invoiceApi = InvoiceApiFactory.createInvoiceApi handleError (fun () -> DateTime.Now) mainContext tbContext

    mailApi.SetProfileRoot profileRoot |> expect "SetProfileRoot" |> ignore
    let discovery = mailApi.ScanForAccounts() |> expect "ScanForAccounts"

    printfn "\n=== %d account(s) discovered ===" discovery.Accounts.Length
    for a in discovery.Accounts do
        let scannable = a.Folders |> List.filter (fun f -> f.IsScannable) |> List.length
        let headerCount =
            match mailApi.CountMessages a.Id with
            | Ok n -> string n
            | Error _ -> "?"
        printfn "  %-28s  %-30s  store %s exists=%b  folders %d/%d scannable  header-pass %s msgs"
            a.DisplayName (String.Join(",", a.EmailAddresses)) a.StoreFormat a.StoreDirectoryExists
            scannable a.Folders.Length headerCount

    // ---- seed suppliers + templates (only if configured) ----
    let seedTemplates (derive: bool) =
        let stored = supplierApi.GetAllSuppliers() |> expect "GetAllSuppliers"
        for s in suppliers do
            let id = (stored |> List.find (fun x -> x.Name = s.Name)).Id
            for t in templateApi.GetTemplatesForSupplier id |> expect "GetTemplatesForSupplier" do
                templateApi.DeleteTemplate t.Id |> expect "DeleteTemplate" |> ignore
            for t in templatesFor s id derive do
                templateApi.AddTemplate t |> expect $"AddTemplate ({s.Name})" |> ignore

    if suppliersConfigured then
        for s in suppliers do
            supplierApi.AddSupplier
                { Name = s.Name; PaymentTermDays = s.PaymentTermDays
                  Matchers = [ { Kind = s.MatcherKind; Value = s.MatcherValue } ] }
            |> expect $"AddSupplier ({s.Name})" |> ignore
        seedTemplates true
        printfn "\n%d suppliers + templates seeded." suppliers.Length
    else
        printfn "\nDISCOVERY MODE - no suppliers configured. Scanning to build the sender table."

    // ---- scan every account, timed ----
    let scanAll (label: string) : TimeSpan =
        let sw = Stopwatch.StartNew()
        let mutable invoices, problems = 0, 0
        for a in discovery.Accounts do
            mailApi.SelectAccount a.Id |> expect "SelectAccount" |> ignore
            match invoiceApi.Scan windowDays with
            | Ok r ->
                invoices <- invoices + r.Invoices.Length
                problems <- problems + r.Problems.Length
                if r.Invoices.Length + r.Problems.Length > 0 then
                    printfn "    %-24s -> %d invoices, %d problems" a.DisplayName r.Invoices.Length r.Problems.Length
            | Error ex -> printfn "    %-24s -> ERROR %s" a.DisplayName ex.Message
        sw.Stop()
        printfn "  %-40s %7.1f s   (total this pass: %d invoices, %d problems)" label sw.Elapsed.TotalSeconds invoices problems
        sw.Elapsed

    printfn "\n===== 12.4  scan timing (all accounts, %dd) =====" windowDays
    let cold = scanAll "cold - watermarks empty"
    let warm = scanAll "warm - second pass, no re-read expected"
    for a in discovery.Accounts do mailApi.ClearWatermarks a.Id |> ignore
    let widen = scanAll "watermarks cleared - full re-read"

    // ---- what the scan saw ----
    printfn "\n===== what the scan actually processed ====="
    printfn "  messages processed (Invoices + ScanProblems rows): %d" (scalar "SELECT (SELECT COUNT(*) FROM Invoices) + (SELECT COUNT(*) FROM ScanProblems)")
    printfn "  cause breakdown:"
    for (cause, n) in query "SELECT Cause, COUNT(*) FROM ScanProblems GROUP BY Cause ORDER BY 2 DESC" (fun r -> r.GetString 0, r.GetInt32 1) do
        printfn "    %-24s %d" cause n

    printfn "\n  top 40 sender domains among messages that yielded no invoice:"
    let domains =
        query "SELECT Sender FROM ScanProblems" (fun r -> r.GetString 0)
        |> List.countBy senderDomain
        |> List.sortByDescending snd
        |> List.truncate 40
    for (d, n) in domains do
        printfn "    %5d  %s" n d

    if not suppliersConfigured then
        // Full sender + subject for messages that read like a bill, so `suppliers` can be filled
        // with domains that actually match rather than guesses.
        printfn "\n  invoice-candidate messages (subject looks bill-like):"
        let billish (s: string) =
            let s = s.ToLowerInvariant()
            [ "invoice"; "bill"; "payment due"; "amount due"; "due date"; "statement"
              "premium"; "renew"; "registration"; "rego"; "account summary"; "tax invoice"
              "your account"; "direct debit"; "receipt" ]
            |> List.exists s.Contains
        query "SELECT Sender, Subject, ReceivedAt FROM ScanProblems ORDER BY Sender" (fun r -> r.GetString 0, r.GetString 1, r.GetString 2)
        |> List.filter (fun (sender, subject, _) -> billish subject || billish sender)
        |> List.truncate 60
        |> List.iter (fun (sender, subject, at) -> printfn "    %-40s | %-55s | %s" (sender.Substring(0, min 40 sender.Length)) (subject.Substring(0, min 55 subject.Length)) (at.Substring(0, min 10 at.Length)))

    // ---- 12.5 coverage (only if configured) ----
    if suppliersConfigured then
        printfn "\n===== 12.5  due-date coverage ====="
        let withTotal = scalar "SELECT COUNT(*) FROM Invoices"
        let withDue = scalar "SELECT COUNT(*) FROM Invoices WHERE DueDate IS NOT NULL"

        exec "DELETE FROM Invoices; DELETE FROM InvoiceTombstones; DELETE FROM ScanProblems;"
        seedTemplates false
        for a in discovery.Accounts do
            mailApi.SelectAccount a.Id |> ignore
            mailApi.ClearWatermarks a.Id |> ignore
            invoiceApi.Scan windowDays |> ignore
        let woTotal = scalar "SELECT COUNT(*) FROM Invoices"
        let woDue = scalar "SELECT COUNT(*) FROM Invoices WHERE DueDate IS NOT NULL"

        let pct d t = if t = 0L then 0.0 else 100.0 * float d / float t
        printfn "  with DateFromField:    %d / %d  (%.0f%%)" withDue withTotal (pct withDue withTotal)
        printfn "  without DateFromField: %d / %d  (%.0f%%)" woDue woTotal (pct woDue woTotal)

        printfn "\n===== paste into docs/changes/invoice-extraction/outcome.md ====="
        printfn "| First cold full scan | %.1fs |" cold.TotalSeconds
        printfn "| Second scan (watermarks warm) | %.1fs |" warm.TotalSeconds
        printfn "| Widen / re-read after clearing watermarks | %.1fs |" widen.TotalSeconds
        printfn "| Due-date coverage, no DateFromField | %d/%d (%.0f%%) |" woDue woTotal (pct woDue woTotal)
        printfn "| Due-date coverage, with DateFromField | %d/%d (%.0f%%) |" withDue withTotal (pct withDue withTotal)
        printfn "| Immediate rescan kept? | narrow yes; widen re-reads (see notes) |"
    else
        printfn "\nNext: pick the invoice-supplier domains from the table above, put them in\nMeasureScan/Program.fs -> suppliers, and run again for the 12.5 coverage numbers."

    printfn "\nExceptions logged: %d" logged.Count
    for ex in logged |> Seq.truncate 20 do printfn "  [%s] %s" ex.ActionName ex.Message

    printfn "\nCleanup: Remove-Item measure.db, Thunderbird.db, Logging.db"
    0
