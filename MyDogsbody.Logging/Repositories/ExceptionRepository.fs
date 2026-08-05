module MyDogsbody.Logging.Repositories.ExceptionRepository

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Database.Types
open MyDogsbody.Logging.Database.Models
open MyDogsbody.Logging.Types

/// The Exceptions collection of the log database. Errors only - each log type gets its own
/// collection, and the collection is what says which type a row is.
let insertOne
    (handleError: HandleErrorBuilder)
    (getExceptionCollection: unit -> ExceptionCollection)
    (entry: ExceptionLogEntry)
    : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Logging.ExceptionRepository.insertOne

    handleError {
        try
            ExceptionLog(
                Message = entry.Message,
                ActionName = entry.ActionName,
                ExceptionDetails = entry.ExceptionDetails,
                CreatedDate = entry.CreatedDate
            )
            |> getExceptionCollection().Insert
            |> ignore
        with ex ->
            return! MyDogsbodyException(action, "Failed to write an exception log entry.", ex)
    }

let getAll
    (handleError: HandleErrorBuilder)
    (getExceptionCollection: unit -> ExceptionCollection)
    ()
    : Result<ExceptionLogEntry list, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Logging.ExceptionRepository.getAll

    handleError {
        try
            return
                getExceptionCollection().FindAll()
                |> Seq.map (fun exceptionLog ->
                    {
                        Message = exceptionLog.Message
                        ActionName = exceptionLog.ActionName
                        ExceptionDetails = exceptionLog.ExceptionDetails
                        CreatedDate = exceptionLog.CreatedDate
                    }
                )
                |> Seq.toList
        with ex ->
            return! MyDogsbodyException(action, "Failed to read the exception log.", ex)
    }
