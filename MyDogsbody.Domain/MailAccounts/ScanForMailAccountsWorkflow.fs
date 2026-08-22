module MyDogsbody.Domain.MailAccounts.ScanForMailAccountsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// If the previously selected account is absent from a fresh discovery, the selection is
/// cleared rather than left pointing at nothing. A workflow rule rather than an adapter one, so
/// it is unit-tested with lambdas - see design.md -> "Workflows".
///
/// Returns whether it cleared anything, because requirements.md asks the system to clear the
/// selection **and say so**. A `unit` return had nowhere to put that fact, so the clearing was
/// invisible past this line and the page could only show the tick disappearing.
let private reconcileSelection
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (saveSelectedMailAccount: SaveSelectedMailAccount)
    (accounts: DiscoveredMailAccount list)
    : Result<bool, MailAccountError> =
    result {
        let! selected = loadSelectedMailAccount ()

        match selected with
        | None -> return false
        | Some id ->
            let stillPresent = accounts |> List.exists (fun account -> account.Id = id)

            if stillPresent then
                return false
            else
                do! saveSelectedMailAccount None
                return true
    }

/// Discovers accounts under the stored profile root, stores what was found, and reconciles the
/// selection against it.
let scanForMailAccounts
    (loadProfileRoot: LoadProfileRoot)
    (discoverMailAccounts: DiscoverMailAccounts)
    (saveMailAccounts: SaveMailAccounts)
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (saveSelectedMailAccount: SaveSelectedMailAccount)
    ()
    : Result<DiscoveryResult, MailAccountError> =
    result {
        let! root = loadProfileRoot ()

        let! path =
            match root with
            | Some path -> Ok path
            | None -> Error ProfileRootMissing

        let! discovery = discoverMailAccounts path
        do! saveMailAccounts discovery.Accounts
        let! selectionCleared = reconcileSelection loadSelectedMailAccount saveSelectedMailAccount discovery.Accounts

        // The discovery adapter cannot know this - it never sees the stored selection - so the
        // flag is the workflow's own answer, overwriting whatever the adapter left in the field.
        return { discovery with SelectionCleared = selectionCleared }
    }
