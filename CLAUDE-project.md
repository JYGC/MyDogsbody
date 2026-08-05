# CLAUDE-project.md

Project reference for MyDogsbody: what it is, how it is laid out, how to build / run / test it, and how it fits together. The working rules — the spec-driven change process and the testing mandate — are in [CLAUDE.md](CLAUDE.md).

## What this is

A Windows desktop app: a WPF shell (`MyDogsbody`) hosting a `BlazorWebView`, whose entire UI is written in F# with Fun.Blazor + MudBlazor. .NET 9, F#-first — the handful of C# projects exist only for LiteDB entity classes and a shared enum.

**The architecture is the functional onion of *Domain Modeling Made Functional*.** A pure domain project at the centre that references nothing, dependencies declared as function types, workflows written as pipelines, and all I/O in an outer ring wired up at the composition root. See [Architecture](#architecture).

**The UI is the exception, deliberately** — it stays on Fun.Blazor + FSharp.Data.Adaptive rather than adopting MVU. See [UI](#ui-funblazor--mudblazor--fsharpdataadaptive).

Storage is split on purpose: **SQLite is the main database**, every integration owns a *separate* store of its own (LiteDB today, not necessarily tomorrow), and **all logging goes to its own separate LiteDB database with one collection per log type** — errors, warnings, information, everything. See [Storage](#storage).

## Project structure

One solution, `MyDogsbody.sln`. Every project targets `net9.0` except the WPF host (`net9.0-windows`, `WinExe`, `Microsoft.NET.Sdk.Razor`, `UseWPF`).

| Ring | Project | Holds |
| --- | --- | --- |
| **Centre** | `MyDogsbody.Domain` | **Not created yet** — see *Architecture → Status*. The pure domain: one folder per workflow area holding constrained types, domain error DUs, dependency function types, and the workflow pipelines. Plus `Result.fs`, its own generic `result` builder. References **no other project** |
| Outer ring | `MyDogsbody.Integrations.Credentials` (+ `.Database.Models`, C#) | LiteDB context, `Repositories/`, `UseCases/`, `Credential` entity. Under this architecture an integration is an **adapter**: it implements the function types the domain declares |
| Outer ring | `MyDogsbody.Integrations.Pdf` | PdfPig-backed `Domains/ReadPdfDomain.fs`, use cases |
| Composition | `MyDogsbody.Startup` | The composition root. `CredentialApiMappers.fs` (domain ⇄ UI, pure), `CredentialApiFactory.fs` (`createCredentialApi`, dependencies as parameters), `Startup.fs` (LiteDB contexts, shared `handleError`, `registerServices`). Plugs real adapters into workflows and maps between the two error types |
| Retiring | `MyDogsbody.Spine` | `Domains/` and `UseCases/` — the layered core this architecture replaces. **Add nothing to it**; see *Architecture → Status* |
| Host | `MyDogsbody` (C#) | WPF `MainWindow` + `BlazorWebView`, `Frame.razor` (MudBlazor providers + theme), the DI registration, `wwwroot/` |
| UI | `MyDogsbody.UI.Portal` | `Shell.fs` (routes), `Pages/`, `Components/`, `ModuleCreators/`, `Layout/` |
| UI | `MyDogsbody.UI.Types` | UI-facing records (`IntegrationCredentialUiType*`) and `Modules/` adaptive-state records |
| Main database | `MyDogsbody.Database` (+ `.Database.Models`, F#) | The **main** store: SQLite `DatabaseContext` — a `SqliteConnection` plus a Dapper.FSharp `QuerySource<_>` per table; `Blog`/`Comment` records. Not consumed by the app yet |
| Main database | `MyDogsbody.Database.Migrations` | FluentMigrator migrations — **the schema source of truth for the main database**. Runner instructions in its `INSTALL.md` |
| Cross-cutting | `MyDogsbody.Builders` | `HandleErrorBuilder` — the outer ring's `Result` computation expression. The domain does **not** use it; see *Architecture → Errors* |
| Cross-cutting | `MyDogsbody.Exceptions` / `.Exceptions.Types` | `ExceptionHelpers`, `MyDogsbodyException`, `ActionNames` |
| Cross-cutting | `MyDogsbody.Enums` (C#) | `InfrastructureType` — shared by F# and C# projects |
| Cross-cutting | `MyDogsbody.Logging` (+ `.Database.Models`, C#) | **The log store — its own LiteDB database, one collection per log type.** Context, repository, use cases, `ExceptionLog` entity. Only errors (`Exceptions`) are implemented today. Not an integration — see *Architecture* |
| Tests | `MyDogsbody.Tests` | xunit v2, the only test project |
| Scratch | `GNUCashAccess`, `GoogleCalendarCRUD`, `TestMsGraphToEmails`, `PdfProcessing`, `MyDogsbody.Integrations.Google` | Standalone experiments; `Integrations.Google` is a one-line stub |

Reference direction, enforced by project references. **Dependencies point inward** — that is the whole rule, and the reference graph is what enforces it:

- **`MyDogsbody.Domain` references nothing.** Not `Builders`, not `Exceptions.Types`, not LiteDB, not an integration. If a domain file needs something it cannot reach, the thing it needs is a dependency function type, not a project reference. This is the invariant the architecture is built on — breaking it costs the whole benefit.
- **Integrations reference `Domain`** and implement the function types it declares. They never reference each other, and they never reference `Startup` or the UI.
- **`MyDogsbody.UI.Portal` references `UI.Types`, `Enums` and (transitively) `Exceptions.Types` — nothing else.** `Domain`, `Spine` and the integrations stay unreachable from the screen. Do not add a `Domain` reference to the UI to save a mapper; see *Architecture → The two mapping points*.
- **`Startup` references everything it wires** — `Domain`, the integrations, `Builders`, `Logging`, `UI.Types` — and nothing references `Startup` except the C# host.
- The C# projects sit at the bottom (entities, enum) and reference nothing upward.
- Nothing references `MyDogsbody.Database` yet, and the sole reference to `.Database.Migrations` is from the scratch project `TestMsGraphToEmails`, which never calls it. Don't read that as "unused" — it is the main database, just not wired in (see *Storage*).
- **Legacy, until `Spine` is gone:** integrations never reference `Spine`; `Spine` is the only project that pulls integrations together; `Spine` does not reference the logging project.

Watch the name collision: `MyDogsbody.Database.Models` is **F# records for the main SQLite database**, while `MyDogsbody.Integrations.*.Database.Models` are **C# classes for that integration's LiteDB store**. Same suffix, different tier, different language. `MyDogsbody.Logging.Database.Models` is a third thing again — the log store's entities, C# for the same LiteDB reason, but belonging to no integration.

## Commands

### Build

```powershell
dotnet build MyDogsbody.sln
dotnet build MyDogsbody.UI.Portal\MyDogsbody.UI.Portal.fsproj   # fastest loop when editing UI
```

### Run

```powershell
dotnet run --project MyDogsbody\MyDogsbody.csproj   # launches the WPF app (Windows only, net9.0-windows)
```

`Logging.db` and `Credentials.db` are created relative to the process working directory — under `dotnet run` that is `bin\Debug\net9.0\`.

### Migrate (main database)

Schema changes to the main SQLite database are applied by FluentMigrator, never by hand. Run from `MyDogsbody.Database.Migrations\` — the `-a` assembly path is relative to it:

```powershell
dotnet tool install -g FluentMigrator.DotNet.Cli   # once
dotnet build MyDogsbody.Database.Migrations\MyDogsbody.Database.Migrations.fsproj
dotnet fm migrate -p sqlite -c "Data Source=bin\Debug\net9.0\test.db" -a .\bin\Debug\net9.0\MyDogsbody.Database.Migrations.dll
```

That is the checked-in `INSTALL.md` verbatim; `-c` is whichever database file you are migrating (`bin\Debug\net9.0\application.db` is what has actually been migrated so far). `dotnet fm rollback` walks the `Down()` methods back.

In-process equivalent: `MigrationSetup.setupMigrations connectionString` builds the runner and calls `MigrateUp()`. Nothing calls it today — an app or test that needs a migrated database calls it itself.

### Test

```powershell
dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj
dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj --filter "FullyQualifiedName~getPdfObject"   # single test
dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj --filter "Level=Unit"                        # one level
dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj --collect:"XPlat Code Coverage"              # coverlet is already referenced
```

Tests are xunit v2 with backtick-quoted F# names, e.g. ``` `getPdfObject returns Error when PDF does not exist` ```; `FullyQualifiedName~` matches on those.

There is no CI, no lint/format step, no `Directory.Build.props`/`global.json`.

### Build state

`dotnet build MyDogsbody.sln` succeeds and `dotnet test` runs green (28 tests: 18 Unit, 7 Integration, 3 Contract). The three previously non-compiling projects were repaired by the `startup-composition-root` change — if the build breaks now, assume you broke it.

## Testing in this codebase

The policy — unit tests first, all four levels green before a change is complete — is in [CLAUDE.md](CLAUDE.md). This section is how to satisfy it here.

Tag every test with its level so the filters above work:

```fsharp
[<Fact; Trait("Level", "Integration")>]
```

### Unit

**Domain workflows** are the easy case, and making them so is the point of the architecture. Dependencies are function types, so a fake is a lambda — no mocking framework, no `handleError`, no temp files, no fixtures:

```fsharp
let loadAccounts : LoadAccounts = fun () -> Ok [ existingAccount ]
let saveAccount  : SaveAccount  = fun a -> Ok (linked a)
let result = LinkAccountWorkflow.linkAccount loadAccounts lookupProfile saveAccount input
```

- **Ok path** — assert **every field** of the output, unwrapping constrained types with their `value` function. `Result.isOk` proves nothing.
- **Error path** — assert the exact DU case *and its payload*: `Error (AlreadyLinked addr)`, with `addr` checked. The payload is what the message is written from, so an unasserted payload is an untested message.
- **Constructor tests** — every `create` on a constrained type gets its own: one accepted value, one rejected value per rule, and the rejection reason asserted.
- **Dependency-not-called** — where a rule short-circuits (validation fails, so nothing is saved), pass a fake that records invocations and assert the store was never reached. This is the domain equivalent of the unlogged-failure path.

**Outer-ring functions** keep the existing shape (dependencies first, input last, `Result<'T, MyDogsbodyException>` out) and the existing test approach:

```fsharp
let handleError = HandleErrorBuilder (fun _ -> ())   // no-op logger, keeps the test off Logging.db
```

- **Ok path** — assert every field, including the entity → domain mapping the store performs.
- **Error path** — assert the `MyDogsbodyException` carries the exact `ActionNames.*` string the function declares, the expected message, and a preserved `InnerException`.
- **Unlogged-failure path** — where the function builds a `MyDogsbodyException` wrapping an `ApplicationException` (the expected-failure idiom, e.g. `ReadPdfDomain.getPdfContent` on a missing file), pass a `HandleErrorBuilder` whose callback records invocations and assert nothing was logged.

Know where the seams are. In `MyDogsbody.Domain` every dependency is substitutable by construction — that is what a function type buys. In the legacy layered code it is not: modules call each other **directly by name** (`CredentialsUseCases.insertOne` calls `CredentialsRepository.insertOne`), so the only substitutable dependency below the use-case layer is the collection getter, and anything bottoming out at `ILiteCollection<T>` is an integration test unless you hand-fake the collection. Cleanly unit-testable there: `*TypeMappers.fs`, pure logic (`DocumentDomain.getContentSplitByLines`), `ReadPdfDomain` (file-path seam), and `ModuleCreators` (takes `getAllCredentials` as a parameter).

### Integration

Covers the outer ring against real storage: adapters/repositories, `*DatabaseContextModule.getDatabaseContext`, `DatabaseContextSetup.createDatabaseContext`, the migrations, and a workflow run with real adapters bound to it (the composition root's job, exercised through `*ApiFactory`). Domain workflows with fake dependencies are **unit** tests, not these — if a test needs a temp file to exercise a rule, the rule is in the wrong ring.

**Main database (SQLite):**

- Fresh temp file per test — `Data Source={Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")}`.
- **Build the schema by calling `MigrationSetup.setupMigrations` against that connection string.** Never hand-write DDL in a test: the migrations are the schema source of truth, so a test that creates its own tables stops proving they match. That makes every SQLite integration test a migration test too.
- `createDatabaseContext` constructs a `SqliteConnection` and never disposes it. Dispose it in the test (`GetDatabaseConnection()` hands it back) — Microsoft.Data.Sqlite pools connections, and a pooled handle can keep the temp file locked on Windows so cleanup fails.
- Migrations get their own tests: `MigrateUp` on an empty file produces the expected tables and columns, and `Down()` reverses it. Nothing else verifies a migration before it runs against real data.

**LiteDB stores (each integration's, and the log store):**

- Fresh temp database per test — `Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")`, `connection=direct`, deleted in `try/finally` or an `IDisposable` fixture. `CredentialApiFactoryTests.withApi` demonstrates the temp-path + cleanup shape. Production uses `shared`; if a change touches concurrent access, pin that mode explicitly.
- **Never let a test reach `Startup.Startup`.** Its module-level `let` bindings open `Logging.db` and `Credentials.db` in the process working directory the moment anything in the module is touched. That is why the composition root is split three ways: test `CredentialApiMappers` and `CredentialApiFactory` — both are free of module-level I/O and take their dependencies as parameters — and leave `Startup.fs` alone. Keep any new composition the same shape.
- `CredentialsDatabaseContextModule.getDatabaseContext` closes over the `LiteDatabase` and never hands it back, so a test cannot dispose the handle and Windows may hold the file. Cleanup is best-effort (`try File.Delete path with _ -> ()`); see `CredentialApiFactoryTests.withApi`. Add a `Dispose` to the context record if a change needs deterministic cleanup.
- Required round-trips: insert → `getAll` returns the row with `ObjectId` surfaced as the `Id` string; update → re-read reflects the new values.
- Characterization target: `CredentialsRepository.updateOne` matches on `InfrastructureType` and **ignores `credential.Id`**, so with two credentials of the same type it updates the first match, and with no match it silently returns `Ok ()`. Pin that before changing it.

### Contract

- **Dependency function types** are this architecture's published interfaces, so CLAUDE.md's shared-suite rule applies to each one. The failure it catches here: a fake returning shapes the real store never produces, leaving a workflow's unit suite green over code that cannot work in production.
- **`CredentialApi`** and every future API record (`MyDogsbody.UI.Types`): a record of functions, so a fake is a record literal rather than a class. Same rule — the suite that exercises the real API also runs against the fakes the UI tests use. `CredentialsBrowserModuleCreatorsTests` shows the fake shape.
- **The two boundary mappers** (see *Architecture → The two mapping points*), asserted field-for-field in both directions. There is no chain test to write — assert each one exhaustively instead, and assert that a constrained type survives the round trip through the store's `string` column unchanged.
- **Error translation at the composition root**: assert each domain error DU case maps to the intended `MyDogsbodyException` (action, message), and that an adapter exception maps to the intended domain error case. This pair is the seam the UI's alerts are written from.
- **`ActionNames`**: the strings are `$"..."`-composed and compiler-unchecked — assert each outer-ring function's error reports its declared action. A typo is otherwise invisible until someone reads the exception log.
- **LiteDB entity shape**: LiteDB is schemaless, so renaming a property on `Credential`/`ExceptionLog` silently orphans stored data. Assert the persisted document's field names, not just the round-tripped object.
- **Legacy**: while the layered credentials path survives, `Contracts/CredentialHopChainTests.fs` keeps covering its five hops. Delete a hop test in the same change that deletes the hop — not before, not after.

### E2E

Composition root down: component/page → API record → workflow → adapter → a real LiteDB file → back into the adaptive state the component renders. (For the credentials path, that middle is still `Spine` until it is migrated.)

- Fun.Blazor components are Blazor `ComponentBase`s, so bUnit can render `Shell`, pages, and `CredentialsEditorDialog`. That harness (bUnit + MudBlazor test services) **still does not exist** — the change that first needs it adds it. It is the one level the `startup-composition-root` change could not satisfy; everything below the rendered component is covered by the integration and contract suites.
- Flows as they come into scope: add credential → row appears in `credentialsBrowser`; edit credential → persisted value changes and the table reflects it; failure → `ErrorBoundary'`/`MudAlert` surfaces the message *and* an `ExceptionLog` row lands in `Logging.db`.
- Driving the real WPF `BlazorWebView` window is out of scope.

### Where tests go

- `MyDogsbody.Tests` is the only test project. Add each new `.fs` to `MyDogsbody.Tests.fsproj` **above `Program.fs`** — it carries the `[<EntryPoint>]` (`GenerateProgramFile=false`) and must stay last in compile order.
- Mirror the source layout: `Domain/<Area>/`, `Startup/`, `Integrations/Pdf/Domains/`, `UI/ModuleCreators/`, `Contracts/`, and `Spine/Domains/` while it lasts.
- The test project references `Builders`, `Enums`, `Exceptions.Types`, `Integrations.Credentials`, `Integrations.Pdf`, `Spine`, `Startup`, `UI.Portal` and `UI.Types`. Add the `ProjectReference` for anything else you test in the same change — `MyDogsbody.Domain` when it exists.

## Architecture

**Functional onion**, after *Domain Modeling Made Functional* (Scott Wlaschin, Pragmatic Bookshelf, 2018). The rationale, the alternatives it beat, and worked sketches are in [`docs/architecture-options.md`](docs/architecture-options.md) — this section is the rule; that document is the argument. The book's own sample bounded context (`src/OrderTaking` in [swlaschin/DomainModelingMadeFunctional](https://github.com/swlaschin/DomainModelingMadeFunctional)) is the reference implementation to copy shapes from.

### Status — specified vs built

This section describes the architecture **all new and changed code follows**. The repository is mid-migration, so what you open will not always match:

| | Specified | Built today |
| --- | --- | --- |
| `MyDogsbody.Domain` | The centre of everything | **Does not exist.** The first change under this architecture creates it |
| `MyDogsbody.Spine` | Gone — pipeline moves to `Domain`, wiring to `Startup` | Still present, still the credentials path |
| DTO hops per feature | 2 mappers, both at the edges | 5 mappers, one per layer |
| Domain error type | A DU per workflow area | `MyDogsbodyException` everywhere |
| Store in a core signature | Never | `Spine/Domains/CredentialsDomain.fs` takes `unit -> CredentialsCollection`, which is `ILiteCollection<Credential>` |

So: **write new work in the shape below.** When you change existing credentials code, migrate the part you touch rather than extending the layer chain — and don't rewrite the whole path as a side effect of an unrelated change. Each migration step is a change folder under `docs/changes/` like any other.

### The rings

Three, and dependencies only ever point inward:

```
  Startup  ──────────────────────────────  composition root
    │  plugs adapters into workflows, maps both error types, owns the handles
    │
    ├─ outer ring ── Integrations.* ─────  LiteDB, PdfPig, HTTP, the clock
    │                  implement the function types the domain declares
    │
    └─ centre ────── MyDogsbody.Domain ──  types + workflows. references nothing.
                       no I/O, no LiteDB, no MyDogsbodyException, no handleError

  UI.Portal ── CredentialApi (UI types) ── Startup     ← the UI reaches no further
```

The centre has no idea any of the rest exists. That is not a style preference — it is what makes the whole domain testable with plain values and no fixtures, and it is enforced by `MyDogsbody.Domain` having no `ProjectReference` elements at all.

### Types first

Model so that invalid states cannot be written down. A workflow area declares, in its `*Types.fs`:

**Constrained primitives** — a private single-case union plus a module `create`. Holding the type *is* the proof it was validated; nothing downstream re-checks:

```fsharp
type EmailAddress = private EmailAddress of string
module EmailAddress =
    let create (s: string) : Result<EmailAddress, string> = ...
    let value (EmailAddress s) = s
```

`create` returns the plain reason for failure; the workflow decides what to call it. Copy the shape from the book's `Common.SimpleTypes.fs`.

**A type per stage of the pipeline** — not one record reused with optional fields. Typed-in, validated, stored are three different types, so code that has one cannot be handed another:

| Stage | Shape | Meaning |
| --- | --- | --- |
| `Unvalidated*` | plain `string` fields | what the user typed. untrusted |
| `Valid*` | constrained types | been through validation |
| `*` (stored) | constrained types + `Id`, timestamps | been through the store |

**A domain error DU per workflow area**, carrying the values needed to write the message:

```fsharp
type LinkError =
    | NotAnEmailAddress of string
    | AlreadyLinked     of EmailAddress
```

**Dependencies as function types** — not interfaces, not classes, not a record of a collection getter:

```fsharp
type LoadAccounts = unit -> Result<LinkedAccount list, LinkError>
type SaveAccount  = ValidAccount -> Result<LinkedAccount, LinkError>
```

### Workflows as pipelines

A use case is one function: **dependencies as leading parameters, input last, `Result` out**. It reads top to bottom as the steps of the job.

```fsharp
let linkAccount
    (loadAccounts: LoadAccounts)
    (lookupProfile: LookupProfile)
    (saveAccount: SaveAccount)
    (input: UnlinkedAccount) : Result<LinkedAccount, LinkError> =
    result {
        let! address = EmailAddress.create input.TypedAddress
                       |> Result.mapError NotAnEmailAddress
        let! linked  = loadAccounts ()
        let! address = ensureNotLinked linked address
        let! name    = lookupProfile address
        return! saveAccount { Address = address; DisplayName = name }
    }
```

- `result` is the domain's own generic builder in `MyDogsbody.Domain/Result.fs` — see *Errors* below for why it cannot be `handleError`. Hand-writing it is what the book's sample does rather than taking a dependency; FsToolkit.ErrorHandling is the alternative if a change needs `asyncResult` and more.
- One workflow per file, named `<Workflow>Workflow.fs`, exposing one public function.
- Steps that are pure decisions stay private functions in the same file.
- **No I/O in the file.** `loadAccounts` performing a query is invisible here on purpose — the workflow sees a function value.

### The two mapping points

The layered design had five mappers, one per hop. This one has **two, both at the edges**, and domain types travel unmapped everywhere between:

| Edge | Lives in | Maps |
| --- | --- | --- |
| Bottom | the integration's store | LiteDB entity (C#, `ObjectId`) ⇄ domain type |
| Top | `Startup/*ApiMappers.fs` | domain type ⇄ `MyDogsbody.UI.Types` record |

The top mapper is a deliberate choice, and it is where this deviates from the sketch in `architecture-options.md`: the workflow's summary type could be handed to the UI directly, saving a mapper, but that would put `MyDogsbody.Domain` in `UI.Portal`'s reference graph. Keeping the UI on its own records is what makes the domain *unreachable* from the screen rather than merely unused there — the same property the `startup-composition-root` change bought and the same reason `CredentialApi` was never widened to take a Spine type. One mapper is the price; pay it.

Everything between those two points is domain types. **Adding a workflow does not add a DTO hop.** If you find yourself writing a third record with the same fields, that is the defect this architecture exists to prevent.

### Errors — two types, meeting once

| Ring | Error type | Builder |
| --- | --- | --- |
| Domain | a `*Error` DU per workflow area | `result` (`MyDogsbody.Domain/Result.fs`) |
| Outer ring + composition root | `MyDogsbodyException` | `handleError` (`MyDogsbody.Builders`) |

The domain speaks in domain terms because those errors are what the UI renders as sentences. The outer ring speaks `MyDogsbodyException` because that is what carries an `ActionName`, an inner exception and a trip to the log. **They meet in the `*ApiFactory`, nowhere else** — inbound, an adapter's exception maps to a domain error case; outbound, the workflow's error maps to a `MyDogsbodyException` for the UI.

**Why two builders and not one:** `HandleErrorBuilder.Bind` returns `Result<_, MyDogsbodyException>` and its `TryWith` handler returns one — the error type is pinned, not generic. It therefore cannot bind a `Result<_, LinkError>`, and it lives in `MyDogsbody.Builders`, which the domain cannot reference anyway. That single constraint is the whole reason `MyDogsbody.Domain/Result.fs` exists.

Outer-ring functions are unchanged from the layered design — `Result<'T, MyDogsbodyException>`, written with `handleError`:

```fsharp
let insertOne (handleError: HandleErrorBuilder) ... : Result<unit, MyDogsbodyException> =
    let action = ActionNames.MyDogsbody.Integrations.Credentials.Repositories.insertOne
    handleError {
        try
            return ...
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new credential.", ex)
    }
```

The `with` branch *yields* the wrapped exception rather than raising it: the builder's `TryWith` takes it, calls `writeLog`, and returns `Error`. So the message + `ActionName` you supply are what gets persisted.

- `action` always comes from `MyDogsbody.Exceptions.Types/ActionNames.fs` — nested modules mirroring the code path, built from `$"..."` string composition. Add an entry there when you add an outer-ring function; don't inline literals. **Domain workflows have no `ActionName`** — their errors are DU cases, which need no string.
- `ExceptionHelpers.isApplicationException`: a `MyDogsbodyException` whose `InnerException` is an `ApplicationException` passes through **unlogged**. That's the idiom for expected/validation failures in the outer ring — see `ReadPdfDomain.getPdfContent` constructing one for a missing file. In the domain the same idea is free: an expected failure is just a DU case and was never an exception.
- `writeLog` is wired in `Startup/Startup.fs` to `MyDogsbody.Logging`, which inserts an `ExceptionLog` into the `Exceptions` collection of `Logging.db` — the separate log database, never the main one (see *Storage → Log database*).
- **Never raise as control flow, in either ring.** Exceptions are caught at the boundary and converted.

### Logging is cross-cutting, not an integration

An integration is an *adapter for a capability the domain declares a need for*: it implements a dependency function type, a workflow calls it, and its data flows out through the composition root into the UI. Credentials and Pdf are integrations. **Logging is none of that** — it is infrastructure the outer ring uses to say what happened, and no workflow ever declares a need for it.

What follows from that, and what to keep true:

- **It is not a dependency function type.** No `WriteLog` appears in a `*Types.fs` next to `LoadAccounts` and `SaveAccount`. The domain does not log — it returns a `*Error` DU case and the composition root decides what that is worth recording. Adding a log type never adds a workflow parameter, a domain type, or a mapper.
- **It is not passed as a parameter.** Every other store arrives as a leading getter (`getCredentialCollection`); the log store never does. Outer-ring functions receive `handleError` — already closed over `writeLog` at the composition root — and that is their entire access to logging. **Do not add a `getExceptionCollection` (or any log-collection) parameter to any function, in either ring.**
- **`MyDogsbody.Domain` cannot reference it**, which the reference graph already guarantees, and neither does `Spine`. Its only non-scratch consumer is `MyDogsbody.Startup`. `MyDogsbody.Tests` does not reference it either — tests pass their own `HandleErrorBuilder` and never touch `Logging.db`.
- **It holds no domain data** and nothing reads from it at runtime — see *Storage → Log database*.
- **Its data never flows back to the user.** A future log viewer reads `Logging.db` through its own read path; it does not become a workflow dependency and it does not turn logging into an integration.

**The names say so too.** The projects were `MyDogsbody.Integrations.Logging` / `.Integrations.Logging.Database.Models` until the `logging-not-an-integration` change moved them to `MyDogsbody.Logging` / `MyDogsbody.Logging.Database.Models`. Nothing about logging is under `MyDogsbody.Integrations.*` any more, and nothing new should go there — rules written for "every integration" are not written for this.

### Composition root

`MyDogsbody.Startup` is where the abstract meets the real: it picks the actual functions that satisfy each dependency type, hands them to the workflows, and translates the two error types. It is the **only** place that knows both LiteDB and the domain. Split into three files on purpose:

| File | Holds | I/O at module init |
| --- | --- | --- |
| `CredentialApiMappers.fs` | domain type ⇄ UI record, total functions | none |
| `CredentialApiFactory.fs` | `createCredentialApi handleError getCredentialCollection` — adapters bound to dependency types, workflows partially applied, `Result.mapError` both ways | none |
| `Startup.fs` | database paths, LiteDB contexts, `handleError`, `credentialApi`, `registerServices` | opens `Logging.db` and `Credentials.db` |

The factory is where an adapter becomes a dependency:

```fsharp
let private loadAccounts : LoadAccounts =
    fun () ->
        GoogleAccountStore.getAll handleError getAccountCollection ()
        |> Result.mapError (fun ex -> GoogleUnreachable ex.Message)   // in:  exception -> domain error

let linkAccount (input: UnlinkedAccount) : Result<LinkedAccount, MyDogsbodyException> =
    LinkAccountWorkflow.linkAccount loadAccounts lookupProfile saveAccount input
    |> Result.mapError toMyDogsbodyException                          // out: domain error -> exception
```

`Startup.fs` holds module-level `let` bindings created once per process. Paths are relative, so the `.db` files land in the process working directory (`bin\Debug\net9.0\` under `dotnet run`). **Everything with behaviour worth testing belongs in the other two files** — that split is what makes the composition root testable at all, so keep it when adding a second API.

**`Result` is not collapsed here.** `CredentialApi` returns `Result<_, MyDogsbodyException>` and the UI decides what a failure looks like (`CredentialsBrowserModule.ErrorAval` → `MudAlert`). Do not reintroduce `|> ignore` on writes or `failwith` on reads.

Adding a feature: declare its types, dependency function types and workflows in `MyDogsbody.Domain`; write the adapters in the integration; declare the API record in `MyDogsbody.UI.Types`; bind it all in a `*ApiFactory.fs`; partially apply in `Startup.fs` and register in `registerServices`. `MainWindow.xaml.cs` should not change. The DI container there exists only to hand the UI an API record.

### UI (Fun.Blazor + MudBlazor + FSharp.Data.Adaptive)

**The UI stays adaptive. This is settled, not pending.** `cval`, `aval`, `transact` and `adapt { }` are the state model, and the onion above stops at the composition root — it does not reach into the screen. MVU/Elmish was evaluated as Option 3 in `docs/architecture-options.md` and **rejected**: it addresses only the UI, the problems it prevents were already fixed by hand in the `startup-composition-root` change, and adopting it would mean a second state paradigm for no defect it currently closes. Do not introduce `Model`/`Msg`/`update`, `Cmd`, `Elmish`, or a dispatch loop. If a page's adaptive state is getting unwieldy, the answer is a better module creator, not a message type.

What the onion *does* ask of the UI is unchanged from today: the screen talks to an API record of functions speaking `MyDogsbody.UI.Types`, and reaches no further.

- `Shell.fs` wires an `ErrorBoundary'` around `html.route [...]`, wrapped in `MainLayout.view`. Each page module exposes `getView()` and `getRoute()` (`routeCi "/settings/credentials"`); register new pages in `Shell.fs`. Settings pages pipe their view through `SettingsComponents.settingsNavMenu`.
- Services are obtained per-view with `html.inject (fun (credentialApi: CredentialApi, dialogService: IDialogService) -> ...)`.
- **State pattern:** `UI.Types/Modules/*.fs` declares a record of `aval<_>` fields + commands (`CredentialsBrowserModule`); `UI.Portal/ModuleCreators/*.fs` builds it from `cval` + `transact` over the API functions; components take the module and render inside `adapt { let! x = ... }`. Components never call the API directly — pages pass callbacks in.
- **Module creators take `startWork: (unit -> unit) -> unit` as their first parameter.** Production passes `fun work -> Async.Start(async { work () })`; a test passes `fun work -> work ()` and never waits on a thread. Don't call `Async.Start` inside a module creator.
- **A write reloads.** Every command that changes stored data calls the load function on success, so the table shows what was stored rather than what the dialog held. Failures set `ErrorAval`; a later success clears it.
- **Dialogs must be classes**, not functions: inherit `FunComponent`, expose `[<Parameter>]` members and a `[<CascadingParameter>] IMudDialogInstance`, and show via `dialogService.ShowAsync<T>(title, DialogParameters<T>, DialogOptions)`. See `CredentialsComponents.CredentialsEditorDialog` / `showCredentialsEditorDialog`; recent commits deliberately moved away from function-style dialogs.
- The WPF side is thin: `Frame.razor` supplies the MudBlazor providers and the dark "Grey 90 Carbon" theme and renders `<Shell />`; `MainWindow.xaml` points `BlazorWebView` at `wwwroot/index.html`.

### Storage

Three tiers, kept deliberately separate: one main database, a private store per integration, and one log database.

#### Main database — SQLite

`MyDogsbody.Database` is the application's main store. `DatabaseContextSetup.createDatabaseContext databaseFilePath` registers Dapper.FSharp's `OptionTypes`, opens a `SqliteConnection` (`Data Source={path}`) and returns a `DatabaseContext` record of getters — `GetDatabaseConnection`, plus one `unit -> QuerySource<'T>` per table, each bound to its table name via `table'<Blog> "Blogs"`. Models are F# `[<CLIMutable>]` records in `MyDogsbody.Database.Models`. Same context-record-of-getters shape as the integrations use, so it partially applies into an outer-ring function the same way — and stops at the same boundary: a `QuerySource<'T>` is no more allowed in a domain signature than an `ILiteCollection<T>` is.

**The schema belongs to `MyDogsbody.Database.Migrations` and nothing else.** The FluentMigrator classes under its `Migrations/` folder are the source of truth: never create or alter a table at runtime, from `DatabaseContextSetup`, or by hand in a SQLite tool. Adding a column means adding a migration. `MigrationSetup.setupMigrations` wires `AddSQLite` + `ScanIn(...).For.Migrations()` and calls `MigrateUp()`; CLI equivalent under *Commands → Migrate*.

Status: the main database is **designed but not wired in**. Nothing references `MyDogsbody.Database`, nothing calls `setupMigrations`, and `Blog`/`Comment` are scaffold sample tables rather than domain tables — treat both as the shape to follow, not as finished schema. There is no composition-root binding either; the first change that needs the main database adds one in `Startup/Startup.fs` alongside the LiteDB contexts, writes store functions against it, and binds those to the dependency function types a workflow declares — never reaching it from the UI, and never handing a `DatabaseContext` inward.

#### Per-integration databases — LiteDB today

Each integration owns its own database file, separate from the main database and from every other integration: a context record of `unit -> ILiteCollection<T>` getters (`CredentialsDatabaseContext`) built by a `*DatabaseContextModule.getDatabaseContext databasePath connectionType`. Entities are mutable **C# classes** in the `*.Database.Models` C# projects because LiteDB needs settable properties / `ObjectId`; F# adapters construct and mutate those instances.

**The collection getter stops at the integration boundary.** `unit -> ILiteCollection<T>` is how a store function receives its handle, and it goes no further inward — it never appears in a `MyDogsbody.Domain` signature, and the domain never names `ILiteCollection`, `LiteDatabase`, `ObjectId` or `BsonValue`. What the domain declares is a function type (`LoadAccounts`, `SaveAccount`); the store's job is to satisfy one, mapping the C# entity to a domain type on the way out. That the layered code violates this in `Spine/Domains/CredentialsDomain.fs` is exactly the leak this architecture was chosen to close.

LiteDB is a per-integration choice, not a project-wide one — an integration may pick a different store, and shares nothing with the main database (no cross-store joins, no shared schema). Whatever it picks, keep the same context-record-of-getters seam so the adapter stays testable, and keep the store swappable: changing database should mean rewriting the store module and its entity project, and nothing inward of them.

The log store below uses the same seam (`LoggingDatabaseContext`, `LoggingDatabaseContextModule.getDatabaseContext`) but is **not** one of these — it is its own tier, not an integration's private store.

#### Log database — LiteDB

**Every log the application writes goes to the logging component's own LiteDB database, and nowhere else.** That means *all* of it — errors, warnings, information, debug, trace — not only the exception log that exists today. `Logging.db` is a third store alongside the main SQLite database and each integration's private store.

The rule holds in both directions:

- **No log row is ever written to the main database or to another integration's store.** If a feature wants to record what happened, it records it here.
- **Nothing outside `MyDogsbody.Logging` opens `Logging.db`.** Callers reach it through the logging use cases, which the composition root partially applies into `handleError` (`Startup/Startup.fs`).
- **The log database holds no domain data.** It is diagnostics only, so it can be deleted, rotated or truncated between runs without the application losing anything it needs. Nothing reads from it at runtime; there is no UI over it yet.

##### One collection per log type

**Each log type gets its own LiteDB collection inside `Logging.db`.** One database, many collections — errors do not share a collection with warnings, and warnings do not share one with information.

**The collection is the severity.** Do not add a `Severity` / `Level` / `LogType` field to a log entity and filter on it — a row's collection already says what it is. A discriminator field and a collection would be two sources of truth for the same fact.

| Log type | Collection | Entity | Status |
| --- | --- | --- | --- |
| Error | `Exceptions` | `ExceptionLog` | implemented |
| Warning | `Warnings` | *(to add)* | not implemented |
| Information | `Informations` | *(to add)* | not implemented |
| Debug / trace | one collection each | *(to add)* | not implemented |

`Exceptions` is the established name and does not get renamed. New collections follow it: plural, named for the log type.

Adding a log type is one change confined to the logging project, in the shape the rest of the codebase already uses, and touches nothing outside it:

1. An entity class in `MyDogsbody.Logging.Database.Models` (C#, settable properties — LiteDB needs them). `ExceptionLog` is the shape to copy; carry only the fields that type genuinely has, rather than reusing `ExceptionDetails` for something that has no exception.
2. A `unit -> ILiteCollection<T>` getter on `LoggingDatabaseContext`, bound in `LoggingDatabaseContextModule.getDatabaseContext` — the same context-record-of-getters seam everything else uses.
3. A repository function and a use case, both returning `Result`, mirroring `ExceptionRepository` / `ExceptionUseCases`.
4. Partial application in `Startup/Startup.fs`.

Status: **only errors are implemented.** `writeLog` is the sole writer and `ExceptionLog` has no severity field — correctly, under the rule above. Adding warnings or information means adding collections to this database; it does **not** mean a second log database, a severity column, or borrowing the main one.

### Conventions

- New `.fs` files must be added to the `.fsproj` `<Compile Include>` list **in dependency order** — F# compile order is significant and there is no glob.
- **`MyDogsbody.Domain` folder shape: one folder per workflow area, named for the area, not the layer.** Inside it, `<Area>Types.fs` first (constrained types, stage types, the error DU, the dependency function types), then one `<Workflow>Workflow.fs` per workflow. `Result.fs` sits at the project root, compiled first. Copy the book's `src/OrderTaking` for anything not covered here.
- **Do not create `Domains/Types`, `UseCases/Types` or `Repositories/Types` folders in new code.** That is the per-layer shape this architecture replaces; it survives only in `Spine` and the existing integrations. Inside an integration, new adapter code goes beside `Database/`, named for what it talks to (`GoogleAccountStore.fs`, `GoogleProfileApi.fs`).
- Migrations are `Migrations/Migration_<timestamp>_<Name>.fs` holding one `[<Migration(<same timestamp>L)>] type <Name>()` with `Up()` and `Down()` — e.g. `Migration_20251104000001_CreateBlogTable.fs` / `CreateBlogTable`. Add each to `MyDogsbody.Database.Migrations.fsproj` in timestamp order, above `MigrationSetup.fs`. Never edit a migration that has been applied; write a new one.

### Naming quirks (grep both spellings)

`Domian` is load-bearing in real identifiers: `DomianTypeMappers`, `DocumentContentDomianTypeDto`, `mapPdfContentUseCaseTypeDtoToDocumentContentDomianTypeDto`, and `DocumentDomian.fs` (whose module is spelled `DocumentDomain`). Also `GetInfrustructureCredentialCallback`, and `MyDogsbody.Integrations.Google` whose module is declared `MyDogsbody.Infrastructure.Google.GoogleCalendar`.
