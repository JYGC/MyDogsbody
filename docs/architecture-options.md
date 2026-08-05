# Architecture options

> **Decided: Option 2 — functional onion (*Domain Modeling Made Functional*).** Option 3 (MVU/Elmish) was
> **rejected**; the UI stays on FSharp.Data.Adaptive. This document is kept as the *rationale* — what
> the alternatives were and why they lost. The rule that binds new code is
> [CLAUDE-project.md → Architecture](../CLAUDE-project.md#architecture); where the two differ, that file wins.

Ways MyDogsbody could have been structured. Written as a menu, before the choice was made.

## What counts as an option

Two requirements, both must be met:

**1. Functional.** Immutable data, pure functions, effects at the edges. Not an object-oriented pattern with functional decoration.

**2. Well documented.** There is a book, an official site, or a maintained reference series to follow — written for F#, not translated from another language. Options are rated on this explicitly, with the primary source named.

Patterns that are popular but fail one of these are listed at the end, with the reason.

---

## Where things stand

The app is built in **layers** — the screen, the rules, the database, each a horizontal slice, with data travelling down through every one.

Two problems remain, in plain terms:

- **Six copies of the same data.** Adding a credential passes through six near-identical record types. Exactly one field is renamed along the way; the rest is copied verbatim. Six identical forms so one word can change on form three.
- **The database leaks upward.** The core rules layer accepts a LiteDB type in its function signatures, so the storage technology reaches into the core. Changing database means editing the core.

The design isn't wrong — it's built for a larger system than this one currently is.

### Already fixed

A third problem — **the screen reaching past its boundary** — has been dealt with. The
composition layer was replaced by a single startup composition root
(`docs/changes/startup-composition-root/`):

- `MyDogsbody.Compositions` and `MyDogsbody.Compositions.Interfaces` are gone. `MyDogsbody.Startup/Startup.fs` builds the database contexts, the shared error handler and the UI-facing API, and the C# host calls one function.
- The UI is handed a `CredentialApi` — a record of functions speaking **UI types**, not core DTOs. `MyDogsbody.UI.Portal` now compiles against `UI.Types`, `Enums` and `Exceptions.Types` and nothing else; the core is genuinely unreachable from the screen rather than merely off-limits by convention.
- Failures are no longer discarded on the way up. A failed write used to report success and a failed read used to crash the boundary; both now return a value the page renders as an alert.

That leaves the two problems above, which is what the options below are for. Note the database
leak is now contained *below* the composition root rather than running to the top of the app —
smaller, but the same defect.

---

## The running example

Every option below is shown doing the same two jobs, so they can be compared directly:

- **Link** — the user types a Google email address on a settings page; the app checks it and stores it in the Google integration's own database.
- **Read** — list the linked addresses back on that page.

All code is a **sketch**, not compilable F#. `...` means "detail omitted".

### How it would look today

Folders, following the existing credentials pattern:

```
MyDogsbody.UI.Types/                     GoogleAccountUiType.fs                       ← copy 1
                                         GoogleAccountApi.fs
MyDogsbody.UI.Portal/                    Pages/Settings/GoogleAccountsPage.fs
                                         Components/GoogleAccountsComponents.fs
                                         ModuleCreators/GoogleAccountsBrowserModuleCreators.fs
MyDogsbody.Startup/                      GoogleAccountApiMappers.fs
                                         GoogleAccountApiFactory.fs
                                         Startup.fs
MyDogsbody.Spine/                        UseCases/GoogleAccountUseCases.fs
                                         UseCases/Types/LinkAccountUseCaseTypeDto.fs      ← copy 2
                                         Domains/GoogleAccountsDomain.fs
                                         Domains/Types/LinkAccountDomainTypeDto.fs        ← copy 3
MyDogsbody.Integrations.Google/          UseCases/GoogleAccountUseCases.fs
                                         UseCases/Types/NewAccountUseCaseTypeDto.fs       ← copy 4
                                         Repositories/GoogleAccountRepository.fs
                                         Repositories/Types/NewAccountRepositoryTypeDto.fs ← copy 5
                                         Database/GoogleDatabaseContextModule.fs
MyDogsbody.Integrations.Google.Database.Models/  GoogleAccount.cs (C#)               ← copy 6
```

Six near-identical records for one email address, five mapper functions between them, and:

```fsharp
// MyDogsbody.Spine/Domains/GoogleAccountsDomain.fs — the leak, in one line
let linkAccount
    (handleError: HandleErrorBuilder)
    (getAccountCollection: unit -> ILiteCollection<GoogleAccount>)  // ← LiteDB, inside the core
    (dto: LinkAccountDomainTypeDto) = ...
```

---

## Option 1 — Dependency rejection + dependency parameterization

**The recommended default in the F# literature, and the closest fit to what already exists.**

Two ideas that work together:

- **Rejection** — don't give your logic a database at all. Fetch the data first, hand plain values to a pure function, save the answer afterwards. Often called the *impure–pure–impure sandwich*: fetch, think, save.
- **Parameterization** — where a function genuinely does need a dependency, pass it as an ordinary parameter and fill it in at startup.

The same shape appears under the name **functional core, imperative shell**: pure rules in the middle, all messy real-world work at the outer edge.

Scott Wlaschin surveys six ways of handling dependencies in F# and lands on exactly this combination as the default, describing rejection as the refactoring to "always" try first.

**What changes here**
- Core functions stop taking `getCredentialCollection`. They take plain records.
- The database call moves out above the rules, into the composition root.
- The core becomes testable with no database, no temp files, no fakes.

### Worked example — linking a Google email address

Same projects as today. The difference is *where the database call happens*, and the middle layers disappearing.

```
MyDogsbody.Spine/
  Domains/GoogleAccountsDomain.fs        ← pure. no database, no Google, no clock
MyDogsbody.Integrations.Google/
  Database/GoogleAccountStore.fs         ← impure edge: talks to LiteDB
  Database/Types/GoogleDatabaseContext.fs
MyDogsbody.Integrations.Google.Database.Models/
  GoogleAccount.cs (C#)                  ← LiteDB entity, unchanged
MyDogsbody.Startup/
  GoogleAccountApiFactory.fs             ← the sandwich is assembled here
  Startup.fs                             ← partial application only
MyDogsbody.UI.Portal/
  Pages/Settings/GoogleAccountsPage.fs
```

**The pure middle** — plain values in, plain values out:

```fsharp
// MyDogsbody.Spine/Domains/GoogleAccountsDomain.fs
module MyDogsbody.Spine.Domains.GoogleAccountsDomain

type GoogleAccount = {
    EmailAddress : string
    DisplayName  : string
    LinkedOn     : DateTimeOffset
}

type LinkFailure =
    | NotAnEmailAddress
    | AlreadyLinked

/// The whole rule for linking an address. No I/O — even "now" is handed in.
let validateNewLink (linked: GoogleAccount list) (now: DateTimeOffset) (address: string) =
    let address = address.Trim().ToLowerInvariant()
    if not (address.Contains "@") then
        Error NotAnEmailAddress
    elif linked |> List.exists (fun a -> a.EmailAddress = address) then
        Error AlreadyLinked
    else
        Ok { EmailAddress = address
             DisplayName  = address.Split('@').[0]
             LinkedOn     = now }
```

**The impure edge** — one job, no decisions:

```fsharp
// MyDogsbody.Integrations.Google/Database/GoogleAccountStore.fs
let getAll handleError (getAccountCollection: unit -> ILiteCollection<GoogleAccount>) ()
    : Result<GoogleAccount list, MyDogsbodyException> = ...

let insertOne handleError getAccountCollection (account: GoogleAccount)
    : Result<unit, MyDogsbodyException> = ...
```

**Link and read** — fetch, think, save:

```fsharp
// MyDogsbody.Startup/GoogleAccountApiFactory.fs
let linkAccount (address: string) =
    handleError {
        let! linked  = GoogleAccountStore.getAll handleError getAccountCollection ()  // impure — fetch
        let  now     = DateTimeOffset.UtcNow                                          // impure — clock
        let! account = GoogleAccountsDomain.validateNewLink linked now address        // PURE   — think
                       |> Result.mapError toMyDogsbodyException
        return! GoogleAccountStore.insertOne handleError getAccountCollection account // impure — save
    }

let listAccounts () =
    GoogleAccountStore.getAll handleError getAccountCollection ()
```

The point: `validateNewLink` holds the whole "may this address be linked?" rule, and it is tested with a list, a date, and a string — no database, no temp files, no fakes. Passing `now` in rather than reading the clock inside is what makes "rejects a duplicate" a repeatable test.

**Documentation** — Excellent, free, F#-specific. A five-part series on *F# for Fun and Profit*, plus Mark Seemann's original posts. This project already uses the parameterization half, so half the reading is confirmation.

**Good** — Fixes the database leak at its root. Biggest testing win for the effort. Smallest conceptual jump from today.
**Bad** — Awkward when logic must fetch *conditionally* ("load this only if that"). Needs real restructuring, not just deletion.
**Effort** — Medium.

---

## Option 2 — Functional onion architecture (Domain Modeling Made Functional)

**The most thoroughly documented option: a full book, written in F#, for exactly this kind of app.**

The larger structure that Option 1 fits inside. Two principles:

- **Types first.** Model the domain with records and choice types so that invalid states can't be written down. Business rules become compile errors instead of runtime bugs.
- **Workflows as pipelines.** A use case is a chain of small functions piped together, each step a plain data transformation. All I/O sits at the outer ring.

**What changes here**
- "Add a credential" becomes one readable pipeline rather than a chain across six projects.
- Types carry the rules — a validated credential is a *different type* from an unvalidated one, so the two can't be confused.
- The six DTO hops collapse: boundary types are kept where the shape genuinely differs, not at every layer. This is the book's own practice, not an inference from it — its sample bounded context has exactly one DTO file, `PlaceOrder.Dto.fs`, at the serialization edge, and the domain types travel unmapped everywhere inside.

### Worked example — linking a Google email address

Folders follow the **workflow**, not the layer. The innermost project references nothing at all.

```
MyDogsbody.Domain/                       ← pure. references no other project
  GoogleAccount/AccountTypes.fs          the vocabulary + the dependency signatures
  GoogleAccount/LinkAccountWorkflow.fs   "link" as one pipeline
  GoogleAccount/ListAccountsWorkflow.fs  "read" as one pipeline
MyDogsbody.Integrations.Google/          ← outer ring: supplies real functions
  Database/GoogleAccountStore.fs
  Profile/GoogleProfileApi.fs            asks Google who owns the address
MyDogsbody.Integrations.Google.Database.Models/
  GoogleAccount.cs (C#)
MyDogsbody.Startup/
  GoogleAccountApiFactory.fs             plugs real functions into the workflows
  Startup.fs                             owns the database handles, nothing else
MyDogsbody.UI.Portal/
  Pages/Settings/GoogleAccountsPage.fs
```

**Types first** — the rules live in the types, so a bad address can't be built:

```fsharp
// MyDogsbody.Domain/GoogleAccount/AccountTypes.fs

/// `private` means the only way to get one is through `create` — so if you are
/// holding an EmailAddress, it has already been checked. Nothing re-checks it later.
/// `create` returns the plain reason for failure; the workflow decides what to call it.
type EmailAddress = private EmailAddress of string
module EmailAddress =
    let create (s: string) : Result<EmailAddress, string> = ...   // rejects "not an address"
    let value (EmailAddress s) = s

type DisplayName = private DisplayName of string          // non-empty, trimmed
type AccountId   = private AccountId of string

/// What the user typed. Untrusted — note the plain string.
type UnlinkedAccount = { TypedAddress: string }

/// Been through validation. The *type* is the proof.
type ValidAccount = { Address: EmailAddress; DisplayName: DisplayName }

/// Been through the store, so it has an Id and a date.
type LinkedAccount = { Id: AccountId; Account: ValidAccount; LinkedOn: DateTimeOffset }

/// The domain's own error type. Note what it is *not*: MyDogsbodyException.
/// That type lives in MyDogsbody.Exceptions.Types, which this project doesn't reference.
type LinkError =
    | NotAnEmailAddress of string
    | AlreadyLinked     of EmailAddress
    | GoogleUnreachable of string

// Dependencies are function types — not interfaces, not classes.
type LoadAccounts  = unit -> Result<LinkedAccount list, LinkError>
type LookupProfile = EmailAddress -> Result<DisplayName, LinkError>
type SaveAccount   = ValidAccount -> Result<LinkedAccount, LinkError>
```

**Link** — the workflow reads top to bottom as the steps of the job:

```fsharp
// MyDogsbody.Domain/GoogleAccount/LinkAccountWorkflow.fs

let private ensureNotLinked (linked: LinkedAccount list) (address: EmailAddress) =
    if linked |> List.exists (fun a -> a.Account.Address = address)
    then Error (AlreadyLinked address)
    else Ok address

/// Dependencies first, input last — the same shape the project already uses.
/// `result` is not `handleError`: see the note under Bad. Everything here fails as LinkError.
let linkAccount
    (loadAccounts: LoadAccounts)
    (lookupProfile: LookupProfile)
    (saveAccount: SaveAccount)
    (input: UnlinkedAccount) : Result<LinkedAccount, LinkError> =
    result {
        let! address = EmailAddress.create input.TypedAddress
                       |> Result.mapError NotAnEmailAddress
        let! linked  = loadAccounts ()
        let! address = ensureNotLinked linked address
        let! name    = lookupProfile address
        return! saveAccount { Address = address; DisplayName = name }
    }
```

**Read** — and the one place the shape genuinely changes, for the screen:

```fsharp
// MyDogsbody.Domain/GoogleAccount/ListAccountsWorkflow.fs
type AccountSummary = { Id: string; Address: string; DisplayName: string; LinkedOn: string }

let private toSummary (a: LinkedAccount) : AccountSummary = ...

let listAccounts (loadAccounts: LoadAccounts) () =
    loadAccounts () |> Result.map (List.map toSummary)
```

**Wiring** — the real functions are chosen once, at startup. This is also the only place the
two error types meet: the stores speak `MyDogsbodyException`, the domain speaks `LinkError`, and
the factory translates in both directions.

```fsharp
// MyDogsbody.Startup/GoogleAccountApiFactory.fs
let private loadAccounts : LoadAccounts =
    fun () ->
        GoogleAccountStore.getAll handleError getAccountCollection ()
        |> Result.mapError (fun ex -> GoogleUnreachable ex.Message)   // in: exception -> LinkError

let private lookupProfile : LookupProfile = ...
let private saveAccount   : SaveAccount   = ...

let linkAccount (input: UnlinkedAccount) : Result<LinkedAccount, MyDogsbodyException> =
    LinkAccountWorkflow.linkAccount loadAccounts lookupProfile saveAccount input
    |> Result.mapError toMyDogsbodyException                          // out: LinkError -> exception

let listAccounts () : Result<AccountSummary list, MyDogsbodyException> =
    ListAccountsWorkflow.listAccounts loadAccounts ()
    |> Result.mapError toMyDogsbodyException
```

Three types instead of six, and each one earns its place: typed-in, validated, stored. `AlreadyLinked` carries the address that clashed, so the error message writes itself. Swapping LiteDB for something else means rewriting `GoogleAccountStore.fs` and its C# entity project, and nothing above them.

The `mapError` pair is the price of the innermost project referencing nothing: a domain that cannot
name `MyDogsbodyException` cannot return one. It buys a domain that compiles without `MyDogsbody.Exceptions.Types`,
LiteDB or the logger, and errors the UI can render as sentences rather than exception messages.

**Documentation** — The strongest of any option. *Domain Modeling Made Functional* (Pragmatic Bookshelf, 2018) is a complete published treatment, plus a free two-day workshop repository and the book's full sample bounded context on GitHub. The book is paid, and the *ideas* are covered free on the author's site — the *designing with types* series for the type modelling, *railway oriented programming* for the pipelines. But the two exact shapes the sketch above uses are **not** in those posts: the `private` constructor and the unvalidated → validated → stored split both come from the book's sample code, which is itself free on GitHub. Read the code alongside the posts. All listed under [Sources](#sources).

**Good** — Answers the DTO duplication and the database leak together. Best long-term shape for an F# app. Enormous learning material.

**Bad** — The biggest change of the three main options. Reads as heavy until the type-modelling idea clicks. Two costs the sketch hides:

- **A second error type, and a builder to go with it.** `HandleErrorBuilder` already binds `Result` — `Bind` is at `MyDogsbody.Builders/HandleErrorBuilder.fs:7` and `ReadPdfDomain.fs` uses `let!` today — so nothing needs teaching. The problem is that its error type is pinned: `Bind` returns `Result<_, MyDogsbodyException>` and `TryWith`'s handler returns one. It cannot bind a `Result<_, LinkError>`. A domain that references no other project therefore needs a second, error-generic builder, plus the `mapError` pair shown in the wiring. Three ways to get the builder: take [FsToolkit.ErrorHandling](https://github.com/demystifyfp/FsToolkit.ErrorHandling), write a generic one next to `HandleErrorBuilder`, or do what the book does — its sample declines the dependency and hand-writes a `ResultBuilder` in `src/OrderTaking/Result.fs`.
- **I/O sits inside the pipeline.** `loadAccounts` is a database read and `lookupProfile` is a network call, both in the middle of the workflow. That is the deliberate difference from Option 1, not an oversight — but note the book makes it visible in the type: its equivalent dependency is `AsyncResult`, and the top-level workflow is an `asyncResult { }` block. Modelling a Google call as a synchronous `Result`, as the sketch does, is the simplification.

**Effort** — Medium to large.

> **Note:** Options 1 and 2 are not rivals. Option 1 is the technique; Option 2 is the building it sits in. Doing Option 1 is a step toward Option 2, never wasted work.

---

## Option 3 — MVU (Model–View–Update) for the screen

**Fixes the UI problems, changes nothing below the UI.**

Popularised by the Elm language, available in F# as **Elmish**:

- **Model** — all screen state in one immutable value.
- **Update** — takes the current state and a message ("user clicked save"), returns the new state.
- **View** — draws the model.

State changes in exactly one place, so "why didn't the screen refresh?" has one place to look.

**What changes here**
- Scattered `cval` variables become a single model per page.
- The bug where the list never reloads after adding a credential becomes structurally hard to write.
- Nothing below the UI is touched.

### Worked example — linking a Google email address

Only the UI folders change. Everything below stays as whichever option you chose.

```
MyDogsbody.UI.Types/
  GoogleAccounts/AccountsModel.fs        the whole screen, as one value
  GoogleAccounts/AccountsUpdate.fs       every state change, in one function — pure
MyDogsbody.UI.Portal/
  Pages/Settings/GoogleAccountsPage.fs   draws the model. decides nothing
MyDogsbody.Startup/
  GoogleAccountApiFactory.fs             unchanged
```

**Model and messages** — the screen has exactly one state value, dialog included:

```fsharp
// MyDogsbody.UI.Types/GoogleAccounts/AccountsModel.fs
type AccountRow = { Id: string; Address: string; DisplayName: string; LinkedOn: string }

type Model = {
    Accounts     : AccountRow list
    IsLoading    : bool
    Error        : string option
    DraftAddress : string option        // Some = the link dialog is open. no separate flag
}

/// Everything that can happen on this screen. Nothing else can.
type Msg =
    | LoadRequested                     // read
    | LoadSucceeded of AccountRow list
    | LoadFailed    of string
    | LinkClicked                       // link
    | DraftChanged  of string
    | LinkConfirmed
    | LinkSucceeded of AccountRow
    | LinkFailed    of string
    | Cancelled
```

**Update** — one function, no I/O, testable without rendering anything:

```fsharp
// MyDogsbody.UI.Types/GoogleAccounts/AccountsUpdate.fs
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadRequested     -> { model with IsLoading = true; Error = None }, Cmd.ofEffect listAccounts
    | LoadSucceeded xs  -> { model with IsLoading = false; Accounts = xs }, Cmd.none
    | LoadFailed e      -> { model with IsLoading = false; Error = Some e }, Cmd.none

    | LinkClicked       -> { model with DraftAddress = Some "" }, Cmd.none
    | DraftChanged a    -> { model with DraftAddress = Some a }, Cmd.none
    | LinkConfirmed     ->
        match model.DraftAddress with
        | Some address -> { model with IsLoading = true }, Cmd.ofEffect (linkAccount address)
        | None         -> model, Cmd.none
    | LinkSucceeded row -> { model with IsLoading    = false
                                        DraftAddress = None
                                        Accounts     = row :: model.Accounts }, Cmd.none
    | LinkFailed e      -> { model with IsLoading = false; Error = Some e }, Cmd.none
    | Cancelled         -> { model with DraftAddress = None }, Cmd.none
```

**View** — reads the model, sends messages, holds no state:

```fsharp
// MyDogsbody.UI.Portal/Pages/Settings/GoogleAccountsPage.fs
let view (model: Model) (dispatch: Msg -> unit) =
    MudTable'' {
        Items model.Accounts
        Loading model.IsLoading
        ToolBarContent (MudButton'' {
            OnClick (fun _ -> dispatch LinkClicked)
            "Link Google account"
        })
        RowTemplate (fun (row: AccountRow) -> fragment {
            MudTd''{ row.Address }
            MudTd''{ row.DisplayName }
            MudTd''{ row.LinkedOn }
        })
    }
```

Two bugs that the credentials page used to have become *unwritable* here rather than merely fixed. The table not refreshing after a write: `LinkSucceeded` **is** the refresh, and the compiler won't let you handle a message without returning a model. The half-wired dialog callback: the dialog is `DraftAddress`, a field like any other, so there is no callback to leave dangling.

Both were repaired by hand in the startup-composition-root change. The difference MVU offers is that nothing has to remember to repair them again on the next page.

**Documentation** — Excellent and free. Official Elmish site, the Elm guide (the origin of the pattern, unusually well written), and the maintained Elmish.WPF project for .NET desktop.

**Good** — Genuinely functional, independent of the other options, and can be adopted one page at a time.
**Bad** — Helps only the UI. Fun.Blazor's adaptive state is already partway there, and the credentials page now has an explicit error channel and reloads after a write, so this is a tightening of discipline rather than a rescue.
**Effort** — Medium, and incremental.

---

## Side by side

| Option | Documentation | Primary source | Fixes DB leak | Fixes duplicate copies | Fixes UI bugs | Effort |
|---|---|---|---|---|---|---|
| 1. Dependency rejection + parameterization | Excellent, free | *F# for Fun and Profit* series | **Yes** | Partly | No | Medium |
| 2. Functional onion / DMMF | Excellent, book | *Domain Modeling Made Functional* | **Yes** | **Yes** | No | Medium–large |
| 3. MVU / Elmish | Excellent, free | Elmish docs + Elm guide | No | No | Already largely addressed | Medium |

---

## Before any of them

Delete the record types that are pure duplicates. This isn't an architecture — it's housekeeping — but it's small, safe, and it makes whichever option you pick far easier to see. Guidance in the field is blunt on this: layers of data objects mirroring each other one-to-one add complexity without adding safety.

The startup composition root removed one hop of this already — the UI no longer maps into a core
DTO by hand — but the five hops between `Startup` and LiteDB are untouched.

---

## The path taken

Option 2 was chosen outright rather than reached through Option 1. The two are not rivals — Option 1 is
the technique, Option 2 the building it sits in — so nothing in Option 1 is skipped; it arrives as part
of the larger shape.

0. ~~**Give the composition layer an honest shape.**~~ Done — see `docs/changes/startup-composition-root/`. The build and the test suite are green again, which is what makes the rest of this list safe to attempt.
1. **Create `MyDogsbody.Domain`.** Empty of features, but referencing nothing, with `Result.fs` in it. The first feature-bearing change fills it.
2. **Migrate as you touch.** New work goes in the new shape. Existing credentials code moves a piece at a time, each piece its own change folder — not one rewrite.
3. **Retire `MyDogsbody.Spine`** when nothing routes through it any more, and delete its hop tests in the same change that deletes the hops.
4. ~~**Option 3.**~~ Rejected — the UI keeps `cval`/`aval`. See CLAUDE-project.md → *UI* for why.

---

## Considered and set aside

**Reader monad** — Functional and documented, but the author of the main F# reference advises against it "unless you can see a clear benefit over the other techniques." It gets awkward once several effects are in play. Covered as part 3 of the same series, if you want to judge for yourself.

**Free monad / tagless final ("dependency interpretation")** — Fully functional and intellectually appealing: calls to dependencies become data, interpreted later. But it costs roughly 100+ lines of scaffolding for a small instruction set, and the F# material is thin and advanced. Recommended only where nothing else works.

**Vertical slice architecture** — Organising by feature rather than by layer is a sound idea, and its documentation is good. It is *dropped here on the first criterion*: it is a code-organisation pattern from the object-oriented .NET world, not a functional architecture, and its literature assumes C# web APIs. The good part of it — grouping a feature's code together — comes free with Option 2's workflow pipelines.

**Clean / hexagonal architecture (the classic form)** — Well documented, but object-oriented at its core: interfaces, injected classes, containers. The functional equivalent is Option 2, which reaches the same goal with functions instead of interfaces.

---

## Sources

**Dependency handling**
- [Six approaches to dependency injection — F# for Fun and Profit](https://fsharpforfunandprofit.com/posts/dependencies/)
- [Revisiting the six approaches (recommendations) — F# for Fun and Profit](https://fsharpforfunandprofit.com/posts/dependencies-5/)
- [Dependency injection using parameters](https://fsharpforfunandprofit.com/posts/dependencies-2/)
- [Dependency injection using the Reader monad](https://fsharpforfunandprofit.com/posts/dependencies-3/)
- [Dependency rejection — Mark Seemann](https://blog.ploeh.dk/2017/02/02/dependency-rejection/)

**Functional core, imperative shell**
- [Functional Core, Imperative Shell — Kenneth Lange](https://kennethlange.com/functional-core-imperative-shell/)
- [Functional Core, Imperative Shell — functional-architecture.org](https://functional-architecture.org/functional_core_imperative_shell/)
- [Applying Functional Core and Imperative Shell in Practice — Rico Fritzsche](https://ricofritzsche.me/applying-functional-core-and-imperative-shell-in-practice/)

**Functional onion / DDD (Option 2)**

*The book and its code*
- [Domain Modeling Made Functional — Pragmatic Bookshelf](https://pragprog.com/titles/swdddf/domain-modeling-made-functional/) — Scott Wlaschin, 2018. The primary source.
- [Free extract, "Functions Are Things" (PDF)](https://media.pragprog.com/titles/swdddf/functions.pdf) — an extract from the *Understanding Functions* chapter, not the whole chapter. The publisher also offers the preface and extracts from chapters 2 and 5, all without purchase.
- [swlaschin/DomainModelingMadeFunctional](https://github.com/swlaschin/DomainModelingMadeFunctional) — the book's own code. `src/OrderTaking` is a complete bounded context, `src/OrderTakingEvolved` the same after the Evolution chapter. **The nearest thing to a reference implementation of the folder layout sketched in Option 2.**
- [swlaschin/DmmfWorkshop](https://github.com/swlaschin/DmmfWorkshop) — the full two-day course, code and notes, free.
- [Workshop outline](https://gist.github.com/swlaschin/c7a3f258cdb9fd7d5cf72cbaad3d6e1d) — the better of the two gists as a reading order; the [workshop description](https://gist.github.com/swlaschin/e7886301f11a49c2a1ab62d3fbd311bd) is the companion.

*Types first — the material behind `AccountTypes.fs`*

The free posts teach the ideas; the two specific shapes the sketch uses come from the book's own
sample code, which is also free. Read them as a pair.

- [The "designing with types" series](https://fsharpforfunandprofit.com/series/designing-with-types/) — eight posts, free
- [Single case union types](https://fsharpforfunandprofit.com/posts/designing-with-types-single-case-dus/) — why wrapping a primitive adds meaning. Note it does **not** use a `private` constructor: it reaches the same end with a `_T` naming convention and `.fsi` signature files
- [`Common.SimpleTypes.fs`](https://github.com/swlaschin/DomainModelingMadeFunctional/blob/master/src/OrderTaking/Common.SimpleTypes.fs) — where `type EmailAddress = private EmailAddress of string` plus a module `create` actually comes from, alongside `String50`, `ZipCode` and `OrderId`. The source for that half of `AccountTypes.fs`
- [Constrained strings](https://fsharpforfunandprofit.com/posts/designing-with-types-more-semantic-types/) — validation attached to the type rather than re-run at each layer
- [Making illegal states unrepresentable](https://fsharpforfunandprofit.com/posts/designing-with-types-making-illegal-states-unrepresentable/) — the principle. Its worked example is a "must have an email or a postal address" rule, not a validation pipeline
- [`PlaceOrder.PublicTypes.fs`](https://github.com/swlaschin/DomainModelingMadeFunctional/blob/master/src/OrderTaking/PlaceOrder.PublicTypes.fs) — `UnvalidatedOrder` → `PricedOrder`, with `ValidatedOrder` kept internal to `PlaceOrder.Implementation.fs`. The model for the `UnlinkedAccount` / `ValidAccount` / `LinkedAccount` split, and for declaring dependencies as function types rather than interfaces

*Workflows as pipelines*
- [Railway Oriented Programming](https://fsharpforfunandprofit.com/rop/) — the two-track model, `bind` and composition. A talk with slides and video; it does not present the `result { let! ... }` form
- [Against Railway-Oriented Programming (when used thoughtlessly)](https://fsharpforfunandprofit.com/posts/against-railway-oriented-programming/) — the same author on where it stops paying: keep `Result` for errors the domain expects and stakeholders can name, leave the rest to exceptions. Read it with the above, not instead of it
- [`PlaceOrder.Implementation.fs`](https://github.com/swlaschin/DomainModelingMadeFunctional/blob/master/src/OrderTaking/PlaceOrder.Implementation.fs) — the pipeline as the book writes it: five dependencies as leading parameters, input last, `asyncResult { }` at the top
- [FsToolkit.ErrorHandling](https://github.com/demystifyfp/FsToolkit.ErrorHandling) · [docs](https://demystifyfp.gitbook.io/fstoolkit-errorhandling) — supplies the `result` builder, which FSharp.Core does not. The book's sample declines the dependency and hand-writes one in [`Result.fs`](https://github.com/swlaschin/DomainModelingMadeFunctional/blob/master/src/OrderTaking/Result.fs)

*The "onion" — it is the book's own term*

Chapter 3, *A Functional Architecture*, is where the book lays this out, and Wlaschin uses "onion
architecture" for it directly.

- [A primer on functional architecture — Increment](https://increment.com/software-architecture/primer-on-functional-architecture/) — **Wlaschin's own article, adapted from chapter 3**, and the closest free stand-in for it: "You don't need to encourage (or nag!) developers to use an onion architecture; it happens automatically as a side effect of the FP approach"
- [Functional architecture is Ports and Adapters — Mark Seemann](https://blog.ploeh.dk/2016/03/18/functional-architecture-is-ports-and-adapters/) — the same argument from the other direction: purity gives you the architecture for free, rather than interfaces enforcing it. Seemann treats onion, hexagonal and ports-and-adapters as names for one thing
- [Functional architecture: a definition — Mark Seemann](https://blog.ploeh.dk/2018/11/19/functional-architecture-a-definition/)

*Talks — about an hour, worth watching before buying the book*
- [Domain Modeling Made Functional — NDC](https://www.youtube.com/watch?v=Up7LcbGZFuo) · [Explore DDD 2019](https://www.youtube.com/watch?v=PLFl95c-IiU) · [2023 version](https://www.youtube.com/watch?v=MlPQ0FsPxPY)
- [NDC session page](https://ndcconferences.com/slot/domain-modeling-made-functional)

*Other worked samples*
- [F# onion architecture sample — Ronnie Holm](https://github.com/ronnieholm/FSharp-onion-architecture-sample)
- [parthopdas/swdddf](https://github.com/parthopdas/swdddf) — an independent working-through of the book's source

**MVU**
- [Elmish — official documentation](https://elmish.github.io/elmish/)
- [Elmish.WPF — static WPF views for Elmish programs](https://github.com/elmish/Elmish.WPF)
- [UI programming with Elmish in F# — Compositional IT](https://www.compositional-it.com/news-blog/ui-programming-with-elmish-in-f/)

**On excess mapping**
- [DTOs & Mapping: The Good, The Bad, And The Excessive — CodeOpinion](https://codeopinion.com/dtos-mapping-the-good-the-bad-and-the-excessive/)
