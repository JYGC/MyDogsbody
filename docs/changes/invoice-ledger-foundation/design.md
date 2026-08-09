# Design — Invoice ledger foundation

Change **#1 of 7**. Requirements in [`requirements.md`](requirements.md); decision record and
measurements in [`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## What this change proves

`MyDogsbody.Database` and `MyDogsbody.Database.Migrations` have existed since the scaffold and have
**never been executed by the application** (friction #11). `MigrationSetup.setupMigrations` has no
caller, `createDatabaseContext` opens a `SqliteConnection` it never disposes, and no composition-root
binding exists. Six later changes assume all of that works.

So the deliverable is really two things at once: a suppliers feature, and the first proof that the
main-database path is sound. The feature is small on purpose — the point is the path.

---

## System architecture and components

```
 UI.Portal  /settings/suppliers
   SuppliersPage.fs ─ SuppliersComponents.fs (dialog) ─ SuppliersBrowserModuleCreators.fs
        │  cval / transact, startWork first parameter, write-then-reload
        ▼
 UI.Types   SupplierApi  { GetAllSuppliers; AddSupplier; EditSupplier; DeleteSupplier }
        │   SupplierUiType, SupplierMatcherUiType, Modules/SuppliersBrowserModule
        ▼
 Startup    SupplierApiFactory.fs ── binds adapters to dependency types, maps both errors
            SupplierApiMappers.fs ── domain ⇄ UI record  (the TOP mapper), error translation
            Startup.fs ──────────── runs migrations, opens the SQLite context, registers
        ▼
 Domain     Suppliers/SuppliersTypes.fs
            Suppliers/AddSupplierWorkflow.fs · EditSupplierWorkflow.fs
                      DeleteSupplierWorkflow.fs · ListSuppliersWorkflow.fs
        ▲
 Database   SupplierStore.fs ─────── outer ring: handleError, Result<_, MyDogsbodyException>
            SupplierRecordMappers.fs  record ⇄ domain  (the BOTTOM mapper)
            DatabaseContext.fs ────── + GetSuppliers, GetSupplierMatchers, Dispose
        ▲
 Migrations Migration_20260809000001_CreateSuppliersTable.fs
            Migration_20260809000002_CreateSupplierMatchersTable.fs   ← schema source of truth
```

Two mapping points, both at the edges, exactly as the architecture requires. Domain types travel
unmapped between `SupplierStore` and `SupplierApiMappers`.

### Projects touched

| Project | Change |
| --- | --- |
| `MyDogsbody.Domain` | **New folder** `Suppliers/`. Still zero `ProjectReference` elements |
| `MyDogsbody.Database.Models` | **New** `SupplierRecord`, `SupplierMatcherRecord` — `[<CLIMutable>]` F# records, same shape as `Blog` |
| `MyDogsbody.Database` | **Gains a `ProjectReference` to `MyDogsbody.Domain`** (Q5.9). New `SupplierRecordMappers.fs`, `SupplierStore.fs`. `DatabaseContext` gains two getters and a `Dispose` |
| `MyDogsbody.Database.Migrations` | **Two new migrations** |
| `MyDogsbody.Exceptions.Types` | `ActionNames.MyDogsbody.Database.SupplierStore.*` and `.Startup.SupplierApi.*` |
| `MyDogsbody.UI.Types` | `SupplierUiType.fs`, `SupplierApi.fs`, `Modules/SuppliersBrowserModule.fs` |
| `MyDogsbody.Startup` | `SupplierApiMappers.fs`, `SupplierApiFactory.fs`; `Startup.fs` gains the SQLite context, the migration run and one registration. **Gains `ProjectReference`s to `MyDogsbody.Database` and `MyDogsbody.Database.Migrations`** |
| `MyDogsbody.UI.Portal` | `Pages/Settings/SuppliersPage.fs`, `Components/SuppliersComponents.fs`, `ModuleCreators/SuppliersBrowserModuleCreators.fs`, `Shell.fs` route, `SettingsComponents` nav entry |
| `MyDogsbody.Tests` | New tests at all four levels. `MyDogsbody.Database` and `.Database.Migrations` are already referenced |
| `MyDogsbody` (C#) | **Nothing.** `MainWindow.xaml.cs` does not change |

---

## Data models and interfaces

### `MyDogsbody.Domain/Suppliers/SuppliersTypes.fs`

```fsharp
namespace MyDogsbody.Domain.Suppliers

/// The identifier the store assigned. Opaque to the domain - it is the store's business what
/// shape it has, the same way CredentialId is.
type SupplierId = private SupplierId of string

module SupplierId =
    let create (value: string) : Result<SupplierId, string> = ...
    let value (SupplierId id) = id

/// Trimmed on the way in, compared case-insensitively for uniqueness. Two spellings that differ
/// only by case or surrounding space are the same supplier - see Edge cases.
type SupplierName = private SupplierName of string

module SupplierName =
    [<Literal>]
    let MaximumLength = 200
    let create (value: string) : Result<SupplierName, string> = ...
    let value (SupplierName name) = name

/// How long after issue this supplier's invoices fall due. A supplier-level fact for the same
/// reason the matcher is one: "Acme bills net 30" is about Acme, not about a document.
/// Nothing in this change reads it - DateFromField in change #2 is what it exists for.
type PaymentTermDays = private PaymentTermDays of int

module PaymentTermDays =
    [<Literal>]
    let Minimum = 0            // due on issue is a real term
    [<Literal>]
    let Maximum = 365
    let create (days: int) : Result<PaymentTermDays, string> = ...
    let value (PaymentTermDays days) = days

/// How a message is recognised as this supplier's. Several per supplier, matching on any (Q7.6.5).
/// Kept on the supplier rather than the template: "is this mail from Acme?" is a fact about Acme,
/// while a template answers the different question "given it is Acme, where are the fields?".
type SupplierMatcher =
    | SenderAddress  of string          // exact, case-insensitive
    | SenderDomain   of string          // the part after @, case-insensitive
    | SubjectPattern of string          // case-insensitive substring - see Decisions taken

module SupplierMatcher =
    [<Literal>]
    let MaximumValueLength = 400
    /// Kind and raw value in, a validated matcher out. The kind is what decides which rule applies.
    let create (kind: MatcherKind) (value: string) : Result<SupplierMatcher, string> = ...
    let kind (matcher: SupplierMatcher) : MatcherKind = ...
    let value (matcher: SupplierMatcher) : string = ...

// One type per pipeline stage.

type UnvalidatedSupplier =
    { Name: string; PaymentTermDays: int; Matchers: (MatcherKind * string) list }

type UnvalidatedSupplierEdit =
    { Id: string; Name: string; PaymentTermDays: int; Matchers: (MatcherKind * string) list }

type ValidSupplier =
    { Name: SupplierName; PaymentTermDays: PaymentTermDays; Matchers: SupplierMatcher list }

type ValidSupplierEdit =
    { Id: SupplierId; Name: SupplierName; PaymentTermDays: PaymentTermDays; Matchers: SupplierMatcher list }

type StoredSupplier =
    { Id: SupplierId; Name: SupplierName; PaymentTermDays: PaymentTermDays; Matchers: SupplierMatcher list }

type SupplierError =
    | SupplierNameInvalid of reason: string
    | SupplierNameTaken of name: string
    | MatcherInvalid of reason: string
    | SupplierIdInvalid of reason: string
    | SupplierNotFound of SupplierId
    | SupplierStoreFailed of message: string

type LoadSuppliers  = unit -> Result<StoredSupplier list, SupplierError>
type SaveSupplier   = ValidSupplier -> Result<StoredSupplier, SupplierError>
/// None when no row carried that identifier, so "not found" stays the workflow's decision.
type UpdateSupplier = ValidSupplierEdit -> Result<StoredSupplier option, SupplierError>
type DeleteSupplier = SupplierId -> Result<bool, SupplierError>
```

`MatcherKind` is a three-case union (`Sender | Domain | Subject`) declared above `SupplierMatcher`.
It exists so the UI record and the persisted row can carry a kind without either of them naming the
matcher union — the same reason `Infrastructure` exists in the credentials area today.

### Workflows

| File | Signature | Notes |
| --- | --- | --- |
| `ListSuppliersWorkflow.fs` | `LoadSuppliers -> unit -> Result<StoredSupplier list, SupplierError>` | Sorts by name. The sort is the workflow's, not the store's, so it is unit-tested |
| `AddSupplierWorkflow.fs` | `LoadSuppliers -> SaveSupplier -> UnvalidatedSupplier -> Result<StoredSupplier, SupplierError>` | Validate, then check the name is free, then save. `loadSuppliers` is a dependency because uniqueness is a *rule*, and the database index is the backstop |
| `EditSupplierWorkflow.fs` | `LoadSuppliers -> UpdateSupplier -> UnvalidatedSupplierEdit -> Result<StoredSupplier, SupplierError>` | Same, excluding the row's own name from the clash check |
| `DeleteSupplierWorkflow.fs` | `DeleteSupplier -> string -> Result<unit, SupplierError>` | Parses the id, deletes, turns `false` into `SupplierNotFound` |

Each is one public function, dependencies first, input last, `Result` out, written with the domain's
own `result` builder. Validation steps stay private in the same file.

### `MyDogsbody.Database.Models`

```fsharp
[<CLIMutable>]
type SupplierRecord = { Id: int; Name: string; PaymentTermDays: int }

[<CLIMutable>]
type SupplierMatcherRecord = { Id: int; SupplierId: int; Kind: string; Value: string }
```

`Kind` persists as `"Sender"` / `"Domain"` / `"Subject"`. Storing the string rather than an integer
means a row is readable in a SQLite browser and a reordered union cannot silently reinterpret
existing data; the mapper's inbound direction is a total match with an explicit failure case.

### `MyDogsbody.UI.Types`

```fsharp
type SupplierMatcherUiType = { Kind: string; Value: string }

type SupplierUiType =
    { Id: string; Name: string; PaymentTermDays: int; Matchers: SupplierMatcherUiType list }

type SupplierUiTypeWithoutId =
    { Name: string; PaymentTermDays: int; Matchers: SupplierMatcherUiType list }

type SupplierApi =
    {
        GetAllSuppliers: unit -> Result<SupplierUiType list, MyDogsbodyException>
        AddSupplier: SupplierUiTypeWithoutId -> Result<unit, MyDogsbodyException>
        EditSupplier: SupplierUiType -> Result<unit, MyDogsbodyException>
        DeleteSupplier: string -> Result<unit, MyDogsbodyException>
    }
```

Writes return `unit` because **a write reloads** — the table shows what was stored, not what the
dialog held.

### Migrations

| Timestamp | Name | Creates |
| --- | --- | --- |
| `20260809000001` | `CreateSuppliersTable` | `Suppliers(Id INTEGER PK identity, Name TEXT(200) NOT NULL, PaymentTermDays INTEGER NOT NULL)` + unique index `IX_Suppliers_Name` on `Name` |
| `20260809000002` | `CreateSupplierMatchersTable` | `SupplierMatchers(Id INTEGER PK identity, SupplierId INTEGER NOT NULL FK → Suppliers.Id ON DELETE CASCADE, Kind TEXT(16) NOT NULL, Value TEXT(400) NOT NULL)` + index on `SupplierId` |

Timestamps come from a block reserved across the seven changes so they stay ordered even if the
changes land out of sequence: **#1 uses `…0001`–`…0002`, #2 `…0003`–`…0004`, #4 `…0005`–`…0009`,
#7 `…0010`.** Both migrations sit above `MigrationSetup.fs` in the `.fsproj`, in timestamp order.

SQLite enforces `ON DELETE CASCADE` only when `PRAGMA foreign_keys = ON`, which is **off by default
per connection**. `DatabaseContextSetup` must set it when it opens the connection, and a migration
test must assert the cascade actually fires — otherwise the constraint is decorative and matchers
are orphaned in production while every test passes.

---

## Sequence diagrams

### Add supplier — success

```
SuppliersPage         ModuleCreator        SupplierApi         AddSupplierWorkflow      SupplierStore        SQLite
     │  save dialog        │                    │                      │                     │               │
     ├────────────────────►│                    │                      │                     │               │
     │                     │ startWork ─┐       │                      │                     │               │
     │                     │            └──────►│ AddSupplier uiType   │                     │               │
     │                     │                    ├─ toUnvalidatedSupplier                     │               │
     │                     │                    ├─────────────────────►│ validate            │               │
     │                     │                    │                      ├─ loadSuppliers ────►│ SELECT ──────►│
     │                     │                    │                      │◄────────────────────┤◄──────────────┤
     │                     │                    │                      ├─ name is free       │               │
     │                     │                    │                      ├─ saveSupplier ─────►│ INSERT ──────►│
     │                     │                    │                      │◄────────────────────┤◄──────────────┤
     │                     │                    │◄─ Ok StoredSupplier ─┤                     │               │
     │                     │◄─ Ok () ───────────┤  (mapError → exception, never taken)       │               │
     │                     ├─ LoadSuppliers (write-then-reload)                              │               │
     │◄─ transact: list, ErrorAval = None ──────┤                                            │               │
```

### Add supplier — name already taken

```
AddSupplierWorkflow                      SupplierStore
   ├─ validate ─────────────── Ok ValidSupplier
   ├─ loadSuppliers ──────────────────────────►  SELECT
   │◄─ Ok [ ...existing "Acme"... ] ────────────
   ├─ name clash detected
   └─ Error (SupplierNameTaken "Acme")

   ► saveSupplier is NEVER called - asserted by a recording fake
   ► SupplierApiFactory maps it to MyDogsbodyException wrapping an ApplicationException,
     so handleError passes it through UNLOGGED (expected failure, not a defect)
   ► MudAlert shows the message; the table keeps its last good data
```

### Startup — the main database's first run

```
Startup.fs (module init)
   ├─ mainDatabasePath = "MyDogsbody.db"
   ├─ MigrationSetup.setupMigrations $"Data Source={mainDatabasePath}"
   │     └─ builds the runner, MigrateUp(), disposes the service provider
   ├─ mainDatabaseContext = DatabaseContextSetup.createDatabaseContext mainDatabasePath
   │     └─ OptionTypes.register(), open SqliteConnection, PRAGMA foreign_keys = ON
   └─ supplierApi = SupplierApiFactory.createSupplierApi handleError mainDatabaseContext
```

Migrations run **before** the context is created, so a store function can never see a table that
does not exist yet.

---

## Error-handling approach

Two error types, meeting once, exactly as the architecture requires.

| Ring | Type | Builder |
| --- | --- | --- |
| `Domain/Suppliers` | `SupplierError` | `result` |
| `MyDogsbody.Database`, composition root | `MyDogsbodyException` | `handleError` |

**Inbound** (`SupplierApiFactory`): a store exception becomes `SupplierStoreFailed ex.Message`.
**Outbound**: the workflow's `SupplierError` becomes a `MyDogsbodyException` carrying the API
operation's action name.

`SupplierApiMappers.toMyDogsbodyException` decides, per case, whether the failure is **expected** —
and that decision is what keeps the log clean:

| `SupplierError` case | Inner exception | Logged? |
| --- | --- | --- |
| `SupplierNameInvalid`, `MatcherInvalid`, `SupplierNameTaken`, `SupplierIdInvalid`, `SupplierNotFound` | `ApplicationException` | **No** — `ExceptionHelpers.isApplicationException` passes it through |
| `SupplierStoreFailed` | the original | **Yes** — one entry |

That is the same idiom `PdfDocumentReader.readContent` uses for a missing file, and it is what the
E2E tests assert through a recording `HandleErrorBuilder` rather than by opening `Logging.db`.

### Action names

```
ActionNames.MyDogsbody.Database.SupplierStore.getAll / insertOne / updateOne / deleteOne
ActionNames.MyDogsbody.Startup.SupplierApi.getAllSuppliers / addSupplier / editSupplier / deleteSupplier
```

`Database` is a **new top-level module** under `ActionNames.MyDogsbody` — the main database is not an
integration, so its entries do not go under `Integrations.`. The existing structural suite
(`Contracts/ActionNamesTests.fs`) already requires every string to end with the name of the binding
that declares it and no two bindings to share a string; both new modules must satisfy it unchanged.

---

## Testing strategy

Unit tests land **before** the implementation, per task. Integration, contract and E2E may follow.

### Unit — no I/O, lambdas for dependencies

- Every `create`: one accepted value and one rejected value **per rule**, with the reason asserted.
  `SupplierName` has three rules (empty, whitespace-only, too long); `PaymentTermDays` has two;
  `SupplierMatcher` has four across its three kinds.
- Each workflow's Ok path with **every output field** asserted after unwrapping the constrained
  types. `Result.isOk` proves nothing.
- Each workflow's error path asserting the **DU case and its payload** — `SupplierNameTaken "Acme"`
  with the name checked, not just the case.
- **Dependency-not-called:** `AddSupplierWorkflow` given a clashing name must not call
  `saveSupplier`; given an invalid matcher must not call it either. A recording lambda proves it.
- Both mappers, which are pure and therefore unit-testable outside the domain.

### Integration — real SQLite, fresh temp file per test

- `withSuppliers`, modelled on `CredentialStoreTests.withStore`: temp path from
  `Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")`, `MigrationSetup.setupMigrations`
  against it, the context created, the test run, **the connection disposed**, the file deleted in
  `try/finally`, and the delete asserted to have succeeded.
- Round trips: insert → `getAll` returns the row with its integer id surfaced as the `Id` string;
  update → re-read reflects new values **and the replaced matcher set**; delete → the row and its
  matchers are both gone.
- The unique index actually refuses a duplicate name, and the cascade actually removes matchers.
- `SupplierApiFactory` with the real store bound to it — the composition root's job, exercised
  without touching `Startup.fs`.

### Contract

- **`LoadSuppliers` / `SaveSupplier` / `UpdateSupplier` / `DeleteSupplier`** each get one shared
  suite, a `[<Theory>]` over `[<MemberData>]` with the implementation chosen by name, run against
  the real adapter **and** every fake used in a workflow unit test. The `MemberData` source must be
  a **public** `let` — a private one fails at run time, not compile time.
- Both mappers, field-for-field, both directions.
- A constrained value survives the round trip through its `TEXT` column unchanged, including
  non-ASCII.
- Every `SupplierError` case → its intended `MyDogsbodyException`; every adapter exception → its
  intended `SupplierError` case.
- Every new `ActionNames` entry is reported by the function that declares it.
- **Persisted shape**: assert the column names, not just the round-tripped object. SQLite is not
  schemaless the way LiteDB is, but a Dapper.FSharp record field renamed without a migration fails
  at run time only — so the persisted names are asserted by reading the table schema.

### E2E — through the real composition path to a real file

Uses the existing `E2E/BlazorTestHarness.fs`. Fun.Blazor views are `NodeRenderFragment`, so they
reach bUnit via `FunFragmentComponent` with the view as its `Fragment` parameter, as
`CredentialsFlowTests.renderBrowser` shows. `startWork` is `fun work -> work ()`.

| Flow | Asserts |
| --- | --- |
| Add supplier | the row appears in the markup with its name, term and matchers |
| Edit supplier | the table shows the new values |
| Delete supplier | the row is gone |
| Validation failure | `MudAlert` shows the message **and nothing is logged** |
| Store failure | `MudAlert` shows the message **and exactly one entry is logged** |
| Success after failure | the alert is cleared |

"Was it logged?" is asserted through a recording `HandleErrorBuilder`, never by opening `Logging.db`.
No test reaches `Startup.Startup`.

### Gate

`dotnet build MyDogsbody.sln` with zero errors, `dotnet test` with zero failures and zero skips.
The suite is 204 green today; this change only adds.

---

## Decisions taken

1. **`SubjectPattern` is a case-insensitive substring, not a regular expression.** Q7.6.5 says
   "subject pattern" without settling which. Regex here would drag the whole match-timeout apparatus
   (friction #9) into a change whose entire value is being the smallest thing that proves SQLite
   works, and the measurement says **sender domain does most of the work** anyway. Change #2 owns
   the regex machinery and revisits this if a real supplier needs it; the stored column is free text
   either way, so upgrading it later is a validation change, not a migration.
2. **`SupplierId` wraps a string; the SQLite column is an integer identity.** Consistent with
   `CredentialId`, and it keeps the domain from naming a storage-shaped integer. The bottom mapper's
   inbound direction (`int` → `string`) is total; the outbound direction is used only for ids that
   came from a row already read, so a parse failure is a bug and surfaces as `SupplierIdInvalid`
   rather than an exception.
3. **`MatcherKind` persists as a string, not an ordinal.** A reordered union cannot silently
   reinterpret stored rows, and a row is readable in a SQLite browser.
4. **Uniqueness is checked in the workflow *and* indexed in the database.** The workflow check is
   what produces `SupplierNameTaken` with a message worth reading; the index is what makes the
   database refuse a duplicate even when the code is wrong (the same belt-and-braces Q5.8 asks for
   on invoices).
5. **`DatabaseContext` gains a `Dispose`.** Both LiteDB context records already carry one, tests
   need it to delete their temp file on Windows, and `createDatabaseContext` currently leaks the
   connection. Additive, so it disturbs nothing.
6. **`PRAGMA foreign_keys = ON` is set when the connection is opened.** Without it the cascade in
   migration `…0002` never fires and matchers are orphaned. Asserted by a migration test.
7. **A supplier with no match rules is legal.** Forbidding it would prevent entering a supplier
   before its rules are known, and a supplier with no rules simply never matches — which is visible
   on the page rather than hidden.
8. **`PaymentTermDays` is added now although nothing reads it until change #2.** It is a column on a
   table this change creates; adding it later is a migration and a mapper edit for no benefit.

---

## Risks

| Risk | Handling |
| --- | --- |
| **The main database has never run** (friction #11). `MigrationSetup` has no caller and the connection is never disposed. Expect to find something | This change is deliberately small so that whatever turns up is diagnosable. Decisions 5 and 6 pre-empt the two known defects |
| **`Startup.fs` opens another file at module load** (friction #6) | Same pattern as the two LiteDB contexts. Tests keep away from `Startup` exactly as they do today; everything worth testing is in the factory and the mappers |
| Microsoft.Data.Sqlite **pools connections**, so a pooled handle can keep a temp file locked on Windows and cleanup fails | Every integration test disposes the context and asserts the file delete succeeded — the assertion is what turns a silent leak into a failing test |
| A future change adds a migration with a colliding timestamp | The reserved block is documented above and repeated in each change's `tasks.md` |
| `SupplierApi` grows a fifth member later and the E2E fakes drift | The contract suite runs against the real API record and every fake, so a drifting fake fails there rather than in production |
