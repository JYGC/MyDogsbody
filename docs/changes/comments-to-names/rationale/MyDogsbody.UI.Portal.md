# Rationale moved out of `MyDogsbody.UI.Portal`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.UI.Portal/ModuleCreators/GoogleAccountsBrowserModuleCreators.fs`

### GoogleAccountsBrowserModuleCreators.fs: loadCalendarsFor

Was a doc comment (`///`) at line 50.

```text
Loads the calendars for one account, so its picker shows that account's own list
(requirements.md: "populate it from that account's own calendars"). A failure here does
not disturb the accounts table - it only leaves that one picker empty - but it is surfaced
via ErrorAval rather than swallowed, so "the picker is empty" comes with a reason (an
expired credential, a network failure, a rate limit) instead of no explanation at all.

The alert names the account by its email, the way the table does. Nothing the user did
starts this fetch - the page runs one per account - so the reason on its own ("needs to be
re-authorised", "Google is rate-limiting this account") leaves someone with two accounts
unable to tell which one to act on.
```

## `MyDogsbody.UI.Portal/ModuleCreators/InvoicesModuleCreators.fs`

### InvoicesModuleCreators.fs: scanUsing

Was a doc comment (`///`) at line 140.

```text
Read the mailbox for `days` (via `scanOperation` - `Scan` or the watermark-clearing
`RescanEverything`), then show the stored ledger for it. The scan may fail (no mail
account, an unreachable store) - the stored ledger stays on screen with the alert.

The page's view comes from `GetInvoices` / `GetProblems`, never from `ScanResult`: that
carries only what THIS scan did (design decision 6), and watermarks mean a scan of an
unchanged mailbox reads no messages and so returns two empty lists. Taking the table from
it blanked a ledger that was still stored - on the initial load too, since `start` scans,
so a returning user opened on an empty table. Q1.19 says the same of problems: they are
persisted precisely "so incremental scanning does not empty the diagnostic list before it
is looked at". Read AFTER the scan, so whatever it just stored is included.
```
