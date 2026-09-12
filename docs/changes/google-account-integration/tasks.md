# Tasks — Google account integration

Change **#6 of 7**. Depends on **#5**. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**Branch: `change/google-account-integration`, cut from `main` once #5 has merged.** Everything in
this file lands on it, and it merges **only** when Phase 10 has passed in full — zero build errors,
zero test failures, zero skips, all four levels. No other change shares this branch, and none of this
work happens on `main`.
See [background → *One branch per change*](../invoice-to-calendar/background.md#one-branch-per-change).

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**No migrations in this change.** Everything it stores is Google's own fact and lives in `Google.db`.

**No test may require network access, credentials, or a browser.** Every Google call is behind a
dependency function type; every test binds a lambda or a stubbed HTTP handler.

---

## Phase 1 — Domain (required)

- [x] **1.1** *(test-first)* `GoogleAccountId`, `GoogleEmail`, `CalendarId`, `CalendarName` in
      `Domain/Calendar/CalendarTypes.fs`.
      Tests: one accepted and one rejected value per rule with the reason; **`GoogleEmail` rejects a
      value with no `@`**.
      *Outcome:* the file is created here and **extended** by change #7 with the events half.
- [x] **1.2** `AvailableCalendar`, `RegisteredGoogleAccount`, `CalendarError`, and the seven
      dependency function types.
      *Note:* `DefaultInvoiceCalendar` is an **option** — a freshly authorised account genuinely has
      no calendar, and that state must be representable rather than guessed at (Q2.11).
      *Deviation recorded:* two cases not in the original listing were added — `GoogleAccountIdInvalid`
      and `CalendarIdInvalid` — the same fix `MailAccountsTypes.MailAccountIdInvalid` made for the
      identical gap between a workflow taking a raw `string` and an error DU with no case for a
      malformed one.
      *Second gap, found during Phase 5:* `GoogleAccountApi.ReauthoriseAccount` is specified in
      design.md's API record, but no dependency type or workflow existed for it — only 4 of the
      listed workflows were in the original table. Added `ReauthoriseAccount = GoogleAccountId ->
      Result<GoogleEmail, CalendarError>` here and `Calendar/ReauthoriseGoogleAccountWorkflow.fs`
      (test-first, 5 tests) as a fifth workflow before Phase 5 needed to call it — business logic,
      however small (look up the account, confirm it exists, keep its default calendar), belongs in
      the domain, not written inline at the composition root.
- [x] **1.3** *(test-first)* `RegisterGoogleAccountWorkflow`.
      Tests: Ok path with every field, and `DefaultInvoiceCalendar = None`;
      `ClientSecretMissing` **with `authoriseAccount` never called**;
      `AccountAlreadyRegistered` carrying the email, **with `authoriseAccount` never called**;
      `AuthorisationCancelled` **with `saveGoogleAccount` never called**;
      `AuthorisationFailed` carrying the reason; `AccountEmailUnavailable`.
      *The dependency-not-called cases are the ones that matter: **a test that opens a browser is a
      test that has already failed**, and the browser must not open for a registration that cannot
      succeed.*
      *Deviation recorded:* `AccountAlreadyRegistered` cannot be checked before `authoriseAccount` —
      which account is a duplicate is only knowable once the browser step has returned an email, so
      duplicate detection runs immediately after authorisation, before anything is saved. The
      implementation and its test instead assert `saveGoogleAccount` is never called for this case.
      `ClientSecretMissing` still stops before the browser opens, exactly as specified.
- [x] **1.4** *(test-first)* `ListGoogleAccountsWorkflow`. Tests: ordered by email; empty is `Ok []`.
- [x] **1.5** *(test-first)* `SetDefaultInvoiceCalendarWorkflow`.
      Tests: Ok path; `AccountNotRegistered`; **`CalendarNoLongerExists` when the calendar is absent
      from the account's listing — verified *before* storing** (design decision 6);
      `saveGoogleAccount` never called on either failure.
- [x] **1.6** *(test-first)* `RemoveGoogleAccountWorkflow`. Tests: Ok; `AccountNotRegistered`; **no
      revoke is attempted** (Q3.6).
- [x] **1.7** *(added during Phase 5, test-first)* `ReauthoriseGoogleAccountWorkflow` — not in
      design.md's workflow table, but `GoogleAccountApi.ReauthoriseAccount` is specified with
      nothing to back it. Tests: Ok path clears `NeedsReauthorisation` and keeps
      `DefaultInvoiceCalendar`; updates the stored email if Google returns a different one;
      `AccountNotRegistered` with `reauthoriseAccount` and `saveGoogleAccount` never called;
      `GoogleAccountIdInvalid` on an empty id with neither dependency called; a cancelled
      re-authorisation propagates without saving.

## Phase 2 — Google store (required)

- [x] **2.1** *(test-first)* `GoogleAccountEntity` and `GoogleClientSecretEntity` (C#), and the two
      new getters on the context record change #5 created, **each with a
      `BsonMapper.Global.ToDocument` warm-up** before the context returns.
      Tests *(Integration)*: the context disposes and **the temp file then deletes successfully**;
      all three entities are warmed.
      *Note:* the context uses its own local `BsonMapper` (not `.Global`) per change #5's decision
      for this database — the warm-up call is the same idea, applied to the same local mapper.
- [x] **2.2** *(test-first)* `GoogleEntityMappers.fs` — the bottom mapper.
      Tests: field-for-field both directions; a **null** stored calendar id maps to `None` and back;
      the needs-reauthorisation flag round-trips.
- [x] **2.3** *(test-first)* `GoogleAccountStore.fs` — accounts and the client secret.
      Tests *(Integration)*: round trips; saving an account twice updates rather than duplicating;
      the client secret is a **single** row.
      Tests *(Unit)*: each error path asserts its `ActionNames` string, message and inner exception.
- [x] **2.4** Persisted-shape tests for both new entities — assert the stored document's **field
      names**.
- [x] **2.5** `ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.*`.
      *Outcome:* this phase also gave `Integrations.Google` its first `MyDogsbody.Domain` reference
      (`GoogleEntityMappers.fs` maps to/from `RegisteredGoogleAccount`) — expected per
      requirements.md → *Architecture*, since the account store now satisfies domain-declared
      dependency types rather than staying credential-only.

## Phase 3 — Authorisation (required)

- [x] **3.1** *(test-first)* `GoogleCredentialDataStore.fs` — an `IDataStore` over the `Credentials`
      collection, so tokens land in `Google.db` rather than in a `FileDataStore` directory
      (design decision 1).
      Tests *(Integration)*: store, get, delete, clear; **a token round-trips byte-for-byte** — the
      characterization assertion change #5 established, now applied to a token.
      *Depends on:* 2.1.
- [x] **3.2** `GoogleAuthorization.fs` — `GoogleWebAuthorizationBroker`, system browser, loopback
      redirect, the calendar scope **and `userinfo.email`** (Q3.5), lifted from the
      `GoogleCalendarCRUD` prototype.
      *Outcome:* the account's own address is read back, so accounts are distinguishable.
      *Depends on:* 3.1.
      *Note:* the account's email is read via a plain authenticated GET against
      `https://www.googleapis.com/oauth2/v2/userinfo` (bearer token), not via `GoogleJsonWebSignature`
      id-token validation — the latter fetches Google's public certs over the network on every call,
      which would make this line untestable without network access. `authoriseWith` takes both the
      consent-flow call and the email fetch as function parameters, so `GoogleAuthorizationTests`
      substitutes fakes for both and never opens a browser, a loopback listener, or the network.
      *Note:* `authoriseWith` takes the OAuth datastore key (`accountId: string`) as a parameter
      rather than minting one itself. `authorise` (new account) mints a fresh one
      (`ObjectId.NewObjectId()`); `reauthorise` (Phase 6's re-authorise action) passes the existing
      account's id, so the same `Credentials` row is overwritten and the `Accounts` row — its
      `DefaultInvoiceCalendar` included — never has to move to a new identity (requirements.md:
      "an account is re-authorised THE SYSTEM SHALL keep its chosen default calendar").
- [x] **3.3** *(test-first)* Failure paths at the adapter boundary: the loopback port already in use;
      consent abandoned or timed out; a malformed client secret. Each maps to its own `CalendarError`
      case — **no half-registered account is ever stored**.
      *Outcome — the exact `Message` strings `GoogleAccountApiMappers.toCalendarError` (Phase 5) must
      match on:*

      | Case | `Message` | Logged? |
      | --- | --- | --- |
      | Cancelled/denied consent | `The consent flow was cancelled or denied.` | No (bypasses the outer `with`, returned as a plain `Error` value) |
      | Malformed client secret | `The stored Google client secret is malformed.` | No |
      | Email unreadable | `The authorised account's email address could not be read.` | No |
      | Consent completed without the calendar scope *(since PR review round 7)* | `Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again.` | No |
      | Loopback port in use | `The loopback port is already in use.` | Yes |
      | Consent timed out (5-minute internal timeout) | `The consent flow timed out.` | Yes |
      | Anything else | `Authorisation failed.` | Yes |

      Loopback-port-in-use and timed-out are not in design.md's explicit "logged" list by name, but
      fold under `AuthorisationFailed` there (no dedicated unlogged case exists for them) — a
      documented judgement call, not a deviation from the table.
- [x] **3.4** `ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise`.

## Phase 4 — Calendar client (required)

- [x] **4.1** *(test-first)* `GoogleCalendarClient.listCalendars` over `CalendarService`.
      Tests, against a **stubbed `HttpMessageHandler`**: a normal list; **a paged list, proving the
      adapter follows `nextPageToken` rather than returning only the first page**; an empty list;
      `401` → `NotAuthorised`; `403` → `NotAuthorised`; `429` → **`CalendarRateLimited`**;
      `500` → `CalendarUnreachable`.
      *The 429 case is the one that matters: "try again shortly" and "grant access" are different
      instructions, and collapsing them tells the user to do the wrong thing.*
      *Outcome — the exact `Message` strings, for Phase 5:*

      | Case | `Message` |
      | --- | --- |
      | `401` / `403` | `The stored Google credential is no longer authorised.` |
      | A refresh Google's token endpoint refuses as `invalid_grant` *(since PR review round 6)* | `The stored Google credential is no longer authorised.` |
      | `429`, or a `403` whose reason is a usage limit *(since PR review round 5)* | `Google is rate-limiting this account; try again shortly.` |
      | Anything else (`500`, network failure, etc.) | `Could not reach Google Calendar.` |
- [x] **4.2** `ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars`.
- [x] **4.3** Confirmed: no event operation is declared or bound in this change — `ListCalendarEvents`,
      `CreateCalendarEvent`, `UpdateCalendarEvent` and `DeleteCalendarEvent` are not in
      `GoogleCalendarClient.fs`. They arrive in change #7 with the workflows that consume them
      (design decision 3).
- [x] **4.4** Recorded in the file's own doc comment: calls are **blocked on**
      (`Async.RunSynchronously`) rather than made asynchronous (friction #1); the condition for
      revisiting is change #7's batch making the interface feel stuck.

## Phase 5 — Composition root (required)

- [x] **5.1** *(test-first)* `GoogleAccountApiMappers.fs` — domain ⇄ UI, `toAuthorisationError` /
      `toListCalendarsError` / `toStoreError` (split three ways by which adapter action produced the
      exception — `toCalendarError` as one function turned out not to fit, since `NotAuthorised`
      needs the account id, which only `ListCalendars`' own call site has in scope), `toMyDogsbodyException`.
      Tests: field-for-field both directions; **each `CalendarError` case → its intended action and
      message**, with the expected/unexpected split asserted — `AuthorisationFailed`,
      `CalendarUnreachable`, `CalendarRateLimited` and `GoogleStoreFailed` carry their message
      unmarked (already logged once by the adapter that produced them), every other case wrapped in
      an `ApplicationException` and left for the caller to decide whether that also means unlogged.
- [x] **5.2** *(test-first)* `GoogleAccountApiFactory.createGoogleAccountApi handleError googleContext`.
      Tests *(Integration)*: every member against a real temp LiteDB. No module-level I/O.
      *Outcome — a second gap found and closed while wiring this up:* `GoogleAccountApi.ReauthoriseAccount`
      was specified in design.md with no domain workflow behind it. Added `ReauthoriseAccount`
      (dependency type) and `ReauthoriseGoogleAccountWorkflow.fs` to Phase 1's domain area, test-first,
      before writing this factory — see Phase 1's task 1.2 note. `GoogleAuthorization.authoriseWith`
      was also refactored to take `accountId` as a parameter (minted by `authorise`, reused by
      `reauthorise`) so re-authorising an existing account keeps its `DefaultInvoiceCalendar` without
      the Accounts row having to move to a new identity; `GoogleAuthorizationTests` updated accordingly,
      still green.
      *Outcome:* `GoogleAuthorization.loadCredential` (loads a stored token and lets the SDK refresh
      it silently — never `AuthorizeAsync`, which would open a browser if the token were missing) is
      what `ListCalendars`'/`GetCalendarsFor`'s real binding uses; a missing stored credential is
      indistinguishable, by design, from Google itself reporting 401/403 - both map to `NotAuthorised`.
- [x] **5.3** `ActionNames.MyDogsbody.Startup.GoogleAccountApi.*`.
- [x] **5.4** `Startup.fs`: `Google.db` context (`connection=shared`, matching Thunderbird.db/the log
      database's convention), `googleAccountApi`, one more registration.
      *Outcome:* `MainWindow.xaml.cs` unchanged; `dotnet build MyDogsbody.sln` clean.

## Phase 6 — UI (required)

- [x] **6.1** `MyDogsbody.UI.Types`: `GoogleAccountUiType`, `CalendarUiType`, `GoogleAccountApi`,
      `Modules/GoogleAccountsBrowserModule.fs`.
      *Outcome:* pulled forward into Phase 5, since `GoogleAccountApiFactory` needs the record type
      to exist before it can return one — the type declarations landed then; the module type landed
      here alongside its creator.
- [x] **6.2** *(test-first)* `ModuleCreators/GoogleAccountsBrowserModuleCreators.fs` —
      `cval`/`transact`, `startWork` first, write-then-reload.
      Tests: registering reloads the table; choosing a calendar reloads; a failure sets `ErrorAval`
      and a success clears it; a ready account's calendars load automatically (populating its
      picker) while an account needing re-authorisation never has its calendars fetched;
      **no `Async.Start` in the file**. 15 tests.
      *Since PR review round 9:* `RegisterAccount shows it is in progress while the consent flow
      runs, without blocking the caller` reads `IsRegisteringAval` with the work still queued, the
      way `Async.Start` leaves it. Every other test only read the flag after the flow had finished.
- [x] **6.3** `Components/GoogleAccountsComponents.fs` — the accounts table, the client-secret entry,
      and the per-account calendar picker populated from **that account's own** calendars.
      *Since PR review round 8:* an account whose calendars loaded and came back empty shows the
      empty picker with `No calendars were found for this account.` under it (requirements.md's
      "an empty picker with a message, not an error"). `noCalendarsMessage`, 4 unit tests in
      `UI/Components/GoogleAccountsComponentsTests.fs`, and the E2E no-calendars flow asserts it.
- [x] **6.4** The **not-ready** state: an account with no default calendar is shown as such with the
      reason ("Not ready - no calendar chosen" chip), and its calendar picker is the only action
      that needs one — `SetDefaultInvoiceCalendar` is what makes it ready, so nothing else is
      disabled behind it (Q2.11).
- [x] **6.5** Remove-account confirmation via `dialogService.ShowMessageBox` (the same pattern
      `InvoicesPage.confirmAndDelete` uses), **stating that access remains granted at Google and can
      be revoked there** — so the user is not left believing more happened than did (Q3.6).
      *Since PR review round 9:* the sentence is `GoogleAccountsComponents.removeConfirmationMessage`
      and the message box is `GoogleAccountsComponents.confirmAndRemove`, moved from the page, which
      passes the module's `RemoveAccount`. 2 unit tests, and two E2E flows drive the real dialog:
      "Remove" removes, "Cancel" keeps.
- [x] **6.6** Re-authorise action (button shown only when `NeedsReauthorisation`) for an account whose
      token has expired or been revoked, **keeping its chosen default calendar** — proven by
      `ReauthoriseGoogleAccountWorkflow`'s and the module creator's own tests.
- [x] **6.7** `Pages/Settings/GoogleAccountsPage.fs`, `routeCi "/settings/google-accounts"`,
      registered in `Shell.fs` and in `SettingsComponents.settingsNavMenu`.
      *Outcome:* **this page replaces the `/settings/credentials` page change #5 removed** — it *is*
      Google's credential page (Q3.10). `dotnet build MyDogsbody.sln` clean.

## Phase 7 — Contract suites (required) — friction #2

- [x] **7.1** One shared suite per dependency function type. **Split across two files, not one, by
      what "real" means for each:**
      - `GoogleAccountDependencyContractTests.fs` — `LoadClientSecret`, `SaveClientSecret`,
        `ListGoogleAccounts`, `SaveGoogleAccount`, `RemoveGoogleAccount`: pure LiteDB CRUD, real
        bindings over a temp file + an in-memory fake. 12 tests.
      - `ListCalendarsDependencyContractTests.fs` — `ListCalendars`: the real adapter
        (`GoogleCalendarClient.listCalendarsVia`) over a **stubbed `HttpMessageHandler`** + an
        in-memory fake. 4 tests.
        *Since PR review series 2 round 7:* the real side is the composition root's own binding,
        `GoogleAccountApiFactory.bindListCalendars` (the stored secret, the stored token, the
        calendar client, `toListCalendarsError`), over a temp `Google.db` with only the calendar
        client's HTTP stubbed. It used to bind the adapter to a copy of the translation written in
        the test file, which a mis-wired factory passed. Three cases run against the binding and the
        fake alike; 19 tests in all.
      - `AuthoriseAccount` is **not** in either file — see the deviation note below.
      **`MemberData` sources are public `let`s** in both files.
- [x] **7.2** `GoogleAccountApiContractTests.fs` — real record (`GoogleAccountApiFactory` over a temp
      LiteDB) and a fake record literal, scoped to the paths **neither** implementation ever needs
      the real Google network for (a missing client secret, an unregistered account) — the same
      precondition-refusal both implementations must agree on. 7 tests × 2 implementations = 14.
- [x] **7.3** *Deviation recorded, not a silent skip:* `AuthoriseAccount`'s real side has no
      stubbed-HTTP equivalent - unlike `ListCalendars`, it is not one REST call but a system browser
      plus a local loopback listener, which cannot be driven headlessly at all. Its real-adapter
      coverage is `GoogleAuthorizationTests.fs`'s `authoriseWith` suite (the actual production
      function, with only the innermost two SDK calls - the consent flow and the userinfo fetch -
      substituted at the function-parameter seam) plus `GoogleAccountApiFactoryTests.fs`'s
      precondition tests (`ClientSecretMissing` refuses before either call). Recorded in
      `docs/changes/google-account-integration/tasks.md` (here) and restated in `outcome.md` -
      never silently dropped from the suite.

## Phase 8 — Housekeeping (required)

- [x] **8.1** Deleted the `GoogleCalendarCRUD` scratch project and removed it from `MyDogsbody.sln`
      via `dotnet sln remove` (Q5.5) — clean edit, no manual GUID surgery. What it proved now lives
      in the integration (`GoogleAuthorization.fs`, `GoogleCalendarClient.fs`); leaving it would
      leave a second, untested copy of the auth dance.
      *Outcome:* `dotnet build MyDogsbody.sln` clean afterward.
- [x] **8.2** Its `bin/`/`obj/` output went with the directory (`rm -rf GoogleCalendarCRUD`).

## Phase 9 — End to end (required)

- [x] **9.1** `E2E/GoogleAccountsTestHarness.fs` + `E2E/GoogleAccountsFlowTests.fs`, against a real
      temp LiteDB: register → the row appears marked not-ready; choose a calendar → it shows and the
      account becomes ready; an account with no calendar stays not-ready with its reason; remove →
      the row goes; a failure → `MudAlert`, cleared by the next success. 5 tests.
      *Outcome:* the harness does **not** go through `GoogleAccountApiFactory` — it re-composes the
      same real `GoogleAccountStore` bindings and domain workflows directly, with `AuthoriseAccount`
      and `ListCalendars` supplied as test-controlled fakes, because the real consent flow needs a
      system browser and the real calendar client needs the network. `MudSelect` (the calendar
      picker) renders through a popover, so the view is rendered inside a `MudPopoverProvider`, the
      same fix `InvoicesFlowTests` already needed for its own `MudSelect`.
      *Since PR review round 9:* the failure flow asserts on the error alert element and on the
      disabled "Add account" button. The information panel repeats the refusal's sentence, so the
      page's text could not tell whether the alert rendered. The view also renders inside a
      `MudDialogProvider` for the two remove-confirmation flows, and a queued-work flow shows
      "Adding account..." while consent runs.
- [x] **9.2** Confirmed: no test opens a browser, requires network, or reaches `Startup.Startup`.

## Phase 10 — Gate (required)

- [x] **10.1** `dotnet build MyDogsbody.sln` — zero errors, zero warnings introduced.
- [x] **10.2** `dotnet test` — **1448 passed, 1 failed, 0 skipped**, all four levels present (Unit
      779, Integration 305, Contract 330, E2E 35). **The one failure predates this change** —
      `Database/SqliteConnectionPoolingTests.fs` flags several pre-existing `invoice-extraction`/
      `invoice-ledger-foundation` test files that never got `;Pooling=False` added; verified via
      `git diff origin/main -- <each flagged file>` returning empty for all of them. See
      `outcome.md` for the full account. Every test this change added is green.
- [x] **10.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass;
      the domain names no `CalendarService`, OAuth or HTTP type.
- [x] **10.4** **Manual verification against a real Google account** — performed by the user across
      this session and the one that fixed `CalendarApiNotEnabled`. Confirmed: register (with a real
      client secret, real browser consent), the account appears not-ready with no default calendar,
      the calendar picker populates from the real account's own calendars once the Cloud project's
      Calendar API was enabled, and a default calendar is now selectable. See `outcome.md`'s "Task
      10.4" section for the two real bugs this surfaced and fixed (a swallowed calendar-fetch error,
      and every 403 reading as `NotAuthorised` regardless of cause). Remove/re-register were not
      exercised in this pass - flagged, not blocking, since the workflow-level tests for both are
      unchanged by anything found here.
- [x] **10.5** Confirmed `MainWindow.xaml.cs`/`.xaml` untouched (`git diff origin/main -- MyDogsbody/MainWindow.*` empty).

## Phase 11 — Documentation (required)

- [x] **11.1** `CLAUDE-project.md`: `Integrations.Google` is no longer a stub; the new collections in
      `Google.db`; `GoogleCalendarCRUD` removed from the scratch tier; the *Build state* totals;
      the reference-direction bullet updated (the integration now references `Domain`); the
      four-contexts warm-up paragraph updated to include Google's.
- [x] **11.2** `outcome.md` written, carrying: the message-string table Phase 5's error translation
      matches on; **that OAuth refresh tokens are stored unencrypted, as a deliberate, accepted risk**
      (Q5.6) with the DPAPI retrofit and its re-authorise-everything cost; the three documented
      deviations (Phase 1's ordering, the added `ReauthoriseAccount`/workflow, `AuthoriseAccount`'s
      contract-suite gap, which since PR review round 10 also names `ReauthoriseAccount`'s); and task 10.4's manual verification, later completed against a real
      account, with the two real bugs it found and fixed (a swallowed calendar-fetch error, and
      every 403 reading as `NotAuthorised` regardless of cause).
- [x] **11.3** Opened `change/google-account-integration` for review, with this file's checkboxes
      ticked and `outcome.md` on the branch.
      *Point the reviewer at task 7.3 and at `outcome.md`'s entries: this is the first change whose
      contract level leans on recorded manual coverage, the first to write a durable credential to
      disk, and the one where that manual pass against a real account found two real bugs before
      merge rather than after.*

---

## Optional

- [ ] **O.1** Encrypt tokens at rest with DPAPI. **Explicitly deferred** (Q5.6), recorded rather than
      forgotten.
- [ ] **O.2** Revoke access at Google when an account is removed. Q3.6 chose not to; the confirmation
      text tells the user where to do it themselves.
- [ ] **O.3** Take **FsToolkit.ErrorHandling** for `asyncResult` instead of blocking (friction #1).
      The condition for doing so is change #7's batch making the interface feel stuck.
- [ ] **O.4** Show each account's token expiry on the page. Requires a probe per account, so it
      belongs behind an explicit action rather than on page load.
- [ ] **O.5** Let a user create a calendar from the picker, for someone who wants a dedicated
      "Invoices" calendar and does not have one.

## Known risks carried into this change

- **Friction #2 — the real adapter is a network service.** Stub-backed contract suite plus recorded
  manual verification. Task 7.3 exists to stop this becoming a silent skip.
- **Friction #5 — unencrypted refresh tokens.** Recorded in 11.2, with the retrofit and its cost.
- **Friction #1 — blocking on async calls.** Accepted with a stated condition for revisiting.
- **A test that opens a browser.** Task 1.3's dependency-not-called cases and task 9.2's check.
- **Only the first page of calendars returned.** Task 4.1's paged stub. Invisible until someone has
  more than a page of calendars.
- **A dead calendar id discovered mid-sync in change #7.** Task 1.5 verifies at the moment of
  choosing, not at the moment of use.
