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
