namespace MyDogsbody.Infrastructure.Database.Types

open LiteDB
open MyDogsbody.Infrastructure.Database.Models

type InfrastructureDatabaseContext =
    {
        GetExceptionCollection: unit -> ILiteCollection<ExceptionLog>
    }
