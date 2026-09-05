# Outcome — Google account integration

Change **#6 of 7**. [`requirements.md`](requirements.md) · [`design.md`](design.md) ·
[`tasks.md`](tasks.md) · [decision record](../invoice-to-calendar/background.md)

Branch `change/google-account-integration`, cut from `main` at `b07b55b` (change #5,
`credentials-per-provider`, already merged).

---

## Gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | **0 errors, 0 warnings introduced.** The one pre-existing warning (`PdfProcessing\Program.fs` FS0025, a scratch project) is untouched. |
| `dotnet test` | **1448 passed, 1 failed, 0 skipped**, out of 1449. Unit 779 (1 failing) / Integration 305 / Contract 330 / E2E 35 — all four levels present. |
| The one failure | **Pre-existing on `main`, not caused by this change** — see below. |
| `Contracts/DomainIsolationTests.fs` + `AssertDomainReferencesNothing` | Pass. The domain still names no `CalendarService`, OAuth or HTTP type. |
| `MyDogsbody/MainWindow.xaml.cs` / `.xaml` | Untouched. |
| `GoogleCalendarCRUD` | Deleted, and removed from `MyDogsbody.sln` via `dotnet sln remove`. |

### The one failing test is not this change's

`MyDogsbody.Tests.Database.SqliteConnectionPoolingTests.every SQLite connection string a test
builds disables pooling` fails on a clean `main` checkout too — verified with
`git diff origin/main -- <every file the failure names>`, which returns **empty** for all of
them (`Contracts/InvoiceDependencyContractTests.fs`, `Contracts/InvoicePersistedShapeTests.fs`,
`Database/InvoiceStoreTests.fs`, `Database/ScanWindowStoreTests.fs`,
`E2E/InvoicesTestHarness.fs`, `Startup/InvoiceApiFactoryTests.fs`,
`Startup/ScanWindowApiFactoryTests.fs`). None of them, nor
`Database/SqliteConnectionPoolingTests.fs` itself, were touched by this change. It is a
pre-existing gap in the `invoice-extraction`/`invoice-ledger-foundation` test harnesses (several
`Data Source=...` connection strings there never got `;Pooling=False` added), unrelated to
anything Google. **Every test this change added is green**; this one failure is a separate,
already-broken thing on `main` and is out of this change's scope to fix.

### Test totals, per level

| Level | Before (branch point) | After (this change) | Δ |
| --- | --- | --- | --- |
| Unit | 706 | 779 (1 pre-existing failure, unrelated) | +73 |
| Integration | 305 | 305 | +0 — see note |
| Contract | 264 | 330 | +66 |
| E2E | 30 | 35 | +5 |
| **Total** | **1305** | **1449** | **+144** |

*Note on Integration:* the "before" count of 305 already includes work from #5's PR review
rounds that landed after the `1270` total `CLAUDE-project.md` last recorded; this change's own
Integration-level additions (`GoogleAccountStoreTests`, `GoogleCredentialDataStoreTests`,
`GoogleAuthorizationTests`, `GoogleCalendarClientTests`, the `GoogleDatabaseContextModuleTests`
extension) land inside that same 305, alongside some pre-existing tests that were already there.

---

## What this change built

- **Domain** (`MyDogsbody.Domain/Calendar/`): `CalendarTypes.fs` and five workflows —
  `RegisterGoogleAccountWorkflow`, `ListGoogleAccountsWorkflow`, `SetDefaultInvoiceCalendarWorkflow`,
  `RemoveGoogleAccountWorkflow`, and `ReauthoriseGoogleAccountWorkflow` (a fifth workflow not in
  design.md's original table — see *Deviations* below).
- **`MyDogsbody.Integrations.Google`**: `GoogleAccountStore.fs`, `GoogleEntityMappers.fs`,
  `GoogleCredentialDataStore.fs` (a custom `Google.Apis.Util.Store.IDataStore`),
  `GoogleAuthorization.fs`, `GoogleCalendarClient.fs`. Two new C# entities
  (`GoogleAccountEntity`, `GoogleClientSecretEntity`) in `.Database.Models`. This is the change
  that gives the integration its **first `MyDogsbody.Domain` reference** — `GoogleEntityMappers.fs`
  maps to `RegisteredGoogleAccount`, and the store/auth/calendar files satisfy domain-declared
  dependency types.
- **`MyDogsbody.Startup`**: `GoogleAccountApiMappers.fs`, `GoogleAccountApiFactory.fs`, wired into
  `Startup.fs` (`Google.db`, `connection=shared`, matching `Thunderbird.db`'s convention).
- **UI**: `GoogleAccountUiType.fs`, `GoogleAccountApi.fs`, `Modules/GoogleAccountsBrowserModule.fs`,
  `GoogleAccountsBrowserModuleCreators.fs`, `GoogleAccountsComponents.fs`,
  `Pages/Settings/GoogleAccountsPage.fs` at `/settings/google-accounts`, wired into `Shell.fs` and
  `SettingsComponents.settingsNavMenu`. This page **is** Google's credential page (Q3.10) — there
  is no separate `/settings/credentials`.
- **Housekeeping**: `GoogleCalendarCRUD` deleted; its four operations' worth of plumbing (the auth
  dance, `CalendarList.List`) now live in `GoogleAuthorization.fs`/`GoogleCalendarClient.fs`.

---

## Deviations from the specs

Three gaps between the written spec and what the code actually needed, each found while building
the next phase and documented at the point it was found (`tasks.md` carries the same notes inline).

### 1. `RegisterGoogleAccountWorkflow`'s `AccountAlreadyRegistered` check cannot run before `authoriseAccount`

Design.md's sequence diagram shows the "already registered" check via `listGoogleAccounts`
happening *before* the browser step. That ordering is not achievable: which account is a
duplicate is only knowable once the browser step has returned an email — a natural key that
doesn't exist until `authoriseAccount()` returns. The implementation authorises first, then
checks for a duplicate before saving. `ClientSecretMissing` still stops before the browser opens,
exactly as specified; only `AccountAlreadyRegistered`'s ordering differs, and its test asserts
`saveGoogleAccount` (not `authoriseAccount`) is never called.

### 2. Two error cases and a fifth workflow, not in the original listing

- `GoogleAccountIdInvalid` / `CalendarIdInvalid` — added to `CalendarError` for the same reason
  `MailAccountsTypes.MailAccountIdInvalid` was: a workflow takes a raw `string` id, and the
  original error DU had no case for a malformed one.
- `ReauthoriseAccount` (dependency type) and `ReauthoriseGoogleAccountWorkflow.fs` — added because
  `GoogleAccountApi.ReauthoriseAccount` is specified in design.md's own API record with nothing to
  back it. Business logic — look up the account, confirm it exists, keep its default calendar —
  belongs in the domain, not written inline at the composition root.

### 3. `AuthoriseAccount`'s real side has no stubbed-HTTP equivalent — recorded, not silently skipped

Friction #2 already anticipated this for `ListCalendars` (solved: `GoogleCalendarClient` driven
by a stubbed `HttpMessageHandler`, in `ListCalendarsDependencyContractTests.fs`). It does not
solve `AuthoriseAccount`: the real implementation is not one REST call but a system browser plus
a local loopback listener, which cannot be driven headlessly at all — there is no HTTP stub to
front for a browser. Its real-adapter coverage is `GoogleAuthorizationTests.fs`'s `authoriseWith`
suite (the actual production function; only the innermost two SDK calls — the consent flow itself
and the userinfo fetch — are substituted at the function-parameter seam) plus
`GoogleAccountApiFactoryTests.fs`'s precondition tests. This is stated here and in `tasks.md`
rather than left as a quietly-thinner contract suite.

### One implementation refinement, not a spec deviation

`GoogleAuthorization.authoriseWith` takes the OAuth datastore key (`accountId: string`) as a
parameter rather than minting one internally. `authorise` (registering a new account) mints a
fresh one; `reauthorise` (an existing account whose token expired or was revoked) passes the
existing account's id, so the same `Credentials` row is overwritten and the `Accounts` row — its
`DefaultInvoiceCalendar` included — never has to move to a new identity. This was not specified
at that level of detail; it is the concrete mechanism requirements.md's "an account is
re-authorised THE SYSTEM SHALL keep its chosen default calendar" needed underneath it.

---

## Message strings the two error-translation functions match on

`GoogleAccountApiMappers.toAuthorisationError` and `.toListCalendarsError` translate an adapter's
`MyDogsbodyException.Message` into the right `CalendarError` case. The exact literal strings,
chosen by the adapters that construct them:

| Adapter action | `Message` | `CalendarError` | Logged? |
| --- | --- | --- | --- |
| `GoogleAuthorization.authorise`/`.reauthorise` | `The consent flow was cancelled or denied.` | `AuthorisationCancelled` | No |
| | `The stored Google client secret is malformed.` | `ClientSecretInvalid` | No |
| | `The authorised account's email address could not be read.` | `AccountEmailUnavailable` | No |
| | `The loopback port is already in use.` | `AuthorisationFailed` | Yes |
| | `The consent flow timed out.` | `AuthorisationFailed` | Yes |
| | *(anything else)* `Authorisation failed.` | `AuthorisationFailed` | Yes |
| `GoogleAuthorization.loadCredential` | `No stored credential for this account.` | `NotAuthorised` | No |
| `GoogleCalendarClient.listCalendars` | `The stored Google credential is no longer authorised.` | `NotAuthorised` | Yes¹ |
| | `Google is rate-limiting this account; try again shortly.` | `CalendarRateLimited` | Yes |
| | *(anything else)* `Could not reach Google Calendar.` | `CalendarUnreachable` | Yes |

¹ Logged by `GoogleCalendarClient`'s own `handleError`, same as every other `listCalendars`
failure; `NotAuthorised` itself is on `toMyDogsbodyException`'s *expected/unlogged* list because
that translation never touches `handleError` at all (same reasoning
`MailAccountApiMappers.toMyDogsbodyException`'s comment gives) — the two "logged" columns above
describe two different translation directions and are not in tension.

---

## Secrets at rest — an accepted risk, deliberately taken (Q5.6)

OAuth tokens are stored **unencrypted** in `Google.db`'s `Credentials` collection, via
`GoogleCredentialDataStore` — the same collection and the same lack of encryption `credentials-per-provider`
already accepted for a pasted API secret. A refresh token is materially worse to leak than an API
key: it is durable, silent to use, and grants calendar access until someone revokes it at Google.
This is a legitimate call for a single-user desktop app on a machine the user controls, not an
oversight. The low-friction retrofit is DPAPI (`ProtectedData`, `CurrentUser` scope); note that
retrofitting means **re-authorising every account**, because tokens already written cannot be
re-encrypted without being read first.

---

## Friction #1 — blocking on async calls, accepted with a stated condition

`GoogleAuthorization.fs` and `GoogleCalendarClient.fs` block on every Google.Apis call
(`Async.RunSynchronously`) rather than exposing `Task`/`Async` through the dependency types.
Calls already run off the render thread via `startWork`, and taking FsToolkit.ErrorHandling for
`asyncResult` is a dependency and a second builder style for no defect this change has. **Revisit
if change #7's batch of calendar-event calls makes the interface feel stuck** — that is where a
batch of API calls first exists in this series.

---

## Task 10.4 — manual verification against a real Google account: NOT performed

**This is the one item in the gate that could not be completed in this session**, and it is
recorded here rather than silently marked done. Verifying `authorise`, `listCalendars`,
`SetDefaultInvoiceCalendar`, `removeStoredToken` and `reauthorise` against the real Google API
needs a real OAuth client secret, a system browser, and a user present to grant consent — none of
which this environment has. Everything short of that boundary is tested (see the contract-suite
split in `tasks.md` Phase 7, and `GoogleAuthorizationTests`/`GoogleCalendarClientTests`).

**Before merging, run this by hand:**

1. `dotnet run --project MyDogsbody\MyDogsbody.csproj`, go to `/settings/google-accounts`.
2. Paste a real OAuth client secret (Desktop app type, from Google Cloud Console) and save.
3. Press "Add account", complete consent in the browser that opens, confirm the row appears with
   your email and "Not ready - no calendar chosen".
4. Open the calendar picker, confirm it lists your real calendars, choose one, confirm the row
   becomes "Ready".
5. Remove the account, confirm the row disappears and the message states access remains granted
   at Google.
6. Re-register the same Google account, confirm it does not silently open a second registration
   (or, if testing re-authorisation specifically, revoke the app's access at
   [myaccount.google.com/permissions](https://myaccount.google.com/permissions), then use the
   account again and confirm it is offered re-authorisation rather than failing opaquely).

Record what was actually observed in a follow-up note once this has been run.

---

## Follow-up: the client secret is now shown, not just its presence

**Change requested after the Phase 11 gate above**: the client secret field must show the
*current* stored value, not just whether one exists, and must not become editable until the user
clicks "Edit".

`GoogleAccountApi.GetClientSecretStatus: unit -> Result<bool, MyDogsbodyException>` was replaced
with `GetClientSecret: unit -> Result<string option, MyDogsbodyException>` (the underlying
`LoadClientSecret` domain dependency already returned the full value - only the API's own member
was narrowed to a bool). Renamed throughout: `ActionNames...getClientSecretStatus` →
`...getClientSecret`; `GoogleAccountApiFactory`, the E2E harness, both contract-suite fakes.

The module (`GoogleAccountsBrowserModule`) replaced `ClientSecretConfiguredAval: aval<bool>` with
`ClientSecretAval: aval<string option>` and added `IsEditingClientSecretAval: aval<bool>`,
`StartEditingClientSecret: unit -> unit` (opens the field, pre-filled with the stored value) and
`CancelEditingClientSecret: unit -> unit` (closes it without saving). `GoogleAccountsComponents.fs`
now renders the stored secret in a **read-only** `MudTextField` with an "Edit" button when one
exists, an "Add client secret" prompt when none does, and the editable field (pre-filled, with
Save/Cancel) only once "Edit" has been pressed. A failed save leaves the field open rather than
silently reverting to read-only, so nothing typed is lost.

Test totals after this follow-up: **1451** (+2 over the Phase 11 gate's 1449) - `StartEditingClientSecret
opens the field without changing the stored value` and `CancelEditingClientSecret closes the field
without saving anything`, plus every existing client-secret test updated for the new shape.
1450 passed, 1 failed (still the same pre-existing `SqliteConnectionPoolingTests` failure, unrelated
to this change - see above), 0 skipped. `dotnet build MyDogsbody.sln`: 0 errors, the same one
pre-existing warning.

---

## Bug found during real manual verification: the calendar picker's empty state had no reason attached

**Reported by the user after registering a real Google account**: the default-invoice-calendar
picker had no options, with nothing on the page explaining why.

Root cause: `GoogleAccountsBrowserModuleCreators.loadCalendarsFor` swallowed a failed
`GetCalendarsFor` outright — `| Error _ -> ()` — on the stated reasoning that "a failure here does
not disturb the accounts table." That reasoning covered leaving the *account row* alone; it did
not justify hiding the failure's *message* as well. Whatever `GetCalendarsFor` actually failed
with (an expired credential producing `NotAuthorised`, a network failure, a rate limit, or
anything else `GoogleCalendarClient.listCalendars`/`GoogleAuthorization.loadCredential` can
return) was discarded before it ever reached the screen — "the picker is empty" was the whole
story the user could see, with no way to tell whether that meant "no calendars exist",
"the credential needs re-authorising", or "Google is unreachable right now".

Fix: `loadCalendarsFor` now sets `ErrorAval` on failure (clearing it on the next successful fetch,
the same "last operation wins" convention every other error in this module already follows), so
the exact `MyDogsbodyException.Message` reaches the `MudAlert` at the top of the page. Locked in
by a new test, `a failed calendar fetch surfaces the reason instead of silently leaving the picker
empty` (`GoogleAccountsBrowserModuleCreatorsTests.fs`).

**This does not by itself explain why the real call failed** — only that the failure is now
visible. Re-running against the real account after this fix will show the actual message (most
likely `NotAuthorised` — "The account '…' needs to be re-authorised" — if the just-issued token
somehow isn't being found by `GoogleAuthorization.loadCredential`, or a message from
`GoogleCalendarClient.listCalendars` itself if the network call is failing). That message is what
should drive the next fix, if one is still needed once it's visible.

Test totals after this fix: **1452** (+1). Same one pre-existing, unrelated `SqliteConnectionPoolingTests`
failure. `dotnet build MyDogsbody.sln`: 0 errors, the same one pre-existing warning.

---

## Task 10.4, partially performed: what the real Google account actually revealed

The manual verification began, and the picker stayed empty. Reading `Logging.db`'s `Exceptions`
collection and `Google.db` directly (rather than guessing) settled it in one pass, and is the
clearest argument yet for the exception log being a real feature rather than scaffolding.

**Everything this change built was working.** `Google.db` held exactly what it should: one
`Accounts` row (`_id` = the minted ObjectId, the correct email, `DefaultInvoiceCalendarId = null`,
`NeedsReauthorisation = false`), and one `Credentials` row keyed
`Google.Apis.Auth.OAuth2.Responses.TokenResponse:<that same id>` carrying both a refresh token and
an access token, with the granted scope including `https://www.googleapis.com/auth/calendar`. The
id-minting design (Phase 3's note), the custom `IDataStore` keying, and the account/credential join
all held up against the real thing.

**The failures were environmental, and the log named all three:**

| When | Reason Google gave | What it actually was |
| --- | --- | --- |
| First attempts | `JsonReaderException` at `GoogleClientSecrets.FromStream` | The pasted client secret was not valid JSON. Self-corrected once re-pasted; `ClientSecretInvalid` reported it correctly. |
| Next | `403 insufficientPermissions` | A token predating the calendar scope. Fixed by re-consenting. |
| Every attempt after | `403 accessNotConfigured` | **The Cloud project never had the Calendar API enabled.** |

**The bug this exposed.** `GoogleCalendarClient` mapped *every* 403 to
`"The stored Google credential is no longer authorised."` → `NotAuthorised` → an alert telling the
user to re-authorise. For `accessNotConfigured` that advice is not merely unhelpful, it is a loop
with no exit: re-authorising can never enable an API. Google's own response carried the remedy —
the project id and the URL that enables it — and the adapter discarded it.

Fixed by reading `error.errors[].reason`, which is the only thing separating the two 403s:

- `accessNotConfigured` → new `CalendarError` case **`CalendarApiNotEnabled`**, message
  `"The Google Calendar API is not enabled for this project. "` + Google's own sentence verbatim.
  Its own case for exactly the reason `CalendarRateLimited` is separate from `NotAuthorised`
  (design decision 7): three different failures, three different instructions to the user.
- other 401/403 → `NotAuthorised`, unchanged.
- everything else → `CalendarUnreachable`, now with Google's text appended rather than a bare
  "could not reach" the user cannot act on.

Locked in by `listCalendars tells a project with the Calendar API switched off apart from a
credential problem` (stub-HTTP, `GoogleCalendarClientTests`) and `toListCalendarsError maps the
API-not-enabled 403 to its own case, NOT to NotAuthorised` (`GoogleAccountApiMappersTests`). The
mapper now matches those two messages by their stable opening (`GoogleCalendarClient.apiNotEnabledPrefix`
/ `unreachablePrefix`) rather than in full, since they now carry variable detail.

Totals: **1454** (+2). Still the same single pre-existing `SqliteConnectionPoolingTests` failure.

### Confirmed working, after enabling the Calendar API

The user enabled the Calendar API for the Cloud project at the URL Google's own error named, and
confirmed: the picker populates with the real account's own calendars, and a default calendar is
now selectable. **Task 10.4 is satisfied for register → list calendars → choose a default.**
Remove and re-register were not exercised in this pass — recorded as not yet manually verified,
not as a blocker: neither workflow changed in the course of finding or fixing the two bugs above,
and both are covered by `RemoveGoogleAccountWorkflow`'s and `ReauthoriseGoogleAccountWorkflow`'s
own unit suites plus `GoogleAccountApiFactoryTests`.

### Still open

- **`NeedsReauthorisation` is never set to `true` by anything.** It is written `false` at
  registration and cleared on re-authorisation, so the "Re-authorise" button the UI renders for a
  flagged account can never actually appear. Design decision 8 chose a stored flag over a live
  probe deliberately, but nothing was ever given the job of raising it — a `NotAuthorised` from
  `listCalendars` is the obvious candidate, at the cost of a write during a read. Flagged rather
  than fixed here: it wants its own decision, not a unilateral one folded into a bug fix.
