module MyDogsbody.UI.Portal.Components.InvoicesComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

let private formatDate (value: DateTime option) =
    match value with
    | Some date -> date.ToString("d MMM yyyy")
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
            for window in windows do
                MudSelectItem'<int>() {
                    Value window.Days
                    window.Label
                }
        }
    }

/// The window picker, "Scan now", and "Rescan everything". Changing the window filters the stored
/// ledger (instant); "Scan now" reads the mailbox resuming from watermarks (task 12.4 measured
/// ~60 s, so it is never automatic); "Rescan everything" discards the watermarks first, for mail
/// a plain scan resumes straight past (a folder read before its supplier existed).
let private windowPicker (invoicesModule: InvoicesModule) =
    adapt {
        let! windows = invoicesModule.ScanWindowsAval
        let! selectedDays = invoicesModule.SelectedWindowDaysAval
        let! isScanning = invoicesModule.IsScanningAval

        div {
            style' "display:flex; gap:1rem; align-items:flex-end"
            windowSelect windows selectedDays invoicesModule.SelectWindow

            MudButton'' {
                Variant Variant.Filled
                Color Color.Primary
                StartIcon Icons.Material.Filled.Refresh
                Disabled isScanning
                OnClick(fun _ -> invoicesModule.Rescan())
                "Scan now"
            }

            MudButton'' {
                Variant Variant.Outlined
                Color Color.Primary
                StartIcon Icons.Material.Filled.RestartAlt
                Disabled isScanning
                OnClick(fun _ -> invoicesModule.RescanEverything())
                "Rescan everything"
            }
        }
    }

/// "37 invoice(s), mail received in the last 90 days" - the count and window above the table.
let private countLine (invoicesModule: InvoicesModule) =
    adapt {
        let! invoices = invoicesModule.InvoicesAval
        let! windows = invoicesModule.ScanWindowsAval
        let! selectedDays = invoicesModule.SelectedWindowDaysAval

        let label =
            windows
            |> List.tryFind (fun window -> window.Days = selectedDays)
            |> Option.map (fun window -> window.Label)
            |> Option.defaultValue $"the last {selectedDays} days"

        MudText'' {
            Typo Typo.body2
            $"{List.length invoices} invoice(s), {label}."
        }
    }

let private errorAlert (invoicesModule: InvoicesModule) =
    adapt {
        let! error = invoicesModule.ErrorAval

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
let invoicesTable (invoicesModule: InvoicesModule) (confirmAndDelete: InvoiceUiType -> unit) =
    fragment {
        errorAlert invoicesModule
        windowPicker invoicesModule
        countLine invoicesModule

        adapt {
            let! invoices = invoicesModule.InvoicesAval
            let! isScanning = invoicesModule.IsScanningAval

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
let problemsView (invoicesModule: InvoicesModule) =
    adapt {
        let! problems = invoicesModule.ProblemsAval

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

/// The tombstones view with an un-delete. The page calls invoicesModule.LoadTombstones() when this tab opens.
let tombstonesView (invoicesModule: InvoicesModule) =
    adapt {
        let! tombstones = invoicesModule.TombstonesAval

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
                            OnClick(fun _ -> invoicesModule.UndeleteInvoice tombstone.SupplierId tombstone.Reference)
                            "Un-delete"
                        }
                    }
                })
        }
    }
