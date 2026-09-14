# Naming compliance — requirements

CLAUDE.md gained a mandatory "## Naming" section: every function, value, parameter, type,
record field, union case and module must have a long, self-documenting name — no single
letters, no abbreviations (lambda parameters included) — and a comment is only earned once a
fully descriptive name would exceed 80 words. This change brings the existing codebase into
compliance. There is no behavior change; this is a pure rename.

This is a mechanical/structural change rather than a behavioral feature, so it is described
here as scope and rules rather than EARS statements.

## Scope

- Every `.fs` file across all projects (`MyDogsbody.Domain` through `MyDogsbody.Tests`).
- Every `.cs` file that is part of the shipped app (LiteDB entity classes, the WPF host) —
  the naming section is written in F# vocabulary but its own words are "anything you can
  name"; the same bar applies to C#.
- **Persisted shapes are in scope.** Per explicit decision: LiteDB entity property names and
  SQLite column names get renamed too, not left as an exception. Consequences:
  - A LiteDB entity property rename needs a `[BsonId]` (or equivalent) attribute wherever the
    renamed property was relied on as the implicit primary key, since LiteDB's default `Id`
    convention keys off the property name.
  - A SQLite column rename needs a FluentMigrator migration (`Rename.Column`), never an
    edit to an already-applied migration.
  - Local `.db` files are git-ignored dev scratch (per CLAUDE-project.md, deleted after
    manual testing) — no production data migration path is needed beyond the schema
    migration itself.
- Excluded: generated/scaffold files this change doesn't touch beyond renames flowing from
  the projects above (none identified); third-party code.

## Rules applied

1. No single-letter identifiers anywhere a name is being introduced — functions, values,
   `let` bindings, parameters, **lambda parameters**, DU case payload names, module-level
   bindings. `_` (an explicit discard pattern) is exempt: it names nothing.
2. No abbreviations. A short technical initialism that is a complete, standalone term in
   its own right — not a truncation of a longer phrase for brevity — is treated as already
   a full word rather than an abbreviation: `Id`, `Api`, `Sql`, `Url`, `Uri`, `Html`, `Json`,
   `Http`/`Https`, `Pdf`, `Ui`, `Xml`, `Guid`, `Csv`. These are the terms CLAUDE.md and
   CLAUDE-project.md already use as first-class vocabulary throughout the architecture
   (`SupplierApi`, `GoogleAccountApi`, stored `Id`, `.db` files). Everything else that
   truncates a longer word purely to save keystrokes is a violation: `ex`, `ctx`, `cfg`,
   `db` (as a bare variable name), `args`, `req`, `resp`, `msg`, `err`, `cmd`, `deps`, `cs`,
   `dt`, `dir`, `sub`, `acc`, and so on — see design.md's glossary.
3. Generic type parameters (`'T`, `'a`) declared purely for parametric polymorphism keep
   ordinary F#/.NET single-letter convention (`'T`, `'TAccount` where meaningful) — this is
   a structural placeholder, not a named value, and F#/BCL signatures we don't own use the
   same convention. Documented as a scope decision, not silently skipped.
4. Renaming is repo-wide and consistent: a symbol keeps one name across every file that
   references it. No project is "half renamed."
5. No behavior change. Tests are updated to compile against the new names; no new test
   cases are required by this change beyond what proves the rename didn't change behavior
   (existing suite staying green) and the migration/`BsonId` tests called out above.
