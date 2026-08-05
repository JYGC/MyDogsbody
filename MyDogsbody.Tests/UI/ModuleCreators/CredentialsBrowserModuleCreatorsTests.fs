module MyDogsbody.Tests.UI.ModuleCreators.CredentialsBrowserModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Enums
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Runs the work on the calling thread. Production passes an Async.Start equivalent; the
/// seam exists so a test never has to wait on a background thread.
let private runSynchronously (work: unit -> unit) = work ()

let private failure message =
    MyDogsbodyException("test.action", message, ApplicationException(message))

let private aCredential id =
    {
        Id = id
        InfrastructureType = InfrastructureType.Google
        Credentials = $"{id}-secret"
        Username = $"{id}@gmail.com"
    }

let private api getAll add edit : CredentialApi =
    {
        GetAllCredentials = getAll
        AddCredential = add
        EditCredential = edit
    }

[<Fact; Trait("Level", "Unit")>]
let ``the module loads the credentials when it is created`` () =
    // Arrange
    let stored = [ aCredential "aaa"; aCredential "bbb" ]
    let credentialApi = api (fun () -> Ok stored) (fun _ -> Ok()) (fun _ -> Ok())

    // Act
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    // Assert — every field, both rows
    let listed = AVal.force browser.CredentialsListAval
    Assert.Equal(2, listed.Length)
    Assert.Equal("aaa", (List.item 0 listed).Id)
    Assert.Equal(InfrastructureType.Google, (List.item 0 listed).InfrastructureType)
    Assert.Equal("aaa-secret", (List.item 0 listed).Credentials)
    Assert.Equal("aaa@gmail.com", (List.item 0 listed).Username)
    Assert.Equal("bbb", (List.item 1 listed).Id)

    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed load surfaces the message and stops loading`` () =
    // Arrange
    let credentialApi =
        api (fun () -> Error(failure "could not read credentials")) (fun _ -> Ok()) (fun _ -> Ok())

    // Act
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    // Assert — an error on screen, not an exception through the boundary
    Assert.Equal(Some "could not read credentials", AVal.force browser.ErrorAval)
    Assert.Empty(AVal.force browser.CredentialsListAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``AddCredential passes the credential through to the api`` () =
    // Arrange
    let added = ResizeArray<IntegrationCredentialUiTypeWithoutId>()
    let credentialApi =
        api (fun () -> Ok []) (fun credential -> added.Add credential; Ok()) (fun _ -> Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    let newCredential: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Microsoft
            Credentials = "new-secret"
            Username = "new@outlook.com"
        }

    // Act
    browser.AddCredential newCredential

    // Assert
    Assert.Single(added) |> ignore
    Assert.Equal(newCredential, added.[0])

[<Fact; Trait("Level", "Unit")>]
let ``a successful add reloads the list so the new row appears`` () =
    // Arrange — the store gains a row between the first load and the second
    let storedRows = ResizeArray<IntegrationCredentialUiType>()
    let credentialApi =
        api
            (fun () -> Ok(List.ofSeq storedRows))
            (fun _ -> storedRows.Add(aCredential "ccc"); Ok())
            (fun _ -> Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    Assert.Empty(AVal.force browser.CredentialsListAval)

    // Act
    browser.AddCredential
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = "ccc-secret"
            Username = "ccc@gmail.com"
        }

    // Assert
    let listed = AVal.force browser.CredentialsListAval
    Assert.Single(listed) |> ignore
    Assert.Equal("ccc", listed.Head.Id)

[<Fact; Trait("Level", "Unit")>]
let ``a failed add surfaces the message and leaves the list alone`` () =
    // Arrange
    let credentialApi =
        api
            (fun () -> Ok [ aCredential "aaa" ])
            (fun _ -> Error(failure "could not save credential"))
            (fun _ -> Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    // Act
    browser.AddCredential
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = "doomed"
            Username = "doomed@gmail.com"
        }

    // Assert
    Assert.Equal(Some "could not save credential", AVal.force browser.ErrorAval)
    Assert.Single(AVal.force browser.CredentialsListAval) |> ignore
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a later success clears an earlier error`` () =
    // Arrange — first write fails, second succeeds
    let attempts = ref 0
    let credentialApi =
        api
            (fun () -> Ok [])
            (fun _ ->
                attempts.Value <- attempts.Value + 1
                if attempts.Value = 1 then Error(failure "first attempt failed") else Ok())
            (fun _ -> Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    let credential: IntegrationCredentialUiTypeWithoutId =
        {
            InfrastructureType = InfrastructureType.Google
            Credentials = "retry"
            Username = "retry@gmail.com"
        }

    // Act
    browser.AddCredential credential
    Assert.Equal(Some "first attempt failed", AVal.force browser.ErrorAval)

    browser.AddCredential credential

    // Assert
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``EditCredential passes the credential through and reloads the list`` () =
    // Arrange
    let edited = ResizeArray<IntegrationCredentialUiType>()
    let loads = ref 0
    let credentialApi =
        api
            (fun () -> loads.Value <- loads.Value + 1; Ok [ aCredential "aaa" ])
            (fun _ -> Ok())
            (fun credential -> edited.Add credential; Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    Assert.Equal(1, loads.Value)
    let changed = { aCredential "aaa" with Credentials = "amended" }

    // Act
    browser.EditCredential changed

    // Assert
    Assert.Single(edited) |> ignore
    Assert.Equal(changed, edited.[0])
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``a failed edit surfaces the message`` () =
    // Arrange
    let credentialApi =
        api
            (fun () -> Ok [ aCredential "aaa" ])
            (fun _ -> Ok())
            (fun _ -> Error(failure "could not update credential"))
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    // Act
    browser.EditCredential (aCredential "aaa")

    // Assert
    Assert.Equal(Some "could not update credential", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``LoadCredentials reloads on demand`` () =
    // Arrange
    let loads = ref 0
    let credentialApi =
        api (fun () -> loads.Value <- loads.Value + 1; Ok []) (fun _ -> Ok()) (fun _ -> Ok())
    let browser =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule runSynchronously credentialApi

    Assert.Equal(1, loads.Value)

    // Act
    browser.LoadCredentials()

    // Assert
    Assert.Equal(2, loads.Value)
