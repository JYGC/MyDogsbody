module MyDogsbody.Tests.E2E.GoogleAccountsFlowTests

open Xunit
open Bunit
open Fun.Blazor
open MudBlazor
open Microsoft.Extensions.DependencyInjection
open MyDogsbody.Domain.Calendar
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.Tests.E2E.GoogleAccountsTestHarness

// User-visible flows, driven through a rendered component down to a real temp LiteDB file and
// back into what the component renders. Everything between is the real thing: the module
// creator, the API record, the domain workflows, the LiteDB store. Only the consent flow and the
// calendar list are faked - the same seam GoogleAuthorizationTests/GoogleCalendarClientTests
// exercise directly, so no test here opens a browser or reaches the network.

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private aCalendar id name isPrimary : AvailableCalendar =
    { Id = CalendarId.create id |> valueOrFail; Name = CalendarName.create name |> valueOrFail; IsPrimary = isPrimary }

/// Renders the Google accounts browser over the harness's real API, inside the two providers its
/// MudBlazor parts render through: a MudPopoverProvider for the calendar picker (a MudSelect, the
/// same way InvoicesFlowTests renders its window picker) and a MudDialogProvider for the remove
/// confirmation (a message box renders no markup without one, as SuppliersFlowTests found for its
/// editor dialog).
///
/// A flow chooses how work runs (`startWork`) and what a row's "Remove" button does (`onRemove`,
/// handed the module).
let private renderBrowserWith
    (startWork: (unit -> unit) -> unit)
    (onRemove: MyDogsbody.UI.Types.Module.GoogleAccountsBrowserModule -> MyDogsbody.UI.Types.GoogleAccountUiType -> unit)
    (harness: GoogleAccountsHarness)
    =
    let browserModule = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule startWork harness.Api

    let view = GoogleAccountsComponents.googleAccountsBrowser browserModule (onRemove browserModule)

    let wrapped =
        fragment {
            MudPopoverProvider''
            MudDialogProvider''
            view
        }

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", wrapped)
            builder.CloseComponent())

    browserModule, rendered

/// Skips the confirmation and removes straight away - for the flows about what removal does rather
/// than what the user is asked first.
let private removeWithoutConfirming
    (browserModule: MyDogsbody.UI.Types.Module.GoogleAccountsBrowserModule)
    (account: MyDogsbody.UI.Types.GoogleAccountUiType)
    =
    browserModule.RemoveAccount account.Id

/// Production's confirmation, shown through the harness's own dialog service.
let private confirmFirst
    (harness: GoogleAccountsHarness)
    (browserModule: MyDogsbody.UI.Types.Module.GoogleAccountsBrowserModule)
    (account: MyDogsbody.UI.Types.GoogleAccountUiType)
    =
    GoogleAccountsComponents.confirmAndRemove
        (harness.Services.GetRequiredService<IDialogService>())
        browserModule.RemoveAccount
        account

/// Work on the calling thread and removal without the dialog - what most flows need.
let private renderBrowser (harness: GoogleAccountsHarness) =
    renderBrowserWith (fun work -> work ()) removeWithoutConfirming harness

/// Work handed off where it is started, the way production's `startWork` (`Async.Start`) hands it
/// to the thread pool - then run by the test itself, on its own thread, when it calls
/// `runHandedOffWork`. `pendingWork` says how much is waiting.
///
/// The two confirmation flows need this rather than `fun work -> work ()`. MudBlazor completes a
/// message box's result on the renderer's dispatcher and the code awaiting it resumes right there,
/// so work run where it is started would run the removal - the store, the reload, every re-render -
/// on the dispatcher. bUnit runs each `WaitForAssertion` check on that same dispatcher, with a
/// one-second timeout. Whenever the dispatcher was busy as "Remove" was clicked, the click was
/// queued instead of running on the test's thread, `Click()` returned at once, and the check waited
/// behind the whole removal: under the full suite's load it failed with "Check count: 0", the check
/// never having run at all.
let private handOffWork () =
    let handedOff = new System.Collections.Concurrent.BlockingCollection<unit -> unit>()

    let startWork (work: unit -> unit) = handedOff.Add work

    /// Waits for work to be handed off, then runs it - and whatever it hands off in turn - here.
    let runHandedOffWork () =
        use timeout = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds 10.0)

        let first =
            try
                handedOff.Take(timeout.Token)
            with :? System.OperationCanceledException ->
                failwith "No work was handed off within ten seconds."

        let rec runFrom (work: unit -> unit) =
            work ()

            match handedOff.TryTake() with
            | true, next -> runFrom next
            | _ -> ()

        runFrom first

    let pendingWork () = handedOff.Count

    startWork, runHandedOffWork, pendingWork

/// The one button, among those `selector` matches, whose label is exactly `label`.
let private buttonLabelled (selector: string) (label: string) (rendered: IRenderedFragment) =
    Assert.Single(rendered.FindAll(selector) |> Seq.filter (fun button -> button.TextContent.Trim() = label))

/// The page's error alerts - a filled, error-severity MudAlert. Asserted on as elements, not through
/// the page's text: the information panel shown whenever no client secret is stored says "No Google
/// client secret has been supplied yet." in the very words the refusal uses, so the text alone
/// cannot tell whether the error alert rendered.
let private errorAlerts (rendered: IRenderedFragment) =
    rendered.FindAll(".mud-alert-filled-error") |> List.ofSeq

/// What the store actually holds, read through the API rather than the rendered table.
let private storedAccountCount (harness: GoogleAccountsHarness) =
    match harness.Api.GetAccounts() with
    | Ok accounts -> List.length accounts
    | Error ex -> failwith ex.Message

[<Fact; Trait("Level", "E2E")>]
let ``registering an account shows it marked not ready, with no default calendar chosen`` () =
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let browserModule, rendered = renderBrowser harness

        browserModule.RegisterAccount()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("person@gmail.com", rendered.Markup)
            Assert.Contains("Not ready - no calendar chosen", rendered.Markup))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``choosing a default calendar makes a not-ready account ready`` () =
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let browserModule, rendered = renderBrowser harness

        browserModule.RegisterAccount()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Not ready - no calendar chosen", rendered.Markup))

        browserModule.SetDefaultInvoiceCalendar "507f1f77bcf86cd799439011" "cal-1"

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Ready", rendered.Markup)
            Assert.DoesNotContain("Not ready", rendered.Markup)
            Assert.DoesNotContain("No calendars were found for this account.", rendered.Markup))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``an account with no calendars stays not ready, with a reason, and no error is logged`` () =
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    // No calendars at all - requirements.md's edge case: "an empty picker with a message, not an
    // error."
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let browserModule, rendered = renderBrowser harness

        browserModule.RegisterAccount()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Not ready - no calendar chosen", rendered.Markup)
            Assert.Contains("No calendars were found for this account.", rendered.Markup)
            Assert.Contains("person@gmail.com", rendered.Markup))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``removing an account makes its row disappear`` () =
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let browserModule, rendered = renderBrowser harness

        browserModule.RegisterAccount()
        rendered.WaitForAssertion(fun () -> Assert.Contains("person@gmail.com", rendered.Markup))

        browserModule.RemoveAccount "507f1f77bcf86cd799439011"

        rendered.WaitForAssertion(fun () -> Assert.DoesNotContain("person@gmail.com", rendered.Markup))
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``removing an account asks first, saying access remains granted at Google, and removes on a yes`` () =
    // requirements.md: "WHEN a user removes an account THE SYSTEM SHALL ask for confirmation, stating
    // that access remains granted at Google" - and "SHALL say that access is still granted at Google
    // and can be revoked there" (Q3.6). Driven through the row's own button and the real message box.
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let startWork, runHandedOffWork, pendingWork = handOffWork ()
        let browserModule, rendered = renderBrowserWith startWork (confirmFirst harness) harness

        runHandedOffWork ()
        browserModule.RegisterAccount()
        runHandedOffWork ()
        rendered.WaitForAssertion(fun () -> Assert.Contains("person@gmail.com", rendered.Markup))

        (buttonLabelled "td button" "Remove" rendered).Click()

        rendered.WaitForAssertion(fun () ->
            let dialog = rendered.Find(".mud-dialog")
            Assert.Contains("Remove 'person@gmail.com'?", dialog.TextContent)
            Assert.Contains("access remains granted at Google - you can revoke it there.", dialog.TextContent))

        // Asking removed nothing, and started nothing.
        Assert.Equal(0, pendingWork ())
        Assert.Equal(1, storedAccountCount harness)

        (buttonLabelled ".mud-dialog button" "Remove" rendered).Click()

        // The yes hands the removal off; it runs here, on the test's own thread.
        runHandedOffWork ()

        rendered.WaitForAssertion(fun () ->
            Assert.Empty(rendered.FindAll(".mud-dialog"))
            Assert.DoesNotContain("person@gmail.com", rendered.Markup))

        Assert.Equal(0, storedAccountCount harness)
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``cancelling the remove confirmation keeps the account`` () =
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let startWork, runHandedOffWork, pendingWork = handOffWork ()
        let browserModule, rendered = renderBrowserWith startWork (confirmFirst harness) harness

        runHandedOffWork ()
        browserModule.RegisterAccount()
        runHandedOffWork ()
        rendered.WaitForAssertion(fun () -> Assert.Contains("person@gmail.com", rendered.Markup))

        (buttonLabelled "td button" "Remove" rendered).Click()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("access remains granted at Google", rendered.Find(".mud-dialog").TextContent))

        (buttonLabelled ".mud-dialog button" "Cancel" rendered).Click()

        rendered.WaitForAssertion(fun () -> Assert.Empty(rendered.FindAll(".mud-dialog")))

        // The answer is acted on before the dialog closes - MudBlazor completes the dialog's result,
        // and the code awaiting it resumes, before it removes the dialog - so a removal would already
        // have been handed off by now.
        Assert.Equal(0, pendingWork ())
        Assert.Contains("person@gmail.com", rendered.Markup)
        Assert.Equal(1, storedAccountCount harness)
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``adding an account shows it is in progress until the consent flow finishes`` () =
    // requirements.md: "WHEN a user presses "Add account" THE SYSTEM SHALL start the consent flow and
    // show that it is in progress", and "WHEN authorisation is running THE SYSTEM SHALL NOT block the
    // user interface". Work is queued, the way the page's Async.Start leaves it, so the page can be
    // read while the consent flow has still to run.
    let queued = System.Collections.Generic.Queue<unit -> unit>()

    let rec drain () =
        match queued.TryDequeue() with
        | true, work ->
            work ()
            drain ()
        | _ -> ()

    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    let listCalendars: ListCalendars = fun _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-secret" |> ignore
        let browserModule, rendered = renderBrowserWith (fun work -> queued.Enqueue work) removeWithoutConfirming harness

        drain ()
        rendered.WaitForAssertion(fun () -> Assert.False((buttonLabelled "button" "Add account" rendered).HasAttribute "disabled"))

        browserModule.RegisterAccount()

        rendered.WaitForAssertion(fun () ->
            Assert.True((buttonLabelled "button" "Adding account..." rendered).HasAttribute "disabled"))

        Assert.DoesNotContain("person@gmail.com", rendered.Markup)

        drain ()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("person@gmail.com", rendered.Markup)
            Assert.False((buttonLabelled "button" "Add account" rendered).HasAttribute "disabled"))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``a failure is shown as an alert, cleared by the next success`` () =
    let authoriseAccount: AuthoriseAccount = fun () -> failwith "must not be called - no client secret is set"
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        let browserModule, rendered = renderBrowser harness

        // requirements.md: "WHEN no client secret has been supplied THE SYSTEM SHALL say so and
        // disable account registration" - and nothing has failed yet.
        rendered.WaitForAssertion(fun () -> Assert.True((buttonLabelled "button" "Add account" rendered).HasAttribute "disabled"))
        Assert.Empty(errorAlerts rendered)

        // The button cannot be pressed, but the refusal does not rest on it: the workflow itself
        // refuses, so the fake authoriser above is never called.
        browserModule.RegisterAccount()

        rendered.WaitForAssertion(fun () ->
            let alert = Assert.Single(errorAlerts rendered)
            Assert.Contains("No Google client secret has been supplied yet.", alert.TextContent))

        browserModule.SetClientSecret "the-secret"

        rendered.WaitForAssertion(fun () ->
            Assert.Empty(errorAlerts rendered)
            Assert.False((buttonLabelled "button" "Add account" rendered).HasAttribute "disabled"))

        Assert.Empty harness.Logged)

let private replacementWarning = "Replacing the client secret may mean existing accounts need re-authorising."

[<Fact; Trait("Level", "E2E")>]
let ``replacing a stored client secret states that existing accounts may need re-authorising`` () =
    // requirements.md: "WHEN a user replaces the client secret THE SYSTEM SHALL ... state that
    // existing accounts may need re-authorising." A replacement can belong to a different OAuth
    // client, and a token Google issued to the old client will not refresh under the new one.
    let authoriseAccount: AuthoriseAccount = fun () -> failwith "must not be called - nothing is registered here"
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        harness.Api.SetClientSecret "the-first-secret" |> ignore
        let browserModule, rendered = renderBrowser harness

        rendered.WaitForAssertion(fun () -> Assert.Contains("the-first-secret", rendered.Markup))
        // Read-only: nothing is being replaced yet.
        Assert.DoesNotContain(replacementWarning, rendered.Markup)

        browserModule.StartEditingClientSecret()

        rendered.WaitForAssertion(fun () -> Assert.Contains(replacementWarning, rendered.Markup))
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``supplying the first client secret does not warn about existing accounts`` () =
    // No secret means no account could have been registered, so there is nothing to re-authorise.
    let authoriseAccount: AuthoriseAccount = fun () -> failwith "must not be called - nothing is registered here"
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        let browserModule, rendered = renderBrowser harness

        browserModule.StartEditingClientSecret()

        rendered.WaitForAssertion(fun () -> Assert.Contains("Client secret (JSON)", rendered.Markup))
        Assert.DoesNotContain(replacementWarning, rendered.Markup)
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``correcting a malformed client secret fills the calendar picker it had left empty`` () =
    // requirements.md: "WHEN the stored client secret is malformed THE SYSTEM SHALL report that", and
    // the report clears on the next success. The correction is that success - and it must also
    // bring back what the malformed secret stopped, or the picker is left empty with nothing saying
    // why.
    let authoriseAccount: AuthoriseAccount =
        fun () -> Ok(GoogleEmail.create "person@gmail.com" |> valueOrFail, GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail)

    // Stands in for the real adapter, which parses the stored secret before calling Google.
    let storedSecret: (unit -> string option) ref = ref (fun () -> None)

    let listCalendars: ListCalendars =
        fun _ ->
            match storedSecret.Value () with
            | Some "not-json" -> Error(ClientSecretInvalid "The stored Google client secret is malformed.")
            | _ -> Ok [ aCalendar "cal-1" "Invoices" true ]

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        storedSecret.Value <-
            fun () ->
                match harness.Api.GetClientSecret() with
                | Ok secret -> secret
                | Error ex -> failwith ex.Message

        harness.Api.SetClientSecret "the-secret" |> ignore
        harness.Api.RegisterAccount() |> ignore
        harness.Api.SetClientSecret "not-json" |> ignore

        let browserModule, rendered = renderBrowser harness

        rendered.WaitForAssertion(fun () ->
            let alert = Assert.Single(errorAlerts rendered)
            Assert.Contains("The stored Google client secret is malformed.", alert.TextContent))

        browserModule.StartEditingClientSecret()
        browserModule.SetClientSecret "the-corrected-secret"

        rendered.WaitForAssertion(fun () ->
            Assert.Empty(errorAlerts rendered)
            Assert.Contains("the-corrected-secret", rendered.Markup)

            let calendars =
                FSharp.Data.Adaptive.AVal.force browserModule.CalendarsByAccountIdAval
                |> Map.tryFind "507f1f77bcf86cd799439011"

            Assert.Equal<MyDogsbody.UI.Types.CalendarUiType list option>(
                Some [ ({ Id = "cal-1"; Name = "Invoices"; IsPrimary = true }: MyDogsbody.UI.Types.CalendarUiType) ],
                calendars
            ))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``saving the client secret field blank says so, and registration stays disabled`` () =
    // requirements.md: "WHEN no client secret has been supplied THE SYSTEM SHALL say so and disable
    // account registration, rather than failing at the authorisation call." Pressing Save on the
    // empty field supplies nothing. Stored, it read back as a secret: "Add account" was enabled and
    // registering failed at the authorisation call as "malformed". Driven through the page's own
    // buttons, work handed off to this thread (see `handOffWork`).
    let authoriseAccount: AuthoriseAccount = fun () -> failwith "must not be called - no client secret is stored"
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        let startWork, runHandedOffWork, _ = handOffWork ()
        let _, rendered = renderBrowserWith startWork removeWithoutConfirming harness

        runHandedOffWork ()
        (buttonLabelled "button" "Add client secret" rendered).Click()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Client secret (JSON)", rendered.Markup))

        (buttonLabelled "button" "Save" rendered).Click()
        runHandedOffWork ()

        rendered.WaitForAssertion(fun () ->
            let alert = Assert.Single(errorAlerts rendered)
            Assert.Contains("Google client secret must not be empty.", alert.TextContent)
            Assert.True((buttonLabelled "button" "Add account" rendered).HasAttribute "disabled"))

        match harness.Api.GetClientSecret() with
        | Ok stored -> Assert.Equal(None, stored)
        | Error ex -> failwith ex.Message

        Assert.Empty harness.Logged)
