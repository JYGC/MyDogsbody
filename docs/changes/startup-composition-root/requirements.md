# Requirements — Startup composition root

Replace the `MyDogsbody.Compositions` / `MyDogsbody.Compositions.Interfaces` pair with a single
startup composition root, and stop discarding `Result` values on the way to the UI.

## Composition root

### Wiring
WHEN the application starts THE SYSTEM SHALL construct the Logging and Credentials database contexts, the shared `handleError` builder, and the credential API in one place.
WHEN the host registers services THE SYSTEM SHALL expose a single F# entry point that performs every registration, so no wiring is expressed in C#.
WHEN a caller needs the credential API THE SYSTEM SHALL supply it as a record of functions resolved from the service provider.

### Testability
WHEN a test constructs the credential API THE SYSTEM SHALL allow the database context and `handleError` to be supplied as parameters.
WHEN a test references the API factory or the mappers THE SYSTEM SHALL NOT open any database file as a side effect of module initialisation.

## UI boundary

### Types crossing the boundary
WHEN the UI calls the credential API THE SYSTEM SHALL accept and return `MyDogsbody.UI.Types` records only.
WHEN `MyDogsbody.UI.Portal` is compiled THE SYSTEM SHALL NOT require a reference to `MyDogsbody.Spine`, directly or transitively.
WHEN a credential crosses the UI boundary THE SYSTEM SHALL preserve `InfrastructureType`, `Credentials` and `Username`, and `Id` where the type carries one.

### Error handling
WHEN a credential write succeeds THE SYSTEM SHALL return `Ok ()`.
WHEN a credential write fails THE SYSTEM SHALL return `Error` carrying the originating `MyDogsbodyException`, and SHALL NOT discard it.
WHEN a credential read fails THE SYSTEM SHALL return `Error` carrying the originating `MyDogsbodyException`, and SHALL NOT raise an exception.
WHEN a credential operation returns `Error` THE SYSTEM SHALL display the error message on the credentials page.
WHEN a credential operation succeeds after a previous failure THE SYSTEM SHALL clear the displayed error.

## Credentials page

### Listing
WHEN the credentials page opens THE SYSTEM SHALL load the stored credentials and display them.
WHEN credentials are loading THE SYSTEM SHALL show the table's loading indicator.

### Adding and editing
WHEN a user confirms the add-credential dialog THE SYSTEM SHALL store the credential and reload the table so the new row is visible.
WHEN a user confirms the edit-credential dialog THE SYSTEM SHALL store the change and reload the table so the amended row is visible.
WHEN a user opens the edit dialog for a row THE SYSTEM SHALL pass that row's existing values, including its `Id`, to the dialog.
WHEN a user cancels either dialog THE SYSTEM SHALL leave the stored credentials unchanged.

## Removal

WHEN the change is complete THE SYSTEM SHALL contain no `MyDogsbody.Compositions` project, no `MyDogsbody.Compositions.Interfaces` project, and no reference to either.

## Edge cases

WHEN the credentials database contains no rows THE SYSTEM SHALL display an empty table rather than an error.
WHEN the credential API is constructed twice in one process THE SYSTEM SHALL reuse the same database context rather than opening a second handle to the same file.
WHEN `handleError` logs a failure THE SYSTEM SHALL continue to write an `ExceptionLog` row to `Logging.db`, except where the inner exception is an `ApplicationException`.
