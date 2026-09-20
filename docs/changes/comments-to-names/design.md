# Comments to names — design

## Approach

Read every comment in a ring sentence by sentence (requirements.md → Rules applied), decide
what each sentence is, and then act on it. The work is judgment per sentence with a compiler
checking every rename, so it is done by reading the code rather than by pattern.

A typical Domain block, and what happens to it:

```fsharp
/// The domain is the part after the LAST '@' of the address, not the first. A quoted local part
/// legally carries its own ("a@b"@acme.example), and splitting on the first '@' also left the
/// trailing '>' on the domain of every display-name sender - so SenderDomain "acme.example" did
/// not match ...
let private senderDomain (sender: string) : string =
```

The first sentence *describes* → it moves into the name
(`senderDomainAfterTheLastAtSignOfTheAddressNotTheFirst`) and is deleted. The rest *explains
why* → it stays, untouched. Most blocks in this codebase split at the first sentence.

## The forms it takes

| A comment that... | becomes | From MyDogsbody.Domain |
| --- | --- | --- |
| describes a private function in its first sentence | the function's name | `/// The row must already exist. Identified by its id and nothing else.` over `ensureExists` → `ensureAStoredSupplierAlreadyHasTheEditedId` |
| trails a union case or field | the case's or field's name | `SenderAddress of string // exact, case-insensitive` → `SenderAddressEqualToIgnoringCase of string` |
| explains a subexpression | an intermediate `let` | `let! deleted = ...` under "turns a false result (no row carried that identifier) into SupplierNotFound" → `let! aRowCarryingThatIdentifierWasDeleted = ...` |
| states a constraint on a literal | the literal's name | `MaxAbsAmount`, "a typo guard, not a policy" → `MaximumAbsoluteAmountAsATypoGuardNotAPolicy` |
| explains an invariant in prose | a type or a labelled field | `Money = private Money of decimal * string // amount, currency code` → `Money of amount: decimal * currencyCode: string`; a `SelectedContent * SelectedContent list` "head and tail so there is always at least one" → a named record |
| only restates the name | nothing — it is deleted | `/// Been through the store.` over `StoredSupplier` |

## What is left alone, and why

- **Rationale**, always. See requirements.md → Scope.
- **Ubiquitous-language types and the architecture's own vocabulary** — requirements.md → Rules
  applied #4. A name such as `SupplierId` is used in hundreds of places, in specs, and in
  CLAUDE-project.md; lengthening it to carry "the identifier the store assigned" would make
  every line that uses it worse. The doc stays where it says something the name cannot
  ("opaque to the domain").
- **A public rename whose cost outruns its sentence.** Measured before deciding, at
  `011d924` (uses across `.fs`, tests included):

  | Identifier | Uses | Test names embedding it | Decision |
  | --- | ---: | ---: | --- |
  | `compilePattern` | 22 in 6 files | 6 | left — comment kept |
  | `computeCutoff` | 12 in 5 files | 1 | left — comment kept |
  | `SupplierGone` | 24 in 12 files | — | left — comment kept |
  | `InvoiceStoreFailed` | 49 in 14 files | — | left — comment kept |
  | `InvoiceNotFound` | 20 in 10 files | — | left — comment kept |
  | `SelectionCleared` | 25 in 12 files | — | left — comment kept |
  | `UploadableInvoice.ofStored` | 8 in 6 files | 2 | left — comment trimmed |

  The renames that *are* made cost 2–18 uses each: `SenderAddress`, `SenderDomain`,
  `SubjectPattern`, `TemplateRuleShapeInvalid`, `InvoiceSyncKey.PropertyName`,
  `InvoiceSyncKey.parts`, `Money.MaxAbsAmount`, and the two `Word` coordinates.

## Persisted shapes

None of the renamed symbols is serialized by name. The search that would find it —
`%A`, `FSharpValue`, `UnionCaseInfo`, `GetUnionFields`, `nameof`, `Enum.Parse` /
`Enum.GetName`, `.GetType().Name` — has no match in production code at `011d924`, so no
renamed union case can leak into a database column or a message. It is re-run at the end of
each phase, because a later phase's code could introduce one.

## Phases

Ring order — an inner ring is finished before an outer ring is edited for its own comments, so
a renamed public symbol is already updated at every call site when the outer ring is reached.

| # | Project(s) | `.fs` / `.cs` files |
| --- | --- | ---: |
| 1 | `MyDogsbody.Domain` — plus call sites of renamed symbols in any project | 48 |
| 2 | `MyDogsbody.Builders`, `MyDogsbody.Exceptions`, `MyDogsbody.Exceptions.Types` | 4 |
| 3 | `MyDogsbody.Database.Models`, `MyDogsbody.Database`, `MyDogsbody.Database.Migrations` | 25 |
| 4 | `MyDogsbody.Integrations.Documents`, `.Google`, `.Thunderbird` and their `*.Database.Models` | 31 |
| 5 | `MyDogsbody.Logging`, `MyDogsbody.Logging.Database.Models` | 6 |
| 6 | `MyDogsbody.Startup` | 15 |
| 7 | `MyDogsbody.UI.Types` | 19 |
| 8 | `MyDogsbody.UI.Portal`, and the WPF host `MyDogsbody` | 24 |
| 9 | `MyDogsbody.Tests` — including the Arrange/Act/Assert markers and banner dividers | 143 |

`MeasureScan` is a standalone experiment outside `MyDogsbody.sln`; it is out of scope, as it
was in `naming-compliance`.

Phase 9 has one thing the others do not: turning a banner divider into a nested module changes
the test's `FullyQualifiedName`, so anything filtering with `--filter FullyQualifiedName~` needs
re-checking, and a `[<MemberData>]` source resolves against the new nesting.

## Mechanics

- **The working tree mixes line endings** (`core.autocrlf=true`; at `011d924`, 141 tracked `.fs`
  files are CRLF and 163 are LF, and 140 of the 240 files this change does not touch are LF).
  Edits go through a small patcher that reads a file, requires each old text to match
  **exactly once**, applies the replacement and writes the file back with whatever line endings
  and encoding it already had — so an edit that does not match fails loudly instead of silently
  doing nothing, and no file changes style. Git prints "LF will be replaced by CRLF" for the LF
  files it diffs; that warning is the baseline's, not this change's.
- **An identifier rename is a word-boundary replace**, qualified (`InvoiceSyncKey.parts`, never
  bare `parts`) wherever the bare word is common, and reviewed per file before it is applied.
  Names that F# lets a record field and a union case share are checked for collisions.
- **The build is the second check.** A missed call site is a compile error, not a runtime
  surprise; a rename applied to something unrelated shows up as a compile error or, if it is
  consistent, as a diff line that has nothing to do with the sentence it was made for — which
  is why the diff is read too.

## Verification

- **Baseline, measured before any edit** on `origin/main` (`011d924`), in the working tree:
  `dotnet build MyDogsbody.sln` → 0 errors, 2 warnings (`FS0760` in
  `PdfDocumentReaderTests.fs`, `FS0020` in `ScanWindowStoreTests.fs` — both already on `main`);
  `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj` → **1726 passed, 0 failed, 0 skipped**.
- **After each phase:** the same two commands. The phase is done when the build shows the same
  two warnings and no others, and the suite is 1726/1726 (or the current total, if the phase
  adds tests). Anything else is this change's doing.
- **Exit check:** re-run `classify-comment-blocks.awk` over the phase's project. It reports
  blocks that *describe a declaration* by the absence of rationale vocabulary, so a block that
  is genuinely rationale but worded without those words still shows up. The phase's residual is
  read, and each block that stays is listed in outcome.md with the reason. From Git Bash, at the
  repository root:

  ```
  find MyDogsbody.Domain -name "*.fs" -not -path "*/obj/*" -not -path "*/bin/*" -print0 \
    | xargs -0 awk -f docs/changes/comments-to-names/classify-comment-blocks.awk \
    | cut -f1 | sort | uniq -c
  ```

- **The known LiteDB flake** (CLAUDE-project.md → Testing) is a possible intermittent failure in
  any test that constructs a LiteDB context. If it appears it is named in outcome.md, not
  re-run until green.
