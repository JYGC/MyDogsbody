# Rationale moved out of `MyDogsbody.Integrations.Google`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Integrations.Google/GoogleAuthorization.fs`

### GoogleAuthorization.fs: authoriseWith

Was a doc comment (`///`) at line 89.

```text
The seam `authorise`/`reauthorise` close over with the real Google.Apis calls above. A test
calls this directly with fakes for both, so no test opens a browser, starts a loopback
listener, or reaches the network - see `GoogleAuthorizationTests`.

Takes `accountId` as a parameter rather than minting one itself, so re-authorising an
existing account can reuse its id as the same OAuth datastore key - the credential row for
that id is simply overwritten, and the Accounts row (its `DefaultInvoiceCalendar` included)
never has to move to a new identity.

Returns raw strings (email, accountId): this is the outer ring, so the domain's
`GoogleEmail`/`GoogleAccountId` wrapping happens at the composition root, the same as every
other adapter in this codebase.
```

### GoogleAuthorization.fs: let! credential =

Was a comment (`//`) at line 116.

```text
These two steps convert their own known failure shapes into a plain Result value
rather than letting them raise - so cancelling consent, a malformed secret, or a
missing email pass through the outer handleError block unlogged, the same idiom
PdfDocumentReader.readContent uses for a missing file.

Both are awaited with `.GetAwaiter().GetResult()`, never `Async.AwaitTask |>
Async.RunSynchronously`. The consent flow is an async method, so its failures arrive
as a faulted Task, and AwaitTask surfaces that as an AggregateException - which no
catch here or below matches, so every named failure used to reach the user as
"One or more errors occurred. (...)", logged. GetResult rethrows the original.
```

## `MyDogsbody.Integrations.Google/GoogleCalendarClient.fs`

### GoogleCalendarClient.fs: | :? TokenResponseException as caughtInvalidGrantException w

Was a comment (`//`) at line 182.

```text
Not an answer from the Calendar API at all: the credential tried to refresh its access
token and Google's token endpoint refused the refresh token - "invalid_grant", "Token has
been expired or revoked." That is requirements.md's "a stored token has expired or been
revoked", and while a refresh token is stored it arrives this way rather than as a 401 from
the call itself (the credential answers a 401 by refreshing). It is also the common case: a
Testing-mode OAuth client's refresh tokens last seven days. Without this clause it fell
to the catch-all below and read as "Could not reach Google Calendar." - a revoked grant
reported as a network problem. Google.Apis.Auth has already deleted the stored token by
the time this runs, so re-authorising is the only remedy left, and the one reported.

Only invalid_grant: the token endpoint's other refusals (invalid_client,
unauthorized_client) implicate the client secret rather than this account's grant, and
keep the catch-all with Google's code appended.
```

### GoogleCalendarClient.fs: service.Events.Patch(googleEvent, CalendarId.value calendarI

Was a comment (`//`) at line 524.

```text
PATCH, not Update (PUT): events.update replaces the whole event resource, so any
field the request body does not set - extendedProperties (the InvoiceSyncKey
createEvent stamped on this event) and reminders among them - is CLEARED
server-side, not left alone. buildAllDayGoogleEvent only ever sets Summary,
Description, Start and End (Q2.14's "title and date"), so an Update here would
silently wipe the sync key on the event's very first update: the next sync would
read the event back as keyless (an orphan) and the invoice as unsynced (a fresh,
duplicate CreateEvent) - precisely the duplicate requirements.md says the extended
property exists to prevent ("the extended property was chosen so a rename would not
cause a duplicate"). Patch sends the same fields but merges rather than replaces, so
extendedProperties and reminders survive untouched (PR #23 review round 3).
```
