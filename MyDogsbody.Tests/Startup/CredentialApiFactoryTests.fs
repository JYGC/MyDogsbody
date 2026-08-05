module MyDogsbody.Tests.Startup.CredentialApiFactoryTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

let private handleError = HandleErrorBuilder (fun _ -> ())

/// Fresh LiteDB file per test, nothing shared between tests. The context now carries a Dispose,
/// so the handle is released and the delete is no longer best-effort.
let private withApi (test: CredentialApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"
    let api = CredentialApiFactory.createCredentialApi handleError context.GetCredentialCollection

    try
        test api
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private googleCredential: IntegrationCredentialUiTypeWithoutId =
    {
        InfrastructureType = InfrastructureType.Google
        Credentials = "google-secret"
        Username = "person@gmail.com"
    }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message} (inner: {ex.InnerException})"

let private single (api: CredentialApi) =
    match api.GetAllCredentials() |> okOrFail "GetAllCredentials" with
    | [ only ] -> only
    | other -> failwith $"Expected exactly one credential, got {List.length other}"

[<Fact; Trait("Level", "Integration")>]
let ``GetAllCredentials returns an empty list for a fresh database`` () =
    withApi (fun api ->
        // Act
        let stored = api.GetAllCredentials() |> okOrFail "GetAllCredentials"

        // Assert
        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential then GetAllCredentials returns the stored credential`` () =
    withApi (fun api ->
        // Act
        api.AddCredential googleCredential |> okOrFail "AddCredential"

        // Assert - every field, plus the id the store assigned
        let stored = single api
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal(InfrastructureType.Google, stored.InfrastructureType)
        Assert.Equal("google-secret", stored.Credentials)
        Assert.Equal("person@gmail.com", stored.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential updates the stored values`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential googleCredential |> okOrFail "AddCredential"
        let stored = single api

        // Act
        api.EditCredential
            {
                Id = stored.Id
                InfrastructureType = InfrastructureType.Google
                Credentials = "rotated-secret"
                Username = "rotated@gmail.com"
            }
        |> okOrFail "EditCredential"

        // Assert
        let reloaded = single api
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal("rotated-secret", reloaded.Credentials)
        Assert.Equal("rotated@gmail.com", reloaded.Username)
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential keeps credentials of different infrastructure types apart`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential googleCredential |> okOrFail "AddCredential google"

        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = "ms-secret"
                Username = "person@outlook.com"
            }
        |> okOrFail "AddCredential microsoft"

        // Act
        let stored = api.GetAllCredentials() |> okOrFail "GetAllCredentials"

        // Assert
        Assert.Equal(2, List.length stored)
        let google = stored |> List.find (fun c -> c.InfrastructureType = InfrastructureType.Google)
        let microsoft = stored |> List.find (fun c -> c.InfrastructureType = InfrastructureType.Microsoft)
        Assert.Equal("google-secret", google.Credentials)
        Assert.Equal("ms-secret", microsoft.Credentials)
    )

// The two tests below replace the characterization tests that pinned the old behaviour:
//   - "EditCredential updates the first match of the infrastructure type, ignoring Id"
//   - "EditCredential returns Ok and changes nothing when no credential matches"
// Both described a defect. The migration fixes it, so they assert the new behaviour instead.

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential updates the addressed row when two credentials share an infrastructure type`` () =
    withApi (fun api ->
        // Arrange - two Google credentials. The old store matched on infrastructure type and
        // ignored the id, so this edit landed on the wrong row.
        api.AddCredential googleCredential |> okOrFail "AddCredential first"

        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = "second-secret"
                Username = "second@gmail.com"
            }
        |> okOrFail "AddCredential second"

        let stored = api.GetAllCredentials() |> okOrFail "GetAllCredentials"
        let second = stored |> List.find (fun c -> c.Credentials = "second-secret")

        // Act
        api.EditCredential
            {
                Id = second.Id
                InfrastructureType = InfrastructureType.Google
                Credentials = "second-rotated"
                Username = "second@gmail.com"
            }
        |> okOrFail "EditCredential"

        // Assert - the second row changed, the first did not
        let reloaded = api.GetAllCredentials() |> okOrFail "GetAllCredentials"
        Assert.Equal(2, List.length reloaded)

        Assert.Equal(
            "google-secret",
            (reloaded |> List.find (fun c -> c.Id <> second.Id)).Credentials
        )

        Assert.Equal(
            "second-rotated",
            (reloaded |> List.find (fun c -> c.Id = second.Id)).Credentials
        )
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential reports not found when no credential carries that id`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential googleCredential |> okOrFail "AddCredential"

        // Act - a well-formed id that was never stored
        let actual =
            api.EditCredential
                {
                    Id = "507f1f77bcf86cd799439011"
                    InfrastructureType = InfrastructureType.Google
                    Credentials = "ignored"
                    Username = "ignored@gmail.com"
                }

        // Assert - an Error naming the id, not the silent Ok the old store returned
        match actual with
        | Error ex ->
            Assert.Equal(ActionNames.MyDogsbody.Startup.CredentialApi.editCredential, ex.ActionName)
            Assert.Equal("No credential was found with id '507f1f77bcf86cd799439011'.", ex.Message)
        | Ok () -> Assert.Fail("Expected Error, but got Ok")

        // Assert - and nothing was changed
        Assert.Equal("google-secret", (single api).Credentials)
    )

// Validation is new behaviour: the old chain stored whatever it was given.

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential rejects an empty secret and stores nothing`` () =
    withApi (fun api ->
        // Act
        let actual =
            api.AddCredential
                {
                    InfrastructureType = InfrastructureType.Google
                    Credentials = "   "
                    Username = "person@gmail.com"
                }

        // Assert
        match actual with
        | Error ex ->
            Assert.Equal(ActionNames.MyDogsbody.Startup.CredentialApi.addCredential, ex.ActionName)
            Assert.Equal("Credentials must not be empty.", ex.Message)
        | Ok () -> Assert.Fail("Expected Error, but got Ok")

        Assert.Empty(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
    )

[<Fact; Trait("Level", "Integration")>]
let ``AddCredential rejects an empty username and stores nothing`` () =
    withApi (fun api ->
        // Act
        let actual =
            api.AddCredential
                {
                    InfrastructureType = InfrastructureType.Google
                    Credentials = "secret"
                    Username = ""
                }

        // Assert
        match actual with
        | Error ex -> Assert.Equal("Username must not be empty.", ex.Message)
        | Ok () -> Assert.Fail("Expected Error, but got Ok")

        Assert.Empty(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
    )

[<Fact; Trait("Level", "Integration")>]
let ``EditCredential rejects an empty secret and changes nothing`` () =
    withApi (fun api ->
        // Arrange
        api.AddCredential googleCredential |> okOrFail "AddCredential"
        let stored = single api

        // Act
        let actual =
            api.EditCredential
                {
                    Id = stored.Id
                    InfrastructureType = InfrastructureType.Google
                    Credentials = ""
                    Username = "person@gmail.com"
                }

        // Assert
        match actual with
        | Error ex -> Assert.Equal("Credentials must not be empty.", ex.Message)
        | Ok () -> Assert.Fail("Expected Error, but got Ok")

        Assert.Equal("google-secret", (single api).Credentials)
    )

[<Fact; Trait("Level", "Integration")>]
let ``a validation failure is never written to the log`` () =
    // Arrange - an expected failure is returned as a value and never handed to writeLog. The
    // exception log is for things worth reading a stack trace about.
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

    let api =
        CredentialApiFactory.createCredentialApi recordingHandleError context.GetCredentialCollection

    try
        // Act
        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Google
                Credentials = ""
                Username = ""
            }
        |> ignore

        // Assert
        Assert.Empty logged
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Integration")>]
let ``a store failure reaches the UI as an Error and is written to the log exactly once`` () =
    // Arrange - infrastructure collapse, in contrast to the test above
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter () : MyDogsbody.Integrations.Credentials.Database.Types.CredentialsCollection =
        raise (InvalidOperationException "database is gone")

    let api = CredentialApiFactory.createCredentialApi recordingHandleError failingGetter

    // Act
    let actual = api.GetAllCredentials()

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Startup.CredentialApi.getAllCredentials, ex.ActionName)
        Assert.Equal("Failed to retrieve all credentials.", ex.Message)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

    Assert.Single logged |> ignore

[<Fact; Trait("Level", "Integration")>]
let ``a credential with awkward characters survives the whole api round trip`` () =
    withApi (fun api ->
        // Arrange
        let awkward = "line one\nline two\ttabbed éàü \"quoted\""

        api.AddCredential
            {
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = awkward
                Username = "person@outlook.com"
            }
        |> okOrFail "AddCredential"

        // Assert
        Assert.Equal(awkward, (single api).Credentials)
    )
