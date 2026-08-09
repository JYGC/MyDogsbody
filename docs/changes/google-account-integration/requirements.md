# Requirements — Google account integration

Change **#6 of 7**. Depends on **#5 (`credentials-per-provider`)**. See
[`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md) for the decision
record; question ids (`Q2.3`, `Q3.1`–`Q3.6`, `Q5.6`) and friction numbers (#1, #2, #5) resolve there.

**What this change is for.** Registering Google accounts, authorising them, listing their calendars,
and choosing **which calendar each account's invoice events go on**. Ask #4.

**What it is not.** No events are created, updated, deleted or listed. No diff, no sync, no invoice
touches a calendar. Change #7 does all of that; this change gives it an authorised account with a
calendar chosen.

**It waits only on #5 landing.** Every question of its own is answered.

---

## Registering an account

### The client secret

WHEN the application is first used with Google THE SYSTEM SHALL ask for one application-wide OAuth client secret, pasted once (Q3.2).
WHEN the client secret has been supplied THE SYSTEM SHALL store it in the Google integration's own database and SHALL NOT ask again.
WHEN no client secret has been supplied THE SYSTEM SHALL say so and disable account registration, rather than failing at the authorisation call.
WHEN a user replaces the client secret THE SYSTEM SHALL accept the new value and state that existing accounts may need re-authorising.

### The consent flow

WHEN a user adds a Google account THE SYSTEM SHALL open the **system browser** and complete authorisation over a **loopback** redirect (Q3.1), as the existing prototype does.
WHEN authorisation is requested THE SYSTEM SHALL request the calendar scope needed to read and write events, and the `userinfo.email` scope so the account can show its own address (Q3.5).
WHEN authorisation completes THE SYSTEM SHALL store the resulting token in the Google integration's own database, in the `Credentials` collection change #5 created.
WHEN a user cancels or denies consent THE SYSTEM SHALL report that specifically and register nothing.
WHEN authorisation fails THE SYSTEM SHALL report the reason and register nothing — a half-registered account must not appear in the table.
WHEN a user adds an account that is already registered THE SYSTEM SHALL refuse with a named error rather than creating a duplicate.
WHEN authorisation is running THE SYSTEM SHALL NOT block the user interface.

### Many accounts

WHEN a user registers several Google accounts THE SYSTEM SHALL keep all of them, each with its own token and its own default calendar.
WHEN accounts are listed THE SYSTEM SHALL show each one's email address, so they are distinguishable.

### Removing and re-authorising

WHEN a user removes a Google account THE SYSTEM SHALL delete its local token and its record, and SHALL NOT attempt to revoke access at Google (Q3.6).
WHEN a user removes an account THE SYSTEM SHALL say that access is still granted at Google and can be revoked there — so the user is not left believing more happened than did.
WHEN a stored token has expired or been revoked THE SYSTEM SHALL report the account as needing re-authorisation, and SHALL offer to re-authorise it in place.
WHEN an account is re-authorised THE SYSTEM SHALL keep its chosen default calendar.

---

## Calendars

WHEN an account is registered THE SYSTEM SHALL be able to list that account's calendars.
WHEN a user chooses a default invoice calendar for an account THE SYSTEM SHALL persist that choice against the account (Q2.3).
WHEN a newly authorised account is shown THE SYSTEM SHALL show it as having **no** default calendar chosen, because it genuinely has none yet.
WHEN an account has no default calendar THE SYSTEM SHALL show it as **not ready**, and any action requiring a calendar SHALL be unavailable with the reason stated (Q2.11).
WHEN a chosen default calendar no longer exists at Google THE SYSTEM SHALL report that specifically rather than failing at the next use.
WHEN the calendar is chosen THE SYSTEM SHALL make it a property of the **registered account**, not a picker on the invoices page — there is no per-upload calendar override (Q2.12).

---

## Architecture

WHEN the Google integration is built THE SYSTEM SHALL reference `MyDogsbody.Domain` and nothing else outward.
WHEN the domain declares what it needs from Google THE SYSTEM SHALL declare it as function types, and the domain SHALL NOT name `CalendarService`, an OAuth type, `ILiteCollection` or an HTTP type.
WHEN the Google integration stores anything THE SYSTEM SHALL store it in its own LiteDB database — registered accounts, their default calendars, their credentials and the client secret — and SHALL NOT write to the main database or to any other integration's store.
WHEN the Google integration's database context gains a collection THE SYSTEM SHALL add a `BsonMapper.Global.ToDocument` warm-up for its entity before the context is returned.
WHEN an adapter function is written THE SYSTEM SHALL keep the outer-ring shape — dependencies first, input last, `Result<'T, MyDogsbodyException>`, written with `handleError`, one `ActionNames` entry per function.
WHEN the composition root wires this integration THE SYSTEM SHALL do so in a factory with no module-level I/O, so it is testable without reaching `Startup.fs`.
WHEN the Google client is called THE SYSTEM SHALL block on its asynchronous calls in this change, because those calls already run off the render thread; and the change description SHALL record this as a decision to revisit if change #7's batch makes the interface feel stuck (friction #1).

---

## Housekeeping

WHEN this change is complete THE SYSTEM SHALL contain no `GoogleCalendarCRUD` scratch project — what it proved has been moved into the integration (Q5.5).
WHEN this change is complete THE SYSTEM SHALL have removed `GoogleCalendarCRUD` from the solution file.

---

## Secrets at rest

WHEN OAuth tokens are stored THE SYSTEM SHALL store them unencrypted.
WHEN this change is described THE SYSTEM SHALL state that as **an accepted risk, deliberately taken** (Q5.6), not as an oversight — a refresh token is durable, silent to use, and grants calendar access until someone revokes it, which is materially worse to leak than an API key.
WHEN the description records the decision THE SYSTEM SHALL note that DPAPI (`ProtectedData`, `CurrentUser` scope) is the low-friction retrofit, and that **retrofitting means re-authorising every account**, because tokens already written cannot be re-encrypted without being read first.

---

## User interface

WHEN a user navigates to `/settings/google-accounts` THE SYSTEM SHALL display the registered accounts, each with its email address, its default invoice calendar, and its readiness.
WHEN the client secret has not been supplied THE SYSTEM SHALL show that first, with somewhere to paste it.
WHEN a user presses "Add account" THE SYSTEM SHALL start the consent flow and show that it is in progress.
WHEN a user opens an account's calendar picker THE SYSTEM SHALL populate it from **that account's own** calendars.
WHEN a user removes an account THE SYSTEM SHALL ask for confirmation, stating that access remains granted at Google.
WHEN an operation fails THE SYSTEM SHALL display the message in a `MudAlert`, and clear it on the next success.
WHEN the page's state is modelled THE SYSTEM SHALL use `cval` / `aval` / `transact` in a module creator taking `startWork` first — no MVU, no dispatch loop, no `Async.Start` inside the module creator.
WHEN this page exists THE SYSTEM SHALL be the place Google's credentials are managed — it replaces the `/settings/credentials` page change #5 removed (Q3.10).

---

## Testing

### Contract tests against a network service

Friction #2: CLAUDE.md requires each dependency function type's shared suite to run against the real
adapter *and* every fake. For a Google dependency, the real adapter is Google.

WHEN a Google dependency function type is tested THE SYSTEM SHALL run its shared contract suite against every fake **and** against the real adapter driven by a stubbed HTTP message handler, so the adapter's own request-building and response-parsing are exercised.
WHEN live verification against Google is performed THE SYSTEM SHALL record it as **manual coverage in the change description**, stating what was run and what was observed.
WHEN this arrangement is used THE SYSTEM SHALL state it explicitly rather than quietly skipping the level.
WHEN a test runs THE SYSTEM SHALL NOT require network access, credentials, or a browser.

### Levels

WHEN a domain function is added THE SYSTEM SHALL have a unit test written **before** the implementation, asserting every field of the success output and the exact error case with its payload.
WHEN a workflow short-circuits THE SYSTEM SHALL have a test proving the dependency was never called.
WHEN the Google store is tested THE SYSTEM SHALL run against a fresh temp LiteDB per test, `connection=direct`, disposed before deletion, with the delete asserted to have succeeded.
WHEN a Google entity is added THE SYSTEM SHALL have a persisted-shape test asserting the stored document's field names.
WHEN the mappers are added THE SYSTEM SHALL have contract tests asserting them field-for-field in both directions.
WHEN error translation is added THE SYSTEM SHALL have a contract test asserting each `CalendarError` case maps to the intended `MyDogsbodyException`, and each adapter exception maps to the intended `CalendarError` case.
WHEN the accounts flow is complete THE SYSTEM SHALL have an E2E test covering registering an account with a faked authoriser, choosing a default calendar, an account shown not-ready without one, removal, and a failure showing an alert.
WHEN a test is added THE SYSTEM SHALL tag it with its level.

### Gate

WHEN this change is complete THE SYSTEM SHALL build the whole solution with zero errors and pass the whole suite with zero failures and zero skips.
WHEN this change is complete THE SYSTEM SHALL have been verified by hand against a real Google account, with the result recorded.

---

## Edge cases

WHEN the loopback port is already in use THE SYSTEM SHALL report that specifically rather than hanging.
WHEN the user closes the browser without completing consent THE SYSTEM SHALL time out with a reason and register nothing.
WHEN the authorised account's email cannot be read THE SYSTEM SHALL refuse the registration, because an account with no address cannot be told apart from another.
WHEN an account has no calendars at all THE SYSTEM SHALL show an empty picker with a message, not an error.
WHEN an account has a very large number of calendars THE SYSTEM SHALL page through the listing rather than returning only the first page.
WHEN the network is unavailable THE SYSTEM SHALL report `CalendarUnreachable` and leave stored accounts intact.
WHEN Google returns a rate-limit or transient error THE SYSTEM SHALL report it distinctly from a permission failure, because the two need different responses from the user.
WHEN two accounts have the same display name THE SYSTEM SHALL still distinguish them by email address.
WHEN the stored client secret is malformed THE SYSTEM SHALL report that rather than producing an obscure authorisation failure.

---

## Out of scope

- **Every event operation** — listing, creating, updating, deleting. Change #7 adds them with their contract suites and the workflows that consume them.
- **The diff, the sync plan and the sync status column.** Change #7.
- **Encrypting tokens at rest.** Deferred deliberately; recorded, not built.
- **Revoking access at Google on removal** (Q3.6). The local token is deleted and the user is told access remains granted.
- **A per-upload calendar override** (Q2.12). The calendar is a property of the account.
- **Any Google service other than Calendar** — no Gmail, no Drive, no Contacts.
