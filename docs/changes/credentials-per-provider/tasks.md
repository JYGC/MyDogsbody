# Tasks — Credentials per provider

Change **#5 of 7**. [`requirements.md`](requirements.md) · [`design.md`](design.md) ·
[decision record](../invoice-to-calendar/background.md)

**Branch: `change/credentials-per-provider`, cut from `main` once #1 has merged** (see the sequencing
constraint below). Everything in this file lands on it, and it merges **only** when Phase 5 has passed
in full — zero build errors, zero test failures, zero skips, all four levels. No other change shares
this branch, and none of this work happens on `main`. **This matters more here than anywhere else in
the series:** the success criterion is *"the suite is still green and two fewer projects exist"*, and
a dropped test count is impossible to attribute if anything else is in the diff.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**This is a refactor with no new feature.** The success criterion is *"the suite is still green and
two fewer projects exist"*.

**The ordering rule here is not the usual one.** Almost nothing new is written, so there is little to
test first. What replaces it is stricter: **characterization tests over the behaviour being
preserved go in before anything moves**, and the suite is checked between deletions rather than only
at the end.

> ### Sequencing constraint — read before scheduling
> **This change must not be the first of the seven to land.** It deletes
> `E2E/CredentialsFlowTests.fs`, the suite's only E2E file, and provides no replacement. Landing it
> first leaves the E2E level empty, which CLAUDE.md forbids. Land change #1 first.

**No migrations in this change.** Nothing goes near the main SQLite database.

---

## Phase 1 — Characterize (required, first — nothing moves until this passes)

CLAUDE.md: *"Existing behaviour you depend on but are not changing gets a characterization test
before you change anything near it."*

- [x] **1.1** `Integrations/Credentials/CredentialCharacterizationTests.fs`, against the **existing**
      store, asserting the *Unchanged behaviour* list in `requirements.md`:
      secret round-trips (embedded newline / tab / non-ASCII / very long); the external username
      round-trips; the `ObjectId` surfaces as a string id; an update reflects on re-read; an update
      to a missing row is reported distinctly; an empty secret and an empty username are each refused
      with a reason.
      *Outcome:* 15 tests pass. **Finding:** the shared store does **not** round-trip leading/trailing
      whitespace — LiteDB `BsonMapper.Global.TrimWhitespace` defaults to `true`. requirements.md's
      "including leading and trailing whitespace" clause is not met today; characterized as-is, and
      the new per-provider store is built with a local `BsonMapper` (`TrimWhitespace`/
      `EmptyStringToNull` off) so it *does* meet it. Deviation recorded in `design.md` and `outcome.md`.
- [x] **1.2** Error-shape characterization: a store failure carries the declared `ActionNames`
      string, the message and a **preserved inner exception**, logged once; an expected validation
      failure reaches the caller as `Error` and is **never logged** — asserted through a recording
      `HandleErrorBuilder`, never by opening `Logging.db`.
- [x] **1.3** Current test totals per level: **Unit 738 / Integration 274 / Contract 299 / E2E 37 =
      1348**, zero skips (measured against `change/credentials-per-provider` head before Phase 1).

## Phase 2 — Build the new home (required, old store still in place)

- [x] **2.1** Created `MyDogsbody.Integrations.Google.Database.Models` (C#, net9.0) with
      `GoogleCredential` — `Id`, `Credentials`, `ExternalUsername`. **No discriminator.** No
      `MyDogsbody.Enums` reference.
- [x] **2.2** `MyDogsbody.Integrations.Google` references the new Models project,
      `MyDogsbody.Builders`, `MyDogsbody.Exceptions.Types` and the `LiteDB` package. No `Domain`.
- [x] **2.3** Module name corrected: `MyDogsbody.Infrastructure.Google.GoogleCalendar` →
      `MyDogsbody.Integrations.Google.GoogleCalendar`. Last of the old naming, gone.
- [x] **2.4** `GoogleCredentialTypes.fs` (named for the `*Types.fs` convention, not the sketch's
      `GoogleCredential.fs` which collides with the C# class name) — `GoogleCredentialSecret`,
      `GoogleExternalUsername`, `GoogleCredentialId`, `ValidGoogleCredential`,
      `ValidGoogleCredentialEdit`, `StoredGoogleCredential`. In the **integration**, not the domain.
      Reason strings identical to the retired store's. Tests: accept + reject-per-rule with the
      reason asserted.
- [x] **2.5** `Database/GoogleDatabaseContextModule.fs` — `GetCredentialCollection` + `Dispose`,
      warm-up before returning. **Deviation:** a *local* `BsonMapper` (not `BsonMapper.Global`)
      with `TrimWhitespace`/`EmptyStringToNull` off — so a secret round-trips byte-for-byte incl.
      whitespace (see Phase 1 finding), and the store is off the documented global first-use race.
      Plus `Database/Types/GoogleDatabaseContext.fs` (`GoogleCredentialsCollection`, the record).
      Also `GoogleCredentialEntityMappers.fs` — the bottom mapper (no `InfrastructureType` pair).
      Tests *(Integration)*: round-trips; Dispose lets the file delete (asserted without try/with);
      whitespace preserved; warm-up complete before return.
- [x] **2.6** `GoogleCredentialStore.fs` — `getAll` / `insertOne` / `updateOne`, `handleError`
      first, collection getter next, input last, `Result<_, MyDogsbodyException>` out. Messages
      identical to the retired store's. Tests *(Integration)*: full CRUD + error shape.
- [x] **2.7** `ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.{getAll,insertOne,updateOne}`.
- [x] **2.8** Gate: `dotnet build MyDogsbody.sln` clean (1 pre-existing warning), suite green —
      **1391 tests** (1348 + 15 characterization + 28 Google), 0 skips. Both stores exist and both
      are tested.

## Phase 3 — Re-point (required) — the moment the move becomes provable

- [x] **3.1** `Integrations/Google/GoogleCredentialCharacterizationTests.fs` — the Phase 1
      assertions retargeted at the **new** store. Every one carries over **except** the
      whitespace-trim characterization: the new store does *not* trim, so a secret round-trips
      byte-for-byte — which is what the requirement actually wants. Called out in the file and in
      `outcome.md`.
- [x] **3.2** `Contracts/GoogleCredentialPersistedShapeTests.fs` — asserts the stored document's
      field names (`_id`, `Credentials`, `ExternalUsername`), that `Username`/`InfrastructureType`/
      `Infrastructure`/`Provider` are **not** present, that the collection is named `Credentials`
      and is the only one, and that surrounding whitespace is persisted intact.
- [x] **3.3** `Contracts/GoogleCredentialDependencyContractTests.fs` — the three store operations
      as a shared `[<Theory>]` suite over the real adapter and an in-memory fake, so change #6's
      fake cannot drift.
- [x] **3.4** Gate: build clean, suite green — **1426 tests**, 0 skips.

## Phase 4 — Delete (required) — one at a time, building between each

- [x] **4.1** UI page deleted: `CredentialsPage.fs`, `Components/CredentialsComponents.fs`,
      `ModuleCreators/CredentialsBrowserModuleCreators.fs`, the `Shell.fs` route, the
      `SettingsComponents` nav entry. Deleted `UI/ModuleCreators/CredentialsBrowserModuleCreatorsTests.fs`
      and `E2E/CredentialsFlowTests.fs`. **Deviation:** `E2E/BlazorTestHarness.fs` was **also
      deleted** — it turned out to be entirely credentials-specific (`CredentialsHarness`,
      `withCredentialsHarness`, referencing `CredentialApi`/`CredentialApiFactory`), its only
      consumer `CredentialsFlowTests`. Suppliers / MailAccounts / Invoices each carry their own
      harness of the same shape; the E2E level stays populated (30 tests, 3 areas). Recorded in
      `outcome.md`.
- [x] **4.2** `MyDogsbody.UI.Types`: `CredentialApi.fs`, `IntegrationCredentialUiType.fs`,
      `Modules/CredentialsBrowserModule.fs`, and the `MyDogsbody.Enums` `ProjectReference`.
- [x] **4.3** `MyDogsbody.UI.Portal`: `MyDogsbody.Enums` `ProjectReference` removed. **`UI.Portal`
      now declares exactly one `ProjectReference` (`UI.Types`)** — an end-state property for the
      whole series, delivered.
- [x] **4.4** Composition root: `CredentialApiFactory.fs`, `CredentialApiMappers.fs`, the
      `Credentials.db` context / `credentialApi` binding / `AddSingleton<CredentialApi>` in
      `Startup.fs`, and its module doc comment. Deleted `Startup/CredentialApiFactoryTests.fs`,
      `Startup/CredentialApiMappersTests.fs`.
- [x] **4.5** `MyDogsbody.Domain/Credentials/` — `CredentialsTypes.fs` + all three workflows (Q3.7).
      Deleted the four `Domain/Credentials/*Tests.fs`. The domain is one area lighter and still
      references nothing.
- [x] **4.6** `MyDogsbody.Integrations.Credentials` + `.Database.Models` deleted (`bin/obj` cleared
      first; `git rm -r` succeeded without the Windows handle problem). Deleted
      `Integrations/Credentials/CredentialStoreTests.fs`, `CredentialsDatabaseContextModuleTests.fs`,
      and the Phase 1 `CredentialCharacterizationTests.fs`.
- [x] **4.7** `MyDogsbody.Enums` deleted (Q3.8).
- [x] **4.8** Edited `Contracts/ActionNamesTests.fs` (covers `GoogleCredentialStore`; asserts no
      `.Integrations.Credentials.` / `.CredentialApi.` entry survives), `Contracts/PersistedShapeTests.fs`
      (log-store half kept), `Contracts/DomainIsolationTests.fs` (`SupplierError` now),
      `Logging/ExceptionStoreTests.fs` (sample action). Deleted `Contracts/CredentialApiContractTests.fs`,
      `Contracts/CredentialBoundaryMapperTests.fs`, `Contracts/CredentialDependencyContractTests.fs`.
      **Deviation:** `Contracts/ErrorTranslationTests.fs` was **deleted, not edited** — every case
      tested `CredentialApiMappers`, which is gone; it had no surviving cases. Recorded in `outcome.md`.
- [x] **4.9** `MyDogsbody.sln`: three `Project(...)` blocks + their config lines removed via
      `dotnet sln remove`; one added (Phase 2). Diff checked by hand — only the three expected blocks
      and their 12 config lines each; nothing else touched. 25 → 23 projects.
- [x] **4.10** `ActionNames.MyDogsbody.Integrations.Credentials.*` and
      `ActionNames.MyDogsbody.Startup.CredentialApi.*` removed; the structural suite passes.
- [x] **4.11** All `bin/`/`obj/` under `MyDogsbody*` cleared; clean `dotnet build MyDogsbody.sln`
      from scratch succeeds.
      *Gate:* **1261 tests**, 0 skips — Unit 706 / Integration 270 / Contract 255 / E2E 30.

## Phase 5 — Gate (required)

- [x] **5.1** `dotnet build MyDogsbody.sln` — **0 errors.** 3 warnings, all pre-existing
      (`PdfProcessing` scratch; `PdfDocumentReaderTests.fs` FS0760; `ScanWindowStoreTests.fs` FS0020)
      — none from this change, which in fact removed one (`CredentialDependencyContractTests.fs`
      FS0020). "Zero warnings" was aspirational; the baseline already had these.
- [x] **5.2** `dotnet test` — **0 failures, 0 skips**, all four levels present (Unit 706 /
      Integration 270 / Contract 255 / E2E 30 = 1261).
- [x] **5.3** Grep — no live reference to any retired name. Surviving hits are `ActionNamesTests`'s
      negative assertions (must contain the literal) and explanatory comments in new code. Recorded
      in `outcome.md` deviation 4.
- [x] **5.4** 23 projects — exactly two fewer than the 25 before.
- [x] **5.5** `Contracts/DomainIsolationTests.fs` + `AssertDomainReferencesNothing` pass; domain has
      one fewer folder (areas: Documents, Suppliers, InvoiceTemplates, Invoices, MailAccounts).
- [x] **5.6** `MyDogsbody.UI.Portal.fsproj` lists exactly one `ProjectReference`.
- [ ] **5.7** Run the app — **manual, not performed this session.** Verified by inspection instead:
      host builds clean; `Startup.fs` opens no `Credentials.db` and registers no `CredentialApi`;
      `Shell.fs` drops the `/settings/credentials` route; `SettingsComponents.fs` drops the nav entry.
      A manual pass is recommended before merge. See `outcome.md`.
- [x] **5.8** `MyDogsbody/MainWindow.xaml.cs` untouched (`git status` clean for `MyDogsbody/`).

## Phase 6 — Documentation (required)

- [ ] **6.1** `CLAUDE-project.md`: remove the three projects from the structure table; remove
      `InfrastructureType` and the domain's `Infrastructure` union from the reference-direction
      notes; update `UI.Portal`'s reference set to two; remove the last naming quirk (the
      `MyDogsbody.Infrastructure.Google` line is now obsolete); **re-point the two reference examples
      that left with the deleted code** — `CredentialStoreTests.withStore` as the LiteDB temp-file
      shape and `CredentialsBrowserModuleCreatorsTests` as the API-record fake — at their Google
      replacements; update the *Build state* totals.
- [x] **6.2** `outcome.md` written — before/after totals per level, every deleted test file, the
      "three domain workflows + a store + two mappers + a UI page removed with their tests"
      statement, `Credentials.db` rows **discarded not migrated** (Q3.9), **secrets unencrypted at
      rest as a deliberate accepted risk** (Q5.6) to be repeated in change #6, and the four
      spec deviations.
- [ ] **6.3** Open `change/credentials-per-provider` for review, with this file's checkboxes ticked
      and `outcome.md` on the branch. **Merge only after Phase 5 passed in full.**
      *The review question for this branch is not "does it work" but "is anything gone that should
      not be" — which is only answerable because nothing else is in the diff.*

---

## Optional

- [ ] **O.1** Delete `Credentials.db` from `bin\Debug\net9.0\` on developer machines. Not required
      for correctness — after this change nothing opens it — but it stops someone finding a stale
      file and wondering.
- [ ] **O.2** Encrypt the secret at rest with DPAPI (`ProtectedData`, `CurrentUser`). **Explicitly
      deferred** (Q5.6). Note that retrofitting means re-authorising every account, because tokens
      already written cannot be re-encrypted without being read first.

## Known risks carried into this change

- **Friction #10 — coverage drops.** Thirteen test files leave. Correct, and recorded per level in
  6.2 rather than allowed to blur.
- **The E2E level would be left empty.** Hence the sequencing constraint at the top of this file.
- **A secret silently altered by the move.** Task 3.1's byte-for-byte assertion is the guard, and it
  is the reason Phase 1 comes before Phase 2.
- **Windows refusing a directory delete, or an IDE rewriting `MyDogsbody.sln`.** Both have happened
  in this repository before; the workarounds are in tasks 4.6 and 4.9.
- **Friction #5 — plaintext secrets, about to hold OAuth refresh tokens.** Deferred deliberately and
  recorded twice.
