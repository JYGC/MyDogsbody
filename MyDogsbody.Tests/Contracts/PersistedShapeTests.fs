module MyDogsbody.Tests.Contracts.PersistedShapeTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.Types
open MyDogsbody.Logging.UseCases

// LiteDB is schemaless, so renaming a property on ExceptionLog silently orphans every row
// already stored - the code keeps compiling and the old data simply stops being found. These
// assert the persisted document's field names, not just that an object round trips.
//
// (The credential half of this file left with the retired shared credentials integration in
// change #5; GoogleCredential's persisted shape is asserted in
// Contracts/GoogleCredentialPersistedShapeTests.fs.)

let private handleError = HandleErrorBuilder (fun _ -> ())

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

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
            ActionName = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne
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
