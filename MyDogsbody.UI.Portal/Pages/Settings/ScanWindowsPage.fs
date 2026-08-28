module MyDogsbody.UI.Portal.Pages.Settings.ScanWindowsPage

open Fun.Blazor
open Fun.Blazor.Router
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators

let private startWork (work: unit -> unit) = Async.Start(async { work () })

let getView () =
    html.inject (fun (scanWindowApi: ScanWindowApi) ->
        let m =
            InvoicesModuleCreators.getScanWindowsBrowserModule startWork scanWindowApi

        let newDaysCval = cval 60

        fragment {
            ScanWindowsComponents.scanWindowsBrowser m newDaysCval
        })
    |> SettingsComponents.settingsNavMenu

let getRoute () = getView () |> routeCi "/settings/scan-windows"
