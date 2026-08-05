# Requirements — Architecture compliance

Bring the repository into compliance with `CLAUDE-project.md` → *Architecture*: create
`MyDogsbody.Domain`, migrate the credentials and documents paths onto workflow pipelines, retire
`MyDogsbody.Spine`, and close the testing mandate to all four levels.

Baseline at the time of writing: `dotnet build MyDogsbody.sln` succeeds, `dotnet test` is green
(28 tests — 18 Unit, 7 Integration, 3 Contract).

## Domain project

### Existence and isolation
WHEN the solution is built THE SYSTEM SHALL contain a `MyDogsbody.Domain` project targeting `net9.0`.
WHEN `MyDogsbody.Domain.fsproj` is inspected THE SYSTEM SHALL contain no `ProjectReference` and no `PackageReference` element.
WHEN any file in `MyDogsbody.Domain` is compiled THE SYSTEM SHALL NOT name `MyDogsbodyException`, `HandleErrorBuilder`, `ILiteCollection`, `LiteDatabase`, `ObjectId`, `BsonValue`, `QuerySource`, or `InfrastructureType`.
WHEN a domain file needs a capability it cannot reach THE SYSTEM SHALL express that capability as a dependency function type rather than a project reference.

### Result builder
WHEN a domain workflow composes fallible steps THE SYSTEM SHALL provide a generic `result` computation expression in `MyDogsbody.Domain/Result.fs`, compiled first.
WHEN `result` binds an `Ok` value THE SYSTEM SHALL pass the unwrapped value to the continuation.
WHEN `result` binds an `Error` value THE SYSTEM SHALL short-circuit and return that `Error` unchanged, without evaluating the continuation.
WHEN `result` is used with any error type THE SYSTEM SHALL compile, being generic in its error type.
WHEN `result` is written THE SYSTEM SHALL NOT include a `TryWith` member, because the domain never catches exceptions.

### Folder shape
WHEN domain code is added THE SYSTEM SHALL place it in one folder per workflow area, holding `<Area>Types.fs` first and one `<Workflow>Workflow.fs` per workflow.
WHEN a workflow file is added THE SYSTEM SHALL expose exactly one public function from it.
WHEN domain code is added THE SYSTEM SHALL NOT create `Domains/Types`, `UseCases/Types` or `Repositories/Types` folders.

## Credentials workflow area

### Types
WHEN a credential secret is constructed THE SYSTEM SHALL require it to pass through a `create` function returning `Result`, and SHALL make its constructor private.
WHEN an external username is constructed THE SYSTEM SHALL require it to pass through a `create` function returning `Result`, and SHALL make its constructor private.
WHEN a credential identifier is constructed THE SYSTEM SHALL require it to pass through a `create` function returning `Result`, and SHALL make its constructor private.
WHEN the domain names the infrastructure a credential belongs to THE SYSTEM SHALL use its own discriminated union, declared in `MyDogsbody.Domain`, and SHALL NOT use the `MyDogsbody.Enums.InfrastructureType` enum.
WHEN a credential is at a different stage of the pipeline THE SYSTEM SHALL represent it with a distinct type — unvalidated, valid, and stored are three types, not one record with optional fields.
WHEN a stored credential is represented THE SYSTEM SHALL carry constrained types plus its identifier, and SHALL NOT carry raw `string` fields for the secret or username.

### Validation
WHEN a user submits a credential whose secret is empty or whitespace THE SYSTEM SHALL reject it with a domain error carrying the reason, and SHALL NOT reach the store.
WHEN a user submits a credential whose external username is empty or whitespace THE SYSTEM SHALL reject it with a domain error carrying the reason, and SHALL NOT reach the store.
WHEN an edit is submitted with an empty or whitespace identifier THE SYSTEM SHALL reject it with a domain error carrying the reason, and SHALL NOT reach the store.
WHEN a credential passes validation THE SYSTEM SHALL NOT re-check those rules anywhere downstream — holding the constrained type is the proof.

### Errors
WHEN a credentials workflow fails THE SYSTEM SHALL return `Error` carrying a case of a `CredentialError` discriminated union declared in the credentials area.
WHEN a `CredentialError` case is returned THE SYSTEM SHALL carry the values the user-facing message is written from.
WHEN the credentials store raises an exception THE SYSTEM SHALL surface it to the workflow as a `CredentialError` case, not as an exception.
WHEN a failure is a bug, a violated invariant or infrastructure collapse THE SYSTEM SHALL leave it as an exception caught at the outer-ring boundary, and SHALL NOT add a `CredentialError` case for it.

### Dependencies
WHEN a credentials workflow needs to read or write stored credentials THE SYSTEM SHALL declare that need as a function type in the credentials area and receive a function value as a leading parameter.
WHEN a credentials workflow is written THE SYSTEM SHALL take its dependencies as leading parameters and its input last, and SHALL return `Result`.
WHEN a credentials workflow is unit tested THE SYSTEM SHALL be exercisable with lambdas alone — no mocking framework, no temp file, no `handleError`.

### Add credential
WHEN a user submits a valid new credential THE SYSTEM SHALL save it and return the stored credential including the identifier the store assigned.
WHEN a user submits a new credential that fails validation THE SYSTEM SHALL return the corresponding `CredentialError` and SHALL NOT invoke the save dependency.

### Edit credential
WHEN a user submits a valid edit for a credential that exists THE SYSTEM SHALL update that credential and return the stored result.
WHEN a user submits an edit whose identifier matches no stored credential THE SYSTEM SHALL return a not-found `CredentialError` carrying that identifier, and SHALL NOT report success.
WHEN a user submits an edit that fails validation THE SYSTEM SHALL return the corresponding `CredentialError` and SHALL NOT invoke the update dependency.
WHEN a credential is updated THE SYSTEM SHALL identify the target row by its identifier, and SHALL NOT identify it by its infrastructure type.
WHEN two stored credentials share an infrastructure type and one is edited THE SYSTEM SHALL update only the credential whose identifier was submitted.

### List credentials
WHEN the stored credentials are requested THE SYSTEM SHALL return every stored credential with every field populated from the store.
WHEN the credentials store is empty THE SYSTEM SHALL return an empty list rather than an error.
WHEN the credentials store fails during a read THE SYSTEM SHALL return the corresponding `CredentialError`.

## Documents workflow area

### Types and dependencies
WHEN a document path is constructed THE SYSTEM SHALL require it to pass through a `create` function that rejects an empty or whitespace path.
WHEN a documents workflow needs a document's content THE SYSTEM SHALL declare that need as a function type and receive a function value as a leading parameter.
WHEN a documents workflow fails THE SYSTEM SHALL return `Error` carrying a case of a `DocumentError` discriminated union.

### Reading lines
WHEN a readable document is supplied THE SYSTEM SHALL return its words grouped into lines, each line ordered left to right.
WHEN a readable document is supplied THE SYSTEM SHALL return its lines ordered from the top of the page downwards.
WHEN two words sit within the line tolerance of each other THE SYSTEM SHALL place them on the same line.
WHEN a document contains no words THE SYSTEM SHALL return an empty list.
WHEN the supplied path fails validation THE SYSTEM SHALL return the corresponding `DocumentError` and SHALL NOT invoke the read dependency.
WHEN the read dependency fails THE SYSTEM SHALL return the corresponding `DocumentError`.
WHEN line grouping is performed THE SYSTEM SHALL do so in `MyDogsbody.Domain`, with no `handleError` and no `ActionName`.

## Outer ring

### Adapters
WHEN an integration is compiled THE SYSTEM SHALL reference `MyDogsbody.Domain` and implement the function types it declares.
WHEN an integration is compiled THE SYSTEM SHALL NOT reference another integration, `MyDogsbody.Startup`, or the UI.
WHEN an adapter is written THE SYSTEM SHALL take its dependencies as leading parameters and its input last, and SHALL return `Result<'T, MyDogsbodyException>` written with `handleError`.
WHEN new adapter code is added to an integration THE SYSTEM SHALL place it beside `Database/`, named for what it talks to.
WHEN an adapter converts a store row THE SYSTEM SHALL map the persistence entity to a domain type before returning it.

### Credentials store
WHEN a credential is inserted THE SYSTEM SHALL persist it and return the stored credential carrying the identifier the store assigned.
WHEN a credential is updated by identifier and the row exists THE SYSTEM SHALL persist the new values and report that the row was found.
WHEN a credential is updated by identifier and no row matches THE SYSTEM SHALL report that no row was found, and SHALL NOT report success.
WHEN all credentials are read THE SYSTEM SHALL surface each row's `ObjectId` as the domain identifier.

### Document reader
WHEN a document is read and the file does not exist THE SYSTEM SHALL return a `MyDogsbodyException` wrapping an `ApplicationException`, and SHALL NOT write a log entry.
WHEN a document is read and the file is not a readable document THE SYSTEM SHALL return a `MyDogsbodyException` and SHALL write a log entry.
WHEN a readable document is supplied THE SYSTEM SHALL return every word with its text and its bottom and left coordinates.

### Action names
WHEN an outer-ring function reports a failure THE SYSTEM SHALL use an action string from `MyDogsbody.Exceptions.Types/ActionNames.fs`, and SHALL NOT inline a literal.
WHEN an action string is declared THE SYSTEM SHALL compose it from nested modules mirroring the real code path of the function that uses it.
WHEN an action string is declared THE SYSTEM SHALL match the name of the function that reports it.
WHEN the change is complete THE SYSTEM SHALL contain no action string that no function uses.
WHEN a domain workflow fails THE SYSTEM SHALL NOT carry an action string, its error being a discriminated union case.

## Composition root

### Wiring
WHEN the composition root binds a workflow THE SYSTEM SHALL supply an adapter function for each dependency function type the workflow declares.
WHEN an adapter returns `Error` THE SYSTEM SHALL translate that `MyDogsbodyException` into a domain error case before the workflow sees it.
WHEN a workflow returns `Error` THE SYSTEM SHALL translate that domain error case into a `MyDogsbodyException` before the UI sees it.
WHEN the two error types are translated THE SYSTEM SHALL do so only in the API factory.
WHEN a translated domain error reaches the UI THE SYSTEM SHALL carry an action string naming the API operation that failed.

### Testability
WHEN a test references the API mappers or the API factory THE SYSTEM SHALL NOT open any database file as a side effect of module initialisation.
WHEN a test constructs the credential API THE SYSTEM SHALL allow `handleError` and the collection getter to be supplied as parameters.
WHEN process-lifetime resources are owned THE SYSTEM SHALL confine them to `Startup.fs`.

### Result handling
WHEN a credential operation succeeds THE SYSTEM SHALL return `Ok` carrying the operation's result.
WHEN a credential operation fails THE SYSTEM SHALL return `Error` carrying the originating `MyDogsbodyException`, and SHALL NOT discard it or raise it.

## Mapping points

WHEN a credential travels between the store and the UI THE SYSTEM SHALL cross exactly two mappers: entity ⇄ domain type in the integration, and domain type ⇄ UI record in `Startup`.
WHEN the change is complete THE SYSTEM SHALL contain no intermediate credential DTO between those two mappers.
WHEN a credential crosses the top mapper THE SYSTEM SHALL preserve the infrastructure, secret and username values, and the identifier where the type carries one.
WHEN a credential crosses the bottom mapper THE SYSTEM SHALL persist the username under the field name `ExternalUsername`, not the UI's `Username`.
WHEN `MyDogsbody.UI.Portal` is compiled THE SYSTEM SHALL NOT require a reference to `MyDogsbody.Domain`, directly or transitively.

## Logging

WHEN a logging repository or use-case function completes THE SYSTEM SHALL return `Result`.
WHEN `writeLog` is invoked and the log write fails THE SYSTEM SHALL discard that failure rather than attempt to log it, and SHALL NOT propagate it to the caller whose failure was being recorded.
WHEN a log entry is written THE SYSTEM SHALL write it to the logging component's own LiteDB database and nowhere else.
WHEN a domain validation failure is translated for the UI THE SYSTEM SHALL NOT write a log entry.
WHEN an adapter failure is translated for the UI THE SYSTEM SHALL have written exactly one log entry, at the outer-ring boundary.
WHEN a log entity is declared THE SYSTEM SHALL NOT carry a severity, level or log-type field.

## Removal

WHEN the change is complete THE SYSTEM SHALL contain no `MyDogsbody.Spine` project, and no reference to it from any project.
WHEN the change is complete THE SYSTEM SHALL contain no `Repositories/` or `UseCases/` folder inside `MyDogsbody.Integrations.Credentials`, and no `Domains/` or `UseCases/` folder inside `MyDogsbody.Integrations.Pdf`.
WHEN a legacy hop is deleted THE SYSTEM SHALL delete that hop's contract test in the same change, and SHALL have moved the assertions it carried onto the replacement boundary.
WHEN the change is complete THE SYSTEM SHALL contain no identifier spelled `Domian`.
WHEN the change is complete THE SYSTEM SHALL build every project in `MyDogsbody.sln`, including the scratch projects that consumed the deleted code.

## Testing

### Levels
WHEN a test is added THE SYSTEM SHALL tag it with its level via `Trait("Level", ...)`.
WHEN a domain workflow is added or changed THE SYSTEM SHALL provide unit tests asserting every output field on the success path, and the exact error case and payload on each failure path.
WHEN a rule short-circuits a workflow THE SYSTEM SHALL provide a unit test proving the downstream dependency was never invoked.
WHEN a constrained type is added THE SYSTEM SHALL provide a unit test per rule: one accepted value, one rejected value, and the rejection reason asserted.
WHEN an outer-ring function is added or changed THE SYSTEM SHALL provide unit tests asserting the declared action string, the message, and a preserved inner exception on the error path.
WHEN a function bottoms out at a real store THE SYSTEM SHALL provide integration tests against a fresh disposable database instance per test, with no state shared between tests.
WHEN a migration exists THE SYSTEM SHALL provide integration tests proving `MigrateUp` produces the expected tables and columns and that `Down` reverses it.
WHEN a published interface exists — a dependency function type, an API record, a boundary mapper, a persisted shape or an error translation — THE SYSTEM SHALL provide a contract test, and SHALL run one shared suite against the real implementation and against every fake standing in for it.
WHEN a user-visible flow exists THE SYSTEM SHALL provide an end-to-end test driving it through a rendered component down to a real database and back into what the component renders.

### Harness
WHEN an end-to-end test renders a component THE SYSTEM SHALL provide a bUnit harness supplying the MudBlazor test services those components require.
WHEN an end-to-end test asserts that a failure was logged THE SYSTEM SHALL assert it through a recording `handleError` callback, and SHALL NOT open the production log database.
WHEN any test runs THE SYSTEM SHALL NOT reach `Startup.Startup`, and SHALL NOT create `Logging.db` or `Credentials.db` in the working directory.

### Gate
WHEN the change is complete THE SYSTEM SHALL build with zero errors and run the full test suite with zero failures and zero skips.

## Documentation

WHEN the change is complete THE SYSTEM SHALL update `CLAUDE-project.md` so its project table, reference-direction rules, *Status — specified vs built* table, build state, testing guidance and naming-quirks section describe the repository as it then stands.

## Edge cases

WHEN a credential secret contains JSON, newlines or non-ASCII characters THE SYSTEM SHALL store and return it unchanged.
WHEN every member of the infrastructure enumeration is stored THE SYSTEM SHALL read each one back as the same member.
WHEN a member is added to the UI's infrastructure enumeration and not to the domain's THE SYSTEM SHALL fail to compile rather than fail at runtime.
WHEN the credentials database file does not yet exist THE SYSTEM SHALL create it on first use rather than fail.
WHEN a workflow's dependency returns an empty list THE SYSTEM SHALL treat that as a valid result, not an error.
WHEN a user cancels a credentials dialog THE SYSTEM SHALL leave the stored credentials unchanged.
WHEN a credential operation succeeds after a previous failure THE SYSTEM SHALL clear the displayed error.

## Decisions taken

These were settled before the specs were written; they are recorded so the reasoning is not lost.

| Decision | Chosen | Rejected |
| --- | --- | --- |
| Packaging | One change folder, `tasks.md` staged into phases that each end build-green | One folder per migration step, as `CLAUDE-project.md` → *Status* suggests. Rejected because the phases share one requirements set and splitting them would duplicate it five times |
| Validation | Reject empty/whitespace secret, username and identifier | Constrained types with no rejecting rule. Rejected because a validation layer that rejects nothing leaves the error DU with nothing real to carry |
| `updateOne` identity | Match on identifier; return not-found when no row matches | Preserving the match-on-infrastructure-type behaviour. Rejected because the migration rewrites the function anyway |
| Test scope | All four levels, including building the bUnit harness | Declaring E2E a known gap as the previous change did. Rejected because the harness is the one level the mandate has never met |

## Explicitly out of scope

- **Duplicate-credential rules.** Nothing today prevents two credentials sharing an infrastructure type, and `CredentialHopChainTests` depends on storing one per type. Fixing `updateOne` to match on identifier makes many-per-type coherent, so no uniqueness rule is added.
- **Warning, information and debug log collections.** `CLAUDE-project.md` records these as not implemented; that is a status, not a violation.
- **Wiring the main SQLite database into the application.** Also recorded as a status. Its migrations are tested here because the testing mandate asks for that independently of wiring.
- **Collapsing the logging project's internal DTO hop.** Logging is not a workflow area and the two-mapper rule does not reach it. Raised as an optional task.
- **Driving the real WPF `BlazorWebView` window.**
