# Requirements — Credentials per provider

Change **#5 of 7**. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record; question ids (`Q3.7`–`Q3.10`) and friction numbers (#5, #10) resolve there.

**What this change is for.** There is no shared credential store and no `Credentials.db`. Each
provider integration owns a `Credentials` collection inside **its own** LiteDB database — Google's
tokens live in Google's database, next to Google's registered accounts (Q3.3/Q3.4).

**This is the only change in the series that deletes working, tested code**, and it is a **pure
refactor with no new feature.** Its success criterion is *"the suite is still green and two fewer
projects exist"*.

**It must go first among the Google work.** Folding it into change #6 would mean a change that
simultaneously deletes a store, moves a UI page, rewires the composition root and adds OAuth — with
no clean point to check the suite. Split, it is a boring refactor followed by an ordinary feature.

---

## Sequencing constraint

> **This change must NOT be the first of the seven to land.**

`E2E/CredentialsFlowTests.fs` is the **only E2E test file in the suite today**. This change deletes
it, and provides no replacement, because after it there is no credentials page — the Google accounts
page in change #6 is where Google's credentials are managed. Landing this change first would leave
the suite with **zero tests at the E2E level**, which CLAUDE.md forbids.

WHEN this change is scheduled THE SYSTEM SHALL be preceded by at least one change that lands E2E coverage of its own — change #1 is the natural one, and is the recommended starting point regardless.
WHEN this change lands THE SYSTEM SHALL still have E2E tests present and passing.
WHEN `E2E/BlazorTestHarness.fs` is considered THE SYSTEM SHALL keep it — it is shared harness code, not a credentials test.

*(§4 records this change as having no dependencies. That is true of its code and false of its test
suite; the constraint is recorded here rather than left to be discovered when the gate fails.)*

---

## Removal

### Projects

WHEN this change is complete THE SYSTEM SHALL contain no `MyDogsbody.Integrations.Credentials` project.
WHEN this change is complete THE SYSTEM SHALL contain no `MyDogsbody.Integrations.Credentials.Database.Models` project.
WHEN this change is complete THE SYSTEM SHALL contain no `MyDogsbody.Enums` project.
WHEN this change is complete THE SYSTEM SHALL contain one new project, `MyDogsbody.Integrations.Google.Database.Models`, so the solution is **two projects smaller** than before.
WHEN a developer greps the solution for `Integrations.Credentials`, `MyDogsbody.Enums` or `InfrastructureType` THE SYSTEM SHALL return no matches outside historical change documents.

### The domain

WHEN this change is complete THE SYSTEM SHALL contain no `MyDogsbody.Domain/Credentials/` folder — no `CredentialsTypes.fs` and none of its three workflows (Q3.7).
WHEN this change is complete THE SYSTEM SHALL contain no `Infrastructure` union in the domain and no `InfrastructureType` enum anywhere (Q3.8).
WHEN a credential is considered THE SYSTEM SHALL treat it as a token the provider's adapter holds, not as a domain concept — nothing in the domain ever reasoned about one.
WHEN the provider is identified THE SYSTEM SHALL identify it by **which database the credential is in**, so a discriminator column would be a second source of truth for a fact the file path already states. This is the argument CLAUDE-project.md already makes for the log store — *"the collection is the severity"* — applied one tier up.

### The user interface

WHEN this change is complete THE SYSTEM SHALL contain no `/settings/credentials` route, no `CredentialsPage`, no `CredentialsComponents` and no `CredentialsBrowserModule` (Q3.10).
WHEN this change is complete THE SYSTEM SHALL show no credentials entry in the settings navigation menu.
WHEN `MyDogsbody.UI.Portal` is built THE SYSTEM SHALL reference only `MyDogsbody.UI.Types` and, transitively, `MyDogsbody.Exceptions.Types` — one project fewer than before, since `MyDogsbody.Enums` leaves the graph entirely.

### The composition root

WHEN this change is complete THE SYSTEM SHALL contain no `CredentialApiFactory.fs` and no `CredentialApiMappers.fs`.
WHEN this change is complete THE SYSTEM SHALL open no `Credentials.db`, and `Startup.fs` SHALL hold no credentials context and no `credentialApi` binding.
WHEN this change is complete THE SYSTEM SHALL register no `CredentialApi` service.

### The stored rows

WHEN the existing `Credentials.db` is considered THE SYSTEM SHALL discard it (Q3.9) — no migration, no import. It is a development database in `bin\Debug\net9.0\` holding a handful of retypeable rows.
WHEN the change is described THE SYSTEM SHALL say that plainly, rather than letting the file quietly stop being read.

---

## What arrives

WHEN a provider integration needs to store a credential THE SYSTEM SHALL store it in a `Credentials` collection inside that integration's own LiteDB database.
WHEN `MyDogsbody.Integrations.Google` is built THE SYSTEM SHALL hold a database context with a `Credentials` collection getter and a `Dispose`, following the shape the retired credentials integration used.
WHEN the Google integration's database context is built THE SYSTEM SHALL warm LiteDB's entity mapping with `BsonMapper.Global.ToDocument` for every entity before returning.
WHEN a Google credential entity is declared THE SYSTEM SHALL declare it as a mutable C# class in `MyDogsbody.Integrations.Google.Database.Models`.
WHEN a Google credential is stored THE SYSTEM SHALL store the secret and the external username, and SHALL NOT store any provider discriminator.
WHEN the Google credential store is written THE SYSTEM SHALL keep the outer-ring shape — dependencies first, input last, `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames` entry per function.
WHEN `MyDogsbody.Integrations.Google` declares its module THE SYSTEM SHALL declare it under `MyDogsbody.Integrations.Google`, correcting the existing `MyDogsbody.Infrastructure.Google` misspelling — this is the change that first makes that project real.

---

## Unchanged behaviour (regression prevention)

The point of this change is that credential *storage* keeps working while its home moves. These are
what the characterization tests lock down **before** anything moves, and what the new per-provider
collection must satisfy afterwards.

WHEN a credential is written and read back THE SYSTEM SHALL CONTINUE TO return the secret **byte-for-byte unchanged**, including leading and trailing whitespace, newlines, and non-ASCII characters — a secret that is trimmed or re-encoded is a secret that no longer works.
WHEN a credential is written and read back THE SYSTEM SHALL CONTINUE TO return the external username unchanged.
WHEN a credential is stored THE SYSTEM SHALL CONTINUE TO surface the store's `ObjectId` as a string identifier.
WHEN a credential is updated THE SYSTEM SHALL CONTINUE TO reflect the new values on re-read.
WHEN an update names an identifier no row carries THE SYSTEM SHALL CONTINUE TO report that distinctly rather than silently succeeding.
WHEN an empty secret or an empty username is submitted THE SYSTEM SHALL CONTINUE TO refuse it with a reason.
WHEN a store operation fails THE SYSTEM SHALL CONTINUE TO report a `MyDogsbodyException` carrying an action name, a message and a preserved inner exception.
WHEN an expected failure occurs THE SYSTEM SHALL CONTINUE TO pass it through unlogged, and an unexpected one SHALL CONTINUE TO be logged exactly once.
WHEN the application starts THE SYSTEM SHALL CONTINUE TO open `Logging.db` unchanged, with the same collection name and the same `ExceptionLog` field names.
WHEN every other page in the application is used THE SYSTEM SHALL CONTINUE TO behave exactly as before — nothing outside the credentials path is touched.

---

## Coverage

Friction #10: this change removes tested, working code, and **coverage goes down before it goes up.**
That is the correct outcome — code that no longer exists needs no tests — but the change must say
plainly what was removed rather than letting the total quietly drop.

WHEN this change is complete THE SYSTEM SHALL record in its outcome document the test count before and after, **per level**, and the list of test files deleted.
WHEN a test is deleted THE SYSTEM SHALL be deleted because the code it tested no longer exists, and never to make a failing suite pass.
WHEN characterization tests are written THE SYSTEM SHALL write them **before** anything is moved or deleted, and they SHALL exercise the behaviour listed under *Unchanged behaviour* against the existing store.
WHEN the move is complete THE SYSTEM SHALL run those same characterization assertions against the **new** per-provider collection.
WHEN this change is complete THE SYSTEM SHALL still have tests present and passing at all four levels.

---

## Secrets at rest

WHEN a credential is stored THE SYSTEM SHALL store it unencrypted, as the retired store did.
WHEN this decision is recorded THE SYSTEM SHALL record it as **an accepted risk, deliberately taken** (Q5.6), not as an oversight — and change #6's description SHALL carry it, because that is where OAuth refresh tokens start being written.
WHEN the retrofit is considered THE SYSTEM SHALL note that DPAPI (`ProtectedData`, `CurrentUser` scope) is the low-friction option, and that **retrofitting means re-authorising every account**, because tokens already written cannot be re-encrypted without being read first.

---

## Testing

WHEN characterization tests are added THE SYSTEM SHALL tag them with their level and keep them after the move, retargeted at the new store.
WHEN the Google credential store is added THE SYSTEM SHALL have integration tests against a fresh temp LiteDB per test, `connection=direct`, disposed before the file is deleted, with the delete asserted to have succeeded.
WHEN the Google credential entity is added THE SYSTEM SHALL have a persisted-shape test asserting the stored document's **field names** — LiteDB is schemaless, so a renamed property silently orphans stored data.
WHEN a dependency function type is published THE SYSTEM SHALL have one shared contract suite run against the real adapter and every fake.
WHEN the existing structural `ActionNames` suite runs THE SYSTEM SHALL pass unchanged, with the retired entries removed and the new ones added.
WHEN the domain isolation test runs THE SYSTEM SHALL continue to pass — `MyDogsbody.Domain` still references nothing.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors.
WHEN this change is complete THE SYSTEM SHALL pass the whole suite with zero failures and zero skips, at all four levels.
WHEN this change is complete THE SYSTEM SHALL contain exactly two fewer projects than before it started.

---

## Edge cases

WHEN stale `bin/` or `obj/` output from a deleted project remains on disk THE SYSTEM SHALL still build clean — the old artefacts are removed rather than left to be resolved by chance.
WHEN a directory rename or delete is refused because an IDE language server holds a handle THE SYSTEM SHALL work around it by removing build output first and moving files individually, and SHALL record what happened.
WHEN the solution file is edited by an IDE project system during the move THE SYSTEM SHALL restore the intended lines by hand and confirm the final diff contains only the expected changes.
WHEN `Credentials.db` remains in a developer's `bin\Debug\net9.0\` after this change THE SYSTEM SHALL simply never open it again; deleting the file is not required for correctness.

---

## Out of scope

- **OAuth, the consent flow, calendar listing, and the Google accounts page.** Change #6. This change creates the *store* the tokens will live in, and nothing that fills it.
- **Any user interface for credentials.** After this change there is none, and that is intended: nothing in the application reads a credential today, so the page was an unused surface. Change #6 supplies the Google accounts page, which *is* Google's credential page.
- **Encrypting secrets at rest.** Deferred deliberately; recorded, not built.
- **A credentials store for any provider other than Google.** The pattern is established here; the next provider copies it.
