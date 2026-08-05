# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Project specifics — structure, architecture, and the build / run / test commands — live in [CLAUDE-project.md](CLAUDE-project.md), imported below. Read it before making any change.

@CLAUDE-project.md

## Coding style

**A functional approach is preferred.** Default to it for all new and changed code, unless the request explicitly asks for something else or the platform makes it impossible.

- **Immutable data.** F# records and discriminated unions over classes with settable properties. `let`-bound values; no `mutable` unless there is genuinely no alternative. To change something, build a new value and return it — don't mutate shared state in place.
- **Types carry the rules.** Model so invalid states can't be written down: constrained types with private constructors and a `create`, a distinct type per pipeline stage, choice types instead of flags. A validated value is a *different type* from an unvalidated one.
- **Pure functions, effects behind a parameter.** No project at the centre performs I/O, reads a clock or generates randomness. Where a workflow genuinely needs one, it declares a **function type** and receives a function value — so the pipeline is testable with lambdas, not mocks, and the real thing is chosen at the composition root.
- **Dependencies as leading function parameters**, partially applied at the composition root — not constructor injection, not a service locator, not a static reference reached into from inside a function.
- **Expressions over statements.** `Result` / `Option` pipelines, `match`, `List.map` / `bind` / `fold` in place of loops and accumulator variables.
- **Errors are values, and which value depends on the ring.** Domain workflows return `Result<'T, '<Area>Error>` — a DU carrying what the message needs — written with the domain's own `result` builder. Outer-ring functions (stores, API clients, adapters) return `Result<'T, MyDogsbodyException>`, written with `handleError`. The two meet only in the `*ApiFactory`. Exceptions are caught at the boundary and converted; never raised as control flow, in either ring.
- **Composition over inheritance.** Modules and functions; no class hierarchies for behaviour you could express as a function.

The reference shape is a workflow: **dependencies first, input last, `Result` out**. See CLAUDE-project.md → *Architecture*. The `Spine` layer functions predate it and are being retired — copy the workflow shape, not theirs.

**When it isn't possible**, say so in the change description, and keep the imperative part as small and as close to the boundary as it can be. The known unavoidable cases in this codebase: LiteDB entities must be mutable C# classes (settable properties, `ObjectId`), Fun.Blazor dialogs must be classes inheriting `FunComponent`, and the WPF host is C# / XAML. In the UI, `cval` + `transact` is the sanctioned mutation point — mutation elsewhere in a component is not.

**The UI keeps its own state model.** FSharp.Data.Adaptive, not MVU — don't introduce `Model`/`Msg`/`update` or a dispatch loop. Why, and what the onion does ask of the screen, in CLAUDE-project.md → *UI*.

If a request explicitly calls for an imperative or object-oriented shape, follow the request; this preference is a default, not a veto.

## Changes (spec-driven work)

> This approach is based on [Kiro's spec methodology](https://kiro.dev/docs/specs/). The [Requirements-First workflow](https://kiro.dev/docs/specs/feature-specs/requirements-first/) is the standard used here: specify system behaviour before making architectural decisions.

Non-trivial features and bug fixes are tracked as a **change** — a folder at `docs/changes/<change-name>/` containing up to three spec files. Use specs for anything complex, costly to get wrong, or requiring iterative design. Skip specs for exploratory/prototype work.

### Spec files

**`requirements.md`** — the *what*. Organise by feature area (H2) and user story group (H3). Each requirement uses EARS notation:

```
WHEN <condition> THE SYSTEM SHALL <action>
```

Example:
```
## Device Management

### Add device
WHEN a user submits a valid new-device form THE SYSTEM SHALL create the device record and record a creation history entry.
WHEN a user submits a device name that already exists THE SYSTEM SHALL display an "Name already taken" error.
```

Also cover edge cases and error-handling scenarios.

**`design.md`** — the *how*. Sections: system architecture and components, sequence diagrams, data models and interfaces, error-handling approach, testing strategy.

**`tasks.md`** — the *steps*. Discrete, trackable implementation tasks, each with a clear description, expected outcome, and any dependencies. Mark tasks required vs optional. Work through independent tasks first, then dependent ones in order.

**`bugfix.md`** — replaces `requirements.md` for bug fixes. Three sections using their own notation:

```
## Current Behavior (Defect)
WHEN <condition> THEN the system <incorrect behavior>

## Expected Behavior (Correct)
WHEN <condition> THEN the system SHALL <correct behavior>

## Unchanged Behavior (Regression Prevention)
WHEN <condition> THEN the system SHALL CONTINUE TO <existing behavior>
```

The "Unchanged Behavior" section is the key addition — explicitly locking down what must not change prevents regressions. The `design.md` for a bugfix includes root cause analysis; `tasks.md` includes tests that verify the bug is fixed and unchanged behavior is preserved.

### Workflow

**Feature:**
1. Create `requirements.md` and agree on it before writing `design.md`.
2. Create `design.md` and agree on it before writing `tasks.md`.
3. Execute `tasks.md` one task at a time, marking each done as you go.

**Bugfix:**
1. Create `bugfix.md` (current / expected / unchanged behavior) and agree on it.
2. Create `design.md` including root cause analysis.
3. Create and execute `tasks.md`, including tests for fix and regression prevention.

Before starting any non-trivial feature, refactor, or bug fix, check `docs/changes/` for an existing change folder. If none exists, create one and start with `requirements.md` (feature) or `bugfix.md` (bug).

## Testing (mandatory)

### The rule

**Unit tests are written before the implementation.** For any change to production code, the failing unit test lands first: add the test, run it, confirm it fails for the reason you expect, then write the code that makes it pass. Do not write the implementation and back-fill tests afterwards. Inside `tasks.md`, this ordering applies per task, not once per change.

**A change is not complete until the solution builds clean and the full test suite is green** — zero build errors, zero test failures, zero skips. The exact invocations are in CLAUDE-project.md → *Commands*.

No `Skip=`d facts, no placeholder assertions, no "green except for X, will fix in a follow-up". If a level genuinely cannot be exercised, say which one and why in the change description — do not silently drop it.

All four levels below must exist and pass before a change is done. Only the unit tests must be written first; integration, contract, and E2E may follow the implementation. Tag every test with its level so levels can be run selectively.

### The four levels

**1. Unit** — every new or changed function, exercised in isolation with no I/O. Required cases: the success path with **every output field asserted**, not a success-or-shape check; the error path, asserting the exact error identity and everything it carries — for a domain workflow that is the DU case *and its payload*, for an outer-ring function the `ActionName`, message and preserved inner exception; and any expected-failure path that is deliberately handled rather than logged. A workflow's dependencies are supplied as lambdas, so "no I/O" is the default rather than an effort.

**2. Integration** — anything touching the database or filesystem, against a real store, with a fresh disposable instance per test and no state shared between tests. Existing behaviour you depend on but are not changing gets a characterization test *before* you change anything near it.

**3. Contract** — the boundaries. Every published interface gets one shared suite run against the real implementation *and* against every fake, so fakes cannot drift; **a dependency function type is a published interface** — its real adapter and every fake standing in for it in a workflow test run the same suite. Every mapper at a ring boundary is asserted field-for-field in both directions, with deliberate renames asserted as renames. Every persisted shape is asserted by field name, and every error translation is asserted case by case. Adding a workflow, a dependency type, a boundary mapper or an API record means adding its contract test in the same change.

**4. E2E** — user-visible flows through the real stack, from the composition root down to a real database and back into what the UI renders. If a flow can only be verified by launching the app, record the manual steps in the change description and state that the coverage was manual.

Per-level guidance for this codebase — the available seams, fixtures, harness gaps, and where test files go — is in CLAUDE-project.md → *Testing in this codebase*. That file names the one level whose harness does not exist yet; until the change that needs it builds it, say so in the change description rather than claiming the level passed.
