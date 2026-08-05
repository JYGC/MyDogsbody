module MyDogsbody.Tests.Contracts.PersistedShapeTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials
open MyDogsbody.Integrations.Credentials.Database
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.Types
open MyDogsbody.Logging.UseCases

// LiteDB is schemaless, so renaming a property on Credential or ExceptionLog silently orphans
// every row already stored - the code keeps compiling and the old data simply stops being found.
// These assert the persisted document's field names, not just that an object round trips.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

/// A second handle on the same file, so the raw documents can be inspected as stored.
let private withStoreAndRawAccess (test: (unit -> Database.Types.CredentialsCollection) -> LiteDatabase -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = CredentialsDatabaseContextModule.getDatabaseContext databasePath "shared"
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")

    try
        test context.GetCredentialCollection rawDatabase
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

[<Fact; Trait("Level", "Contract")>]
let ``a credential is persisted under the documented field names`` () =
    withStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        {
            Infrastructure = Microsoft
            Credentials = CredentialSecret.create "persisted-secret" |> valueOrFail
            ExternalUsername = ExternalUsername.create "persisted@outlook.com" |> valueOrFail
        }
        |> CredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Assert
        let document = rawDatabase.GetCollection("Credentials").FindAll() |> Seq.exactlyOne

        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "InfrastructureType", "expected an InfrastructureType field")
        Assert.True(document.ContainsKey "Credentials", "expected a Credentials field")
        Assert.True(document.ContainsKey "ExternalUsername", "expected an ExternalUsername field")

        // The UI calls it Username; that name is renamed at the top boundary and must never
        // reach the store.
        Assert.False(document.ContainsKey "Username", "Username must not be persisted")

        Assert.Equal("persisted-secret", document.["Credentials"].AsString)
        Assert.Equal("persisted@outlook.com", document.["ExternalUsername"].AsString)
    )

[<Fact; Trait("Level", "Contract")>]
let ``the credentials collection is named Credentials`` () =
    withStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        {
            Infrastructure = Google
            Credentials = CredentialSecret.create "secret" |> valueOrFail
            ExternalUsername = ExternalUsername.create "person@gmail.com" |> valueOrFail
        }
        |> CredentialStore.insertOne handleError getCollection
        |> okOrFail "insertOne"
        |> ignore

        // Assert
        Assert.Contains("Credentials", rawDatabase.GetCollectionNames())
    )

[<Fact; Trait("Level", "Contract")>]
let ``every Infrastructure case round trips through the store`` () =
    withStoreAndRawAccess (fun getCollection _ ->
        // Arrange - guards against a case that persists in a form it cannot be read back from
        let allCases =
            Reflection.FSharpType.GetUnionCases(typeof<Infrastructure>)
            |> Array.map (fun case -> Reflection.FSharpValue.MakeUnion(case, [||]) :?> Infrastructure)

        // Act
        for infrastructure in allCases do
            {
                Infrastructure = infrastructure
                Credentials = CredentialSecret.create $"{infrastructure}-secret" |> valueOrFail
                ExternalUsername = ExternalUsername.create $"{infrastructure}@example.com" |> valueOrFail
            }
            |> CredentialStore.insertOne handleError getCollection
            |> okOrFail "insertOne"
            |> ignore

        // Assert
        let stored = CredentialStore.getAll handleError getCollection () |> okOrFail "getAll"
        Assert.Equal(allCases.Length, List.length stored)

        for infrastructure in allCases do
            let row = stored |> List.find (fun c -> c.Infrastructure = infrastructure)
            Assert.Equal($"{infrastructure}-secret", CredentialSecret.value row.Credentials)
            Assert.Equal($"{infrastructure}@example.com", ExternalUsername.value row.ExternalUsername)
    )

// ---------- the log store ----------

let private withLogStoreAndRawAccess
    (test: (unit -> MyDogsbody.Logging.Database.Types.ExceptionCollection) -> LiteDatabase -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = LoggingDatabaseContextModule.getDatabaseContext databasePath "shared"
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")

    try
        test context.GetExceptionCollection rawDatabase
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

[<Fact; Trait("Level", "Contract")>]
let ``an exception log entry is persisted under the documented field names`` () =
    withLogStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        {
            Message = "Failed to insert new credential."
            ActionName = ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.insertOne
            ExceptionDetails = "System.InvalidOperationException: disk gone"
            CreatedDate = DateTime(2026, 8, 5, 14, 30, 0)
        }
        |> ExceptionUseCases.addException handleError getCollection
        |> okOrFail "addException"

        // Assert
        let document = rawDatabase.GetCollection("Exceptions").FindAll() |> Seq.exactlyOne

        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "Message", "expected a Message field")
        Assert.True(document.ContainsKey "ActionName", "expected an ActionName field")
        Assert.True(document.ContainsKey "ExceptionDetails", "expected an ExceptionDetails field")
        Assert.True(document.ContainsKey "CreatedDate", "expected a CreatedDate field")

        Assert.Equal("Failed to insert new credential.", document.["Message"].AsString)
    )

[<Fact; Trait("Level", "Contract")>]
let ``a log entry carries no severity field, because the collection is the severity`` () =
    withLogStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        {
            Message = "a failure"
            ActionName = "an.action"
            ExceptionDetails = "details"
            CreatedDate = DateTime(2026, 8, 5)
        }
        |> ExceptionUseCases.addException handleError getCollection
        |> okOrFail "addException"

        // Assert - a discriminator as well as a collection would be two sources of truth
        let document = rawDatabase.GetCollection("Exceptions").FindAll() |> Seq.exactlyOne
        Assert.False(document.ContainsKey "Severity", "Severity must not be persisted")
        Assert.False(document.ContainsKey "Level", "Level must not be persisted")
        Assert.False(document.ContainsKey "LogType", "LogType must not be persisted")
    )

[<Fact; Trait("Level", "Contract")>]
let ``errors are written to the Exceptions collection and no other`` () =
    withLogStoreAndRawAccess (fun getCollection rawDatabase ->
        // Arrange / Act
        {
            Message = "a failure"
            ActionName = "an.action"
            ExceptionDetails = "details"
            CreatedDate = DateTime(2026, 8, 5)
        }
        |> ExceptionUseCases.addException handleError getCollection
        |> okOrFail "addException"

        // Assert - one collection per log type; errors live in Exceptions, which is the
        // established name and does not get renamed
        Assert.Equal<string list>([ "Exceptions" ], rawDatabase.GetCollectionNames() |> List.ofSeq)
    )
