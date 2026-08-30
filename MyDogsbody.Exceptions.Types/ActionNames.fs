/// Every action string an outer-ring function reports a failure under.
///
/// Nested modules mirror the real code path of the function that uses the string, so an entry
/// reads as where the failure happened. They are $"..."-composed and compiler-unchecked, which is
/// why Contracts/ActionNamesTests.fs asserts that each function reports the one it declares -
/// before that test existed, two entries here were silently wrong: one truncated so it did not
/// name its own function, and one naming the opposite mapping.
///
/// Domain workflows have no entry. Their errors are discriminated union cases, which need no
/// string; the only credentials entries below the composition root belong to the store.
module MyDogsbody.Exceptions.Types.ActionNames

module MyDogsbody =
    let private myDogsbody = "MyDogsbody"

    /// The composition root's own actions.
    ///
    /// A domain error still has to reach the UI as a MyDogsbodyException, and that carries an
    /// action, so it names the API operation that failed rather than the workflow inside.
    module Startup =
        let private startup = $"{myDogsbody}.Startup"

        module CredentialApi =
            let private credentialApi = $"{startup}.CredentialApi"
            let getAllCredentials = $"{credentialApi}.getAllCredentials"
            let addCredential = $"{credentialApi}.addCredential"
            let editCredential = $"{credentialApi}.editCredential"

        module SupplierApi =
            let private supplierApi = $"{startup}.SupplierApi"
            let getAllSuppliers = $"{supplierApi}.getAllSuppliers"
            let addSupplier = $"{supplierApi}.addSupplier"
            let editSupplier = $"{supplierApi}.editSupplier"
            let deleteSupplier = $"{supplierApi}.deleteSupplier"

        module TemplateApi =
            let private templateApi = $"{startup}.TemplateApi"
            let getTemplatesForSupplier = $"{templateApi}.getTemplatesForSupplier"
            let addTemplate = $"{templateApi}.addTemplate"
            let editTemplate = $"{templateApi}.editTemplate"
            let deleteTemplate = $"{templateApi}.deleteTemplate"
            let reorderTemplates = $"{templateApi}.reorderTemplates"
            let testTemplate = $"{templateApi}.testTemplate"

        module InvoiceApi =
            let private invoiceApi = $"{startup}.InvoiceApi"
            let scan = $"{invoiceApi}.scan"
            let rescanEverything = $"{invoiceApi}.rescanEverything"
            let getInvoices = $"{invoiceApi}.getInvoices"
            let deleteInvoice = $"{invoiceApi}.deleteInvoice"
            let getProblems = $"{invoiceApi}.getProblems"
            let getTombstones = $"{invoiceApi}.getTombstones"
            let undeleteInvoice = $"{invoiceApi}.undeleteInvoice"

        module ScanWindowApi =
            let private scanWindowApi = $"{startup}.ScanWindowApi"
            let getScanWindows = $"{scanWindowApi}.getScanWindows"
            let addScanWindow = $"{scanWindowApi}.addScanWindow"
            let deleteScanWindow = $"{scanWindowApi}.deleteScanWindow"
            let getSelectedScanWindow = $"{scanWindowApi}.getSelectedScanWindow"
            let selectScanWindow = $"{scanWindowApi}.selectScanWindow"

        module MailAccountApi =
            let private mailAccountApi = $"{startup}.MailAccountApi"
            let getProfileRoot = $"{mailAccountApi}.getProfileRoot"
            let setProfileRoot = $"{mailAccountApi}.setProfileRoot"
            let scanForAccounts = $"{mailAccountApi}.scanForAccounts"
            let getAccounts = $"{mailAccountApi}.getAccounts"
            let selectAccount = $"{mailAccountApi}.selectAccount"
            let countMessages = $"{mailAccountApi}.countMessages"
            let clearWatermarks = $"{mailAccountApi}.clearWatermarks"

    /// The main SQLite database's own actions. A sibling of Integrations rather than a member of
    /// it - MyDogsbody.Database is the application's main store, not an integration, so its
    /// entries do not go under Integrations.
    module Database =
        let private database = $"{myDogsbody}.Database"

        module SupplierStore =
            let private supplierStore = $"{database}.SupplierStore"
            let getAll = $"{supplierStore}.getAll"
            let insertOne = $"{supplierStore}.insertOne"
            let updateOne = $"{supplierStore}.updateOne"
            let deleteOne = $"{supplierStore}.deleteOne"

        module TemplateStore =
            let private templateStore = $"{database}.TemplateStore"
            let getForSupplier = $"{templateStore}.getForSupplier"
            let insertOne = $"{templateStore}.insertOne"
            let updateOne = $"{templateStore}.updateOne"
            let deleteOne = $"{templateStore}.deleteOne"
            let reorder = $"{templateStore}.reorder"

        module InvoiceStore =
            let private invoiceStore = $"{database}.InvoiceStore"
            let getInvoices = $"{invoiceStore}.getInvoices"
            let upsertInvoice = $"{invoiceStore}.upsertInvoice"
            let deleteInvoice = $"{invoiceStore}.deleteInvoice"
            let getTombstones = $"{invoiceStore}.getTombstones"
            let saveTombstone = $"{invoiceStore}.saveTombstone"
            let removeTombstone = $"{invoiceStore}.removeTombstone"
            let getScanProblems = $"{invoiceStore}.getScanProblems"
            let saveScanProblems = $"{invoiceStore}.saveScanProblems"
            let clearScanProblems = $"{invoiceStore}.clearScanProblems"

        module ScanWindowStore =
            let private scanWindowStore = $"{database}.ScanWindowStore"
            let getScanWindows = $"{scanWindowStore}.getScanWindows"
            let saveScanWindow = $"{scanWindowStore}.saveScanWindow"
            let deleteScanWindow = $"{scanWindowStore}.deleteScanWindow"
            let getSelectedScanWindow = $"{scanWindowStore}.getSelectedScanWindow"
            let saveSelectedScanWindow = $"{scanWindowStore}.saveSelectedScanWindow"

    module Integrations =
        let private integrations = $"{myDogsbody}.Integrations"

        module Credentials =
            let private credentials = $"{integrations}.Credentials"

            module CredentialStore =
                let private credentialStore = $"{credentials}.CredentialStore"
                let getAll = $"{credentialStore}.getAll"
                let insertOne = $"{credentialStore}.insertOne"
                let updateOne = $"{credentialStore}.updateOne"

        module Google =
            let private google = $"{integrations}.Google"

            module GoogleCredentialStore =
                let private googleCredentialStore = $"{google}.GoogleCredentialStore"
                let getAll = $"{googleCredentialStore}.getAll"
                let insertOne = $"{googleCredentialStore}.insertOne"
                let updateOne = $"{googleCredentialStore}.updateOne"

        module Documents =
            let private documents = $"{integrations}.Documents"

            module PdfDocumentReader =
                let private pdfDocumentReader = $"{documents}.PdfDocumentReader"
                let readContent = $"{pdfDocumentReader}.readContent"

        /// Only ThunderbirdStore's functions appear here - ThunderbirdFolderScanner,
        /// ThunderbirdAccountReader, MailFolderEnumerator and MailFolderReader construct
        /// MailAccountError directly rather than going through handleError, because their
        /// failures (a locked file, a malformed prefs.js) are expected in the domain's own
        /// terms - see design.md -> "Error-handling approach". ThunderbirdStore is genuine
        /// LiteDB CRUD, the same shape as CredentialStore, so it keeps the usual pattern.
        module Thunderbird =
            let private thunderbird = $"{integrations}.Thunderbird"

            module ThunderbirdStore =
                let private thunderbirdStore = $"{thunderbird}.ThunderbirdStore"
                let loadProfileRoot = $"{thunderbirdStore}.loadProfileRoot"
                let saveProfileRoot = $"{thunderbirdStore}.saveProfileRoot"
                let loadMailAccounts = $"{thunderbirdStore}.loadMailAccounts"
                let saveMailAccounts = $"{thunderbirdStore}.saveMailAccounts"
                let loadSelectedMailAccount = $"{thunderbirdStore}.loadSelectedMailAccount"
                let saveSelectedMailAccount = $"{thunderbirdStore}.saveSelectedMailAccount"
                let loadWatermark = $"{thunderbirdStore}.loadWatermark"
                let saveWatermark = $"{thunderbirdStore}.saveWatermark"
                let clearWatermarks = $"{thunderbirdStore}.clearWatermarks"
                let updateCachedMessageCount = $"{thunderbirdStore}.updateCachedMessageCount"

    module Logging =
        let private logging = $"{myDogsbody}.Logging"

        module ExceptionRepository =
            let private exceptionRepository = $"{logging}.ExceptionRepository"
            let insertOne = $"{exceptionRepository}.insertOne"
            let getAll = $"{exceptionRepository}.getAll"

        module ExceptionUseCases =
            let private exceptionUseCases = $"{logging}.ExceptionUseCases"
            let addException = $"{exceptionUseCases}.addException"
            let getAllExceptions = $"{exceptionUseCases}.getAllExceptions"
