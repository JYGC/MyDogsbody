namespace MyDogsbody.Integrations.Logging.Repositories.Types

open System

type ExceptionRepositoryTypeDto =
    {
        Message: string
        ActionName: string
        ExceptionDetails: string
        CreatedDate: DateTime
    }