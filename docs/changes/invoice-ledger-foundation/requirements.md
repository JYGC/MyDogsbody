# Requirements — Invoice ledger foundation

Change **#1 of 7**. Depends on nothing. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record and the measurements these requirements are drawn from; question ids (`Q5.1`, `Q7.6.5`) and
friction numbers resolve there.

**What this change is for.** The main SQLite database is designed, migrated and **has never been run
by the application** — `MigrationSetup` has no caller and there is no composition-root binding
(friction #11). This change makes suppliers its first real consumer, end to end: migrations, store
functions, a domain area, an API record, and a page. It is deliberately the smallest change that
proves that path works, because every later change in the series leans on it.

**What it is not.** No invoices, no templates, no mail, no calendar. A supplier here is a name, a set
of match rules and a payment term — nothing consumes any of that yet.

---

## Main database

### Schema

WHEN the migration runner is applied to an empty database THE SYSTEM SHALL create a `Suppliers` table with an identity primary key, a name column, and a payment-term-days column.
WHEN the migration runner is applied to an empty database THE SYSTEM SHALL create a `SupplierMatchers` table with an identity primary key, a supplier-id foreign key, a matcher-kind column and a matcher-value column.
WHEN the `Suppliers` table is created THE SYSTEM SHALL place a unique index on the supplier name, so a duplicate is refused by the database even when the code is wrong.
WHEN a supplier row is deleted THE SYSTEM SHALL delete every `SupplierMatchers` row referencing it, leaving no orphaned matcher.
WHEN each new migration's `Down()` is run THE SYSTEM SHALL remove exactly what its `Up()` created, leaving the database as it was before it.
WHEN a developer changes the shape of either table THE SYSTEM SHALL require a new migration — no DDL is issued from a store function, from `DatabaseContextSetup`, from a test, or by hand in a SQLite tool.

### Wiring

WHEN the application starts THE SYSTEM SHALL apply every outstanding migration to the main database before any store function is called.
WHEN the application starts THE SYSTEM SHALL open the main database at a path relative to the process working directory, alongside the existing LiteDB files.
WHEN the main database is opened THE SYSTEM SHALL expose it as a `DatabaseContext` record of getters — one `unit -> QuerySource<'T>` per table — in the same shape the integrations already use.
WHEN a caller has finished with a `DatabaseContext` THE SYSTEM SHALL provide a `Dispose` that closes the underlying `SqliteConnection`, so a test can delete its temp file rather than leave it locked.
WHEN `MyDogsbody.Database` is built THE SYSTEM SHALL allow it to reference `MyDogsbody.Domain`, and nothing else new.

---

## Suppliers workflow area

### Types

WHEN a supplier name is created from a non-empty string THE SYSTEM SHALL accept it, trimmed of leading and trailing whitespace.
WHEN a supplier name is created from an empty or whitespace-only string THE SYSTEM SHALL reject it with the reason "Supplier name must not be empty."
WHEN a supplier name longer than 200 characters is created THE SYSTEM SHALL reject it with a reason naming the limit.
WHEN a payment term of between 0 and 365 days inclusive is created THE SYSTEM SHALL accept it.
WHEN a payment term outside that range is created THE SYSTEM SHALL reject it with a reason naming the bound.
WHEN a supplier is modelled THE SYSTEM SHALL carry its payment term as a supplier-level fact, not as a property of any template or invoice — see Q7.6.3 and the `DateFromField` rule that consumes it in change #2.
WHEN a supplier identifier is created from an empty string THE SYSTEM SHALL reject it.
WHEN the pipeline is modelled THE SYSTEM SHALL use a distinct type per stage — `UnvalidatedSupplier`, `ValidSupplier`, `StoredSupplier` — so code holding one cannot be handed another.
WHEN `MyDogsbody.Domain` is built THE SYSTEM SHALL still have zero `ProjectReference` elements, and the suppliers area SHALL name no SQLite, Dapper, LiteDB or exception type.

### Match rules

WHEN a supplier is defined THE SYSTEM SHALL allow it to carry zero or more match rules, each being a sender address, a sender domain, or a subject pattern.
WHEN a supplier carries several match rules THE SYSTEM SHALL treat them as alternatives — a message matches the supplier if **any** rule matches (Q7.6.5).
WHEN a sender-address rule is created from a value containing no `@` THE SYSTEM SHALL reject it with a reason saying so.
WHEN a sender-domain rule is created from a value containing `@` THE SYSTEM SHALL reject it with a reason saying a domain is expected, not an address.
WHEN a subject-pattern rule is created from an empty or whitespace-only value THE SYSTEM SHALL reject it.
WHEN any match rule is created from a value longer than 400 characters THE SYSTEM SHALL reject it with a reason naming the limit.
WHEN a subject pattern is stored THE SYSTEM SHALL define it as a **case-insensitive substring**, not a regular expression — see `design.md` → *Decisions taken* for why, and change #2 for where that is revisited.

### Errors

WHEN a supplier workflow fails THE SYSTEM SHALL return a `SupplierError` discriminated union case carrying the values its message is written from, never an exception and never a bare string.
WHEN a supplier name fails validation THE SYSTEM SHALL return `SupplierNameInvalid` carrying the reason.
WHEN a match rule fails validation THE SYSTEM SHALL return `MatcherInvalid` carrying the reason.
WHEN a supplier name is already in use THE SYSTEM SHALL return `SupplierNameTaken` carrying the offending name.
WHEN an edit or delete names an identifier no row carries THE SYSTEM SHALL return `SupplierNotFound` carrying that identifier.
WHEN an identifier cannot be interpreted THE SYSTEM SHALL return `SupplierIdInvalid` carrying the reason.
WHEN the store itself fails THE SYSTEM SHALL return `SupplierStoreFailed` carrying the message, so the composition root has somewhere to put an adapter failure the user must still be told about.

### Dependencies

WHEN a suppliers workflow needs the store THE SYSTEM SHALL declare it as a function type — `LoadSuppliers`, `SaveSupplier`, `UpdateSupplier`, `DeleteSupplier` — and receive a function value, never an interface, a class or a collection getter.
WHEN a suppliers workflow is tested THE SYSTEM SHALL be satisfiable with lambdas alone, requiring no database, no temp file and no mocking framework.

### Add supplier

WHEN a user submits a valid new supplier THE SYSTEM SHALL create the supplier record with its name, payment term and every match rule submitted with it.
WHEN a user submits a supplier name that is already taken THE SYSTEM SHALL return `SupplierNameTaken` and SHALL NOT call the save dependency.
WHEN a user submits a supplier whose name fails validation THE SYSTEM SHALL return `SupplierNameInvalid` and SHALL NOT call the save dependency.
WHEN a user submits a supplier one of whose match rules fails validation THE SYSTEM SHALL return `MatcherInvalid` naming the offending rule and SHALL NOT call the save dependency, so a partially valid supplier is never stored.
WHEN a user submits a supplier with no match rules THE SYSTEM SHALL accept it — a supplier with no rules simply never matches, and forbidding it would prevent entering a supplier before its rules are known.

### Edit supplier

WHEN a user submits a valid edit to an existing supplier THE SYSTEM SHALL update the name, payment term and match rules to exactly what was submitted, replacing the previous rule set rather than merging with it.
WHEN a user submits an edit naming an identifier no row carries THE SYSTEM SHALL return `SupplierNotFound` and change nothing.
WHEN a user renames a supplier to a name another supplier already holds THE SYSTEM SHALL return `SupplierNameTaken` and change nothing.
WHEN a user submits an edit that leaves the name unchanged THE SYSTEM SHALL NOT report `SupplierNameTaken` against the supplier's own existing row.

### Delete supplier

WHEN a user deletes an existing supplier THE SYSTEM SHALL remove the supplier and its match rules.
WHEN a user deletes a supplier that no longer exists THE SYSTEM SHALL return `SupplierNotFound`.

### List suppliers

WHEN the suppliers page loads THE SYSTEM SHALL return every stored supplier with its name, payment term and match rules, ordered by name.
WHEN no suppliers are stored THE SYSTEM SHALL return an empty list, not an error.

---

## Outer ring

### Supplier store

WHEN a store function is written THE SYSTEM SHALL take its dependencies as leading parameters, its input last, and return `Result<'T, MyDogsbodyException>` written with `handleError`.
WHEN a store function fails THE SYSTEM SHALL report the exact `ActionNames.*` string it declares, a message describing what it was doing, and the preserved inner exception.
WHEN a supplier is written and read back THE SYSTEM SHALL return every field unchanged, including a name containing non-ASCII characters and a payment term of zero.
WHEN the store maps a row to a domain type THE SYSTEM SHALL do so in one mapper at the bottom edge, and the domain type SHALL travel unmapped from there to the composition root.
WHEN an outer-ring function is added THE SYSTEM SHALL add exactly one `ActionNames` entry for it, composed under `ActionNames.MyDogsbody.Database.*`.

### Composition root

WHEN the suppliers API is built THE SYSTEM SHALL bind the store functions to the domain's dependency function types, mapping `MyDogsbodyException` inbound to a `SupplierError` case and the workflow's `SupplierError` outbound to a `MyDogsbodyException`.
WHEN the two error types meet THE SYSTEM SHALL do so only in `SupplierApiFactory`, nowhere else.
WHEN `SupplierApiFactory` or `SupplierApiMappers` is loaded THE SYSTEM SHALL perform no I/O at module initialisation, so both are testable without reaching `Startup.fs`.
WHEN the suppliers API returns THE SYSTEM SHALL return `Result` uncollapsed — no `|> ignore` on a write, no `failwith` on a read.
WHEN the host requests services THE SYSTEM SHALL register the suppliers API alongside the existing credentials API, and `MainWindow.xaml.cs` SHALL NOT change.

---

## User interface

### Suppliers page

WHEN a user navigates to `/settings/suppliers` THE SYSTEM SHALL display a table of every stored supplier showing its name, its payment term in days, and its match rules.
WHEN the suppliers page is reachable THE SYSTEM SHALL list it in the settings navigation menu alongside the existing settings pages.
WHEN a user presses the add button THE SYSTEM SHALL open a dialog for a new supplier's name, payment term and match rules.
WHEN a user opens an existing row THE SYSTEM SHALL open the same dialog populated with that supplier's current values.
WHEN a user saves the dialog THE SYSTEM SHALL call the API and then reload the table, so the table shows what was stored rather than what the dialog held.
WHEN a user deletes a supplier THE SYSTEM SHALL ask for confirmation before calling the API, and reload the table on success.
WHEN an API call fails THE SYSTEM SHALL display the message in a `MudAlert` and leave the table showing the last successfully loaded data.
WHEN an API call succeeds after a previous failure THE SYSTEM SHALL clear the alert.
WHEN no suppliers are stored THE SYSTEM SHALL display an empty table with a message inviting the user to add one, not an error.
WHEN the UI is built THE SYSTEM SHALL reach no further than the `SupplierApi` record and `MyDogsbody.UI.Types`; `UI.Portal` SHALL NOT gain a reference to `MyDogsbody.Domain` or `MyDogsbody.Database`.
WHEN the page's state is modelled THE SYSTEM SHALL use `cval` / `aval` / `transact` in a module creator taking `startWork` as its first parameter — no `Model`/`Msg`/`update`, no dispatch loop, no `Async.Start` inside the module creator.

---

## Testing

### Levels

WHEN a supplier workflow is added or changed THE SYSTEM SHALL have a unit test written **before** the implementation, asserting every field of the success output and the exact error case with its payload on failure.
WHEN a workflow short-circuits on a validation failure THE SYSTEM SHALL have a test proving the store dependency was never called.
WHEN a constrained type gains a `create` THE SYSTEM SHALL have a test per rule: one accepted value, one rejected value, and the rejection reason asserted.
WHEN the supplier store is tested THE SYSTEM SHALL run against a real SQLite database in a fresh temp file per test, with the schema built by calling `MigrationSetup.setupMigrations` — never hand-written DDL.
WHEN a SQLite integration test finishes THE SYSTEM SHALL dispose the connection and delete the temp file, and SHALL assert that the delete succeeded.
WHEN each new migration is added THE SYSTEM SHALL have a test that `MigrateUp` on an empty file produces the expected tables and columns, and that `Down()` reverses it.
WHEN a dependency function type is published THE SYSTEM SHALL have one shared contract suite run against the real adapter **and** against every fake used in a workflow test.
WHEN the bottom mapper is added THE SYSTEM SHALL have a contract test asserting it field-for-field in both directions, with any deliberate rename asserted as a rename.
WHEN the top mapper is added THE SYSTEM SHALL have the same, and a test asserting a constrained type survives a round trip through the store unchanged.
WHEN error translation is added THE SYSTEM SHALL have a contract test asserting each `SupplierError` case maps to the intended `MyDogsbodyException` action and message, and each adapter exception maps to the intended `SupplierError` case.
WHEN an `ActionNames` entry is added THE SYSTEM SHALL have a contract test asserting the function reports the entry it declares, and the existing structural suite SHALL continue to pass.
WHEN the suppliers flow is complete THE SYSTEM SHALL have an E2E test driving the page through the real composition path to a real SQLite file and back into the rendered markup, covering add, edit, delete, a validation failure with nothing logged, and a store failure with exactly one entry logged.
WHEN any test runs THE SYSTEM SHALL NOT reach `Startup.Startup`, whose module-level bindings open real database files in the working directory.
WHEN a test is added THE SYSTEM SHALL tag it with its level via `Trait("Level", ...)`.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors.
WHEN this change is complete THE SYSTEM SHALL pass the whole test suite with zero failures and zero skips, at all four levels.

---

## Edge cases

WHEN the main database file does not exist at startup THE SYSTEM SHALL create it and apply every migration, rather than failing.
WHEN the main database exists and is already at the latest migration THE SYSTEM SHALL start without reapplying anything.
WHEN the main database file exists but is not a valid SQLite database THE SYSTEM SHALL surface the failure as a `MyDogsbodyException` with a message naming the file, rather than an unhandled exception at module load.
WHEN two suppliers differ only by leading or trailing whitespace in their names THE SYSTEM SHALL treat them as the same name, because the name is trimmed before the uniqueness check.
WHEN two suppliers differ only by letter case THE SYSTEM SHALL treat them as the same name, so "Acme" and "ACME" cannot both exist.
WHEN a supplier name contains characters outside the Basic Multilingual Plane THE SYSTEM SHALL store and return them unchanged.
WHEN a supplier is saved with duplicate match rules THE SYSTEM SHALL store them as submitted — deduplicating is not this change's job, and a repeated rule changes no outcome.
WHEN the store returns a row whose identifier cannot be interpreted THE SYSTEM SHALL return `SupplierIdInvalid` rather than throwing.

---

## Out of scope

- Anything that **consumes** a supplier: templates, matching, extraction, invoices, calendar.
- `MatchSupplierWorkflow` — pure supplier matching lands in change #2, where `ScannedMessage` first exists.
- Import or export of suppliers.
- Merging two suppliers, or renaming one across existing invoices — there are no invoices yet.
- Any change to `Blogs` or `Comments`. They are scaffold and this change leaves them alone.
- Any change to the credentials path. Change #5 removes it; this change must not disturb it.
