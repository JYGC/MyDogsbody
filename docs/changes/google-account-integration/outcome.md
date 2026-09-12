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

**`ReauthoriseAccount` has the same gap, and until PR review round 10 it was not recorded.** It is
the dependency type deviation 2 added, and its real binding is `GoogleAccountApiFactory`'s
`reauthoriseAccountDependency`: the stored client secret, `GoogleAuthorization.reauthorise` (which
is `authoriseWith` over the real consent flow and email fetch, under the account's existing id), then
`GoogleEmail.create`. There is no browser to stub, for the reason above. The adapter half is
`authoriseWith`, whose suite in `GoogleAuthorizationTests.fs` covers it at the same seam as
`AuthoriseAccount`. The binding itself is reached by a test only through the unregistered-account
refusal (`GoogleAccountApiFactoryTests`, `GoogleAccountApiContractTests`), which stops before it.
Nothing reaches it in production either: the "Re-authorise" button renders only for an account
flagged `NeedsReauthorisation`, which nothing sets. **Its shared contract suite is deferred to the
change that sets that flag**, beside the other re-authorisation items rounds 2-7 recorded, rather
than written now against a path no user can take.

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
| `GoogleAuthorization.authorise`/`.reauthorise` | `The consent flow was cancelled or denied.` — OAuth `access_denied` only² | `AuthorisationCancelled` | No |
| | `The stored Google client secret is malformed.` | `ClientSecretInvalid` | No |
| | `The authorised account's email address could not be read.` | `AccountEmailUnavailable` | No |
| | `Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again.` — consent completed without the calendar scope⁵ | `AuthorisationFailed` **carrying that sentence** | No |
| | `The loopback port is already in use.` | `AuthorisationFailed` **carrying that sentence** | Yes |
| | `The consent flow timed out.` | `AuthorisationFailed` **carrying that sentence** | Yes |
| | *(anything else)* `Authorisation failed.` | `AuthorisationFailed` carrying the **inner exception's** message | Yes |
| `GoogleAuthorization.loadCredential` | `No stored credential for this account.` | `NotAuthorised` | No |
| | `The stored Google client secret is malformed.` | `ClientSecretInvalid` | No |
| `GoogleCalendarClient.listCalendars` | `The stored Google credential is no longer authorised.` — a `401`/`403`, or a refresh Google refuses as `invalid_grant`⁴ | `NotAuthorised` | Yes¹ |
| | `Google is rate-limiting this account; try again shortly.` — a `429`, or a `403` whose reason is a usage limit³ | `CalendarRateLimited` | Yes |
| | *(anything else)* `Could not reach Google Calendar.` | `CalendarUnreachable` | Yes |

¹ Logged by `GoogleCalendarClient`'s own `handleError`, same as every other `listCalendars`
failure; `NotAuthorised` itself is on `toMyDogsbodyException`'s *expected/unlogged* list because
that translation never touches `handleError` at all (same reasoning
`MailAccountApiMappers.toMyDogsbodyException`'s comment gives) — the two "logged" columns above
describe two different translation directions and are not in tension.

² Since PR review round 4. Before it, **none of the five `authorise` rows was reachable in
production**: the consent flow fails as a faulted `Task`, `Async.AwaitTask` surfaced that as an
`AggregateException`, and no typed catch matched it — so every one of them reached the user as
`One or more errors occurred. (...)`, logged. A `TokenResponseException` carrying any code other
than `access_denied` (a failed code exchange, e.g. `invalid_client`) is now `Authorisation failed.`,
logged — the user did consent, so it is not reported as their choice.

³ Since PR review round 5. Google documents `rateLimitExceeded`, `userRateLimitExceeded`,
`quotaExceeded` (and, API-wide, `dailyLimitExceeded`) as **403**s as well as 429s; before round 5
every one of them read as `NotAuthorised`. Any other 401/403 is still `NotAuthorised`.

⁴ Since PR review round 6. An expired or revoked grant does not reach the Calendar API as a `401`:
the credential refreshes first (or refreshes in answer to the `401`), Google's token endpoint answers
`invalid_grant`, and the failure is a `TokenResponseException`. Before round 6 that fell to the
catch-all and read `Could not reach Google Calendar. Error:"invalid_grant", ...` →
`CalendarUnreachable`. The token endpoint's other refusals (`invalid_client`, `unauthorized_client`)
still fall to the catch-all.

⁵ Since PR review round 7. Google's granular consent screen lets a user finish consent without
ticking Calendar; before round 7 that registered the account anyway, and every calendar fetch for it
then read `NotAuthorised` (`403 insufficientPermissions`). Only the exact calendar scope counts; a
token whose `scope` is absent is let through.

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

---

## PR review, round 1 — two defects found in the diff, both fixed

No review comments had been left on PR #21, so this round's findings are all from reading the
diff. Both were reproduced against the checked-out head with a throwaway script before anything
was changed.

### 1. A refused duplicate registration stranded its OAuth refresh token

`RegisterGoogleAccountWorkflow` authorises, *then* checks for a duplicate (deviation 1 above — it
has to, the email is what consent returns). But completing consent has already persisted a token
against the freshly minted id, and refusing by simply not saving left it there: no `Accounts` row
ever pointed at it, and the only thing that deletes a token is removing the account it belongs to.
Every refused attempt stranded another one, unencrypted and unreachable from the UI — which makes
the accepted risk in *Secrets at rest* above quietly worse than it was written.

Measured before the fix, driving the real composition with an `AuthoriseAccount` that persists a
token the way `GoogleWebAuthorizationBroker` does: one successful registration then **two** refused
duplicates left `accounts=1, credentials=3`, three distinct
`TokenResponse:<id>` rows each carrying a refresh token.

Fixed with a new dependency function type, `DiscardAuthorisation: GoogleAccountId -> Result<unit,
CalendarError>`, taken by `registerGoogleAccount` and called on the duplicate path before the error
is returned. Its result is deliberately discarded — a failed cleanup must not replace
`AccountAlreadyRegistered`, which is the answer the user actually needs, and the adapter's own
`handleError` has already recorded why it failed. Bound at the composition root to
`GoogleAuthorization.removeStoredToken`, the same adapter `RemoveAccount` uses.

### 2. `RemoveAccount` raised straight through its declared `Result`

`GoogleAuthorization.removeStoredToken` returned `unit` and was not written with `handleError`, on
the stated reasoning that a leftover token row is inert. But `IDataStore` is exception-based by
construction, so `GoogleCredentialDataStore` re-raises whatever `GoogleCredentialStore` hands back
— and `GoogleAccountApi.RemoveAccount`, declared `string -> Result<unit, MyDogsbodyException>`,
raised it. Reproduced: with the credential collection unreachable, `RemoveAccount` threw
`MyDogsbodyException: Failed to retrieve all credentials.` **after** deleting the account row. The
UI's `Error` branch never runs on that path, so there is no `MudAlert` and no reload — the row it
had just deleted stays on screen.

`removeStoredToken` now has the outer-ring shape every other function in the file has:
`handleError`, `Result<unit, MyDogsbodyException>`, and its own `ActionNames` entry. The removal
still reports `Ok` when only the token deletion fails — the account row really is gone, and
reporting failure would tell the user to retry something already done — but the failure is a
logged value rather than an escaping exception.

**It had no test at all**, which is how both halves survived: the factory tests only reached
`RemoveAccount`'s error paths, and `E2E/GoogleAccountsTestHarness.fs` rebuilt `RemoveAccount`
*without* the token deletion, so the E2E remove flow could not see it either. The harness now
mirrors `GoogleAccountApiFactory` exactly.

### Also corrected

- **`loadCredential` reported `GoogleAuthorization.authorise` as its action.** requirements.md asks
  for one `ActionNames` entry per function, and the exception log is where "the consent flow
  failed" is told apart from "the stored token could not be loaded" — the distinction *this
  change's own* manual verification was debugged through. It now has its own entry, as does
  `removeStoredToken`.
- **`GoogleAccountsBrowserModuleCreators.loadAccounts`' comment** claimed ready accounts are not
  re-fetched on reload. The code fetches calendars for every account not flagged for
  re-authorisation, ready ones included — which is correct (a ready account's picker must still
  offer a change), so the comment was corrected rather than the code. It now says plainly that this
  is one `GetCalendarsFor` call per account per reload.
- `design.md`'s sequence diagram, workflow table and decision 5 now match the shipped call order
  instead of the one deviation 1 records as unachievable.

### Round 1 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. 3 warnings, all pre-existing and none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` | 1454 → **1466** (+12), 0 skipped. |
| Failures | The same one pre-existing `SqliteConnectionPoolingTests` failure described above. |

Test totals per level after this round, each measured with `--filter "Level=..."`: Unit **784**
(the one pre-existing failure is tagged `Unit`), Integration **310**, Contract **337**, E2E **35**
— 1466 in total.

**A second environmental flake was observed once during this round** and is recorded here rather
than re-run away: `E2E\MailAccountsFlowTests.a walk hitting an unreadable directory lists it and
the other accounts still appear` failed with `UnauthorizedAccessException` from
`Directory.Delete(root, true)` in its own `finally` — its `icacls /reset` had not taken effect
before the delete. It passes in isolation, the file is untouched by this change (last modified by
PR #17), and `%TEMP%` already held five orphaned `mdb-e2e-*` directories from earlier runs, so the
cleanup has been losing this race for a while. It is an `icacls`-under-parallelism race in a
Thunderbird test, unrelated to anything Google, and distinct from the documented LiteDB
`BsonMapper` flake.

---

## PR review, round 2 — one defect found in the diff, fixed

A cold second read of the whole PR diff (round 1's own commit included, judged on the code
rather than on its summary). PR #21 still carries **no review comments**, so this round's single
finding is again from reading the diff. It was reproduced against the checked-out head with a
throwaway script before anything was changed.

### The two named authorisation failures never reached the user

`GoogleAuthorization.authoriseWith` deliberately chooses a distinct sentence for each failure it
can name, and the table above records them. `GoogleAccountApiMappers.toAuthorisationError` matched
three of those sentences and let the other two fall into its catch-all, which prefers the **inner
exception's** message over the adapter's own. That preference is right for `"Authorisation
failed."` — the adapter's catch-all, which carries nothing — but for the two named cases the inner
exception carries *less* than the sentence it replaced.

Measured before the fix, driving the real `authoriseWith` and then the real
`toAuthorisationError >> toMyDogsbodyException` pair, printing the `Message` the `MudAlert`
renders:

| Failure | Adapter's chosen `Message` | What the user actually saw |
| --- | --- | --- |
| Loopback port in use | `The loopback port is already in use.` | `Failed to listen on prefix because it conflicts with an existing registration on the machine.` |
| Consent timed out (`OperationCanceledException`) | `The consent flow timed out.` | `The operation was canceled.` |
| Consent timed out (`TaskCanceledException`) | `The consent flow timed out.` | `A task was canceled.` |

Both contradict requirements.md's own edge cases: *"WHEN the loopback port is already in use THE
SYSTEM SHALL report that **specifically**"* — the words "loopback" and "port" never appeared — and
*"WHEN the user closes the browser without completing consent THE SYSTEM SHALL time out **with a
reason**"* — `"The operation was canceled."` is not a reason, it names neither what was cancelled
nor why.

Fixed by matching those two sentences explicitly in `toAuthorisationError` and carrying them
through as the `AuthorisationFailed` payload. The catch-all is unchanged, and so is everything
else: the full exception is still logged by the adapter's own `handleError`, so no diagnostic
detail is lost — only the *user-facing* sentence changes. After the fix all five paths through
`authoriseWith` produce the message the adapter chose, and the generic path still prefers its
inner exception (`the real reason`).

Locked in by three new contract tests in `GoogleAccountApiMappersTests.fs`, written red first:
the loopback case, the timed-out case (both `OperationCanceledException` and
`TaskCanceledException`), and one that runs the whole inbound-then-outbound translation and
asserts the exact string the `MudAlert` shows.

### Considered and deliberately not changed

- **`GoogleAccountsComponents.fs`'s `let mutable editedSecret`** is the only `let mutable` in
  `MyDogsbody.UI.Portal`, and CLAUDE.md names `cval` + `transact` as the UI's sanctioned mutation
  point. Probed rather than assumed: F# lifts the captured local, the `ValueChanged` closure and
  the `Save` handler share one cell, and the typed value is what `SetClientSecret` receives — so
  it is a convention deviation with no behavioural cost. Replacing it would mean widening
  `GoogleAccountsBrowserModule`'s contract for no defect, which is more churn than the finding
  earns.
- **`GoogleAuthorization.reauthorise` reports `authorise`'s `ActionName`**, so the exception log
  cannot tell a failed re-authorisation from a failed registration — the same shape round 1 fixed
  for `loadCredential`. Left alone because the path is unreachable today: the "Re-authorise"
  button only renders for an account with `NeedsReauthorisation = true`, which nothing sets. It
  belongs with the `NeedsReauthorisation` change below, not folded in ahead of it.
- **`NeedsReauthorisation` is still never set to `true`** (see *Still open* above). Round 2 agrees
  with round 1's deferral: choosing what raises it — a `NotAuthorised` from `listCalendars` is the
  obvious candidate, at the cost of a write during a read — is a decision, not a review fix.

### Round 2 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The 3 pre-existing warnings are unchanged and none is in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` | 1466 → **1469** (+3), 0 skipped. |
| Failures | The same one pre-existing `SqliteConnectionPoolingTests` failure described above — it fails on `main` too and none of the files it flags is touched by this change. |

Per level, each measured with `--filter "Level=..."`: Unit **784** (carrying the one pre-existing
failure), Integration **310**, Contract **340** (+3), E2E **35**. Neither documented flake — the
LiteDB `BsonMapper` first-use race, or `MailAccountsFlowTests`' `icacls` cleanup race — was
observed in this round's runs.

---

## PR review, round 3 — two defects found in the diff, both fixed

A cold third read of the whole PR diff, rounds 1 and 2 included and judged on the code rather than
on their summaries. PR #21 still carries **no review comments** (0 review comments, 2 issue
comments — rounds 1 and 2's own summaries), so this round's findings are again all from reading
the diff. Both were reproduced against the checked-out head with throwaway scripts before anything
was changed.

### 1. Round 1's stranded-token fix closed one post-consent path out of three

Round 1 established the rule and built `DiscardAuthorisation` for it: consent persists a token
against the minted id *before* the workflow decides whether the registration is allowed, so any
refusal owes a discard or the token is left with no account row pointing at it, unreachable by
removal and durable until revoked at Google.

It then applied that rule to the duplicate branch only. But the duplicate check is the *first*
step after consent, not the last: `listGoogleAccounts` and `saveGoogleAccount` both run afterwards,
and either failing returned an error with the token left exactly where round 1 said it must not be
left. `RemoveAccount` cannot reach it (there is no account row), and the next "Add account" mints
a fresh id and a fresh token, so nothing ever collects it.

Measured before the fix, driving `RegisterGoogleAccountWorkflow` over a **real** temp `Google.db`
with an `AuthoriseAccount` that persists a token through the production `GoogleCredentialDataStore`
the way `GoogleWebAuthorizationBroker` does:

| Post-consent failure | Before | After |
| --- | --- | --- |
| `listGoogleAccounts` fails | `accounts=0, credentials=1` | `accounts=0, credentials=0` |
| `saveGoogleAccount` fails | `accounts=0, credentials=1` | `accounts=0, credentials=0` |
| duplicate refused (round 1's path) | `accounts=1, credentials=1` | unchanged |
| successful registration | `accounts=1, credentials=1` | unchanged |

Fixed by grouping the whole post-consent tail into one nested `result { }` and discarding on any
`Error` out of it, rather than adding a second and third call site. That closes the class instead
of the two instances: change #7 cannot reopen it by adding a step. The discard is still
deliberately `|> ignore`d — a failed cleanup must not replace the answer the user needs — and a
success still never discards.

Safe because `SaveGoogleAccount` returning `Error` *means the account was not saved*; that is the
contract the dependency type declares, and it is what makes "discard the token" the right call
rather than a guess about how far the write got.

Locked in by three unit tests written red first (`RegisterGoogleAccountWorkflowTests`): the
unreadable-account-list path, the refused-save path, and one asserting the save failure still
reaches the user when the discard itself fails. `DiscardAuthorisation` actually deleting the token
was already covered by `GoogleAccountDependencyContractTests`' shared suite against the real
adapter and the fake, so no new integration test was needed to complete the chain.

### 2. A malformed **stored** client secret reported "Authorisation failed." on the calendar path

requirements.md's own edge case: *"WHEN the stored client secret is malformed THE SYSTEM SHALL
report that rather than producing an obscure authorisation failure."* `authoriseWith` honours it —
`JsonException`/`FormatException` become `The stored Google client secret is malformed.`, unlogged.
`loadCredential` parses the *same* secret with the *same* `GoogleClientSecrets.FromStream` call and
did not, so the failure fell into its catch-all.

This is reachable through a path requirements.md explicitly supports: register an account with a
good secret, then press **Edit** and replace it with a bad paste (`SetClientSecret` validates
nothing). Every calendar picker then fails, and the reason the page showed was the one string the
requirement names.

Measured before the fix, driving the real `loadCredential` over a real temp `Google.db` holding a
real token, then the real `toListCalendarsError >> toMyDogsbodyException` pair:

| Stored secret | `CalendarError` before | MudAlert before | Logged before |
| --- | --- | --- | --- |
| `not json at all` | `CalendarUnreachable` | `Authorisation failed.` | 1 |
| `{"hello":"world"}` | `CalendarUnreachable` | `Authorisation failed.` | 1 |

After: both give `ClientSecretInvalid`, the MudAlert reads `The stored Google client secret is
malformed.`, and **0** are logged. A well-formed secret still loads its credential unchanged.

`CalendarUnreachable` was wrong on three counts, not one: it says Google could not be reached when
nothing was ever sent to Google; it is on the *logged* side of the error table, so a user's typo
wrote an exception-log entry; and it gives the user no action, where the correct case tells them to
re-paste the secret.

Fixed by wrapping only the parse — the one thing that step does, so any failure in it means exactly
one thing — and yielding a named `Error` value, which `handleError` passes through unlogged. The
mapper gained the matching case. `authoriseWith` is deliberately untouched: it already names this
failure, and its catch-all is honest for the action it describes.

Locked in by a two-case integration `[<Theory>]` in `GoogleAuthorizationTests` (message,
`ActionName`, preserved inner exception, nothing logged) and two contract tests in
`GoogleAccountApiMappersTests` (the mapping, and the whole inbound-then-outbound chain ending at
the exact string the `MudAlert` renders).

### Considered and deliberately not changed

- **`loadCalendarsFor`'s success clears another account's error.** `loadAccounts` fires one
  `GetCalendarsFor` per account, and each success sets `ErrorAval <- None`, so with several
  accounts a later success can wipe an earlier failure's message. It is the "last operation wins"
  convention every module creator in this codebase follows, and changing it means per-account error
  state — a widening of `GoogleAccountsBrowserModule`'s contract for a defect only visible with
  more than one account, one of which is broken. Flagged, not folded in.
- **`GoogleAuthorization.reauthorise` still reports `authorise`'s `ActionName`.** Round 2's
  deferral stands and its reasoning is unchanged: the path is unreachable until something sets
  `NeedsReauthorisation`.
- **`NeedsReauthorisation` is still never set to `true`** (see *Still open*). Round 3 agrees with
  rounds 1 and 2: what raises it is a decision, not a review fix.

### Round 3 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` | 1469 → **1476** (+7), 0 skipped. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **787** (+3, and carrying the one
pre-existing failure, which is tagged `Unit`), Integration **312** (+2), Contract **342** (+2),
E2E **35** — 1476 in total.

**Three flake observations, named rather than re-run away.** All three are in Thunderbird or
SQLite files that `git diff origin/main HEAD` reports as byte-identical on this branch, so none is
this change's:

- The documented LiteDB global `BsonMapper` first-use race, twice, both with the captured signature
  `CLAUDE-project.md` records — `InvalidOperationException: Collection was modified; enumeration
  operation may not execute` out of `BsonMapper.SerializeObject`, raised at the warm-up line
  `ThunderbirdDatabaseContextModule.fs:16`. Once in the baseline run
  (`ThunderbirdDatabaseContextModuleTests.getDatabaseContext exposes a working collection getter for
  every one of the five entities`) and once in the per-level Contract run
  (`ThunderbirdDependencyContractTests.a saved selection is visible to a later load...`, real
  adapter). Different tests, one cause, and the cause is the open item that paragraph already says
  wants a process-wide lock and its own change folder.
- A **third**, not previously recorded: `E2E\MailAccountsFlowTests.a message count survives the next
  scan and keeps the time it was taken` failed once in the first full run and passed in the second
  and in isolation. It is a `WaitForAssertion`-timed bUnit render over the same Thunderbird LiteDB
  store, so most likely the same race seen from the E2E level, but that is not asserted here — what
  *is* checked is that every file in its chain (`MailAccountsFlowTests.fs`,
  `MailAccountsTestHarness.fs`, `MailAccountApiFactory.fs`, `MailAccountApiMappers.fs`,
  `ThunderbirdStore.fs`, `MailAccountsBrowserModuleCreators.fs`) is byte-identical to `origin/main`.
  Recorded so the next round has a name for it rather than rediscovering it.

---

## PR review, round 4 — two defects found in the diff, both fixed

A cold fourth read of the whole PR diff, rounds 1–3 included and judged on the code. PR #21 still
carries **no review comments** (0 review comments; 3 issue comments, which are rounds 1–3's own
summaries), so both findings are from reading the diff. Both were reproduced against the
checked-out head with throwaway scripts before anything was changed — driving the **real**
`runRealConsentFlow`, and Google.Apis.Auth's own `AuthorizationCodeInstalledApp` with its browser
step replaced by a fake code receiver and its token endpoint stubbed.

### 1. Every named consent failure was unreachable in production

`authoriseWith` awaited the consent flow with `Async.AwaitTask |> Async.RunSynchronously`.
`GoogleWebAuthorizationBroker.AuthorizeAsync` is an async method, and so is `runRealConsentFlow`
(a `task { }`), so every failure arrives as a **faulted `Task`** — and `Async.AwaitTask` surfaces
a faulted task as an `AggregateException`. None of the typed catches (`TokenResponseException`,
`JsonException`, `FormatException`, `HttpListenerException`) matches that, so each fell to the
logged catch-all, and the mapper's catch-all then showed the `AggregateException`'s own sentence.

The unit tests passed over this because every fake consent flow **raised synchronously**, before a
`Task` existed — a shape the real flow never produces. That also means round 2's fix (keeping the
adapter's loopback-port sentence) was correct in the mapper but could not reach a user: round 2's
own measurement drove `authoriseWith` with a synchronous throw.

Measured before → after, the string the `MudAlert` renders (`toAuthorisationError >>
toMyDogsbodyException`) and the log count:

| Failure | Before | After |
| --- | --- | --- |
| Malformed secret, **real** `runRealConsentFlow` | `One or more errors occurred. (Unexpected character encountered while parsing value: n. ...)`, logged 1 | `The stored Google client secret is malformed.`, logged 0 |
| Consent denied (the library's own installed-app flow) | `One or more errors occurred. (Error:"access_denied", Description:"", Uri:"")`, logged 1 | `The consent flow was cancelled or denied.`, logged 0 |
| Loopback port in use | `One or more errors occurred. (Failed to listen on prefix)`, logged 1 | `The loopback port is already in use.`, logged 1 |
| Consent timed out | `The consent flow timed out.`, logged 1 | unchanged — a cancelled task surfaces as `OperationCanceledException` either way |

Fixed by awaiting with `.GetAwaiter().GetResult()`, which rethrows the original exception, for both
the consent flow and the email fetch. Two refinements the fix needed so that it did not trade one
defect for another:

- **The `TokenResponseException` catch is narrowed to `access_denied`.** Unwrapping alone would have
  made a failed code *exchange* (`invalid_client` — a wrong or rotated `client_secret`, after the user
  had consented) read `The consent flow was cancelled or denied.`, **unlogged**: telling a user who
  just said yes that they said no, with nothing in the log. Measured against the library's flow with
  the token endpoint stubbed to `401 invalid_client`: now `Authorisation failed.`, logged, inner
  `TokenResponseException` carrying `invalid_client`.
- **Malformed now means any failure to parse the secret, on this path as on `loadCredential`'s.**
  Widening, noted: `GoogleClientSecrets.FromStream(...).Secrets` raises `InvalidOperationException`
  for well-formed JSON that is not an OAuth client secret (`{ "hello": "world" }`, or a pasted
  service-account key) and `NullReferenceException` for an empty paste — `SetClientSecret` validates
  nothing, so both are storable. `runRealConsentFlow` turns any parse failure into the
  `FormatException` `authoriseWith` already names as malformed.

Tests, written red first (12 failing before the fix, each for the predicted reason): the five
typed-failure tests now fake the consent flow as a faulted `Task` (a cancelled one for the timeout),
which is what the real flow returns; a four-input theory drives the **real** `runRealConsentFlow`
with malformed secrets (safe — the parse fails before any browser or listener exists); and a test
for the `invalid_client` exchange failure.

### 2. A registration that failed *inside* `authorise`, after consent, still stranded its token

Round 3 grouped the workflow's post-consent tail so every exit hands the token back. But the first
post-consent step is not in the workflow at all: `authoriseWith` reads the account's email after
the consent flow has already persisted the token. An `Error` from there carries no id, so
`DiscardAuthorisation` cannot be pointed at it, and no account row will ever carry the minted id,
so removal cannot reach it either — the same stranded refresh token rounds 1 and 3 fixed elsewhere,
and a contradiction of design decision 5's "any exit".

Measured with the library's own installed-app flow persisting a real token through
`GoogleCredentialDataStore` over a temp `Google.db`:

| Failure after consent | Before | After |
| --- | --- | --- |
| userinfo returns no email (`AccountEmailUnavailable`) | `credentials=1` | `credentials=0` |
| userinfo call fails (`503`) | `credentials=1` | `credentials=0` |
| success (control) | `credentials=1` | `credentials=1` |

Fixed by `GoogleAuthorization.authoriseNewAccountWith`: `authoriseWith`, then `removeStoredToken`
for the freshly minted id on any `Error`. That id is minted for the one attempt and never handed out
on failure, so nothing else can refer to a token under it; a failure before consent wrote anything
finds nothing to delete and logs nothing. `authorise` now binds it. `reauthorise` deliberately does
not — its id belongs to a registered account, and whether a failed re-authorisation should delete
that account's token belongs with the `NeedsReauthorisation` change. `AuthoriseAccount`'s doc
comment now states the contract.

Locked in by four integration tests over a real temp `Google.db`, written red first: both
post-consent failures leave no token; success keeps it; and a failure before consent is reported
exactly as before (`cancelled or denied`, unlogged).

**One residual, stated rather than fixed:** `GoogleAccountApiFactory`'s `authoriseAccount` re-validates
the email the adapter accepted with `GoogleEmail.create`, and a non-blank email without `@` would
still return `AccountEmailUnavailable` without discarding. Google's userinfo endpoint does not return
such an address, and that function binds the real browser flow, so there is no seam to put a red
test through — left, rather than added untested.

### Considered and deliberately not changed

Rounds 1–3's deferrals stand, for the reasons they give: `NeedsReauthorisation` is never raised;
`reauthorise` reports `authorise`'s `ActionName`; a `loadCalendarsFor` success clears another
account's error; the `let mutable editedSecret`. This round measured two further facts that the
`NeedsReauthorisation` change will need, recorded here rather than acted on, since neither path is
reachable until that change exists:

- **Re-authorising never reaches the consent screen while a refresh token is stored.** The library's
  installed-app flow reuses any stored token that has a refresh token — revoked or not — so
  `reauthorise` hands `fetchRealAccountEmail` the stale access token instead of a fresh consent.
- **A revoked refresh token reads as `CalendarUnreachable`, not `NotAuthorised`,** on the first
  calendar fetch: the refresh fails with a `TokenResponseException` (`invalid_grant`) that
  `GoogleCalendarClient`'s catch-all maps to "Could not reach Google Calendar.", logged. The library
  also deletes the stored token when that happens, so the *next* fetch reads `NotAuthorised`.
  **Overturned by PR review round 6:** unlike the bullet above, this path does not wait on the
  `NeedsReauthorisation` change — the calendar fetch runs for every account on every page load.

### Round 4 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` | 1476 → **1485** (+9), 0 skipped. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **792** (carrying the one pre-existing
failure, which is tagged `Unit`), Integration **316**, Contract **342**, E2E
**35** — 1485 in total.

**One full-suite run in this round stalled, and it is not explained.** Its testhost sat at about 25 s of CPU for four and a half minutes (the whole suite normally takes 7-9 s) and was killed before a dump was taken, so the stalled test is not named. Four further full runs under `--blame-hang --blame-hang-timeout 60s` all completed in 10-11 s with no hang and no dump. This round's change does block on `.GetAwaiter().GetResult()`, but every fake it adds returns an already-completed `Task`, and production calls run on the thread pool via `Async.Start`, so reading finds no deadlock path. Recorded as an unreproduced stall rather than attributed to a known flake: if it recurs, take the dump before killing it.

---

## PR review, round 5 — one defect found in the diff, fixed

A cold fifth read of the whole PR diff, rounds 1–4 included and judged on the code. PR #21 still
carries **no review comments** (0 review comments; 4 issue comments, which are rounds 1–4's own
summaries), so the finding is from reading the diff.

### A rate-limited account was told to re-authorise

`GoogleCalendarClient.listCalendarsVia` split 403s by one reason only: `accessNotConfigured` became
`CalendarApiNotEnabled`, and **every other 401/403** became `"The stored Google credential is no
longer authorised."` → `NotAuthorised`. But Google's Calendar API error guide documents its usage
limits as **403**s as well as 429s — `userRateLimitExceeded`, `rateLimitExceeded` and
`quotaExceeded`, all domain `usageLimits`, remedy "back off and retry". Each one read as a
permission failure, which is exactly what requirements.md forbids ("WHEN Google returns a
rate-limit or transient error THE SYSTEM SHALL report it distinctly from a permission failure")
and what design decision 7 gives `CalendarRateLimited` its own case to prevent.

**This overturns a recorded decision.** Task 10.4's fix above left "other 401/403 → `NotAuthorised`,
unchanged", and tasks.md 4.1 specified `403` → `NotAuthorised` outright. Neither weighed the
usage-limit 403s; the requirement they sit under does, so the requirement wins.

Measured before the fix, driving the real adapter over a stubbed `HttpMessageHandler` with the
guide's own bodies, then the real `toListCalendarsError` and `toMyDogsbodyException` — the string
the `MudAlert` renders:

| Google's response | Before | After |
| --- | --- | --- |
| `403 userRateLimitExceeded` | `The account '…' needs to be re-authorised.` (`NotAuthorised`) | `Google is rate-limiting this account; try again shortly.` (`CalendarRateLimited`) |
| `403 rateLimitExceeded` | same, `NotAuthorised` | same, `CalendarRateLimited` |
| `403 quotaExceeded` | same, `NotAuthorised` | same, `CalendarRateLimited` |
| `403 dailyLimitExceeded` | same, `NotAuthorised` | same, `CalendarRateLimited` |
| `429 rateLimitExceeded` (control) | `CalendarRateLimited` | unchanged |
| `403 insufficientPermissions` (control) | `NotAuthorised` | unchanged |
| `401 authError` (control) | `NotAuthorised` | unchanged |
| `403 accessNotConfigured` (control) | `CalendarApiNotEnabled` | unchanged |

Fixed by a named list of usage-limit reasons in `GoogleCalendarClient`, matched on a 403 in the
same clause as a 429 and ordered ahead of the 401/403 clause. Widening, noted:
`dailyLimitExceeded` is not in the Calendar guide's own list but is the API-wide usage-limit reason
from the same `usageLimits` domain, so it is included. For a daily quota, "try again shortly" is
optimistic about the wait. It is still the right kind of instruction (wait, don't re-authorise), and
the adapter logs Google's full response.

Tests, written red first (8 failing before the fix, each because the adapter returned the
not-authorised message): a four-reason theory in `GoogleCalendarClientTests` asserting the exact
message, `ActionName` and the preserved 403 `GoogleApiException`; the same four reasons in the
`ListCalendars` contract suite, asserting `Error (CalendarRateLimited <message>)` whole; and one
control fact, green before and after, locking `insufficientPermissions` to `NotAuthorised`.
No E2E test was added: the E2E harness substitutes `ListCalendars` with a fake, so it never reaches
this adapter. The adapter → mapper → alert chain is covered link by link: this adapter test, plus
the existing `toListCalendarsError maps a 429-shaped message to CalendarRateLimited`.

### Considered and deliberately not changed

- **Round 4's `.GetAwaiter().GetResult()` fix stands.** Its fakes return faulted or cancelled `Task`s,
  which is the shape the real flow produces. Its malformed-secret theory drives the real
  `runRealConsentFlow`, whose parse sits inside the `task { }`, so that failure also arrives as a
  faulted task. The one way a blocking wait could deadlock is a captured `SynchronizationContext`,
  and that does not arise in production: every caller is reached through the page's
  `Async.Start` on the thread pool. The suite did not stall this round (see the gate).
- A userinfo call hitting `HttpClient`'s own timeout surfaces as `TaskCanceledException` and reads as
  "The consent flow timed out." Imprecise, but the remedy is the same (try again), and the minted
  token is handed back either way.
- `loadCredential` builds a `GoogleAuthorizationCodeFlow` per calendar fetch and never disposes it.
  Its `HttpClient` only opens a connection to refresh a token, so there is no measurable cost in a
  desktop app.
- Rounds 1–4's deferrals stand, for the reasons they give.

### Round 5 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | 1485 → **1494** (+9), 0 skipped, 11 s. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **792** (carrying the one pre-existing
failure, which is tagged `Unit`), Integration **321** (+5), Contract **346** (+4), E2E **35** — 1494
in total.

---

## PR review, round 6 — two defects found in the diff, both fixed

A cold sixth read of the whole PR diff, rounds 1–5 included and judged on the code. PR #21 still
carries **no review comments** (0 review comments; 5 issue comments, which are rounds 1–5's own
summaries), so both findings are from reading the diff. Both were reproduced against the
checked-out head before anything was changed.

### 1. An expired or revoked refresh token read as "Could not reach Google Calendar."

**This overturns a recorded decision.** Round 4 measured this exact behaviour and listed it under
*Considered and deliberately not changed*, as a path not reachable until the `NeedsReauthorisation`
change exists. That holds for the bullet beside it (re-authorising), not for this one: the calendar
fetch runs for every account on every load of `/settings/google-accounts` (`loadAccounts` →
`GetCalendarsFor`), so a revoked grant reaches it the next time the page opens. It is also the most
common way a token stops working here — an OAuth client left in Testing publishing status, the
likely state of a single user's own Desktop client, issues refresh tokens that expire after seven
days.

With Google an expired or revoked grant does not arrive as a `401` from the Calendar API: the
`UserCredential` refreshes first (or refreshes in answer to the `401`), Google's token endpoint
answers `400 invalid_grant` ("Token has been expired or revoked."), and the failure is a
`TokenResponseException` — which no `GoogleApiException` clause matches, so it fell to the
catch-all. requirements.md: "WHEN a stored token has expired or been revoked THE SYSTEM SHALL report
the account as needing re-authorisation", and "report a rate-limit or transient error distinctly
from a permission failure" — broken here in the direction round 5 did not look at: a permission
failure reported as a transient one.

Measured with a throwaway script driving the real `listCalendarsVia` with a real `UserCredential`
(its token endpoint stubbed to Google's `invalid_grant` body), then the real `toListCalendarsError`
and `toMyDogsbodyException` — the string the `MudAlert` renders:

| Path | Before | After |
| --- | --- | --- |
| Access token lapsed — refreshed before the call | `Could not reach Google Calendar. Error:"invalid_grant", Description:"Token has been expired or revoked.", Uri:""` (`CalendarUnreachable`), logged 1 | `The account '…' needs to be re-authorised.` (`NotAuthorised`), logged 1 |
| Access token still live — Google answers `401`, refreshed in response | same, `CalendarUnreachable`, logged 1 | same, `NotAuthorised`, logged 1 |

Both are logged by the adapter's own `handleError`, like every other `listCalendars` failure
(footnote ¹). In both paths the library deletes the stored token, which is why the *next* fetch
already read `NotAuthorised` through `No stored credential for this account.` — before the fix the
same account got two different diagnoses on consecutive loads.

Fixed by one clause in `GoogleCalendarClient.listCalendarsVia`: a `TokenResponseException` carrying
`invalid_grant` reports `The stored Google credential is no longer authorised.`, the message
`toListCalendarsError` already maps to `NotAuthorised`. **Not widened:** the token endpoint's other
refusals (`invalid_client`, `unauthorized_client`) implicate the client secret rather than the
account's grant, and which instruction they deserve belongs with the `NeedsReauthorisation`
change; they keep the catch-all, with Google's code appended.

Tests, written red first (3 failing before the fix, each because the adapter returned the "Could
not reach" message): a two-path theory in `GoogleCalendarClientTests` asserting the exact message,
the `ActionName` and the preserved `TokenResponseException` carrying `invalid_grant`; and the same
case in the `ListCalendars` contract suite, asserting `Error (NotAuthorised accountId)` whole. As in
round 5, no E2E test: the E2E harness substitutes `ListCalendars` with a fake, so the adapter →
mapper → alert chain is covered link by link — these tests, plus the existing
`toListCalendarsError maps a 401/403-shaped message to NotAuthorised`.

### 2. Replacing the client secret did not say what it can cost

requirements.md: "WHEN a user replaces the client secret THE SYSTEM SHALL accept the new value,
return the field to its read-only display, **and state that existing accounts may need
re-authorising**." Nothing on the page said so, and neither tasks.md nor this file recorded it as
deferred — a requirement with no behaviour. The cost is real: a replacement can belong to a
different OAuth client, Google will not refresh a token issued to the old one, and every registered
account's next calendar fetch then fails with nothing on screen connecting it to the replacement.

Fixed in `GoogleAccountsComponents.fs`: the edit panel says "Replacing the client secret may mean
existing accounts need re-authorising." whenever it is opened over an already-stored secret — not
when the first secret is supplied, since no account can exist before one. It is said while the user
is replacing, before Save, which needs no new module state.

Tests (E2E, bUnit over the real harness): `replacing a stored client secret states that existing
accounts may need re-authorising`, red first (the sentence not found), and a control, `supplying the
first client secret does not warn about existing accounts`, green before and after.

### Considered and deliberately not changed

- **Round 5's usage-limit matching stands.** Its four reasons are Google's `usageLimits` reasons,
  disjoint from the other 403 reasons the Calendar API documents (`insufficientPermissions`,
  `accessNotConfigured`, `forbidden`, `domainPolicy`), so no other 403 changed behaviour; and its
  test bodies are Google's own shape, which still carries `errors[].reason` beside the newer
  `status`/`details` fields. A `403 domainPolicy` (a Workspace admin blocking the app) still reads
  `NotAuthorised`, which re-authorising cannot fix — the pre-existing "any other 401/403" rule, not
  something round 5 changed, and not reachable for a personal account.
- `NotAuthorised`'s sentence names the account by its opaque id rather than its email — a payload
  choice shared with `AccountNotRegistered` and `NoDefaultCalendar`, not something this round
  introduced.
- Rounds 1–5's deferrals stand, for the reasons they give.

### Round 6 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | 1494 → **1499** (+5), 0 skipped, 11 s wall. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **792** (carrying the one pre-existing
failure, which is tagged `Unit`), Integration **323** (+2), Contract
**347** (+1), E2E **37** (+2) — 1499 in total.

---

## PR review, round 7 — one defect found in the diff, fixed

A cold seventh read of the whole PR diff, rounds 1–6 included and judged on the code. PR #21 still
carries **no review comments** (0 review comments; 6 issue comments, which are rounds 1–6's own
summaries), so the finding is from reading the diff. It was reproduced against the checked-out head
before anything was changed.

### A consent that did not grant Calendar access still registered the account

`authoriseWith` treated *completing* consent as *granting* it. Google's granular consent screen
gives each non-sign-in scope its own checkbox whenever a request carries one to three sign-in scopes
plus at least one non-sign-in scope (Google's "granular permissions" guide). That is exactly
`GoogleAuthorization.scopes`: `userinfo.email` (sign-in) plus `calendar`. The guide says granular
permissions are always on for newly created OAuth client IDs, which is what a user of this app
pastes. In Google's words, "users may not grant all scopes your app requests", and the installed-app
guide says the app "must verify which scopes were actually granted". Nothing here read the token's
`scope`.

So a user who left Calendar unticked got an account in the table, against requirements.md's "a
half-registered account must not appear in the table". Every calendar fetch for it then answered
`403 insufficientPermissions` → `NotAuthorised` → "needs to be re-authorised", on a page that offers
no way to re-authorise (nothing sets `NeedsReauthorisation`). The only way out was Remove, then Add
again.

Measured with a throwaway script driving the real `authoriseNewAccountWith` over a temp `Google.db`
(a consent that persists a token granting `userinfo.email openid` only), then the real
`RegisterGoogleAccountWorkflow`:

| | Before | After |
| --- | --- | --- |
| `authoriseNewAccountWith` | `Ok ("person@gmail.com", <id>)` | `Error`: `Google Calendar access was not granted - tick the calendar permission on Google's consent screen and try again.` |
| Tokens left in `Credentials` | 1 | 0 |
| `registerGoogleAccount` | `Ok`, registered with no default calendar | `Error (AuthorisationFailed "<that sentence>")` |
| Logged | 0 | 0 |

Fixed in `GoogleAuthorization.authoriseWith`: after consent and before the email is read, a token
whose `scope` does not contain the exact calendar scope is refused with that sentence, unlogged (an
`ApplicationException` inner carrying the granted scopes). It is the user's choice, like a cancelled
consent. `authoriseNewAccountWith` already hands back the token for any post-consent failure, so
none is stranded. `toAuthorisationError` keeps the sentence (`AuthorisationFailed`), as it does for
the loopback-port and timed-out sentences, because the inner exception carries no instruction.
`reauthorise` goes through `authoriseWith` too, so it gets the same check.

Two deliberate choices. Only the exact scope counts: `.../calendar.readonly` shares its prefix and
must not pass a substring match. And a token whose `scope` is absent is let through: Google's code
exchange always reports it, so refusing on silence would only risk refusing every registration, and
the calendar fetch still reports an insufficient scope.

Tests, written red first. Four failed before the fix, each for the predicted reason: the two refused
cases returned `Ok`, the mapper took the inner exception's text, and the integration case reached
its must-not-be-called email fake. They are:

- a two-case unit theory asserting the exact message, the `ActionName`, the preserved inner
  exception, nothing logged and the email never read;
- a four-case unit control (calendar granted in either order; `scope` empty or absent), green
  before and after;
- an integration test proving the token is handed back;
- a contract test over `toAuthorisationError` and the whole chain to the `MudAlert` string.

`GoogleAuthorizationTests`' shared fake credential is now a real `UserCredential` carrying every
requested scope rather than `null`, since `authoriseWith` now reads it. No E2E test: the E2E harness
substitutes `AuthoriseAccount` with a fake, so it never reaches this adapter.

### Considered and deliberately not changed

- **Round 6's `invalid_grant` clause stands.** Its test drives a real `UserCredential` over a real
  `GoogleAuthorizationCodeFlow` whose token endpoint is stubbed, so the exception shape is the
  library's own (`TokenResponseException`, unwrapped by `ClientServiceRequest.Execute`) on both the
  proactive-refresh and the refresh-on-`401` paths. It is guarded to `invalid_grant` only, and no
  other failure changed message: a `GoogleApiException` never matches it, and every other
  `TokenResponseException` code still reaches the catch-all.
- **Round 6's client-secret warning stands.** It is shown in the edit panel, while replacing, and
  only over an already-stored secret.
- **`ReauthoriseGoogleAccountWorkflow` saves whichever email the re-consent returns.** Choosing a
  different Google account in the browser would re-key the row to another address, possibly one
  already registered. Not reachable until something sets `NeedsReauthorisation`, so it joins that
  change's list next to round 4's two measured facts.
- Rounds 1–6's deferrals stand, for the reasons they give.

### Round 7 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this change touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | 1499 → **1507** (+8), 0 skipped, 8 s. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **798** (+6, carrying the one
pre-existing failure, which is tagged `Unit`), Integration **324** (+1), Contract **348** (+1),
E2E **37**. 1507 in total.

---

## PR review, round 8 — two defects found in the diff, both fixed

An eighth cold read of the whole PR diff, rounds 1–7 included and judged on the code. PR #21 still
carries **no review comments** (0 review comments; 7 issue comments, which are rounds 1–7's own
summaries), so both findings come from reading the diff. Round 7's granted-scope check was verified
first, against the real SDK rather than from memory.

### Round 7's granted-scope check, verified against Google.Apis.Auth 1.72.0

- **It reads the right property.** Reflection on the package's `TokenResponse`: `Scope` carries
  `[JsonProperty("scope")]`, the RFC 6749 §5.1 field.
- **The real code exchange fills it verbatim.** A throwaway probe outside the repository drove the
  real `GoogleAuthorizationCodeFlow` through `AuthorizationCodeInstalledApp` (the same path
  `GoogleWebAuthorizationBroker` takes). It used a fake `ICodeReceiver` and stubbed only the token
  endpoint, through the flow's `HttpClientFactory`. For a sign-in-only grant, `credential.Token.Scope`
  came back as the endpoint's space-separated string, `https://www.googleapis.com/auth/userinfo.email
  openid`. That same string is what `IDataStore.StoreAsync` receives, and what
  `NewtonsoftJsonSerializer` reads back. A response with no `scope` gives `Scope = null`.
- **Letting a token that reports no scope through is sound, and for a stronger reason than round 7
  gave.** RFC 6749 §5.1 makes `scope` optional only "if identical to the scope requested by the
  client". So when it is absent, the scope was granted as requested; that is not a refusal left
  unsaid.
- **Tests and clean-up hold.** The unit theory asserts the exact `ActionName`, the message, the
  preserved `ApplicationException` inner carrying the granted scopes, that nothing was logged, and
  that the email was never read. The integration test proves `authoriseNewAccountWith` hands back
  the token consent wrote (0 rows left). The contract test covers `toAuthorisationError` and the
  chain to the `MudAlert` string. The outbound `AuthorisationFailed` exception has no
  `ApplicationException` inner, but nothing on that side logs (the factory only `Result.mapError`s),
  so the refusal stays unlogged end to end.

### 1. An account with no calendars showed an empty picker with no message

requirements.md, *Edge cases*: "WHEN an account has no calendars at all THE SYSTEM SHALL show an empty
picker with a message, not an error." The component rendered an empty `MudSelect` and nothing else.
The E2E test named `an account with no calendars stays not ready, with a reason, …` asserted only the
`Not ready - no calendar chosen` chip, which every account without a default calendar shows whether
its picker holds twenty calendars or none. So manual verification's own complaint ("the picker had no
options, with nothing on the page explaining why") was still true for a fetch that *succeeds* with
zero calendars. No deferral of this requirement was recorded anywhere.

Fixed in `GoogleAccountsComponents`. A pure `noCalendarsMessage` returns
`No calendars were found for this account.` for an account whose `CalendarsByAccountIdAval` entry
holds `[]`, and nothing otherwise. The picker renders that message as a caption under the `MudSelect`.
Only *loaded and empty* earns it. An account with no entry is still loading, or its fetch failed, and
a failure already has the page's `MudAlert`, so "no calendars" there would be a claim nobody checked.

Tests, written red first. Against a stub `noCalendarsMessage` returning `None`, three failed, each for
the predicted reason:

- two unit tests (`… says so for an account whose calendars loaded and are empty`,
  `… reads only that account's own calendars`), each `Expected: Some(No calendars were found for this
  account.)`, `Actual: null`;
- the E2E no-calendars flow, now asserting the message: "Sub-string not found".

Green before and after: two unit controls (`… says nothing for an account whose calendars have not
loaded`, `… says nothing for an account with calendars`), and a new `DoesNotContain` in the E2E
`choosing a default calendar makes a not-ready account ready`. The unit tests are in the new
`UI/Components/GoogleAccountsComponentsTests.fs`, following `MailAccountsComponentsTests`' pure-helper
precedent.

### 2. One outbound error translation was never asserted

`toMyDogsbodyException`'s `CalendarApiNotEnabled` case returns an *expected* exception: unlogged, with
an `ApplicationException` inner, carrying Google's own sentence. Every other `CalendarError` case has
its own contract test. This one appeared only in `every CalendarError case produces a non-empty message
and the declared action`, which checks neither the message nor the unlogged marking. That misses
requirements.md's "each `CalendarError` case maps to the intended `MyDogsbodyException`". Added
`CalendarApiNotEnabled becomes an unlogged exception carrying Google's own sentence` (Contract): it
asserts the `ActionName`, the exact message, the `ApplicationException` inner, and
`ExceptionHelpers.isApplicationException`. **Test-only:** the translation was already right, so the
test passed on its first run. This adds missing coverage and changes no behaviour, so there is no red
run to report.

### Considered and deliberately not changed

- **A refused scope check leaves the grant live at Google.** Only the local token is deleted, as for
  every other post-consent failure, and Q3.6 chose not to revoke.
- **`reauthorise`'s refusal does not discard the account's token.** That decision belongs to the
  `NeedsReauthorisation` change (round 4's note on `authoriseNewAccountWith`), and the path is
  unreachable today.
- Rounds 1–7's deferrals stand, for the reasons they give.

### Round 8 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, none in a file this round touches (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | 1507 → **1512** (+5), 0 skipped, 9 s. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **802** (+4, carrying the one
pre-existing failure, which is tagged `Unit`), Integration **324**, Contract **349** (+1), E2E **37**
(two existing flows gained assertions). 1512 in total.

---

## PR review, round 9 — three defects found in the diff, all fixed

A ninth cold read of the whole PR diff, rounds 1–8 included. PR #21 still carries **no review
comments** (0 review comments; 8 issue comments, which are rounds 1–8's own summaries), so every
finding comes from reading the diff. Round 8's picker message was checked first. All three findings
are the same kind: a UI requirement that no test would notice breaking.

### Round 8's `noCalendarsMessage`, re-checked

- **Correct in every state the page reaches.** Loading, before any list has arrived: no entry,
  nothing said. Loaded empty: the message. Loaded with calendars: nothing. A first fetch that
  failed: no entry, and the alert says why. Removal: the entry goes with the account. Another
  account's list: keyed by that account's own id. An account flagged `NeedsReauthorisation` never
  renders the picker at all.
- **One claim in its doc comment was wrong.** A *re-fetch* that fails does not drop the entry. A
  reload keeps the last list that loaded until a new one replaces it, as a non-empty picker's
  options do too. So an account that last loaded empty keeps its message beside the new alert. That
  is the last answer Google gave, not an unchecked claim, and blanking it would also blank every
  non-empty picker on a transient failure. Comment corrected; behaviour unchanged.
- **Its test file is sound.** It is tagged `Unit` and compiled above `Program.fs`. It sits in
  `UI/Components/` beside `MailAccountsComponentsTests`, mirroring the source layout. The component
  reaches no API: it reads the module's avals and calls the module's commands.

### The build's two test-file warnings are `main`'s, not this PR's

`git diff origin/main...HEAD` touches neither `Integrations/Documents/PdfDocumentReaderTests.fs` nor
`Database/ScanWindowStoreTests.fs`, nor what their warning lines call (`PdfDocumentBuilder`,
`ScanWindowStore.deleteScanWindow`). Measured as well as inferred: an export of `origin/main`
(`git archive`, built in a scratch directory outside the repository) reports the same FS0760
(`PdfDocumentReaderTests.fs(27,19)`) and FS0020 (`ScanWindowStoreTests.fs(76,9)`). Left alone, as
someone else's. `CLAUDE-project.md` now says they were verified on `main`.

### 1. The E2E failure flow could not fail on the alert it is named for

`a failure is shown as an alert, cleared by the next success` asserted that the page contained "No
Google client secret has been supplied yet" after `RegisterAccount`, and did not once a secret was
saved. But the information panel shown whenever no secret is stored says that sentence, in the
refusal's exact words, before anything has been pressed, and it goes when a secret is saved. So the
test passed whether or not the error `MudAlert` ever rendered, and the "failure showing an alert"
flow that requirements.md's *Testing* section asks of the E2E level was not proven. Its precondition,
"WHEN no client secret has been supplied THE SYSTEM SHALL say so and disable account registration",
had no test of the *disabled* half at any level.

Measured before changing anything: with the error alert suppressed (the component reading
`ErrorAval` as always `None`) and `secret.IsNone` dropped from the button's `Disabled`, the old test
still passed.

Fixed in the test. It now asserts on the error-severity alert element (`.mud-alert-filled-error`):
none before, exactly one carrying the sentence after the refusal, none after the next success. It
also asserts that "Add account" is disabled without a secret and enabled once one is saved. Under the
same mutations it fails: on the disabled assertion (`Assert.True() Failure`), and, with the button
left alone, on the alert (`Assert.Single() Failure: The collection was empty`).

### 2. The remove confirmation's wording was asserted nowhere

requirements.md: "WHEN a user removes an account THE SYSTEM SHALL say that access is still granted at
Google and can be revoked there", and "SHALL ask for confirmation, stating that access remains granted
at Google" (Q3.6). design.md's E2E list: "remove → the row goes and the confirmation states access
remains granted at Google". The sentence lived in a private function in `GoogleAccountsPage`. The E2E
flow skipped the dialog, with a comment deferring the wording to "a component test" that did not
exist. Nothing would have noticed the sentence dropped, or a removal that no longer asked first.

Fixed. `GoogleAccountsComponents.removeConfirmationMessage` (pure) carries the sentence.
`GoogleAccountsComponents.confirmAndRemove dialogService removeAccount account` is the message box,
moved from the page with its behaviour unchanged. It takes the removal as a callback, so it reaches no
API; the page passes `googleAccountsBrowserModule.RemoveAccount`. Tests, red first:

- `removeConfirmationMessage names the account and says access remains granted at Google` and
  `removeConfirmationMessage names the account by its email, not its opaque id` (both Unit). Both
  failed against a stub returning `""` (`Strings differ … Actual: ""`; `Sub-string not found`), then
  passed.
- E2E `removing an account asks first, saying access remains granted at Google, and removes on a
  yes`. It uses the row's own "Remove" button and the real message box inside a `MudDialogProvider`.
  It asserts the sentence on the dialog, the store still holding the account while the dialog is
  open, and the row and the stored account gone after "Remove".
- E2E `cancelling the remove confirmation keeps the account`. With the confirmation mutated to remove
  whatever the answer, it fails (`Sub-string not found`).

### 3. "Show that it is in progress" was never observed

requirements.md: "WHEN a user presses "Add account" THE SYSTEM SHALL start the consent flow and show
that it is in progress", and "WHEN authorisation is running THE SYSTEM SHALL NOT block the user
interface". Every test ran its work on the calling thread, so `IsRegisteringAval` was only ever read
after the consent flow had finished, and only ever asserted `false`. With the
`isRegisteringCval.Value <- true` line removed from `registerAccount`, every existing Google UI test
(component, module creator, E2E) stayed green.

Test-only: the behaviour was already right. Work is queued, the way `Async.Start` leaves it:

- `RegisterAccount shows it is in progress while the consent flow runs, without blocking the caller`
  (Unit). `RegisterAccount` returns with the API not yet called and `IsRegisteringAval = true`. Once
  the queue drains, the API has been called once, the flag is `false` and the account is listed.
- E2E `adding an account shows it is in progress until the consent flow finishes`. While the flow is
  queued, the button reads "Adding account..." and is disabled. Once it runs, the row appears and the
  button reads "Add account" again.

Both fail with the line removed (`Assert.True() Failure`; `Assert.Single() Failure: The collection
was empty`), and pass with it.

### Considered and deliberately not changed

- **A default calendar deleted at Google after it was chosen still reads "Ready".** requirements.md's
  "WHEN a chosen default calendar no longer exists at Google THE SYSTEM SHALL report that specifically
  rather than failing at the next use" is met the way design decision 6 agreed:
  `SetDefaultInvoiceCalendarWorkflow` checks the calendar exists before storing it
  (`CalendarNoLongerExists`). A calendar deleted after it was stored is next met by change #7's sync,
  which is where "the next use" is. Flagging it on this page (the loaded list lacks the stored id)
  would be a new readiness rule, a design decision rather than a review fix. Recorded here for change
  #7.
- **A failed re-fetch keeps the last list** — see *Round 8's `noCalendarsMessage`, re-checked* above.
- Rounds 1–8's deferrals stand, for the reasons they give.

### Round 9 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. The same 3 pre-existing warnings, all present on `main` (`PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020). |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | 1512 → **1518** (+6), 0 skipped, 9 s. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |

Per level, each measured with `--filter "Level=..."`: Unit **805** (+3, carrying the
one pre-existing failure, which is tagged `Unit`), Integration **324**, Contract **349**,
E2E **40** (+3; the failure flow also gained assertions). 1518 in total.

---

## PR review, round 10 — two defects found in the diff, both fixed

A tenth cold read of the whole PR diff, rounds 1–9 included. PR #21 still carries **no review
comments** (0 review comments; 9 issue comments, which are rounds 1–9's own summaries), so every
finding comes from reading the diff and from running the suite. Round 9's change was checked first.

### Round 9's change, re-checked

- **`confirmAndRemove` keeps the UI rule.** It takes `IDialogService` and the removal as a callback,
  reaches no API, and the page passes the module's `RemoveAccount`. The message box offers "Remove"
  and "Cancel" and no "No", so `ShowMessageBox` returns `true` or `null`. `Cancel` and a dismissed
  dialog both give `null`, and `HasValue && Value` removes nothing. The code after the answer
  resumes on the renderer's context, so `RemoveAccount`'s `transact` runs there, and `startWork`
  (`Async.Start`) takes the store call off it. Behaviour is identical to the page code it replaced.
- **The unit test `RegisterAccount shows it is in progress…` is deterministic.** Its queue is used
  on one thread. It fails without the flag and is tagged `Unit`. The E2E `adding an account shows
  it is in progress…` and the rewritten failure-alert flow run their work on the test's own thread
  (`RegisterAccount`/`SetClientSecret` are called directly, not from a click), and both are tagged.
  One of the two confirmation flows is not deterministic: see 1.
- **The deferral of "a default calendar deleted at Google after it was chosen" is consistent with
  the specs.** design.md's decision 6 and sequence diagram, and tasks.md's Phase 1 test list
  ("`CalendarNoLongerExists` when the calendar is absent"), map requirements.md's "a chosen default
  calendar no longer exists at Google" to the check `SetDefaultInvoiceCalendarWorkflow` makes while
  the calendar is being chosen. No EARS statement asks this page to re-check a stored default.

### 1. A round 9 E2E flow failed under the full suite

The first full-suite run of this round failed `removing an account asks first, saying access
remains granted at Google, and removes on a yes`. It failed at the wait after the dialog's "Remove"
click, with `WaitForFailedException … Check count: 0`: the assertion had not been evaluated even
once in the second allowed. It passed in the E2E-only run. Across nine full-suite runs of round 9's
head it failed once. The other failures in those runs were the known flakes: the LiteDB `BsonMapper`
race (`ThunderbirdDependencyContractTests` real adapter; `ThunderbirdStoreTests.loadProfileRoot
returns None for a fresh database`) and the `icacls` race (`ThunderbirdFolderScannerTests.scan
records an unreadable directory and continues the walk`).

**Why.** bUnit 1.40.0 runs every `WaitForAssertion` check through the renderer's dispatcher
(`renderedFragment.InvokeAsync`), with a one-second default timeout. MudBlazor 8.13.0's
`DialogReference` completes a dialog's result with a plain `TaskCompletionSource`, with no
`RunContinuationsAsynchronously`. `MudDialogProvider.DismissInstance` calls `Dismiss(result)`
before it removes the dialog. So the code awaiting `ShowMessageBox` resumes inline, inside the
click, and with `fun work -> work ()` the whole removal ran there: the store, the token deletion,
the reload and every re-render. bUnit's synchronous `Click()` discards its task. Blazor's dispatcher
runs dispatched work inline on the calling thread when it is idle, so usually the removal had
finished before the wait began. But whenever the dispatcher was busy as "Remove" was clicked, the
click was queued and `Click()` returned at once. The check then queued behind the whole removal,
and the removal took more than a second under the full suite's load. Production never runs work
there: its `startWork` is `Async.Start`.

**Reproduced before changing anything**, with two temporary probes, both since removed. Making the
harness's `RemoveAccount` take 1.5 s alone did not fail the flow, because the click ran inline. With
the dispatcher also held from another thread as "Remove" was clicked, the flow failed exactly as the
gate did: `Check count: 0`, at the same wait.

**Fixed in the test.** Both confirmation flows now hand work off the way production does:
`handOffWork` puts it in a `BlockingCollection`, and the test runs it on its own thread,
waiting up to ten seconds for the answer to hand it off. Under the same two probes all ten Google
flows pass. The change adds two assertions:

- the "Remove" flow asserts that nothing was handed off while the dialog was asking;
- the "Cancel" flow asserts that nothing was handed off once the dialog closed. That check is
  deterministic because the answer is acted on before the dialog is removed.

Checked against temporary mutations of `confirmAndRemove`, both reverted:

- **Never remove:** the "Remove" flow fails 3 runs in 3 (`No work was handed off within ten
  seconds.`).
- **Always remove:** the "Cancel" flow fails 5 runs in 5 (`Assert.Equal() Failure … Expected: 0,
  Actual: 1`).

### 2. `ReauthoriseAccount`'s contract level was dropped without being recorded

CLAUDE.md: "a dependency function type is a published interface", owing one shared suite over its
real adapter and every fake; "if a level genuinely cannot be exercised, say which one and why".
`ReauthoriseAccount`, added by deviation 2, has no contract suite, and deviation 3 recorded the gap
for `AuthoriseAccount` alone. Fixed as a record: deviation 3 now names it, says what does cover its
adapter half, and **defers the suite itself** to the change that sets `NeedsReauthorisation`. Until
then the path is unreachable, and that change will decide its failure handling (round 4's and
round 7's notes). Documentation only; no behaviour changed.

### Considered and deliberately not changed

- **`confirmAndRemove` discards its `Task`**, so an exception from the message box would go
  unobserved. No input raises one on this page: `MudDialogProvider` is in `Frame.razor`, and
  `RemoveAccount` only transacts and hands work off. Returning the `Task` for Blazor to await would
  change the component's parameter type for no failing input.
- **The in-progress flows' `Queue` is not thread-safe.** Only the test thread touches it, because
  `RegisterAccount` is called directly rather than from a click.
- Rounds 1–9's deferrals stand, for the reasons they give.

### Round 10 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, 0 new warnings. Production code is unchanged this round. The test project's recompile reported only the two test-file warnings present on `main` (`PdfDocumentReaderTests.fs` FS0760, `ScanWindowStoreTests.fs` FS0020), and `PdfProcessing\Program.fs` FS0025 is untouched. |
| `dotnet test` (`--blame-hang --blame-hang-timeout 2m`) | **1518**, unchanged: no test added or removed, two rewritten. 0 skipped, 9 s. No hang, no dump. |
| Reproducible failures | One: the same pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. |
| Repeat runs after the fix | 8 further full-suite runs: both confirmation flows passed in every one. Apart from the pre-existing failure, run 4 hung in `MailAccountsFlowTests.a walk hitting an unreadable directory lists it and the other accounts still appear`, whose `icacls /reset` never exited while `WaitForExit()` waits with no timeout (stacks captured with `dotnet-stack`; the file is unchanged from `main`). The stuck `icacls` was stopped by hand so the run could finish. Run 5 hit the known `icacls` race in `ThunderbirdFolderScannerTests`. Neither is from this PR. |

Per level, each measured with `--filter "Level=..."`: Unit **805** (carrying the one pre-existing
failure, which is tagged `Unit`), Integration **324**, Contract **349**, E2E **40**. 1518 in total.

---

## PR review, series 2 round 1 — two defects found in the diff, both fixed

**Reviewer comments: 0.** The PR has 0 review comments. Its 10 issue comments are series 1's round
summaries. Both findings below are this round's own, from reading the diff.

### 1. Calendars fetched for several accounts at once could be lost

- **The defect.** `loadAccounts` starts one `GetCalendarsFor` per account, and production's
  `startWork` runs each one on a pool thread. Each success read `CalendarsByAccountIdAval`'s map and
  wrote back a copy with its account added, and nothing serialised that read-then-write. Two
  finishing together drop one of them. That account's picker is then empty with no caption, since
  it has no entry and `noCalendarsMessage` says nothing, and with no alert, since nothing failed.
  `RemoveAccount`'s `Map.remove` does the same read-then-write.
- **Measured before the fix**, through the real module creator with production's `startWork` and a
  fake API:
  - 8 accounts whose fetches return together: calendars were lost in 99 trials of 100, with as few
    as 3 of 8 kept.
  - 2 accounts: one was lost in 187/200 trials at 0 µs apart, 27/200 at 10 µs, and 0/200 at 50 µs
    and beyond. The window is tens of microseconds. That is narrow for real network completions,
    but it is reachable, and the loss is silent when it happens.
  - A synchronous `startWork` control lost nothing.
- **Fix.** Every change to the map goes through one `changeCalendars` helper, which holds a lock
  around the `transact`. Mutation still goes through `cval` + `transact`, and the lock only makes
  each read-then-write one step. After the fix the same probes lost nothing in any scenario.
- **Test, written red first:** `calendars fetched for several accounts at once all reach the picker
  map` (Unit). It runs 20 trials of 8 accounts, each fetch on its own thread, all released together
  by a barrier.
  - Red: `Trial 1: calendars lost for accounts ["1"; "2"; "3"; "5"; "6"; "7"; "8"].`
  - Green in 10 repeated runs of its class.
  - The `RemoveAccount` path uses the same helper but has no red test of its own.

### 2. The calendar picker offered calendars the account can only read

- **The defect.** `GoogleCalendarClient` requested the calendar list without `minAccessRole`, so
  Google returned every calendar the account can see, including read-only ones: "Holidays in ...",
  "Birthdays", and anything subscribed to. A read-only calendar chosen as the default passes
  `SetDefaultInvoiceCalendarWorkflow`'s existence check, and the account shows **Ready** with a
  calendar that can never take an invoice event. Change #7's first insert would find that out,
  which is the mid-sync failure design decision 6 checks for at choosing time.
- **Fix.** Every page's request asks for `minAccessRole=writer`, which returns writer and owner
  calendars. `ListCalendars`' doc comment now states that promise.
- **Tests, written red first.** Both use a stub that answers the way Google does: read-only entries
  are left out only when the request asks for writer access.
  - `listCalendars offers only calendars the account can add events to, on every page`
    (Integration, over two pages). Red: 4 ids came back, including `holidays@...` and
    `addressbook#contacts@...`.
  - `the real adapter lists only calendars the account can add events to` (Contract). Red:
    `Assert.Single() Failure: The collection contained 2 items`.
- **Not checked against a real Google account.** Task 10.4's manual coverage is where that belongs.
  `writer` is the Calendar API's documented `minAccessRole` value, and the pinned
  `Google.Apis.Calendar.v3` 1.69 exposes it as `MinAccessRoleEnum.Writer`.
- **Not changed:** the no-calendars caption. Every Google account owns its primary calendar, so in
  practice the writable list is never empty.

### Considered and deliberately not changed

- `removeStoredToken` logs a store failure twice: once in `GoogleCredentialStore.getAll`'s own
  `handleError`, reached through `GoogleCredentialDataStore`, and again in its own. This is noise in
  the diagnostics log only, and the user sees nothing different.
- Series 1's deferrals stand, for the reasons they give.

### Series 2 round 1 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. Only pre-existing warnings. `PdfProcessing\Program.fs` FS0025 was re-emitted. The test project's two warnings from `main` (FS0760, FS0020) were not re-emitted, because that project was already up to date |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | **1521** (+3), 1520 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **806** (including the one pre-existing
failure, which is tagged `Unit`), Integration **325**, Contract **350**, E2E **40**. 1521 in total.

---

## PR review, series 2 round 2 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 11 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `969e6ec` before anything changed.

### Round 1's two fixes, re-checked

- **The lock around the calendars map stands.** `calendarsGate` is only taken on a work thread,
  always before `transact` takes the adaptive objects' own locks, and nothing that holds an adaptive
  lock ever takes it. So it adds no lock cycle. Every read-then-write of the map goes through it,
  and the other cells are written whole, never read and rewritten.
- **`minAccessRole=writer` stands.** It narrows only which calendars are offered, and
  `SetDefaultInvoiceCalendarWorkflow`'s existence check reads the same list, so a calendar can only
  be chosen if the picker could have offered it.

### Saving the client secret did not fetch the calendars again

- **The defect.** Every calendar fetch parses the stored client secret before it calls Google. So
  the secret decides what every picker can show, but `SetClientSecret` re-read only the secret
  itself. Two user-visible results:
  - **Correcting a malformed secret left every picker empty with nothing saying why.** The page
    loads, each fetch fails, and the alert reads "The stored Google client secret is malformed."
    Saving a good secret clears that alert, but nothing fetches the calendars again. Each picker
    stays empty with no alert and no no-calendars caption, until the user leaves the page and comes
    back. That is the unexplained empty picker round 8 fixed, reached a different way.
  - **A bad paste over a working secret said nothing.** Pickers kept the lists fetched with the old
    secret and no alert appeared. The failure only showed at the next page load.
- **Measured before the fix**, through the real module creator with a fake API that fails calendar
  fetches while the stored secret is malformed:

  | Scenario | After page load | After `SetClientSecret` |
  | --- | --- | --- |
  | malformed → corrected | no calendars, alert shown, 1 fetch | **no calendars, no alert, still 1 fetch** |
  | working → malformed | calendars shown, no alert, 1 fetch | **old calendars, no alert, still 1 fetch** |

- **Fix.** A successful save re-reads the secret, then reloads the accounts, which fetches each
  account's calendars. This is the page's "a write reloads" rule, applied to what the write
  affects. Both reloads run inside the save's own work item. Starting them as two separate work
  items would race: re-reading the secret succeeds and clears the alert, and it could finish after
  a failing fetch had set the alert. So `loadClientSecret` is now `startWork` over a synchronous
  `reloadClientSecret`, and the save calls `reloadClientSecret ()`, then `loadAccounts ()`.
- **Cost.** One `GetCalendarsFor` per account each time a secret is saved, the same as every other
  write on this page. A secret is rarely saved, and with no accounts yet it is one local read.
- **Tests, written red first:**
  - `correcting a malformed client secret loads the calendars it could not` (Unit). Red:
    `Expected: Some([{ Id = "cal-1" ... }]) Actual: null`.
  - `replacing the client secret with one that does not work says so straight away` (Unit). It runs
    work newest first, which is the order a pool thread can finish it in. Red: `Expected: 2 Actual: 1`
    fetches.
  - `correcting a malformed client secret fills the calendar picker it had left empty` (E2E, through
    the real store and workflows). Red: the wait timed out with the account's calendars `null` after
    the alert had cleared.
- **The ordering test was checked against the racy fix.** Starting the two reloads as separate work
  items makes both other tests pass. The newest-first test then fails at the alert: `Expected:
  Some(The stored Google client secret is malformed.) Actual: null`. That mutation was reverted and
  never committed.

### Considered and deliberately not changed

- Series 1's deferrals and series 2 round 1's stand, for the reasons they give.

### Series 2 round 2 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors, and no warnings re-emitted: the test project was already up to date from the red-first builds. That earlier build reported only `main`'s two test-file warnings (`PdfDocumentReaderTests.fs` FS0760, `ScanWindowStoreTests.fs` FS0020), and `PdfProcessing\Program.fs` FS0025 is untouched |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | **1524** (+3), 1523 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **808** (including the one pre-existing
failure, which is tagged `Unit`), Integration **325**, Contract **350**, E2E **41**. 1524 in total.

---

## PR review, series 2 round 3 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 12 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `648c04c` before anything changed.

### Rounds 1 and 2, re-checked

- **The lock around the calendars map stands**, for the reasons series 2 round 2 gives.
- **`minAccessRole=writer` stands.** The fakes in `ListCalendarsDependencyContractTests` cannot
  express an access role, since `AvailableCalendar` has none. So the writer-only case is correctly a
  real-adapter-only contract test.
- **Round 2's save path stands.** It fixed the race it describes, but only for the save. Opening
  the page still started the same two work items separately, which is the defect below.

### Opening the page could wipe the alert a failing calendar fetch had just set

- **The defect.** The module creator started two separate work items: `loadClientSecret` for the
  secret, and `loadAccounts` for the accounts, which then starts one calendar fetch per account.
  Reading the secret succeeds and clears the alert. With a malformed stored secret, every fetch
  fails fast, before any network call. If the read finished last, it wiped the fetch's "The stored
  Google client secret is malformed." Every picker was then empty, with no alert and no
  no-calendars caption. That is the empty picker round 2 closed for the save path, reached from a
  page load instead. A missing token takes the same fast path ("No stored credential for this
  account."), so it is exposed the same way.
- **Measured before the fix.** A scratch script ran the real `GoogleAccountApiFactory` over a temp
  `Google.db` holding a malformed secret and one account, with production's `startWork`
  (`Async.Start`). Each trial opened the page and waited for all work to finish:

  | Connection | Trials | Alert lost, before | Alert lost, after |
  | --- | --- | --- | --- |
  | `shared` (production's) | 4512 (4000 + 500 warm, 12 in cold processes) | **1** | **0** of 4000 |
  | `direct` | 1000 | **4** | **0** |

  It is rare in production because `shared` mode serialises LiteDB operations on the file's mutex,
  and the read is queued first. It is not impossible, and when it happens it is silent.
- **Fix.** The page reads the secret, then loads the accounts, in one work item. This is the
  ordering the save path already uses. `loadClientSecret` is gone, and `reloadClientSecret`'s doc
  comment says why it is never started as work of its own. The table's spinner is still set where
  the page is built, before any work runs.
- **Cost.** The secret read and the accounts read now run one after the other instead of side by
  side: one local LiteDB read, and under `shared` mode they were already serialised.
- **Tests:**
  - `opening the page over a malformed client secret keeps the alert its calendar fetch set`
    (Unit), written red first. Work finishes newest first, the ordering round 2's save-path test
    uses. Red: `Expected: Some(The stored Google client secret is malformed.) Actual: null`.
  - `opening the page shows the accounts table loading before any work has run` (Unit). This is a
    guard for the fix, not a red-first test: it passed before and must keep passing. It was checked
    by deleting the fix's spinner line.
  - No new E2E test. The E2E harness runs work where it is started, or in the order it was handed
    off, so it cannot produce the ordering that loses the alert. The page-load and correction flows
    already there still pass unchanged.

### Considered and deliberately not changed

- Series 1's deferrals and series 2 rounds 1 and 2's stand, for the reasons they give. That
  includes round 3's "a `loadCalendarsFor` success clears another account's error". This round's
  finding is different: the alert was cleared by reading the stored client secret, which cannot
  report anything wrong with the calendars, not by another account's successful fetch.

### Series 2 round 3 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. 3 warnings, all pre-existing and all present on `main`: `PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020 |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1524 → **1526** (+2), 1525 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **810** (including the one pre-existing
failure, which is tagged `Unit`), Integration **325**, Contract **350**, E2E **41**. 1526 in total.

---

## PR review, series 2 round 4 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 13 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `4856f9b` before anything changed.

### Rounds 1–3, re-checked

- **The lock around the calendars map stands.** Both writers of the map (`loadCalendarsFor`'s
  success and `removeAccount`) go through `changeCalendars`, and nothing inside the lock takes
  another one.
- **`minAccessRole=writer` stands.** Google's `writer` is a minimum: owned calendars come back too,
  which the contract test's owned entry covers.
- **The save path and the page load stand.** Each reads the secret and then loads the accounts in
  one work item, and the spinner is still set before any work runs.

### Saving the client secret field blank stored it as a supplied secret

- **The defect.** `SetClientSecret` stored whatever it was given, and a stored secret is what both
  the page and `RegisterGoogleAccountWorkflow` read as "a secret has been supplied". Pressing
  **Add client secret** then **Save** without pasting stores `""`. The page then drops "No Google
  client secret has been supplied yet.", shows an empty read-only field, and enables **Add
  account**, and registering fails at the authorisation call as "The stored Google client secret is
  malformed.". requirements.md: "WHEN no client secret has been supplied THE SYSTEM SHALL say so
  and disable account registration, rather than failing at the authorisation call." The same
  save over a working secret replaced it with nothing.
- **Measured** with a scratch script over the real `GoogleAccountApiFactory`, a temp `Google.db`,
  and the real module creator and component rendered through bUnit:

  | Input | `SetClientSecret` | Stored afterwards | Page says none supplied | **Add account** | `RegisterAccount` |
  | --- | --- | --- | --- | --- | --- |
  | `""`, `"   "`, `"\r\n"`, before | `Ok` | `Some <blank>` | no | enabled | "The stored Google client secret is malformed." |
  | the same three, after | `Error` "Google client secret must not be empty." | `None` | yes | disabled | "No Google client secret has been supplied yet.", before any browser opens |

  Nothing was logged in any of the six runs.
- **Fix.** A new domain workflow, `SetClientSecretWorkflow.setClientSecret`. It refuses a null or
  blank secret with `ClientSecretInvalid "Google client secret must not be empty."` and never
  reaches the store. Anything else it stores verbatim. The factory's `SetClientSecret` goes through
  it, and so does the E2E harness, which mirrors the factory. `ClientSecretInvalid` was already an
  expected, unlogged case, so no mapper changed.
- **Decisions the finding did not ask for:**
  - Refuse rather than store the blank as "no secret". Refusing keeps a working secret from being
    wiped by an accidental save, and the edit field stays open for a real paste.
  - The rule is a workflow, not an inline check in the factory. It is a domain rule, and the
    factory holds no business logic (deviation 2's reasoning for `ReauthoriseGoogleAccountWorkflow`).
  - `SaveClientSecret` still takes a `string`, with no constrained type. A constrained type would
    change a published dependency type and its contract suite for no behaviour this finding needs.
- **Not covered:** a blank secret already stored by an earlier build of this branch still reads as
  supplied. Only this unmerged branch could have written one, and **Edit** replaces it.
- **Tests**, the first eight written red first against a stub holding today's behaviour:
  - Unit, `setClientSecret refuses a blank secret and never reaches the store` (`""`, `"   "`,
    `"\r\n\t"`) and `... refuses a null secret ...`. Red: each returned `Ok ()` where
    `Error (ClientSecretInvalid "Google client secret must not be empty.")` was expected.
  - Integration, `SetClientSecret refuses a blank secret without writing anything, as an unlogged
    exception` (`GoogleAccountApiFactoryTests`). It asserts the message, the `ActionName`, the
    `ApplicationException` inner, and that the `ClientSecret` collection is empty. Red:
    `SetClientSecret expected Error, but got Ok`.
  - Contract, `SetClientSecret refuses a blank secret as an unlogged exception, keeping what was
    stored`, run against the real API and the fake. Red on both: `SetClientSecret expected Error,
    but got Ok`. The fake now refuses a blank secret too.
  - E2E, `saving the client secret field blank says so, and registration stays disabled`. It
    presses the page's own **Add client secret** and **Save** buttons, with work handed off to the
    test thread. Red: `Assert.Single() Failure: The collection was empty`, meaning no alert.
  - Two guard unit tests, not red first because the stub already passed them:
    `setClientSecret saves the pasted secret exactly as given` (surrounding whitespace kept) and
    `... reports a store failure as the store gave it`.
- `design.md`'s workflow table gains the row.

### Considered and deliberately not changed

- **The calendar picker shows a chosen calendar's name, not its id.** This was checked because
  MudSelect renders the raw value when no item matches. With the calendars loaded, the field reads
  "Invoices". Before they load, or when their fetch failed, it is blank rather than showing an id.
- Series 1's deferrals and series 2 rounds 1–3's stand, for the reasons they give.

### Series 2 round 4 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. 3 warnings, all pre-existing and all present on `main`: `PdfProcessing\Program.fs` FS0025, `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020 |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1526 → **1536** (+10), 1535 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **816** (+6, including the one pre-existing
failure, which is tagged `Unit`), Integration **326** (+1), Contract **352** (+2), E2E **42** (+1).
1536 in total. The new E2E flow, which clicks through the renderer's dispatcher, passed in 5 further
runs of its class.

---

## PR review, series 2 round 5 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 14 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `d3e6d49` before anything changed.

### Rounds 1–4, re-checked

- **The lock around the calendars map stands.** Both writers of the map (`loadCalendarsFor`'s
  success and `removeAccount`) go through `changeCalendars`, and nothing inside the lock takes
  another one.
- **`minAccessRole=writer` stands.** It is set on every page's request, and this round's change
  touches only what a listed calendar is called, not which calendars are listed.
- **The save path and the page load stand.** Each reads the secret and then loads the accounts in
  one work item, and the spinner is still set before any work runs.
- **`SetClientSecretWorkflow` stands.** It refuses a null or blank secret without reaching the
  store and stores anything else verbatim. The factory's `SetClientSecret` and the E2E harness both
  go through it, and `ClientSecretInvalid` stays an expected, unlogged case.

### The calendar picker named calendars by their owner's title, not the account's own name for them

- **The defect.** `GoogleCalendarClient` named each calendar from `summary`, the title its owner
  gave it. Google's calendar list also carries `summaryOverride`, "the summary that the
  authenticated user has set for this calendar", and that is the name Google Calendar shows the
  user. `CalendarName`'s own doc comment promises "a calendar's display name, as Google shows it".
  Writable calendars shared by other people are exactly the ones `minAccessRole=writer` keeps, and
  their titles are chosen by other people. Two people's shared calendars, both titled "Invoices" and
  renamed by this account to tell them apart, reached the picker as two identical "Invoices". A
  user could choose the wrong one, and nothing would say so.
- **Measured before the fix**, with a scratch script driving the real `listCalendarsVia` over a
  stubbed handler that returned Google's own entry shape:

  | Entry (`summary` / `summaryOverride`) | Name in the picker, before | After |
  | --- | --- | --- |
  | `Invoices` / `Invoices - Alice` | `Invoices` | `Invoices - Alice` |
  | `Invoices` / `Invoices - Bob` | `Invoices` | `Invoices - Bob` |
  | `person@example.com` / *(none)* | `person@example.com` | `person@example.com` |

- **Fix.** `toAvailableCalendar` takes `summaryOverride` when the account has set one, and `summary`
  otherwise. Only the name changes: ids, `IsPrimary`, paging and `minAccessRole` are untouched.
- **Tests:**
  - `listCalendars names each calendar the way the account's own calendar list does`
    (Integration, `GoogleCalendarClientTests`), written red first. It asserts every field of all
    three entries. Red: `Expected: [("alice@...", "Invoices - Alice", False); ("bob@...", "Invoices
    - Bob", False); ("primary", "person@example.com", True)]`, `Actual: [("alice@...", "Invoices",
    False); ("bob@...", "Invoices", False); ...]`.
  - `the real adapter names a calendar the way the account's own list does` (Contract, real adapter
    only, like the writer-access test: `AvailableCalendar` has no override for a fake to express).
    Written after the fix, so it was checked against the original adapter restored from git: red,
    `Expected: "Invoices - Alice"`, `Actual: "Invoices"`. That restore was reverted and never
    committed.
  - No Unit test: `toAvailableCalendar` is private, and the stubbed-handler tests above are how this
    adapter is exercised everywhere else. No E2E test: the E2E harness substitutes `ListCalendars`
    with a fake, so it never reaches the adapter (the same reason rounds 5 and 6 of series 1 give).
- **Not checked against a real Google account.** Task 10.4's manual coverage is where that belongs.

### Considered and deliberately not changed

- **A successful accounts load wipes the alert a failed secret read set.** The page reads the secret
  and then loads the accounts, and the accounts load's success clears the alert. If the read failed
  and the load then succeeded, the page would say no secret has been supplied. Both read the same
  `Google.db`, so one failing while the other succeeds takes a damaged `ClientSecret` document that
  nothing in the app can write. This is the class series 1 round 3 deferred ("a success clears
  another operation's error"), and the reverse of the ordering series 2 round 3 fixed.
- **Each calendar fetch builds a `GoogleAuthorizationCodeFlow` and never disposes it.** Its
  `HttpClient` is only used to refresh the access token, which happens at most about once an hour
  per account, because a refreshed token is stored for the next fetch. The cost is negligible.
- **Calendars the user has hidden in Google Calendar are not offered** (`showHidden` defaults to
  false). Hiding is the user's own choice in Google's list. A stored default that is hidden later
  joins series 1 round 9's "a calendar deleted after it was stored" deferral.
- Series 1's deferrals and series 2 rounds 1–4's stand, for the reasons they give.

### Series 2 round 5 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. 2 warnings re-emitted, both pre-existing and present on `main`: `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760, `Tests\Database\ScanWindowStoreTests.fs` FS0020. `PdfProcessing\Program.fs` FS0025 is untouched and was not rebuilt |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1536 → **1538** (+2), 1536 passed, 0 skipped, no hang |
| Failures | Two, neither in code this round touched. The pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too. And the known LiteDB flake: `ThunderbirdStoreTests.loadProfileRoot returns None for a fresh database` threw `Collection was modified` from `BsonMapper.SerializeObject` at `ThunderbirdDatabaseContextModule.fs:16`'s warm-up, the global `BsonMapper` race *Per-integration databases* documents. It was not re-run; the Integration-level count taken straight afterwards passed it (327 of 327) |

Per level, each measured with `--filter "Level=..."`: Unit **816** (unchanged, including the one
pre-existing failure, which is tagged `Unit`), Integration **327** (+1), Contract
**353** (+1), E2E **42** (unchanged). 1538 in total.

---

## PR review, series 2 round 6 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 15 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `4c09970` before anything changed.

### Rounds 1–5, re-checked

- **The lock around the calendars map stands.** Both writers of the map (`loadCalendarsFor`'s
  success and `removeAccount`) still go through `changeCalendars`, and this round's change keeps that.
- **`minAccessRole=writer` stands.** It is still set on every page's request.
- **The save path and the page load stand.** Each reads the secret and then loads the accounts in
  one work item, and the spinner is still set before any work runs.
- **`SetClientSecretWorkflow` stands.** It refuses a null or blank secret without reaching the store,
  and the factory's `SetClientSecret` goes through it.
- **`summaryOverride` stands.** `toAvailableCalendar` takes it when set and `summary` otherwise, and
  an entry with no usable name either way is still dropped, as before.

### A failed calendar fetch did not say which account it was for

- **The defect.** Opening the page fetches every account's calendars, one fetch per account, with
  nothing the user did to tie a failure to an account. The alert showed the failure's message
  alone. For `NotAuthorised` that message was `The account '<id>' needs to be re-authorised.`, and
  the id is the store's ObjectId. The page names accounts only by email and shows that id nowhere.
  So a user with two accounts, one of whose tokens had expired, could not tell which account to act
  on. A Testing-mode OAuth client's refresh tokens last seven days, so this is the common failure.
  Nothing sets `NeedsReauthorisation` yet, so there is no **Re-authorise** button, and the only
  remedy is to remove the account and add it again. That makes knowing which account essential.
  `Google is rate-limiting this account; try again shortly.` had the same gap.
- **Series 1 round 6 considered this and did not change it**, calling it "a payload choice shared
  with `AccountNotRegistered` and `NoDefaultCalendar`, not something this round introduced". This
  PR did introduce it. `NotAuthorised` is the one of the three that the page reaches, on every
  page load.
- **Measured before the fix**, with a scratch script over the real `GoogleAccountApiFactory`, a temp
  `Google.db` holding a client secret and two accounts with no stored token, and the real module
  creator:

  | | Before | After |
  | --- | --- | --- |
  | The alert | `The account '66e2f0c1a2b3c4d5e6f70802' needs to be re-authorised.` | `Could not load the calendars for bob@example.com: This Google account needs to be re-authorised.` |
  | Names `bob@example.com`, whose fetch failed | no | yes |
  | Names the id, which the page never shows | yes | no |
  | Names `alice@example.com` | no | no |
  | Logged | 0 | 0 |

- **Fix.** Two parts:
  - The module creator's `loadCalendarsFor` takes the account rather than its id. A failure now
    reads `Could not load the calendars for <email>: <message>`, naming the account the way the
    table does.
  - `NotAuthorised`'s sentence no longer names the store's id: `This Google account needs to be
    re-authorised.` The page now says which account it is.
- **Decisions the finding did not ask for:**
  - The account is named in the UI, not in the domain error. `NotAuthorised` carries a
    `GoogleAccountId`, and `ListCalendars` is given only that. Putting the email in the payload would
    change a published dependency type and its contract suite. The module creator already holds the
    account it is fetching for.
  - The prefix is on every calendar-fetch failure, including the ones that are not about that
    account: the malformed secret, the API not being enabled, and Google being unreachable. Each
    still reads truly: that account's calendars could not be loaded, and why. A
    `MyDogsbodyException` does not tell the UI which failures are global.
  - `SetDefaultInvoiceCalendar`'s failures are not prefixed. The user chose a calendar in that
    account's row, so the page already shows which account it is.
- **Tests, the first three written red first:**
  - Unit, `a failed calendar fetch names the account it failed for`, over two accounts where the
    second's fetch fails. Red: `Expected: Some(Could not load the calendars for 2@example.com: This
    Google account needs to be re-authorised.)`, `Actual: Some(This Google account needs to be
    re-authorised.)`.
  - Contract, `NotAuthorised becomes an unlogged exception asking for re-authorisation, without the
    store's id`. It replaces `... naming the id`, and asserts the message, the `ActionName` and the
    unlogged `ApplicationException` inner. Red: `Expected: "This Google account needs to be
    re-author"···`, `Actual: "The account 'acc-1' needs to be re-author"···`.
  - E2E, `a calendar fetch that fails says which account it failed for`, through the real store and
    workflows. It registers two accounts and fails bob's fetch as `NotAuthorised`. Red: `Not found:
    "bob@example.com"` in `"The account '507f1f77bcf86cd799439012' ne"···`.
  - Four existing unit tests assert the alert text exactly, and now expect the account named. They
    are the first calendar-fetch failure test and the three malformed-secret ordering tests. What
    each is about is unchanged. The API contract fake's `GetCalendarsFor` refusal uses the new
    sentence, so it still mirrors the real API.
  - No Integration test: nothing that does I/O changed.

### Considered and deliberately not changed

- **`AccountNotRegistered` still names an id.** It is reached only for an id no registered account
  has, so there is no email to name it by.
- **`NoDefaultCalendar` still names an id.** Nothing in this change produces it. Change #7's sync is
  its first producer, and its sentence belongs to that change.
- **Only the last failure's message survives**, and a later success can clear it. That is series 1
  round 3's "a `loadCalendarsFor` success clears another account's error". This round changes what
  the surviving message says, not which one survives.
- Series 1's deferrals and series 2 rounds 1–5's stand, for the reasons they give.

### Series 2 round 6 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. One warning re-emitted, pre-existing and present on `main`: `PdfProcessing\Program.fs` FS0025. The test project's two, also `main`'s (`PdfDocumentReaderTests.fs` FS0760, `ScanWindowStoreTests.fs` FS0020), came from the red-first build and were not re-emitted, since that project was up to date |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1538 → **1540** (+2), 1539 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |
| The per-level runs | The Contract-level run on its own failed `MailAccountApiContractTests.ScanForAccounts against the committed fixture finds ten accounts, and GetAccounts sees them(implementation: "real api")` after 1 ms. That code is untouched here, and the test passed in the full run. Its `withRealApi` builds a Thunderbird context first thing, and that context's `BsonMapper.Global` warm-up is where the known race fires. `invoice-extraction`'s `outcome.md` records this same test hitting it. The stack trace was not captured, and the test was not re-run |

Per level, each measured with `--filter "Level=..."`: Unit **817** (+1, including the one
pre-existing failure, which is tagged `Unit`), Integration **327** (unchanged), Contract **353**
(unchanged: one test replaced), E2E **43** (+1). 1540 in total.

---

## PR review, series 2 round 7 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 16 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `76f2117` before anything changed.

### Rounds 1–6, re-checked

- **The lock around the calendars map stands.** Both writers of the map (`loadCalendarsFor`'s
  success and `removeAccount`) go through `changeCalendars`. Nothing holding the lock waits on
  anything the render thread holds, and no other path reads and rewrites the map.
- **`minAccessRole=writer` stands.** It is set on every page's request. The contract stub leaves out
  read-only calendars only when the query carries `minAccessRole=writer`.
- **The save path and the page load stand.** Each reads the secret and then loads the accounts in
  one work item, and the spinner is set before any work runs.
- **`SetClientSecretWorkflow` stands.** It refuses a null or blank secret without reaching the store,
  and the factory's `SetClientSecret` and the E2E harness both go through it.
- **`summaryOverride` stands, and the picker shows names.** A bUnit probe of the real module creator
  and component showed a stored default as "Invoices A", its loaded name, not its id.
- **Round 6's prefix and sentence stand.** Every calendar-fetch failure names the account by email,
  `NotAuthorised`'s outbound sentence names no id, and the mapper test, the API contract fake and the
  unit and E2E flows agree on the wording.
- **A refused calendar choice does not stay in the picker.** Checked because MudSelect keeps a value
  of its own. The same probe chose a calendar whose save failed. The picker went back to the stored
  calendar, and stayed there after a later success cleared the alert.
- **The Google tests are stable.** Ten further runs of the 314 Google-filtered tests were all green.

### The `ListCalendars` contract suite did not run the composition root's binding

- **The defect.** CLAUDE.md makes a dependency function type a published interface, whose "real
  adapter and every fake standing in for it in a workflow test run the same suite". design.md's
  arrangement has the real adapter run "the same shared suite" over a stubbed `HttpMessageHandler`.
  `ListCalendarsDependencyContractTests` did neither:
  - Its "real adapter" was `GoogleCalendarClient.listCalendarsVia` bound to a copy of the error
    translation, written in the test file. The copy knew three of Google's answers and never read the
    store. It had no `CalendarApiNotEnabled`, no `ClientSecretInvalid`, and no stored-secret or
    stored-token step. The production binding was a closure inside `createGoogleAccountApi` that no
    test reached past `loadCredential`: the factory tests stop before Google, and the E2E harness
    replaces `ListCalendars` with a fake.
  - Its fake ran one test of its own and none of the "shared behaviour" section.

  So the translation every calendar alert is written from was untested where production wires it.
- **Measured before the fix**, with two temporary mutations of production code, both reverted and
  never committed:

  | Mutation | Tests failing, whole suite |
  | --- | --- |
  | The factory translates Google's answers with the store's translation (`toStoreError`), so every calendar failure would read as a store failure | **none**, apart from the pre-existing `SqliteConnectionPoolingTests` |
  | `toListCalendarsError` turns a rate limit into `NotAuthorised` | one, the mapper's own test. The suite's 429 test and its four usage-limit 403 tests passed, because they checked the copy |

- **Fix.**
  - `GoogleAccountApiFactory.bindListCalendars handleError googleContext listCalendarsWith` is the
    binding, moved out of `createGoogleAccountApi` with the calendar call as its last parameter.
    `createGoogleAccountApi` passes `GoogleCalendarClient.listCalendars`, so production behaviour is
    unchanged. The two secret loaders it shares with the other bindings are now private helpers.
  - The suite's real side is that binding over a temp `Google.db` holding a client secret and the
    account's token, with only the calendar client's HTTP stubbed (`listCalendarsVia`). Every
    existing real-side test runs through it unchanged.
  - Three cases run against the binding and the fake alike: an authorised account's calendars with
    every field; an authorised account with none; and an account with no stored authorisation, which
    is `NotAuthorised` carrying its id with nothing sent to Google. The fake now holds calendars per
    account and answers `NotAuthorised` for any other, as the binding does.
  - The `invalid_grant` case cannot go through the binding, whose credential refreshes against
    Google's real token endpoint. It still composes the adapter with a stubbed token endpoint, but now
    translates with the production `toListCalendarsError` rather than the copy.
- **Decisions the finding did not ask for:**
  - The binding's stored token carries no refresh token. With one, a 401 from the stub would make the
    credential refresh against Google's real token endpoint, and no test may reach the network.
  - The copy is deleted rather than corrected. A corrected copy drifts again the next time the mapper
    gains a case, and change #7 adds four dependency types of the same shape.
  - `CLAUDE-project.md`'s contract guidance now says so: a network dependency's suite runs the
    composition root's own binding with only the network call stubbed.
- **Tests:**
  - Red first, against the old suite: `the real binding reports a project with the Calendar API
    switched off as CalendarApiNotEnabled, carrying Google's sentence`. Red: `Expected
    CalendarApiNotEnabled carrying Google's sentence, got Error (CalendarUnreachable "The Google
    Calendar API is not enabled for this project. Google Calendar API has not been used in project
    000000000000 before or it is disabled. ...")`.
  - Red first, with the suite moved onto the binding before it existed: `error FS0039: The value,
    constructor, namespace or type 'bindListCalendars' is not defined.`
  - Added with the extraction: the three shared cases (six runs), and two binding-only cases,
    `... a malformed stored client secret as ClientSecretInvalid, without reaching Google` and
    `... a missing client secret as ClientSecretMissing, without reaching Google`. The old fake-only
    test is folded into the shared cases. The suite has 19 tests, up from 11.
  - **The mutations again, against the new suite.** The swapped translation fails 7 of its 19 tests:
    the 401, the 429, the four usage-limit 403s and the switched-off API. The mapper mutation fails 5:
    the 429 and the four 403s. Both reverted.
  - No new Unit, Integration or E2E test. Production behaviour is unchanged, and the extraction is
    the only production edit. The existing factory tests still reach the binding through
    `createGoogleAccountApi`.

### Also corrected

- `CLAUDE-project.md`'s *Build state* still gave series 2 round 3's counts. It now gives this round's.
- `tasks.md` 7.1 and design.md's contract arrangement name the binding the suite runs.

### Considered and deliberately not changed

- Series 1's deferrals and series 2 rounds 1–6's stand, for the reasons they give.

### Series 2 round 7 gate

| Check | Result |
| --- | --- |
| `dotnet build MyDogsbody.sln` | 0 errors. 2 warnings re-emitted, both pre-existing and present on `main`: `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760 and `Tests\Database\ScanWindowStoreTests.fs` FS0020. `PdfProcessing\Program.fs` FS0025 is untouched and was not rebuilt |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1540 → **1548** (+8), 1547 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |
| Baseline over `76f2117`, measured independently before anything changed | 1540: Unit 817, Integration 327, Contract 353, E2E 43; 1539 passed, and the same one pre-existing failure. It confirms series 2 round 6's own record, which had not been re-measured |
| Repeat runs | The 314 Google-filtered tests, 10 runs over `76f2117` before the fix: all green. The 19-test `ListCalendarsDependencyContractTests`, 5 runs after it: 5 of 5 green |

Per level, each measured with `--filter "Level=..."`: Unit **817** (including the one
pre-existing failure, which is tagged `Unit`), Integration **327**, Contract
**361** (+8), E2E **43**. 1548 in total.

---

## PR review, series 2 round 8 — one defect found in the diff, fixed

**Reviewer comments: 0.** The PR has 0 review comments, and its 17 issue comments are the earlier
rounds' summaries. The finding below is this round's own, from reading the diff. It was reproduced
against `7ce8db3` before anything changed.

### Rounds 1–7, re-checked

- **The lock around the calendars map stands.** Both writers of the map (`loadCalendarsFor`'s
  success and `removeAccount`) go through `changeCalendars`. The lock is only taken on a work
  thread, before `transact`, and nothing inside it takes another.
- **`minAccessRole=writer` stands.** `listAllPages` sets it inside the paging loop, so every page's
  request carries it.
- **The save path and the page load stand.** Each reads the secret and then loads the accounts in
  one work item, and the spinner is set before any work runs.
- **`SetClientSecretWorkflow` stands.** It refuses a null or blank secret without reaching the
  store, and the factory's `SetClientSecret` and the E2E harness both go through it.
- **`summaryOverride` stands.** `toAvailableCalendar` takes it when it is set, and `summary`
  otherwise.
- **Round 6's prefix and sentence stand.** Every calendar-fetch failure names the account by email,
  and `NotAuthorised`'s outbound sentence names no id.
- **Round 7's binding stands.** `createGoogleAccountApi` passes `GoogleCalendarClient.listCalendars`
  to `bindListCalendars`, so production is unchanged, and the suite's three shared cases run the
  binding and the fake alike. Its one adapter-level case (`invalid_grant`) says why in the file: the
  binding's credential would refresh against Google's real token endpoint.

### No test reached the factory's `SaveGoogleAccount` or `DiscardAuthorisation` binding

- **The defect.** Round 7 found that the `ListCalendars` suite ran a copy of the composition root's
  binding. The other Google dependency suite did the same, and there the copy hid more.
  `GoogleAccountDependencyContractTests` said its real side was "the real bindings
  (GoogleAccountApiFactory over a temp LiteDB)". It never called the factory: it re-composed each
  binding in the test file. The E2E harness re-composed them too. And no other test can reach two of
  the factory's bindings:
  - `SaveGoogleAccount` is used only by registering, re-authorising and choosing a calendar.
  - `DiscardAuthorisation` is used only by registering.

  Each of those goes through Google (the browser, or the calendar list) before the binding runs, and
  the factory tests stop at the refusals that come first.
- **Measured before the fix**, with temporary mutations of the factory, reverted and never
  committed:

  | Mutation of `GoogleAccountApiFactory` | Whole suite, before |
  | --- | --- |
  | `saveGoogleAccount` returns `Ok` without saving: every registration and every default-calendar choice is lost | **0 failing**, apart from the pre-existing `SqliteConnectionPoolingTests` (1547 of 1548 passed) |
  | `discardAuthorisation` returns `Ok` without discarding: every refused registration strands its refresh token, series 1 round 1's defect | **0 failing**, the same 1547 of 1548 |

- **Fix.**
  - The factory's six storage-facing bindings are public functions: `bindLoadClientSecret`,
    `bindSaveClientSecret`, `bindListGoogleAccounts`, `bindSaveGoogleAccount`,
    `bindRemoveGoogleAccount` and `bindDiscardAuthorisation`. Each takes `handleError` and the
    context, and returns its dependency type. `createGoogleAccountApi` binds through them. They are
    the closures it already had, so production behaviour is unchanged.
  - The contract suite's real side is those functions, over a temp `Google.db`. The copies are
    deleted, and the file's header says what it runs.
- **Widening, noted.** The E2E harness's six storage dependencies are the same functions, so every
  E2E flow that registers an account or chooses a calendar runs production's `SaveGoogleAccount`.
  The API record around them is still composed in the harness (tasks.md 9.1).
- **Tests.**
  - Red first, with the suite and the harness moved onto the bindings before they existed: 12
    compile errors, one per name in each file, each `error FS0039: The value, constructor,
    namespace or type 'bindSaveGoogleAccount' is not defined` (and the same for the other five).
  - **The mutations again, after the fix:** The never-saving
    `saveGoogleAccount` now fails **12** tests: the suite's three that save an account
    (`SaveGoogleAccount then ListGoogleAccounts returns every field intact`, `saving an account
    twice updates rather than duplicating`, `RemoveGoogleAccount deletes a known account and reports
    true`) and 9 of the 13 E2E flows. The never-discarding `discardAuthorisation` fails **2**:
    `DiscardAuthorisation removes the authorisation it names` and `... leaves every other account's
    authorisation alone`. The pre-existing `SqliteConnectionPoolingTests` failure is set aside in
    both counts. Both mutations were reverted and never committed, and the factory was compared
    byte-for-byte with the fixed version afterwards.
  - No new test, and no behavioural red run, because no behaviour changed. The 22 shared-suite tests
    now run the factory's bindings rather than copies of them.
- **Also corrected.** tasks.md 7.1 gave that suite as 12 tests over five dependency types. It is 22
  over six: `DiscardAuthorisation` joined it in series 1 round 1.

### Considered and deliberately not changed

- **The factory's members are still reached by no test past their preconditions.**
  `RegisterAccount`, `ReauthoriseAccount` and `SetDefaultInvoiceCalendar` are only driven as far as
  their refusals (no client secret, an unregistered account), and the E2E harness composes its own
  API record. Measured: after the fix, with the `RegisterAccount` member
  handing the workflow a discard that does nothing (`fun _ -> Ok ()`), no test fails (1547 of 1548
  pass, the pre-existing failure aside). Closing that means the factory taking its three Google-facing
  dependencies (`AuthoriseAccount`, `ReauthoriseAccount`, `ListCalendars`) as parameters, so that
  the E2E harness goes through the composition root with fakes in their place. That reverses
  tasks.md 9.1's recorded choice, so it is flagged rather than folded in. Change #7 adds four
  Google-facing dependency types to this factory and meets the same question for its own flows.
- Series 1's deferrals and series 2 rounds 1–7's stand, for the reasons they give.

### Series 2 round 8 gate

| Check | Result |
| --- | --- |
| Baseline over `7ce8db3`, measured before anything changed | 1548 tests, 1547 passed, 0 skipped. The one failure is the pre-existing `SqliteConnectionPoolingTests`. Build: 0 errors, and only `main`'s three warnings |
| `dotnet build MyDogsbody.sln` | 0 errors. 2 warnings re-emitted, both pre-existing and present on `main`: `Tests\Database\ScanWindowStoreTests.fs` FS0020 and `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760. `PdfProcessing\Program.fs` FS0025 is untouched and was not rebuilt (the baseline build reported it) |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1548 → **1548**, 1547 passed, 0 skipped, no hang. No test was added or removed: the 22 contract tests and the E2E harness now run the factory's bindings |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **817** (including the one pre-existing failure, which is tagged `Unit`), Integration **327**, Contract
**361**, E2E **43**. 1548 in total, unchanged.

---

## After PR review series 2: the members that go through Google, tested past their refusals

**Why.** Series 2 round 8 found that the factory's `RegisterAccount`, `ReauthoriseAccount` and
`SetDefaultInvoiceCalendar` were tested only as far as their refusals, and round 9 agreed. The
factory composed each of them, and the E2E harness re-composed its own copy around its fakes, so no
test ran the factory's composition past the refusals that come before Google. Of the two remedies
rounds 8 and 9 set out, the user chose the second: make each member's composition a public function
that both the factory and the tests call. It keeps tasks.md 9.1's choice that the harness composes
its own API record, which the first remedy would have reversed.

**Measured before**, with a temporary mutation of `createGoogleAccountApi`, reverted from git and
never committed: with `RegisterAccount` handed a discard that did nothing, so that every refused
registration would leave its refresh token stored, the whole suite still passed - 1547 of 1548, the
pre-existing `SqliteConnectionPoolingTests` aside.

**What changed.**

- `GoogleAccountApiFactory` gains four public functions, each taking its Google-facing dependency as
  a parameter: `registerAccountWith` (`AuthoriseAccount`), `reauthoriseAccountWith`
  (`ReauthoriseAccount`), and `getCalendarsForWith` and `setDefaultInvoiceCalendarWith`
  (`ListCalendars`). Each is the workflow over the storage bindings, its answer mapped to the UI
  record and its error translated: the code `createGoogleAccountApi` already had, moved rather than
  changed. `createGoogleAccountApi` hands them the real consent flow and calendar client, so
  production behaviour is unchanged.
- The E2E harness's `RegisterAccount`, `GetCalendarsFor` and `SetDefaultInvoiceCalendar` are those
  functions, handed its two fakes. The harness still composes the record itself and never calls
  `createGoogleAccountApi` (tasks.md 9.1). Its storage-only members stay composed over the factory's
  `bind*` functions, and no flow uses its `ReauthoriseAccount`.
- **Widening, noted:** `GetCalendarsFor` was not in round 8's list. It has the same shape - a
  Google-facing member the factory tests reached only as far as its refusals - so it is included.

**Tests**, in `Startup/GoogleAccountApiFactoryTests.fs`:

- **Red first.** The tests and the harness were moved onto the four functions before they existed:
  `error FS0039: The value, constructor, namespace or type 'registerAccountWith' is not defined`, and
  the same for the other three, in both files. No behaviour changed, so there is no behavioural red
  run; the mutations below are what show the tests can fail.
- **Integration**, each against a temp `Google.db` with a `handleError` that records what it logs.
  `registerAccountWith`: storing the authorised account and keeping the token consent wrote; refusing
  an account already registered and discarding the token consent wrote for it; refusing before any
  consent flow when no client secret has been supplied. `reauthoriseAccountWith`: storing the email
  Google now reports while keeping the default calendar; a cancelled consent leaving the account as
  it was. `setDefaultInvoiceCalendarWith`: storing a calendar the account still has; refusing one it
  no longer has, storing nothing.
- **Unit**, since `getCalendarsForWith` touches no store: every calendar field mapped; a blank account
  id refused without asking for calendars; a failed fetch passed on with its own message.
- Every success asserts every field of the returned record and of the stored row. Every refusal
  asserts the message, the `ActionName` and the inner exception.

**The mutations again, after the change.** Each was made in the working tree, run against the whole
suite, and undone by restoring a copy of the finished file, compared byte for byte afterwards.

| Mutation | Whole suite, after (the pre-existing failure aside) |
| --- | --- |
| `registerAccountWith`'s discard does nothing | **1 failing**: `registerAccountWith refuses an account already registered, and discards the token consent wrote for it` |
| `registerAccountWith`'s save does nothing | **10 failing**: `registerAccountWith stores the authorised account, not ready, and keeps the token consent wrote` and 9 E2E flows |
| `reauthoriseAccountWith`'s save does nothing | **1 failing**: `reauthoriseAccountWith stores the email Google now reports, keeping the default calendar` |
| `setDefaultInvoiceCalendarWith`'s save does nothing | **2 failing**: `setDefaultInvoiceCalendarWith stores a calendar the account still has` and E2E `choosing a default calendar makes a not-ready account ready` |

**What stays untested, and why.** `createGoogleAccountApi` now only chooses which real adapter each
function is handed. The consent flow needs a system browser (tasks.md 7.3), and the calendar list's
real binding, `bindListCalendars`, has its own contract suite over a stubbed `HttpMessageHandler`.

**Also updated:** tasks.md 5.2 and 9.1, and CLAUDE-project.md's contract guidance, since change #7
adds four Google-facing dependency types to this same factory.

### Gate

| Check | Result |
| --- | --- |
| Baseline over `d62afe3`, measured before anything changed | 1548 tests, 1547 passed, 0 skipped. The one failure is the pre-existing `SqliteConnectionPoolingTests` |
| `dotnet build MyDogsbody.sln` | 0 errors. 2 warnings re-emitted, both pre-existing and present on `main`: `Tests\Database\ScanWindowStoreTests.fs` FS0020 and `Tests\Integrations\Documents\PdfDocumentReaderTests.fs` FS0760. `PdfProcessing\Program.fs` FS0025 is untouched and was not rebuilt |
| `dotnet test` (`--blame-hang --blame-hang-timeout 5m`) | 1548 → **1558** (+10), 1557 passed, 0 skipped, no hang |
| Failures | One: the pre-existing `SqliteConnectionPoolingTests`, which fails on `main` too |

Per level, each measured with `--filter "Level=..."`: Unit **820** (+3, including the one pre-existing failure, which is tagged `Unit`), Integration **334** (+7), Contract **361**, E2E **43**. 1558 in total.
