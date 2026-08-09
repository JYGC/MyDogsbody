# Tasks — Invoice ledger foundation

Change **#1 of 7**. [`requirements.md`](requirements.md) · [`design.md`](design.md) ·
[decision record](../invoice-to-calendar/background.md)

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation is written.
Integration, contract and E2E tests may follow the implementation. Tasks marked *(test-first)* carry
production code; the rest are scaffolding, wiring or verification.

**Reserved migration timestamps for this change: `20260809000001`–`20260809000002`.** Later changes
in the series own `…0003` onward — see each change's tasks file.

---

## Phase 0 — Project wiring (required, no tests)

- [ ] **0.1** Add a `ProjectReference` from `MyDogsbody.Database` to `MyDogsbody.Domain` (Q5.9).
      *Outcome:* solution builds; `MyDogsbody.Domain` still has **zero** `ProjectReference` elements
      and `AssertDomainReferencesNothing` still passes.
- [ ] **0.2** Add `ProjectReference`s from `MyDogsbody.Startup` to `MyDogsbody.Database` and
      `MyDogsbody.Database.Migrations`.
      *Outcome:* solution builds. *Depends on:* 0.1.
- [ ] **0.3** Confirm `MyDogsbody.Tests` already references `MyDogsbody.Database` and
      `.Database.Migrations` (it does — the migrations are tested). Add nothing.

## Phase 1 — Suppliers domain (required)

### Types

- [ ] **1.1** *(test-first)* `SupplierId`, `SupplierName`, `PaymentTermDays`.
      Tests: one accepted and one rejected value **per rule**, rejection reason asserted —
      `SupplierName` empty / whitespace-only / over 200 chars; `PaymentTermDays` below 0 / above 365;
      `SupplierId` empty. Plus `value` round-trips what `create` accepted.
      *Outcome:* `MyDogsbody.Domain/Suppliers/SuppliersTypes.fs` compiled first in the `Suppliers/`
      folder, added to the `.fsproj` in dependency order.
- [ ] **1.2** *(test-first)* `MatcherKind` and `SupplierMatcher` with its `create`.
      Tests: sender address without `@` rejected; sender domain containing `@` rejected; empty
      subject pattern rejected; any value over 400 chars rejected; each kind's accepted value
      round-trips through `kind` and `value`.
      *Depends on:* 1.1.
- [ ] **1.3** Stage types (`UnvalidatedSupplier`, `UnvalidatedSupplierEdit`, `ValidSupplier`,
      `ValidSupplierEdit`, `StoredSupplier`), `SupplierError`, and the four dependency function
      types. No test of its own — type declarations, exercised by every task below.
      *Depends on:* 1.2.

### Workflows

- [ ] **1.4** *(test-first)* `ListSuppliersWorkflow`.
      Tests: Ok path with every field of every returned supplier asserted; the returned list is
      ordered by name regardless of the order the dependency returned; empty store returns `Ok []`;
      a dependency error is propagated unchanged.
      *Depends on:* 1.3.
- [ ] **1.5** *(test-first)* `AddSupplierWorkflow`.
      Tests: Ok path with every output field asserted; `SupplierNameInvalid` with the reason;
      `MatcherInvalid` with the reason and the offending rule identified; `SupplierNameTaken "Acme"`
      with the name asserted; a name clashing only by case or surrounding whitespace is still
      `SupplierNameTaken`; **`saveSupplier` never called** on any of the three failures, proved by a
      recording lambda; a supplier with no matchers is accepted.
      *Depends on:* 1.3.
- [ ] **1.6** *(test-first)* `EditSupplierWorkflow`.
      Tests: Ok path, every field; `SupplierIdInvalid` on an unusable id; `SupplierNotFound` when the
      update dependency returns `None`; `SupplierNameTaken` when renaming onto another supplier;
      **no clash reported when the name is unchanged** — the row must not collide with itself; the
      matcher set is **replaced**, not merged; `updateSupplier` never called on a validation failure.
      *Depends on:* 1.3.
- [ ] **1.7** *(test-first)* `DeleteSupplierWorkflow`.
      Tests: Ok path; `SupplierIdInvalid` on an unusable id; `SupplierNotFound` when the dependency
      returns `false`; `deleteSupplier` never called on an unusable id.
      *Depends on:* 1.3.

## Phase 2 — Migrations (required)

- [ ] **2.1** *(test-first)* `Migration_20260809000001_CreateSuppliersTable.fs`.
      Tests: `MigrateUp` on an empty temp file produces `Suppliers` with the expected columns and
      types; the unique index on `Name` exists and **refuses a second row with the same name**;
      `Down()` removes the table and the index.
      *Outcome:* file added to `MyDogsbody.Database.Migrations.fsproj` above `MigrationSetup.fs`, in
      timestamp order.
- [ ] **2.2** *(test-first)* `Migration_20260809000002_CreateSupplierMatchersTable.fs`.
      Tests: as above for `SupplierMatchers`; the foreign key exists; **deleting a supplier removes
      its matchers** (this is the test that catches `PRAGMA foreign_keys` being off — design
      decision 6); `Down()` reverses it.
      *Depends on:* 2.1.
- [ ] **2.3** Confirm the existing `Blogs` / `Comments` migration tests still pass untouched.
      *Outcome:* no scaffold migration is edited by this change.

## Phase 3 — Main-database plumbing (required)

- [ ] **3.1** *(test-first)* `SupplierRecord` and `SupplierMatcherRecord` in
      `MyDogsbody.Database.Models`, and `SupplierRecordMappers.fs` — the **bottom mapper**.
      Tests: field-for-field in both directions; `MatcherKind` ⇄ its persisted string for all three
      kinds; an unrecognised kind string maps to an error rather than a default; a name with
      non-ASCII characters survives unchanged.
      *Depends on:* 1.3, 0.1.
- [ ] **3.2** *(test-first)* `DatabaseContext` gains `GetSuppliers`, `GetSupplierMatchers` and
      **`Dispose`** (design decision 5); `DatabaseContextSetup.createDatabaseContext` binds them and
      issues `PRAGMA foreign_keys = ON` (decision 6).
      Tests *(Integration)*: a context created against a temp file can be disposed and **the file
      then deletes successfully**; `PRAGMA foreign_keys` reads back as `1` on the connection handed
      out by `GetDatabaseConnection`.
- [ ] **3.3** *(test-first)* `SupplierStore.fs` — `getAll`, `insertOne`, `updateOne`, `deleteOne`.
      Outer-ring shape: `handleError` first, dependencies next, input last,
      `Result<_, MyDogsbodyException>` out.
      Tests *(Integration)*: a `withSuppliers` helper (fresh temp file, migrations applied, context
      disposed, file deleted, **delete asserted**); insert → `getAll` returns the row with its id
      surfaced as a string and its matchers attached; update → re-read shows new values and the
      **replaced** matcher set; delete → row and matchers both gone; the unique index refuses a
      duplicate.
      Tests *(Unit)*: each function's error path asserts the declared `ActionNames` string, the
      message, and a preserved `InnerException`.
      *Depends on:* 2.2, 3.1, 3.2.
- [ ] **3.4** `ActionNames.MyDogsbody.Database.SupplierStore.*` — four entries under a **new
      top-level `Database` module** (the main database is not an integration).
      *Outcome:* `Contracts/ActionNamesTests.fs` passes unchanged — every string ends with its
      binding's name, no two bindings share a string.
      *Depends on:* 3.3.

## Phase 4 — Composition root (required)

- [ ] **4.1** *(test-first)* `SupplierApiMappers.fs` — domain ⇄ UI record (**the top mapper**),
      `toSupplierError`, `toMyDogsbodyException`.
      Tests: mapper field-for-field both directions; **each `SupplierError` case → its intended
      action and message**, with the expected/unexpected split asserted (design → *Error handling*)
      — the five expected cases wrap an `ApplicationException`, `SupplierStoreFailed` does not;
      each adapter exception → its intended `SupplierError` case.
      *Outcome:* no module-level I/O.
      *Depends on:* 1.3.
- [ ] **4.2** *(test-first)* `SupplierApiFactory.createSupplierApi handleError databaseContext`.
      Tests *(Integration)*: each of the four API members against a real temp database — Ok path with
      every field, and the error path for each. No module-level I/O, so `Startup.fs` is never
      touched.
      *Depends on:* 3.3, 4.1.
- [ ] **4.3** `ActionNames.MyDogsbody.Startup.SupplierApi.*` — four entries.
      *Depends on:* 4.2.
- [ ] **4.4** `Startup.fs`: main database path, `MigrationSetup.setupMigrations` **before** the
      context is created, `supplierApi`, and one more `AddSingleton` in `registerServices`.
      *Outcome:* `MainWindow.xaml.cs` unchanged. No test — this file is deliberately untestable and
      holds nothing but partial application.
      *Depends on:* 4.2, 0.2.

## Phase 5 — UI types and state (required)

- [ ] **5.1** `SupplierUiType.fs` (`SupplierUiType`, `SupplierUiTypeWithoutId`,
      `SupplierMatcherUiType`) and `SupplierApi.fs` in `MyDogsbody.UI.Types`.
      *Outcome:* no domain type leaks into `UI.Types`.
- [ ] **5.2** `Modules/SuppliersBrowserModule.fs` — a record of `aval` fields plus commands, same
      shape as `CredentialsBrowserModule`: list, `IsLoadingAval`, `ErrorAval`, load / add / edit /
      delete.
      *Depends on:* 5.1.
- [ ] **5.3** *(test-first)* `ModuleCreators/SuppliersBrowserModuleCreators.fs` — `cval` + `transact`
      over the API functions, `startWork` as the **first** parameter, write-then-reload.
      Tests *(Unit)*: with `startWork = fun work -> work ()` and a fake `SupplierApi` record literal
      — a successful add reloads the list; a failed add sets `ErrorAval`; a later success clears it;
      no `Async.Start` anywhere in the file.
      *Depends on:* 5.2.

## Phase 6 — Page (required)

- [ ] **6.1** `Components/SuppliersComponents.fs` — the `MudTable` and the editor dialog.
      The dialog is a **class** inheriting `FunComponent` with `[<Parameter>]` members and a
      `[<CascadingParameter>] IMudDialogInstance`, shown via
      `dialogService.ShowAsync<T>(title, DialogParameters<T>, DialogOptions)` — copy
      `CredentialsComponents.CredentialsEditorDialog`.
      *Outcome:* the dialog edits name, payment term and a repeating list of (kind, value) matchers.
- [ ] **6.2** `Pages/Settings/SuppliersPage.fs` with `getView()` / `getRoute()`
      (`routeCi "/settings/suppliers"`), services obtained per-view with `html.inject`, view piped
      through `SettingsComponents.settingsNavMenu`.
      *Depends on:* 6.1, 5.3.
- [ ] **6.3** Register the route in `Shell.fs` and add the nav entry in `SettingsComponents`.
      *Depends on:* 6.2.
- [ ] **6.4** Delete confirmation before the API call.
      *Depends on:* 6.2.

## Phase 7 — Contract suites (required)

- [ ] **7.1** One shared suite per dependency function type — `LoadSuppliers`, `SaveSupplier`,
      `UpdateSupplier`, `DeleteSupplier` — as a `[<Theory>]` over `[<MemberData>]` with the
      implementation chosen by name, run against the real adapter **and every fake** used in Phase 1.
      **The `MemberData` source must be a public `let`.**
      *Depends on:* 3.3, 1.7.
- [ ] **7.2** `SupplierApi` contract suite, run against the real API record **and** the fake used in
      5.3 and Phase 8.
      *Depends on:* 4.2, 5.3.
- [ ] **7.3** Persisted-shape test: assert the `Suppliers` and `SupplierMatchers` column names by
      reading the table schema, not just the round-tripped object.
      *Depends on:* 2.2.
- [ ] **7.4** Constrained-type round trip: a `SupplierName` written and read back through its `TEXT`
      column is unchanged, including non-ASCII and the maximum length.
      *Depends on:* 3.3.

## Phase 8 — End to end (required)

- [ ] **8.1** `E2E/SuppliersFlowTests.fs` using `BlazorTestHarness`, `FunFragmentComponent` and
      `startWork = fun work -> work ()`, against a real temp SQLite file.
      Flows: add → the row appears; edit → the table shows new values; delete → the row is gone;
      validation failure → `MudAlert` shows the message **and nothing is logged**; store failure →
      `MudAlert` shows the message **and exactly one entry is logged**; success after failure clears
      the alert.
      Use `rendered.WaitForAssertion` so the re-render after a write is awaited. Assert logging
      through a **recording `HandleErrorBuilder`**, never by opening `Logging.db`.
      *Depends on:* 6.3, 4.2.
- [ ] **8.2** Confirm no test in the change reaches `Startup.Startup`.

## Phase 9 — Gate (required)

- [ ] **9.1** `dotnet build MyDogsbody.sln` — zero errors.
- [ ] **9.2** `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — zero failures, **zero skips**,
      all four levels present. Record the new totals per level in the change description.
- [ ] **9.3** `Contracts/DomainIsolationTests.fs` and the `AssertDomainReferencesNothing` build
      target both still pass — `MyDogsbody.Domain` still references nothing.
- [ ] **9.4** Run the app (`dotnet run --project MyDogsbody\MyDogsbody.csproj`), add a supplier, edit
      it, delete it. Confirm `MyDogsbody.db` appears in `bin\Debug\net9.0\` with the migrations
      applied. **This is the first time the main database has ever been run by the application
      (friction #11) — record what turned up, including nothing.**
- [ ] **9.5** Confirm `MainWindow.xaml.cs` is untouched by the change's diff.

## Phase 10 — Documentation (required)

- [ ] **10.1** `CLAUDE-project.md`: the main database is no longer "designed but not wired in" —
      update the *Storage → Main database* status paragraph, the project-structure table rows for
      `MyDogsbody.Database` (it now references `Domain` and holds store functions) and
      `MyDogsbody.Startup`, and the *Build state* test totals.
- [ ] **10.2** Add an `outcome.md` to this folder recording the new test totals per level, anything
      friction #11 turned up, and the manual verification from 9.4.

---

## Optional

- [ ] **O.1** A "used by *n* templates" column on the suppliers table. Nothing consumes suppliers
      until change #2, so there is nothing to count yet.
- [ ] **O.2** Sort and filter on the suppliers table. `MudTable` gives both cheaply; deferred
      because the table is expected to hold ~30 rows (measured) and neither is needed to prove the
      database path.
- [ ] **O.3** Deduplicate identical match rules on save. Currently stored as submitted — a repeated
      rule changes no outcome, so this is tidiness rather than correctness.

## Known risks carried into this change

- **The main database has never been executed by the application.** Phase 9.4 is where that stops
  being true. Design decisions 5 (`Dispose`) and 6 (`PRAGMA foreign_keys`) pre-empt the two defects
  already visible by reading the code; expect a third.
- **`Startup.fs` opens one more file at module load.** Same pattern as the two LiteDB contexts, same
  rule for tests: keep away from `Startup`.
- **Migration timestamp collisions** if changes land out of order. The reserved block is at the top
  of this file and repeated in each sibling change.
