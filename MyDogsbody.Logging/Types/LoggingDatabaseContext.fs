namespace MyDogsbody.Logging.Types

open LiteDB
open MyDogsbody.Logging.Models

type LoggingDatabaseContext =
    {
        GetExceptionCollection: unit -> ILiteCollection<ExceptionLog>
    }
