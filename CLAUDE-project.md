# CLAUDE-project.md

Project reference for MyDogsbody: what it is, how it is laid out, how to build / run / test it, and how it fits together. The working rules — the spec-driven change process and the testing mandate — are in [CLAUDE.md](CLAUDE.md).

## What this is

A Windows desktop app: a WPF shell (`MyDogsbody`) hosting a `BlazorWebView`, whose entire UI is written in F# with Fun.Blazor + MudBlazor. .NET 9, F#-first — the handful of C# projects exist only for LiteDB entity classes.

**The architecture is the functional onion of *Domain Modeling Made Functional*.** A pure domain project at the centre that references nothing, dependencies declared as function types, workflows written as pipelines, and all I/O in an outer ring wired up at the composition root. See [Architecture](#architecture).

**The UI is the exception, deliberately** — it stays on Fun.Blazor + FSharp.Data.Adaptive rather than adopting MVU. See [UI](#ui-funblazor--mudblazor--fsharpdataadaptive).

Storage is split on purpose: **SQLite is the main database**, every integration owns a *separate* store of its own (LiteDB today, not necessarily tomorrow), and **all logging goes to its own separate LiteDB database with one collection per log type** — errors, warnings, information, everything. See [Storage](#storage).

## Project structure

One solution, `MyDogsbody.sln`. Every project targets `net9.0` except the WPF host (`net9.0-windows`, `WinExe`, `Microsoft.NET.Sdk.Razor`, `UseWPF`).

| Ring | Project | Holds |
| --- | --- | --- |
| **Centre** | `MyDogsbody.Domain` | The pure domain: `Documents/`, `Suppliers/`, `InvoiceTemplates/`, `Invoices/` and `MailAccounts/`, each holding `<Area>Types.fs` (constrained types, stage types, the error DU, the dependency function types) and one `<Workflow>Workflow.fs` per workflow. Plus `Result.fs`, its own generic `result` builder. References **no other project**, and a build target in its `.fsproj` fails the build if one is ever added |
| Outer ring | `MyDogsbody.Integrations.Google` (+ `.Database.Models`, C#) | *(a one-line stub until `credentials-per-provider` made it real)* `GoogleCredentialTypes.fs` — constrained types `GoogleCredentialSecret` / `GoogleExternalUsername` / `GoogleCredentialId`, and `ValidGoogleCredential` / `ValidGoogleCredentialEdit` / `StoredGoogleCredential`, **in the integration, not the domain** (a credential is not a domain concept). `GoogleDatabaseContext.fs` (context record), `GoogleDatabaseContextModule.fs` (`getDatabaseContext` — uses a **local** `BsonMapper`, not `BsonMapper.Global`, with `TrimWhitespace`/`EmptyStringToNull` off so a secret round-trips byte-for-byte), `GoogleCredentialEntityMappers.fs` (the bottom mapper — no enum-translation pair), `GoogleCredentialStore.fs` (`getAll` / `insertOne` / `updateOne`, outer-ring shape), `GoogleCredential` entity (`Id` / `Credentials` / `ExternalUsername`, no discriminator). `GoogleCalendar.fs` is still a one-line placeholder for change #6, and nothing wires the store into `Startup.fs` yet. An integration is an **adapter**: it implements the function types the domain declares |
| Outer ring | `MyDogsbody.Integrations.Documents` | *(was `Integrations.Pdf` until `invoice-extraction`)* One project per **capability**, not per library. `PdfDocumentReader.fs` (PdfPig — satisfies both `ReadDocumentContent` and `ReadDocumentText`), `WordDocumentReader.fs` (DocumentFormat.OpenXml, `.docx` only), `PlainTextDocumentReader.fs`, `EmailBodyReader.fs` (HtmlAgilityPack — HTML → block-tagged lines). `DocumentReaders.dispatch` is the composition-root binding of `ReadDocumentText` over all four. The four `readText` readers return `DocumentError` directly (no `ActionName`) — the `MailFolderReader` precedent; `readContent` keeps its outer-ring shape |
| Outer ring | `MyDogsbody.Integrations.Thunderbird` (+ `.Database.Models`, C#) | `ThunderbirdFolderScanner.fs`, `ThunderbirdAccountReader.fs`, `MailFolderEnumerator.fs`, `MailFolderReader.fs` (mbox/maildir discovery and reading — MimeKit, the only project that takes it as a dependency; the mbox path **streams** via `foldMboxSegments`, `StreamChunkBytes` = 4 MiB at a time, so a folder of any size reads in bounded memory — `MaxBufferableBytes` is a per-*segment* ceiling since `invoice-extraction` Phase 14, not a per-folder one), `ThunderbirdStore.fs` (LiteDB, the integration's own facts), `ThunderbirdEntityMappers.fs`. `ThunderbirdFolderScanner`/`ThunderbirdAccountReader`/`MailFolderEnumerator`/`MailFolderReader` construct `MailAccountError` directly rather than going through `handleError` — see *Errors* below for why |
| Composition | `MyDogsbody.Startup` | The composition root. `*ApiMappers.fs` (domain ⇄ UI + error translation, pure) and `*ApiFactory.fs` (`create*Api`, dependencies as parameters) for Supplier / Template / MailAccount / **Invoice / ScanWindow**; `Startup.fs` (the Logging and Thunderbird LiteDB contexts, the main SQLite context, migration run, `getCurrentTime = DateTime.Now`, shared `handleError`, `registerServices`). `InvoiceApiMappers` owns the shared `InvoiceError` ⇄ `MyDogsbodyException` translation (both the ledger and the scan-window factories return `InvoiceError`). Plugs real adapters into workflows and maps between the two error types |
| Host | `MyDogsbody` (C#) | WPF `MainWindow` + `BlazorWebView`, `Frame.razor` (MudBlazor providers + theme), the DI registration, `wwwroot/` |
| UI | `MyDogsbody.UI.Portal` | `Shell.fs` (routes), `Pages/`, `Components/`, `ModuleCreators/`, `Layout/` |
| UI | `MyDogsbody.UI.Types` | UI-facing records (`SupplierUiType*`, `SupplierApi`, `TemplateUiType*`, `MailAccountUiType*`, `InvoiceUiType*`) and `Modules/` adaptive-state records |
| Main database | `MyDogsbody.Database` (+ `.Database.Models`, F#) | The **main** store: SQLite `DatabaseContext` — a `SqliteConnection` plus a Dapper.FSharp `QuerySource<_>` per table, and a `Dispose`; `Blog`/`Comment`/`SupplierRecord`/`SupplierMatcherRecord` records; `SupplierRecordMappers.fs` (the bottom mapper) and `SupplierStore.fs` (the adapter). References `MyDogsbody.Domain` and `MyDogsbody.Builders`. `Blog`/`Comment` remain scaffold; `Suppliers`/`SupplierMatchers` is the first table pair actually consumed by the app |
| Main database | `MyDogsbody.Database.Migrations` | FluentMigrator migrations — **the schema source of truth for the main database**. Runner instructions in its `INSTALL.md` |
| Cross-cutting | `MyDogsbody.Builders` | `HandleErrorBuilder` — the outer ring's `Result` computation expression. The domain does **not** use it; see *Architecture → Errors* |
| Cross-cutting | `MyDogsbody.Exceptions` / `.Exceptions.Types` | `ExceptionHelpers`, `MyDogsbodyException`, `ActionNames` |
| Cross-cutting | `MyDogsbody.Logging` (+ `.Database.Models`, C#) | **The log store — its own LiteDB database, one collection per log type.** Context, repository, use cases, `ExceptionLog` entity. Only errors (`Exceptions`) are implemented today. Not an integration — see *Architecture* |
| Tests | `MyDogsbody.Tests` | xunit v2, the only test project |
| Scratch | `GNUCashAccess`, `GoogleCalendarCRUD`, `TestMsGraphToEmails`, `PdfProcessing` | Standalone experiments |

Reference direction, enforced by project references. **Dependencies point inward** — that is the whole rule, and the reference graph is what enforces it:

- **`MyDogsbody.Domain` references nothing.** Not `Builders`, not `Exceptions.Types`, not LiteDB, not an integration. If a domain file needs something it cannot reach, the thing it needs is a dependency function type, not a project reference. This is the invariant the architecture is built on — breaking it costs the whole benefit. Enforced twice: the `AssertDomainReferencesNothing` target in `MyDogsbody.Domain.fsproj` fails the build, and `Contracts/DomainIsolationTests.fs` asserts the same thing from the suite. (`FSharp.Core` arrives implicitly from the SDK and is the language runtime, not a dependency in this sense.)
- **A credential is not a domain concept.** Its constrained types (`GoogleCredentialSecret` and the rest) live in `MyDogsbody.Integrations.Google`, not the centre, so there is nothing about a credential for the domain to translate. The `credentials-per-provider` change removed the last thing that had pulled a shared enum toward the domain — `MyDogsbody.Enums.InfrastructureType`, the domain's own `Infrastructure` union, and the pair of edge mappers between them are all gone, and so is the `MyDogsbody.Enums` project. If the domain needs a value it cannot reach, it declares a function type, not a project reference.
- **An integration references `Domain` when it implements a function type the domain declares** — `Integrations.Documents` and `Integrations.Thunderbird` both do. **`Integrations.Google` deliberately does not**, and that is not an oversight to fix: per the bullet above a credential is not a domain concept, so the integration declares its own constrained types and there is no function type for it to satisfy. It references only `Builders`, `Exceptions.Types` and its own `.Database.Models`. If change #6's account or calendar work turns out to need a domain-declared dependency, *that* is what adds the reference. No integration ever references another, `Startup`, or the UI.
- **`MyDogsbody.UI.Portal` references *only* `UI.Types` and (transitively) `Exceptions.Types` — nothing else.** `MyDogsbody.Enums` is out of the reference graph entirely; `Domain` and the integrations stay unreachable from the screen. Do not add a `Domain` reference to the UI to save a mapper; see *Architecture → The two mapping points*.
- **`Startup` references everything it wires** — `Domain`, the integrations, `Builders`, `Logging`, `UI.Types`, and now `Database` + `Database.Migrations` — and nothing references `Startup` except the C# host and a throwaway smoke harness.
- The C# projects sit at the bottom (entities) and reference nothing upward.
- **`MyDogsbody.Database` references `MyDogsbody.Domain` and `MyDogsbody.Builders`**, and is itself referenced by `MyDogsbody.Startup` and `MyDogsbody.Tests` — the `invoice-ledger-foundation` change wired it in. It sits in the outer ring like an integration (same `handleError` / `Result<_, MyDogsbodyException>` shape, same bottom-mapper convention) without being one: it is the application's *main* store, not a per-integration one, so its `ActionNames` entries live under a `Database` module rather than `Integrations.*`. The sole other reference to `.Database.Migrations` remains the scratch project `TestMsGraphToEmails`, which never calls it.

Watch the name collision: `MyDogsbody.Database.Models` is **F# records for the main SQLite database**, while `MyDogsbody.Integrations.*.Database.Models` are **C# classes for that integration's LiteDB store**. Same suffix, different tier, different language. `MyDogsbody.Logging.Database.Models` is a third thing again — the log store's entities, C# for the same LiteDB reason, but belonging to no integration.

`MyDogsbody.Spine` is **gone**. The layered `Domains/` → `UseCases/` chain it held was replaced by workflow pipelines in `MyDogsbody.Domain` plus adapters in the integrations; nothing references it and no file should reintroduce that shape.

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

`Logging.db`, `MyDogsbody.db` and `Thunderbird.db` are created relative to the process working directory. Measured directly while closing `invoice-ledger-foundation`: running the command above from the repository root (as written) puts all of them **at the repository root**, not under `bin\Debug\net9.0-windows\` — `dotnet run` does not change directory into the build output before launching the app. `.gitignore` carries `*.db` (added in `invoice-extraction` Phase 14, after the `MeasureScan` commits had checked four of them in by accident — including a `Thunderbird.db` holding real account data), so they no longer show in `git status`; still delete them after a manual test. (`Thunderbird.db` joined the list with `thunderbird-account-selection`; a change that adds a per-integration store adds a file here too, so add it to this sentence in the same change. The Google credential store from `credentials-per-provider` would create a `Google.db`, but nothing wires it in yet, so it does not appear until change #6.)

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

`dotnet build MyDogsbody.sln` succeeds and `dotnet test` runs green: **1270 tests — 706 Unit, 270 Integration, 264 Contract, 30 E2E**, zero skips (measured against `credentials-per-provider`'s head, including PR review round 1 — via `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` and the same command with `--filter "Level=..."` per level). If the build breaks now, assume you broke it.

> **A second known flake joined the LiteDB one during `invoice-extraction`:** any test that constructs `ThunderbirdPersistedShapeTests` or another LiteDB context can still hit the `BsonMapper` race under the full suite's parallelism (it passes in isolation) — this is the documented one below. The SQLite store-test harnesses added by that change deliberately **do not** call `SqliteConnection.ClearAllPools()`, because that process-global call was clearing other tests' pooled connections mid-use; they leak their GUID-named temp file instead if the pool still holds a handle.

If you see an intermittent failure, do not re-run until it passes. **There is one known flake**, in any test that constructs a LiteDB context: LiteDB's global `BsonMapper` still has a first-use race that the documented warm-up narrows but does not close — see *Per-integration databases*, which carries the captured stack trace. Anything else intermittent is yours.

`MyDogsbody.Startup.fsproj` pins `Microsoft.Extensions.DependencyInjection.Abstractions` explicitly — keep that pin at or above whatever `FluentMigrator` (via `MyDogsbody.Database.Migrations`) resolves to. Falling behind turns into an `NU1605` package-downgrade error on the WPF host (`MyDogsbody.csproj`) specifically — `dotnet build MyDogsbody.sln` only reports it as a warning, so it can look harmless until someone runs the app.

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
- **Unlogged-failure path** — where the function builds a `MyDogsbodyException` wrapping an `ApplicationException` (the expected-failure idiom, e.g. `PdfDocumentReader.readContent` on a missing file), pass a `HandleErrorBuilder` whose callback records invocations and assert nothing was logged.

Know where the seams are. In `MyDogsbody.Domain` every dependency is substitutable by construction — that is what a function type buys, and it is why the domain suite needs no fixtures at all. In the outer ring the seam is the collection getter: anything bottoming out at `ILiteCollection<T>` is an **integration** test unless you hand-fake the collection, and a getter that raises is how "the store is unreachable" is simulated. Cleanly unit-testable outside the domain: `SupplierRecordMappers` / `SupplierApiMappers` and `GoogleCredentialEntityMappers` (pure), `PdfDocumentReader`'s missing-file path (file-path seam), and `ModuleCreators` (takes the API record and `startWork` as parameters).

### Integration

Covers the outer ring against real storage: adapters/repositories, `*DatabaseContextModule.getDatabaseContext`, `DatabaseContextSetup.createDatabaseContext`, the migrations, and a workflow run with real adapters bound to it (the composition root's job, exercised through `*ApiFactory`). Domain workflows with fake dependencies are **unit** tests, not these — if a test needs a temp file to exercise a rule, the rule is in the wrong ring.

**Main database (SQLite):**

- Fresh temp file per test — `Data Source={Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")}`.
- **Build the schema by calling `MigrationSetup.setupMigrations` against that connection string.** Never hand-write DDL in a test: the migrations are the schema source of truth, so a test that creates its own tables stops proving they match. That makes every SQLite integration test a migration test too.
- `createDatabaseContext` constructs a `SqliteConnection` and never disposes it. Dispose it in the test (`GetDatabaseConnection()` hands it back) — Microsoft.Data.Sqlite pools connections, and a pooled handle can keep the temp file locked on Windows so cleanup fails.
- Migrations get their own tests: `MigrateUp` on an empty file produces the expected tables and columns, and `Down()` reverses it. Nothing else verifies a migration before it runs against real data.

**LiteDB stores (each integration's, and the log store):**

- Fresh temp database per test — `Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")`, `connection=direct`, deleted in `try/finally`. `GoogleCredentialStoreTests.withStore` demonstrates the shape. Production uses `shared`; if a change touches concurrent access, pin that mode explicitly.
- **Never let a test reach `Startup.Startup`.** Its module-level `let` bindings open `Logging.db`, `MyDogsbody.db` and `Thunderbird.db` (and run its migrations) in the process working directory the moment anything in the module is touched. That is why the composition root is split three ways per API: test `SupplierApiMappers` and `SupplierApiFactory` (and the other API pairs the same way) — all free of module-level I/O and taking their dependencies as parameters — and leave `Startup.fs` alone. Keep any new composition the same shape.
- **Both context records carry a `Dispose`**, so call it before deleting the temp file — Windows keeps a LiteDB file locked until the handle is released. `GoogleDatabaseContextModuleTests` asserts the delete actually succeeds afterwards.
- Required round-trips: insert → `getAll` returns the row with `ObjectId` surfaced as the `Id` string; update → re-read reflects the new values.
- `MyDogsbody.Tests` **does** reference `MyDogsbody.Logging`, because the log store's repository and use cases are tested against a temp file like any other LiteDB store. Nothing reaches the `Logging.db` in the working directory; a test that wants to prove *whether something was logged* passes a recording `HandleErrorBuilder` instead.

### Contract

- **Dependency function types** are this architecture's published interfaces, so CLAUDE.md's shared-suite rule applies to each one. The failure it catches here: a fake returning shapes the real store never produces, leaving a workflow's unit suite green over code that cannot work in production.
- **`SupplierApi`** and every other API record (`MyDogsbody.UI.Types`): a record of functions, so a fake is a record literal rather than a class. Same rule — the suite that exercises the real API also runs against the fakes the UI tests use. `SuppliersBrowserModuleCreatorsTests` shows the fake shape, and `Contracts/GoogleCredentialDependencyContractTests.fs` is the reference example of one shared suite run against a real adapter and an in-memory fake.
- **The two boundary mappers** (see *Architecture → The two mapping points*), asserted field-for-field in both directions. There is no chain test to write — assert each one exhaustively instead, and assert that a constrained type survives the round trip through the store's `string` column unchanged.
- **Error translation at the composition root**: assert each domain error DU case maps to the intended `MyDogsbodyException` (action, message), and that an adapter exception maps to the intended domain error case. This pair is the seam the UI's alerts are written from.
- **`ActionNames`**: the strings are `$"..."`-composed and compiler-unchecked — assert each outer-ring function's error reports its declared action. A typo is otherwise invisible until someone reads the exception log.
- **LiteDB entity shape**: LiteDB is schemaless, so renaming a property on `GoogleCredential`/`ExceptionLog` silently orphans stored data. Assert the persisted document's field names, not just the round-tripped object — `Contracts/PersistedShapeTests.fs`, `Contracts/GoogleCredentialPersistedShapeTests.fs`.
- **`ActionNames` is also asserted structurally**, not only per function: `Contracts/ActionNamesTests.fs` walks the nested modules by reflection and requires every string to end with the name of the binding that declares it, and no two bindings to declare the same string. Those two rules are what catch a truncated or copy-pasted entry — both existed in the file before the suite did.

### E2E

Composition root down: component/page → API record → workflow → adapter → a real database file (SQLite for suppliers, LiteDB for mail accounts) → back into the adaptive state the component renders.

- **The harnesses exist**, one per E2E area: `E2E/SuppliersTestHarness.fs`, `E2E/MailAccountsTestHarness.fs`, `E2E/InvoicesTestHarness.fs`. Each subclasses bUnit's `TestContext`, registers `AddMudServices()`, and sets `JSInterop.Mode <- JSRuntimeMode.Loose` — MudBlazor calls into JS for popovers and key interception during render, and none of that is under test. Use `withSuppliersHarness` for the happy paths and `withUnreachableSupplierStoreHarness` for failure paths. (`E2E/BlazorTestHarness.fs` was deleted by `credentials-per-provider` — it had turned out to be entirely credentials-specific.)
- **Fun.Blazor views are `NodeRenderFragment`, not Blazor `RenderFragment`.** They reach bUnit through `FunFragmentComponent` with the view passed as its `Fragment` parameter; `SuppliersFlowTests.renderBrowser` shows the three lines involved.
- Module creators take `startWork`, so an E2E test passes `fun work -> work ()` and asserts against `rendered.Markup` without waiting on a thread. Use `rendered.WaitForAssertion` so the re-render after a write is awaited.
- Covered flows (suppliers): add supplier → the row appears; edit supplier → the table shows the new values; delete supplier → the row disappears; validation failure → `MudAlert` shows the message **and nothing is logged**; store failure → `MudAlert` shows the message **and exactly one entry is logged**; a success after a failure clears the alert.
- **Assert "was it logged?" through a recording `HandleErrorBuilder`**, not by opening `Logging.db`. `writeLog` is a lambda, so the harness collects what it was handed — no log file, no logging reference needed in the flow test.
- Driving the real WPF `BlazorWebView` window is out of scope for the suite. To exercise the real `Startup.fs` bindings by hand, build a throwaway console project that references `MyDogsbody.Startup` and calls `Startup.supplierApi`; that runs the genuine composition root against real `.db` files in its own working directory without putting `Startup` in the test suite's reach.

### Where tests go

- `MyDogsbody.Tests` is the only test project. Add each new `.fs` to `MyDogsbody.Tests.fsproj` **above `Program.fs`** — it carries the `[<EntryPoint>]` (`GenerateProgramFile=false`) and must stay last in compile order.
- Mirror the source layout: `Domain/<Area>/`, `Startup/`, `Integrations/Google/`, `Integrations/Documents/`, `Integrations/Thunderbird/`, `Logging/`, `Database/`, `UI/ModuleCreators/`, `Contracts/`, `E2E/`.
- The test project references `Builders`, `Database`, `Database.Migrations`, `Domain`, `Exceptions.Types`, `Integrations.Documents`, `Integrations.Google`, `Integrations.Thunderbird`, `Logging`, `Startup`, `UI.Portal` and `UI.Types`, plus the `bunit` and `MudBlazor` packages. Add the `ProjectReference` for anything else you test in the same change.
- A shared contract suite is a `[<Theory>]` over `[<MemberData>]`, with the implementation chosen by name — see `Contracts/GoogleCredentialDependencyContractTests.fs`. **The `MemberData` source must be a public `let`**, not `let private`: xUnit resolves it by reflection on the compiled class and a private binding fails at run time, not compile time.

## Architecture

**Functional onion**, after *Domain Modeling Made Functional* (Scott Wlaschin, Pragmatic Bookshelf, 2018). Everything below is self-contained — the shapes to copy are written out here rather than cited.

### Why this shape, and not another

It was chosen to close the two defects in the *Status* table below — the DTO hops and the store in a core signature. Everything else on the table was weighed and set aside:

| Considered | Why not |
| --- | --- |
| **Dependency rejection + parameterization** — fetch–think–save, a.k.a. functional core / imperative shell | Not a rival: it is the technique this architecture contains. Its whole benefit arrives with the onion, so nothing about it is skipped |
| **MVU / Elmish** | Helps only the screen, for defects that are already fixed. See *UI* |
| **Reader monad** | Wlaschin advises against it "unless you can see a clear benefit over the other techniques", and it gets awkward once several effects are in play |
| **Free monad / tagless final** | Roughly 100+ lines of scaffolding for a small instruction set, and the F# material is thin and advanced |
| **Vertical slice architecture** | A code-organisation pattern from the object-oriented .NET world; its literature assumes C# web APIs. The good part — a feature's code grouped together — comes free with workflow pipelines |
| **Clean / hexagonal, classic form** | Object-oriented at its core: interfaces, injected classes, containers. This reaches the same goal with functions instead |

### Status — specified vs built

The migration is **done**. The `architecture-compliance` change created `MyDogsbody.Domain`, moved both paths onto workflow pipelines, and deleted `MyDogsbody.Spine`:

| | Specified | Built today |
| --- | --- | --- |
| `MyDogsbody.Domain` | The centre of everything | ✅ exists, references nothing, holds `Documents/`, `Suppliers/`, `InvoiceTemplates/`, `Invoices/` and `MailAccounts/` |
| `MyDogsbody.Spine` | Gone — pipeline moves to `Domain`, wiring to `Startup` | ✅ deleted |
| DTO hops per feature | 2 mappers, both at the edges | ✅ 2 per area — e.g. `SupplierRecordMappers`/`SupplierApiMappers`, `TemplateRecordMappers`/`TemplateApiMappers`, `ThunderbirdEntityMappers`/`MailAccountApiMappers` |
| Domain error type | A DU per workflow area | ✅ `DocumentError`, `SupplierError`, `MailAccountError`, `InvoiceError`, and the rest |
| Store in a core signature | Never | ✅ never — the domain names no store type |

What is **not** built, and is a status rather than a violation: only the error log type exists in the log database (see *Log database*). The main SQLite database's own "designed but not wired in" gap closed with `invoice-ledger-foundation` — see *Storage → Main database*.

So: **write new work in the shape below**, and don't reintroduce a layer chain to "match the existing code" — there is no longer any existing code in that shape to match.

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

  UI.Portal ── SupplierApi (UI types) ──── Startup     ← the UI reaches no further
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

`create` returns the plain reason for failure; the workflow decides what to call it.

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

- `result` is the domain's own generic builder in `MyDogsbody.Domain/Result.fs` — see *Errors* below for why it cannot be `handleError`. Write it by copying `MyDogsbody.Builders/HandleErrorBuilder.fs` and taking two things out: the `writeLog` constructor parameter, and the `MyDogsbodyException` annotations on `Bind` and `TryWith` that pin its error type. What is left — `Bind`, `Return`, `ReturnFrom`, `Zero`, `Delay`, `Run` — is the whole builder. If a workflow ever needs `asyncResult`, take the **FsToolkit.ErrorHandling** package rather than hand-writing that as well.
- One workflow per file, named `<Workflow>Workflow.fs`, exposing one public function.
- Steps that are pure decisions stay private functions in the same file.
- **No I/O in the file.** `loadAccounts` performing a query is invisible here on purpose — the workflow sees a function value.

### The two mapping points

The layered design had five mappers, one per hop. This one has **two, both at the edges**, and domain types travel unmapped everywhere between:

| Edge | Lives in | Maps |
| --- | --- | --- |
| Bottom | the integration's store | LiteDB entity (C#, `ObjectId`) ⇄ domain type |
| Top | `Startup/*ApiMappers.fs` | domain type ⇄ `MyDogsbody.UI.Types` record |

The top mapper is a deliberate choice, and the book does not require it: a workflow's read/summary type could be handed to the UI directly, saving a mapper, but that would put `MyDogsbody.Domain` in `UI.Portal`'s reference graph. Keeping the UI on its own records is what makes the domain *unreachable* from the screen rather than merely unused there — the same property the `startup-composition-root` change bought, and the same reason `SupplierApi` was never widened to take a domain type. One mapper is the price; pay it.

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
    let action = ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.insertOne
    handleError {
        try
            return ...
        with ex ->
            return! MyDogsbodyException(action, "Failed to insert new credential.", ex)
    }
```

The `with` branch *yields* the wrapped exception rather than raising it: the builder's `TryWith` takes it, calls `writeLog`, and returns `Error`. So the message + `ActionName` you supply are what gets persisted.

- `action` always comes from `MyDogsbody.Exceptions.Types/ActionNames.fs` — nested modules mirroring the code path, built from `$"..."` string composition. Add an entry there when you add an outer-ring function; don't inline literals. **Domain workflows have no `ActionName`** — their errors are DU cases, which need no string.
- `ExceptionHelpers.isApplicationException`: a `MyDogsbodyException` whose `InnerException` is an `ApplicationException` passes through **unlogged**. That's the idiom for expected/validation failures in the outer ring — see `PdfDocumentReader.readContent` constructing one for a missing file. In the domain the same idea is free: an expected failure is just a DU case and was never an exception.
- `writeLog` is wired in `Startup/Startup.fs` to `MyDogsbody.Logging`, which inserts an `ExceptionLog` into the `Exceptions` collection of `Logging.db` — the separate log database, never the main one (see *Storage → Log database*).
- **Never raise as control flow, in either ring.** Exceptions are caught at the boundary and converted.
- **Not every failure earns a DU case.** A domain error case is for a failure the domain *expects* and a person could name out loud — "that address is already linked". Bugs, violated invariants and infrastructure collapse are not that: they stay exceptions, get caught at the outer-ring boundary, and arrive as a `MyDogsbodyException` with a stack trace worth reading. A `*Error` DU that tries to enumerate everything that could go wrong becomes unreadable and stops being matchable.

### Logging is cross-cutting, not an integration

An integration is an *adapter for a capability the domain declares a need for*: it implements a dependency function type, a workflow calls it, and its data flows out through the composition root into the UI. Documents and Thunderbird are integrations. **Logging is none of that** — it is infrastructure the outer ring uses to say what happened, and no workflow ever declares a need for it.

What follows from that, and what to keep true:

- **It is not a dependency function type.** No `WriteLog` appears in a `*Types.fs` next to `LoadAccounts` and `SaveAccount`. The domain does not log — it returns a `*Error` DU case and the composition root decides what that is worth recording. Adding a log type never adds a workflow parameter, a domain type, or a mapper.
- **It is not passed as a parameter.** Every other store arrives as a leading getter (`getCredentialCollection`); the log store never does. Outer-ring functions receive `handleError` — already closed over `writeLog` at the composition root — and that is their entire access to logging. **Do not add a `getExceptionCollection` (or any log-collection) parameter to any function, in either ring.**
- **`MyDogsbody.Domain` cannot reference it**, which the reference graph already guarantees. Its only non-scratch consumers are `MyDogsbody.Startup`, which wires it into `writeLog`, and `MyDogsbody.Tests`, which exercises the log store itself against a temp file. No workflow, adapter or component reaches it — anything wanting to record a failure returns it and lets `handleError` decide.
- **It holds no domain data** and nothing reads from it at runtime — see *Storage → Log database*.
- **Its data never flows back to the user.** A future log viewer reads `Logging.db` through its own read path; it does not become a workflow dependency and it does not turn logging into an integration.

**The names say so too.** The projects were `MyDogsbody.Integrations.Logging` / `.Integrations.Logging.Database.Models` until the `logging-not-an-integration` change moved them to `MyDogsbody.Logging` / `MyDogsbody.Logging.Database.Models`. Nothing about logging is under `MyDogsbody.Integrations.*` any more, and nothing new should go there — rules written for "every integration" are not written for this.

### Composition root

`MyDogsbody.Startup` is where the abstract meets the real: it picks the actual functions that satisfy each dependency type, hands them to the workflows, and translates the two error types. It is the **only** place that knows both LiteDB and the domain. Split into three files on purpose:

| File | Holds | I/O at module init |
| --- | --- | --- |
| `SupplierApiMappers.fs` | domain type ⇄ UI record, plus `toMyDogsbodyException` / `toSupplierError`. Total functions | none |
| `SupplierApiFactory.fs` | `createSupplierApi handleError databaseContext` — adapters bound to dependency types, workflows partially applied, `Result.mapError` both ways | none |
| `Startup.fs` | database paths, the LiteDB and SQLite contexts, `handleError`, `supplierApi`, `registerServices` | opens `Logging.db`, `MyDogsbody.db` and `Thunderbird.db` |

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

`Startup.fs` holds module-level `let` bindings created once per process. Paths are relative, so the `.db` files land in the process working directory — the repository root under the documented `dotnet run` invocation (*Commands → Run*), not `bin\Debug\net9.0-windows\`. **Everything with behaviour worth testing belongs in the other two files** — that split is what makes the composition root testable at all, so keep it when adding a second API.

**`Result` is not collapsed here.** `SupplierApi` returns `Result<_, MyDogsbodyException>` and the UI decides what a failure looks like (`SuppliersBrowserModule.ErrorAval` → `MudAlert`). Do not reintroduce `|> ignore` on writes or `failwith` on reads.

Adding a feature: declare its types, dependency function types and workflows in `MyDogsbody.Domain`; write the adapters in the integration; declare the API record in `MyDogsbody.UI.Types`; bind it all in a `*ApiFactory.fs`; partially apply in `Startup.fs` and register in `registerServices`. `MainWindow.xaml.cs` should not change. The DI container there exists only to hand the UI an API record.

**One sanctioned exception:** the WPF host now also supplies a `FolderPicker` (`unit -> string option`, `MyDogsbody.UI.Types`) — the domain never opens dialogs, so a folder-choosing capability cannot be built at the composition root the way an API record is. `MainWindow.xaml.cs` constructs one from `Microsoft.Win32.OpenFolderDialog` and registers it directly, alongside (not instead of) `Startup.registerServices`. This was the `thunderbird-account-selection` change's one change to the host, and it should stay that — one function, one registration. Two F#/C# interop traps worth knowing before touching this again: `FolderPicker` is a type *abbreviation*, erased at compile time, so C# has no `FolderPicker` type to reference — register under the erased CLR type instead (`FSharpFunc<Unit, FSharpOption<string>>`), which is what the F# side's `html.inject (fun (picker: FolderPicker) -> ...)` actually resolves against; and a function returning another function (`Func<string> -> FolderPicker`) compiles as a curry-flattened multi-argument CLR method unless built via `FSharpFunc<_,_>.FromConverter` — see `MyDogsbody.UI.Types/FolderPicker.fs`.

### UI (Fun.Blazor + MudBlazor + FSharp.Data.Adaptive)

**The UI stays adaptive. This is settled, not pending.** `cval`, `aval`, `transact` and `adapt { }` are the state model, and the onion above stops at the composition root — it does not reach into the screen. MVU/Elmish was evaluated and **rejected**: it addresses only the UI, the problems it prevents were already fixed by hand in the `startup-composition-root` change, and adopting it would mean a second state paradigm for no defect it currently closes. Do not introduce `Model`/`Msg`/`update`, `Cmd`, `Elmish`, or a dispatch loop. If a page's adaptive state is getting unwieldy, the answer is a better module creator, not a message type.

What the onion *does* ask of the UI is unchanged from today: the screen talks to an API record of functions speaking `MyDogsbody.UI.Types`, and reaches no further.

- `Shell.fs` wires an `ErrorBoundary'` around `html.route [...]`, wrapped in `MainLayout.view`. Each page module exposes `getView()` and `getRoute()` (`routeCi "/settings/suppliers"`); register new pages in `Shell.fs`. Settings pages pipe their view through `SettingsComponents.settingsNavMenu`.
- Services are obtained per-view with `html.inject (fun (supplierApi: SupplierApi, dialogService: IDialogService) -> ...)`.
- **State pattern:** `UI.Types/Modules/*.fs` declares a record of `aval<_>` fields + commands (`SuppliersBrowserModule`); `UI.Portal/ModuleCreators/*.fs` builds it from `cval` + `transact` over the API functions; components take the module and render inside `adapt { let! x = ... }`. Components never call the API directly — pages pass callbacks in.
- **Module creators take `startWork: (unit -> unit) -> unit` as their first parameter.** Production passes `fun work -> Async.Start(async { work () })`; a test passes `fun work -> work ()` and never waits on a thread. Don't call `Async.Start` inside a module creator.
- **A write reloads.** Every command that changes stored data calls the load function on success, so the table shows what was stored rather than what the dialog held. Failures set `ErrorAval`; a later success clears it.
- **Dialogs must be classes**, not functions: inherit `FunComponent`, expose `[<Parameter>]` members and a `[<CascadingParameter>] IMudDialogInstance`, and show via `dialogService.ShowAsync<T>(title, DialogParameters<T>, DialogOptions)`. See `SuppliersComponents.SuppliersEditorDialog` / `showSuppliersEditorDialog`; recent commits deliberately moved away from function-style dialogs.
- The WPF side is thin: `Frame.razor` supplies the MudBlazor providers and the dark "Grey 90 Carbon" theme and renders `<Shell />`; `MainWindow.xaml` points `BlazorWebView` at `wwwroot/index.html`.

### Storage

Three tiers, kept deliberately separate: one main database, a private store per integration, and one log database.

#### Main database — SQLite

`MyDogsbody.Database` is the application's main store. `DatabaseContextSetup.createDatabaseContext databaseFilePath` registers Dapper.FSharp's `OptionTypes`, opens a `SqliteConnection` (`Data Source={path};Foreign Keys=True` — the connection-string keyword, not a runtime `PRAGMA`, so it self-applies on every open regardless of who opens the connection) and returns a `DatabaseContext` record of getters — `GetDatabaseConnection`, plus one `unit -> QuerySource<'T>` per table, each bound to its table name via `table'<Blog> "Blogs"` — and a `Dispose` that closes the connection. Models are F# `[<CLIMutable>]` records in `MyDogsbody.Database.Models`. Same context-record-of-getters shape as the integrations use, so it partially applies into an outer-ring function the same way — and stops at the same boundary: a `QuerySource<'T>` is no more allowed in a domain signature than an `ILiteCollection<T>` is.

**The schema belongs to `MyDogsbody.Database.Migrations` and nothing else.** The FluentMigrator classes under its `Migrations/` folder are the source of truth: never create or alter a table at runtime, from `DatabaseContextSetup`, or by hand in a SQLite tool. Adding a column means adding a migration. `MigrationSetup.setupMigrations` wires `AddSQLite` + `ScanIn(...).For.Migrations()` and calls `MigrateUp()`; CLI equivalent under *Commands → Migrate*. **FluentMigrator's SQLite generator refuses `Create.ForeignKey` outright** ("Foreign keys are not supported in SQLite") because SQLite has no `ALTER TABLE ADD CONSTRAINT` — a foreign key has to be declared inline in `CREATE TABLE`, which the fluent `Create.Table()` builder cannot express either. `Migration_20260809000002_CreateSupplierMatchersTable.fs` is the pattern to copy: `this.Execute.Sql("CREATE TABLE ... FOREIGN KEY (...) REFERENCES ... ON DELETE CASCADE")` for the table, then the fluent builder again for anything that doesn't involve the constraint (indexes, `Down()`).

**A migration may now carry seed data as well as structure.** `Migration_20260810000007_CreateScanWindowsTable.fs` (the `invoice-extraction` change) is the first: `Insert.IntoTable("ScanWindows").Row(box {| Days = d |})` for the five seeded windows in `Up()`, a matching `Delete.FromTable(...).Row(...)` in `Down()`. Consequence, accepted knowingly: if a user deletes a seeded row, re-running migrations does **not** restore it (FluentMigrator never re-runs a version already in `VersionInfo`) — which is why `ScanWindowDays.fallback` exists rather than the code assuming 14 is present. Every migration before that one created schema and nothing else; copy the one you actually need.

Status: the main database is **wired in as of `invoice-ledger-foundation` (change #1 of the invoice-to-calendar series)**, with suppliers as its first real consumer end to end — migrations, `SupplierStore.fs`, `SupplierApiFactory.fs`, and `/settings/suppliers`. `Startup.fs` calls `MigrationSetup.setupMigrations` before constructing the context, exactly the pattern below always intended. `Blog`/`Comment` remain scaffold sample tables, deliberately untouched by that change — treat them as the shape to follow, not as finished schema, and the next feature needing the main database adds its own migrations, store functions and dependency-type bindings the same way `Suppliers`/`SupplierMatchers` did — never reaching it from the UI, and never handing a `DatabaseContext` inward.

Two things worth knowing before adding a second table pair:

- **`InsertAsync`/`UpdateAsync`/`DeleteAsync`/`SelectAsync` on `Dapper.FSharp.SQLite`'s `IDbConnection` are async-only** — there is no synchronous overload. `SupplierStore.fs`'s private `runSync` (`Async.AwaitTask >> Async.RunSynchronously`) is the bridge every store function uses to keep the established synchronous `Result<'T, MyDogsbodyException>` outer-ring shape; copy it rather than inventing another one.
- **Dapper.FSharp's `excludeColumn`/`includeColumn` custom operations don't resolve inside an `insert { }` block that has no `for x in table do`** — the CE rewrites the selector against a bound loop variable that isn't there, and the compiler error (`Expression<Func<'0,'1>>` vs. a stray `unit -> ...`) doesn't point at the real cause. `SupplierStore.fs` inserts via plain parameterised Dapper SQL instead (`insertSupplierRow` / `insertMatcherRow`), folding a `SELECT last_insert_rowid()` into the same command text as the `INSERT` so the assigned id comes back in the same round trip regardless of the connection's open/closed state. Reuse that shape for a new identity-column insert rather than re-fighting the CE.

#### Per-integration databases — LiteDB today

Each integration owns its own database file, separate from the main database and from every other integration: a context record of `unit -> ILiteCollection<T>` getters (`ThunderbirdDatabaseContext`, `GoogleDatabaseContext`) built by a `*DatabaseContextModule.getDatabaseContext databasePath connectionType`. Entities are mutable **C# classes** in the `*.Database.Models` C# projects because LiteDB needs settable properties / `ObjectId`; F# adapters construct and mutate those instances.

**The collection getter stops at the integration boundary.** `unit -> ILiteCollection<T>` is how a store function receives its handle, and it goes no further inward — it never appears in a `MyDogsbody.Domain` signature, and the domain never names `ILiteCollection`, `LiteDatabase`, `ObjectId` or `BsonValue`. What the domain declares is a function type (`LoadSuppliers`, `SaveSupplier`, `UpdateSupplier`); the store's job is to satisfy one, mapping the entity to a domain type on the way out. (`GoogleCredentialStore` is the exception that proves the rule: a credential is not a domain concept, so it satisfies no domain type at all — its `ValidGoogleCredential` / `StoredGoogleCredential` live in the integration.) The layered code used to violate this by taking `unit -> CredentialsCollection` in a core signature — that leak is exactly what this architecture was chosen to close, and it is now closed.

**Every `*DatabaseContextModule.getDatabaseContext` must warm LiteDB's entity mapping before it returns**, with `BsonMapper.Global.ToDocument(TheEntity()) |> ignore`. LiteDB caches the mapping on a global `BsonMapper` and builds it lazily on first use, so two threads mapping the same entity for the first time at once can observe a half-built mapping and silently drop a property — a row round-trips with a null where a value was written. The UI calls the API from `Async.Start` threads and `writeLog` runs on whichever thread failed, so this is reachable in production, not only in parallel tests. It was found as a 6-in-10 intermittent test failure while the `architecture-compliance` change was being written, and the warm-up made that failure much rarer. Do the same for any new context. (The Google context added by `credentials-per-provider` warms a *local* `BsonMapper` rather than `BsonMapper.Global` — same warm-up, private mapper — which also keeps it clear of the global race described next.)

**But the warm-up does not close the race, and this paragraph used to claim it did.** PR #11's third review round captured the intermittent failure the suite has kept showing since: `System.InvalidOperationException: Collection was modified; enumeration operation may not execute` out of `BsonMapper.SerializeObject`, raised **at the warm-up line itself** (`CredentialsDatabaseContextModule.fs:16`, in the store `credentials-per-provider` later removed — the same warm-up now lives in `ThunderbirdDatabaseContextModule.fs` and the log store's own context module, while `GoogleDatabaseContextModule.fs` runs it on a *local* `BsonMapper` the global race cannot reach). `BsonMapper.Global` publishes an `EntityMapper` before its member list is filled, so two threads reaching it for the first time still collide — the warm-up has simply become the place they collide, since it is the first thing every context construction runs. Keep writing it (it still serialises the common path and it is what the log store's own contexts rely on), but know that:

- **Production is not exposed the way the paragraph above implies.** `Startup.fs`'s module-level bindings construct all three contexts sequentially on one thread, so every warm-up completes before any `Async.Start` thread exists. The race needs two contexts under construction at once, which today only the parallel test suite does.
- **The real fix is a process-wide lock** around the warm-up — or one static initialiser warming every entity under a single lock — not a per-context `ToDocument` call. That wants its own change folder; nothing in the `invoice-templates` series touches it.
- So an intermittent failure in a test that constructs a LiteDB context is most likely this, and it is still not a licence to re-run until green. Note which test, and say so in the change description.

Each context record also carries a `Dispose` that closes the underlying `LiteDatabase`. Production opens one per process and never disposes it; tests dispose every one they open, which is what lets them delete their temp file rather than leaving it locked.

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
2. One F# record in `MyDogsbody.Logging/Types/`, next to `ExceptionLogEntry` — **one per log type, not one per layer.** The repository and the use case share it.
3. A `unit -> ILiteCollection<T>` getter on `LoggingDatabaseContext`, bound in `LoggingDatabaseContextModule.getDatabaseContext`, and a `BsonMapper.Global.ToDocument` warm-up for the new entity beside the existing one.
4. A repository function and a use case, both taking `handleError` first and returning `Result<_, MyDogsbodyException>`, mirroring `ExceptionRepository` / `ExceptionUseCases`.
5. An `ActionNames.MyDogsbody.Logging.*` entry per function.
6. Partial application in `Startup/Startup.fs`.

Status: **only errors are implemented.** `writeLog` is the sole writer and `ExceptionLog` has no severity field — correctly, under the rule above. Note that `Startup.fs` gives the log write a **separate no-op `handleError`**: handing a failed log write back to `writeLog` would recurse, so the cycle is broken there and the `Result` is discarded at that one call site, with a comment saying why. Adding warnings or information means adding collections to this database; it does **not** mean a second log database, a severity column, or borrowing the main one.

### Conventions

- New `.fs` files must be added to the `.fsproj` `<Compile Include>` list **in dependency order** — F# compile order is significant and there is no glob.
- **`MyDogsbody.Domain` folder shape: one folder per workflow area, named for the area, not the layer.** Inside it, `<Area>Types.fs` first (constrained types, stage types, the error DU, the dependency function types), then one `<Workflow>Workflow.fs` per workflow. `Result.fs` sits at the project root, compiled first.
- **Do not create `Domains/Types`, `UseCases/Types` or `Repositories/Types` folders.** That is the per-layer shape this architecture replaced, and no project still has one. Inside an integration, adapter code goes beside `Database/`, named for what it talks to (`GoogleCredentialStore.fs`, `PdfDocumentReader.fs`, `ThunderbirdStore.fs`).
- Migrations are `Migrations/Migration_<timestamp>_<Name>.fs` holding one `[<Migration(<same timestamp>L)>] type <Name>()` with `Up()` and `Down()` — e.g. `Migration_20251104000001_CreateBlogTable.fs` / `CreateBlogTable`. Add each to `MyDogsbody.Database.Migrations.fsproj` in timestamp order, above `MigrationSetup.fs`. Never edit a migration that has been applied; write a new one.

### Naming quirks (grep both spellings)

The `Domian` misspelling is **gone** — it lived only in `Spine` and the DTO hops, which no longer exist. `GetInfrustructureCredentialCallback` (later `OnCredentialSubmitted`) is gone too — it lived in the credentials UI, which `credentials-per-provider` removed. Neither spelling needs grepping any more.

The `MyDogsbody.Infrastructure.Google.GoogleCalendar` module name is also gone: `credentials-per-provider` made `MyDogsbody.Integrations.Google` a real project and corrected its module to `MyDogsbody.Integrations.Google.GoogleCalendar`.
