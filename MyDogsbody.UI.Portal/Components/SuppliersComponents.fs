module MyDogsbody.UI.Portal.Components.SuppliersComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open Microsoft.AspNetCore.Components

let private matcherKinds = [ "Sender"; "Domain"; "Subject" ]

let private formatMatchers (matchers: SupplierMatcherUiType list) =
    matchers
    |> List.map (fun m -> $"{m.Kind}: {m.Value}")
    |> String.concat ", "

let suppliersBrowser
  (suppliersBrowserModule: SuppliersBrowserModule)
  (showAddSupplierModal: _ -> unit)
  (showEditSupplierModal: SupplierUiType -> unit)
  (confirmAndDeleteSupplier: SupplierUiType -> unit) =
    fragment {
        adapt {
            let! error = suppliersBrowserModule.ErrorAval
            match error with
            | Some message ->
                MudAlert''{
                    Severity Severity.Error
                    Variant Variant.Filled
                    Dense true
                    message
                }
            | None -> ()
        }
        adapt {
            let! suppliers = suppliersBrowserModule.SuppliersListAval
            let! isLoading = suppliersBrowserModule.IsLoadingAval
            MudTable''{
                Items suppliers
                Breakpoint Breakpoint.Sm
                Loading isLoading
                FixedHeader true
                LoadingProgressColor Color.Info
                Striped true
                Height "80vh"
                NoRecordsContent (fragment {
                    MudText''{ "No suppliers yet - add one to get started." }
                })
                ToolBarContent (fragment {
                    MudText''{
                        Typo Typo.h3
                        "Suppliers"
                    }
                    MudSpacer''{}
                    adapt {
                        MudButton''{
                            Variant Variant.Filled
                            Color Color.Primary
                            EndIcon Icons.Material.Filled.Add
                            OnClick (fun _ ->
                                showAddSupplierModal ()
                            )
                            "New Supplier"
                        }
                    }
                })
                HeaderContent (
                    fragment {
                        MudTh''{ "Name" }
                        MudTh''{ "Payment term (days)" }
                        MudTh''{ "Match rules" }
                        MudTh''{ }
                    }
                )
                RowTemplate (fun (supplier: SupplierUiType) ->
                    fragment {
                        MudTd''{ $"{supplier.Name}" }
                        MudTd''{ $"{supplier.PaymentTermDays}" }
                        MudTd''{ formatMatchers supplier.Matchers }
                        MudTd''{
                            MudButton''{
                                Variant Variant.Filled
                                Color Color.Primary
                                OnClick (fun _ ->
                                    showEditSupplierModal supplier
                                )
                                "Edit"
                            }
                            MudButton''{
                                Variant Variant.Filled
                                Color Color.Error
                                OnClick (fun _ ->
                                    confirmAndDeleteSupplier supplier
                                )
                                "Delete"
                            }
                        }
                    }
                )
            }
        }
    }

type SuppliersEditorDialog() =
    inherit FunComponent()

    [<CascadingParameter>]
    member val private __mudDialogInstance : IMudDialogInstance = null with get, set

    [<Parameter>]
    member val public Title : string = "Add Supplier" with get, set

    [<Parameter>]
    member val public SupplierUiType : SupplierUiTypeWithoutId = {
        Name = "";
        PaymentTermDays = 0;
        Matchers = [] } with get, set

    [<Parameter>]
    member val public OnSupplierSubmitted : (SupplierUiTypeWithoutId -> unit) = fun _ -> () with get, set

    override this.Render() =
        let nameCval = cval this.SupplierUiType.Name
        let paymentTermDaysCval = cval this.SupplierUiType.PaymentTermDays
        let matchersCval = cval this.SupplierUiType.Matchers
        let newMatcherKindCval = cval "Sender"
        let newMatcherValueCval = cval ""

        fragment {
            MudDialog''{
                TitleContent (fragment {
                    MudText'' {
                        Typo Typo.h6
                        this.Title
                    }
                })
                DialogContent (fragment {
                    MudGrid'' {
                        MudItem'' {
                            xs 12
                            sm 12
                            md 12
                            adapt {
                                let! supplierName, setSupplierName = nameCval.WithSetter()
                                MudTextField'' {
                                    Label "Name"
                                    Variant Variant.Text
                                    Value supplierName
                                    Immediate true
                                    ValueChanged setSupplierName
                                }
                            }
                        }
                    }
                    MudGrid'' {
                        MudItem'' {
                            xs 12
                            sm 12
                            md 12
                            adapt {
                                let! paymentTermDays, setPaymentTermDays = paymentTermDaysCval.WithSetter()
                                MudNumericField'' {
                                    Label "Payment term (days)"
                                    Variant Variant.Text
                                    Value paymentTermDays
                                    Min 0
                                    Max 365
                                    Immediate true
                                    ValueChanged setPaymentTermDays
                                }
                            }
                        }
                    }
                    MudGrid'' {
                        MudItem'' {
                            xs 12
                            sm 12
                            md 12
                            adapt {
                                let! matchers = matchersCval
                                MudList'' {
                                    // Indexed, because match rules may legitimately repeat -
                                    // requirements.md says a duplicate is stored as submitted. Removing
                                    // by value would take every identical rule with it, not the one
                                    // whose button was pressed.
                                    for index, matcher in List.indexed matchers do
                                        MudListItem'' {
                                            // The label goes in the child content, not MudListItem's
                                            // Text: a list item renders one or the other, so setting
                                            // both left every rule showing an unlabelled delete button.
                                            MudStack'' {
                                                Row true
                                                MudText'' { $"{matcher.Kind}: {matcher.Value}" }
                                                MudIconButton'' {
                                                    Icon Icons.Material.Filled.Delete
                                                    OnClick (fun _ ->
                                                        transact (fun _ ->
                                                            matchersCval.Value <- matchers |> List.removeAt index
                                                        )
                                                    )
                                                }
                                            }
                                        }
                                }
                            }
                        }
                    }
                    MudGrid'' {
                        MudItem'' {
                            xs 12
                            sm 4
                            md 4
                            adapt {
                                let! newMatcherKind, setNewMatcherKind = newMatcherKindCval.WithSetter()
                                MudSelect'' {
                                    Label "Match rule kind"
                                    Variant Variant.Text
                                    Value newMatcherKind
                                    ValueChanged setNewMatcherKind
                                    fragment {
                                        for matcherKindOption in matcherKinds do
                                            MudSelectItem'' {
                                                Value matcherKindOption
                                                matcherKindOption
                                            }
                                    }
                                }
                            }
                        }
                        MudItem'' {
                            xs 12
                            sm 6
                            md 6
                            adapt {
                                let! newMatcherValue, setNewMatcherValue = newMatcherValueCval.WithSetter()
                                MudTextField'' {
                                    Label "Match rule value"
                                    Variant Variant.Text
                                    Value newMatcherValue
                                    Immediate true
                                    ValueChanged setNewMatcherValue
                                }
                            }
                        }
                        MudItem'' {
                            xs 12
                            sm 2
                            md 2
                            adapt {
                                let! newMatcherKind = newMatcherKindCval
                                let! newMatcherValue = newMatcherValueCval
                                MudIconButton'' {
                                    Icon Icons.Material.Filled.Add
                                    Disabled (String.IsNullOrWhiteSpace newMatcherValue)
                                    OnClick (fun _ ->
                                        transact (fun _ ->
                                            matchersCval.Value <-
                                                matchersCval.Value @ [ { Kind = newMatcherKind; Value = newMatcherValue } ]
                                            newMatcherValueCval.Value <- ""
                                        )
                                    )
                                }
                            }
                        }
                    }
                })
                DialogActions (fragment {
                    MudButton'' {
                        OnClick (fun _ -> this.__mudDialogInstance.Cancel() |> ignore)
                        "Cancel"
                    }
                    adapt {
                        let! supplierName = nameCval
                        let! paymentTermDays = paymentTermDaysCval
                        let! matchers = matchersCval
                        let disableOkButton = String.IsNullOrWhiteSpace supplierName
                        MudButton'' {
                            Disabled disableOkButton
                            Color Color.Primary
                            OnClick (fun _ ->
                                this.OnSupplierSubmitted
                                    {
                                        Name = supplierName
                                        PaymentTermDays = paymentTermDays
                                        Matchers = matchers
                                    }
                                this.__mudDialogInstance.Close() |> ignore
                            )
                            "Ok"
                        }
                    }
                })
            }
        }

let showSuppliersEditorDialog
  (dialogService: IDialogService)
  (dialogTitle: string)
  (onSupplierSubmitted: SupplierUiTypeWithoutId -> unit)
  (supplierOption: SupplierUiTypeWithoutId option) =
    let options = new DialogOptions(
        CloseOnEscapeKey = false,
        BackdropClick = false,
        FullWidth = true
    )
    let parameters = new DialogParameters<SuppliersEditorDialog>()
    parameters.Add("Title", dialogTitle)
    parameters.Add("OnSupplierSubmitted", onSupplierSubmitted)
    if supplierOption.IsSome then
        parameters.Add("SupplierUiType", supplierOption.Value)
    dialogService.ShowAsync<SuppliersEditorDialog>(
        dialogTitle,
        parameters,
        options
    )
