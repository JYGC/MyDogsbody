module MyDogsbody.Tests.Domain.Calendar.SetClientSecretWorkflowTests

open Xunit
open MyDogsbody.Domain.Calendar

/// A client secret as Google's console downloads it, with the surrounding whitespace a paste
/// brings along. Placeholder project number, per the fixtures' rule.
let private pastedSecret =
    "  { \"installed\": { \"client_id\": \"000000000000-test.apps.googleusercontent.com\", \"client_secret\": \"test-secret\" } }\r\n"

let private blankRefusal = ClientSecretInvalid "Google client secret must not be empty."

[<Fact; Trait("Level", "Unit")>]
let ``setClientSecret saves the pasted secret exactly as given`` () =
    let received = ResizeArray<string>()

    let save: SaveClientSecret =
        fun secret ->
            received.Add secret
            Ok()

    let actual = SetClientSecretWorkflow.setClientSecret save pastedSecret

    Assert.Equal(Ok(), actual)
    // Verbatim - the surrounding whitespace included. The JSON parser ignores it, and the store
    // keeps a secret byte-for-byte, so nothing here trims it either.
    Assert.Equal<string list>([ pastedSecret ], List.ofSeq received)

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\r\n\t")>]
let ``setClientSecret refuses a blank secret and never reaches the store`` (secret: string) =
    // requirements.md: "WHEN no client secret has been supplied THE SYSTEM SHALL say so and disable
    // account registration". A blank save supplies nothing, so it must not be stored as though it
    // had - and it must not overwrite a secret that works.
    let received = ResizeArray<string>()

    let save: SaveClientSecret =
        fun value ->
            received.Add value
            Ok()

    let actual = SetClientSecretWorkflow.setClientSecret save secret

    Assert.Equal(Error blankRefusal, actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setClientSecret refuses a null secret and never reaches the store`` () =
    let received = ResizeArray<string>()

    let save: SaveClientSecret =
        fun value ->
            received.Add value
            Ok()

    let actual = SetClientSecretWorkflow.setClientSecret save null

    Assert.Equal(Error blankRefusal, actual)
    Assert.Empty received

[<Fact; Trait("Level", "Unit")>]
let ``setClientSecret reports a store failure as the store gave it`` () =
    let save: SaveClientSecret = fun _ -> Error(GoogleStoreFailed "Failed to save the Google client secret.")

    let actual = SetClientSecretWorkflow.setClientSecret save pastedSecret

    Assert.Equal(Error(GoogleStoreFailed "Failed to save the Google client secret."), actual)
