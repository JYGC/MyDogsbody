namespace MyDogsbody.Logging.Database.Types

open LiteDB
open MyDogsbody.Logging.Database.Models

type ExceptionCollection = ILiteCollection<ExceptionLog>

type LoggingDatabaseContext =
    {
        GetExceptionCollection: unit -> ExceptionCollection
    }
