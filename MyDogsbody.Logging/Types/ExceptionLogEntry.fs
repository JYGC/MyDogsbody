namespace MyDogsbody.Logging.Types

open System

/// One exception, as the log records it.
///
/// A single type for the whole component, not one per layer: the repository and the use case had
/// identical records a hop apart, and copying between them proved nothing. Logging is not a
/// workflow area, so it has no domain type for these to sit either side of.
///
/// No Severity, Level or LogType field, deliberately: the collection a row lives in is what says
/// what it is. A discriminator as well would be two sources of truth for one fact.
type ExceptionLogEntry =
    {
        Message: string
        ActionName: string
        ExceptionDetails: string
        CreatedDate: DateTime
    }
