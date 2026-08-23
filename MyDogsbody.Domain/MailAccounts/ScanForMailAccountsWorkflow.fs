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

/// A message count is a separate, user-triggered action whose header pass costs *minutes* on the
/// measured profile (design.md -> Decisions taken #4), which is the whole reason it is cached with
/// the time it was taken rather than computed on render. A scan re-reads prefs.js and re-enumerates
/// folders; it never counts messages, so discovery reports every account with no count at all.
/// Storing that straight over the previous set threw the figure away for an account the scan had
/// just found again - and requirements.md's "state when it was taken, because the count is a
/// snapshot and the mailbox keeps growing" only means anything if a stale count survives to be
/// stated.
///
/// Carried by id, and only the count: everything else about the row is the fresh scan's answer, so
/// this cannot turn into merging a stale row forward. An account the scan no longer finds is not
/// resurrected - a fresh scan is still the whole truth about WHICH accounts exist. A discovery that
/// somehow arrived with a count of its own keeps it.
let private carryCachedCounts
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

/// Discovers accounts under the stored profile root, stores what was found, and reconciles the
/// selection against it.
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
        let accounts = carryCachedCounts previous discovery.Accounts

        do! saveMailAccounts accounts
        let! selectionCleared = reconcileSelection loadSelectedMailAccount saveSelectedMailAccount accounts

        // The discovery adapter cannot know either of these - it never sees the stored selection
        // and never sees the previously stored accounts - so both are the workflow's own answer,
        // overwriting what the adapter left in those fields.
        return { discovery with Accounts = accounts; SelectionCleared = selectionCleared }
    }
