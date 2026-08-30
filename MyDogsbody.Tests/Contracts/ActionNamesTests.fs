module MyDogsbody.Tests.Contracts.ActionNamesTests

open System
open System.IO
open System.Reflection
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Documents
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database
open MyDogsbody.Integrations.Documents
open MyDogsbody.Logging.Repositories
open MyDogsbody.Logging.Types

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

let private failingGoogleCredentialCollection () : Database.Types.GoogleCredentialsCollection =
    raise (InvalidOperationException "store is gone")

let private failingExceptionCollection () : MyDogsbody.Logging.Database.Types.ExceptionCollection =
    raise (InvalidOperationException "log store is gone")

let private handleError = HandleErrorBuilder ignore

let private actionOf result =
    match result with
    | Error (ex: MyDogsbodyException) -> ex.ActionName
    | Ok _ -> failwith "Expected Error, but got Ok"

let private aValidGoogleCredential: ValidGoogleCredential =
    {
        Secret = GoogleCredentialSecret.create "secret" |> valueOrFail
        Username = GoogleExternalUsername.create "person@gmail.com" |> valueOrFail
    }

let private aValidGoogleEdit: ValidGoogleCredentialEdit =
    {
        Id = GoogleCredentialId.create "507f1f77bcf86cd799439011" |> valueOrFail
        Secret = GoogleCredentialSecret.create "secret" |> valueOrFail
        Username = GoogleExternalUsername.create "person@gmail.com" |> valueOrFail
    }

// ---------- each function reports its declared action ----------

[<Fact; Trait("Level", "Contract")>]
let ``GoogleCredentialStore.getAll reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll,
        GoogleCredentialStore.getAll handleError failingGoogleCredentialCollection () |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``GoogleCredentialStore.insertOne reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne,
        GoogleCredentialStore.insertOne handleError failingGoogleCredentialCollection aValidGoogleCredential |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``GoogleCredentialStore.updateOne reports its declared action`` () =
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.updateOne,
        GoogleCredentialStore.updateOne handleError failingGoogleCredentialCollection aValidGoogleEdit |> actionOf
    )

[<Fact; Trait("Level", "Contract")>]
let ``PdfDocumentReader.readContent reports its declared action`` () =
    // Arrange
    let missing = DocumentPath.create (Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")) |> valueOrFail

    // Assert
    Assert.Equal(
        ActionNames.MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent,
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

[<Fact; Trait("Level", "Contract")>]
let ``no action is still declared under a retired module`` () =
    // Change #4 renamed Integrations.Pdf -> Integrations.Documents; change #5 removed
    // Integrations.Credentials and Startup.CredentialApi entirely. The structural suite would not
    // notice a leftover entry under either, so it is asserted directly.
    for name, value in allDeclaredActions () do
        Assert.DoesNotContain(".Integrations.Pdf.", value)
        Assert.DoesNotContain(".Integrations.Credentials.", value)
        Assert.DoesNotContain(".CredentialApi.", value)
        Assert.False(name.Contains "+Pdf+", $"{name} is still nested under a Pdf module")
        Assert.False(name.Contains "+Credentials+", $"{name} is still nested under a Credentials module")

    let declared = allDeclaredActions () |> List.map snd
    Assert.Contains("MyDogsbody.Integrations.Documents.PdfDocumentReader.readContent", declared)
    Assert.Contains("MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll", declared)
