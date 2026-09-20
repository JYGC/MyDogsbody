module MyDogsbody.Domain.MailAccounts.ScanForMailAccountsWorkflow

open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts

/// Cleared rather than left pointing at nothing. A workflow rule rather than an adapter one, so
/// it is unit-tested with lambdas - see design.md -> "Workflows".
///
/// Because requirements.md asks the system to clear the selection **and say so**. A `unit`
/// return had nowhere to put that fact, so the clearing was invisible past this line and the
/// page could only show the tick disappearing.
let private clearSelectionIfPreviouslySelectedAccountIsAbsentFromFreshDiscoveryReportingWhetherItDid
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

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ScanForMailAccountsWorkflow.fs: carryTheCachedMessageCountForwardByAccountIdFromThePreviouslyStoredAccounts
let private carryTheCachedMessageCountForwardByAccountIdFromThePreviouslyStoredAccounts
    (previous: DiscoveredMailAccount list)
    (discovered: DiscoveredMailAccount list)
    : DiscoveredMailAccount list =
    discovered
    |> List.map (fun account ->
        match account.CachedMessageCount with
        | Some _ -> account
        | None ->
            match previous |> List.tryFind (fun stored -> stored.Id = account.Id) with
            | Some stored when stored.CachedMessageCount.IsSome ->
                { account with CachedMessageCount = stored.CachedMessageCount }
            | _ -> account)

let scanForMailAccounts
    (loadProfileRoot: LoadProfileRoot)
    (discoverMailAccounts: DiscoverMailAccounts)
    (loadMailAccounts: LoadMailAccounts)
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
        let! previous = loadMailAccounts ()
        let accounts =
            carryTheCachedMessageCountForwardByAccountIdFromThePreviouslyStoredAccounts previous discovery.Accounts

        do! saveMailAccounts accounts
        let! selectionCleared =
            clearSelectionIfPreviouslySelectedAccountIsAbsentFromFreshDiscoveryReportingWhetherItDid
                loadSelectedMailAccount
                saveSelectedMailAccount
                accounts

        // The discovery adapter cannot know either of these - it never sees the stored selection
        // and never sees the previously stored accounts - so both are the workflow's own answer,
        // overwriting what the adapter left in those fields.
        return { discovery with Accounts = accounts; SelectionCleared = selectionCleared }
    }
