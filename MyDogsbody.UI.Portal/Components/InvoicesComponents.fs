module MyDogsbody.UI.Portal.Components.InvoicesComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

let private formatDate (value: DateTime option) =
    match value with
    | Some d -> d.ToString("d MMM yyyy")
    | None -> "-"

/// The window picker. A MudSelect, NOT a fixed MudToggleGroup - the number of windows is unknown
/// at build time and the labels come from the store. The label says what it measures (Q1.6).
let private windowSelect (windows: ScanWindowUiType list) (selectedDays: int) (onSelect: int -> unit) =
    MudSelect'<int>() {
        Label "Scan window"
        Dense true
        Value selectedDays
        ValueChanged(fun (days: int) -> onSelect days)

        fragment {
            for w in windows do
                MudSelectItem'<int>() {
                    Value w.Days
                    w.Label
                }
        }
    }

/// The window picker and an explicit "Scan now". Changing the window filters the stored ledger
/// (instant); "Scan now" reads the mailbox (task 12.4 measured ~60 s, so it is never automatic).
let private windowPicker (m: InvoicesModule) =
    adapt {
        let! windows = m.ScanWindowsAval
        let! selectedDays = m.SelectedWindowDaysAval
        let! isScanning = m.IsScanningAval

        div {
            style' "display:flex; gap:1rem; align-items:flex-end"
            windowSelect windows selectedDays m.SelectWindow

            MudButton'' {
                Variant Variant.Filled
                Color Color.Primary
                StartIcon Icons.Material.Filled.Refresh
                Disabled isScanning
                OnClick(fun _ -> m.Rescan())
                "Scan now"
            }
        }
    }

/// "37 invoice(s), mail received in the last 90 days" - the count and window above the table.
let private countLine (m: InvoicesModule) =
    adapt {
        let! invoices = m.InvoicesAval
        let! windows = m.ScanWindowsAval
        let! selectedDays = m.SelectedWindowDaysAval

        let label =
            windows
            |> List.tryFind (fun w -> w.Days = selectedDays)
            |> Option.map (fun w -> w.Label)
            |> Option.defaultValue $"the last {selectedDays} days"

        MudText'' {
            Typo Typo.body2
            $"{List.length invoices} invoice(s), {label}."
        }
    }

let private errorAlert (m: InvoicesModule) =
    adapt {
        let! error = m.ErrorAval

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

/// The invoices table with the window picker and the count line above it.
let invoicesTable (m: InvoicesModule) (confirmAndDelete: InvoiceUiType -> unit) =
    fragment {
        errorAlert m
        windowPicker m
        countLine m

        adapt {
            let! invoices = m.InvoicesAval
            let! isScanning = m.IsScanningAval

            MudTable'' {
                Items invoices
                Loading isScanning
                Striped true
                Dense true
                Breakpoint Breakpoint.Sm

                // Q1.10: an invoice with no due date is listed anyway, greyed out. The class goes
                // on the row MudTable itself renders - RowTemplate supplies the CELLS only, as
                // every other table in this application does. Wrapping them in a MudTr as well
                // produced <tr><tr><td>...</td></tr></tr>, which is not a legal table row and
                // which CSS table fix-up boxes into a single anonymous cell of the outer row, so
                // the ledger's columns stopped lining up with its own headers.
                RowClassFunc(fun (invoice: InvoiceUiType) (_: int) ->
                    if invoice.CanBecomeCalendarEvent then "" else "mud-text-disabled")

                NoRecordsContent(fragment { MudText'' { "No invoices in this window." } })

                HeaderContent(
                    fragment {
                        MudTh'' { "Supplier" }
                        MudTh'' { "Reference" }
                        MudTh'' { "Amount" }
                        MudTh'' { "Issued" }
                        MudTh'' { "Due" }
                        MudTh'' { "" }
                    }
                )

                RowTemplate(fun (invoice: InvoiceUiType) ->
                    fragment {
                        MudTd'' { invoice.SupplierName }
                        MudTd'' { invoice.Reference }
                        MudTd'' { $"{invoice.Currency} {invoice.Amount}" }
                        MudTd'' { formatDate invoice.IssueDate }

                        MudTd'' {
                            if invoice.CanBecomeCalendarEvent then
                                span { formatDate invoice.DueDate }
                            else
                                MudTooltip'' {
                                    Text(invoice.CannotUploadReason |> Option.defaultValue "")

                                    MudText'' {
                                        Color Color.Warning
                                        "no due date"
                                    }
                                }
                        }

                        MudTd'' {
                            MudIconButton'' {
                                Icon Icons.Material.Filled.Delete
                                Color Color.Error
                                OnClick(fun _ -> confirmAndDelete invoice)
                            }
                        }
                    })
            }
        }
    }

/// The problems view: sender, subject, date and cause per row.
let problemsView (m: InvoicesModule) =
    adapt {
        let! problems = m.ProblemsAval

        MudTable'' {
            Items problems
            Dense true

            NoRecordsContent(
                fragment {
                    MudText'' { "No scan problems - every message either yielded an invoice or matched no supplier." }
                }
            )

            HeaderContent(
                fragment {
                    MudTh'' { "From" }
                    MudTh'' { "Subject" }
                    MudTh'' { "Received" }
                    MudTh'' { "Problem" }
                }
            )

            RowTemplate(fun (problem: ScanProblemUiType) ->
                fragment {
                    MudTd'' { problem.Sender }
                    MudTd'' { problem.Subject }
                    MudTd'' { problem.ReceivedAt.ToString("d MMM yyyy") }
                    MudTd'' { problem.Cause }
                })
        }
    }

/// The tombstones view with an un-delete. The page calls m.LoadTombstones() when this tab opens.
let tombstonesView (m: InvoicesModule) =
    adapt {
        let! tombstones = m.TombstonesAval

        MudTable'' {
            Items tombstones
            Dense true

            NoRecordsContent(fragment { MudText'' { "No deleted invoices." } })

            HeaderContent(
                fragment {
                    MudTh'' { "Supplier" }
                    MudTh'' { "Reference" }
                    MudTh'' { "Deleted" }
                    MudTh'' { "" }
                }
            )

            RowTemplate(fun (tombstone: TombstoneUiType) ->
                fragment {
                    MudTd'' { tombstone.SupplierName }
                    MudTd'' { tombstone.Reference }
                    MudTd'' { tombstone.DeletedAt.ToString("d MMM yyyy") }

                    MudTd'' {
                        MudButton'' {
                            Variant Variant.Text
                            Color Color.Primary
                            OnClick(fun _ -> m.UndeleteInvoice tombstone.SupplierId tombstone.Reference)
                            "Un-delete"
                        }
                    }
                })
        }
    }
