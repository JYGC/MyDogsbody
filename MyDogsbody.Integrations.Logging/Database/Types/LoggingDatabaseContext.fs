namespace MyDogsbody.Integrations.Logging.Database.Types

open LiteDB
open MyDogsbody.Integrations.Logging.Database.Models

type ExceptionCollection = ILiteCollection<ExceptionLog>

type LoggingDatabaseContext =
    {
        GetExceptionCollection: unit -> ExceptionCollection
    }
