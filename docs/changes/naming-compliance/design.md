# Naming compliance — design

## Approach

Rename in dependency order, one phase per ring, verifying a build after each phase before
starting the next — a symbol renamed in an inner ring must be updated at every call site in
outer rings before those outer rings are themselves edited for their own local violations.

Phases:

1. `MyDogsbody.Domain` (references nothing; 44 files)
2. `MyDogsbody.Builders`, `MyDogsbody.Exceptions`, `MyDogsbody.Exceptions.Types` (4 files)
3. `MyDogsbody.Database.Models`, `MyDogsbody.Database`, `MyDogsbody.Database.Migrations`
   (23 files) — includes SQLite column renames via new migrations
4. `MyDogsbody.Integrations.Google`, `.Integrations.Documents`, `.Integrations.Thunderbird`
   + their C# `*.Database.Models` entity projects (23 `.fs` + entity `.cs` files) — includes
   LiteDB entity property renames + `[BsonId]` fix-ups
5. `MyDogsbody.Logging` + `MyDogsbody.Logging.Database.Models` (5 `.fs` + entity `.cs`)
6. `MyDogsbody.Startup` (13 files) — composition root, wires everything renamed so far
7. `MyDogsbody.UI.Types` (17 files)
8. `MyDogsbody.UI.Portal` (21 files) + WPF host `MyDogsbody` C# project
9. `MyDogsbody.Tests` (127 files) — last, since it references every project above
10. Scratch/standalone projects (`GNUCashAccess`, `MeasureScan`, `PdfProcessing`,
    `TestMsGraphToEmails`) — lowest priority; not part of the shipped app or its test
    suite, touched only if time remains after 1–9 build and test clean.

After each phase: `dotnet build` the affected project(s) (and anything already-renamed that
references them) before moving on. Full `dotnet build MyDogsbody.sln` and
`dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` only make sense once phases 1–9 are
done, since Tests and Startup both reach every other project.

## Execution mechanism

Each phase is delegated to a subagent. Because renames ripple (a public function/type/field
renamed in project A must be updated at its call sites in projects B, C that reference A),
phases run **sequentially, not in parallel** — a later phase's agent sees the already-renamed
state of everything earlier on disk. Within a phase touching several small projects, one
agent handles all of them together if the projects are small (phase 2, 3, 4, 5), since
they're tightly coupled (e.g., a store and its entity project).

Each subagent is briefed with:
- The naming rule (quoted from CLAUDE.md) and the glossary below.
- The specific project(s)/files in its phase.
- Instruction to grep the *whole repo* (not just its own phase's projects) for usages of any
  public symbol it renames, and update every call site — even in projects not yet visited,
  so later phases start from a consistent base and don't rediscover the same rename.
- Instruction to keep behavior identical — this is a rename, not a refactor of logic.
- Instruction to report back: files changed, symbols renamed (old → new) for anything public
  (crosses a file/project boundary), and whether the project(s) it touched build cleanly.

## Scope decisions (see requirements.md for the rules themselves)

- **Exempted "already a full word" initialisms**: `Id`, `Api`, `Sql`, `Url`, `Uri`, `Html`,
  `Json`, `Http`/`Https`, `Pdf`, `Ui`, `Xml`, `Guid`, `Csv`. Chosen because CLAUDE.md and
  CLAUDE-project.md themselves use these as established first-class vocabulary
  (`SupplierApi`, stored `Id`, `.db`), and because they name a specific standalone concept
  rather than truncating one for brevity. If the user disagrees for a specific one (most
  likely candidate: `Id` → `Identifier`), it's a small follow-up rename, not a redesign.
- **Generic type parameters** (`'T`, `'a`) keep single-letter/short F# convention — a
  structural placeholder for parametric polymorphism, not a named value; BCL/FSharp.Core
  signatures we don't own use the same convention throughout.
- **`_` (discard)** is exempt — it is the explicit "this names nothing" pattern.

## Glossary — abbreviation → full word (apply the meaning that fits the local context; these
are starting points, not a blind find/replace, since e.g. `r` means a different thing in
every file it appears in)

| Short form | Expands to (contextually) |
| --- | --- |
| `ex` | `caughtException` (in a `with ex ->` handler) or a more specific name if the exception's role is more specific |
| `ctx` | `context` (or `<Thing>Context` if it's a specific context record) |
| `cfg`/`config` (bare) | `configuration` |
| `db` (bare var) | `database` or `databaseContext`/`databaseConnection` as fits |
| `deps` | `dependencies` |
| `args` | `arguments` |
| `params` | `parameters` |
| `req` | `request` |
| `resp` | `response` |
| `msg` | `message` |
| `err` | `error` |
| `cmd` | `command` |
| `cs` | spell out what it actually holds (seen: credential secret, connection string, calendar summary — contextual) |
| `dt` | `dateTime` or `date` |
| `dir` | `directory` |
| `sub` | `subscription`/`substring`/whatever it actually is — contextual |
| `acc` | `account`/`accumulator` — contextual |
| single letters (`a,b,c,d,e,f,i,j,k,l,m,n,o,p,r,s,t,u,v,w,x,y,z`) | the actual thing the value represents in that spot — read the code, name what it is |

## Verification

- `dotnet build` after each phase (project-level, then solution-level once all production
  code is done).
- `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` once Tests compiles against the
  renamed production code. No new test cases are expected — the same tests, recompiled,
  should still pass (behavior is unchanged). Where a rename touches a persisted-shape
  contract test (`Contracts/PersistedShapeTests.fs`,
  `Contracts/GoogleCredentialPersistedShapeTests.fs`, `Contracts/ActionNamesTests.fs`), the
  asserted field-name strings are updated to match — that is the point of those tests.
- New FluentMigrator migration(s) for any SQLite column rename get their own
  `MigrateUp`/`Down` round-trip test, per CLAUDE-project.md's existing migration-testing
  convention.
