module MyDogsbody.Logging.UseCases.ExceptionUseCases

open MyDogsbody.Logging.Database.Types
open MyDogsbody.Logging.Repositories.Types
open MyDogsbody.Logging.Repositories
open MyDogsbody.Logging.UseCases.Types

let addException
  (getExceptionCollection: unit -> ExceptionCollection)
  (exceptionUseCaseTypeDto: ExceptionUseCaseTypeDto)
  : unit =
    let exceptionRepositoryTypeDto: ExceptionRepositoryTypeDto =
        {
            Message = exceptionUseCaseTypeDto.Message
            ActionName = exceptionUseCaseTypeDto.ActionName
            ExceptionDetails = exceptionUseCaseTypeDto.ExceptionDetails
            CreatedDate = exceptionUseCaseTypeDto.CreatedDate
        }
    ExceptionRepository.insertOne
        getExceptionCollection
        exceptionRepositoryTypeDto

let getAllExceptions
    (getExceptionCollection: unit -> ExceptionCollection)
    : ExceptionUseCaseTypeDto list =
        let exceptionRepositoryTypeDtos =
            ExceptionRepository.getAll getExceptionCollection
        exceptionRepositoryTypeDtos
        |> List.map (fun exceptionRepositoryTypeDto ->
            {
                Message = exceptionRepositoryTypeDto.Message
                ActionName = exceptionRepositoryTypeDto.ActionName
                ExceptionDetails = exceptionRepositoryTypeDto.ExceptionDetails
                CreatedDate = exceptionRepositoryTypeDto.CreatedDate
            }
        )