module MyDogsbody.Infrastructure.Database.Repositories.ExceptionRepository

open System
open MyDogsbody.Exceptions.Types
open MyDogsbody.Infrastructure.Database.Models
open LiteDB

let insertOne
  (getExceptionCollection: unit -> ILiteCollection<ExceptionLog>)
  (dex: MyDogsbodyException)
  : unit =
    let exceptionLog = ExceptionLog()
    exceptionLog.Message <- dex.Message
    exceptionLog.ActionName <- dex.ActionName
    exceptionLog.CreatedDate <- DateTime.UtcNow
    exceptionLog.ExceptionDetails <- dex.ToString()
    getExceptionCollection().Insert(exceptionLog)
    |> ignore

let getMany
  (getExceptionCollection: unit -> ILiteCollection<ExceptionLog>)
  (startDateTime: DateTime)
  (endDateTime: DateTime)
  : ExceptionLog list =
    getExceptionCollection()
        .Query()
        .Where(fun i ->
            i.CreatedDate > startDateTime &&
            i.CreatedDate < endDateTime
        )
        .ToEnumerable()
    |> Seq.toList