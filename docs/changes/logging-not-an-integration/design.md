# Design — Logging is not an integration

## Why the name is wrong

An integration is a capability the application composes with others: it owns a slice of domain
behaviour, `Spine` pulls it into a use case, and its data flows up the layer chain into the UI.
`Integrations.Credentials` and `Integrations.Pdf` fit. Logging does not:

- `Spine` does not reference it, and must not — composing integrations is Spine's job, and logging
  is not something to compose.
- It never appears in a use case and has no DTO hop. Nothing maps a logging type across a layer.
- It is not passed as a layer dependency. Every other store arrives as a leading collection-getter
  parameter; the log store arrives already closed over inside `handleError`.
- It holds no domain data and nothing reads it at runtime.

The `Integrations.` prefix therefore asserts something untrue about where logging sits, and the
reference graph already disagrees with it.

## Scope

Two projects move:

| From | To |
| --- | --- |
| `MyDogsbody.Integrations.Logging` | `MyDogsbody.Logging` |
| `MyDogsbody.Integrations.Logging.Database.Models` | `MyDogsbody.Logging.Database.Models` |

The entity project follows the component. Leaving it as `Integrations.Logging.Database.Models`
would move the misnomer rather than remove it, and would leave `MyDogsbody.Integrations.*` matching
a project that is not an integration.

**Nothing inside the projects is restructured.** Folder shape (`Database/Types`, `Repositories/Types`,
`UseCases/Types`), file names, function names, DTO fields, the `Exceptions` collection name and the
`ExceptionLog` document shape are all untouched. This change is a rename and only a rename.

## What has to change

| Kind | Files |
| --- | --- |
| Directory names | the two project folders |
| Project file names | `MyDogsbody.Logging.fsproj`, `MyDogsbody.Logging.Database.Models.csproj` |
| Namespace / module declarations | 6 `.fs` files, 1 `.cs` file |
| `open` inside the project | `LoggingDatabaseContext.fs`, `LoggingDatabaseContextModule.fs`, `ExceptionRepository.fs`, `ExceptionUseCases.fs` |
| `open` in consumers | `MyDogsbody.Startup/Startup.fs`, `PdfProcessing/Program.fs` |
| `ProjectReference` | `MyDogsbody.Logging.fsproj` (→ models), `MyDogsbody.Startup.fsproj`, `PdfProcessing.fsproj` |
| Solution entries | `MyDogsbody.sln` — two `Project(...)` lines, paths and display names |

The project GUIDs in `MyDogsbody.sln` stay as they are. A rename does not need new GUIDs, and
keeping them means the `GlobalSection(ProjectConfigurationPlatforms)` block needs no edit at all —
fewer places to get wrong.

`MyDogsbody.Exceptions.Types/ActionNames.fs` has **no** logging entries, so the persisted
`ActionName` strings are unaffected. Verified by grep before starting, not assumed.

## Assembly name

Neither project sets `<AssemblyName>` or `<RootNamespace>`, so both default to the project file
name. Renaming the `.fsproj` / `.csproj` is therefore what renames the assembly — no property to
add, and the F# namespaces are declared explicitly in each file rather than derived, so they must
be edited by hand.

## Stale build output

`bin/` and `obj/` under the old directory names hold artefacts named after the old assembly, plus
`project.assets.json` recording the old reference paths. Moving the directory carries them along.
They are deleted rather than reused: a stale `obj/` can resolve a reference that the edited
`.fsproj` no longer declares, which would let a half-finished rename appear to build.

## Error handling

None. No function's `Result` shape, error identity, message or `ActionName` changes. The
`HandleErrorBuilder` wiring in `Startup.fs` is re-pointed by namespace only — `writeLog` is still
built from `ExceptionUseCases.addException` partially applied over
`loggingDatabaseContext.GetExceptionCollection`.

## Testing strategy

There is no new behaviour, so there is no new unit test to write first. The existing suite is the
regression net, and the requirement is that it passes **unchanged** — a rename that needed a test
edited would be a rename that changed behaviour.

- **Compiler as the primary check.** F# namespaces are explicit and `open` is checked; a missed
  rename is a build error, not a silent fallthrough. The build covers every reference in the
  table above.
- **Existing suite, unmodified.** 28 tests. `MyDogsbody.Tests` does not reference the logging
  project — tests pass a no-op or recording `HandleErrorBuilder` and never touch `Logging.db` —
  so a green run after the rename also confirms the change did not leak into the test project.
- **Manual check of the one thing the compiler cannot see:** that `Logging.db` still gets an
  `Exceptions` row. The collection name is a string literal in `LoggingDatabaseContextModule`, so
  it is asserted by reading the file, not by the type checker.

## Risk

Low, and mechanical. The one genuine risk is a partial rename that builds because stale `obj/`
output papers over it — addressed by deleting build output before the verification build, and by
grepping for `Integrations.Logging` afterwards rather than trusting a green build alone.
