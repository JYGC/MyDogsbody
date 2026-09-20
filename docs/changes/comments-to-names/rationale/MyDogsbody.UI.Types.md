# Rationale moved out of `MyDogsbody.UI.Types`

Comment blocks of 10 lines or more, moved here verbatim by the
`comments-to-names` change (phase 10). Each was replaced in the source by one line naming its
section below. Nothing was reworded; the text is exactly what the source held.

## `MyDogsbody.UI.Types/InvoiceSyncUiType.fs`

### InvoiceSyncUiType.fs: SyncPlanRowUiType

Was a doc comment (`///`) at line 19.

```text
One action the plan would take, shown before anything runs (Q2.13) - naming the invoice, not
just an event id (design decision 3). InvoiceId and DueDate are None only for a delete: the
invoice itself has already left the ledger, which is the whole reason it is a delete.

SyncKey carries the raw InvoiceSyncKey string (supplier + reference, InvoiceSyncKey.value) -
not shown on screen, but the ONLY field ExecuteSyncPlan may use to tell one row's action from
another's. Reference alone is not unique: the ledger's index is on (supplier, reference), not
on reference alone, so two different suppliers can share the same reference text. Matching a
ticked selection back to plan actions by Reference let a tick on one supplier's row also fire
a different supplier's action for the same reference text - PR #23 review round 1.
```
