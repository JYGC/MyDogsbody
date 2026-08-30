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

- [ ] **2.1** Create `MyDogsbody.Integrations.Google.Database.Models` (C#, net9.0) with
      `GoogleCredential` — `Id`, `Credentials`, `ExternalUsername`. **No discriminator.**
      *Outcome:* property names carried across from the retired entity, minus the enum.
- [ ] **2.2** `MyDogsbody.Integrations.Google` references it, `MyDogsbody.Builders` and
      `MyDogsbody.Exceptions.Types`.
- [ ] **2.3** Correct the module name: `MyDogsbody.Infrastructure.Google.GoogleCalendar` →
      `MyDogsbody.Integrations.Google.*`. **This is the change that first makes the project real, so
      it is the change that fixes the last naming quirk.**
- [ ] **2.4** *(test-first)* `GoogleCredential.fs` — the constrained types, in the **integration**,
      not the domain.
      Tests: one accepted and one rejected value per rule with the reason asserted.
- [ ] **2.5** *(test-first)* `Database/GoogleDatabaseContextModule.fs` — `GetCredentialCollection`
      and `Dispose`, with a `BsonMapper.Global.ToDocument` warm-up before returning.
      Tests *(Integration)*: a context over a temp database can be disposed and **the file then
      deletes successfully**; the warm-up runs.
      *Note:* change #6 adds `GetAccountCollection` to this same record.
- [ ] **2.6** *(test-first)* `GoogleCredentialStore.fs` — `getAll`, `insertOne`, `updateOne`. Outer
      ring: `handleError` first, dependencies next, input last, `Result<_, MyDogsbodyException>` out.
      *Depends on:* 2.5, 2.4.
- [ ] **2.7** `ActionNames.MyDogsbody.Integrations.Google.GoogleCredentialStore.*` — three entries.
- [ ] **2.8** Gate: build clean, suite green. **Both stores now exist and both are tested.**

## Phase 3 — Re-point (required) — the moment the move becomes provable

- [ ] **3.1** Copy the Phase 1 characterization assertions into
      `Integrations/Google/GoogleCredentialCharacterizationTests.fs`, retargeted at the **new** store.
      *Outcome:* every assertion that passed against the old store passes against the new one —
      **especially the byte-for-byte secret**, which is the one failure that would otherwise stay
      invisible until an API call failed for an unrelated-looking reason.
      *Depends on:* 2.6, 1.1, 1.2.
- [ ] **3.2** Persisted-shape test for `GoogleCredential` — assert the stored document's **field
      names**, not just the round-tripped object.
- [ ] **3.3** Contract suite for the credential store, run against the real adapter and every fake.
- [ ] **3.4** Gate: build clean, suite green.

## Phase 4 — Delete (required) — one at a time, building between each

- [ ] **4.1** The UI page: `Pages/Settings/CredentialsPage.fs`,
      `Components/CredentialsComponents.fs`, `ModuleCreators/CredentialsBrowserModuleCreators.fs`,
      the `Shell.fs` route, and the `SettingsComponents` nav entry.
      Delete `UI/ModuleCreators/CredentialsBrowserModuleCreatorsTests.fs` and
      `E2E/CredentialsFlowTests.fs` — **keep `E2E/BlazorTestHarness.fs`**, which is shared harness
      code every future E2E flow needs.
- [ ] **4.2** `MyDogsbody.UI.Types`: `CredentialApi.fs`, `IntegrationCredentialUiType.fs`,
      `Modules/CredentialsBrowserModule.fs`, and the `MyDogsbody.Enums` reference.
- [ ] **4.3** `MyDogsbody.UI.Portal`: the `MyDogsbody.Enums` `ProjectReference`.
      *Outcome:* **`UI.Portal` now references only `UI.Types` and, transitively,
      `Exceptions.Types`** — one of the end-state properties for the whole series.
- [ ] **4.4** The composition root: `CredentialApiFactory.fs`, `CredentialApiMappers.fs`, and the
      credentials context, binding and registration in `Startup.fs`.
      Delete `Startup/CredentialApiFactoryTests.fs` and `Startup/CredentialApiMappersTests.fs`.
- [ ] **4.5** `MyDogsbody.Domain/Credentials/` — `CredentialsTypes.fs` and all three workflows (Q3.7).
      Delete the four `Domain/Credentials/*Tests.fs` files.
      *Outcome:* the domain is one area lighter and still references nothing.
- [ ] **4.6** `MyDogsbody.Integrations.Credentials` and
      `MyDogsbody.Integrations.Credentials.Database.Models`. **Remove `bin/` and `obj/` first** — the
      `logging-not-an-integration` change recorded that Windows refuses the directory operation while
      an IDE language server holds handles on the build output.
      Delete `Integrations/Credentials/CredentialStoreTests.fs`,
      `CredentialsDatabaseContextModuleTests.fs` and the Phase 1 characterization file (its
      assertions now live in 3.1).
- [ ] **4.7** `MyDogsbody.Enums` (Q3.8). Same `bin`/`obj` caution.
- [ ] **4.8** Edit — **do not delete** — the four contract files that carry credentials cases
      alongside surviving ones: `Contracts/ActionNamesTests.fs`,
      `Contracts/PersistedShapeTests.fs`, `Contracts/ErrorTranslationTests.fs`,
      `Contracts/DomainIsolationTests.fs`. Also delete
      `Contracts/CredentialApiContractTests.fs`, `Contracts/CredentialBoundaryMapperTests.fs` and
      `Contracts/CredentialDependencyContractTests.fs`.
- [ ] **4.9** `MyDogsbody.sln`: remove three `Project(...)` blocks and their
      `ProjectConfigurationPlatforms` lines; add one.
      *Outcome:* check the final `.sln` diff by hand. An IDE project system has previously removed an
      unrelated project block during a move.
- [ ] **4.10** Remove `ActionNames.MyDogsbody.Integrations.Credentials.*` and
      `ActionNames.MyDogsbody.Startup.CredentialApi.*`.
      *Outcome:* the structural suite passes — it fails if a retired entry is left behind.
- [ ] **4.11** Remove stale `bin/` and `obj/` output for all three deleted projects.

## Phase 5 — Gate (required)

- [ ] **5.1** `dotnet build MyDogsbody.sln` — zero errors, **zero warnings**, every remaining project
      including the WPF host.
- [ ] **5.2** `dotnet test` — zero failures, **zero skips**, and **tests present at all four levels**.
- [ ] **5.3** Grep: no match for `Integrations.Credentials`, `MyDogsbody.Enums` or
      `InfrastructureType` in code, project files or the solution. The only hits are in
      `docs/changes/`.
- [ ] **5.4** Project count is **exactly two fewer** than before this change started.
- [ ] **5.5** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass, and
      `MyDogsbody.Domain` now has one fewer folder.
- [ ] **5.6** Confirm `UI.Portal`'s `.fsproj` lists exactly one `ProjectReference`.
- [ ] **5.7** Run the app. Every remaining page works; there is no credentials entry in the settings
      nav; no `Credentials.db` is created in `bin\Debug\net9.0\`.
- [ ] **5.8** Confirm `MainWindow.xaml.cs` is untouched.

## Phase 6 — Documentation (required)

- [ ] **6.1** `CLAUDE-project.md`: remove the three projects from the structure table; remove
      `InfrastructureType` and the domain's `Infrastructure` union from the reference-direction
      notes; update `UI.Portal`'s reference set to two; remove the last naming quirk (the
      `MyDogsbody.Infrastructure.Google` line is now obsolete); **re-point the two reference examples
      that left with the deleted code** — `CredentialStoreTests.withStore` as the LiteDB temp-file
      shape and `CredentialsBrowserModuleCreatorsTests` as the API-record fake — at their Google
      replacements; update the *Build state* totals.
- [ ] **6.2** `outcome.md`, and it must be blunt (friction #10):
      test totals **before and after, per level**; the list of every deleted test file; the statement
      that **three domain workflows, a store, two mappers and a UI page were removed along with their
      tests**; that the rows in `Credentials.db` were **discarded, not migrated** (Q3.9); and that
      **secrets remain unencrypted at rest as a deliberate, accepted risk** (Q5.6), to be repeated in
      change #6's description where OAuth refresh tokens start being written.
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
