/// Where the abstract meets the real: the adapters that satisfy each MailAccounts dependency
/// function type, the workflows partially applied over them, and the translation between the
/// two error types.
///
/// This is the only place that knows both the Thunderbird integration and the domain, and the
/// only place the two error types meet. Dependencies are leading parameters, so a test supplies
/// a temp database context; no module-level bindings, so nothing opens a file on import.
module MyDogsbody.Startup.MailAccountApiFactory

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.UI.Types

let createMailAccountApi (handleError: HandleErrorBuilder) (thunderbirdContext: ThunderbirdDatabaseContext) : MailAccountApi =

    // ---------- Storage dependencies: ThunderbirdStore speaks MyDogsbodyException; every
    // dependency type here speaks MailAccountError, so each is translated on the way out. ----------

    let loadProfileRoot: LoadProfileRoot =
        fun () ->
            ThunderbirdStore.loadProfileRoot handleError thunderbirdContext.GetProfileRootCollection ()
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let saveProfileRoot: SaveProfileRoot =
        fun path ->
            ThunderbirdStore.saveProfileRoot handleError thunderbirdContext.GetProfileRootCollection path
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let loadMailAccounts: LoadMailAccounts =
        fun () ->
            ThunderbirdStore.loadMailAccounts
                handleError
                thunderbirdContext.GetAccountsCollection
                thunderbirdContext.GetFoldersCollection
                ()
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let saveMailAccounts: SaveMailAccounts =
        fun accounts ->
            ThunderbirdStore.saveMailAccounts
                handleError
                thunderbirdContext.GetAccountsCollection
                thunderbirdContext.GetFoldersCollection
                accounts
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let loadSelectedMailAccount: LoadSelectedMailAccount =
        fun () ->
            ThunderbirdStore.loadSelectedMailAccount handleError thunderbirdContext.GetSelectedAccountCollection ()
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    let saveSelectedMailAccount: SaveSelectedMailAccount =
        fun selected ->
            ThunderbirdStore.saveSelectedMailAccount handleError thunderbirdContext.GetSelectedAccountCollection selected
            |> Result.mapError MailAccountApiMappers.toMailAccountError

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

    let clearWatermarksDependency: ClearWatermarks =
        fun accountId ->
            ThunderbirdStore.clearWatermarksFor handleError thunderbirdContext.GetWatermarksCollection accountId
            |> Result.mapError MailAccountApiMappers.toMailAccountError

    // ---------- Discovery: ThunderbirdFolderScanner / ThunderbirdAccountReader /
    // MailFolderEnumerator already speak MailAccountError, so no translation is needed - they
    // are composed directly into DiscoverMailAccounts. ----------

    let discoverMailAccounts: DiscoverMailAccounts =
        fun profileRoot ->
            let rootPath = ProfileRootPath.value profileRoot

            // Checked before the walk rather than inferred from it. ThunderbirdFolderScanner
            // cannot canonicalise a path that is not there, so it silently returns an empty
            // outcome, which is indistinguishable from "walked it, found no prefs.js" - and the
            // user would be told to look for their profile when the real answer is that the
            // drive is unplugged (requirements.md -> "Choosing the profile folder", "Edge cases").
            if not (System.IO.Directory.Exists rootPath) then
                Error(
                    ProfileRootUnreachable(
                        rootPath,
                        "the folder no longer exists, or is on a drive or network share that is not available"
                    )
                )
            else

            let scanOutcome = ThunderbirdFolderScanner.scan rootPath
            let accountResults = scanOutcome.ProfileDirectories |> List.map (fun dir -> dir, ThunderbirdAccountReader.read dir)

            let accounts =
                accountResults
                |> List.collect (fun (_, result) ->
                    match result with
                    | Ok accts -> accts
                    | Error _ -> [])
                |> List.map (fun account ->
                    let folders = MailFolderEnumerator.enumerate account.StoreDirectory account.StoreFormat
                    { account with Folders = folders })

            // A malformed profile's prefs.js is reported alongside directories the walk itself
            // could not read, and the other profiles still return - design.md -> "a malformed
            // prefs.js yields ProfileUnreadable and other profiles still return".
            let malformedProfiles =
                accountResults
                |> List.choose (fun (dir, result) ->
                    match result with
                    | Error(ProfileUnreadable(path, reason)) -> Some({ Path = path; Reason = reason }: UnreadableDirectory)
                    | Error _ -> Some({ Path = dir; Reason = "This profile could not be read." }: UnreadableDirectory)
                    | Ok _ -> None)

            if List.isEmpty scanOutcome.ProfileDirectories && List.isEmpty scanOutcome.Unreadable then
                Error(NoProfileFound rootPath)
            else
                Ok
                    {
                        Accounts = accounts
                        ProfilesFound = scanOutcome.ProfileDirectories
                        Unreadable = scanOutcome.Unreadable @ malformedProfiles
                        // Discovery never reads the stored selection, so it cannot answer this.
                        // ScanForMailAccountsWorkflow reconciles and overwrites it.
                        SelectionCleared = false
                    }

    let lookupAccount: MailFolderReader.LookupAccount =
        fun accountId -> loadMailAccounts () |> Result.map (List.tryFind (fun a -> a.Id = accountId))

    // ---------- Workflows, partially applied over the dependencies above. ----------

    let toException = MailAccountApiMappers.toMyDogsbodyException

    {
        GetProfileRoot =
            fun () ->
                loadProfileRoot ()
                |> Result.map (Option.map ProfileRootPath.value)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.getProfileRoot)

        SetProfileRoot =
            fun path ->
                SetProfileRootWorkflow.setProfileRoot saveProfileRoot path
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.setProfileRoot)

        ScanForAccounts =
            fun () ->
                ScanForMailAccountsWorkflow.scanForMailAccounts
                    loadProfileRoot
                    discoverMailAccounts
                    saveMailAccounts
                    loadSelectedMailAccount
                    saveSelectedMailAccount
                    ()
                |> Result.map MailAccountApiMappers.toDiscoveryResultUiType
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.scanForAccounts)

        GetAccounts =
            fun () ->
                ListMailAccountsWorkflow.listMailAccounts loadMailAccounts loadSelectedMailAccount ()
                |> Result.map (fun (accounts, selected) ->
                    accounts |> List.map MailAccountApiMappers.toMailAccountUiType, selected |> Option.map MailAccountId.value)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.getAccounts)

        SelectAccount =
            fun id ->
                SelectMailAccountWorkflow.selectMailAccount loadMailAccounts saveSelectedMailAccount id
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.selectAccount)

        CountMessages =
            fun id ->
                result {
                    let! accountId = MailAccountId.create id |> Result.mapError MailAccountIdInvalid
                    let! count = MailFolderReader.countMessages lookupAccount accountId

                    do!
                        ThunderbirdStore.updateCachedMessageCount
                            handleError
                            thunderbirdContext.GetAccountsCollection
                            accountId
                            count
                            DateTime.UtcNow
                        |> Result.mapError MailAccountApiMappers.toMailAccountError

                    return count
                }
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.countMessages)

        ClearWatermarks =
            fun id ->
                result {
                    let! accountId = MailAccountId.create id |> Result.mapError MailAccountIdInvalid
                    return! clearWatermarksDependency accountId
                }
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.MailAccountApi.clearWatermarks)
    }
