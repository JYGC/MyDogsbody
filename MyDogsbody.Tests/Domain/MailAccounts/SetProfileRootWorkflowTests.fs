module MyDogsbody.Tests.Domain.MailAccounts.SetProfileRootWorkflowTests

open Xunit
open MyDogsbody.Domain.MailAccounts

/// Records every save attempt, so "the store was never reached" is assertable.
let private recordingSave (outcome: ProfileRootPath -> Result<unit, MailAccountError>) =
    let received = ResizeArray<ProfileRootPath>()

    let save: SaveProfileRoot =
        fun path ->
            received.Add path
            outcome path

    save, received

[<Fact; Trait("Level", "Unit")>]
let ``setProfileRoot validates, persists and returns the chosen path`` () =
    let save, received = recordingSave (fun _ -> Ok ())
    let chosen = @"E:\Users\jygcn\AppData\Roaming\Thunderbird\Profiles\49stkd1y.default"

    let actual = SetProfileRootWorkflow.setProfileRoot save chosen

    match actual with
    | Ok path -> Assert.Equal(chosen, ProfileRootPath.value path)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

    let saved = Assert.Single received
    Assert.Equal(chosen, ProfileRootPath.value saved)

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``setProfileRoot rejects an empty path and never saves`` (entered: string) =
    let save, received = recordingSave (fun _ -> Ok ())

    let actual = SetProfileRootWorkflow.setProfileRoot save entered

    Assert.Equal(Error (ProfileRootInvalid "Profile root path must not be empty."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setProfileRoot rejects a relative path and never saves`` () =
    let save, received = recordingSave (fun _ -> Ok ())

    let actual = SetProfileRootWorkflow.setProfileRoot save @"Thunderbird\Profiles"

    Assert.Equal(Error (ProfileRootInvalid "Profile root path must be an absolute path."), actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setProfileRoot returns the store's failure unchanged`` () =
    let save, _ = recordingSave (fun _ -> Error (MailStoreFailed "disk full"))

    let actual = SetProfileRootWorkflow.setProfileRoot save @"C:\Profiles"

    Assert.Equal(Error (MailStoreFailed "disk full"), actual)
