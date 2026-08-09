# Tasks — Google account integration

Change **#6 of 7**. Depends on **#5**. [`requirements.md`](requirements.md) ·
[`design.md`](design.md) · [decision record](../invoice-to-calendar/background.md)

**The ordering rule, per task:** where a task produces production code, its unit test is written
first, run, and confirmed to fail *for the reason expected* before the implementation. Tasks marked
*(test-first)* carry production code.

**No migrations in this change.** Everything it stores is Google's own fact and lives in `Google.db`.

**No test may require network access, credentials, or a browser.** Every Google call is behind a
dependency function type; every test binds a lambda or a stubbed HTTP handler.

---

## Phase 1 — Domain (required)

- [ ] **1.1** *(test-first)* `GoogleAccountId`, `GoogleEmail`, `CalendarId`, `CalendarName` in
      `Domain/Calendar/CalendarTypes.fs`.
      Tests: one accepted and one rejected value per rule with the reason; **`GoogleEmail` rejects a
      value with no `@`**.
      *Outcome:* the file is created here and **extended** by change #7 with the events half.
- [ ] **1.2** `AvailableCalendar`, `RegisteredGoogleAccount`, `CalendarError`, and the seven
      dependency function types.
      *Note:* `DefaultInvoiceCalendar` is an **option** — a freshly authorised account genuinely has
      no calendar, and that state must be representable rather than guessed at (Q2.11).
- [ ] **1.3** *(test-first)* `RegisterGoogleAccountWorkflow`.
      Tests: Ok path with every field, and `DefaultInvoiceCalendar = None`;
      `ClientSecretMissing` **with `authoriseAccount` never called**;
      `AccountAlreadyRegistered` carrying the email, **with `authoriseAccount` never called**;
      `AuthorisationCancelled` **with `saveGoogleAccount` never called**;
      `AuthorisationFailed` carrying the reason; `AccountEmailUnavailable`.
      *The dependency-not-called cases are the ones that matter: **a test that opens a browser is a
      test that has already failed**, and the browser must not open for a registration that cannot
      succeed.*
- [ ] **1.4** *(test-first)* `ListGoogleAccountsWorkflow`. Tests: ordered by email; empty is `Ok []`.
- [ ] **1.5** *(test-first)* `SetDefaultInvoiceCalendarWorkflow`.
      Tests: Ok path; `AccountNotRegistered`; **`CalendarNoLongerExists` when the calendar is absent
      from the account's listing — verified *before* storing** (design decision 6);
      `saveGoogleAccount` never called on either failure.
- [ ] **1.6** *(test-first)* `RemoveGoogleAccountWorkflow`. Tests: Ok; `AccountNotRegistered`; **no
      revoke is attempted** (Q3.6).

## Phase 2 — Google store (required)

- [ ] **2.1** *(test-first)* `GoogleAccountEntity` and `GoogleClientSecretEntity` (C#), and the two
      new getters on the context record change #5 created, **each with a
      `BsonMapper.Global.ToDocument` warm-up** before the context returns.
      Tests *(Integration)*: the context disposes and **the temp file then deletes successfully**;
      all three entities are warmed.
- [ ] **2.2** *(test-first)* `GoogleEntityMappers.fs` — the bottom mapper.
      Tests: field-for-field both directions; a **null** stored calendar id maps to `None` and back;
      the needs-reauthorisation flag round-trips.
- [ ] **2.3** *(test-first)* `GoogleAccountStore.fs` — accounts and the client secret.
      Tests *(Integration)*: round trips; saving an account twice updates rather than duplicating;
      the client secret is a **single** row.
      Tests *(Unit)*: each error path asserts its `ActionNames` string, message and inner exception.
- [ ] **2.4** Persisted-shape tests for both new entities — assert the stored document's **field
      names**.
- [ ] **2.5** `ActionNames.MyDogsbody.Integrations.Google.GoogleAccountStore.*`.

## Phase 3 — Authorisation (required)

- [ ] **3.1** *(test-first)* `GoogleCredentialDataStore.fs` — an `IDataStore` over the `Credentials`
      collection, so tokens land in `Google.db` rather than in a `FileDataStore` directory
      (design decision 1).
      Tests *(Integration)*: store, get, delete, clear; **a token round-trips byte-for-byte** — the
      characterization assertion change #5 established, now applied to a token.
      *Depends on:* 2.1.
- [ ] **3.2** `GoogleAuthorization.fs` — `GoogleWebAuthorizationBroker`, system browser, loopback
      redirect, the calendar scope **and `userinfo.email`** (Q3.5), lifted from the
      `GoogleCalendarCRUD` prototype.
      *Outcome:* the account's own address is read back, so accounts are distinguishable.
      *Depends on:* 3.1.
- [ ] **3.3** *(test-first)* Failure paths at the adapter boundary: the loopback port already in use;
      consent abandoned or timed out; a malformed client secret. Each maps to its own `CalendarError`
      case — **no half-registered account is ever stored**.
- [ ] **3.4** `ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise`.

## Phase 4 — Calendar client (required)

- [ ] **4.1** *(test-first)* `GoogleCalendarClient.listCalendars` over `CalendarService`.
      Tests, against a **stubbed `HttpMessageHandler`**: a normal list; **a paged list, proving the
      adapter follows `nextPageToken` rather than returning only the first page**; an empty list;
      `401` → `NotAuthorised`; `403` → `NotAuthorised`; `429` → **`CalendarRateLimited`**;
      `500` → `CalendarUnreachable`.
      *The 429 case is the one that matters: "try again shortly" and "grant access" are different
      instructions, and collapsing them tells the user to do the wrong thing.*
- [ ] **4.2** `ActionNames.MyDogsbody.Integrations.Google.GoogleCalendarClient.listCalendars`.
- [ ] **4.3** Confirm **no event operation is declared or bound in this change** — `ListCalendarEvents`,
      `CreateCalendarEvent`, `UpdateCalendarEvent` and `DeleteCalendarEvent` arrive in change #7 with
      the workflows that consume them (design decision 3).
      *The prototype's event code may be moved into this file, but it is not exposed as a dependency
      type yet.*
- [ ] **4.4** Record in the change description that calls are **blocked on** rather than made
      asynchronous (friction #1), and that the condition for revisiting is change #7's batch making
      the interface feel stuck.

## Phase 5 — Composition root (required)

- [ ] **5.1** *(test-first)* `GoogleAccountApiMappers.fs` — domain ⇄ UI, `toCalendarError`,
      `toMyDogsbodyException`.
      Tests: field-for-field both directions; **each `CalendarError` case → its intended action and
      message**, with the expected/unexpected split asserted (design → *Error handling*) — in
      particular that `CalendarUnreachable` **is** logged, because change #7 needs the log to explain
      why a sync plan was refused.
- [ ] **5.2** *(test-first)* `GoogleAccountApiFactory.createGoogleAccountApi handleError googleContext`.
      Tests *(Integration)*: every member against a real temp LiteDB and stubbed HTTP.
      No module-level I/O.
- [ ] **5.3** `ActionNames.MyDogsbody.Startup.GoogleAccountApi.*`.
- [ ] **5.4** `Startup.fs`: `Google.db` context, `googleAccountApi`, one more registration.
      *Outcome:* `MainWindow.xaml.cs` unchanged.

## Phase 6 — UI (required)

- [ ] **6.1** `MyDogsbody.UI.Types`: `GoogleAccountUiType`, `CalendarUiType`, `GoogleAccountApi`,
      `Modules/GoogleAccountsBrowserModule.fs`.
- [ ] **6.2** *(test-first)* `ModuleCreators/GoogleAccountsBrowserModuleCreators.fs` —
      `cval`/`transact`, `startWork` first, write-then-reload.
      Tests: registering reloads the table; choosing a calendar reloads; a failure sets `ErrorAval`
      and a success clears it; **no `Async.Start` in the file**.
- [ ] **6.3** `Components/GoogleAccountsComponents.fs` — the accounts table, the client-secret entry,
      and the per-account calendar picker populated from **that account's own** calendars.
- [ ] **6.4** The **not-ready** state: an account with no default calendar is shown as such with the
      reason, and any action requiring a calendar is unavailable (Q2.11).
- [ ] **6.5** Remove-account confirmation, **stating that access remains granted at Google and can be
      revoked there** — so the user is not left believing more happened than did (Q3.6).
- [ ] **6.6** Re-authorise action for an account whose token has expired or been revoked, **keeping
      its chosen default calendar**.
- [ ] **6.7** `Pages/Settings/GoogleAccountsPage.fs`, `routeCi "/settings/google-accounts"`,
      registered in `Shell.fs` and in `SettingsComponents.settingsNavMenu`.
      *Outcome:* **this page replaces the `/settings/credentials` page change #5 removed** — it *is*
      Google's credential page (Q3.10).

## Phase 7 — Contract suites (required) — friction #2

- [ ] **7.1** One shared suite per dependency function type — `LoadClientSecret`, `SaveClientSecret`,
      `AuthoriseAccount`, `ListGoogleAccounts`, `SaveGoogleAccount`, `RemoveGoogleAccount`,
      `ListCalendars` — run against **every fake and against the real adapter over stubbed HTTP**.
      **`MemberData` sources must be public `let`s.**
- [ ] **7.2** `GoogleAccountApi` contract suite: real record and every fake.
- [ ] **7.3** Write the arrangement into the test file as a comment and into the change description:
      **fakes + stub-backed real adapter, with live verification recorded as manual coverage.**
      *This must be stated explicitly rather than quietly skipping the level.*

## Phase 8 — Housekeeping (required)

- [ ] **8.1** Delete the `GoogleCalendarCRUD` scratch project and remove it from `MyDogsbody.sln`
      (Q5.5). What it proved now lives in the integration; leaving it would leave a second, untested
      copy of the auth dance.
      *Outcome:* check the `.sln` diff by hand.
- [ ] **8.2** Remove its `bin/` and `obj/` output.

## Phase 9 — End to end (required)

- [ ] **9.1** `E2E/GoogleAccountsFlowTests.fs` with a **faked authoriser** and stubbed HTTP, against a
      real temp LiteDB: register → the row appears marked not-ready; choose a calendar → it shows and
      the account becomes ready; an account with no calendar stays not-ready with its reason; remove
      → the row goes; a failure → `MudAlert`, cleared by the next success.
- [ ] **9.2** Confirm no test opens a browser, requires network, or reaches `Startup.Startup`.

## Phase 10 — Gate (required)

- [ ] **10.1** `dotnet build MyDogsbody.sln` — zero errors.
- [ ] **10.2** `dotnet test` — zero failures, **zero skips**, all four levels. Record totals per level.
- [ ] **10.3** `Contracts/DomainIsolationTests.fs` and `AssertDomainReferencesNothing` still pass;
      the domain names no `CalendarService`, OAuth or HTTP type.
- [ ] **10.4** **Manual verification against a real Google account**: register, list calendars, choose
      a default, remove, re-register. Record what was run and what was observed — **this is the
      coverage the contract level cannot supply.**
- [ ] **10.5** Confirm `MainWindow.xaml.cs` is untouched.

## Phase 11 — Documentation (required)

- [ ] **11.1** `CLAUDE-project.md`: `Integrations.Google` is no longer a stub; the new collections in
      `Google.db`; `GoogleCalendarCRUD` removed from the scratch tier; the *Build state* totals.
- [ ] **11.2** `outcome.md`, and it must carry two things beyond the totals:
      **(a)** the manual verification from 10.4, stated as manual coverage of the contract level;
      **(b)** **that OAuth refresh tokens are stored unencrypted, as a deliberate, accepted risk**
      (Q5.6) — a refresh token is durable, silent to use, and valid until revoked; DPAPI
      (`ProtectedData`, `CurrentUser`) is the retrofit, and **retrofitting means re-authorising every
      account**, because tokens already written cannot be re-encrypted without being read first.

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
