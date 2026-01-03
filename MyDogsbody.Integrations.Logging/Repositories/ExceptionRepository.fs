module MyDogsbody.Integrations.Logging.Repositories.ExceptionRepository

open MyDogsbody.Integrations.Logging.Database.Types
open MyDogsbody.Integrations.Logging.Repositories.Types
open MyDogsbody.Integrations.Logging.Database.Models

let insertOne
  (getExceptionCollection: unit -> ExceptionCollection)
  (exceptionRepositoryTypeDto: ExceptionRepositoryTypeDto)
  : unit =
    let exceptionCollection = getExceptionCollection()
    exceptionCollection.Insert(
        ExceptionLog(
            Message = exceptionRepositoryTypeDto.Message,
            ActionName = exceptionRepositoryTypeDto.ActionName,
            ExceptionDetails = exceptionRepositoryTypeDto.ExceptionDetails,
            CreatedDate = exceptionRepositoryTypeDto.CreatedDate
        )
    ) |> ignore

let getAll
  (getExceptionCollection: unit -> ExceptionCollection)
  : ExceptionRepositoryTypeDto list =
    let exceptionCollection = getExceptionCollection()
    exceptionCollection.FindAll()
    |> Seq.map (fun exceptionLog ->
        {
            Message = exceptionLog.Message
            ActionName = exceptionLog.ActionName
            ExceptionDetails = exceptionLog.ExceptionDetails
            CreatedDate = exceptionLog.CreatedDate
        }
    )
    |> Seq.toList