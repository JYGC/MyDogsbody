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

/// Records every discarded authorisation, so both "the stranded token was cleaned up" and "a
/// successful registration did not throw its own token away" are assertable.
let private recordingDiscard () =
    let discarded = ResizeArray<GoogleAccountId>()

    let discard: DiscardAuthorisation =
        fun accountId ->
            discarded.Add accountId
            Ok ()

    discard, discarded

/// The workflow never authorises in these tests, so a discard would mean it discarded something
/// that was never obtained.
let private unusedDiscard: DiscardAuthorisation =
    fun _ -> failwith "discardAuthorisation must not be called"

/// A save that refuses, and records whether it was reached at all.
let private failingSave (reason: string) =
    let attempted = ResizeArray<RegisteredGoogleAccount>()

    let save: SaveGoogleAccount =
        fun account ->
            attempted.Add account
            Error (GoogleStoreFailed reason)

    save, attempted

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount registers a newly authorised account with no default calendar`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok []
    let authorise: AuthoriseAccount = fun () -> Ok (email "new@example.com", accountId "new-account")
    let save, received = recordingSave ()
    let discard, discarded = recordingDiscard ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    match actual with
    | Ok registered ->
        Assert.Equal("new-account", GoogleAccountId.value registered.Id)
        Assert.Equal("new@example.com", GoogleEmail.value registered.EmailAddress)
        Assert.Equal(None, registered.DefaultInvoiceCalendar)
        Assert.False registered.NeedsReauthorisation
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    Assert.Single received |> ignore

    // The registration was accepted, so the token consent just wrote is the one this account
    // will use - discarding it would leave a registered account with no credential at all.
    Assert.Empty discarded

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount refuses when no client secret has been supplied, and never authorises`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok None
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> failwith "authoriseAccount must not be called"
    let save, received = recordingSave ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise unusedDiscard save ()

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
    let discard, discarded = recordingDiscard ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    Assert.Equal(Error (AccountAlreadyRegistered existing.EmailAddress), actual)
    Assert.Empty received

    // Consent has already written a token against the id authorise just minted. Refusing by
    // simply not saving would strand it: no account row would ever point at it, and only
    // removing an account deletes a token.
    Assert.Equal<string list>([ "some-other-id" ], discarded |> Seq.map GoogleAccountId.value |> List.ofSeq)

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount still reports the duplicate when discarding the stranded authorisation fails`` () =
    let existing = account "existing@example.com"
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok [ existing ]

    let authorise: AuthoriseAccount =
        fun () -> Ok (existing.EmailAddress, accountId "some-other-id")

    let discard: DiscardAuthorisation = fun _ -> Error (GoogleStoreFailed "the credential store is unreachable")
    let save, received = recordingSave ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    // A failed cleanup must not replace the answer the user needs. The discard adapter's own
    // handleError has already recorded why it failed.
    Assert.Equal(Error (AccountAlreadyRegistered existing.EmailAddress), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount discards the authorisation consent wrote when the account list cannot be read`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Error (GoogleStoreFailed "the account store is unreachable")

    let authorise: AuthoriseAccount =
        fun () -> Ok (email "new@example.com", accountId "minted-id")

    let save, received = recordingSave ()
    let discard, discarded = recordingDiscard ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    // The failure the user needs to see is the store's, unchanged by the cleanup.
    Assert.Equal(Error (GoogleStoreFailed "the account store is unreachable"), actual)
    Assert.Empty received

    // Consent completed before this step, so a token is already sitting in the credential store
    // keyed by the minted id. Nothing will ever point at it - only removing an account deletes a
    // token, and no account row was written - so it has to be discarded here or not at all.
    Assert.Equal<string list>([ "minted-id" ], discarded |> Seq.map GoogleAccountId.value |> List.ofSeq)

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount discards the authorisation consent wrote when the account cannot be saved`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok []

    let authorise: AuthoriseAccount =
        fun () -> Ok (email "new@example.com", accountId "minted-id")

    let save, attempted = failingSave "the account store is unreachable"
    let discard, discarded = recordingDiscard ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    Assert.Equal(Error (GoogleStoreFailed "the account store is unreachable"), actual)
    Assert.Single attempted |> ignore

    // `SaveGoogleAccount` returning Error means the account was not stored - that is the contract
    // the dependency type declares. So the registration produced nothing but a token, and the
    // token is exactly what has to go.
    Assert.Equal<string list>([ "minted-id" ], discarded |> Seq.map GoogleAccountId.value |> List.ofSeq)

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount still reports the save failure when discarding the stranded authorisation fails`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> Ok []

    let authorise: AuthoriseAccount =
        fun () -> Ok (email "new@example.com", accountId "minted-id")

    let save, _ = failingSave "the account store is unreachable"
    let discard: DiscardAuthorisation = fun _ -> Error (GoogleStoreFailed "the credential store is unreachable")

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise discard save ()

    // A failed cleanup must not replace the answer the user needs, exactly as on the duplicate
    // path. The discard adapter's own handleError has already recorded why it failed.
    Assert.Equal(Error (GoogleStoreFailed "the account store is unreachable"), actual)

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports a cancelled consent flow and saves nothing`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error AuthorisationCancelled
    let save, received = recordingSave ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise unusedDiscard save ()

    Assert.Equal(Error AuthorisationCancelled, actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports a failed authorisation with its reason`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error (AuthorisationFailed "loopback port in use")
    let save, received = recordingSave ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise unusedDiscard save ()

    Assert.Equal(Error (AuthorisationFailed "loopback port in use"), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``registerGoogleAccount reports an unavailable email and saves nothing`` () =
    let loadSecret: LoadClientSecret = fun () -> Ok (Some "secret")
    let listAccounts: ListGoogleAccounts = fun () -> failwith "listGoogleAccounts must not be called"
    let authorise: AuthoriseAccount = fun () -> Error AccountEmailUnavailable
    let save, received = recordingSave ()

    let actual =
        RegisterGoogleAccountWorkflow.registerGoogleAccount loadSecret listAccounts authorise unusedDiscard save ()

    Assert.Equal(Error AccountEmailUnavailable, actual)
    Assert.Empty received
