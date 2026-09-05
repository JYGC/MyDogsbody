module MyDogsbody.Tests.Domain.Calendar.RegisterGoogleAccountWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private email value = GoogleEmail.create value |> valueOrFail
let private accountId value = GoogleAccountId.create value |> valueOrFail

let private account emailValue : RegisteredGoogleAccount =
    {
        Id = accountId $"account-{emailValue}"
        EmailAddress = email emailValue
        DefaultInvoiceCalendar = None
        NeedsReauthorisation = false
    }

/// Records every save, so "the store was never reached" is assertable.
let private recordingSave () =
    let received = ResizeArray<RegisteredGoogleAccount>()

    let save: SaveGoogleAccount =
        fun account ->
            received.Add account
            Ok account

    save, received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount registers a newly authorised account with no default calendar`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok []
    let authorise: AuthoriseAccount = fun () -> Ok (email "new@example.com", accountId "new-account")
    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    match actual with
    | Ok registered ->
        Assert.Equal("new-account", GoogleAccountId.value registered.Id)
        Assert.Equal("new@example.com", GoogleEmail.value registered.EmailAddress)
        Assert.Equal(None, registered.DefaultInvoiceCalendar)
        Assert.False registered.NeedsReauthorisation
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount refuses when no client secret has been supplied, and never authorises`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok None
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> failwith "authoriseAccount must not be called"
    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    Assert.Equal(Error ClientSecretMissing, actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount refuses an already-registered account and never saves it again`` () =
    let existing = account "existing@example.com"
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]

    let authorise: AuthoriseAccount =
        fun () -> Ok (existing.EmailAddress, accountId "some-other-id")

    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    Assert.Equal(Error (AccountAlreadyRegistered existing.EmailAddress), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports a cancelled consent flow and saves nothing`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error AuthorisationCancelled
    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    Assert.Equal(Error AuthorisationCancelled, actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports a failed authorisation with its reason`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error (AuthorisationFailed "loopback port in use")
    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    Assert.Equal(Error (AuthorisationFailed "loopback port in use"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports an unavailable email and saves nothing`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error AccountEmailUnavailable
    let save, received = recordingSave ()

    let actual = RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise save ()

    Assert.Equal(Error AccountEmailUnavailable, actual)
    Assert.Empty received
