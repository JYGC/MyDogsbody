module MyDogsbody.Tests.Domain.Calendar.RemoveGoogleAccountWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private accountId value = GoogleAccountId.create value |> valueOrFail

[<Fact; Trait("Level", "Unit")>]
let ``removeGoogleAccount removes a known account`` () =
    let received = ResizeArray<GoogleAccountId>()

    let remove: RemoveGoogleAccount =
        fun id ->
            received.Add id
            Ok true

    let actual = RemoveGoogleAccountWorkflow.removeGoogleAccount remove "acc-1"

    Assert.Equal(Ok (), actual)
    Assert.Equal<GoogleAccountId list>([ accountId "acc-1" ], List.ofSeq received)

[<Fact; Trait("Level", "Unit")>]
let ``removeGoogleAccount reports an account that is not registered`` () =
    let remove: RemoveGoogleAccount = fun _ -> Ok false

    let actual = RemoveGoogleAccountWorkflow.removeGoogleAccount remove "unknown"

    Assert.Equal(Error (AccountNotRegistered (accountId "unknown")), actual)

[<Fact; Trait("Level", "Unit")>]
let ``removeGoogleAccount never attempts to revoke access at Google - only the local removal function is called`` () =
    // The dependency type is RemoveGoogleAccount = GoogleAccountId -> Result<bool, CalendarError>,
    // with no revoke-related dependency in this workflow's parameter list at all (Q3.6) - there is
    // nothing to call and nothing to fake, which is itself the guarantee.
    let mutable calls = 0
    let remove: RemoveGoogleAccount = fun _ -> calls <- calls + 1; Ok true

    RemoveGoogleAccountWorkflow.removeGoogleAccount remove "acc-1" |> ignore

    Assert.Equal(1, calls)

[<Fact; Trait("Level", "Unit")>]
let ``removeGoogleAccount rejects an empty id and never calls the removal function`` () =
    let mutable called = false
    let remove: RemoveGoogleAccount = fun _ -> called <- true; Ok true

    let actual = RemoveGoogleAccountWorkflow.removeGoogleAccount remove "   "

    Assert.Equal(Error (GoogleAccountIdInvalid "Google account id must not be empty."), actual)
    Assert.False called
