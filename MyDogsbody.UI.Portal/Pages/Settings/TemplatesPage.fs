module MyDogsbody.UI.Portal.Pages.Settings.TemplatesPage

open Fun.Blazor
open Fun.Blazor.Router
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MudBlazor
open MyDogsbody.UI.Types

let private startWork (work: unit -> unit) =
    Async.Start(async { work () })

let private confirmAndDelete
  (dialogService: IDialogService)
  (templatesBrowserModule: MyDogsbody.UI.Types.Module.TemplatesBrowserModule)
  (template: TemplateUiType) =
    task {
        let! confirmed =
            dialogService.ShowMessageBox(
                title = "Delete template",
                message = $"Delete '{template.Name}'? This cannot be undone.",
                yesText = "Delete",
                cancelText = "Cancel"
            )

        if confirmed.HasValue && confirmed.Value then
            templatesBrowserModule.DeleteTemplate template.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let private moveTemplate
  (templatesBrowserModule: MyDogsbody.UI.Types.Module.TemplatesBrowserModule)
  (allTemplates: TemplateUiType list)
  (template: TemplateUiType)
  (direction: int) =
    let ordered = allTemplates |> List.sortBy (fun t -> t.Position) |> List.map (fun t -> t.Id)
    let currentIndex = ordered |> List.findIndex (fun id -> id = template.Id)
    let targetIndex = currentIndex + direction

    if targetIndex >= 0 && targetIndex < ordered.Length then
        let swapped =
            ordered
            |> List.mapi (fun i id ->
                if i = currentIndex then ordered.[targetIndex]
                elif i = targetIndex then ordered.[currentIndex]
                else id)

        templatesBrowserModule.ReorderTemplates swapped

let private getViewForSupplier (supplierId: string) =
    html.inject (fun (templateApi: TemplateApi, supplierApi: SupplierApi, dialogService: IDialogService) ->
        let templatesBrowserModule =
            TemplatesBrowserModuleCreators.getTemplatesBrowserModule startWork templateApi supplierId

        let supplierNameAval =
            TemplatesBrowserModuleCreators.getSupplierNameAval startWork supplierApi supplierId

        adapt {
            let! allTemplates = templatesBrowserModule.TemplatesListAval
            let! supplierName = supplierNameAval

            TemplatesComponents.templatesBrowser
                supplierName
                templatesBrowserModule
                (fun () ->
                    TemplatesComponents.showTemplatesEditorDialog
                        dialogService
                        "Add Template"
                        supplierId
                        templatesBrowserModule.AddTemplate
                        templateApi.TestTemplate
                        None
                    |> ignore)
                (fun (template: TemplateUiType) ->
                    TemplatesComponents.showTemplatesEditorDialog
                        dialogService
                        "Edit Template"
                        supplierId
                        (fun (changed: TemplateUiTypeWithoutId) ->
                            templatesBrowserModule.EditTemplate
                                {
                                    Id = template.Id
                                    SupplierId = changed.SupplierId
                                    Name = changed.Name
                                    DocumentPart = changed.DocumentPart
                                    AttachmentFormat = changed.AttachmentFormat
                                    Position = template.Position
                                    Rules = changed.Rules
                                })
                        templateApi.TestTemplate
                        (Some
                            {
                                SupplierId = template.SupplierId
                                Name = template.Name
                                DocumentPart = template.DocumentPart
                                AttachmentFormat = template.AttachmentFormat
                                Position = template.Position
                                Rules = template.Rules
                            })
                    |> ignore)
                (confirmAndDelete dialogService templatesBrowserModule)
                (moveTemplate templatesBrowserModule allTemplates)
        }
    )
    |> SettingsComponents.settingsNavMenu

let getRoute () =
    routeCif "/settings/suppliers/%s/templates" (fun (supplierId: string) -> getViewForSupplier supplierId)
