namespace MyDogsbody.Logging.Types

open LiteDB
open MyDogsbody.Exceptions.Types

type LoggingDatabaseContext =
    {
        GetExceptionCollection: unit -> ILiteCollection<MyDogsbodyException>
    }
