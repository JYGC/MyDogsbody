module MyDogsbody.Tests.UI.ModuleCreators.TemplatesBrowserModuleCreatorsTests

open System
open Xunit
open FSharp.Data.Adaptive
open MyDogsbody.Exceptions.Types
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types

/// Runs the work on the calling thread. Production passes an Async.Start equivalent; the seam
/// exists so a test never has to wait on a background thread.
let private runSynchronously (work: unit -> unit) = work ()

let private failure message = MyDogsbodyException("test.action", message, ApplicationException(message))

let private aTemplate id : TemplateUiType =
    { Id = id; SupplierId = "1"; Name = $"Template {id}"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = [] }

let private api getForSupplier add edit delete reorder : TemplateApi =
    {
        GetTemplatesForSupplier = getForSupplier
        AddTemplate = add
        EditTemplate = edit
        DeleteTemplate = delete
        ReorderTemplates = reorder
        TestTemplate = fun _ -> failwith "not used"
    }

[<Fact; Trait("Level", "Unit")>]
let ``the module loads the supplier's templates when it is created`` () =
    let stored = [ aTemplate "1"; aTemplate "2" ]
    let templateApi = api (fun _ -> Ok stored) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ _ -> Ok())

    let browser =
        TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    let listed = AVal.force browser.TemplatesListAval
    Assert.Equal(2, listed.Length)
    Assert.False(AVal.force browser.IsLoadingAval)
    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``a failed load surfaces the message and stops loading`` () =
    let templateApi =
        api (fun _ -> Error(failure "could not read templates")) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ _ -> Ok())

    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Equal(Some "could not read templates", AVal.force browser.ErrorAval)
    Assert.Empty(AVal.force browser.TemplatesListAval)
    Assert.False(AVal.force browser.IsLoadingAval)

[<Fact; Trait("Level", "Unit")>]
let ``a successful add reloads the list so the new row appears`` () =
    let storedRows = ResizeArray<TemplateUiType>()
    let templateApi =
        api
            (fun _ -> Ok(List.ofSeq storedRows))
            (fun _ -> storedRows.Add(aTemplate "ccc"); Ok())
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Empty(AVal.force browser.TemplatesListAval)

    browser.AddTemplate { SupplierId = "1"; Name = "New"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = [] }

    let listed = AVal.force browser.TemplatesListAval
    Assert.Single(listed) |> ignore
    Assert.Equal("ccc", listed.Head.Id)

[<Fact; Trait("Level", "Unit")>]
let ``a failed add surfaces the message and leaves the list alone`` () =
    let templateApi =
        api (fun _ -> Ok [ aTemplate "1" ]) (fun _ -> Error(failure "could not save template")) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    browser.AddTemplate { SupplierId = "1"; Name = "Doomed"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = [] }

    Assert.Equal(Some "could not save template", AVal.force browser.ErrorAval)
    Assert.Single(AVal.force browser.TemplatesListAval) |> ignore

[<Fact; Trait("Level", "Unit")>]
let ``a later success clears an earlier error`` () =
    let attempts = ref 0
    let templateApi =
        api
            (fun _ -> Ok [])
            (fun _ ->
                attempts.Value <- attempts.Value + 1
                if attempts.Value = 1 then Error(failure "first attempt failed") else Ok())
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"
    let template: TemplateUiTypeWithoutId = { SupplierId = "1"; Name = "Retry"; DocumentPart = "AnyPart"; AttachmentFormat = ""; Position = 0; Rules = [] }

    browser.AddTemplate template
    Assert.Equal(Some "first attempt failed", AVal.force browser.ErrorAval)

    browser.AddTemplate template

    Assert.Equal(None, AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``EditTemplate passes the template through and reloads the list`` () =
    let edited = ResizeArray<TemplateUiType>()
    let loads = ref 0
    let templateApi =
        api
            (fun _ -> loads.Value <- loads.Value + 1; Ok [ aTemplate "1" ])
            (fun _ -> Ok())
            (fun t -> edited.Add t; Ok())
            (fun _ -> Ok())
            (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Equal(1, loads.Value)
    let changed = { aTemplate "1" with Name = "Amended" }

    browser.EditTemplate changed

    Assert.Single(edited) |> ignore
    Assert.Equal(changed, edited.[0])
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``DeleteTemplate passes the id through and reloads the list`` () =
    let deleted = ResizeArray<string>()
    let loads = ref 0
    let templateApi =
        api
            (fun _ -> loads.Value <- loads.Value + 1; Ok [])
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun id -> deleted.Add id; Ok())
            (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Equal(1, loads.Value)

    browser.DeleteTemplate "1"

    Assert.Single(deleted) |> ignore
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``ReorderTemplates passes the order through and reloads the list`` () =
    let reordered = ResizeArray<string * string list>()
    let loads = ref 0
    let templateApi =
        api
            (fun _ -> loads.Value <- loads.Value + 1; Ok [])
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun _ -> Ok())
            (fun supplierId ids -> reordered.Add(supplierId, ids); Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Equal(1, loads.Value)

    browser.ReorderTemplates [ "2"; "1" ]

    Assert.Single(reordered) |> ignore
    Assert.Equal(("1", [ "2"; "1" ]), reordered.[0])
    Assert.Equal(2, loads.Value)

[<Fact; Trait("Level", "Unit")>]
let ``a failed reorder surfaces the message`` () =
    let templateApi =
        api (fun _ -> Ok [ aTemplate "1" ]) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ _ -> Error(failure "could not reorder"))
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    browser.ReorderTemplates [ "1" ]

    Assert.Equal(Some "could not reorder", AVal.force browser.ErrorAval)

[<Fact; Trait("Level", "Unit")>]
let ``LoadTemplates reloads on demand`` () =
    let loads = ref 0
    let templateApi = api (fun _ -> loads.Value <- loads.Value + 1; Ok []) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ -> Ok()) (fun _ _ -> Ok())
    let browser = TemplatesBrowserModuleCreators.getTemplatesBrowserModule runSynchronously templateApi "1"

    Assert.Equal(1, loads.Value)

    browser.LoadTemplates()

    Assert.Equal(2, loads.Value)

/// Walks up from the test assembly rather than hard-coding a path, so this keeps working
/// whatever the working directory or build configuration is.
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
        IO.Path.Combine(repositoryRoot (), "MyDogsbody.UI.Portal", "ModuleCreators", "TemplatesBrowserModuleCreators.fs")

    Assert.True(IO.File.Exists sourceFilePath, $"Expected to find {sourceFilePath}")
    let source = IO.File.ReadAllText sourceFilePath
    Assert.DoesNotContain("Async.Start(", source)

// PR #14 review: the route handed getViewForSupplier the supplier id where it expects the
// supplier name (getViewForSupplier supplierId supplierId), so /settings/suppliers/7/templates
// rendered the header "Templates for 7". The route only carries the id, so the name has to be
// resolved from SupplierApi - through the same startWork seam every other load uses, never a
// synchronous call on the render thread.

let private supplierApi getAll : SupplierApi =
    {
        GetAllSuppliers = getAll
        AddSupplier = fun _ -> failwith "not used"
        EditSupplier = fun _ -> failwith "not used"
        DeleteSupplier = fun _ -> failwith "not used"
    }

let private aSupplier id name : SupplierUiType =
    { Id = id; Name = name; PaymentTermDays = 30; Matchers = [] }

[<Fact; Trait("Level", "Unit")>]
let ``the supplier name aval resolves the route's id to that supplier's name`` () =
    let api = supplierApi (fun () -> Ok [ aSupplier "6" "Globex"; aSupplier "7" "Acme" ])

    let actual = TemplatesBrowserModuleCreators.getSupplierNameAval runSynchronously api "7"

    Assert.Equal("Acme", AVal.force actual)

[<Fact; Trait("Level", "Unit")>]
let ``the supplier name aval falls back to the id when no supplier carries it`` () =
    let api = supplierApi (fun () -> Ok [ aSupplier "6" "Globex" ])

    let actual = TemplatesBrowserModuleCreators.getSupplierNameAval runSynchronously api "7"

    Assert.Equal("7", AVal.force actual)

[<Fact; Trait("Level", "Unit")>]
let ``the supplier name aval falls back to the id when the lookup fails, rather than blanking the header`` () =
    let api = supplierApi (fun () -> Error(failure "suppliers are unreachable"))

    let actual = TemplatesBrowserModuleCreators.getSupplierNameAval runSynchronously api "7"

    Assert.Equal("7", AVal.force actual)
