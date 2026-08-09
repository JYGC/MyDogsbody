# Design — Credentials per provider

Change **#5 of 7**. Requirements in [`requirements.md`](requirements.md); decision record in
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## Root of the problem

`MyDogsbody.Integrations.Credentials` is a **shared** store for other integrations' secrets, keyed by
a discriminator (`InfrastructureType` / the domain's `Infrastructure` union) that says which
integration each row belongs to. Three things follow, and all three are wrong:

1. **The discriminator is a second source of truth.** A Google credential belongs to Google because
   an enum column says so — while the obvious fact, *it is the credential Google's adapter reads*,
   is not recorded anywhere. CLAUDE-project.md already rejects exactly this shape one tier down:
   *"the collection is the severity — do not add a `Severity` field"*. Put the credential in the
   provider's own database and the file path states the fact.
2. **It made a credential a domain concept.** `MyDogsbody.Domain/Credentials/` holds a constrained
   type, three workflows and an error union — for data **no workflow ever reasons about**. A
   credential is a token an adapter presents. There is no rule to express and no decision to make.
3. **It forced `MyDogsbody.Enums` to exist.** A whole C# project whose only purpose was sharing
   `InfrastructureType` between F# and C#, plus a pair of edge mappers translating it to the domain's
   own union and back — and it is in `UI.Portal`'s reference graph.

Removing the shared store removes all three at once.

---

## Scope — what moves, goes and arrives

| Goes | Arrives |
| --- | --- |
| `MyDogsbody.Integrations.Credentials` (`CredentialStore.fs`, `CredentialEntityMappers.fs`, the context module) | A `Credentials` collection and getter on the Google integration's own context record |
| `MyDogsbody.Integrations.Credentials.Database.Models` (C# `Credential`) | `MyDogsbody.Integrations.Google.Database.Models` (C# `GoogleCredential`) |
| `Credentials.db` in the working directory | Nothing — the rows live in `Google.db` |
| `MyDogsbody.Domain/Credentials/` — `CredentialsTypes.fs` and three workflows | Nothing. A credential stops being a domain concept |
| `MyDogsbody.Enums`, `InfrastructureType`, the domain's `Infrastructure` union, and the pair of edge mappers | Nothing. The database identifies the provider |
| `Startup/CredentialApiFactory.fs`, `CredentialApiMappers.fs`, the context and the binding | Nothing yet — change #6 adds `GoogleAccountApiFactory` |
| `/settings/credentials`, `CredentialsPage`, `CredentialsComponents`, `CredentialsBrowserModule*` | Nothing. Change #6's Google accounts page *is* Google's credential page |
| The rows already in `Credentials.db` | Nothing — discarded and retyped |

**Arithmetic: three projects removed against one added — the solution ends two projects smaller, one
domain area lighter, and with `/settings/credentials` gone from the nav.**

### Reference graph, after

```
 before                                   after
 ──────                                   ─────
 UI.Portal → UI.Types                     UI.Portal → UI.Types
           → Enums                                  (→ Exceptions.Types, transitively)
           (→ Exceptions.Types)

 Domain/Credentials/ (3 workflows)        —

 Startup → Integrations.Credentials       Startup → Integrations.Google
         → Domain                                 → Domain
```

`UI.Portal` referencing exactly `UI.Types` and (transitively) `Exceptions.Types` is one of the
end-state properties for the whole series, and this is the change that delivers it.

### Verified before writing this

Nothing consumes `CredentialApi` except the credentials page itself, and nothing consumes
`InfrastructureType` outside the credentials path and its tests. **No feature loses a capability it
was using** — the page was an unused surface over a store nothing read.

---

## Data models and interfaces

### `MyDogsbody.Integrations.Google.Database.Models` (C#)

```csharp
public class GoogleCredential
{
    public ObjectId Id { get; set; }
    public string Credentials { get; set; }        // the secret, byte-for-byte as entered
    public string ExternalUsername { get; set; }
    // NO InfrastructureType. The database is the provider.
}
```

Property names are carried across **unchanged** from the retired `Credential` entity, minus the
discriminator. LiteDB is schemaless, so the persisted-shape test asserts the field names rather than
just the round trip.

### `MyDogsbody.Integrations.Google`

```fsharp
// GoogleCredential.fs — the shape the retired store had, minus the discriminator
type GoogleCredentialSecret   = private GoogleCredentialSecret of string
type GoogleExternalUsername   = private GoogleExternalUsername of string
type GoogleCredentialId       = private GoogleCredentialId of string
type StoredGoogleCredential   = { Id: GoogleCredentialId; Secret: …; Username: … }

// Database/GoogleDatabaseContextModule.fs
type GoogleDatabaseContext =
    { GetCredentialCollection: unit -> ILiteCollection<GoogleCredential>
      Dispose: unit -> unit }
// change #6 adds GetAccountCollection to this same record

// GoogleCredentialStore.fs — outer ring: handleError first, Result<_, MyDogsbodyException> out
let getAll    (handleError: HandleErrorBuilder) getCollection () = …
let insertOne (handleError: HandleErrorBuilder) getCollection credential = …
let updateOne (handleError: HandleErrorBuilder) getCollection credential = …
```

**These constrained types live in the integration, not the domain.** That is the point of Q3.7: the
validation that a secret is non-empty is the adapter's own precondition, not a rule any workflow
expresses. If a future workflow ever genuinely reasons about a credential, *that* is when a domain
type earns its place.

### The naming quirk, fixed here

`MyDogsbody.Integrations.Google` currently declares its only module as
`MyDogsbody.Infrastructure.Google.GoogleCalendar` — the last survivor of the old naming.
CLAUDE-project.md says to *"leave it or fix it with whatever change first makes that project real"*.
**This is that change.** Everything in the project is declared under `MyDogsbody.Integrations.Google`.

---

## Sequence — how the move is staged

The ordering matters more than usual, because the point of this change is that the suite is green
throughout, not only at the end.

```
1. CHARACTERIZE           write tests over the behaviour being PRESERVED, against the
                          EXISTING store: secret byte-for-byte, username, id, update,
                          not-found, empty-value refusal, error shape, logging behaviour
                          ► run them. They pass. This is the baseline.

2. BUILD THE NEW HOME     Google.Database.Models, the context module (with the BsonMapper
                          warm-up), GoogleCredentialStore, its ActionNames entries
                          ► the OLD store is still there and still green

3. RE-POINT               run the SAME characterization assertions against the new store
                          ► both stores green. This is the only moment both exist, and it
                            is what makes the move provable rather than hopeful

4. DELETE                 the UI page, the composition-root pair, the domain area, the two
                          credentials projects, MyDogsbody.Enums, and the tests of code
                          that no longer exists
                          ► one deletion at a time, building between each

5. GATE                   build clean; suite green with ZERO skips at all four levels;
                          two fewer projects; no match for the retired names
```

Step 3 is why step 1 is not optional. CLAUDE.md: *"Existing behaviour you depend on but are not
changing gets a characterization test **before** you change anything near it."*

---

## Error-handling approach

Unchanged in the outer ring: `Result<'T, MyDogsbodyException>` written with `handleError`, one
`ActionNames` entry per function.

**What disappears** is the domain half. `CredentialError` and the `toCredentialError` /
`toMyDogsbodyException` pair in `CredentialApiMappers` existed only because a credential was a domain
concept. With the workflows gone, the Google credential store's `Result` goes straight to whatever
calls it — which in change #6 is the Google account factory.

```
ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.*   ← DELETED
ActionNames.MyDogsbody.Startup.CredentialApi.*                      ← DELETED
ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.getAll / insertOne / updateOne
```

The structural suite (`Contracts/ActionNamesTests.fs`) will fail if a retired entry is left behind or
a new one does not end with its binding's name.

---

## Testing strategy

### The tests that go in first

Characterization tests over the *preserved* behaviour, written and passing against the existing store
before anything moves, then re-run against the new one:

| Assertion | Why it is the one that matters |
| --- | --- |
| A secret round-trips **byte-for-byte**, including leading/trailing whitespace, embedded newlines and non-ASCII | A refresh token that has been trimmed or re-encoded is a refresh token that no longer works, and nothing on screen would say so |
| The external username round-trips unchanged | |
| The store's `ObjectId` surfaces as a string id | |
| An update reflects on re-read; an update to a missing row is reported distinctly | |
| An empty secret or username is refused with a reason | |
| A failure carries an action name, a message and a preserved inner exception | |
| An expected failure is **not** logged; an unexpected one is logged **exactly once** | Asserted through a recording `HandleErrorBuilder`, never by opening `Logging.db` |

### The tests that are deleted

Thirteen files, each because the code it tested no longer exists — **never** to make a failing suite
pass:

```
Contracts/CredentialApiContractTests.fs          Domain/Credentials/AddCredentialWorkflowTests.fs
Contracts/CredentialBoundaryMapperTests.fs       Domain/Credentials/CredentialsTypesTests.fs
Contracts/CredentialDependencyContractTests.fs   Domain/Credentials/EditCredentialWorkflowTests.fs
E2E/CredentialsFlowTests.fs                      Domain/Credentials/ListCredentialsWorkflowTests.fs
Integrations/Credentials/CredentialStoreTests.fs
Integrations/Credentials/CredentialsDatabaseContextModuleTests.fs
Startup/CredentialApiFactoryTests.fs             Startup/CredentialApiMappersTests.fs
UI/ModuleCreators/CredentialsBrowserModuleCreatorsTests.fs
```

Four files are **edited, not deleted**: `Contracts/ActionNamesTests.fs`,
`Contracts/PersistedShapeTests.fs`, `Contracts/ErrorTranslationTests.fs` and
`Contracts/DomainIsolationTests.fs` — each carries credentials cases alongside cases that survive.

**`E2E/BlazorTestHarness.fs` is kept.** It is shared harness code — `TestContext` subclass,
`AddMudServices()`, `JSRuntimeMode.Loose` — not a credentials test, and every future E2E flow needs
it.

### Two reference examples that leave with the code

`CredentialStoreTests.withStore` is cited in CLAUDE-project.md as *the* demonstration of the LiteDB
temp-file shape, and `CredentialsBrowserModuleCreatorsTests` as the demonstration of an API-record
fake. **The Google credential store tests take over both roles**, and CLAUDE-project.md is updated to
point at them.

### The E2E floor — see the sequencing constraint

`E2E/CredentialsFlowTests.fs` is the suite's only E2E file today. Deleting it with no replacement
would leave the E2E level empty, which CLAUDE.md forbids. **This change therefore cannot be the first
of the seven to land.** Change #1 is the natural predecessor and is the recommended starting point
anyway.

### Gate

Build clean; suite green with **zero skips at all four levels**; exactly two fewer projects; no grep
match for `Integrations.Credentials`, `MyDogsbody.Enums` or `InfrastructureType` outside
`docs/changes/`.

---

## Decisions taken

1. **The credential's constrained types live in the integration, not the domain.** Q3.7 removes the
   domain area outright rather than relocating it, because there was never a rule to express — only
   a non-empty check that is the adapter's own precondition.
2. **No discriminator column, anywhere.** The database is the provider. An `InfrastructureType`
   beside a row in `Google.db` would be a second source of truth for a fact the file path states.
3. **`MyDogsbody.Enums` goes with it.** Its only reason to exist was sharing that enum between F# and
   C#. Removing it is what takes `UI.Portal`'s reference set down to two.
4. **The `MyDogsbody.Infrastructure.Google` module name is corrected here**, since this is the change
   that first makes that project real.
5. **The existing rows are discarded** (Q3.9). No migration path. A development database in
   `bin\Debug\net9.0\` holding a handful of retypeable rows does not justify import code that would
   be deleted immediately afterwards — but the change description says so, rather than letting the
   file quietly stop being read.
6. **Secrets stay unencrypted** (Q5.6). An accepted risk, deliberately taken, and recorded in change
   #6's description because that is where OAuth refresh tokens — durable, silent to use, and valid
   until revoked — start being written.
7. **The `Credentials` collection name is kept.** It is the established name and carrying it across
   means the shape a future provider copies is the one already documented.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #10 — coverage goes down before it goes up.** Thirteen test files leave a suite that is currently 204 green | Correct outcome, honestly recorded: `outcome.md` states the before/after counts **per level** and lists every deleted file. A test is deleted only because its subject no longer exists |
| **The E2E level would be left empty** | The sequencing constraint in `requirements.md`. This change lands after #1 |
| **A secret is silently altered by the move** | The byte-for-byte characterization test, written first, run against both stores. This is the one failure that would be invisible until an API call failed for an unrelated-looking reason |
| **A directory rename or delete is refused by Windows** because an IDE language server holds handles | Known from the `logging-not-an-integration` change: remove `bin/` and `obj/` **first**, move or delete files individually, then repair `MyDogsbody.sln` by hand and check the diff is only the expected lines |
| **An IDE rewrites the solution file mid-move** | It has happened before, removing a whole `Project(...)` block and its twelve configuration lines. Check the final `.sln` diff explicitly |
| **Something unnoticed depends on the credentials path** | Verified before writing this design: nothing consumes `CredentialApi` outside its own page, and nothing consumes `InfrastructureType` outside the credentials path and its tests |
| **Friction #5 — plaintext secrets, now for OAuth tokens** | Deferred deliberately, recorded in two places. DPAPI is the retrofit; **retrofitting means re-authorising every account**, because tokens already written cannot be re-encrypted without being read first |
