module MyDogsbody.Tests.Logging.ExceptionStoreTests

open System
open System.IO
open Xunit
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Database
open MyDogsbody.Logging.Repositories
open MyDogsbody.Logging.UseCases
open MyDogsbody.Logging.Types

// The log store is its own tier - not an integration, not the main database. These tests reach
// it against a fresh temp file per test; nothing here touches the Logging.db in the working
// directory.

let private handleError = HandleErrorBuilder (fun _ -> ())

let private withLogStore (test: (unit -> MyDogsbody.Logging.Database.Types.ExceptionCollection) -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = LoggingDatabaseContextModule.getDatabaseContext databasePath "direct"

    try
        test context.GetExceptionCollection
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private anEntry: ExceptionLogEntry =
    {
        Message = "Failed to insert new credential."
        ActionName = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne
        ExceptionDetails = "System.InvalidOperationException: disk gone"
        CreatedDate = DateTime(2026, 8, 5, 14, 30, 0)
    }

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

[<Fact; Trait("Level", "Integration")>]
let ``addException then getAllExceptions returns the entry with every field`` () =
    withLogStore (fun getCollection ->
        // Act
        ExceptionUseCases.addException handleError getCollection anEntry
        |> okOrFail "addException"

        let stored =
            ExceptionUseCases.getAllExceptions handleError getCollection ()
            |> okOrFail "getAllExceptions"

        // Assert
        let readBack = Assert.Single stored
        Assert.Equal(anEntry.Message, readBack.Message)
        Assert.Equal(anEntry.ActionName, readBack.ActionName)
        Assert.Equal(anEntry.ExceptionDetails, readBack.ExceptionDetails)
        Assert.Equal(anEntry.CreatedDate, readBack.CreatedDate)
    )

[<Fact; Trait("Level", "Integration")>]
let ``getAllExceptions returns an empty list for a fresh log database`` () =
    withLogStore (fun getCollection ->
        // Act
        let stored =
            ExceptionUseCases.getAllExceptions handleError getCollection ()
            |> okOrFail "getAllExceptions"

        // Assert
        Assert.Empty stored
    )

[<Fact; Trait("Level", "Integration")>]
let ``every logged exception is kept, including repeats of the same action`` () =
    withLogStore (fun getCollection ->
        // Arrange - the log is a record of what happened, so nothing is deduplicated
        for index in 1 .. 3 do
            ExceptionUseCases.addException handleError getCollection { anEntry with Message = $"failure {index}" }
            |> okOrFail "addException"

        // Act
        let stored =
            ExceptionUseCases.getAllExceptions handleError getCollection ()
            |> okOrFail "getAllExceptions"

        // Assert
        Assert.Equal(3, List.length stored)
    )

[<Fact; Trait("Level", "Integration")>]
let ``insertOne reports its declared action when the collection cannot be reached`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter () : MyDogsbody.Logging.Database.Types.ExceptionCollection =
        raise (InvalidOperationException "log database is gone")

    // Act
    let actual = ExceptionRepository.insertOne recordingHandleError failingGetter anEntry

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Logging.ExceptionRepository.insertOne, ex.ActionName)
        Assert.Equal("Failed to write an exception log entry.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
    | Ok () -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``getAll reports its declared action when the collection cannot be reached`` () =
    // Arrange
    let logged = ResizeArray<MyDogsbodyException>()
    let recordingHandleError = HandleErrorBuilder logged.Add

    let failingGetter () : MyDogsbody.Logging.Database.Types.ExceptionCollection =
        raise (InvalidOperationException "log database is gone")

    // Act
    let actual = ExceptionRepository.getAll recordingHandleError failingGetter ()

    // Assert
    match actual with
    | Error ex ->
        Assert.Equal(ActionNames.MyDogsbody.Logging.ExceptionRepository.getAll, ex.ActionName)
        Assert.Equal("Failed to read the exception log.", ex.Message)
        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``addException reports its declared action when the store fails`` () =
    // Arrange
    let recordingHandleError = HandleErrorBuilder ignore

    let failingGetter () : MyDogsbody.Logging.Database.Types.ExceptionCollection =
        raise (InvalidOperationException "log database is gone")

    // Act
    let actual = ExceptionUseCases.addException recordingHandleError failingGetter anEntry

    // Assert - the use case surfaces the repository's failure rather than swallowing it
    match actual with
    | Error ex -> Assert.Equal(ActionNames.MyDogsbody.Logging.ExceptionRepository.insertOne, ex.ActionName)
    | Ok () -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Integration")>]
let ``Dispose releases the log file so it can be deleted`` () =
    // Arrange
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = LoggingDatabaseContextModule.getDatabaseContext databasePath "direct"
    ExceptionUseCases.addException handleError context.GetExceptionCollection anEntry |> ignore

    // Act
    context.Dispose()

    // Assert - no try/with: the delete must actually succeed
    File.Delete databasePath
    Assert.False(File.Exists databasePath)
