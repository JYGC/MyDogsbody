# Tasks — Startup composition root

Unit tests land before the implementation they cover, per task.

## Phase 0 — Bootstrapping (required)

- [x] **0.1** Replace the stale tests under `MyDogsbody.Tests/Infrastructure/DocumentInfrastructure/`; add `ProjectReference` for `MyDogsbody.Spine` and `MyDogsbody.Integrations.Pdf`.
      *Outcome:* `dotnet test` runs for the first time.
      *Note:* the two old files were rewritten, not repaired, into `Spine/Domains/DocumentDomainTests.fs` and `Integrations/Pdf/Domains/ReadPdfDomainTests.fs`. `getPdfDocumentHandler` no longer exists (it is `ReadPdfDomain.getPdfContent`), and `GetContentSplitByLinesTests` asserted an `Error` carrying a `DivideByZeroException` that the current `getContentSplitByLines` cannot produce — it would have failed had it compiled. The old folder is a pre-refactor name and was removed.
- [x] **0.2** Fix stale namespaces in `PdfProcessing/Program.fs`.
      *Outcome:* the scratch project compiles; solution build no longer blocked by it.
      *Depends on:* nothing.

## Phase 1 — Contract (required)

- [x] **1.1** Add `MyDogsbody.Exceptions.Types` reference to `MyDogsbody.UI.Types`.
- [x] **1.2** Add `MyDogsbody.UI.Types/CredentialApi.fs` declaring the `CredentialApi` record.
      *Outcome:* the UI-facing surface exists as a type, no implementation yet.
      *Depends on:* 1.1.
- [x] **1.3** Extend `CredentialsBrowserModule` with `ErrorAval`, `AddCredential`, `EditCredential`.
      *Depends on:* 1.2.

## Phase 2 — Tests first (required)

- [x] **2.1** `CredentialApiMappersTests` — UI ⇄ Spine both directions, **every field asserted**. Must fail to compile/run before 3.1.
      *Depends on:* 1.2.
- [x] **2.2** `CredentialsBrowserModuleCreatorsTests` — against a hand-built `CredentialApi`: Ok path populates the list; Error path sets `ErrorAval` and clears `IsLoadingAval`; a successful write triggers a reload; a successful write after a failure clears the error.
      *Depends on:* 1.3.
- [x] **2.3** `CredentialApiFactoryTests` (Integration) — real temp LiteDB file, fresh per test, deleted after: add → `GetAllCredentials` returns the row with a non-empty `Id`; edit → re-read reflects the new values.
      *Depends on:* 1.2.
- [x] **2.4** `CredentialHopChainTests` (Contract) — a value entered as `IntegrationCredentialUiTypeWithoutId` arrives unchanged in the LiteDB `Credential`, and back out as `IntegrationCredentialUiType`, asserted field-for-field.
      *Depends on:* 1.2.

## Phase 3 — Implementation (required)

- [x] **3.1** `MyDogsbody.Startup/CredentialApiMappers.fs` — pure mapping, no module-level I/O.
      *Depends on:* 2.1.
- [x] **3.2** `MyDogsbody.Startup/CredentialApiFactory.fs` — `createCredentialApi handleError getCredentialCollection`, no module-level I/O.
      *Depends on:* 3.1, 2.3.
- [x] **3.3** `MyDogsbody.Startup/Startup.fs` — database paths, contexts, `handleError`, `credentialApi`, `registerServices`. Content moved from `Compositions/SetupDatabase.fs`.
      *Depends on:* 3.2.
- [x] **3.4** Add `MyDogsbody.Startup` to `MyDogsbody.sln`.
      *Depends on:* 3.3.

## Phase 4 — Rewire (required)

- [x] **4.1** `CredentialsBrowserModuleCreators` takes `CredentialApi`; owns error state; reloads after a successful write.
      *Depends on:* 2.2, 1.3.
- [x] **4.2** `CredentialsComponents.credentialsBrowser` — `showEditCredentialsModal` takes `IntegrationCredentialUiType`; render `ErrorAval` as a `MudAlert`.
      *Depends on:* 1.3.
- [x] **4.3** `CredentialsPage` injects `CredentialApi`; remove the `MyDogsbody.Spine.UseCases.Types` open and the hand-built DTO; wire both dialogs to the module commands.
      *Depends on:* 4.1, 4.2.
- [x] **4.4** Remove the `Compositions.Interfaces` `ProjectReference` from `MyDogsbody.UI.Portal.fsproj`.
      *Depends on:* 4.3.
- [x] **4.5** `MainWindow.xaml.cs` calls `Startup.registerServices`; drop both old `using`s and both `ProjectReference`s, add `MyDogsbody.Startup`.
      *Depends on:* 3.4.

## Phase 5 — Removal (required)

- [x] **5.1** Delete `MyDogsbody.Compositions/` and `MyDogsbody.Compositions.Interfaces/` from disk and from `MyDogsbody.sln`.
      *Depends on:* 4.4, 4.5.

## Phase 6 — Gate (required)

- [x] **6.1** `dotnet build MyDogsbody.sln` — zero errors.
- [x] **6.2** `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` — zero failures, zero skips.
      *Depends on:* 6.1.

## Phase 7 — Documentation (required)

- [x] **7.1** `docs/architecture-options.md` — rework the three options' worked examples onto the Startup composition root.
      *(That file has since been deleted; the option it settled on, and why the others lost, are now in `CLAUDE-project.md` → Architecture.)*
- [x] **7.2** `CLAUDE-project.md` — it currently documents `Compositions/SetupDatabase.fs`, `CredentialCompositions`, the "UI.Portal references Compositions.Interfaces" rule, the layer chain and the known-broken build state. All stale after this change.

## Known gap

**E2E is not covered.** The bUnit + MudBlazor test harness does not exist in this repository, and
building it is a larger piece of work than this change. The add → row-appears and edit →
row-updates flows are verified at the integration and contract levels, and the state transitions
they depend on are verified by the module-creator unit tests against a fake `CredentialApi`. They
are **not** verified through a rendered component, and **the WPF app has not been run** — the
window has never been opened against this change. Recorded here rather than dropped.

## Follow-ups not taken in this change

- `CredentialsRepository.updateOne` matches on `InfrastructureType` and ignores the `Id`, so editing one of two credentials sharing a type updates the wrong row, and editing a type with no rows silently succeeds. Pinned by two characterization tests in `CredentialApiFactoryTests`; **not fixed** — it is a defect in the integration, not in the composition root, and fixing it changes stored-data behaviour.
- `CredentialsDatabaseContextModule.getDatabaseContext` gives no way to dispose the `LiteDatabase`, so integration tests delete their temp file on a best-effort basis.
- The five DTO hops between `Startup` and LiteDB are untouched: a credential is copied through `AddCredentialUseCaseTypeDto` → `AddCredentialDomainTypeDto` (the `Username` → `ExternalUsername` rename) → `NewCredentialUseCaseTypeDto` → `NewCredentialRepositoryTypeDto` → the LiteDB `Credential`, with a mapper at each step. Collapsing them to two edge mappers is the job of the architecture migration in `CLAUDE-project.md` → Architecture, not of this change.
