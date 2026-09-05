module MyDogsbody.Tests.Domain.Calendar.ListGoogleAccountsWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private account emailValue : RegisteredGoogleAccount =
    {
        Id = GoogleAccountId.create $"account-{emailValue}" |> valueOrFail
        EmailAddress = GoogleEmail.create emailValue |> valueOrFail
        DefaultInvoiceCalendar = None
        NeedsReauthorisation = false
    }

[<Fact; Trait("Level", "Unit")>]
let ``listGoogleAccounts orders the accounts by email`` () =
    let unordered = [ account "zed@example.com"; account "amy@example.com"; account "mid@example.com" ]
    let load: ListGoogleAccounts = fun () -> Ok unordered

    let actual = ListGoogleAccountsWorkflow.listGoogleAccounts load ()

    match actual with
    | Ok accounts ->
        let emails = accounts |> List.map (fun a -> GoogleEmail.value a.EmailAddress)
        Assert.Equal<string list>([ "amy@example.com"; "mid@example.com"; "zed@example.com" ], emails)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``listGoogleAccounts returns an empty list as Ok []`` () =
    let load: ListGoogleAccounts = fun () -> Ok []

    let actual = ListGoogleAccountsWorkflow.listGoogleAccounts load ()

    Assert.Equal(Ok [], actual)

[<Fact; Trait("Level", "Unit")>]
let ``listGoogleAccounts returns the store's failure unchanged`` () =
    let load: ListGoogleAccounts = fun () -> Error (GoogleStoreFailed "disk full")

    let actual = ListGoogleAccountsWorkflow.listGoogleAccounts load ()

    Assert.Equal(Error (GoogleStoreFailed "disk full"), actual)
