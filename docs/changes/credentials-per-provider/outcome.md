# Outcome — Credentials per provider

Change **#5 of 7**. [`requirements.md`](requirements.md) · [`design.md`](design.md) ·
[`tasks.md`](tasks.md) · [decision record](../invoice-to-calendar/background.md)

Branch `change/credentials-per-provider`, cut from `main` at `6f449ab` (change #4 merged).

**This was a pure refactor with no new feature.** The success criterion was *"the suite is still
green and two fewer projects exist"*. Both hold.

---

## The headline: coverage went down before it goes up (friction #10)

Thirteen test files were deleted. **Every one because the code it tested no longer exists — never
to make a failing suite pass.** Said plainly, this change removed:

- **Three domain workflows** — `AddCredentialWorkflow`, `EditCredentialWorkflow`,
  `ListCredentialsWorkflow` — with `CredentialsTypes.fs` (the `CredentialError` DU, the
  `Infrastructure` union, three constrained types, three dependency function types). The whole
  `MyDogsbody.Domain/Credentials/` area (Q3.7). A real, if small, loss of domain-level coverage,
  stated here rather than left to blur.
- **A store** — `CredentialStore.fs` (`getAll` / `insertOne` / `updateOne`) and its context module.
- **Two mappers** — `CredentialEntityMappers.fs` (bottom) and `CredentialApiMappers.fs` (top, with
  the `CredentialError` ⇆ `MyDogsbodyException` translation pair).
- **A UI page** — `/settings/credentials`, `CredentialsPage`, `CredentialsComponents` (incl. the
  `CredentialsEditorDialog`), `CredentialsBrowserModuleCreators`, `CredentialsBrowserModule`,
  `CredentialApi`, `IntegrationCredentialUiType`.
- **Three projects** — `MyDogsbody.Integrations.Credentials`,
  `MyDogsbody.Integrations.Credentials.Database.Models`, `MyDogsbody.Enums` (with
  `InfrastructureType` and, by extension, the domain's `Infrastructure` union and the edge-mapper
  pair — Q3.8).

### Test totals, per level

| Level | Before (branch head, pre-Phase 1) | After (Phase 5 gate) | Δ |
| --- | --- | --- | --- |
| Unit | 738 | 706 | −32 |
| Integration | 274 | 270 | −4 |
| Contract | 299 | 255 | −44 |
| E2E | 37 | 30 | −7 |
| **Total** | **1348** | **1261** | **−87** |

Zero skips before and after. All four levels present and passing.

The net is −87, not −(the count of the deleted files): the retired credentials tests numbered
~130, and this change *added* ~43 (the Google credential store's own unit / integration / contract
coverage). What is genuinely gone is the domain-workflow coverage for a concept that is no longer a
domain concept.

### Test files deleted (13)

```
Domain/Credentials/CredentialsTypesTests.fs          Contracts/CredentialApiContractTests.fs
Domain/Credentials/AddCredentialWorkflowTests.fs     Contracts/CredentialBoundaryMapperTests.fs
Domain/Credentials/EditCredentialWorkflowTests.fs    Contracts/CredentialDependencyContractTests.fs
Domain/Credentials/ListCredentialsWorkflowTests.fs   Contracts/ErrorTranslationTests.fs
Integrations/Credentials/CredentialStoreTests.fs     Startup/CredentialApiFactoryTests.fs
Integrations/Credentials/CredentialsDatabaseContextModuleTests.fs   Startup/CredentialApiMappersTests.fs
UI/ModuleCreators/CredentialsBrowserModuleCreatorsTests.fs
```

Plus `Integrations/Credentials/CredentialCharacterizationTests.fs` — the Phase 1 baseline file,
deleted in Phase 4 once its assertions had been carried into
`Integrations/Google/GoogleCredentialCharacterizationTests.fs` (Phase 3).

### Test files edited, not deleted (5)

`Contracts/ActionNamesTests.fs` (now covers `GoogleCredentialStore` + asserts no retired
`.Integrations.Credentials.` / `.CredentialApi.` entry survives), `Contracts/PersistedShapeTests.fs`
(the log-store half kept; the credential half gone), `Contracts/DomainIsolationTests.fs` (references
`SupplierError` instead of `CredentialError` for the "domain assembly links no MyDogsbody assembly"
check), `Logging/ExceptionStoreTests.fs` (sample action string), plus the two surviving E2E harness
files had a stale comment updated.

### Test files added (7)

```
Integrations/Google/GoogleCredentialTypesTests.fs            (Unit)
Integrations/Google/GoogleDatabaseContextModuleTests.fs      (Integration)
Integrations/Google/GoogleCredentialStoreTests.fs            (Integration)
Integrations/Google/GoogleCredentialCharacterizationTests.fs (Integration + Unit)
Contracts/GoogleCredentialPersistedShapeTests.fs             (Contract)
Contracts/GoogleCredentialDependencyContractTests.fs         (Contract, shared real+fake suite)
```
(6 files; the 7th slot is the transient Phase 1 `CredentialCharacterizationTests.fs`.)

---

## Project arithmetic

| | |
| --- | --- |
| Before | 25 projects |
| Removed | `MyDogsbody.Integrations.Credentials`, `MyDogsbody.Integrations.Credentials.Database.Models`, `MyDogsbody.Enums` |
| Added | `MyDogsbody.Integrations.Google.Database.Models` |
| After | **23 projects — exactly two fewer** |

`MyDogsbody.Integrations.Google` was already in the solution as a one-line stub; this change made it
real (and corrected its module name from `MyDogsbody.Infrastructure.Google.GoogleCalendar` to
`MyDogsbody.Integrations.Google.GoogleCalendar` — the last of the old naming).

`MyDogsbody.UI.Portal` now declares **exactly one** `ProjectReference` (`MyDogsbody.UI.Types`);
`MyDogsbody.Enums` left `UI.Portal`'s graph entirely. `MyDogsbody.Domain` still declares no
reference and has one fewer folder.

---

## The stored rows were discarded, not migrated (Q3.9)

There was no `Credentials.db` migration and no import path. Any rows a developer had in
`bin\Debug\net9.0\Credentials.db` are gone; the handful of retypeable values are re-entered on the
Google accounts page in change #6. `Startup.fs` simply never opens `Credentials.db` again. The file,
if present on a machine, is inert — deleting it is optional (task O.1).

---

## Secrets remain unencrypted at rest — a deliberate, accepted risk (Q5.6)

The Google credential store writes the secret in plain text, exactly as the retired store did. This
is a legitimate call for a single-user desktop app on a machine the user controls — **it is on the
record as a decision, not an oversight.**

**Change #6's description must carry this forward**, because that is where OAuth refresh tokens —
durable, silent to use, valid until revoked — start being written. The low-friction retrofit is
DPAPI (`ProtectedData`, `CurrentUser` scope); note that retrofitting means **re-authorising every
account**, because tokens already written cannot be re-encrypted without being read first.

---

## Deviations from the specs

The specs were written before the code was measured. Three places where reality differed:

### 1. The shared store did not round-trip whitespace — the new store fixes it

`requirements.md`'s regression clause says the secret SHALL CONTINUE TO round-trip *"byte-for-byte
unchanged, including leading and trailing whitespace"*. **The retired shared store did not do that.**
LiteDB's `BsonMapper.Global.TrimWhitespace` defaults to `true`, so every string property was trimmed
on write — `"  padded  "` was stored and read back as `"padded"`. The Phase 1 characterization test
recorded this as-is (`the shared store trims leading and trailing whitespace from the secret`).

The new `GoogleDatabaseContextModule` uses a **local `BsonMapper`** (not `BsonMapper.Global`) with
`TrimWhitespace` and `EmptyStringToNull` switched off. So the per-provider store *does* round-trip
the secret byte-for-byte — which is what the requirement actually asks for, and what an OAuth refresh
token needs. This is the one Phase 1 assertion the new store deliberately does **not** reproduce: it
does better. A local mapper also means this store is not exposed to the documented `BsonMapper.Global`
first-use race (the known suite flake).

### 2. `E2E/BlazorTestHarness.fs` was deleted, not kept

`design.md` and task 4.1 say to keep `E2E/BlazorTestHarness.fs` as "shared harness code every future
E2E flow needs". In fact it was **entirely credentials-specific** — `CredentialsHarness`,
`withCredentialsHarness`, `withUnreachableStoreHarness`, all referencing `CredentialApi` /
`CredentialApiFactory` / `CredentialsDatabaseContextModule` — and its only consumer was
`CredentialsFlowTests.fs`. The Suppliers, MailAccounts and Invoices E2E areas each already carry
their own harness of the same shape. Keeping `BlazorTestHarness.fs` would have left it referencing
three deleted projects. It was deleted; the E2E level stays populated (30 tests across 3 areas).

### 3. `Contracts/ErrorTranslationTests.fs` was deleted, not edited

Task 4.8 lists it among the four files to "edit, not delete — carries credentials cases alongside
surviving ones". It had **no** surviving cases — every test exercised
`CredentialApiMappers.toMyDogsbodyException` / `toCredentialError`, both of which are gone. Deleted.
The other three files on that list (`ActionNamesTests`, `PersistedShapeTests`,
`DomainIsolationTests`) genuinely did carry surviving cases and were edited.

### 4. Grep gate — the surviving hits are intentional

Task 5.3 asks for "no match for `Integrations.Credentials`, `MyDogsbody.Enums` or
`InfrastructureType` in code … the only hits are in `docs/changes/`". The surviving in-code hits are:
`Contracts/ActionNamesTests.fs`'s **negative assertions** (`Assert.DoesNotContain(".Integrations.Credentials.", …)`
— the test that enforces the gate must contain the literal), and a handful of explanatory comments
in the new Google code and edited tests that name what was removed. No live reference to any retired
type, project or member remains.

---

## Naming quirk closed

`CLAUDE-project.md`'s "one quirk survives" note — `MyDogsbody.Integrations.Google` declaring its
module as `MyDogsbody.Infrastructure.Google.GoogleCalendar` — is resolved. The module is now
`MyDogsbody.Integrations.Google.GoogleCalendar`. `GetInfrustructureCredentialCallback` /
`OnCredentialSubmitted` are both gone with `CredentialsComponents.fs`.

---

## Gate — Phase 5

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | **0 errors.** 3 warnings, all pre-existing (`PdfProcessing` scratch project; `PdfDocumentReaderTests.fs` FS0760; `ScanWindowStoreTests.fs` FS0020) — none introduced here; the change removed one warning (`CredentialDependencyContractTests.fs` FS0020). |
| `dotnet test` | **1261 passed, 0 failed, 0 skipped.** Unit 706 / Integration 270 / Contract 255 / E2E 30 — all four levels present. |
| Grep for retired names | No live reference (see Deviation 4). |
| Project count | 23 — exactly two fewer than the 25 before. |
| `Contracts/DomainIsolationTests.fs` + `AssertDomainReferencesNothing` | Pass. `MyDogsbody.Domain` has one fewer folder. |
| `MyDogsbody.UI.Portal` `.fsproj` | Exactly one `ProjectReference`. |
| `MyDogsbody/MainWindow.xaml.cs` | Untouched (`git status` clean for `MyDogsbody/`). |

### Task 5.7 — running the app — manual coverage, performed

Driving the real WPF `BlazorWebView` window is out of the suite's scope (the established
convention), so this was a manual pass. `dotnet run --project MyDogsbody/MyDogsbody.csproj`:

- The app launched and closed cleanly — exit 0, no stack trace. A missing `CredentialApi`
  registration or a `TypeInitializationException` in `Startup` (the module opens its databases the
  moment anything touches it) would have thrown; nothing did.
- It created `MyDogsbody.db` and `Thunderbird.db` in the working directory (the repo root) and
  **no `Credentials.db`** — not at the root, not under `bin\`. `Logging.db` was not created either,
  which means nothing was logged as an exception during the run.
- The settings navigation listed Suppliers / Mail accounts / Scan windows / Logs → Exceptions, with
  **no Credentials entry**. Every remaining page (`/`, `/settings`, `/settings/suppliers`,
  `/settings/mail-accounts`, `/settings/scan-windows`, `/settings/exceptionlogs`, `/invoices`)
  rendered without an error boundary or `MudAlert`.

---

## PR review — round 1

One finding, fixed in a single commit; the Phase 5 tables above are left as the historical
record of what Phase 5 measured.

**Finding (reviewer): the new bottom boundary mapper had no field-for-field contract test.**
`GoogleCredentialEntityMappers.fs` is new code in this change and is *the* bottom mapping point of
the Google integration, but its only coverage was transitive, through `GoogleCredentialStore` and
the persisted-shape / dependency-contract suites. CLAUDE.md is explicit — *"Every mapper at a ring
boundary is asserted field-for-field in both directions, with deliberate renames asserted as
renames"* and *"Adding … a boundary mapper … means adding its contract test in the same change."*
The retired `Contracts/CredentialBoundaryMapperTests.fs` had exactly this for `CredentialEntityMappers`;
its bottom-mapper half was not carried over (only its `InfrastructureType` half was genuinely
obsolete).

Fix: added `Contracts/GoogleCredentialBoundaryMapperTests.fs` (Contract, 9 cases) — `toNewEntity`
and `toStoredCredential` field-for-field, both `toStoredCredential` null-property `Error` branches,
`applyEdit` (identifier preserved), `toObjectId`, a byte-for-byte pure round trip, and the two
deliberate renames asserted as renames (integration `Secret` / `Username` ⇄ persisted
`Credentials` / `ExternalUsername`). The mapper was already correct — verified by probe before the
test was written — so these lock in behaviour rather than fixing a bug.

Also scrubbed two comments that named symbols this change deleted: `MyDogsbody.Domain/Result.fs`
(`CredentialError` → `SupplierError`, an illustrative type) and `MyDogsbody.Startup/SupplierApiMappers.fs`
(dropped a `same as CredentialApiMappers` cross-reference — the same file's other `CredentialApiMappers`
mention was already removed by this change, this one was missed).

### Totals after round 1

| Level | Phase 5 gate | After round 1 | Δ (round 1) |
| --- | --- | --- | --- |
| Unit | 706 | 706 | — |
| Integration | 270 | 270 | — |
| Contract | 255 | 264 | +9 |
| E2E | 30 | 30 | — |
| **Total** | **1261** | **1270** | **+9** |

Build: 0 errors, same 3 pre-existing warnings. `dotnet test`: 1270 passed, 0 failed, 0 skipped.
Project count unchanged at 23; `MyDogsbody/MainWindow.xaml.cs` still untouched.

---

## PR review — rounds 2 and 3

Round 2 found nothing and pushed nothing. Round 3 re-read the whole diff cold and found **one**
finding, documentation-only; no production code or test changed, so the totals above are unchanged
(1270 passed / 0 failed / 0 skipped; Unit 706 · Integration 270 · Contract 264 · E2E 30).

**Finding (round 3): `CLAUDE-project.md`'s reference-direction bullet stated a rule this change
falsified.** It read *"Integrations reference `Domain` and implement the function types it
declares"* — unconditionally. After this change `MyDogsbody.Integrations.Google` is a real
integration with a store and **no `Domain` reference**, which is the deliberate consequence of
Q3.7 (a credential is not a domain concept, so there is no function type for it to satisfy).
Task 6.1 claimed the reference-direction bullets were updated; this one was missed, leaving the
repo's governing instruction file asserting a rule one of its three integrations breaks — an
invitation for the next change to "fix" it by adding a reference the design deliberately omits.
The bullet now says which integrations reference `Domain`, which does not, and why.

### Verified and rebutted, not changed

Each of these was reproduced against the checked-out head before being dropped:

- **The local `BsonMapper` (deviation 1) is sound.** Probed directly: `BsonMapper.Global` really
  does have `TrimWhitespace = true` / `EmptyStringToNull = true`, and serialising
  `"  1//0abc\tDEF   \n"` through it returns `"1//0abc\tDEF"` — the retired store did silently
  trim every secret. A local mapper with both switches off returns the input byte-for-byte.
- **Deleting `E2E/BlazorTestHarness.fs` (deviation 2) was forced, not optional.** The file declared
  `CredentialsHarness` / `withCredentialsHarness` / `withUnreachableStoreHarness` over
  `CredentialApi`, `CredentialApiFactory` and `CredentialsDatabaseContextModule` — three deleted
  projects — and its only consumer was `CredentialsFlowTests.fs`. Nothing shared survived it.
- **Deleting `Contracts/ErrorTranslationTests.fs` (deviation 3) lost nothing.** All nine cases
  exercised `CredentialApiMappers.toMyDogsbodyException` / `toCredentialError`; zero surviving
  cases, contrary to what task 4.8 assumed.
- **`GoogleDatabaseContext`'s `IDisposable` implementation does not self-recurse.** `this.Dispose()`
  inside the interface member resolves to the record's `Dispose` *field*, not the explicit
  interface method — probed: the field runs exactly once.
- Every retired name's surviving grep hit is a negative assertion or an explanatory comment
  (deviation 4), and the three deleted project directories left on disk are empty.

### Deferred to change #6 — flagged, not fixed here

Both are behaviour carried over verbatim from the retired store, with no caller today. Changing
either would be a behaviour change inside a change whose success criterion is *"the suite is still
green and two fewer projects exist"*, so they belong with the code that first calls the store:

- **`GoogleCredentialStore.updateOne` ignores `collection.Update`'s `bool`.** If the row vanishes
  between `FindById` and `Update`, the caller gets `Ok (Some …)` for a write that did not land.
- **The dependency contract suite's fake and the real adapter diverge on a malformed identifier.**
  `GoogleCredentialId.create` accepts any non-whitespace string, but the real adapter's `toObjectId`
  needs 24 hex characters — so `"abc"` yields a logged `Error` from the adapter and `Ok None` from
  the fake. Change #6 should either tighten `GoogleCredentialId.create` or add the case to the
  shared suite.
