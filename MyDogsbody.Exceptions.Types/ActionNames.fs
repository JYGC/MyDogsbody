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

    module Integrations =
        let private integrations = $"{myDogsbody}.Integrations"

        module Credentials =
            let private credentials = $"{integrations}.Credentials"

            module CredentialStore =
                let private credentialStore = $"{credentials}.CredentialStore"
                let getAll = $"{credentialStore}.getAll"
                let insertOne = $"{credentialStore}.insertOne"
                let updateOne = $"{credentialStore}.updateOne"

        module Pdf =
            let private pdf = $"{integrations}.Pdf"

            module PdfDocumentReader =
                let private pdfDocumentReader = $"{pdf}.PdfDocumentReader"
                let readContent = $"{pdfDocumentReader}.readContent"

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
