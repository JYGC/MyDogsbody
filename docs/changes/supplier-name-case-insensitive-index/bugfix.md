# Bugfix: Suppliers.Name unique index is case-sensitive

Follow-up to `invoice-ledger-foundation`. Found during code review of that change's stacked PR split.

## Current Behavior (Defect)

WHEN two suppliers are added whose names differ only by case (e.g. `"Acme"` and `"acme"`) THEN the `IX_Suppliers_Name` unique index does not reject the second insert, because SQLite's default column collation is `BINARY` (case-sensitive) and the index was created without `COLLATE NOCASE`.

This contradicts `design.md`'s decision 4 for `invoice-ledger-foundation`, which states the index exists specifically as a database-level backstop for the workflow's case-insensitive uniqueness rule ("the index is what makes the database refuse a duplicate even when the code is wrong"). Today it only backstops exact-case duplicates.

## Expected Behavior (Correct)

WHEN two suppliers are added whose names differ only by case THEN the `Suppliers` table's unique index SHALL reject the second insert, matching the case-insensitive rule `AddSupplierWorkflow`/`EditSupplierWorkflow` already enforce at the domain level.

## Unchanged Behavior (Regression Prevention)

WHEN two suppliers are added with the exact same name THEN the unique index SHALL CONTINUE TO reject the second insert (already covered by `SupplierMigrationsTests.fs`).

WHEN `MigrateUp` is run against an empty database THEN the `Suppliers` and `SupplierMatchers` tables SHALL CONTINUE TO be created with their existing columns.

WHEN `Down` is run THEN it SHALL CONTINUE TO remove the `Suppliers`/`SupplierMatchers` tables and their index, leaving the database as `MigrateUp` found it.

## Root cause

`Migration_20260809000001_CreateSuppliersTable.fs` creates `IX_Suppliers_Name` via FluentMigrator's fluent index builder (`Create.Index(...).OnColumn("Name")...WithOptions().Unique()`), which has no way to express a per-column `COLLATE` clause — the same category of FluentMigrator/SQLite gap `CLAUDE-project.md` already documents for `Create.ForeignKey`. The index was never given `COLLATE NOCASE`, so it enforces byte-for-byte uniqueness rather than the case-insensitive rule the design called for.

## Fix

A new migration (`20260810000001`) drops `IX_Suppliers_Name` and recreates it via `Execute.Sql` with `Name COLLATE NOCASE`, mirroring the existing `Execute.Sql` workaround pattern already used for the `SupplierMatchers` foreign key. `Down()` reverses it back to the original fluent (case-sensitive) index, so rollback restores exactly what this migration changed.

Timestamp `20260810000001` is a new reservation, one day past `invoice-ledger-foundation`'s own `20260809000001`–`…0002` block and clear of `invoice-to-calendar`'s `#2` reservation (`…0003`–`…0004`) recorded in `docs/changes/invoice-to-calendar/background.md`.
