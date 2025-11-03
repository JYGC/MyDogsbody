module MyDogsbody.Logging.Repositories.ExceptionsRepository

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Models
open MyDogsbody.Logging.Types

let insertLog
  (databaseContext: LoggingDatabaseContext)
  (ex: MyDogsbodyException)
  : unit =
    let exceptionLog = ExceptionLog()
    exceptionLog.Message <- ex.Message
    exceptionLog.ActionName <- ex.ActionName
    exceptionLog.CreatedDate <- DateTime.UtcNow
    exceptionLog.ExceptionDetails <- ex
    databaseContext.GetExceptionCollection().Insert(exceptionLog)
    |> ignore

let getLogs
  (databaseContext: LoggingDatabaseContext)
  (startDateTime: DateTime)
  (endDateTime: DateTime)
  : ExceptionLog list =
    databaseContext.GetExceptionCollection()
        .Query().Where(fun i ->
            i.CreatedDate > startDateTime &&
            i.CreatedDate < endDateTime
        ).ToEnumerable()
    |> Seq.toList