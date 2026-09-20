# Rationale moved out of `MyDogsbody.Database`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.Database/DatabaseContextSetup.fs`

### DatabaseContextSetup.fs: databaseConnection

Was a comment (`//`) at line 23.

```text
"Foreign Keys=True" makes Microsoft.Data.Sqlite issue PRAGMA foreign_keys = 1 itself on
every open of this connection - PRAGMAs are per-connection and off by default in SQLite,
so without this the SupplierMatchers -> Suppliers cascade (migration ...0002) is decorative.

"Pooling=False": a pooled connection keeps its file handle open after Dispose(), so a
temp-database test cannot delete its file - which is what drove the harnesses to the
process-global SqliteConnection.ClearAllPools(), disposing connections other parallel tests
were mid-command on. With pooling off, Dispose() releases the handle immediately and there
is nothing global to clear.

This is a trade, not a free win. One SqliteConnection *object* is held for the process
lifetime (Startup.fs), but its underlying handle is opened and closed per store operation -
explicitly in SupplierStore/TemplateStore's inTransaction, and by Dapper around every
SelectAsync on a closed connection - so the pool was amortising real work. Measured on
Microsoft.Data.Sqlite 9.0.10, one connection object over 2000 open/query/close cycles:
0.090 ms per cycle pooled, 0.470 ms unpooled (+0.38 ms, 5.2x). A suppliers page load is two
of those cycles, so well under a millisecond - invisible in a desktop UI, and worth paying
to stop the suite failing ~2 runs in 45. See docs/changes/sqlite-pool-flake.
```

## `MyDogsbody.Database/InvoiceStore.fs`

### InvoiceStore.fs: isMissingSupplier

Was a doc comment (`///`) at line 55.

```text
Whether a failed write failed because the supplier row it referenced is not there any more.

`Invoices` and `InvoiceTombstones` each declare exactly one foreign key - `SupplierId` - so a
foreign-key violation on a write to either says precisely that and nothing else. The
composition root turns it into the domain's `SupplierGone`, which
`ScanForInvoicesWorkflow.step` records as that one message's problem and carries on past;
requirements.md asks for exactly that - "WHEN a scan finds an invoice whose supplier has since
been deleted THE SYSTEM SHALL report it as a problem rather than storing an invoice with no
supplier."

Without it the violation arrived as an undifferentiated `InvoiceStoreFailed`, which the same
`step` treats as FATAL: one supplier deleted from the suppliers page while a scan was running
(measured at ~60 s, so the window is wide open, and the page stays reachable throughout) ended
the whole run with "Failed to store invoice.", discarded every problem the scan had gathered,
and reset the account's watermarks. The `SupplierGone` branch and its unit test existed from the
start; nothing in production could reach them.

Kept here rather than at the composition root because it is SQLite knowledge: swapping the
store swaps this with it, and the factory's one line stays as it is.

The chain is walked rather than probed at a fixed depth - `runSync` (Async.AwaitTask) wraps the
SqliteException in an AggregateException, so the real one sits two levels down today, and
nothing should depend on it staying there.
```
