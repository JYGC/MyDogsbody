/// Where the ledger's abstract meets the real: the adapters that satisfy each Invoices
/// dependency function type, the workflows partially applied over them, and the InvoiceError <->
/// MyDogsbodyException translation.
///
/// The only place that knows both the main SQLite database, the Thunderbird store, the four
/// document readers AND the domain. Dependencies are leading parameters; no module-level bindings.
module MyDogsbody.Startup.InvoiceApiFactory

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.Integrations.Documents
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.UI.Types

/// The one bound ReadDocumentText: the four format readers behind DocumentReaders.dispatch. The
/// readers already return DocumentError, so no translation.
let readDocumentText: ReadDocumentText =
    DocumentReaders.dispatch
        PdfDocumentReader.readText
        WordDocumentReader.readText
        PlainTextDocumentReader.readText
        EmailBodyReader.readText

let createInvoiceApi
    (handleError: HandleErrorBuilder)
    (getCurrentTime: unit -> DateTime)
    (databaseContext: DatabaseContext)
    (thunderbirdContext: ThunderbirdDatabaseContext)
    : InvoiceApi =

    let databaseConnection = databaseContext.GetDatabaseConnection

    // ---------- main-database adapters (MyDogsbodyException -> InvoiceError) ----------

    let toInvoiceError = InvoiceApiMappers.toInvoiceError

    let loadInvoices: LoadInvoices =
        fun cutoff ->
            InvoiceStore.getInvoices handleError databaseConnection databaseContext.GetInvoices cutoff
            |> Result.mapError toInvoiceError

    /// The one adapter failure this area translates to a NAMED domain case rather than to the
    /// catch-all: a write refused because the supplier row is gone. `ScanForInvoicesWorkflow.step`
    /// records `SupplierGone` as that message's problem and carries on, while every other
    /// InvoiceError from the upsert ends the whole scan - so flattening this one to
    /// `InvoiceStoreFailed` meant a supplier deleted mid-scan killed the run instead of costing one
    /// row (requirements.md: "WHEN a scan finds an invoice whose supplier has since been deleted THE
    /// SYSTEM SHALL report it as a problem rather than storing an invoice with no supplier").
    let upsertInvoice: UpsertInvoice =
        fun invoice ->
            InvoiceStore.upsertInvoice handleError databaseConnection getCurrentTime invoice
            |> Result.mapError (fun caughtException ->
                if InvoiceStore.isMissingSupplier caughtException then
                    SupplierGone invoice.SupplierId
                else
                    toInvoiceError caughtException)

    let deleteFromLedger: DeleteInvoice =
        fun invoiceId ->
            InvoiceStore.deleteInvoice handleError databaseConnection databaseContext.GetInvoices invoiceId
            |> Result.mapError toInvoiceError

    let loadTombstones: LoadTombstones =
        fun () ->
            InvoiceStore.getTombstones handleError databaseConnection databaseContext.GetInvoiceTombstones ()
            |> Result.mapError toInvoiceError

    let saveTombstone: SaveTombstone =
        fun tombstone ->
            InvoiceStore.saveTombstone handleError databaseConnection tombstone |> Result.mapError toInvoiceError

    let removeTombstone: RemoveTombstone =
        fun supplierId reference ->
            InvoiceStore.removeTombstone handleError databaseConnection supplierId reference |> Result.mapError toInvoiceError

    let saveScanProblems: SaveScanProblems =
        fun problems ->
            InvoiceStore.saveScanProblems handleError databaseConnection problems |> Result.mapError toInvoiceError

    let clearScanProblems: ClearScanProblems =
        fun ids -> InvoiceStore.clearScanProblems handleError databaseConnection ids |> Result.mapError toInvoiceError

    let loadScanProblems () =
        InvoiceStore.getScanProblems handleError databaseConnection databaseContext.GetScanProblems ()
        |> Result.mapError toInvoiceError

    // ---------- sibling-area adapters ----------

    let loadSuppliers: LoadSuppliers =
        fun () ->
            SupplierStore.getAll handleError databaseConnection databaseContext.GetSuppliers databaseContext.GetSupplierMatchers ()
            |> Result.mapError (fun caughtException -> SupplierStoreFailed caughtException.Message)

    let loadTemplatesForSupplier: LoadTemplatesForSupplier =
        fun supplierId ->
            TemplateStore.getForSupplier
                handleError
                databaseConnection
                databaseContext.GetInvoiceTemplates
                databaseContext.GetTemplateFieldRules
                supplierId
            |> Result.mapError (fun caughtException -> TemplateError.TemplateStoreFailed caughtException.Message)

    // ---------- Thunderbird adapters (same wiring as MailAccountApiFactory) ----------

    let loadSelectedMailAccount: LoadSelectedMailAccount =
        fun () ->
            ThunderbirdStore.loadSelectedMailAccount handleError thunderbirdContext.GetSelectedAccountCollection ()
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let loadMailAccounts () =
        ThunderbirdStore.loadMailAccounts
            handleError
            thunderbirdContext.GetAccountsCollection
            thunderbirdContext.GetFoldersCollection
            ()
        |> Result.mapError MailAccountApiMappers.toMailAccountError

    let lookupAccount: MailFolderReader.LookupAccount =
        fun accountId -> loadMailAccounts () |> Result.map (List.tryFind (fun account -> account.Id = accountId))

    let loadWatermark: MailFolderReader.LoadWatermark =
        fun accountId relativePath ->
            ThunderbirdStore.loadWatermarkEntry handleError thunderbirdContext.GetWatermarksCollection accountId relativePath
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let saveWatermark: MailFolderReader.SaveWatermark =
        fun accountId relativePath watermark ->
            ThunderbirdStore.saveWatermarkEntry
                handleError
                thunderbirdContext.GetWatermarksCollection
                accountId
                relativePath
                watermark
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    /// Deletes every watermark for one account - the "Rescan everything" pre-clear (decision 16)
    /// and the fatal-scan reset (decision 17). Same wiring as MailAccountApiFactory.
    let clearWatermarksForAccount: ClearWatermarks =
        fun accountId ->
            ThunderbirdStore.clearWatermarksFor handleError thunderbirdContext.GetWatermarksCollection accountId
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let readMailFolder: ReadMailFolder =
        fun accountId cutoff -> MailFolderReader.read lookupAccount loadWatermark saveWatermark accountId cutoff

    // ---------- workflows, partially applied ----------

    let toException = InvoiceApiMappers.toMyDogsbodyException

    /// A supplierId -> name map for the top mapper.
    let supplierNames () : Result<Map<string, string>, MyDogsbodyException> =
        loadSuppliers ()
        |> Result.map (fun suppliers ->
            suppliers
            |> List.map (fun supplier -> SupplierId.value supplier.Id, SupplierName.value supplier.Name)
            |> Map.ofList)
        |> Result.mapError (fun error ->
            SupplierApiMappers.toMyDogsbodyException ActionNames.MyDogsbody.Startup.InvoiceApi.getInvoices error)

    /// Scan / RescanEverything differ only by ScanMode: FullRescan clears the account's
    /// watermarks first (decision 16). ScanMode is a domain type and never crosses the InvoiceApi
    /// boundary - the factory picks the mode, the UI picks the member.
    let runScan (mode: ScanMode) (action: string) (rawDays: int) : Result<ScanResultUiType, MyDogsbodyException> =
        match ScanWindowDays.create rawDays with
        | Error reason -> Error(toException action (ScanWindowInvalid reason))
        | Ok window ->
            ScanForInvoicesWorkflow.scanForInvoices
                getCurrentTime
                loadSelectedMailAccount
                clearWatermarksForAccount
                readMailFolder
                readDocumentText
                loadSuppliers
                loadTemplatesForSupplier
                loadTombstones
                upsertInvoice
                saveScanProblems
                clearScanProblems
                mode
                window
            |> Result.mapError (toException action)
            |> Result.bind (fun result ->
                supplierNames ()
                |> Result.map (fun names ->
                    { Invoices = result.Invoices |> List.map (InvoiceApiMappers.toInvoiceUiType names)
                      Problems = result.Problems |> List.map (InvoiceApiMappers.toProblemUiType names) }))

    { Scan = runScan IncrementalScan ActionNames.MyDogsbody.Startup.InvoiceApi.scan

      RescanEverything = runScan FullRescan ActionNames.MyDogsbody.Startup.InvoiceApi.rescanEverything

      GetInvoices =
        fun rawDays ->
            match ScanWindowDays.create rawDays with
            | Error reason ->
                Error(toException ActionNames.MyDogsbody.Startup.InvoiceApi.getInvoices (ScanWindowInvalid reason))
            | Ok window ->
                let cutoff = Some(ScanForInvoicesWorkflow.computeCutoff getCurrentTime window)

                result {
                    let! names = supplierNames ()

                    let! invoices =
                        loadInvoices cutoff
                        |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.getInvoices)

                    return invoices |> List.map (InvoiceApiMappers.toInvoiceUiType names)
                }

      DeleteInvoice =
        fun invoiceId ->
            DeleteInvoiceWorkflow.deleteInvoice deleteFromLedger saveTombstone getCurrentTime invoiceId
            |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.deleteInvoice)

      GetProblems =
        fun () ->
            result {
                let! names = supplierNames ()
                let! problems = loadScanProblems () |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.getProblems)
                return problems |> List.map (InvoiceApiMappers.toProblemUiType names)
            }

      GetTombstones =
        fun () ->
            result {
                let! names = supplierNames ()
                let! tombstones = loadTombstones () |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.getTombstones)
                return tombstones |> List.map (InvoiceApiMappers.toTombstoneUiType names)
            }

      UndeleteInvoice =
        fun rawSupplierId rawReference ->
            result {
                let! supplierId =
                    SupplierId.create rawSupplierId
                    |> Result.mapError (fun reason -> toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice (ScanWindowInvalid reason))

                let! reference =
                    InvoiceReference.create rawReference
                    |> Result.mapError (fun reason -> toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice (InvoiceReferenceInvalid reason))

                return!
                    UndeleteInvoiceWorkflow.undeleteInvoice removeTombstone supplierId reference
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice)
            } }
