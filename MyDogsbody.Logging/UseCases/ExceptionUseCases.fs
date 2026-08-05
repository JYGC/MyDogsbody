module MyDogsbody.Logging.UseCases.ExceptionUseCases

open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Logging.Database.Types
open MyDogsbody.Logging.Repositories
open MyDogsbody.Logging.Types

/// What the composition root partially applies into handleError's writeLog.
///
/// Returns Result like every other outer-ring function. Startup.fs is the one caller that
/// discards it, and it has to: a failed log write cannot itself be logged without recursing, and
/// it must not displace the failure being recorded. Surfacing the Result is still what makes
/// these functions testable at all.
let addException
    (handleError: HandleErrorBuilder)
    (getExceptionCollection: unit -> ExceptionCollection)
    (entry: ExceptionLogEntry)
    : Result<unit, MyDogsbodyException> =
    ExceptionRepository.insertOne handleError getExceptionCollection entry

let getAllExceptions
    (handleError: HandleErrorBuilder)
    (getExceptionCollection: unit -> ExceptionCollection)
    ()
    : Result<ExceptionLogEntry list, MyDogsbodyException> =
    ExceptionRepository.getAll handleError getExceptionCollection ()
