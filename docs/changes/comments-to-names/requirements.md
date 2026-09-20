# Comments to names — requirements

CLAUDE.md's "## Naming" section says a comment is earned only when a name that fully
described the thing would exceed 200 words, and that below that limit "a comment means the
name isn't finished yet, so lengthen the name instead of writing the comment." The codebase
carries far more comment than that leaves room for. This change moves what those comments
*describe* into the names they sit on. There is no behavior change.

This is a mechanical/structural change rather than a behavioral feature, so — like
`naming-compliance` — it is described here as scope and rules rather than EARS statements.

## What the comments are

Measured at `origin/main` (`011d924`) with `classify-comment-blocks.awk`, which is in this
folder, over every `.fs` and `.cs` file. The eleven C# files hold 19 of the blocks and 68 of the
lines; the F# files alone are 2,132 blocks and 6,730 lines. It sorts by vocabulary, so it is good
for sizing a change and for its exit check and is not a substitute for reading each block.

| Kind | Blocks | Lines | Can a name absorb it? |
| --- | ---: | ---: | --- |
| Rationale — why, history, measured evidence, a spec citation | 869 | 4,431 | No. A name says what a thing is, not why it is that way |
| Describes a declaration — no rationale vocabulary, directly above a `let` / `type` / case / field | 623 | 1,402 | **Yes. This is the change's target** |
| Describes a statement — inside a body | 253 | 535 | Yes — an intermediate `let` |
| `// Arrange` / `// Act` / `// Assert` | 219 | 243 | Yes — the binding names |
| `// ---------- banner ----------` | 187 | 187 | Yes — a nested module |
| **Total** | **2,151** | **6,798** | |

`MyDogsbody.Domain` alone is 303 blocks and 1,416 lines: 167 rationale, 120 describing a
declaration, 16 describing a statement.

`naming-compliance`'s addendum audited the repository and found every block to be "why"
content. That audit asked whether a comment describes something that is already named; it did
not ask whether a longer name could absorb what the comment says. Many blocks *begin* with a
description and *end* with a rationale, and the classifier files those under rationale — so the
two questions have different answers.

## Scope

- **The change ships in phases, one architectural ring each, in ring order** — the way
  `naming-compliance` ran, and so each phase can be reviewed as its own PR. Phase 1 is
  `MyDogsbody.Domain` plus every call site, in any project, of a public symbol it renames.
  design.md lists the phases; tasks.md tracks them.
- **Rationale stays.** The 4,431 lines saying why cannot become names. Moving them out of the
  source into `docs/changes/*/design.md`, leaving a pointer, is a different move with a
  different risk (it changes what a reader finds in the file) and is a separate change if
  wanted.
- `// Arrange` / `// Act` / `// Assert` markers and banner dividers are a Tests-phase concern;
  production code has almost none.

## Rules applied

1. **The sentence is the unit.** Each comment is read sentence by sentence, and each sentence is
   one of three things:
   - *describes* what the thing is or does — the name must carry it, and the sentence goes;
   - *restates* the name, the type signature, or what the next few lines visibly do — the
     sentence just goes, no rename needed;
   - *explains why* — history, measured evidence, a requirement or design citation, a
     constraint on callers, a consequence — it stays. Where deleting a neighbouring sentence
     would leave it dangling ("this", "one"), it is reworded minimally and nothing else about it
     changes.
2. **Private and local names are renamed freely.** They have no other call site.
3. **Public names are renamed when the sentence being absorbed is worth the ripple.** The
   compiler and a repo-wide search find every call site; a test *name* that embeds a renamed
   identifier is updated with it. A public rename that would touch many test names, or many
   files for one sentence, is not made — each such decision is listed, with its number, in
   outcome.md so it can be overruled.
4. **Some names are left alone on purpose**, and their comments stay wherever the comment says
   something the name cannot:
   - ubiquitous-language types whose doc *defines a term* the specs and CLAUDE-project.md use
     (`SupplierId`, `ProfileRootPath`, `ScanCutoff`, ...);
   - workflow entry points, which are named for their workflow, one per `*Workflow.fs`;
   - a constrained type's `create` / `value`, and the dependency function types, which
     CLAUDE-project.md names as published interfaces;
   - any identifier CLAUDE.md or CLAUDE-project.md refers to by name.
5. **A name never carries a claim the code does not back.** Where a comment misdescribes its
   code, the name says what the code does, and the discrepancy is recorded in outcome.md.
6. **Structural forms are allowed where they remove a comment** — an intermediate `let`, a
   labelled union field, a named record in place of a tuple — and only where the existing suite
   covers the behavior. Otherwise the change is a rename.
7. **No behavior change.** Tests are updated to compile against the new names and nothing else.

## Testing note

This is a rename with no behavior change, so CLAUDE.md's "failing unit test lands first" rule
has no new behavior to specify a test for — the same position `naming-compliance` took.
Compliance is shown by the full existing suite passing unchanged in every assertion, and by the
build being no worse than `main`'s (see design.md → Verification). Where a phase makes a
structural change to a function (rule 6), the existing tests that drive it are named in
outcome.md.
