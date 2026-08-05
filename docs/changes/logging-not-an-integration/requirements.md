# Requirements — Logging is not an integration

Logging is cross-cutting infrastructure, not a capability the application composes with others.
`CLAUDE-project.md` → *Logging is cross-cutting, not an integration* states the rule; the project
names still contradict it. This change makes the names agree with the rule.

## Naming

### Project identity
WHEN a developer looks for the logging component THE SYSTEM SHALL present it as `MyDogsbody.Logging`, with no `Integrations.` segment in the project name, directory name, assembly name or namespaces.
WHEN a developer looks for the logging entity classes THE SYSTEM SHALL present them as `MyDogsbody.Logging.Database.Models`.
WHEN a developer greps the solution for `Integrations.Logging` THE SYSTEM SHALL return no matches outside historical change documents.

### Reference graph
WHEN the solution is loaded THE SYSTEM SHALL show `MyDogsbody.Logging` referenced only by `MyDogsbody.Startup` and the scratch project `PdfProcessing`.
WHEN `MyDogsbody.Spine` is built THE SYSTEM SHALL NOT require the logging project, because logging is not something `Spine` composes.

## Unchanged behaviour (regression prevention)

This is a rename. Nothing observable changes.

WHEN `handleError` logs a failure THE SYSTEM SHALL CONTINUE TO write an `ExceptionLog` row to the `Exceptions` collection of `Logging.db`.
WHEN the inner exception is an `ApplicationException` THE SYSTEM SHALL CONTINUE TO pass it through unlogged.
WHEN the application starts THE SYSTEM SHALL CONTINUE TO open `Logging.db` at the same relative path, with the same `shared` connection mode.
WHEN an `ExceptionLog` is persisted THE SYSTEM SHALL CONTINUE TO store the same document field names — LiteDB is schemaless, so a namespace change must not reach the stored shape.
WHEN a caller invokes `ExceptionUseCases.addException` or `ExceptionRepository.insertOne` THE SYSTEM SHALL CONTINUE TO accept the same DTOs and return the same `Result`.
WHEN the existing test suite runs THE SYSTEM SHALL CONTINUE TO pass every test, with no test added, removed or weakened to accommodate the rename.

## Edge cases

WHEN stale `bin/` or `obj/` output from the old project name remains on disk THE SYSTEM SHALL still build clean — the old build artefacts are removed rather than left to be resolved by chance.
WHEN a log row written by a previous build is read back THE SYSTEM SHALL CONTINUE TO deserialise it, because the collection name and document fields are untouched.
