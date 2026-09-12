module MyDogsbody.Tests.UI.Components.GoogleAccountsComponentsTests

open Xunit
open MyDogsbody.UI.Types
open MyDogsbody.UI.Portal.Components

// requirements.md -> Edge cases: "WHEN an account has no calendars at all THE SYSTEM SHALL show an
// empty picker with a message, not an error." The picker is populated from
// `CalendarsByAccountIdAval`, which gains an account's entry only once that account's calendars
// have loaded - so "loaded, and empty" is an entry holding [], and "not loaded" (still loading, or
// the fetch failed, which has its own alert) is no entry at all. Only the first earns the message.

let private aCalendar id name isPrimary : CalendarUiType = { Id = id; Name = name; IsPrimary = isPrimary }

[<Fact; Trait("Level", "Unit")>]
let ``noCalendarsMessage says so for an account whose calendars loaded and are empty`` () =
    let calendarsByAccountId = Map.ofList [ "acc-1", [] ]

    Assert.Equal(
        Some "No calendars were found for this account.",
        GoogleAccountsComponents.noCalendarsMessage calendarsByAccountId "acc-1"
    )

[<Fact; Trait("Level", "Unit")>]
let ``noCalendarsMessage says nothing for an account whose calendars have not loaded`` () =
    // Not loaded yet, or the fetch failed - a failure is already reported by the page's MudAlert,
    // and "no calendars" would be a claim nobody has checked.
    Assert.Equal(None, GoogleAccountsComponents.noCalendarsMessage Map.empty "acc-1")

[<Fact; Trait("Level", "Unit")>]
let ``noCalendarsMessage says nothing for an account with calendars`` () =
    let calendarsByAccountId = Map.ofList [ "acc-1", [ aCalendar "cal-1" "Personal" true ] ]

    Assert.Equal(None, GoogleAccountsComponents.noCalendarsMessage calendarsByAccountId "acc-1")

[<Fact; Trait("Level", "Unit")>]
let ``noCalendarsMessage reads only that account's own calendars`` () =
    // Each account's picker is populated from that account's own calendars (requirements.md), so
    // another account's empty list must not put the message on this one, nor the reverse.
    let calendarsByAccountId =
        Map.ofList [ "acc-empty", []; "acc-full", [ aCalendar "cal-1" "Personal" true ] ]

    Assert.Equal(None, GoogleAccountsComponents.noCalendarsMessage calendarsByAccountId "acc-full")

    Assert.Equal(
        Some "No calendars were found for this account.",
        GoogleAccountsComponents.noCalendarsMessage calendarsByAccountId "acc-empty"
    )

// requirements.md -> Removing and re-authorising: "WHEN a user removes an account THE SYSTEM SHALL
// say that access is still granted at Google and can be revoked there - so the user is not left
// believing more happened than did" (Q3.6), and User interface: "ask for confirmation, stating that
// access remains granted at Google".

let private anAccount id email : GoogleAccountUiType =
    { Id = id; EmailAddress = email; DefaultInvoiceCalendarId = None; NeedsReauthorisation = false }

[<Fact; Trait("Level", "Unit")>]
let ``removeConfirmationMessage names the account and says access remains granted at Google`` () =
    Assert.Equal(
        "Remove 'person@gmail.com'? Its local token and record are deleted, but access remains granted at Google - you can revoke it there.",
        GoogleAccountsComponents.removeConfirmationMessage (anAccount "acc-1" "person@gmail.com")
    )

[<Fact; Trait("Level", "Unit")>]
let ``removeConfirmationMessage names the account by its email, not its opaque id`` () =
    // Accounts are told apart on screen by email address (requirements.md), so the confirmation
    // must say which one is going in the same terms.
    let message =
        GoogleAccountsComponents.removeConfirmationMessage (anAccount "507f1f77bcf86cd799439011" "other@gmail.com")

    Assert.Contains("'other@gmail.com'", message)
    Assert.DoesNotContain("507f1f77bcf86cd799439011", message)
