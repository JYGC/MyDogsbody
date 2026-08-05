module MyDogsbody.Tests.Contracts.CredentialBoundaryMapperTests

open System
open Xunit
open LiteDB
open MyDogsbody.Enums
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database.Models
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// The architecture's two mapping points, asserted field-for-field in both directions:
//   bottom - LiteDB entity <-> domain type, in the integration
//   top    - domain type   <-> UI record,   in Startup
// There is no chain test to write any more; each edge is asserted exhaustively instead.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private storedCredential id infrastructure secret username : StoredCredential =
    {
        Id = CredentialId.create id |> valueOrFail
        Infrastructure = infrastructure
        Credentials = CredentialSecret.create secret |> valueOrFail
        ExternalUsername = ExternalUsername.create username |> valueOrFail
    }

// ---------- bottom mapper ----------

[<Fact; Trait("Level", "Contract")>]
let ``toNewEntity carries every field of a valid credential`` () =
    // Arrange
    let credential: ValidCredential =
        {
            Infrastructure = Google
            Credentials = CredentialSecret.create "google-secret" |> valueOrFail
            ExternalUsername = ExternalUsername.create "person@gmail.com" |> valueOrFail
        }

    // Act
    let actual = CredentialEntityMappers.toNewEntity credential

    // Assert
    Assert.Equal(InfrastructureType.Google, actual.InfrastructureType)
    Assert.Equal("google-secret", actual.Credentials)
    Assert.Equal("person@gmail.com", actual.ExternalUsername)

[<Fact; Trait("Level", "Contract")>]
let ``toStoredCredential carries every field of an entity`` () =
    // Arrange
    let entity =
        Credential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            InfrastructureType = InfrastructureType.Microsoft,
            Credentials = "ms-secret",
            ExternalUsername = "person@outlook.com"
        )

    // Act
    let actual = CredentialEntityMappers.toStoredCredential entity

    // Assert
    match actual with
    | Ok stored ->
        Assert.Equal("507f1f77bcf86cd799439011", CredentialId.value stored.Id)
        Assert.Equal(Microsoft, stored.Infrastructure)
        Assert.Equal("ms-secret", CredentialSecret.value stored.Credentials)
        Assert.Equal("person@outlook.com", ExternalUsername.value stored.ExternalUsername)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Contract")>]
[<InlineData(null, "person@outlook.com", "Credentials must not be empty.")>]
[<InlineData("secret", null, "Username must not be empty.")>]
let ``toStoredCredential rejects a row that cannot satisfy the domain's rules``
    (secret: string)
    (username: string)
    (expectedReason: string)
    =
    // Arrange - LiteDB is schemaless, so a document written by an older build can carry a null
    // where a constrained type is required
    let entity =
        Credential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            InfrastructureType = InfrastructureType.Google,
            Credentials = secret,
            ExternalUsername = username
        )

    // Act
    let actual = CredentialEntityMappers.toStoredCredential entity

    // Assert
    match actual with
    | Error reason -> Assert.Equal(expectedReason, reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Contract")>]
let ``applyEdit overwrites every mutable field of the entity`` () =
    // Arrange
    let entity =
        Credential(
            Id = ObjectId "507f1f77bcf86cd799439011",
            InfrastructureType = InfrastructureType.Google,
            Credentials = "old-secret",
            ExternalUsername = "old@gmail.com"
        )

    let edit: ValidCredentialEdit =
        {
            Id = CredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail
            Infrastructure = Microsoft
            Credentials = CredentialSecret.create "new-secret" |> valueOrFail
            ExternalUsername = ExternalUsername.create "new@outlook.com" |> valueOrFail
        }

    // Act
    let actual = CredentialEntityMappers.applyEdit edit entity

    // Assert - and the identifier is untouched, because the row is addressed by it
    Assert.Equal(InfrastructureType.Microsoft, actual.InfrastructureType)
    Assert.Equal("new-secret", actual.Credentials)
    Assert.Equal("new@outlook.com", actual.ExternalUsername)
    Assert.Equal("507f1f77bcf86cd799439011", string actual.Id)

[<Fact; Trait("Level", "Contract")>]
let ``the bottom mapper round trips a credential unchanged`` () =
    // Arrange
    let credential: ValidCredential =
        {
            Infrastructure = Microsoft
            Credentials = CredentialSecret.create "line one\nline two\ttabbed éàü" |> valueOrFail
            ExternalUsername = ExternalUsername.create "person@outlook.com" |> valueOrFail
        }

    // Act - out to the entity and back
    let entity = CredentialEntityMappers.toNewEntity credential
    entity.Id <- ObjectId "507f1f77bcf86cd799439011"
    let actual = CredentialEntityMappers.toStoredCredential entity

    // Assert - the constrained types survive the store's plain string columns
    match actual with
    | Ok stored ->
        Assert.Equal(
            CredentialSecret.value credential.Credentials,
            CredentialSecret.value stored.Credentials
        )

        Assert.Equal(
            ExternalUsername.value credential.ExternalUsername,
            ExternalUsername.value stored.ExternalUsername
        )

        Assert.Equal(credential.Infrastructure, stored.Infrastructure)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Fact; Trait("Level", "Contract")>]
let ``every Infrastructure case survives the bottom mapper both ways`` () =
    // Arrange - adding a case to the union without teaching the mapper about it fails here
    let allCases =
        Reflection.FSharpType.GetUnionCases(typeof<Infrastructure>)
        |> Array.map (fun case -> Reflection.FSharpValue.MakeUnion(case, [||]) :?> Infrastructure)

    for infrastructure in allCases do
        // Act
        let asEnum = CredentialEntityMappers.toInfrastructureType infrastructure
        let backToDomain = CredentialEntityMappers.fromInfrastructureType asEnum

        // Assert
        Assert.Equal(Ok infrastructure, backToDomain)

[<Fact; Trait("Level", "Contract")>]
let ``every InfrastructureType member has a domain equivalent`` () =
    // Arrange - the other direction: a member added to the C# enum only. The compiler cannot
    // catch this one, because an enum can hold any integer.
    let allMembers =
        Enum.GetValues(typeof<InfrastructureType>) |> Seq.cast<InfrastructureType>

    for infrastructureType in allMembers do
        // Act
        let actual = CredentialEntityMappers.fromInfrastructureType infrastructureType

        // Assert
        Assert.True(
            Result.isOk actual,
            $"InfrastructureType.{infrastructureType} has no case in the domain's Infrastructure union"
        )

[<Fact; Trait("Level", "Contract")>]
let ``fromInfrastructureType rejects a value no build ever declared`` () =
    // Arrange - what a row written by a future build looks like to this one
    let unknown = enum<InfrastructureType> 99

    // Act
    let actual = CredentialEntityMappers.fromInfrastructureType unknown

    // Assert
    match actual with
    | Error reason -> Assert.Equal("Stored InfrastructureType '99' has no domain equivalent.", reason)
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

// ---------- top mapper ----------

[<Fact; Trait("Level", "Contract")>]
let ``the username is renamed at the top boundary and nowhere else`` () =
    // Arrange - a deliberate rename, asserted as a rename: the UI's Username is the domain's and
    // the store's ExternalUsername
    let entered: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = "secret"
            Username = "renamed@gmail.com"
        }

    // Act
    let domainValue = CredentialApiMappers.toUnvalidatedCredential entered

    let entity =
        CredentialEntityMappers.toNewEntity
            {
                Infrastructure = domainValue.Infrastructure
                Credentials = CredentialSecret.create domainValue.Credentials |> valueOrFail
                ExternalUsername = ExternalUsername.create domainValue.ExternalUsername |> valueOrFail
            }

    // Assert - the same value, under the name each ring calls it
    Assert.Equal("renamed@gmail.com", domainValue.ExternalUsername)
    Assert.Equal("renamed@gmail.com", entity.ExternalUsername)

[<Fact; Trait("Level", "Contract")>]
let ``a credential crosses exactly the two mappers between the UI and the store`` () =
    // Arrange - UI record -> domain -> entity -> domain -> UI record. Two mappers, no hop
    // between them; if a third DTO appears, this test is where it shows up.
    let entered: IntegrationCredentialUiType =
        {
            Id = "507f1f77bcf86cd799439011"
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = """{ "refresh_token": "1//abcDEF" }"""
            Username = "person@outlook.com"
        }

    // Act
    let unvalidated = CredentialApiMappers.toUnvalidatedCredentialEdit entered

    let entity =
        CredentialEntityMappers.toNewEntity
            {
                Infrastructure = unvalidated.Infrastructure
                Credentials = CredentialSecret.create unvalidated.Credentials |> valueOrFail
                ExternalUsername = ExternalUsername.create unvalidated.ExternalUsername |> valueOrFail
            }

    entity.Id <- ObjectId entered.Id

    let backToDomain =
        match CredentialEntityMappers.toStoredCredential entity with
        | Ok stored -> stored
        | Error reason -> failwith reason

    let actual = CredentialApiMappers.toUiType backToDomain

    // Assert - every field arrives back unchanged
    Assert.Equal(entered.Id, actual.Id)
    Assert.Equal(entered.InfrastructureType, actual.InfrastructureType)
    Assert.Equal(entered.Credentials, actual.Credentials)
    Assert.Equal(entered.Username, actual.Username)

[<Fact; Trait("Level", "Contract")>]
let ``toUiType exposes the store's identifier as the UI's Id`` () =
    // Arrange
    let stored = storedCredential "507f1f77bcf86cd799439011" Google "secret" "person@gmail.com"

    // Act
    let actual = CredentialApiMappers.toUiType stored

    // Assert
    Assert.Equal("507f1f77bcf86cd799439011", actual.Id)
