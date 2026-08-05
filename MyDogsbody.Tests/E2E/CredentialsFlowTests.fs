module MyDogsbody.Tests.E2E.CredentialsFlowTests

open System
open Xunit
open Bunit
open Fun.Blazor
open MyDogsbody.Enums
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types
open MyDogsbody.Tests.E2E.BlazorTestHarness

// User-visible flows, driven through a rendered component down to a real temp LiteDB file and
// back into what the component renders. Everything between is the real thing: the module
// creator, the API record, the workflows, the adapter.
//
// Driving the WPF BlazorWebView window is out of scope; the manual check is recorded in the
// change description instead.

/// Renders the credentials table over the harness's real API. Work runs on the calling thread,
/// so the test never waits on a background thread.
///
/// Fun.Blazor views are NodeRenderFragments rather than Blazor RenderFragments, so they reach
/// bUnit through FunFragmentComponent - the same bridge the framework uses to host one inside a
/// Blazor tree.
let private renderBrowser (harness: CredentialsHarness) =
    let browserModule =
        CredentialsBrowserModuleCreators.getCredentialsBrowserModule (fun work -> work ()) harness.Api

    let view =
        CredentialsComponents.credentialsBrowser browserModule (fun _ -> ()) (fun _ -> ())

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", view)
            builder.CloseComponent()
        )

    browserModule, rendered

let private aCredential infrastructureType secret username : IntegrationCredentialUiTypeWithoutId =
    {
        InfrastructureType = infrastructureType
        Credentials = secret
        Username = username
    }

[<Fact; Trait("Level", "E2E")>]
let ``a credential added through the module appears as a row in the rendered table`` () =
    withCredentialsHarness (fun harness ->
        // Arrange
        let browserModule, rendered = renderBrowser harness
        Assert.DoesNotContain("person@gmail.com", rendered.Markup)

        // Act
        browserModule.AddCredential (aCredential InfrastructureType.Google "google-secret" "person@gmail.com")

        // Assert - what the screen shows, not what the dialog held
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("person@gmail.com", rendered.Markup)
            Assert.Contains("google-secret", rendered.Markup)
            Assert.Contains("Google", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a credential edited through the module updates the row the table shows`` () =
    withCredentialsHarness (fun harness ->
        // Arrange
        let browserModule, rendered = renderBrowser harness
        browserModule.AddCredential (aCredential InfrastructureType.Google "original-secret" "original@gmail.com")
        rendered.WaitForAssertion(fun () -> Assert.Contains("original-secret", rendered.Markup))

        let stored =
            match harness.Api.GetAllCredentials() with
            | Ok [ only ] -> only
            | other -> failwith $"Expected exactly one credential, got {other}"

        // Act
        browserModule.EditCredential
            {
                Id = stored.Id
                InfrastructureType = InfrastructureType.Microsoft
                Credentials = "rotated-secret"
                Username = "rotated@outlook.com"
            }

        // Assert
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("rotated-secret", rendered.Markup)
            Assert.Contains("rotated@outlook.com", rendered.Markup)
            Assert.Contains("Microsoft", rendered.Markup)
            Assert.DoesNotContain("original-secret", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a validation failure is shown as an alert and is not written to the log`` () =
    withCredentialsHarness (fun harness ->
        // Arrange
        let browserModule, rendered = renderBrowser harness

        // Act - an empty secret, which the domain rejects
        browserModule.AddCredential (aCredential InfrastructureType.Google "" "person@gmail.com")

        // Assert - the message reaches the screen
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Credentials must not be empty.", rendered.Markup)
        )

        // Assert - and nothing was stored
        match harness.Api.GetAllCredentials() with
        | Ok stored -> Assert.Empty stored
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

        // Assert - an expected failure is never logged: the exception log is for things worth
        // reading a stack trace about
        Assert.Empty harness.Logged
    )

[<Fact; Trait("Level", "E2E")>]
let ``a store failure is shown as an alert and is written to the log exactly once`` () =
    withUnreachableStoreHarness (fun harness ->
        // Arrange / Act - the initial load fails, because the store cannot be reached
        let _, rendered = renderBrowser harness

        // Assert - the message reaches the screen
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Failed to retrieve all credentials.", rendered.Markup)
        )

        // Assert - infrastructure collapse is logged, and once
        Assert.Single harness.Logged |> ignore

        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Integrations.Credentials.CredentialStore.getAll,
            harness.Logged.[0].ActionName
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a successful write after a failure clears the alert`` () =
    withCredentialsHarness (fun harness ->
        // Arrange - fail first
        let browserModule, rendered = renderBrowser harness
        browserModule.AddCredential (aCredential InfrastructureType.Google "" "person@gmail.com")
        rendered.WaitForAssertion(fun () -> Assert.Contains("Credentials must not be empty.", rendered.Markup))

        // Act
        browserModule.AddCredential (aCredential InfrastructureType.Google "good-secret" "person@gmail.com")

        // Assert
        rendered.WaitForAssertion(fun () ->
            Assert.DoesNotContain("Credentials must not be empty.", rendered.Markup)
            Assert.Contains("good-secret", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``the table renders every stored credential`` () =
    withCredentialsHarness (fun harness ->
        // Arrange
        let browserModule, rendered = renderBrowser harness

        // Act
        browserModule.AddCredential (aCredential InfrastructureType.Google "google-secret" "person@gmail.com")
        browserModule.AddCredential (aCredential InfrastructureType.Microsoft "ms-secret" "person@outlook.com")

        // Assert
        rendered.WaitForAssertion(fun () ->
            Assert.Contains("person@gmail.com", rendered.Markup)
            Assert.Contains("person@outlook.com", rendered.Markup)
            Assert.Contains("google-secret", rendered.Markup)
            Assert.Contains("ms-secret", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``an empty store renders a table rather than an error`` () =
    withCredentialsHarness (fun harness ->
        // Act
        let _, rendered = renderBrowser harness

        // Assert - the heading is there, and no alert
        Assert.Contains("Credentials", rendered.Markup)
        Assert.DoesNotContain("mud-alert", rendered.Markup)
        Assert.Empty harness.Logged
    )
