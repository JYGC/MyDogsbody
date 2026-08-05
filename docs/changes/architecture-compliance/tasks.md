# Tasks — Architecture compliance

**Status: complete.** All 12 phases and all four optional items are done; results, behaviour
changes and the four specification-conflict resolutions are in [outcome.md](outcome.md).

Requirements in [requirements.md](requirements.md), design in [design.md](design.md).

**Unit tests land before the implementation they cover, per task.** Where a phase has a "tests
first" group, those tasks must be written and observed failing — for the reason expected, not
merely failing to compile against a missing module — before the implementation group starts.

**Every phase ends at a green build.** `dotnet build MyDogsbody.sln` after each phase; the full
suite must also pass from Phase 3 onwards. The migration is never left half-applied.

Each new `.fs` goes into its `.fsproj` `<Compile Include>` list in dependency order, and every test
file goes into `MyDogsbody.Tests.fsproj` **above `Program.fs`**.

---

## Phase 0 — Domain project (required)

- [x] **0.1** Create `MyDogsbody.Domain/MyDogsbody.Domain.fsproj`, `net9.0`, with **no**
      `ProjectReference` and **no** `PackageReference`. Add it to `MyDogsbody.sln`.
      *Outcome:* the centre exists and is empty.
- [x] **0.2** Add `MyDogsbody.Domain` `ProjectReference` to `MyDogsbody.Tests.fsproj`.
      *Depends on:* 0.1.
- [x] **0.3** `Tests/Domain/ResultTests.fs` — `result` returns, binds an `Ok`, short-circuits an
      `Error` without evaluating the continuation, and compiles against two different error types.
      *Outcome:* fails; `Result.fs` does not exist.
      *Depends on:* 0.2.
- [x] **0.4** `MyDogsbody.Domain/Result.fs` at the project root, compiled first — `Bind`, `Return`,
      `ReturnFrom`, `Zero`, `Delay`, `Run`. No `TryWith`, no `writeLog`, no error-type annotation.
      *Depends on:* 0.3.
- [x] **0.5** `Tests/Contracts/DomainIsolationTests.fs` — parse `MyDogsbody.Domain.fsproj` and
      assert it declares no `ProjectReference`.
      *Outcome:* the invariant is enforced by a test, not by memory.
      *Depends on:* 0.1.

## Phase 1 — Credentials domain (required)

### Tests first

- [x] **1.1** `Tests/Domain/Credentials/CredentialsTypesTests.fs` — `CredentialSecret.create`,
      `ExternalUsername.create`, `CredentialId.create`: one accepted value each; null, empty and
      whitespace rejected each, **with the reason asserted**.
      *Depends on:* 0.4.
- [x] **1.2** `Tests/Domain/Credentials/AddCredentialWorkflowTests.fs` — Ok path asserting **every
      field** of the returned `StoredCredential`, unwrapped with each type's `value`; one test per
      validation failure asserting the exact case *and payload*; a recording `SaveCredential` proving
      the store is never reached when validation fails.
      *Depends on:* 1.1.
- [x] **1.3** `Tests/Domain/Credentials/EditCredentialWorkflowTests.fs` — Ok path, every field;
      `CredentialNotFound` carrying the submitted `CredentialId` when no stored credential matches;
      each validation failure; recording fakes proving `UpdateCredential` is never invoked when the
      identifier is absent or validation fails; **two stored credentials sharing an infrastructure
      type, only the submitted one updated**.
      *Depends on:* 1.1.
- [x] **1.4** `Tests/Domain/Credentials/ListCredentialsWorkflowTests.fs` — every field of every
      returned credential; empty store returns `Ok []`; store error propagated as its own case.
      *Depends on:* 1.1.

### Implementation

- [x] **1.5** `MyDogsbody.Domain/Credentials/CredentialsTypes.fs` — `Infrastructure` DU, the three
      constrained types, the five stage types, `CredentialError`, and the three dependency function
      types. Shapes in design.md → *Data models and interfaces*.
      *Depends on:* 1.1.
- [x] **1.6** `MyDogsbody.Domain/Credentials/AddCredentialWorkflow.fs` — one public function.
      *Depends on:* 1.2, 1.5.
- [x] **1.7** `MyDogsbody.Domain/Credentials/EditCredentialWorkflow.fs` — one public function;
      loads, decides not-found, then updates.
      *Depends on:* 1.3, 1.5.
- [x] **1.8** `MyDogsbody.Domain/Credentials/ListCredentialsWorkflow.fs` — one public function.
      *Depends on:* 1.4, 1.5.

## Phase 2 — Credentials adapter (required)

- [x] **2.1** Add `MyDogsbody.Domain` `ProjectReference` to
      `MyDogsbody.Integrations.Credentials.fsproj`.
- [x] **2.2** `Tests/Integrations/Credentials/CredentialStoreTests.fs` (Integration) — fresh temp
      LiteDB file per test, `connection=direct`, best-effort delete in `finally`: insert → read back
      with a non-empty identifier and every field mapped; update by identifier → re-read reflects the
      new values; update an unknown identifier → `Ok None`; **two credentials sharing an
      infrastructure type → only the addressed row changes**.
      *Depends on:* 2.1, 1.5.
- [x] **2.3** `Tests/Integrations/Credentials/CredentialsDatabaseContextModuleTests.fs`
      (Integration) — the getter returns a collection usable against a temp file.
      *Depends on:* 2.1.
- [x] **2.4** `MyDogsbody.Integrations.Credentials/CredentialEntityMappers.fs` — the **bottom
      mapper**: `Credential` entity ⇄ domain types, both directions, including
      `ExternalUsername` and the `InfrastructureType` ⇄ `Infrastructure` translation by exhaustive
      `match`.
      *Depends on:* 2.1.
- [x] **2.5** `MyDogsbody.Integrations.Credentials/CredentialStore.fs` — `getAll`, `insertOne`,
      `updateOne`, each `handleError`-written, dependencies leading, input last,
      `Result<'T, MyDogsbodyException>` out. `updateOne` matches on identifier and returns
      `Ok None` when no row matches.
      *Depends on:* 2.2, 2.4.
- [x] **2.6** Delete `Repositories/` and `UseCases/` from `MyDogsbody.Integrations.Credentials`,
      including their `Types/` folders and the four DTO records, and remove them from the `.fsproj`.
      *Depends on:* 2.5.

## Phase 3 — Composition root (required)

- [x] **3.1** `Tests/Startup/CredentialApiMappersTests.fs` — rewrite for the new types: UI record →
      `UnvalidatedCredential` / `UnvalidatedCredentialEdit`, `StoredCredential` → UI record, every
      field both directions; `InfrastructureType` ⇄ `Infrastructure` for **every** member.
      *Depends on:* 1.5.
- [x] **3.2** `Tests/Contracts/ErrorTranslationTests.fs` — every `CredentialError` case maps to the
      intended action and message; an adapter `MyDogsbodyException` maps to the intended domain case.
      *Depends on:* 1.5.
- [x] **3.3** Add `ActionNames.MyDogsbody.Startup.CredentialApi` — one entry per API operation.
      Resolution 2 in design.md → *Specification conflicts found*.
      *Depends on:* nothing.
- [x] **3.4** `MyDogsbody.Startup/CredentialApiMappers.fs` — rewrite onto domain types; add
      `toMyDogsbodyException`. Total functions, no module-level bindings.
      *Depends on:* 3.1, 3.2, 3.3.
- [x] **3.5** `MyDogsbody.Startup/CredentialApiFactory.fs` — bind `CredentialStore` functions to
      `LoadCredentials` / `SaveCredential` / `UpdateCredential` with `Result.mapError` inbound;
      partially apply the three workflows; `Result.mapError toMyDogsbodyException` outbound. No
      module-level bindings.
      *Depends on:* 3.4, 2.5.
- [x] **3.6** Rewrite `Tests/Startup/CredentialApiFactoryTests.fs` (Integration) for the new wiring
      against a temp database. **Replace** the two characterization tests pinning the old
      match-on-infrastructure-type and silent-`Ok` behaviour with tests asserting the new
      match-on-identifier and not-found behaviour — see design.md → *Behaviour that changes*.
      *Depends on:* 3.5.
- [x] **3.7** Add `MyDogsbody.Domain` and both integration `ProjectReference`s to
      `MyDogsbody.Startup.fsproj`; remove the `MyDogsbody.Spine` reference. `Startup.fs` changes
      only where the factory's parameters changed.
      *Depends on:* 3.5.
- [x] **3.8** Confirm `MyDogsbody.UI.Portal` and `MyDogsbody.UI.Types` are unchanged by this phase,
      and that neither reaches `MyDogsbody.Domain` directly or transitively.
      *Depends on:* 3.7.

*Gate: the credentials path now runs UI → API → workflow → adapter → LiteDB with two mappers.
`Spine` is still referenced only by `MyDogsbody.Tests` and the scratch `PdfProcessing`.*

## Phase 4 — Documents domain and PDF adapter (required)

- [x] **4.1** `Tests/Domain/Documents/DocumentsTypesTests.fs` — `DocumentPath.create` accepts a
      path, rejects null, empty and whitespace, reason asserted.
      *Depends on:* 0.4.
- [x] **4.2** `Tests/Domain/Documents/ReadDocumentLinesWorkflowTests.fs` — the four grouping cases
      **moved from** `Spine/Domains/DocumentDomainTests.fs` (words joined left to right; lines top
      down; words within tolerance grouped; no words → empty list), minus the `handleError`
      argument; plus reader error propagated, and a recording fake proving the reader is never
      invoked when the path fails validation.
      *Depends on:* 4.1.
- [x] **4.3** `MyDogsbody.Domain/Documents/DocumentsTypes.fs` and
      `MyDogsbody.Domain/Documents/ReadDocumentLinesWorkflow.fs` — the line grouping currently in
      `Spine/Domains/DocumentDomian.fs`, with no `handleError` and no `ActionName`.
      *Depends on:* 4.1, 4.2.
- [x] **4.4** Add `MyDogsbody.Domain` `ProjectReference` to `MyDogsbody.Integrations.Pdf.fsproj`.
- [x] **4.5** `Tests/Integrations/Pdf/PdfDocumentReaderTests.fs` — **Unit:** missing file returns a
      `MyDogsbodyException` wrapping an `ApplicationException`, with the declared action and message,
      and a recording `handleError` proving nothing was logged. **Integration:** a non-PDF file
      returns a logged failure; a PDF generated with PdfPig's `PdfDocumentBuilder` returns its words
      with text and coordinates — this closes the untested Ok path in today's `ReadPdfDomainTests`.
      *Depends on:* 4.4, 4.3.
- [x] **4.6** `MyDogsbody.Integrations.Pdf/PdfDocumentReader.fs` — PdfPig behind
      `ReadDocumentContent`, `handleError`-written. Delete `Domains/` and `UseCases/` and their
      `Types/` folders, and remove them from the `.fsproj`.
      *Depends on:* 4.5.
- [x] **4.7** Delete `Tests/Integrations/Pdf/Domains/ReadPdfDomainTests.fs`, superseded by 4.5.
      *Depends on:* 4.5, 4.6.

## Phase 5 — Retire Spine (required)

- [x] **5.1** Repoint `PdfProcessing/Program.fs` at `ReadDocumentLinesWorkflow` with the real
      reader bound to it. Scratch project, but it must compile.
      *Depends on:* 4.6.
- [x] **5.2** Move the assertions carried by `Tests/Contracts/CredentialHopChainTests.fs` onto their
      replacement boundaries — persisted field names and the `Username` → `ExternalUsername` rename
      into `PersistedShapeTests` (6.4) and `CredentialBoundaryMapperTests` (6.2), the
      every-infrastructure-type round trip into `PersistedShapeTests` — **then** delete the file.
      Not before, not after, per `CLAUDE-project.md` → *Contract → Legacy*.
      *Depends on:* 6.2, 6.4.
- [x] **5.3** Delete `Tests/Spine/Domains/DocumentDomainTests.fs`, superseded by 4.2.
      *Depends on:* 4.3.
- [x] **5.4** Delete `MyDogsbody.Spine/` from disk and from `MyDogsbody.sln`; remove its
      `ProjectReference` from `MyDogsbody.Tests.fsproj` and anywhere else it survives.
      *Depends on:* 5.1, 5.2, 5.3, 3.7.
- [x] **5.5** Grep for `Domian` across `.fs` and `.fsproj` and confirm no identifier remains.
      *Depends on:* 5.4, 6.1.

## Phase 6 — Action names and contract suites (required)

- [x] **6.1** Rewrite `MyDogsbody.Exceptions.Types/ActionNames.fs` — nested modules mirroring the
      real code path (`Integrations.Credentials.CredentialStore`, `Integrations.Pdf.PdfDocumentReader`,
      `Startup.CredentialApi`, `Logging.*`). Delete the dead `MyDogsbody.Infrastructure`,
      `MyDogsbody.Domains`, `MyDogsbody.UseCases` and `MyDogsbody.Repositories` modules. The two
      wrong strings recorded in design.md → *Latent defects* go with them.
      *Depends on:* 5.4, 4.6.
- [x] **6.2** `Tests/Contracts/CredentialBoundaryMapperTests.fs` — both edge mappers field-for-field
      in both directions; `Username` → `ExternalUsername` asserted **as a deliberate rename**; a
      constrained type survives the round trip through the store's `string` column unchanged.
      *Depends on:* 2.4, 3.4.
- [x] **6.3** `Tests/Contracts/CredentialDependencyContractTests.fs` — one shared suite per
      dependency function type, run against the real adapter binding **and** against every fake used
      in the Phase 1 workflow tests. Shape in design.md → *The shared-suite shape*.
      *Depends on:* 3.5.
- [x] **6.4** `Tests/Contracts/PersistedShapeTests.fs` — persisted document field names for
      `Credential` and `ExceptionLog`, asserted by name; every `Infrastructure` member round-trips
      through the store.
      *Depends on:* 2.5, 7.3.
- [x] **6.5** `Tests/Contracts/ActionNamesTests.fs` — every outer-ring function's error reports its
      declared action string.
      *Depends on:* 6.1.
- [x] **6.6** `Tests/Contracts/CredentialApiContractTests.fs` — one shared suite over `CredentialApi`,
      run against the real API over a temp database and against the fake
      `CredentialsBrowserModuleCreatorsTests` uses.
      *Depends on:* 3.5.

## Phase 7 — Logging returns Result (required)

- [x] **7.1** Add `MyDogsbody.Logging` `ProjectReference` to `MyDogsbody.Tests.fsproj`.
      Resolution 4 in design.md → *Specification conflicts found*.
- [x] **7.2** `Tests/Logging/ExceptionStoreTests.fs` (Integration) — fresh temp log database per
      test: insert → `getAll` returns the row with every field; a failing collection getter returns
      `Error` carrying the declared action and a preserved inner exception.
      *Depends on:* 7.1.
- [x] **7.3** Change `ExceptionRepository.insertOne`/`getAll` and
      `ExceptionUseCases.addException`/`getAllExceptions` to return `Result<_, MyDogsbodyException>`,
      written with `handleError`. Add their action strings to `ActionNames`.
      *Depends on:* 7.2.
- [x] **7.4** `Startup.fs` — the `writeLog` callback discards the log write's `Result` with a comment
      stating why: a failed log write cannot itself be logged, and must not displace the failure being
      recorded. This is the one sanctioned discard.
      *Depends on:* 7.3.

## Phase 8 — Main database migrations (required)

- [x] **8.1** Add `MyDogsbody.Database` and `MyDogsbody.Database.Migrations` `ProjectReference`s to
      `MyDogsbody.Tests.fsproj`.
- [x] **8.2** `Tests/Database/MigrationsTests.fs` (Integration) — fresh temp SQLite file per test;
      **build the schema by calling `MigrationSetup.setupMigrations`**, never hand-written DDL;
      assert `MigrateUp` produces the expected tables and columns, and that `Down` reverses it.
      Dispose the connection handed back by `GetDatabaseConnection()` so Windows releases the file.
      *Depends on:* 8.1.

## Phase 9 — End-to-end (required)

- [x] **9.1** Add bUnit and the MudBlazor test services to `MyDogsbody.Tests.fsproj`; build
      `Tests/E2E/BlazorTestHarness.fs` — a `TestContext` with the MudBlazor providers registered, JS
      interop stubbed, and a `CredentialApi` supplied by the caller.
      *Outcome:* the credentials table renders headlessly against a fake API.
      *Risk:* highest in the change — see design.md → *Risk — the bUnit harness*. Prove the smallest
      render before 9.2 depends on it.
      *Depends on:* 3.5.
- [x] **9.2** `Tests/E2E/CredentialsFlowTests.fs` — through a rendered component down to a real temp
      LiteDB file and back into what the component renders: add a credential → the row appears; edit
      a credential → the table shows the new values; submit an empty secret → the alert shows the
      message **and a recording `handleError` proves nothing was logged**; force a store failure →
      the alert shows the message **and exactly one log entry was recorded**.
      *Depends on:* 9.1, 3.5.

## Phase 10 — Gate (required)

- [x] **10.1** `dotnet build MyDogsbody.sln` — zero errors, every project including the scratch ones.
- [x] **10.2** `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — zero failures, **zero skips**.
      *Depends on:* 10.1.
- [x] **10.3** Run each level filter (`--filter "Level=Unit"`, `Integration`, `Contract`, `E2E`) and
      confirm every level reports a non-zero, passing count.
      *Depends on:* 10.2.
- [x] **10.4** `dotnet run --project MyDogsbody\MyDogsbody.csproj` — open the window, add, edit and
      reject a credential by hand. The previous change shipped without this; record the result in the
      change description either way.
      *Depends on:* 10.2.

## Phase 11 — Documentation (required)

- [x] **11.1** `CLAUDE-project.md` — project table (add `MyDogsbody.Domain`, drop `MyDogsbody.Spine`),
      reference-direction rules (drop the legacy `Spine` clauses), *Status — specified vs built*
      (all five rows now match), *Build state* (new project and test counts), *Testing in this
      codebase* (the E2E harness now exists; the seams paragraph no longer describes a layered core),
      and *Naming quirks* (the `Domian` spellings are gone; `GetInfrustructureCredentialCallback` and
      the `Integrations.Google` module name remain).
      *Depends on:* 10.3.
- [x] **11.2** Record in this folder the four resolutions from design.md → *Specification conflicts
      found* that changed a documented rule, so `CLAUDE-project.md` and the specs do not disagree.
      *Depends on:* 11.1.

---

## Optional

- [x] **O.1** Collapse the logging project's `ExceptionRepositoryTypeDto` / `ExceptionUseCaseTypeDto`
      pair into one record. Two identical DTOs one hop apart; out of scope per requirements.md, but
      cheap once Phase 7 is touching both files.
- [x] **O.2** Rename `GetInfrustructureCredentialCallback` in
      `UI.Portal/Components/CredentialsComponents.fs`. UI-internal, three call sites, no behaviour
      change. Update the *Naming quirks* section with it.
- [x] **O.3** Give `CredentialsDatabaseContext` and `LoggingDatabaseContext` a `Dispose`, so
      integration tests stop deleting temp files on a best-effort basis.
- [x] **O.4** Add a build-time check that `MyDogsbody.Domain.fsproj` stays reference-free, so the
      invariant fails the build rather than a test. 0.5 covers it as a test; this makes it earlier.

## Known risks — how they landed

- **9.1 (the bUnit harness) was the one that might not have worked.** It did: MudBlazor renders
  headlessly with `AddMudServices()` plus `JSInterop.Mode <- Loose`, and Fun.Blazor views reach
  bUnit through `FunFragmentComponent`. The fallback was not needed and **no level was
  downgraded** — all four report a non-zero passing count.
- **Phase 3 was the widest step**, as predicted, and could not be subdivided: deleting the
  credentials DTO layers breaks `Spine` immediately, so 2.6 and Phase 3 were carried through as
  one continuous stretch and the build was taken green at the end of it rather than between.
- **Phase 6.1 renamed every action string.** Rows already in a developer's `Logging.db` carry the
  old names. No migration was written: the log database holds no domain data and can be deleted.
- **Unforeseen:** a genuine concurrency race in LiteDB's global `BsonMapper`, reachable in
  production, surfaced as a 6-in-10 flaky suite. See [outcome.md](outcome.md) → *Found on the way*.
