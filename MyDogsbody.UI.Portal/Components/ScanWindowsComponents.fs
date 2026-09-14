module MyDogsbody.UI.Portal.Components.ScanWindowsComponents

open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// The /settings/scan-windows list: add, delete, the remembered one marked, and the LAST one's
/// delete shown as unavailable with the reason (CannotDeleteLastScanWindow is a domain rule).
let scanWindowsBrowser (scanWindowsBrowserModule: ScanWindowsBrowserModule) (newDaysCval: cval<int>) =
    fragment {
        adapt {
            let! error = scanWindowsBrowserModule.ErrorAval

            match error with
            | Some message ->
                MudAlert'' {
                    Severity Severity.Error
                    Variant Variant.Filled
                    Dense true
                    message
                }
            | None -> ()
        }

        MudText'' {
            Typo Typo.h3
            "Scan windows"
        }

        MudText'' {
            Typo Typo.body2
            "How far back the invoices page scans your mail, in days. Adding one takes effect without a restart."
        }

        // add
        adapt {
            let! newDays = newDaysCval

            div {
                MudNumericField'<int>() {
                    Label "Days"
                    Value newDays
                    ValueChanged(fun (days: int) -> transact (fun _ -> newDaysCval.Value <- days))
                    Min 1
                    Max 3650
                }

                MudButton'' {
                    Variant Variant.Filled
                    Color Color.Primary
                    OnClick(fun _ -> scanWindowsBrowserModule.AddWindow newDays)
                    "Add window"
                }
            }
        }

        adapt {
            let! windows = scanWindowsBrowserModule.WindowsAval
            let! selectedDays = scanWindowsBrowserModule.SelectedWindowDaysAval
            let onlyOne = List.length windows <= 1

            MudTable'' {
                Items windows
                Dense true

                HeaderContent(
                    fragment {
                        MudTh'' { "Window" }
                        MudTh'' { "" }
                    }
                )

                RowTemplate(fun (window: ScanWindowUiType) ->
                    fragment {
                        MudTd'' {
                            span { window.Label }

                            if window.Days = selectedDays then
                                span {
                                    class' "mud-primary-text"
                                    "  (current)"
                                }
                        }

                        MudTd'' {
                            if onlyOne then
                                MudTooltip'' {
                                    Text "The last scan window cannot be deleted - the picker must always have something to offer."

                                    MudButton'' {
                                        Disabled true
                                        "Delete"
                                    }
                                }
                            else
                                MudButton'' {
                                    Variant Variant.Text
                                    Color Color.Error
                                    OnClick(fun _ -> scanWindowsBrowserModule.DeleteWindow window.Id)
                                    "Delete"
                                }
                        }
                    })
            }
        }
    }
