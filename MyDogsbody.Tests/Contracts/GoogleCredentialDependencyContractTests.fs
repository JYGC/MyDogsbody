module MyDogsbody.Tests.Contracts.GoogleCredentialDependencyContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

// The Google credential store is the store change #6's account factory will call. Its three
// operations are a boundary, and a boundary owes a shared contract suite: the suite below runs
// against the real adapter (over a temp LiteDB file) AND against the in-memory fake a workflow
// test would stand in for it, so the fake cannot drift into shapes the real store never
// produces.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private aCredential secret username : ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

let private anEdit id secret username : ValidGoogleCredentialEdit =
    {
        Id = GoogleCredentialId.create id |> valueOrFail
        Secret = GoogleCredentialSecret.create secret |> valueOrFail
        Username = GoogleExternalUsername.create username |> valueOrFail
    }

type private GoogleCredentialDependencies =
    {
        Load: unit -> Result<StoredGoogleCredential list, MyDogsbodyException>
        Save: ValidGoogleCredential -> Result<StoredGoogleCredential, MyDogsbodyException>
        Update: ValidGoogleCredentialEdit -> Result<StoredGoogleCredential option, MyDogsbodyException>
    }

// ---------- the real adapter, over a temp LiteDB file ----------

let private withRealDependencies (test: GoogleCredentialDependencies -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "direct"
    let getCollection = context.GetCredentialCollection

    try
        test
            {
                Load = fun () -> GoogleCredentialStore.getAll handleError getCollection ()
                Save = fun c -> GoogleCredentialStore.insertOne handleError getCollection c
                Update = fun e -> GoogleCredentialStore.updateOne handleError getCollection e
            }
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// ---------- the in-memory fake ----------

let private withFakeDependencies (test: GoogleCredentialDependencies -> unit) =
    let rows = ResizeArray<StoredGoogleCredential>()
    let mutable nextId = 0

    let newId () =
        nextId <- nextId + 1
        GoogleCredentialId.create (nextId.ToString("x24")) |> valueOrFail

    test
        {
            Load = fun () -> Ok (List.ofSeq rows)

            Save =
                fun c ->
                    let stored = { Id = newId (); Secret = c.Secret; Username = c.Username }
                    rows.Add stored
                    Ok stored

            Update =
                fun e ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = e.Id) with
                    | None -> Ok None
                    | Some index ->
                        let updated = { Id = e.Id; Secret = e.Secret; Username = e.Username }
                        rows.[index] <- updated
                        Ok (Some updated)
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real adapter" |]
        [| box "in-memory fake" |]
    ]

let private withImplementation (name: string) (test: GoogleCredentialDependencies -> unit) =
    match name with
    | "real adapter" -> withRealDependencies test
    | "in-memory fake" -> withFakeDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Load returns an empty list for an empty store`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        Assert.Empty(dependencies.Load() |> okOrFail "Load")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Save returns the credential with a non-empty identifier and every field intact`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let actual =
            aCredential "google-secret" "person@gmail.com" |> dependencies.Save |> okOrFail "Save"

        Assert.False(String.IsNullOrWhiteSpace(GoogleCredentialId.value actual.Id))
        Assert.Equal("google-secret", GoogleCredentialSecret.value actual.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value actual.Username)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a saved credential is visible to Load`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved =
            aCredential "ms-secret" "person@gmail.com" |> dependencies.Save |> okOrFail "Save"

        let loaded = Assert.Single(dependencies.Load() |> okOrFail "Load")
        Assert.Equal(GoogleCredentialId.value saved.Id, GoogleCredentialId.value loaded.Id)
        Assert.Equal("ms-secret", GoogleCredentialSecret.value loaded.Secret)
        Assert.Equal("person@gmail.com", GoogleExternalUsername.value loaded.Username)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Save gives each credential a distinct identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let first = aCredential "first" "first@gmail.com" |> dependencies.Save |> okOrFail "Save"
        let second = aCredential "second" "second@gmail.com" |> dependencies.Save |> okOrFail "Save"
        Assert.NotEqual<string>(GoogleCredentialId.value first.Id, GoogleCredentialId.value second.Id)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Update returns the updated credential when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aCredential "original" "original@gmail.com" |> dependencies.Save |> okOrFail "Save"

        let actual =
            anEdit (GoogleCredentialId.value saved.Id) "rotated" "rotated@gmail.com"
            |> dependencies.Update
            |> okOrFail "Update"

        match actual with
        | Some updated ->
            Assert.Equal(GoogleCredentialId.value saved.Id, GoogleCredentialId.value updated.Id)
            Assert.Equal("rotated", GoogleCredentialSecret.value updated.Secret)
            Assert.Equal("rotated@gmail.com", GoogleExternalUsername.value updated.Username)
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Update returns None when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        aCredential "original" "original@gmail.com" |> dependencies.Save |> okOrFail "Save" |> ignore

        let actual =
            anEdit "507f1f77bcf86cd799439011" "ignored" "ignored@gmail.com"
            |> dependencies.Update
            |> okOrFail "Update"

        Assert.True(Option.isNone actual)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``Update changes only the addressed row`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let first = aCredential "first" "first@gmail.com" |> dependencies.Save |> okOrFail "Save"
        let second = aCredential "second" "second@gmail.com" |> dependencies.Save |> okOrFail "Save"

        anEdit (GoogleCredentialId.value second.Id) "second-rotated" "second@gmail.com"
        |> dependencies.Update
        |> okOrFail "Update"
        |> ignore

        let loaded = dependencies.Load() |> okOrFail "Load"
        Assert.Equal(2, List.length loaded)

        let reloadedFirst = loaded |> List.find (fun row -> row.Id = first.Id)
        let reloadedSecond = loaded |> List.find (fun row -> row.Id = second.Id)
        Assert.Equal("first", GoogleCredentialSecret.value reloadedFirst.Secret)
        Assert.Equal("second-rotated", GoogleCredentialSecret.value reloadedSecond.Secret)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an update is visible to a later load`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let saved = aCredential "original" "original@gmail.com" |> dependencies.Save |> okOrFail "Save"

        anEdit (GoogleCredentialId.value saved.Id) "rotated" "rotated@outlook.com"
        |> dependencies.Update
        |> okOrFail "Update"
        |> ignore

        let reloaded = Assert.Single(dependencies.Load() |> okOrFail "Load")
        Assert.Equal("rotated", GoogleCredentialSecret.value reloaded.Secret)
        Assert.Equal("rotated@outlook.com", GoogleExternalUsername.value reloaded.Username)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an awkward secret survives a save and load unchanged`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        let awkward = "  line one\nline two\ttabbed éàü \"quoted\" {json:true}  "

        aCredential awkward "person@gmail.com" |> dependencies.Save |> okOrFail "Save" |> ignore

        Assert.Equal(awkward, GoogleCredentialSecret.value (Assert.Single(dependencies.Load() |> okOrFail "Load")).Secret)
    )
