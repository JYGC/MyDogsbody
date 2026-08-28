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

    let conn = databaseContext.GetDatabaseConnection

    // ---------- main-database adapters (MyDogsbodyException -> InvoiceError) ----------

    let toInvoiceError = InvoiceApiMappers.toInvoiceError

    let loadInvoices: LoadInvoices =
        fun cutoff ->
            InvoiceStore.getInvoices handleError conn databaseContext.GetInvoices cutoff
            |> Result.mapError toInvoiceError

    let upsertInvoice: UpsertInvoice =
        fun invoice ->
            InvoiceStore.upsertInvoice handleError conn getCurrentTime invoice
            |> Result.mapError toInvoiceError

    let deleteFromLedger: DeleteInvoice =
        fun invoiceId ->
            InvoiceStore.deleteInvoice handleError conn databaseContext.GetInvoices invoiceId
            |> Result.mapError toInvoiceError

    let loadTombstones: LoadTombstones =
        fun () ->
            InvoiceStore.getTombstones handleError conn databaseContext.GetInvoiceTombstones ()
            |> Result.mapError toInvoiceError

    let saveTombstone: SaveTombstone =
        fun tombstone ->
            InvoiceStore.saveTombstone handleError conn tombstone |> Result.mapError toInvoiceError

    let removeTombstone: RemoveTombstone =
        fun supplierId reference ->
            InvoiceStore.removeTombstone handleError conn supplierId reference |> Result.mapError toInvoiceError

    let saveScanProblems: SaveScanProblems =
        fun problems ->
            InvoiceStore.saveScanProblems handleError conn problems |> Result.mapError toInvoiceError

    let clearScanProblems: ClearScanProblems =
        fun ids -> InvoiceStore.clearScanProblems handleError conn ids |> Result.mapError toInvoiceError

    let loadScanProblems () =
        InvoiceStore.getScanProblems handleError conn databaseContext.GetScanProblems ()
        |> Result.mapError toInvoiceError

    // ---------- sibling-area adapters ----------

    let loadSuppliers: LoadSuppliers =
        fun () ->
            SupplierStore.getAll handleError conn databaseContext.GetSuppliers databaseContext.GetSupplierMatchers ()
            |> Result.mapError (fun ex -> SupplierStoreFailed ex.Message)

    let loadTemplatesForSupplier: LoadTemplatesForSupplier =
        fun supplierId ->
            TemplateStore.getForSupplier
                handleError
                conn
                databaseContext.GetInvoiceTemplates
                databaseContext.GetTemplateFieldRules
                supplierId
            |> Result.mapError (fun ex -> TemplateError.TemplateStoreFailed ex.Message)

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
        fun accountId -> loadMailAccounts () |> Result.map (List.tryFind (fun a -> a.Id = accountId))

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

    let readMailFolder: ReadMailFolder =
        fun accountId cutoff -> MailFolderReader.read lookupAccount loadWatermark saveWatermark accountId cutoff

    // ---------- workflows, partially applied ----------

    let toException = InvoiceApiMappers.toMyDogsbodyException

    /// A supplierId -> name map for the top mapper.
    let supplierNames () : Result<Map<string, string>, MyDogsbodyException> =
        loadSuppliers ()
        |> Result.map (fun suppliers ->
            suppliers
            |> List.map (fun s -> SupplierId.value s.Id, SupplierName.value s.Name)
            |> Map.ofList)
        |> Result.mapError (fun err ->
            SupplierApiMappers.toMyDogsbodyException ActionNames.MyDogsbody.Startup.InvoiceApi.getInvoices err)

    { Scan =
        fun rawDays ->
            match ScanWindowDays.create rawDays with
            | Error reason ->
                Error(toException ActionNames.MyDogsbody.Startup.InvoiceApi.scan (ScanWindowInvalid reason))
            | Ok window ->
                ScanForInvoicesWorkflow.scanForInvoices
                    getCurrentTime
                    loadSelectedMailAccount
                    readMailFolder
                    readDocumentText
                    loadSuppliers
                    loadTemplatesForSupplier
                    loadTombstones
                    upsertInvoice
                    saveScanProblems
                    clearScanProblems
                    window
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.scan)
                |> Result.bind (fun result ->
                    supplierNames ()
                    |> Result.map (fun names ->
                        { Invoices = result.Invoices |> List.map (InvoiceApiMappers.toInvoiceUiType names)
                          Problems = result.Problems |> List.map (InvoiceApiMappers.toProblemUiType names) }))

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
                    |> Result.mapError (fun r -> toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice (ScanWindowInvalid r))

                let! reference =
                    InvoiceReference.create rawReference
                    |> Result.mapError (fun r -> toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice (InvoiceReferenceInvalid r))

                return!
                    UndeleteInvoiceWorkflow.undeleteInvoice removeTombstone supplierId reference
                    |> Result.mapError (toException ActionNames.MyDogsbody.Startup.InvoiceApi.undeleteInvoice)
            } }
