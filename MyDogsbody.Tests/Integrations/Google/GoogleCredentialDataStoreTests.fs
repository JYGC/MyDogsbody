module MyDogsbody.Tests.Integrations.Google.GoogleCredentialDataStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

/// No-op logger, so these tests never reach Logging.db.
let private handleError = HandleErrorBuilder(fun _ -> ())

/// Fresh disposable database per test, no state shared - the same shape every other Google
/// store test in this project uses. The raw collection getter is handed back alongside the
/// store under test, so a test can verify through `GoogleCredentialStore` directly.
let private withDataStore
    (test: Google.Apis.Util.Store.IDataStore -> (unit -> Database.Types.GoogleCredentialsCollection) -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        let dataStore =
            GoogleCredentialDataStore.GoogleCredentialDataStore(handleError, context.GetCredentialCollection)
            :> Google.Apis.Util.Store.IDataStore

        test dataStore context.GetCredentialCollection
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// Not `private`: Newtonsoft's reflection-based serializer (used cross-assembly, from
// Google.Apis) cannot see a private type's properties, and would silently serialize it as `{}`.
[<CLIMutable>]
type SampleToken = { AccessToken: string; RefreshToken: string; ExpiresInSeconds: int }

[<Fact; Trait("Level", "Integration")>]
let ``GetAsync returns the default value when nothing has been stored for the key`` () =
    withDataStore (fun dataStore _ ->
        let actual = dataStore.GetAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously
        Assert.Equal(Unchecked.defaultof<SampleToken>, actual)
    )

[<Fact; Trait("Level", "Integration")>]
let ``StoreAsync then GetAsync returns the same value, byte-for-byte including awkward characters`` () =
    withDataStore (fun dataStore _ ->
        let token =
            {
                AccessToken = "  ya29.a0Af\nline two\ttabbed éàü \"quoted\"  "
                RefreshToken = "1//0-abc_DEF"
                ExpiresInSeconds = 3600
            }

        dataStore.StoreAsync("account-1", token) |> Async.AwaitTask |> Async.RunSynchronously

        let actual = dataStore.GetAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously

        Assert.Equal(token, actual)
    )

[<Fact; Trait("Level", "Integration")>]
let ``storing twice under the same key updates rather than duplicating`` () =
    withDataStore (fun dataStore getCollection ->
        let first = { AccessToken = "first"; RefreshToken = "r1"; ExpiresInSeconds = 100 }
        let second = { AccessToken = "second"; RefreshToken = "r2"; ExpiresInSeconds = 200 }

        dataStore.StoreAsync("account-1", first) |> Async.AwaitTask |> Async.RunSynchronously
        dataStore.StoreAsync("account-1", second) |> Async.AwaitTask |> Async.RunSynchronously

        let actual = dataStore.GetAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously
        Assert.Equal(second, actual)

        let allRows = GoogleCredentialStore.getAll handleError getCollection () |> Result.defaultValue []
        Assert.Equal(1, List.length allRows)
    )

[<Fact; Trait("Level", "Integration")>]
let ``different keys are stored independently`` () =
    withDataStore (fun dataStore _ ->
        let forAccountOne = { AccessToken = "one"; RefreshToken = "r1"; ExpiresInSeconds = 1 }
        let forAccountTwo = { AccessToken = "two"; RefreshToken = "r2"; ExpiresInSeconds = 2 }

        dataStore.StoreAsync("account-1", forAccountOne) |> Async.AwaitTask |> Async.RunSynchronously
        dataStore.StoreAsync("account-2", forAccountTwo) |> Async.AwaitTask |> Async.RunSynchronously

        let readOne = dataStore.GetAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously
        let readTwo = dataStore.GetAsync<SampleToken>("account-2") |> Async.AwaitTask |> Async.RunSynchronously

        Assert.Equal(forAccountOne, readOne)
        Assert.Equal(forAccountTwo, readTwo)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteAsync removes the stored value so GetAsync returns the default again`` () =
    withDataStore (fun dataStore _ ->
        let token = { AccessToken = "gone-soon"; RefreshToken = "r"; ExpiresInSeconds = 1 }

        dataStore.StoreAsync("account-1", token) |> Async.AwaitTask |> Async.RunSynchronously
        dataStore.DeleteAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously

        let actual = dataStore.GetAsync<SampleToken>("account-1") |> Async.AwaitTask |> Async.RunSynchronously
        Assert.Equal(Unchecked.defaultof<SampleToken>, actual)
    )

[<Fact; Trait("Level", "Integration")>]
let ``DeleteAsync for a key that was never stored does not throw`` () =
    withDataStore (fun dataStore _ ->
        dataStore.DeleteAsync<SampleToken>("never-stored") |> Async.AwaitTask |> Async.RunSynchronously
    )

[<Fact; Trait("Level", "Integration")>]
let ``ClearAsync empties the collection`` () =
    withDataStore (fun dataStore getCollection ->
        dataStore.StoreAsync("account-1", { AccessToken = "a"; RefreshToken = "b"; ExpiresInSeconds = 1 })
        |> Async.AwaitTask
        |> Async.RunSynchronously

        dataStore.ClearAsync() |> Async.AwaitTask |> Async.RunSynchronously

        let remaining = GoogleCredentialStore.getAll handleError getCollection ()
        Assert.Equal(Ok [], remaining)
    )
