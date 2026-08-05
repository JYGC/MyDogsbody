module MyDogsbody.Tests.Startup.CredentialApiFactoryTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder (fun _ -> ())

/// Fresh LiteDB file per test, nothing shared between tests.
let private withApi (test: CredentialApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"
    let api = CredentialApiFactory.createCredentialApi handleError context.GetCredentialCollection
    try
        test api
    finally
        // getDatabaseContext closes over the LiteDatabase and never hands it back, so the
        // handle cannot be disposed from here and Windows may still hold the file. The
        // delete is best effort; the seam needs a Dispose before it can be guaranteed.
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``GetAllCredentials returns an empty list for a fresh database`` () =
    withApi (fun api ->
        match api.GetAllCredentials() with
        | Ok credentials -> Assert.Empty(credentials)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential then GetAllCredentials returns the stored credential`` () =
    withApi (fun api ->
        // Arrange
        let newCredential: IntegrationCredentialUiTypeWithoutId =
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "google-secret"
                Username = "user@gmail.com"
            }

        // Act
        let addResult = api.AddCredential newCredential

        // Assert
        Assert.Equal(Ok(), addResult)

        match api.GetAllCredentials() with
        | Ok [ stored ] ->
            Assert.False(String.IsNullOrWhiteSpace stored.Id)
            Assert.Equal(InfrastructureType.Google, stored.InfrastructureType)
            Assert.Equal("google-secret", stored.Credentials)
            // Username survives the ExternalUsername rename at the domain boundary.
            Assert.Equal("user@gmail.com", stored.Username)
        | Ok other -> Assert.Fail($"Expected exactly one credential, got {other.Length}")
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential updates the stored values`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "old-secret"
                Username = "old@gmail.com"
            }
        |> ignore

        let stored =
            match api.GetAllCredentials() with
            | Ok [ credential ] -> credential
            | _ -> failwith "Arrange failed: expected exactly one stored credential"

        // Act
        let editResult =
            api.EditCredential
                { stored with
                    Credentials = "new-secret"
                    Username = "new@gmail.com" }

        // Assert
        Assert.Equal(Ok(), editResult)

        match api.GetAllCredentials() with
        | Ok [ updated ] ->
            Assert.Equal(stored.Id, updated.Id)
            Assert.Equal(InfrastructureType.Google, updated.InfrastructureType)
            Assert.Equal("new-secret", updated.Credentials)
            Assert.Equal("new@gmail.com", updated.Username)
        | Ok other -> Assert.Fail($"Expected exactly one credential, got {other.Length}")
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential keeps credentials of different infrastructure types apart`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "google-secret"
                Username = "user@gmail.com"
            }
        |> ignore

        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = "microsoft-secret"
                Username = "user@outlook.com"
            }
        |> ignore

        // Act
        let result = api.GetAllCredentials()

        // Assert
        match result with
        | Ok credentials ->
            Assert.Equal(2, credentials.Length)

            let google =
                credentials |> List.find (fun c -> c.InfrastructureType = InfrastructureType.Google)
            Assert.Equal("google-secret", google.Credentials)
            Assert.Equal("user@gmail.com", google.Username)

            let microsoft =
                credentials |> List.find (fun c -> c.InfrastructureType = InfrastructureType.Microsoft)
            Assert.Equal("microsoft-secret", microsoft.Credentials)
            Assert.Equal("user@outlook.com", microsoft.Username)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

// --- Characterization -------------------------------------------------------------------
// CredentialsRepository.updateOne matches on InfrastructureType and ignores the Id. These
// two tests pin that behaviour as it stands today; neither asserts that it is desirable.

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential updates the first match of the infrastructure type, ignoring Id`` () =
    withApi (fun api ->
        // Arrange — two credentials sharing one infrastructure type
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "first-secret"
                Username = "first@gmail.com"
            }
        |> ignore

        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "second-secret"
                Username = "second@gmail.com"
            }
        |> ignore

        let second =
            match api.GetAllCredentials() with
            | Ok credentials -> credentials |> List.item 1
            | Error ex -> failwith $"Arrange failed: {ex.Message}"

        // Act — edit the *second* credential by Id
        api.EditCredential { second with Credentials = "edited-secret" } |> ignore

        // Assert — the *first* one changed instead
        match api.GetAllCredentials() with
        | Ok credentials ->
            Assert.Equal("edited-secret", (List.item 0 credentials).Credentials)
            Assert.Equal("second-secret", (List.item 1 credentials).Credentials)
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential returns Ok and changes nothing when no credential matches`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "google-secret"
                Username = "user@gmail.com"
            }
        |> ignore

        // Act — no Microsoft credential exists
        let result =
            api.EditCredential
                {
                    Id = "68b4f1c2e3a4b5c6d7e8f900"
                    InfrastructureType = InfrastructureType.Microsoft
                    Credentials = "microsoft-secret"
                    Username = "user@outlook.com"
                }

        // Assert — a silent success, not an error
        Assert.Equal(Ok(), result)

        match api.GetAllCredentials() with
        | Ok [ unchanged ] ->
            Assert.Equal(InfrastructureType.Google, unchanged.InfrastructureType)
            Assert.Equal("google-secret", unchanged.Credentials)
        | Ok other -> Assert.Fail($"Expected exactly one credential, got {other.Length}")
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")
    )
