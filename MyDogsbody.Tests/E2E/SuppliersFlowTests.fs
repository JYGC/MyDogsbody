module MyDogsbody.Tests.E2E.SuppliersFlowTests

open Xunit
open Bunit
open Fun.Blazor
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types
open MyDogsbody.Tests.E2E.SuppliersTestHarness

// User-visible flows, driven through a rendered component down to a real temp SQLite file
// (schema built by the real migrations) and back into what the component renders. Everything
// between is the real thing: the module creator, the API record, the workflows, the adapter.
//
// Driving the WPF BlazorWebView window is out of scope; the manual check is recorded in the
// change description instead.

/// Renders the suppliers table over the harness's real API. Work runs on the calling thread, so
/// the test never waits on a background thread.
let private renderBrowser (harness: SuppliersHarness) =
    let browserModule =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule (fun work -> work ()) harness.Api

    let view =
        SuppliersComponents.suppliersBrowser browserModule (fun _ -> ()) (fun _ -> ()) (fun _ -> ())

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", view)
            builder.CloseComponent()
        )

    browserModule, rendered

let private aSupplier name termDays matchers : SupplierUiTypeWithoutId =
    { Name = name; PaymentTermDays = termDays; Matchers = matchers }

[<Fact; Trait("Level", "E2E")>]
let ``a supplier added through the module appears as a row in the rendered table`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness
        Assert.DoesNotContain("Acme", rendered.Markup)

        browserModule.AddSupplier (aSupplier "Acme" 30 [ { Kind = "Domain"; Value = "acme.example" } ])

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Acme", rendered.Markup)
            Assert.Contains("30", rendered.Markup)
            Assert.Contains("acme.example", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a supplier edited through the module updates the row the table shows`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness
        browserModule.AddSupplier (aSupplier "Original Co" 30 [])
        rendered.WaitForAssertion(fun () -> Assert.Contains("Original Co", rendered.Markup))

        let stored =
            match harness.Api.GetAllSuppliers() with
            | Ok [ only ] -> only
            | other -> failwith $"Expected exactly one supplier, got {other}"

        browserModule.EditSupplier
            { Id = stored.Id; Name = "Renamed Co"; PaymentTermDays = 60; Matchers = [] }

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Renamed Co", rendered.Markup)
            Assert.Contains("60", rendered.Markup)
            Assert.DoesNotContain("Original Co", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a supplier deleted through the module disappears from the rendered table`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness
        browserModule.AddSupplier (aSupplier "Gone Soon" 0 [])
        rendered.WaitForAssertion(fun () -> Assert.Contains("Gone Soon", rendered.Markup))

        let stored =
            match harness.Api.GetAllSuppliers() with
            | Ok [ only ] -> only
            | other -> failwith $"Expected exactly one supplier, got {other}"

        browserModule.DeleteSupplier stored.Id

        rendered.WaitForAssertion(fun () -> Assert.DoesNotContain("Gone Soon", rendered.Markup))
    )

[<Fact; Trait("Level", "E2E")>]
let ``a validation failure is shown as an alert and is not written to the log`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness

        // An empty name, which the domain rejects
        browserModule.AddSupplier (aSupplier "" 30 [])

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Supplier name must not be empty.", rendered.Markup)
        )

        match harness.Api.GetAllSuppliers() with
        | Ok stored -> Assert.Empty stored
        | Error ex -> Assert.Fail($"Expected Ok, but got Error: {ex.Message}")

        // An expected failure is never logged: the exception log is for things worth reading a
        // stack trace about.
        Assert.Empty harness.Logged
    )

[<Fact; Trait("Level", "E2E")>]
let ``a store failure is shown as an alert and is written to the log exactly once`` () =
    withUnreachableSupplierStoreHarness (fun harness ->
        // The initial load fails, because the store cannot be reached
        let _, rendered = renderBrowser harness

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Failed to retrieve all suppliers.", rendered.Markup)
        )

        Assert.Single harness.Logged |> ignore

        Assert.Equal(
            MyDogsbody.Exceptions.Types.ActionNames.MyDogsbody.Database.SupplierStore.getAll,
            harness.Logged.[0].ActionName
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``a successful write after a failure clears the alert`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness
        browserModule.AddSupplier (aSupplier "" 30 [])
        rendered.WaitForAssertion(fun () -> Assert.Contains("Supplier name must not be empty.", rendered.Markup))

        browserModule.AddSupplier (aSupplier "Good Co" 30 [])

        rendered.WaitForAssertion(fun () ->
            Assert.DoesNotContain("Supplier name must not be empty.", rendered.Markup)
            Assert.Contains("Good Co", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``the table renders every stored supplier`` () =
    withSuppliersHarness (fun harness ->
        let browserModule, rendered = renderBrowser harness

        browserModule.AddSupplier (aSupplier "Acme" 30 [])
        browserModule.AddSupplier (aSupplier "Bravo Corp" 14 [])

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Acme", rendered.Markup)
            Assert.Contains("Bravo Corp", rendered.Markup)
        )
    )

[<Fact; Trait("Level", "E2E")>]
let ``an empty store renders a table rather than an error`` () =
    withSuppliersHarness (fun harness ->
        let _, rendered = renderBrowser harness

        Assert.Contains("Suppliers", rendered.Markup)
        Assert.DoesNotContain("mud-alert", rendered.Markup)
        Assert.Empty harness.Logged
    )
