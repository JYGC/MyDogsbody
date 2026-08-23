module MyDogsbody.Tests.Contracts.ThunderbirdDependencyContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.Integrations.Thunderbird.MailFolderReader
open MyDogsbody.Startup
open MyDogsbody.UI.Types
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

// A dependency function type is this architecture's published interface, so CLAUDE.md's shared-
// suite rule applies to each one - the suite below runs against the real adapter binding AND
// against an in-memory fake of the kind the workflow unit tests use for it. Related Load/Save
// (and Discover/Read) pairs are grouped into one suite each, the same way
// SupplierDependencyContractTests groups its four CRUD functions - ten domain dependency types
// become seven grouped suites below, plus FolderPicker's own (Phase 7's UI-level type).

let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error(error: MailAccountError) -> failwith $"{label} expected Ok, but got Error: {error}"

let private withTempContext (test: ThunderbirdDatabaseContext -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context
    finally
        context.Dispose()

        try
            File.Delete databasePath
        with _ ->
            ()

let private anAccount id storeDirectory : DiscoveredMailAccount =
    {
        Id = MailAccountId.create id |> valueOrFail
        ProfilePath = @"C:\profile"
        DisplayName = $"Account {id}"
        EmailAddresses = [ $"{id}@example.com" ]
        StoreFormat = Mbox
        StoreDirectory = storeDirectory
        StoreDirectoryExists = true
        Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 10L; IsScannable = true } ]
        CachedMessageCount = None
    }

/// A store directory that is configured but not on disk - the state discovery deliberately keeps
/// an account in rather than dropping it. `imap.delta.example.com` is declared by the
/// measured-shape fixture's prefs.js and has no directory under `ImapMail/`.
let private goneStoreDirectory =
    Path.Combine(measuredShapeProfile, "ImapMail", "imap.delta.example.com")

// ---------- ProfileRoot: LoadProfileRoot / SaveProfileRoot ----------

type private ProfileRootDependencies = { Load: LoadProfileRoot; Save: SaveProfileRoot }

let private realProfileRootDependencies (test: ProfileRootDependencies -> unit) =
    withTempContext (fun context ->
        test
            {
                Load =
                    fun () ->
                        ThunderbirdStore.loadProfileRoot handleError context.GetProfileRootCollection ()
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
                Save =
                    fun path ->
                        ThunderbirdStore.saveProfileRoot handleError context.GetProfileRootCollection path
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
            })

let private fakeProfileRootDependencies (test: ProfileRootDependencies -> unit) =
    let mutable stored: ProfileRootPath option = None

    test
        {
            Load = fun () -> Ok stored
            Save =
                fun path ->
                    stored <- Some path
                    Ok()
        }

let profileRootImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withProfileRootImplementation (name: string) (test: ProfileRootDependencies -> unit) =
    match name with
    | "real adapter" -> realProfileRootDependencies test
    | "in-memory fake" -> fakeProfileRootDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof profileRootImplementations)>]
let ``LoadProfileRoot returns None for a fresh store`` (implementation: string) =
    withProfileRootImplementation implementation (fun deps -> Assert.Equal(None, deps.Load() |> okOrFail "Load"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof profileRootImplementations)>]
let ``a saved profile root is visible to a later load`` (implementation: string) =
    withProfileRootImplementation implementation (fun deps ->
        let path = ProfileRootPath.create @"C:\Thunderbird" |> valueOrFail
        deps.Save path |> okOrFail "Save"

        let loaded = deps.Load() |> okOrFail "Load"
        Assert.Equal(Some @"C:\Thunderbird", loaded |> Option.map ProfileRootPath.value))

// ---------- Accounts: LoadMailAccounts / SaveMailAccounts ----------

type private AccountsDependencies = { Load: LoadMailAccounts; Save: SaveMailAccounts }

let private realAccountsDependencies (test: AccountsDependencies -> unit) =
    withTempContext (fun context ->
        test
            {
                Load =
                    fun () ->
                        ThunderbirdStore.loadMailAccounts handleError context.GetAccountsCollection context.GetFoldersCollection ()
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
                Save =
                    fun accounts ->
                        ThunderbirdStore.saveMailAccounts
                            handleError
                            context.GetAccountsCollection
                            context.GetFoldersCollection
                            accounts
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
            })

let private fakeAccountsDependencies (test: AccountsDependencies -> unit) =
    let mutable stored: DiscoveredMailAccount list = []

    test
        {
            Load = fun () -> Ok stored
            Save =
                fun accounts ->
                    stored <- accounts
                    Ok()
        }

let accountsImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withAccountsImplementation (name: string) (test: AccountsDependencies -> unit) =
    match name with
    | "real adapter" -> realAccountsDependencies test
    | "in-memory fake" -> fakeAccountsDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof accountsImplementations)>]
let ``LoadMailAccounts returns an empty list for a fresh store`` (implementation: string) =
    withAccountsImplementation implementation (fun deps -> Assert.Empty(deps.Load() |> okOrFail "Load"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof accountsImplementations)>]
let ``a saved account with a folder is visible to a later load`` (implementation: string) =
    withAccountsImplementation implementation (fun deps ->
        let account = anAccount @"C:\p|account1" @"C:\p\a1"
        deps.Save [ account ] |> okOrFail "Save"

        let loaded = deps.Load() |> okOrFail "Load"
        let readBack = Assert.Single loaded
        Assert.Equal(@"C:\p|account1", MailAccountId.value readBack.Id)
        let folder = Assert.Single readBack.Folders
        Assert.Equal("INBOX", folder.RelativePath))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof accountsImplementations)>]
let ``SaveMailAccounts replaces the previous set rather than accumulating`` (implementation: string) =
    withAccountsImplementation implementation (fun deps ->
        deps.Save [ anAccount @"C:\p|account1" @"C:\p\a1" ] |> okOrFail "Save 1"
        deps.Save [ anAccount @"C:\p|account2" @"C:\p\a2" ] |> okOrFail "Save 2"

        let loaded = deps.Load() |> okOrFail "Load"
        let only = Assert.Single loaded
        Assert.Equal(@"C:\p|account2", MailAccountId.value only.Id))

// ---------- Selection: LoadSelectedMailAccount / SaveSelectedMailAccount ----------

type private SelectionDependencies = { Load: LoadSelectedMailAccount; Save: SaveSelectedMailAccount }

let private realSelectionDependencies (test: SelectionDependencies -> unit) =
    withTempContext (fun context ->
        test
            {
                Load =
                    fun () ->
                        ThunderbirdStore.loadSelectedMailAccount handleError context.GetSelectedAccountCollection ()
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
                Save =
                    fun selected ->
                        ThunderbirdStore.saveSelectedMailAccount handleError context.GetSelectedAccountCollection selected
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
            })

let private fakeSelectionDependencies (test: SelectionDependencies -> unit) =
    let mutable stored: MailAccountId option = None

    test
        {
            Load = fun () -> Ok stored
            Save =
                fun selected ->
                    stored <- selected
                    Ok()
        }

let selectionImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withSelectionImplementation (name: string) (test: SelectionDependencies -> unit) =
    match name with
    | "real adapter" -> realSelectionDependencies test
    | "in-memory fake" -> fakeSelectionDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof selectionImplementations)>]
let ``LoadSelectedMailAccount returns None for a fresh store`` (implementation: string) =
    withSelectionImplementation implementation (fun deps -> Assert.Equal(None, deps.Load() |> okOrFail "Load"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof selectionImplementations)>]
let ``a saved selection is visible to a later load, and clearing it persists as absent`` (implementation: string) =
    withSelectionImplementation implementation (fun deps ->
        let id = MailAccountId.create @"C:\p|account1" |> valueOrFail
        deps.Save(Some id) |> okOrFail "Save"
        Assert.Equal(Some id, deps.Load() |> okOrFail "Load")

        deps.Save None |> okOrFail "Clear"
        Assert.Equal(None, deps.Load() |> okOrFail "Load"))

// ---------- Watermarks: LoadWatermark / SaveWatermark (integration-internal) / ClearWatermarks ----------

type private WatermarkDependencies =
    {
        Load: LoadWatermark
        Save: SaveWatermark
        Clear: ClearWatermarks
    }

let private realWatermarkDependencies (test: WatermarkDependencies -> unit) =
    withTempContext (fun context ->
        test
            {
                Load =
                    fun accountId relativePath ->
                        ThunderbirdStore.loadWatermarkEntry handleError context.GetWatermarksCollection accountId relativePath
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
                Save =
                    fun accountId relativePath watermark ->
                        ThunderbirdStore.saveWatermarkEntry
                            handleError
                            context.GetWatermarksCollection
                            accountId
                            relativePath
                            watermark
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
                Clear =
                    fun accountId ->
                        ThunderbirdStore.clearWatermarksFor handleError context.GetWatermarksCollection accountId
                        |> Result.mapError MailAccountApiMappers.toMailAccountError
            })

let private fakeWatermarkDependencies (test: WatermarkDependencies -> unit) =
    let store = Collections.Generic.Dictionary<string * string, FolderWatermark>()

    test
        {
            Load =
                fun accountId relativePath ->
                    match store.TryGetValue((MailAccountId.value accountId, relativePath)) with
                    | true, wm -> Ok(Some wm)
                    | false, _ -> Ok None
            Save =
                fun accountId relativePath watermark ->
                    store.[(MailAccountId.value accountId, relativePath)] <- watermark
                    Ok()
            Clear =
                fun accountId ->
                    let accountIdValue = MailAccountId.value accountId

                    store.Keys
                    |> Seq.filter (fun (a, _) -> a = accountIdValue)
                    |> Seq.toList
                    |> List.iter (fun key -> store.Remove key |> ignore)

                    Ok()
        }

let watermarkImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withWatermarkImplementation (name: string) (test: WatermarkDependencies -> unit) =
    match name with
    | "real adapter" -> realWatermarkDependencies test
    | "in-memory fake" -> fakeWatermarkDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof watermarkImplementations)>]
let ``LoadWatermark returns None when nothing has been saved`` (implementation: string) =
    withWatermarkImplementation implementation (fun deps ->
        let id = MailAccountId.create "a" |> valueOrFail
        Assert.Equal(None, deps.Load id "INBOX" |> okOrFail "Load"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof watermarkImplementations)>]
let ``a saved watermark is visible to a later load, and ClearWatermarks removes it`` (implementation: string) =
    withWatermarkImplementation implementation (fun deps ->
        let id = MailAccountId.create "a" |> valueOrFail
        let watermark: FolderWatermark = { SizeBytes = 100L; ModifiedAt = DateTime(2026, 8, 20); OffsetReached = 50L }

        deps.Save id "INBOX" watermark |> okOrFail "Save"
        Assert.Equal(Some watermark, deps.Load id "INBOX" |> okOrFail "Load")

        deps.Clear id |> okOrFail "Clear"
        Assert.Equal(None, deps.Load id "INBOX" |> okOrFail "Load"))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof watermarkImplementations)>]
let ``a watermark carrying a filesystem modification time round trips it exactly, kind included`` (implementation: string) =
    withWatermarkImplementation implementation (fun deps ->
        let id = MailAccountId.create "a" |> valueOrFail

        // Exactly the value readFolder stores: File.GetLastWriteTimeUtc, so Kind = Utc, and NTFS
        // keeps 100-ns ticks so it carries sub-millisecond precision. readFolder then compares
        // the loaded value to the file's own mtime with `=`, so anything a store loses here
        // turns every incremental read into a silent full re-read. The other watermark tests
        // above use DateTime(2026, 8, 20) - Kind Unspecified, midnight, whole seconds - which is
        // the one subset that survives a lossy store unchanged, so the fake and the real adapter
        // agreed over a difference that only production ever sees.
        let modifiedAt = DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc).AddTicks 1234L
        let watermark: FolderWatermark = { SizeBytes = 100L; ModifiedAt = modifiedAt; OffsetReached = 50L }

        deps.Save id "INBOX" watermark |> okOrFail "Save"

        let loaded =
            deps.Load id "INBOX"
            |> okOrFail "Load"
            |> Option.defaultWith (fun () -> failwith "expected a saved watermark")

        Assert.Equal(modifiedAt.Ticks, loaded.ModifiedAt.Ticks)
        Assert.Equal(DateTimeKind.Utc, loaded.ModifiedAt.Kind)
        Assert.Equal(100L, loaded.SizeBytes)
        Assert.Equal(50L, loaded.OffsetReached))

// ---------- CountMessages ----------

type private CountMessagesDependencies = { Count: MailAccountId -> Result<int, MailAccountError> }

let private realCountMessagesDependencies (test: CountMessagesDependencies -> unit) =
    let alphaStoreDirectory = Path.Combine(measuredShapeProfile, "ImapMail", "imap.alpha.example.com")
    let folders = MailFolderEnumerator.enumerate alphaStoreDirectory Mbox
    let account = { anAccount "alpha" alphaStoreDirectory with Folders = folders }

    // Discovery keeps a configured-but-missing account and gives it no folders, so both sides of
    // the suite have to answer for one.
    let gone = { anAccount "gone" goneStoreDirectory with StoreDirectoryExists = false; Folders = [] }

    let lookupAccount: LookupAccount =
        fun id ->
            if id = account.Id then Ok(Some account)
            elif id = gone.Id then Ok(Some gone)
            else Ok None

    test { Count = fun id -> MailFolderReader.countMessages lookupAccount id }

let private fakeCountMessagesDependencies (test: CountMessagesDependencies -> unit) =
    let lookupAccount: LookupAccount =
        fun id ->
            if MailAccountId.value id = "alpha" then
                // A store directory that exists, holding no folder file - "the account is empty",
                // which must stay distinguishable from "the store is gone" below.
                Ok(
                    Some
                        { anAccount "alpha" (Path.GetTempPath()) with
                            Folders = [ { RelativePath = "INBOX"; DisplayName = "INBOX"; SizeBytes = 0L; IsScannable = true } ] }
                )
            elif MailAccountId.value id = "gone" then
                Ok(Some { anAccount "gone" goneStoreDirectory with StoreDirectoryExists = false; Folders = [] })
            else
                Ok None

    test { Count = fun id -> MailFolderReader.countMessages lookupAccount id }

let countMessagesImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withCountMessagesImplementation (name: string) (test: CountMessagesDependencies -> unit) =
    match name with
    | "real adapter" -> realCountMessagesDependencies test
    | "in-memory fake" -> fakeCountMessagesDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof countMessagesImplementations)>]
let ``CountMessages returns MailAccountNotFound for an unknown account`` (implementation: string) =
    withCountMessagesImplementation implementation (fun deps ->
        let unknown = MailAccountId.create "not-alpha" |> valueOrFail
        Assert.Equal(Error(MailAccountNotFound unknown), deps.Count unknown))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof countMessagesImplementations)>]
let ``CountMessages reports a store directory that is gone rather than counting it as zero`` (implementation: string) =
    // "The store is gone" and "the account holds no messages" are different answers and must not
    // arrive as the same one - a count of zero for an account whose mail directory has been
    // deleted or whose drive is unplugged is a silent wrong result. The `alpha` case above pins
    // the other half: a store that IS there and holds nothing still counts zero.
    withCountMessagesImplementation implementation (fun deps ->
        let gone = MailAccountId.create "gone" |> valueOrFail

        match deps.Count gone with
        | Error(StoreDirectoryMissing(id, path)) ->
            Assert.Equal(gone, id)
            Assert.Equal(goneStoreDirectory, path)
        | other -> Assert.Fail(sprintf "Expected Error(StoreDirectoryMissing _), but got: %A" other))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof countMessagesImplementations)>]
let ``CountMessages counts an account whose store directory is on disk`` (implementation: string) =
    // The half the guard must not swallow. Both sides hold different fixtures, so the count
    // itself differs - what the contract pins is that a store that IS there is counted, never
    // refused as missing.
    withCountMessagesImplementation implementation (fun deps ->
        let alpha = MailAccountId.create "alpha" |> valueOrFail

        match deps.Count alpha with
        | Ok count -> Assert.True(count >= 0)
        | other -> Assert.Fail(sprintf "Expected Ok, but got: %A" other))

// ---------- DiscoverMailAccounts ----------

type private DiscoverDependencies = { Discover: DiscoverMailAccounts }

let private discoverMailAccounts: DiscoverMailAccounts =
    fun profileRoot ->
        let rootPath = ProfileRootPath.value profileRoot
        let scanOutcome = ThunderbirdFolderScanner.scan rootPath

        let accounts =
            scanOutcome.ProfileDirectories
            |> List.collect (fun dir ->
                match ThunderbirdAccountReader.read dir with
                | Ok accts -> accts
                | Error _ -> [])
            |> List.map (fun account ->
                { account with Folders = MailFolderEnumerator.enumerate account.StoreDirectory account.StoreFormat })

        if List.isEmpty scanOutcome.ProfileDirectories && List.isEmpty scanOutcome.Unreadable then
            Error(NoProfileFound rootPath)
        else
            Ok
                {
                    Accounts = accounts
                    ProfilesFound = scanOutcome.ProfileDirectories
                    Unreadable = scanOutcome.Unreadable
                    SelectionCleared = false
                }

let private realDiscoverDependencies (test: DiscoverDependencies -> unit) = test { Discover = discoverMailAccounts }

/// The "fake" a workflow unit test would use - no filesystem, a fixed result.
let private fakeDiscoverDependencies (test: DiscoverDependencies -> unit) =
    test
        {
            Discover =
                fun profileRoot ->
                    if ProfileRootPath.value profileRoot = measuredShapeProfile then
                        Ok
                            {
                                Accounts = [ anAccount "fake-account" @"C:\fake" ]
                                ProfilesFound = [ measuredShapeProfile ]
                                Unreadable = []
                                SelectionCleared = false
                            }
                    else
                        Error(NoProfileFound(ProfileRootPath.value profileRoot))
        }

let discoverImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withDiscoverImplementation (name: string) (test: DiscoverDependencies -> unit) =
    match name with
    | "real adapter" -> realDiscoverDependencies test
    | "in-memory fake" -> fakeDiscoverDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof discoverImplementations)>]
let ``DiscoverMailAccounts finds at least one account under the measured-shape fixture`` (implementation: string) =
    withDiscoverImplementation implementation (fun deps ->
        let path = ProfileRootPath.create measuredShapeProfile |> valueOrFail
        let result = deps.Discover path |> okOrFail "Discover"
        Assert.NotEmpty result.Accounts
        Assert.Equal<string list>([ measuredShapeProfile ], result.ProfilesFound)
        // Discovery never reads the stored selection, so neither implementation may claim to have
        // reconciled one - only ScanForMailAccountsWorkflow can answer this.
        Assert.False result.SelectionCleared)

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof discoverImplementations)>]
let ``DiscoverMailAccounts reports NoProfileFound for a folder with no prefs.js`` (implementation: string) =
    withDiscoverImplementation implementation (fun deps ->
        let emptyFolder = Path.Combine(Path.GetTempPath(), $"mdb-contract-{Guid.NewGuid()}")
        Directory.CreateDirectory emptyFolder |> ignore

        try
            let path = ProfileRootPath.create emptyFolder |> valueOrFail

            match deps.Discover path with
            | Error(NoProfileFound reportedPath) -> Assert.Equal(emptyFolder, reportedPath)
            | other -> Assert.Fail($"Expected Error(NoProfileFound _), but got: {other}")
        finally
            Directory.Delete(emptyFolder, true))

// ---------- ReadMailFolder ----------

type private ReadMailFolderDependencies = { Read: ReadMailFolder }

let private realReadMailFolderDependencies (test: ReadMailFolderDependencies -> unit) =
    let account =
        { anAccount "reader-account" mboxFixtures with
            Folders = [ { RelativePath = "NoMessageId.mbox"; DisplayName = "NoMessageId.mbox"; SizeBytes = 0L; IsScannable = true } ] }

    let gone = { anAccount "gone-account" goneStoreDirectory with StoreDirectoryExists = false; Folders = [] }

    let lookupAccount: LookupAccount =
        fun id ->
            if id = account.Id then Ok(Some account)
            elif id = gone.Id then Ok(Some gone)
            else Ok None

    let noWatermark: LoadWatermark = fun _ _ -> Ok None
    let ignoreWatermark: SaveWatermark = fun _ _ _ -> Ok()

    test { Read = fun accountId cutoff -> MailFolderReader.read lookupAccount noWatermark ignoreWatermark accountId cutoff }

let private fakeReadMailFolderDependencies (test: ReadMailFolderDependencies -> unit) =
    let message: MailMessage =
        {
            SourceMessageId = "synthesized:fake"
            Sender = "sender@example.com"
            Subject = "Fake message"
            ReceivedAt = DateTime(2026, 1, 5)
            BodyText = Some "body"
            BodyHtml = None
            Attachments = []
        }

    test
        {
            Read =
                fun accountId _cutoff ->
                    match MailAccountId.value accountId with
                    | "reader-account" -> Ok [ message ]
                    // A configured-but-missing store is not "this account has no mail" - the fake
                    // has to say so too, or a workflow test could stay green over a shape the real
                    // reader never produces.
                    | "gone-account" -> Error(StoreDirectoryMissing(accountId, goneStoreDirectory))
                    | _ -> Error(MailAccountNotFound accountId)
        }

let readMailFolderImplementations: obj[] seq = [ [| box "real adapter" |]; [| box "in-memory fake" |] ]

let private withReadMailFolderImplementation (name: string) (test: ReadMailFolderDependencies -> unit) =
    match name with
    | "real adapter" -> realReadMailFolderDependencies test
    | "in-memory fake" -> fakeReadMailFolderDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof readMailFolderImplementations)>]
let ``ReadMailFolder returns at least one message with every field populated for a known account`` (implementation: string) =
    withReadMailFolderImplementation implementation (fun deps ->
        let id = MailAccountId.create "reader-account" |> valueOrFail
        let cutoff = ScanCutoff.ofStartOfDay (DateTime(2000, 1, 1))

        let messages = deps.Read id cutoff |> okOrFail "Read"

        let message = Assert.Single messages
        Assert.False(String.IsNullOrWhiteSpace message.SourceMessageId)
        Assert.False(String.IsNullOrWhiteSpace message.Sender)
        Assert.True(message.BodyText.IsSome || message.BodyHtml.IsSome))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof readMailFolderImplementations)>]
let ``ReadMailFolder returns MailAccountNotFound for an unknown account`` (implementation: string) =
    withReadMailFolderImplementation implementation (fun deps ->
        let unknown = MailAccountId.create "not-reader-account" |> valueOrFail
        let cutoff = ScanCutoff.ofStartOfDay (DateTime(2000, 1, 1))

        Assert.Equal(Error(MailAccountNotFound unknown), deps.Read unknown cutoff))

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof readMailFolderImplementations)>]
let ``ReadMailFolder reports a store directory that is gone rather than returning no messages`` (implementation: string) =
    withReadMailFolderImplementation implementation (fun deps ->
        let gone = MailAccountId.create "gone-account" |> valueOrFail
        let cutoff = ScanCutoff.ofStartOfDay (DateTime(2000, 1, 1))

        match deps.Read gone cutoff with
        | Error(StoreDirectoryMissing(id, path)) ->
            Assert.Equal(gone, id)
            Assert.Equal(goneStoreDirectory, path)
        | other -> Assert.Fail(sprintf "Expected Error(StoreDirectoryMissing _), but got: %A" other))

// ---------- FolderPicker (Phase 7's UI-level type - no real, GUI-only implementation to run
// headlessly against; both members here are the kind of lambda a test substitutes it with) ----------

let folderPickerImplementations: obj[] seq = [ [| box "returns a path" |]; [| box "returns none (cancelled)" |] ]

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof folderPickerImplementations)>]
let ``FolderPicker conforms to unit -> string option for both outcomes`` (implementation: string) =
    let picker: FolderPicker =
        match implementation with
        | "returns a path" -> fun () -> Some @"C:\chosen"
        | "returns none (cancelled)" -> fun () -> None
        | other -> failwith $"Unknown implementation '{other}'"

    match implementation, picker () with
    | "returns a path", Some path -> Assert.Equal(@"C:\chosen", path)
    | "returns none (cancelled)", None -> ()
    | _, actual -> Assert.Fail($"Unexpected result for '{implementation}': {actual}")
