# Tasks — Logging is not an integration

A rename with no behaviour change, so no test lands first — see `design.md` → *Testing strategy*.
The existing suite must pass unchanged; that is the gate.

## Phase 1 — Move (required)

- [x] **1.1** `git mv MyDogsbody.Integrations.Logging MyDogsbody.Logging`, then rename the `.fsproj` inside it.
      *Note:* the directory rename was refused (`Permission denied`) — VS Code's Ionide (`fsautocomplete`) and C# Dev Kit (CPS) language servers hold handles on project directories. `dotnet build-server shutdown` did not release them. Worked around by creating the target directories and `git mv`-ing each file individually, which Windows permits, then removing the emptied originals. History is preserved; `git status` shows all nine files as renames.
- [x] **1.2** `git mv MyDogsbody.Integrations.Logging.Database.Models MyDogsbody.Logging.Database.Models`, then rename the `.csproj` inside it.
- [x] **1.3** Delete `bin/` and `obj/` under both renamed directories.
      *Note:* had to be done *before* the move, not after — the locked handles were on the build-output directories.

## Phase 2 — Namespaces (required)

- [x] **2.1** `ExceptionLog.cs` — namespace.
- [x] **2.2** The six `.fs` files — `namespace` / `module` declarations and every intra-project `open`.

## Phase 3 — References (required)

- [x] **3.1** `MyDogsbody.Logging.fsproj` — `ProjectReference` to the renamed models project.
- [x] **3.2** `MyDogsbody.Startup.fsproj` and `MyDogsbody.Startup/Startup.fs` — reference path and three `open`s.
- [x] **3.3** `PdfProcessing.fsproj` and `PdfProcessing/Program.fs` — reference path and three `open`s.
- [x] **3.4** `MyDogsbody.sln` — both `Project(...)` lines: display name and path. GUIDs unchanged, so the configuration block needs no edit.
      *Note:* while the old directory was briefly absent, an IDE project system removed the `MyDogsbody.Integrations.Logging.Database.Models` entry from the solution outright — the `Project(...)` line, its `EndProject`, and all twelve of its `ProjectConfigurationPlatforms` lines. Restored by hand under the new name with the original GUID. The final `git diff` of `MyDogsbody.sln` is exactly two changed lines; anything more means the IDE edited it again.

## Phase 4 — Gate (required)

- [x] **4.1** `dotnet build MyDogsbody.sln` — 0 errors, 0 warnings, all 19 projects including the WPF host.
- [x] **4.2** `dotnet test` — 28 passed, 0 failed, 0 skipped. No test file was edited by this change.
      *See the flake note below.*
- [x] **4.3** Grep for `Integrations.Logging` — no matches in code, project files or the solution. The only hits are in `docs/changes/` (this change's own specs), which is what the requirement allows.
- [x] **4.4** `LoggingDatabaseContextModule.fs` still binds `exceptionCollectionName = "Exceptions"`. The persisted collection name and the `ExceptionLog` field names are byte-for-byte unchanged, so existing `Logging.db` files still read back.

## Phase 5 — Documentation (required)

- [x] **5.1** `CLAUDE-project.md` — the summary line, the project-structure row, the name-collision note, the `writeLog` wiring line, the *Log database* rules, the add-a-log-type steps, and the "the project name is wrong and stays wrong for now" paragraph, which this change made obsolete.

## Flake observed (pre-existing, not caused by this change)

The **first** post-build full run failed one test:
`CredentialApiFactoryTests.AddCredential keeps credentials of different infrastructure types apart`,
`Assert.Equal(2, credentials.Length)` — actual `1`. The second `AddCredential` did not land, and the
test discards both add `Result`s with `|> ignore`, so the failure surfaced only at the count.

It did not reproduce: the test passes in isolation, and three further full-suite runs were 28/28.

**It cannot be this change.** `MyDogsbody.Tests` has no `ProjectReference` to the logging project —
it constructs its own `HandleErrorBuilder (fun _ -> ())` and never opens `Logging.db`. The renamed
code is not in that test's reference closure, and `git diff` for this change touches no credentials,
Spine, UI or test file.

Prime suspect is the already-recorded disposal gap: `getDatabaseContext` closes over the `LiteDatabase`
and never hands it back, so every integration test leaks an open handle on a temp file for the life of
the run (`docs/changes/startup-composition-root/tasks.md` → *Follow-ups not taken*). Diagnosing it
needs the add `Result`s asserted rather than ignored, so the failure names itself. Left for the change
that fixes the disposal seam — not fixed here, because that is a test-and-integration defect and this
change is a rename.

## Not done

- The `.fs` file names and folder shape inside the projects are unchanged, as designed.
- Nothing is committed. The working tree holds the renames and the doc edits.
