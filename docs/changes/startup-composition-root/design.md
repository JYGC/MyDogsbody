# Design — Startup composition root

## System architecture and components

### Why not literally in the host project

The host `MyDogsbody.csproj` is a C# WPF project (`Microsoft.NET.Sdk.Razor`, `UseWPF`), and a
C# project cannot compile an F# file. `Startup.fs` therefore lives in a new F# project,
**`MyDogsbody.Startup`**, referenced by the host. The host calls one function and expresses no
wiring itself, which is the property that was actually wanted.

### Before

```
MyDogsbody (C#)  ──►  MyDogsbody.Compositions.Interfaces  ──►  MyDogsbody.Spine
     │                          ▲
     └──► MyDogsbody.Compositions │
                                  │
              MyDogsbody.UI.Portal ┘        (so UI.Portal sees Spine transitively)
```

`Compositions.Interfaces` declares `ICredentialCompositions` in terms of **Spine DTOs**, so the
"UI never references Spine" rule holds only by project name. `CredentialsPage.fs` already opens
`MyDogsbody.Spine.UseCases.Types` and builds an `AddCredentialUseCaseTypeDto` by hand.

### After

```
MyDogsbody (C#)  ──►  MyDogsbody.Startup  ──►  MyDogsbody.Spine  ──►  Integrations
       │                     │
       │                     └──►  MyDogsbody.UI.Types   (CredentialApi lives here)
       │                                  ▲
       └──►  MyDogsbody.UI.Portal  ───────┘
```

`MyDogsbody.UI.Portal` references `UI.Types` and `Enums` only. Spine is no longer reachable from
the UI by any path.

### Project layout

`MyDogsbody.Startup` — three files, in compile order:

| File | Holds | Side effects at module init |
| --- | --- | --- |
| `CredentialApiMappers.fs` | UI record ⇄ Spine DTO mapping | none |
| `CredentialApiFactory.fs` | `createCredentialApi handleError getCredentialCollection` | none |
| `Startup.fs` | database paths, contexts, `handleError`, `credentialApi`, `registerServices` | opens `Logging.db` and `Credentials.db` |

The split is the point. `SetupDatabase.fs` today opens both database files the moment any binding
in the module is touched, which is why `CredentialCompositions` cannot be tested. Keeping the
mappers and the factory in files with **no module-level bindings that perform I/O** means a test
can reach them without a database, and `Startup.fs` — the one file that cannot be tested — contains
nothing but partial application.

## Sequence diagrams

### Startup

```
MainWindow.xaml.cs
  └─ Startup.registerServices services
       ├─ AddSingleton<CredentialApi> credentialApi
       └─ credentialApi = CredentialApiFactory.createCredentialApi
                            handleError
                            credentialDatabaseContext.GetCredentialCollection
```

### Add a credential

```
CredentialsEditorDialog  ──(IntegrationCredentialUiTypeWithoutId)──►  browserModule.AddCredential
  └─ api.AddCredential
       ├─ CredentialApiMappers.toAddCredentialUseCaseTypeDto        (pure)
       └─ CredentialUseCases.addNewCredential handleError getColl   (Result)
  ── Ok ()  ──►  transact: Error := None ; LoadCredentials ()   → table reloads
  ── Error ex ──►  transact: Error := Some ex.Message           → MudAlert shows
```

### Read credentials

```
browserModule.LoadCredentials
  └─ api.GetAllCredentials
       ├─ CredentialUseCases.getAllCredentials handleError getColl  (Result)
       └─ Result.map (List.map CredentialApiMappers.toUiType)       (pure)
  ── Ok rows ──► transact: CredentialsList := rows ; IsLoading := false
  ── Error ex ─► transact: Error := Some ex.Message ; IsLoading := false
```

The read failure path is the behavioural change that matters most: today it is
`| Error ex -> failwith ex.Message`, which surfaces as an `ErrorBoundary'` crash rather than a
message on the page.

## Data models and interfaces

`MyDogsbody.UI.Types/CredentialApi.fs` — the whole surface the UI can reach:

```fsharp
type CredentialApi =
    {
        GetAllCredentials : unit -> Result<IntegrationCredentialUiType list, MyDogsbodyException>
        AddCredential     : IntegrationCredentialUiTypeWithoutId -> Result<unit, MyDogsbodyException>
        EditCredential    : IntegrationCredentialUiType -> Result<unit, MyDogsbodyException>
    }
```

This requires `MyDogsbody.UI.Types` to reference `MyDogsbody.Exceptions.Types`, which has no
project references of its own, so no cycle and no new transitive surface.

`CredentialsBrowserModule` gains an error channel and the two write commands:

```fsharp
type CredentialsBrowserModule =
    {
        CredentialsListAval : aval<IntegrationCredentialUiType list>
        IsLoadingAval       : aval<bool>
        ErrorAval           : aval<string option>
        LoadCredentials     : unit -> unit
        AddCredential       : IntegrationCredentialUiTypeWithoutId -> unit
        EditCredential      : IntegrationCredentialUiType -> unit
    }
```

`LoadCredentials` exists today but is never invoked by anything — that is the reason the table does
not refresh after a write. Here the write commands call it.

### Mapping

| UI type | Spine DTO | Direction |
| --- | --- | --- |
| `IntegrationCredentialUiTypeWithoutId` | `AddCredentialUseCaseTypeDto` | UI → Spine |
| `IntegrationCredentialUiType` | `CredentialUseCaseTypeDto` | both |

Field-for-field, no renames. The `Username` → `ExternalUsername` rename stays where it is, at the
Spine domain boundary, and is not touched by this change.

## Error-handling approach

Unchanged below the composition root: every layer function still returns
`Result<'T, MyDogsbodyException>` via `HandleErrorBuilder`, and `handleError` still writes an
`ExceptionLog` row unless the inner exception is an `ApplicationException`.

What changes is the last hop. `CredentialApi` returns the `Result` rather than collapsing it:

- `|> ignore` on writes — deleted. A failed write reported success.
- `failwith ex.Message` on reads — deleted. A failed read crashed the boundary.

The UI is now the place that decides what to do with a failure, which is `ErrorAval` → `MudAlert`.

## Testing strategy

| Level | What | Where |
| --- | --- | --- |
| Unit | `CredentialApiMappers` both directions, every field asserted | `Startup/CredentialApiMappersTests.fs` |
| Unit | `getCredentialsBrowserModule` against a hand-built `CredentialApi` — Ok path, Error path, refresh-after-write, error cleared on success | `UI/ModuleCreators/CredentialsBrowserModuleCreatorsTests.fs` |
| Integration | `createCredentialApi` against a real temp LiteDB file — add → read round trip, edit → read reflects change | `Startup/CredentialApiFactoryTests.fs` |
| Contract | UI record → Spine DTO → … → LiteDB `Credential` and back, asserted field-for-field | `Contracts/CredentialHopChainTests.fs` |

`Startup.fs` itself is not tested: it is partial application over the two files that are, and
touching it opens the real database files. That is the reason it holds nothing else.

E2E (bUnit rendering `CredentialsPage` against a real LiteDB file) is **not** included — the bUnit
harness does not exist in this repository and standing it up is a larger change than this one.
Recorded as a gap in `tasks.md` rather than silently dropped.

## Bootstrapping

`dotnet build MyDogsbody.sln` does not currently succeed and `dotnet test` cannot run at all, so the
completion gate cannot be invoked before this change fixes it. Per CLAUDE-project.md the first
change under the testing policy carries that repair:

- `MyDogsbody.Tests` — two test files still open `MyDogsbody.Domains.*` and
  `MyDogsbody.Infrastructure.PdfDocuments.*`, renamed to `MyDogsbody.Spine.Domains.*` and
  `MyDogsbody.Integrations.Pdf.Domains.*`. The project also lacks references to Spine and
  Integrations.Pdf.
- `PdfProcessing` — the same stale namespaces in `Program.fs`.
- `MyDogsbody.UI.Portal` — `CredentialsComponents.fs:65` passes an `IntegrationCredentialUiType`
  to a callback declared as `IntegrationCredentialUiTypeWithoutId`. Fixed by this change anyway:
  the edit path needs the `Id`.
