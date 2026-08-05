module MyDogsbody.Tests.Contracts.CredentialApiContractTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// CredentialApi is a published interface, so one suite runs against the real API and against the
// in-memory fake the UI's module-creator tests use. Without this, the UI suite could stay green
// over a fake that behaves in ways the real API never would.

let private handleError = HandleErrorBuilder (fun _ -> ())

// ---------- the real API, over a temp LiteDB file ----------

let private withRealApi (test: CredentialApi -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test (CredentialApiFactory.createCredentialApi handleError context.GetCredentialCollection)
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

// ---------- the in-memory fake ----------

/// A record literal rather than a class, because CredentialApi is a record of functions. Kept
/// faithful to the real API's behaviour - including validation and not-found - which is exactly
/// what this suite is here to enforce.
let private withFakeApi (test: CredentialApi -> unit) =
    let rows = ResizeArray<IntegrationCredentialUiType>()
    let mutable nextId = 0

    let validate (credentials: string) (username: string) =
        if String.IsNullOrWhiteSpace credentials then
            Some "Credentials must not be empty."
        elif String.IsNullOrWhiteSpace username then
            Some "Username must not be empty."
        else
            None

    let fail action message = Error (MyDogsbodyException(action, message))

    test
        {
            GetAllCredentials = fun () -> Ok (List.ofSeq rows)

            AddCredential =
                fun credential ->
                    match validate credential.Credentials credential.Username with
                    | Some reason -> fail ActionNames.MyDogsbody.Startup.CredentialApi.addCredential reason
                    | None ->
                        nextId <- nextId + 1

                        rows.Add
                            {
                                Id = nextId.ToString("x24")
                                InfrastructureType = credential.InfrastructureType
                                Credentials = credential.Credentials
                                Username = credential.Username
                            }

                        Ok ()

            EditCredential =
                fun credential ->
                    match validate credential.Credentials credential.Username with
                    | Some reason -> fail ActionNames.MyDogsbody.Startup.CredentialApi.editCredential reason
                    | None ->
                        match rows |> Seq.tryFindIndex (fun row -> row.Id = credential.Id) with
                        | Some index ->
                            rows.[index] <- credential
                            Ok ()
                        | None ->
                            fail
                                ActionNames.MyDogsbody.Startup.CredentialApi.editCredential
                                $"No credential was found with id '{credential.Id}'."
        }

/// Public because xUnit's MemberData resolves it by reflection on the compiled class.
let implementations: obj[] seq =
    [
        [| box "real api" |]
        [| box "fake api" |]
    ]

let private withImplementation (name: string) (test: CredentialApi -> unit) =
    match name with
    | "real api" -> withRealApi test
    | "fake api" -> withFakeApi test
    | other -> failwith $"Unknown implementation '{other}'"

let private aCredential: IntegrationCredentialUiTypeWithoutId =
    {
        InfrastructureType = InfrastructureType.Google
        Credentials = "google-secret"
        Username = "person@gmail.com"
    }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

let private errorOrFail label result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex
    | Ok _ -> failwith $"{label} expected Error, but got Ok"

// ---------- the shared suite ----------

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``GetAllCredentials returns an empty list before anything is added`` (implementation: string) =
    withImplementation implementation (fun api ->
        Assert.Empty(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddCredential stores every field and assigns an identifier`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Act
        api.AddCredential aCredential |> okOrFail "AddCredential"

        // Assert
        let stored = Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
        Assert.False(String.IsNullOrWhiteSpace stored.Id)
        Assert.Equal(InfrastructureType.Google, stored.InfrastructureType)
        Assert.Equal("google-secret", stored.Credentials)
        Assert.Equal("person@gmail.com", stored.Username)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditCredential changes the addressed credential`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        api.AddCredential aCredential |> okOrFail "AddCredential"
        let stored = Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")

        // Act
        api.EditCredential
            {
                Id = stored.Id
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = "rotated"
                Username = "rotated@outlook.com"
            }
        |> okOrFail "EditCredential"

        // Assert
        let reloaded = Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
        Assert.Equal(stored.Id, reloaded.Id)
        Assert.Equal(InfrastructureType.Microsoft, reloaded.InfrastructureType)
        Assert.Equal("rotated", reloaded.Credentials)
        Assert.Equal("rotated@outlook.com", reloaded.Username)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddCredential rejects an empty secret with the documented message`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Act
        let actual =
            api.AddCredential { aCredential with Credentials = "   " }
            |> errorOrFail "AddCredential"

        // Assert
        Assert.Equal("Credentials must not be empty.", actual.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.CredentialApi.addCredential, actual.ActionName)
        Assert.Empty(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``AddCredential rejects an empty username with the documented message`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Act
        let actual =
            api.AddCredential { aCredential with Username = "" }
            |> errorOrFail "AddCredential"

        // Assert
        Assert.Equal("Username must not be empty.", actual.Message)
        Assert.Empty(api.GetAllCredentials() |> okOrFail "GetAllCredentials")
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditCredential reports not found for an unknown identifier`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        api.AddCredential aCredential |> okOrFail "AddCredential"

        // Act
        let actual =
            api.EditCredential
                {
                    Id = "507f1f77bcf86cd799439011"
                    InfrastructureType = InfrastructureType.Google
                    Credentials = "ignored"
                    Username = "ignored@gmail.com"
                }
            |> errorOrFail "EditCredential"

        // Assert
        Assert.Equal("No credential was found with id '507f1f77bcf86cd799439011'.", actual.Message)
        Assert.Equal(ActionNames.MyDogsbody.Startup.CredentialApi.editCredential, actual.ActionName)
        Assert.Equal("google-secret", (Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")).Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``EditCredential rejects an empty secret and changes nothing`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        api.AddCredential aCredential |> okOrFail "AddCredential"
        let stored = Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")

        // Act
        let actual =
            api.EditCredential { stored with Credentials = "" }
            |> errorOrFail "EditCredential"

        // Assert
        Assert.Equal("Credentials must not be empty.", actual.Message)
        Assert.Equal("google-secret", (Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")).Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``two credentials sharing an infrastructure type are kept apart`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        api.AddCredential aCredential |> okOrFail "AddCredential"
        api.AddCredential { aCredential with Credentials = "second-secret" } |> okOrFail "AddCredential"

        let stored = api.GetAllCredentials() |> okOrFail "GetAllCredentials"
        let second = stored |> List.find (fun row -> row.Credentials = "second-secret")

        // Act
        api.EditCredential { second with Credentials = "second-rotated" } |> okOrFail "EditCredential"

        // Assert
        let reloaded = api.GetAllCredentials() |> okOrFail "GetAllCredentials"
        Assert.Equal(2, List.length reloaded)
        Assert.Equal("google-secret", (reloaded |> List.find (fun row -> row.Id <> second.Id)).Credentials)
        Assert.Equal("second-rotated", (reloaded |> List.find (fun row -> row.Id = second.Id)).Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``an awkward secret survives the api round trip`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        let awkward = "line one\nline two\ttabbed éàü \"quoted\""

        // Act
        api.AddCredential { aCredential with Credentials = awkward } |> okOrFail "AddCredential"

        // Assert
        Assert.Equal(awkward, (Assert.Single(api.GetAllCredentials() |> okOrFail "GetAllCredentials")).Credentials)
    )

[<Theory; Trait("Level", "Contract")>]
[<MemberData(nameof implementations)>]
let ``every InfrastructureType member round trips through the api`` (implementation: string) =
    withImplementation implementation (fun api ->
        // Arrange
        let allMembers =
            Enum.GetValues(typeof<InfrastructureType>) |> Seq.cast<InfrastructureType> |> Seq.toList

        // Act
        for infrastructureType in allMembers do
            api.AddCredential
                {
                    InfrastructureType = infrastructureType
                    Credentials = $"{infrastructureType}-secret"
                    Username = $"{infrastructureType}@example.com"
                }
            |> okOrFail "AddCredential"

        // Assert
        let stored = api.GetAllCredentials() |> okOrFail "GetAllCredentials"
        Assert.Equal(List.length allMembers, List.length stored)

        for infrastructureType in allMembers do
            let row = stored |> List.find (fun c -> c.InfrastructureType = infrastructureType)
            Assert.Equal($"{infrastructureType}-secret", row.Credentials)
            Assert.Equal($"{infrastructureType}@example.com", row.Username)
    )
