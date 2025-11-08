module MyDogsbody.Infrastructure.Database.Repositories.ExceptionsRepository

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Infrastructure.Database.Models
open MyDogsbody.Infrastructure.Database.Types

let insertLog
  (databaseContext: InfrastructureDatabaseContext)
  (ex: MyDogsbodyException)
  : unit =
    let exceptionLog = ExceptionLog()
    exceptionLog.Message <- ex.Message
    exceptionLog.ActionName <- ex.ActionName
    exceptionLog.CreatedDate <- DateTime.UtcNow
    exceptionLog.ExceptionDetails <- ex.ToString()
    databaseContext.GetExceptionCollection().Insert(exceptionLog)
    |> ignore

let getLogs
  (databaseContext: InfrastructureDatabaseContext)
  (startDateTime: DateTime)
  (endDateTime: DateTime)
  : ExceptionLog list =
    databaseContext.GetExceptionCollection()
        .Query().Where(fun i ->
            i.CreatedDate > startDateTime &&
            i.CreatedDate < endDateTime
        ).ToEnumerable()
    |> Seq.toList