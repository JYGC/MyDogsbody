# MeasureScan

Throwaway harness for `docs/changes/invoice-extraction/outcome.md` tasks **12.4** (scan timing) and
**12.5** (due-date coverage). **Not in `MyDogsbody.sln`** — the gate never touches it. Delete this
folder, and `measure.db` / `Thunderbird.db` / `Logging.db` from the repo root, once the numbers are
recorded.

It uses the real composition root: real migrations, real `MailAccountApi` / `SupplierApi` /
`TemplateApi` / `InvoiceApi`, real `MailFolderReader` against your real Thunderbird profile. No UI,
no clicking. It seeds the four measured suppliers + templates (`background.md` → *The four worked
templates*), scans, and prints a table to paste into `outcome.md`.

## Run

All commands from the **repo root** (so the `.db` files land there). `profileRoot` at the top of
`Program.fs` is already your path.

### 1. Discovery mode (suppliers still `REPLACE...`)

```powershell
dotnet run --project MeasureScan\MeasureScan.fsproj
```

Scans **every** account and prints:

- one line per account: format, folder counts, header-pass message count;
- per-account invoice/problem counts from the scan;
- the **cause breakdown** and a **top-40 sender-domain table** — the domains your invoices
  actually come from.

Use that table to fill in `suppliers` in `Program.fs`:

- `MatcherValue` = the sending domain from the table (e.g. Xero really sends from
  `post.xero.com`, not `xero.com`). If a supplier's mail is spread across domains, use
  `MatcherKind = "Subject"` and a substring common to its subjects.
- `PaymentTermDays` = only matters for a supplier that prints no due date (IODM); check a real
  invoice.
- `DateFormat` = open one PDF each, match the style (`d MMM yyyy`, `d/M/yyyy`, `d/M/yy`,
  `MMM d, yyyy`).

### 2. Measurement mode (suppliers filled in)

Reboot first for a true cold reading, then:

```powershell
dotnet run --project MeasureScan\MeasureScan.fsproj
```

It prints the `===== paste into outcome.md =====` block. Paste it into the table at the bottom of
`outcome.md`.

If the cause breakdown still shows lots of `NoSupplierMatched` for real supplier mail, the
`MatcherValue` is wrong — check it against the sender-domain table.

## What the numbers mean

- **12.4** — if "window widen" is more than ~2 s of scanning, Q1.9's immediate rescan loses:
  replace it with an explicit **Refresh** button. `InvoicesModuleCreators.getInvoicesModule`
  already splits `GetInvoices` (fast) from `Scan`, so it's a small change.
- **12.5** — the `12% → 39%` prediction was over ~30 suppliers / 558 PDFs. Four suppliers won't
  reproduce that scale; your number is "of what these four templates extracted, X% carry a due
  date". **Read it off the merged branch before starting change #7** — if it's near 12%, the
  calendar sync isn't the highest-value next change (friction #19).

## Cleanup

```powershell
Remove-Item measure.db, Thunderbird.db, Logging.db -ErrorAction SilentlyContinue
Remove-Item -Recurse MeasureScan
```
