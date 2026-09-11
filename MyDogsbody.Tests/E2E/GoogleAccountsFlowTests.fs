module MyDogsbody.Tests.E2E.GoogleAccountsFlowTests

open Xunit
open Bunit
open Fun.Blazor
open MudBlazor
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

/// Renders the Google accounts browser over the harness's real API, with the confirmation dialog
/// skipped - `removeWithoutConfirming` calls `RemoveAccount` directly, since bUnit has no system
/// dialog to click through and the confirmation wording itself belongs to a component test, not
/// this flow.
///
/// The calendar picker is a MudSelect, which renders through a popover - so the view is rendered
/// inside a MudPopoverProvider, the same way InvoicesFlowTests renders its window picker.
let private renderBrowser (harness: GoogleAccountsHarness) =
    let browserModule = GoogleAccountsBrowserModuleCreators.getGoogleAccountsBrowserModule (fun work -> work ()) harness.Api

    let removeWithoutConfirming (account: MyDogsbody.UI.Types.GoogleAccountUiType) =
        browserModule.RemoveAccount account.Id

    let view = GoogleAccountsComponents.googleAccountsBrowser browserModule removeWithoutConfirming

    let wrapped =
        fragment {
            MudPopoverProvider''
            view
        }

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", wrapped)
            builder.CloseComponent())

    browserModule, rendered

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
let ``a failure is shown as an alert, cleared by the next success`` () =
    let authoriseAccount: AuthoriseAccount = fun () -> failwith "must not be called - no client secret is set"
    let listCalendars: ListCalendars = fun _ -> Ok []

    withGoogleAccountsHarness authoriseAccount listCalendars (fun harness ->
        let browserModule, rendered = renderBrowser harness

        // No client secret has been supplied yet - the workflow itself refuses, so the fake
        // authoriser above is never called.
        browserModule.RegisterAccount()

        rendered.WaitForAssertion(fun () -> Assert.Contains("No Google client secret has been supplied yet", rendered.Markup))

        browserModule.SetClientSecret "the-secret"

        rendered.WaitForAssertion(fun () -> Assert.DoesNotContain("No Google client secret has been supplied yet", rendered.Markup))

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
