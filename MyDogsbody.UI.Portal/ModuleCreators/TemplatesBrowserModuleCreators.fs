module MyDogsbody.UI.Portal.ModuleCreators.TemplatesBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Builds one supplier's templates browser state.
///
/// `startWork` is how the module gets off the render thread. Production passes an Async.Start
/// equivalent; a test passes `fun work -> work ()` and never has to wait.
let getTemplatesBrowserModule
  (startWork: (unit -> unit) -> unit)
  (templateApi: TemplateApi)
  (supplierId: string)
  : TemplatesBrowserModule =
    let isLoadingCval = cval false
    let errorCval = cval<string option> None
    let templatesListCval = cval<TemplateUiType list> []

    let loadTemplates () =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            let result = templateApi.GetTemplatesForSupplier supplierId
            transact (fun _ ->
                match result with
                | Ok templates ->
                    templatesListCval.Value <- templates
                    errorCval.Value <- None
                | Error ex ->
                    errorCval.Value <- Some ex.Message
                isLoadingCval.Value <- false
            )
        )

    /// Runs a write and reloads, so the table shows what was actually stored rather than what
    /// the dialog was holding.
    let write operation =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            match operation () with
            | Ok () ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isLoadingCval.Value <- false
                )
                loadTemplates ()
            | Error (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false
                )
        )

    loadTemplates ()

    {
        TemplatesListAval = templatesListCval
        IsLoadingAval = isLoadingCval
        ErrorAval = errorCval
        LoadTemplates = loadTemplates
        AddTemplate = fun template -> write (fun () -> templateApi.AddTemplate template)
        EditTemplate = fun template -> write (fun () -> templateApi.EditTemplate template)
        DeleteTemplate = fun id -> write (fun () -> templateApi.DeleteTemplate id)
        ReorderTemplates = fun ids -> write (fun () -> templateApi.ReorderTemplates supplierId ids)
    }
