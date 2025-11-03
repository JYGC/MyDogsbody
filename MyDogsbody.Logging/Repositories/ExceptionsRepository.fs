module MyDogsbody.Logging.Repositories.ExceptionsRepository

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Types

let insertLog
  (databaseContext: LoggingDatabaseContext)
  (ex: MyDogsbodyException)
  : unit =
    databaseContext.GetExceptionCollection().Insert(ex)
    |> ignore

let getLogs
  (databaseContext: LoggingDatabaseContext)
  (startDateTime: DateTime)
  (endDateTime: DateTime)
  : MyDogsbodyException list =
    databaseContext.GetExceptionCollection()
        .Query().Where(fun i ->
            i.CreatedDate > startDateTime &&
            i.CreatedDate < endDateTime
        ).ToEnumerable()
    |> Seq.toList