module MyDogsbody.Tests.Contracts.ActionNamesTests

open System
open System.IO
open System.Reflection
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Pdf
open MyDogsbody.Logging.Repositories
open MyDogsbody.Logging.Types
open MyDogsbody.Startup
open MyDogsbody.UI.Types

// Action strings are $"..."-composed and compiler-unchecked, so a typo is invisible until
// someone reads the exception log. Before this suite existed two entries were already wrong: one
// truncated so it did not name its own function, and one naming the opposite mapping.
//
// Two things are asserted: that each function reports the action it declares, and that every
// declared action is well formed and reachable.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private failingCredentialCollection () : Database.Types.CredentialsCollection =
    raise (InvalidOperationException "store is gone")

let private failingExceptionCollection () : MyDogsbody.Logging.Database.Types.ExceptionCollection =
    raise (InvalidOperationException "log store is gone")

let private handleError = HandleErrorBuilder ignore

let private actionOf result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex.ActionName
    | Ok _ -> failwith "Expected Error, but got Ok"

let private aValidCredential: ValidCredential =
    {
        Infrastructure = Google
        Credentials = CredentialSecret.create "secret" |> valueOrFail
        ExternalUsername = ExternalUsername.create "person@gmail.com" |> valueOrFail
    }

let private aValidEdit: ValidCredentialEdit =
    {
        Id = CredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail
        Infrastructure = Google
        Credentials = CredentialSecret.create "secret" |> valueOrFail
        ExternalUsername = ExternalUsername.create "person@gmail.com" |> valueOrFail
    }

// ---------- each function reports its declared action ----------

[<Fact; Trait("Level", "Contract")>]
let ``CredentialStore.getAll reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll,
        CredentialStore.getAll handleError failingCredentialCollection () |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``CredentialStore.insertOne reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.insertOne,
        CredentialStore.insertOne handleError failingCredentialCollection aValidCredential |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``CredentialStore.updateOne reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.updateOne,
        CredentialStore.updateOne handleError failingCredentialCollection aValidEdit |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``PdfDocumentReader.readContent reports its declared action`` () =
    // Arrange
    let missing = DocumentPath.create (Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")) |> valueOrFail

    // Assert
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Pdf.PdfDocumentReader.readContent,
        PdfDocumentReader.readContent handleError missing |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``ExceptionRepository.insertOne reports its declared action`` () =
    // Arrange
    let entry: ExceptionLogEntry =
        {
            Message = "m"
            ActionName = "a"
            ExceptionDetails = "d"
            CreatedDate = DateTime(2026, 8, 5)
        }

    // Assert
    Assert.Equal(
        ActionNames.MyDogsbody.Logging.ExceptionRepository.insertOne,
        ExceptionRepository.insertOne handleError failingExceptionCollection entry |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``ExceptionRepository.getAll reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Logging.ExceptionRepository.getAll,
        ExceptionRepository.getAll handleError failingExceptionCollection () |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``the credential api reports its declared action for each operation`` () =
    // Arrange - a store that cannot be reached, so every operation fails at the same place and
    // the action is the only thing distinguishing the three
    let api = CredentialApiFactory.createCredentialApi handleError failingCredentialCollection

    let aUiCredential: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = "secret"
            Username = "person@gmail.com"
        }

    // Assert
    Assert.Equal(
        ActionNames.MyDogsbody.Startup.CredentialApi.getAllCredentials,
        api.GetAllCredentials() |> actionOf
    )

    Assert.Equal(
        ActionNames.MyDogsbody.Startup.CredentialApi.addCredential,
        api.AddCredential aUiCredential |> actionOf
    )

    Assert.Equal(
        ActionNames.MyDogsbody.Startup.CredentialApi.editCredential,
        api.EditCredential
            {
                Id = "507f1f77bcf86cd799439011"
                InfrastructureType = InfrastructureType.Google
                Credentials = "secret"
                Username = "person@gmail.com"
            }
        |> actionOf
    )

// ---------- every declared action is well formed ----------

/// Walks the nested modules of ActionNames by reflection, so the checks below cannot fall behind
/// the file the way a hand-written list would.
let private allDeclaredActions () =
    let rec walk (declaringType: Type) =
        [
            for property in declaringType.GetProperties(BindingFlags.Public ||| BindingFlags.Static) do
                if property.PropertyType = typeof<string> then
                    yield $"{declaringType.FullName}.{property.Name}", string (property.GetValue null)

            for nested in declaringType.GetNestedTypes(BindingFlags.Public) do
                yield! walk nested
        ]

    walk (typeof<MyDogsbodyException>.Assembly.GetType "MyDogsbody.Exceptions.Types.ActionNames+MyDogsbody")

[<Fact; Trait("Level", "Contract")>]
let ``every declared action is non-empty and rooted at MyDogsbody`` () =
    // Arrange
    let actions = allDeclaredActions ()

    // Assert - the walk found something, so a silent zero-match is not mistaken for success
    Assert.NotEmpty actions

    for name, value in actions do
        Assert.False(String.IsNullOrWhiteSpace value, $"{name} is empty")
        Assert.StartsWith("MyDogsbody.", value)
        Assert.DoesNotContain("..", value)
        Assert.False(value.EndsWith ".", $"{name} ends with a separator")

[<Fact; Trait("Level", "Contract")>]
let ``every declared action ends with the name of the binding that declares it`` () =
    // Arrange - this is the assertion that would have caught the truncated entry: it was bound
    // to mapAddCredentialUseCaseTypeDtoToAddCredentialDomainTypeDto but composed the string
    // "...mapAddCredentialUseCaseTypeDtoToAddCredentialDomain".
    let actions = allDeclaredActions ()

    // Assert
    for name, value in actions do
        let bindingName = name.Substring(name.LastIndexOf '.' + 1)
        let lastSegment = value.Substring(value.LastIndexOf '.' + 1)

        let message =
            sprintf
                "%s declares the action '%s', whose last segment '%s' does not match the binding name '%s'"
                name
                value
                lastSegment
                bindingName

        // Parenthesised so F# reads this as a comparison rather than a named argument.
        Assert.True((lastSegment = bindingName), message)

[<Fact; Trait("Level", "Contract")>]
let ``no two bindings declare the same action`` () =
    // Arrange - a copy-paste that leaves two functions reporting the same action makes the log
    // ambiguous about where a failure came from. This is what would have caught the entry that
    // named the opposite mapping.
    let actions = allDeclaredActions ()

    // Act
    let duplicates =
        actions
        |> List.groupBy snd
        |> List.filter (fun (_, group) -> List.length group > 1)

    // Assert
    let describe (value: string, group: (string * string) list) =
        let bindings = String.Join(", ", group |> List.map fst)
        sprintf "%s <- %s" value bindings

    let message =
        "these actions are declared more than once: "
        + String.Join("; ", duplicates |> List.map describe)

    Assert.True(List.isEmpty duplicates, message)
