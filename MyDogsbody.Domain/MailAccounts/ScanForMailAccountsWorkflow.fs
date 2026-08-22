module MyDogsbody.Domain.MailAccounts.ScanForMailAccountsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// If the previously selected account is absent from a fresh discovery, the selection is
/// cleared rather than left pointing at nothing. A workflow rule rather than an adapter one, so
/// it is unit-tested with lambdas - see design.md -> "Workflows".
let private reconcileSelection
    (loadSelectedMailAccount: LoadSelectedMailAccount)
    (saveSelectedMailAccount: SaveSelectedMailAccount)
    (accounts: DiscoveredMailAccount list)
    : Result<unit, MailAccountError> =
    result {
        let! selected = loadSelectedMailAccount ()

        match selected with
        | None -> return ()
        | Some id ->
            let stillPresent = accounts |> List.exists (fun account -> account.Id = id)

            if stillPresent then
                return ()
            else
                return! saveSelectedMailAccount None
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
        do! reconcileSelection loadSelectedMailAccount saveSelectedMailAccount discovery.Accounts

        return discovery
    }
