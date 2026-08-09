# Design — Google account integration

Change **#6 of 7**. Depends on **#5**. Requirements in [`requirements.md`](requirements.md); decision
record in [`../invoice-to-calendar/background.md`](../invoice-to-calendar/background.md).

---

## What this change actually risks

Most of it is a settings page over a store — the shape this codebase has done three times. Two things
are genuinely new, and both are recorded rather than discovered:

1. **The real adapter is a network service**, so the contract-test rule cannot be satisfied the usual
   way (friction #2). The arrangement is written down below and stated in the change description.
2. **It starts writing OAuth refresh tokens to an unencrypted store** (friction #5). Deliberate,
   accepted, and recorded — a refresh token is durable, silent to use, and valid until revoked.

---

## System architecture and components

```
 UI.Portal  /settings/google-accounts
   GoogleAccountsPage.fs ─ GoogleAccountsComponents.fs ─ GoogleAccountsBrowserModuleCreators.fs
        ▼
 UI.Types   GoogleAccountApi { GetClientSecretStatus; SetClientSecret;
                               GetAccounts; RegisterAccount; ReauthoriseAccount;
                               RemoveAccount; GetCalendarsFor; SetDefaultInvoiceCalendar }
        ▼
 Startup    GoogleAccountApiFactory.fs · GoogleAccountApiMappers.fs
        ▼
 Domain     Calendar/CalendarTypes.fs        ← CREATED here (accounts + calendars half);
                                               change #7 EXTENDS it (events + the sync plan)
            Calendar/RegisterGoogleAccountWorkflow.fs
            Calendar/ListGoogleAccountsWorkflow.fs
            Calendar/SetDefaultInvoiceCalendarWorkflow.fs
            Calendar/RemoveGoogleAccountWorkflow.fs
        ▲
 Integrations.Google
   GoogleAuthorization.fs        system browser + loopback, GoogleWebAuthorizationBroker
   GoogleCredentialDataStore.fs  IDataStore over the Credentials collection  ← see decision 1
   GoogleCalendarClient.fs       CalendarService behind ListCalendars
   GoogleAccountStore.fs         Accounts + ClientSecret collections
   GoogleCredential.fs           (change #5)
   Database/GoogleDatabaseContextModule.fs   + GetAccountCollection, GetClientSecretCollection
   GoogleEntityMappers.fs        entity ⇄ domain  (the BOTTOM mapper)
        ▲
 Integrations.Google.Database.Models (C#)
   GoogleCredential (change #5) · GoogleAccountEntity · GoogleClientSecretEntity
```

`GoogleCalendarCRUD` — the scratch prototype that proved the auth dance and the four event operations
— is **deleted** in this change (Q5.5). Its list/insert/update/delete code moves into
`GoogleCalendarClient.fs`, but only `ListCalendars` is bound here.

### What is deliberately left for change #7

`ListCalendarEvents`, `CreateCalendarEvent`, `UpdateCalendarEvent` and `DeleteCalendarEvent` are
**not declared or bound here**, even though the prototype already does all four and moving them would
cost nothing. A dependency function type is a published interface owing a contract suite, and a suite
for a type no workflow consumes is a suite written against a guess. They arrive in change #7 with the
workflows that use them — the same rule that kept `ReadDocumentText` out of change #2.

---

## Data models and interfaces

### `Domain/Calendar/CalendarTypes.fs` — the half this change creates

```fsharp
namespace MyDogsbody.Domain.Calendar

type GoogleAccountId  = private GoogleAccountId of string
type GoogleEmail      = private GoogleEmail of string        // must contain '@'
type CalendarId       = private CalendarId of string
type CalendarName     = private CalendarName of string

/// A calendar as Google lists it. The domain carries only what a person picks from.
type AvailableCalendar = { Id: CalendarId; Name: CalendarName; IsPrimary: bool }

/// A registered account, carrying its default invoice calendar - Q2.3.
///
/// The option is not laziness: a freshly authorised account genuinely has no calendar chosen yet,
/// and per Q2.11 that state renders as NOT READY with the sync action disabled, rather than as a
/// failure at the API.
type RegisteredGoogleAccount =
    { Id: GoogleAccountId
      EmailAddress: GoogleEmail
      DefaultInvoiceCalendar: CalendarId option
      NeedsReauthorisation: bool }

/// Change #7 adds EventRejected, EventNoLongerExists and the rest.
type CalendarError =
    | ClientSecretMissing
    | ClientSecretInvalid       of reason: string
    | AuthorisationCancelled
    | AuthorisationFailed       of reason: string
    | AccountAlreadyRegistered  of GoogleEmail
    | AccountNotRegistered      of GoogleAccountId
    | AccountEmailUnavailable
    | NotAuthorised             of GoogleAccountId
    | CalendarUnreachable       of message: string
    | CalendarRateLimited       of message: string
    | CalendarNoLongerExists    of CalendarId
    | NoDefaultCalendar         of GoogleAccountId
    | GoogleStoreFailed         of message: string

type LoadClientSecret  = unit -> Result<string option, CalendarError>
type SaveClientSecret  = string -> Result<unit, CalendarError>
type AuthoriseAccount  = unit -> Result<GoogleEmail * GoogleAccountId, CalendarError>
type ListGoogleAccounts = unit -> Result<RegisteredGoogleAccount list, CalendarError>
type SaveGoogleAccount  = RegisteredGoogleAccount -> Result<RegisteredGoogleAccount, CalendarError>
type RemoveGoogleAccount = GoogleAccountId -> Result<bool, CalendarError>
type ListCalendars      = GoogleAccountId -> Result<AvailableCalendar list, CalendarError>
```

`CalendarRateLimited` is separate from `NotAuthorised` on purpose: *"try again shortly"* and
*"you need to grant access"* need different responses, and collapsing them produces an alert that
tells the user to do the wrong thing.

### Workflows

| File | Signature | Notes |
| --- | --- | --- |
| `RegisterGoogleAccountWorkflow.fs` | `LoadClientSecret -> ListGoogleAccounts -> AuthoriseAccount -> SaveGoogleAccount -> unit -> Result<RegisteredGoogleAccount, CalendarError>` | Refuses before authorising if there is no client secret or the account is already registered — **the browser must not open for a registration that cannot succeed** |
| `ListGoogleAccountsWorkflow.fs` | `ListGoogleAccounts -> unit -> Result<RegisteredGoogleAccount list, CalendarError>` | Ordered by email |
| `SetDefaultInvoiceCalendarWorkflow.fs` | `ListGoogleAccounts -> ListCalendars -> SaveGoogleAccount -> string -> string -> Result<RegisteredGoogleAccount, CalendarError>` | Confirms the calendar still exists at Google before storing it |
| `RemoveGoogleAccountWorkflow.fs` | `RemoveGoogleAccount -> string -> Result<unit, CalendarError>` | Local only. No revoke |

### LiteDB, added to change #5's context

| Entity | Collection | Holds |
| --- | --- | --- |
| `GoogleClientSecretEntity` | `ClientSecret` | One row: the application-wide client secret JSON |
| `GoogleAccountEntity` | `Accounts` | Per account: id, email, default calendar id (nullable), needs-reauthorisation flag |
| `GoogleCredential` *(change #5)* | `Credentials` | The OAuth token, keyed by account |

All three get a `BsonMapper.Global.ToDocument` warm-up before the context is returned.

---

## Sequence diagrams

### Registering an account

```
GoogleAccountsPage    GoogleAccountApi    RegisterGoogleAccountWorkflow    Google (system browser)
     │ Add account        │                       │
     ├───────────────────►├──────────────────────►│ loadClientSecret
     │                    │                       │   None → ClientSecretMissing, STOP
     │                    │                       │        ► the browser never opens for a
     │                    │                       │          registration that cannot succeed
     │                    │                       ├ listGoogleAccounts
     │                    │                       │   already registered → AccountAlreadyRegistered
     │                    │                       ├ authoriseAccount ─────────► system browser
     │                    │                       │                             loopback redirect
     │                    │                       │                             calendar + userinfo.email
     │                    │                       │◄─ (email, accountId) ───────┤
     │                    │                       │   cancelled → AuthorisationCancelled, save NOTHING
     │                    │                       ├ saveGoogleAccount
     │                    │                       │   DefaultInvoiceCalendar = None   ← NOT READY
     │◄─ RegisteredGoogleAccount ─────────────────┤
     └─ transact: the row appears, marked "no calendar chosen"

  ► the TOKEN lands in the Credentials collection of Google.db, via the custom IDataStore
```

### Choosing the default invoice calendar

```
SetDefaultInvoiceCalendarWorkflow
   ├─ listGoogleAccounts     → the account exists?          no → AccountNotRegistered
   ├─ listCalendars accountId → the calendar still exists?   no → CalendarNoLongerExists
   └─ saveGoogleAccount { account with DefaultInvoiceCalendar = Some calendarId }

  ► checking BEFORE storing is what stops change #7 discovering a dead calendar id
    halfway through a sync batch.
```

### The readiness rule — Q2.11

```
account with DefaultInvoiceCalendar = None
   → shown as NOT READY, with the reason
   → change #7's sync action is DISABLED for it

  ► NOT an error at the API. A freshly authorised account genuinely has no calendar yet,
    and the option type is what makes that state representable instead of guessed at.
```

---

## Error-handling approach

`CalendarError` in the domain, `MyDogsbodyException` in the outer ring, meeting only in
`GoogleAccountApiFactory`.

Expected failures — wrapped in an `ApplicationException` and passed through `handleError`
**unlogged**: `ClientSecretMissing`, `ClientSecretInvalid`, `AuthorisationCancelled`,
`AccountAlreadyRegistered`, `AccountNotRegistered`, `AccountEmailUnavailable`, `NotAuthorised`,
`NoDefaultCalendar`, `CalendarNoLongerExists`.

Logged once: `AuthorisationFailed`, `CalendarUnreachable`, `CalendarRateLimited`,
`GoogleStoreFailed`.

`CalendarUnreachable` is logged because it is the one that will matter when change #7 starts
refusing to produce a sync plan from a failed read (friction #18) — the log is where you find out
*why* the read failed.

```
ActionNames.MyDogsbody.Integrations.Google.GoogleAuthorization.authorise
                                          .GoogleCalendarClient.listCalendars
                                          .GoogleAccountStore.{load,save,remove}Account
                                                             .{load,save}ClientSecret
ActionNames.MyDogsbody.Startup.GoogleAccountApi.*
```

---

## Testing strategy

### Contract tests against a network service — friction #2, stated not skipped

CLAUDE.md requires each dependency function type's shared suite to run against the real implementation
*and* every fake. For `ListCalendars` and `AuthoriseAccount` the real implementation is Google.

**The arrangement:**

| Layer | How it is covered |
| --- | --- |
| Every **fake** | The shared suite, as normal |
| The **real adapter** | The same shared suite, with `CalendarService` constructed over a **stubbed `HttpMessageHandler`** returning recorded Google responses. This exercises the adapter's own request-building, paging and response-parsing — the part that can actually be wrong |
| **Google itself** | **Manual, recorded in the change description**: what was run, against which account, what was observed |

That last row is a real gap and is written down as one. **What it must never become is a silently
skipped level.**

Recorded stub responses include: a normal calendar list; a **paged** calendar list (so the adapter is
proved to follow `nextPageToken` rather than returning only the first page); an empty list; a `401`;
a `403` for permission; a `429` rate limit; and a `500`. The last three are what prove
`NotAuthorised`, `CalendarUnreachable` and `CalendarRateLimited` are distinguished rather than
collapsed.

### Unit

Every `create` per rule. Every workflow's Ok path with all fields and every error case with its
payload. **Dependency-not-called** is unusually important here: `RegisterGoogleAccountWorkflow` must
not call `authoriseAccount` when there is no client secret or the account is already registered — a
test that opens a browser during a unit run is a test that has already failed.

### Integration

The Google store against a fresh temp LiteDB per test, `connection=direct`, disposed before deletion,
**with the delete asserted**. Round trips for accounts, the client secret and the token. The custom
`IDataStore` proved to write into the `Credentials` collection and read back byte-for-byte — the
characterization assertion change #5 established, now applied to a token rather than a pasted secret.

### E2E

With a faked authoriser and stubbed HTTP: register → the row appears marked not-ready; choose a
calendar → it shows and the account becomes ready; an account with no calendar stays not-ready with
its reason; remove → the row goes and the confirmation states access remains granted at Google; a
failure → `MudAlert`, cleared by the next success. **No test requires network, credentials or a
browser.**

### Manual verification — required, and recorded

Against a real Google account: register, list calendars, choose a default, remove, re-register.
Record in `outcome.md` what was run and what was observed. This is the coverage the contract level
cannot supply.

---

## Decisions taken

1. **A custom `IDataStore` writes tokens into the `Credentials` collection of `Google.db`**, rather
   than the prototype's `FileDataStore`. Change #5 established that a provider's credentials live in
   that provider's own database; a `FileDataStore` directory beside it would be a second credential
   store, which is exactly what #5 removed.
2. **The client secret is a single-row collection in `Google.db`**, not a file and not an app
   setting. Same rule: it is Google's own fact.
3. **Only `ListCalendars` is declared and bound here.** The four event operations arrive in change #7
   with the workflows that consume them, so each dependency type gets a contract suite written
   against a real consumer rather than a guess.
4. **Blocking on the async client** (friction #1). Calls already run off the render thread via
   `startWork`, and taking FsToolkit.ErrorHandling for `asyncResult` is a dependency and a second
   builder style for no defect this change has. **Revisit if change #7's batch makes the interface
   feel stuck** — that is where a batch of API calls first exists.
5. **`RegisterGoogleAccountWorkflow` checks everything it can before authorising.** Opening a browser
   and completing consent, only to refuse the registration afterwards, wastes the user's time and
   leaves a granted scope with nothing to show for it.
6. **`SetDefaultInvoiceCalendarWorkflow` verifies the calendar exists before storing it.** Otherwise
   change #7 discovers a dead calendar id halfway through a sync batch, which is the worst possible
   moment.
7. **`CalendarRateLimited` is its own case.** "Try again shortly" and "grant access" are different
   instructions.
8. **`NeedsReauthorisation` is a stored flag, not a live probe.** Checking every account's token on
   page load would make rendering a table depend on the network.
9. **`GoogleCalendarCRUD` is deleted** (Q5.5). What it proved is now in the integration; leaving it
   would leave a second, untested copy of the auth dance.

---

## Risks

| Risk | Handling |
| --- | --- |
| **Friction #2 — the real adapter is a network service** | Stub-backed contract suite over the real adapter, plus recorded manual verification. Written into the change description, never silently skipped |
| **Friction #5 — unencrypted OAuth refresh tokens.** Materially worse to leak than an API key | Recorded as an accepted risk in this change's description, with DPAPI named as the retrofit and the re-authorisation cost stated |
| **Friction #1 — blocking on async calls** | Accepted here with a stated condition for revisiting, rather than left as a habit |
| **A test opens a browser** | The authoriser is a dependency function type; every test binds a lambda. `RegisterGoogleAccountWorkflow`'s dependency-not-called tests are what keep it that way |
| **The adapter returns only the first page of calendars** | A paged stub response in the contract suite. Easy to get wrong, invisible until someone has more than a page of calendars |
| **A dead calendar id discovered mid-sync in change #7** | Verified at the moment it is chosen, not at the moment it is used |
| **The loopback port is in use, or consent is abandoned** | Both are named error cases with their own tests; neither leaves a half-registered account |
