module MyDogsbody.Tests.Contracts.CredentialDependencyContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup

// A dependency function type is this architecture's published interface, so CLAUDE.md's
// shared-suite rule applies to each one: the suite below runs against the real adapter binding
// AND against every fake the workflow unit tests stand in for it.
//
// The failure this catches: a fake returning a shape the real store never produces, leaving a
// workflow's unit suite green over code that cannot work in production.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private aCredential infrastructure secret username : ValidCredential =
    {
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

let private anEdit id infrastructure secret username : ValidCredentialEdit =
    {
        Id = CredentialId.create id |> valueOrFail
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

/// The three dependencies, bound together over one store, however that store is implemented.
type private CredentialDependencies =
    {
        Load: LoadCredentials
        Save: SaveCredential
        Update: UpdateCredential
    }

// ---------- the real adapter, over a temp LiteDB file ----------

let private withRealDependencies (test: CredentialDependencies -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"
    let getCollection = context.GetCredentialCollection

    try
        test
            {
                Load =
                    fun () ->
                        CredentialStore.getAll handleError getCollection ()
                        |> Result.mapError CredentialApiMappers.toCredentialError
                Save =
                    fun credential ->
                        CredentialStore.insertOne handleError getCollection credential
                        |> Result.mapError CredentialApiMappers.toCredentialError
                Update =
                    fun edit ->
                        CredentialStore.updateOne handleError getCollection edit
                        |> Result.mapError CredentialApiMappers.toCredentialError
            }
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// ---------- the in-memory fake the workflow unit tests use ----------

let private withFakeDependencies (test: CredentialDependencies -> unit) =
    // Stands in for the store the same way the Phase 1 workflow lambdas do. Modelled closely
    // enough that the suite below cannot tell the two apart.
    let rows = ResizeArray<StoredCredential>()
    let mutable nextId = 0

    let newId () =
        nextId <- nextId + 1
        CredentialId.create (nextId.ToString("x24")) |> valueOrFail

    test
        {
            Load = fun () -> Ok (List.ofSeq rows)

            Save =
                fun credential ->
                    let stored =
                        {
                            Id = newId ()
                            Infrastructure = credential.Infrastructure
                            Credentials = credential.Credentials
                            ExternalUsername = credential.ExternalUsername
                        }

                    rows.Add stored
                    Ok stored

            Update =
                fun edit ->
                    match rows |> Seq.tryFindIndex (fun row -> row.Id = edit.Id) with
                    | None -> Ok None
                    | Some index ->
                        let updated =
                            {
                                Id = edit.Id
                                Infrastructure = edit.Infrastructure
                                Credentials = edit.Credentials
                                ExternalUsername = edit.ExternalUsername
                            }

                        rows.[index] <- updated
                        Ok (Some updated)
        }

/// Every implementation of the three dependency types must satisfy all of this.
/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real adapter" |]
        [| box "in-memory fake" |]
    ]

let private withImplementation (name: string) (test: CredentialDependencies -> unit) =
    match name with
    | "real adapter" -> withRealDependencies test
    | "in-memory fake" -> withFakeDependencies test
    | other -> failwith $"Unknown implementation '{other}'"

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (error: CredentialError) -> failwith $"{label} expected Ok, but got Error: {error}"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``LoadCredentials returns an empty list for an empty store`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Act
        let actual = dependencies.Load() |> okOrFail "Load"

        // Assert
        Assert.Empty actual
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveCredential returns the credential with a non-empty identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Act
        let actual =
            aCredential Google "google-secret" "person@gmail.com"
            |> dependencies.Save
            |> okOrFail "Save"

        // Assert - every field, and an identifier the caller did not supply
        Assert.False(String.IsNullOrWhiteSpace(CredentialId.value actual.Id))
        Assert.Equal(Google, actual.Infrastructure)
        Assert.Equal("google-secret", CredentialSecret.value actual.Credentials)
        Assert.Equal("person@gmail.com", ExternalUsername.value actual.ExternalUsername)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``a saved credential is visible to LoadCredentials`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        let saved =
            aCredential Microsoft "ms-secret" "person@outlook.com"
            |> dependencies.Save
            |> okOrFail "Save"

        // Act
        let actual = dependencies.Load() |> okOrFail "Load"

        // Assert
        let loaded = Assert.Single actual
        Assert.Equal(CredentialId.value saved.Id, CredentialId.value loaded.Id)
        Assert.Equal(Microsoft, loaded.Infrastructure)
        Assert.Equal("ms-secret", CredentialSecret.value loaded.Credentials)
        Assert.Equal("person@outlook.com", ExternalUsername.value loaded.ExternalUsername)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``SaveCredential gives each credential a distinct identifier`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Act
        let first = aCredential Google "first" "first@gmail.com" |> dependencies.Save |> okOrFail "Save"
        let second = aCredential Google "second" "second@gmail.com" |> dependencies.Save |> okOrFail "Save"

        // Assert
        Assert.NotEqual<string>(CredentialId.value first.Id, CredentialId.value second.Id)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateCredential returns the updated credential when the identifier matches`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        let saved =
            aCredential Google "original" "original@gmail.com"
            |> dependencies.Save
            |> okOrFail "Save"

        // Act
        let actual =
            anEdit (CredentialId.value saved.Id) Google "rotated" "rotated@gmail.com"
            |> dependencies.Update
            |> okOrFail "Update"

        // Assert
        match actual with
        | Some updated ->
            Assert.Equal(CredentialId.value saved.Id, CredentialId.value updated.Id)
            Assert.Equal("rotated", CredentialSecret.value updated.Credentials)
            Assert.Equal("rotated@gmail.com", ExternalUsername.value updated.ExternalUsername)
        | None -> Assert.Fail("Expected the row to be found")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateCredential returns None when the identifier matches nothing`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        aCredential Google "original" "original@gmail.com" |> dependencies.Save |> okOrFail "Save"

        // Act
        let actual =
            anEdit "507f1f77bcf86cd799439011" Google "ignored" "ignored@gmail.com"
            |> dependencies.Update
            |> okOrFail "Update"

        // Assert - None, never a silent success
        Assert.True(Option.isNone actual)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``UpdateCredential changes only the addressed row`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange - two credentials sharing an infrastructure type
        let first = aCredential Google "first" "first@gmail.com" |> dependencies.Save |> okOrFail "Save"
        let second = aCredential Google "second" "second@gmail.com" |> dependencies.Save |> okOrFail "Save"

        // Act
        anEdit (CredentialId.value second.Id) Google "second-rotated" "second@gmail.com"
        |> dependencies.Update
        |> okOrFail "Update"
        |> ignore

        // Assert
        let loaded = dependencies.Load() |> okOrFail "Load"
        Assert.Equal(2, List.length loaded)

        let reloadedFirst = loaded |> List.find (fun row -> row.Id = first.Id)
        let reloadedSecond = loaded |> List.find (fun row -> row.Id = second.Id)
        Assert.Equal("first", CredentialSecret.value reloadedFirst.Credentials)
        Assert.Equal("second-rotated", CredentialSecret.value reloadedSecond.Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an update is visible to a later load`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        let saved = aCredential Google "original" "original@gmail.com" |> dependencies.Save |> okOrFail "Save"

        // Act
        anEdit (CredentialId.value saved.Id) Microsoft "rotated" "rotated@outlook.com"
        |> dependencies.Update
        |> okOrFail "Update"
        |> ignore

        // Assert
        let loaded = dependencies.Load() |> okOrFail "Load"
        let reloaded = Assert.Single loaded
        Assert.Equal(Microsoft, reloaded.Infrastructure)
        Assert.Equal("rotated", CredentialSecret.value reloaded.Credentials)
        Assert.Equal("rotated@outlook.com", ExternalUsername.value reloaded.ExternalUsername)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an awkward secret survives a save and load unchanged`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        let awkward = "line one\nline two\ttabbed éàü \"quoted\" {json:true}"

        // Act
        aCredential Google awkward "person@gmail.com" |> dependencies.Save |> okOrFail "Save" |> ignore
        let loaded = dependencies.Load() |> okOrFail "Load"

        // Assert
        Assert.Equal(awkward, CredentialSecret.value (Assert.Single loaded).Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``every Infrastructure case can be saved and loaded back`` (implementation: string) =
    withImplementation implementation (fun dependencies ->
        // Arrange
        let allCases =
            Reflection.FSharpType.GetUnionCases(typeof<Infrastructure>)
            |> Array.map (fun case -> Reflection.FSharpValue.MakeUnion(case, [||]) :?> Infrastructure)

        // Act
        for infrastructure in allCases do
            aCredential infrastructure $"{infrastructure}-secret" $"{infrastructure}@example.com"
            |> dependencies.Save
            |> okOrFail "Save"
            |> ignore

        // Assert
        let loaded = dependencies.Load() |> okOrFail "Load"
        Assert.Equal(allCases.Length, List.length loaded)

        for infrastructure in allCases do
            let stored = loaded |> List.find (fun row -> row.Infrastructure = infrastructure)
            Assert.Equal($"{infrastructure}-secret", CredentialSecret.value stored.Credentials)
    )
