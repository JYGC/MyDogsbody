namespace MyDogsbody.Logging.Repositories.Types

open System

type ExceptionRepositoryTypeDto =
    {
        Message: string
        ActionName: string
        ExceptionDetails: string
        CreatedDate: DateTime
    }