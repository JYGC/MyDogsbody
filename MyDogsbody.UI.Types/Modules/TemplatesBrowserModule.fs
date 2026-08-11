namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type TemplatesBrowserModule =
    {
        TemplatesListAval: aval<TemplateUiType list>
        IsLoadingAval: aval<bool>
        /// The message from the last failed operation, cleared by the next successful one.
        ErrorAval: aval<string option>
        LoadTemplates: unit -> unit
        AddTemplate: TemplateUiTypeWithoutId -> unit
        EditTemplate: TemplateUiType -> unit
        DeleteTemplate: string -> unit
        ReorderTemplates: string list -> unit
    }
