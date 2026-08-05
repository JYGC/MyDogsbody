namespace MyDogsbody.Logging.UseCases.Types

open System

type ExceptionUseCaseTypeDto =
    {
        Message: string
        ActionName: string
        ExceptionDetails: string
        CreatedDate: DateTime
    }