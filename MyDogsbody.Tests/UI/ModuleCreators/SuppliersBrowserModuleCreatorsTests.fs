module MyDogsbody.Tests.UI.ModuleCreators.SuppliersBrowserModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Runs the work on the calling thread. Production passes an Async.Start equivalent; the seam
/// exists so a test never has to wait on a background thread.
let private runSynchronously (work: unit -> unit) = work ()

let private failure message =
    MyDogsbodyException("test.action", message, ApplicationException(message))

let private aSupplier id : SupplierUiType =
    { Id = id; Name = $"Supplier {id}"; PaymentTermDays = 30; Matchers = [] }

let private api getAll add edit delete : SupplierApi =
    {
        GetAllSuppliers = getAll
        AddSupplier = add
        EditSupplier = edit
        DeleteSupplier = delete
    }

[<Fact; Trait("Level", "Unit")>]
let ``the module loads the suppliers when it is created`` () =
    let stored = [ aSupplier "1"; aSupplier "2" ]
    let supplierApi = api (fun () -> Ok stored) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok())

    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    let listed = AVal.force browser.SuppliersListAval
    Assert.Equal(2, listed.Length)
    Assert.Equal("1", (List.item 0 listed).Id)
    Assert.Equal("2", (List.item 1 listed).Id)
    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed load surfaces the message and stops loading`` () =
    let supplierApi =
        api (fun () -> Error(failure "could not read suppliers")) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok())

    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    Assert.Equal(Some "could not read suppliers", AVal.force browser.ErrorAval)
    Assert.Empty(AVal.force browser.SuppliersListAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``AddSupplier passes the supplier through to the api`` () =
    let added = ResizeArray<SupplierUiTypeWithoutId>()
    let supplierApi =
        api (fun () -> Ok []) (fun supplier -> added.Add supplier; Ok()) (fun _ -> Ok()) (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    let newSupplier: SupplierUiTypeWithoutId =
        { Name = "New Co"; PaymentTermDays = 14; Matchers = [] }

    browser.AddSupplier newSupplier

    Assert.Single(added) |> ignore
    Assert.Equal(newSupplier, added.[0])

[<Fact; Trait("Level", "Unit")>]
let ``a successful add reloads the list so the new row appears`` () =
    let storedRows = ResizeArray<SupplierUiType>()
    let supplierApi =
        api
            (fun () -> Ok(List.ofSeq storedRows))
            (fun _ -> storedRows.Add(aSupplier "ccc"); Ok())
            (fun _ -> Ok())
            (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    Assert.Empty(AVal.force browser.SuppliersListAval)

    browser.AddSupplier { Name = "Ccc"; PaymentTermDays = 0; Matchers = [] }

    let listed = AVal.force browser.SuppliersListAval
    Assert.Single(listed) |> ignore
    Assert.Equal("ccc", listed.Head.Id)

[<Fact; Trait("Level", "Unit")>]
let ``a failed add surfaces the message and leaves the list alone`` () =
    let supplierApi =
        api
            (fun () -> Ok [ aSupplier "1" ])
            (fun _ -> Error(failure "could not save supplier"))
            (fun _ -> Ok())
            (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    browser.AddSupplier { Name = "Doomed"; PaymentTermDays = 0; Matchers = [] }

    Assert.Equal(Some "could not save supplier", AVal.force browser.ErrorAval)
    Assert.Single(AVal.force browser.SuppliersListAval) |> ignore
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a later success clears an earlier error`` () =
    let attempts = ref 0
    let supplierApi =
        api
            (fun () -> Ok [])
            (fun _ ->
                attempts.Value <- attempts.Value + 1
                if attempts.Value = 1 then Error(failure "first attempt failed") else Ok())
            (fun _ -> Ok())
            (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    let supplier: SupplierUiTypeWithoutId = { Name = "Retry"; PaymentTermDays = 0; Matchers = [] }

    browser.AddSupplier supplier
    Assert.Equal(Some "first attempt failed", AVal.force browser.ErrorAval)

    browser.AddSupplier supplier

    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``EditSupplier passes the supplier through and reloads the list`` () =
    let edited = ResizeArray<SupplierUiType>()
    let loads = ref 0
    let supplierApi =
        api
            (fun () -> loads.Value <- loads.Value + 1; Ok [ aSupplier "1" ])
            (fun _ -> Ok())
            (fun supplier -> edited.Add supplier; Ok())
            (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    Assert.Equal(1, loads.Value)
    let changed = { aSupplier "1" with Name = "Amended" }

    browser.EditSupplier changed

    Assert.Single(edited) |> ignore
    Assert.Equal(changed, edited.[0])
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``a failed edit surfaces the message`` () =
    let supplierApi =
        api
            (fun () -> Ok [ aSupplier "1" ])
            (fun _ -> Ok())
            (fun _ -> Error(failure "could not update supplier"))
            (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    browser.EditSupplier (aSupplier "1")

    Assert.Equal(Some "could not update supplier", AVal.force browser.ErrorAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``DeleteSupplier passes the id through and reloads the list`` () =
    let deleted = ResizeArray<string>()
    let loads = ref 0
    let supplierApi =
        api
            (fun () -> loads.Value <- loads.Value + 1; Ok [])
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun id -> deleted.Add id; Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    Assert.Equal(1, loads.Value)

    browser.DeleteSupplier "1"

    Assert.Single(deleted) |> ignore
    Assert.Equal("1", deleted.[0])
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``a failed delete surfaces the message`` () =
    let supplierApi =
        api
            (fun () -> Ok [ aSupplier "1" ])
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun _ -> Error(failure "could not delete supplier"))
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    browser.DeleteSupplier "1"

    Assert.Equal(Some "could not delete supplier", AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``LoadSuppliers reloads on demand`` () =
    let loads = ref 0
    let supplierApi =
        api (fun () -> loads.Value <- loads.Value + 1; Ok []) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok())
    let browser =
        SuppliersBrowserModuleCreators.getSuppliersBrowserModule runSynchronously supplierApi

    Assert.Equal(1, loads.Value)

    browser.LoadSuppliers()

    Assert.Equal(2, loads.Value)

/// Walks up from the test assembly rather than hard-coding a path, so this keeps working
/// whatever the working directory or build configuration is - the same approach
/// Contracts/DomainIsolationTests.fs uses to find MyDogsbody.sln.
let private repositoryRoot () =
    let rec find (directory: IO.DirectoryInfo) =
        if isNull (box directory) then
            failwith "Could not locate MyDogsbody.sln above the test assembly."
        elif IO.File.Exists(IO.Path.Combine(directory.FullName, "MyDogsbody.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (IO.DirectoryInfo(AppContext.BaseDirectory))

[<Fact; Trait("Level", "Unit")>]
let ``no Async.Start appears anywhere in the module creator file`` () =
    let sourceFilePath =
        IO.Path.Combine(
            repositoryRoot (),
            "MyDogsbody.UI.Portal", "ModuleCreators", "SuppliersBrowserModuleCreators.fs"
        )

    Assert.True(IO.File.Exists sourceFilePath, $"Expected to find {sourceFilePath}")
    let source = IO.File.ReadAllText sourceFilePath
    // "Async.Start(" rather than "Async.Start" - the doc comment mentions the concept in prose
    // without calling it.
    Assert.DoesNotContain("Async.Start(", source)
