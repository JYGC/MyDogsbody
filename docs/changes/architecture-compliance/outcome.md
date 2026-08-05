# Outcome — Architecture compliance

What was built, what changed behaviour, what was found on the way, and every place the specs or
`CLAUDE-project.md` had to bend. Requirements in [requirements.md](requirements.md), design in
[design.md](design.md), task list in [tasks.md](tasks.md).

## Result

All 12 phases and all four optional items are done.

| | Before | After |
| --- | --- | --- |
| Build | green | green, every project including the scratch tier |
| Tests | 28 — 18 Unit, 7 Integration, 3 Contract | **204 — 72 Unit, 45 Integration, 80 Contract, 7 E2E**, zero skips |
| `MyDogsbody.Domain` | did not exist | exists, references nothing, enforced by a build target *and* a contract test |
| `MyDogsbody.Spine` | the credentials path | deleted |
| Mappers per feature | 5 | 2, both at the edges |
| Domain error type | `MyDogsbodyException` everywhere | `CredentialError`, `DocumentError` |
| Store in a core signature | `unit -> CredentialsCollection` | none |
| E2E level | no harness | bUnit + MudBlazor, 7 flows |

The suite was run **8 times consecutively** at the end, green every time.

## Behaviour that changed

Three user-visible changes, all agreed at the requirements gate:

1. **Empty values are rejected.** A credential with a blank secret or username is refused with a
   message, where it was previously stored.
2. **Editing targets the row by id.** `CredentialsRepository.updateOne` matched on
   `InfrastructureType`, so with two credentials of one type it updated the first. It now matches
   on the identifier.
3. **Editing a missing credential reports not-found.** It previously returned `Ok ()` silently.

The two characterization tests that pinned (2) and (3) were replaced, in the same task, by tests
asserting the new behaviour — not deleted quietly.

## Found on the way

**A latent production race in LiteDB's global `BsonMapper`.** The new parallel test classes made
it visible: the suite failed **6 runs out of 10**, always in whichever two tests happened to race,
with a row read back carrying a null `Credentials`. LiteDB builds an entity mapping lazily and
caches it on a process-global mapper, so two threads mapping `Credential` for the first time at
once can observe a half-built mapping and silently drop a property.

This was **not** a test-only problem: the UI calls the API from `Async.Start` threads and
`writeLog` runs on whichever thread failed, so it was reachable in the running application. Fixed
at the source — each `getDatabaseContext` now warms the mapping on one thread before handing the
context out — rather than by serialising the tests. 0 failures in 10 runs afterwards, and the rule
is written into `CLAUDE-project.md` → *Per-integration databases*.

**Two wrong action strings**, both recorded in design.md → *Latent defects* and both in code this
change deleted. `Contracts/ActionNamesTests.fs` is what stops them returning: it walks the nested
modules by reflection and requires every string to end with the name of the binding that declares
it, and no two bindings to declare the same string.

**`PdfDocumentReader` leaked a file handle.** The old `ReadPdfDomain.getPdfContent` opened a
`PdfDocument` and never disposed it. The rewrite binds it with `use`.

## Specification conflicts, and how they were resolved

Four places where `CLAUDE-project.md` could not be followed literally. Each is now reflected in
that file, so the specs and the reference no longer disagree.

**1. The domain cannot use `InfrastructureType`.** The enum lives in `MyDogsbody.Enums` and the
domain may reference no project. It declares its own `Infrastructure` union; the two edge mappers
translate. *Correction to design.md:* that document claimed both directions would be exhaustive
matches. Only domain → enum is — `InfrastructureType` is a C# enum and can hold any integer, so
enum → domain returns `Result` and fails loudly on an unknown value. Contract tests walk every
declared member in both directions to catch a mismatch before production does.

**2. A translated domain error still needs an `ActionName`.** Domain workflows have none, but
`MyDogsbodyException` requires one, so it names the API operation that failed —
`ActionNames.MyDogsbody.Startup.CredentialApi.*`. `MyDogsbodyException` gained a two-argument
constructor for this: a domain error never was an exception, so it carries no inner exception.

**3. The E2E logging assertion contradicted the no-logging-reference rule.** Resolved by asserting
through a recording `handleError` callback. `writeLog` is a lambda, so the harness collects what it
was handed — no log file, no logging reference in the flow test.

**4. `MyDogsbody.Tests` now references `MyDogsbody.Logging` after all.** Phase 7 changed those
functions, so the mandate requires tests for them, and the doc's own integration section already
contemplated testing "the log store" against a temp file. Nothing touches the `Logging.db` in the
working directory.

## Manual verification

The WPF window was launched and stayed up, which proves the composition root initialises — its
module-level bindings open both databases the moment anything touches the module. **Clicking
through the window by hand was not performed**, and the flows were not driven through the real
`BlazorWebView`.

Instead, the real composition root was exercised by a throwaway console harness referencing
`MyDogsbody.Startup` and calling `Startup.credentialApi` in its own working directory — the genuine
module-level bindings, real `.db` files, no test doubles:

```
add valid                          OK
add empty secret (must reject)     ERROR: Credentials must not be empty.
add empty username (must reject)   ERROR: Username must not be empty.
listed                             1 row(s)
   6a7314332bb7c50a8064125f | Google | g-secret | person@gmail.com
edit existing                      OK
edit unknown id (must reject)      ERROR: No credential was found with id '507f1f77bcf86cd799439011'.
after edit                         g-rotated | rotated@gmail.com
```

`Credentials.db` was created; **`Logging.db` was not** — every failure above was expected, and
expected failures are never logged. That is the designed behaviour, confirmed against the real
wiring rather than a fake.

The harness lived outside the repository and was deleted. Recreate it from the recipe in
`CLAUDE-project.md` → *E2E* if a future change needs the same check.

## Not done

- **Warning / information / debug log collections.** A status in `CLAUDE-project.md`, not a
  violation; out of scope per requirements.md.
- **Wiring the main SQLite database into the application.** Also a status. Its migrations are now
  tested (`MigrateUp`, `Down`, re-apply, and a row in each table), which the mandate asked for
  independently of wiring — `MigrationSetup.rollbackAll` was added so `Down()` is exercised at all.
- **Duplicate-credential rules.** Deliberately none: fixing `updateOne` to match on id makes
  many-per-type coherent.
- **The `MyDogsbody.Integrations.Google` module-name quirk** (`MyDogsbody.Infrastructure.Google.GoogleCalendar`).
  A one-line stub in the scratch tier. The `Domian` and `Infrustructure` misspellings are gone.
