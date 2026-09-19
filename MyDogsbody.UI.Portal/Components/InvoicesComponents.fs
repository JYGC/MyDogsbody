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

/// The plan rows a "sync now" press would act on right now, mirroring
/// `InvoicesModuleCreators.rowsMatchingSelection` exactly (that module compiles before this one, so
/// it cannot be reused from here - the function is five lines and kept identical by inspection
/// rather than worth a shared module for). Used by the page to decide whether the confirmation in
/// task 8.5 is needed and what to list in it.
let rowsPendingSync (selectedInvoiceIds: Set<string>) (syncView: SyncViewUiType) : SyncPlanRowUiType list =
    if Set.isEmpty selectedInvoiceIds then
        syncView.Plan
    else
        syncView.Plan
        |> List.filter (fun planRow ->
            match planRow.InvoiceId with
            | Some invoiceId -> Set.contains invoiceId selectedInvoiceIds
            | None -> false)

let private formatSyncAction (action: SyncPlanActionUiType) =
    match action with
    | CreateSyncAction -> "Create"
    | UpdateSyncAction -> "Update"
    | DeleteSyncAction -> "Delete"

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
            let! selectedInvoiceIds = invoicesModule.SelectedInvoiceIdsAval

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
                        MudTh'' { "" }
                        MudTh'' { "Supplier" }
                        MudTh'' { "Reference" }
                        MudTh'' { "Amount" }
                        MudTh'' { "Issued" }
                        MudTh'' { "Due" }
                        MudTh'' { "Calendar sync" }
                        MudTh'' { "" }
                    }
                )

                RowTemplate(fun (invoice: InvoiceUiType) ->
                    fragment {
                        MudTd'' {
                            // Task 8.2/8.8: only a row that can become a calendar event is ever
                            // selectable - a delete has no InvoiceId and so can never be ticked by
                            // invoice id (it rides along in "everything outstanding" instead).
                            if invoice.CanBecomeCalendarEvent then
                                MudCheckBox'' {
                                    Value(Set.contains invoice.Id selectedInvoiceIds)
                                    Color Color.Primary
                                    ValueChanged(fun (_: bool) -> invoicesModule.ToggleInvoice invoice.Id)
                                }
                        }

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
                            // Task 8.3: three per-row states when ready; an invoice that cannot
                            // become a calendar event shows no status of its own - its reason is
                            // already the "Due" cell above, not repeated here.
                            match invoice.CanBecomeCalendarEvent, invoice.SyncStatus with
                            | false, _ ->
                                MudText'' {
                                    Typo Typo.caption
                                    "not uploadable"
                                }
                            | true, Some UpToDateSync ->
                                MudChip'' {
                                    Color Color.Success
                                    "Up to date"
                                }
                            | true, Some MissingSync ->
                                MudChip'' {
                                    Color Color.Warning
                                    "Missing"
                                }
                            | true, Some ChangedSync ->
                                MudChip'' {
                                    Color Color.Info
                                    "Changed"
                                }
                            | true, None ->
                                MudText'' {
                                    Typo Typo.caption
                                    "-"
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

/// The plan preview (task 8.4): every outstanding action, naming the invoice, shown before anything
/// runs (Q2.13).
let private syncPlanPreview (plan: SyncPlanRowUiType list) =
    MudTable'' {
        Items plan
        Dense true

        HeaderContent(
            fragment {
                MudTh'' { "Supplier" }
                MudTh'' { "Reference" }
                MudTh'' { "Due" }
                MudTh'' { "Action" }
            }
        )

        RowTemplate(fun (row: SyncPlanRowUiType) ->
            fragment {
                MudTd'' { row.SupplierName }
                MudTd'' { row.Reference }
                MudTd'' { formatDate row.DueDate }

                MudTd'' {
                    match row.Action with
                    | CreateSyncAction ->
                        MudChip'' {
                            Color Color.Success
                            "Create"
                        }
                    | UpdateSyncAction ->
                        MudChip'' {
                            Color Color.Info
                            "Update"
                        }
                    | DeleteSyncAction ->
                        MudChip'' {
                            Color Color.Error
                            "Delete"
                        }
                }
            })
    }

/// The orphaned-events view (task 8.6): events the plan does not fully explain, labelled with why
/// (PR #23 review round 3's OrphanedEventReasonUiType). A keyless or unparseable-key event, and a
/// duplicate of another event's sync key, both NEED ATTENTION - neither is ever a deletion
/// candidate, because the app deletes only what it can prove it created, and requirements.md asks
/// the duplicate be reported "as a duplicate" rather than folded into the keyless case. A
/// pending-delete orphan (its invoice already left the ledger) is shown too, distinctly, even
/// though the same row already appears in the plan above.
let private orphanedEventsView (invoicesModule: InvoicesModule) =
    adapt {
        let! syncView = invoicesModule.SyncViewAval

        if not (List.isEmpty syncView.OrphanedEvents) then
            fragment {
                MudText'' {
                    Typo Typo.subtitle2
                    "Calendar events the plan does not fully explain"
                }

                MudTable'' {
                    Items syncView.OrphanedEvents
                    Dense true

                    HeaderContent(
                        fragment {
                            MudTh'' { "Title" }
                            MudTh'' { "Date" }
                            MudTh'' { "Status" }
                        }
                    )

                    RowTemplate(fun (orphan: OrphanedEventUiType) ->
                        fragment {
                            MudTd'' { orphan.Title }
                            MudTd'' { orphan.Date.ToString("d MMM yyyy") }

                            MudTd'' {
                                match orphan.Reason with
                                | NoRecognisableSyncKey ->
                                    MudChip'' {
                                        Color Color.Warning
                                        "Needs attention - no recognisable sync key"
                                    }
                                | DuplicateOfAnotherEventsSyncKey ->
                                    MudChip'' {
                                        Color Color.Warning
                                        "Needs attention - duplicate event for the same invoice"
                                    }
                                | InvoiceAlreadyLeftTheLedger ->
                                    MudChip'' {
                                        Color Color.Default
                                        "Its invoice has left the ledger - pending delete, above"
                                    }
                            }
                        })
                }
            }
    }

/// Per-row results of the most recent sync run (task 8.7, Q2.8's partial-failure reporting).
let private lastSyncOutcomesView (invoicesModule: InvoicesModule) =
    adapt {
        let! outcomes = invoicesModule.LastSyncOutcomesAval

        if not (List.isEmpty outcomes) then
            fragment {
                MudText'' {
                    Typo Typo.subtitle2
                    "Result of the last sync"
                }

                MudTable'' {
                    Items outcomes
                    Dense true

                    HeaderContent(
                        fragment {
                            MudTh'' { "Supplier" }
                            MudTh'' { "Reference" }
                            MudTh'' { "Action" }
                            MudTh'' { "Result" }
                        }
                    )

                    RowTemplate(fun (outcome: SyncOutcomeRowUiType) ->
                        fragment {
                            MudTd'' { outcome.SupplierName }
                            MudTd'' { outcome.Reference }
                            MudTd'' { formatSyncAction outcome.Action }

                            MudTd'' {
                                match outcome.Result with
                                | SyncSucceeded ->
                                    MudChip'' {
                                        Color Color.Success
                                        "Succeeded"
                                    }
                                // EventNoLongerExists: the calendar already agreed with the target
                                // state, so this is a success, not a failure.
                                | SyncAlreadyGone ->
                                    MudChip'' {
                                        Color Color.Success
                                        "Already up to date"
                                    }
                                | SyncFailed message ->
                                    MudChip'' {
                                        Color Color.Error
                                        $"Failed: {message}"
                                    }
                            }
                        })
                }
            }
    }

/// The calendar sync controls and plan, shown alongside the invoices table: the app-owned notice
/// (task 8.9, Q2.14), the bulk button naming the count it will act on and disabled with its reason
/// when nothing can run (task 8.8), the plan itself before anything runs (task 8.4), the orphaned
/// events (task 8.6) and the last run's per-row outcomes (task 8.7).
///
/// onSyncRequested is a page-supplied callback (task 8.5's delete confirmation needs
/// IDialogService, which this component does not have - components never call the API or open
/// dialogs directly, the page passes callbacks in).
let calendarSyncSection (invoicesModule: InvoicesModule) (onSyncRequested: unit -> unit) =
    fragment {
        MudText'' {
            Typo Typo.h6
            "Calendar sync"
        }

        MudAlert'' {
            Severity Severity.Info
            Variant Variant.Text
            Dense true
            "Calendar events this app creates are app-owned: syncing overwrites a hand-edited title or date on one of them."
        }

        adapt {
            let! syncView = invoicesModule.SyncViewAval
            let! pendingActionCount = invoicesModule.PendingActionCountAval
            let! isSyncing = invoicesModule.IsSyncingAval

            // Q2.11: no account ready at all disables the button with its reason. With an account
            // ready but nothing outstanding, task 8.10 says so plainly instead of leaving an
            // enabled button that would do nothing.
            let disabledReason =
                match syncView.NotReadyReason with
                | Some reason -> Some reason
                | None when pendingActionCount = 0 -> Some "Up to date - nothing to sync."
                | None -> None

            div {
                style' "display:flex; gap:1rem; align-items:center"

                MudTooltip'' {
                    Text(disabledReason |> Option.defaultValue "")

                    MudButton'' {
                        Variant Variant.Filled
                        Color Color.Primary
                        StartIcon Icons.Material.Filled.Sync
                        Disabled(disabledReason.IsSome || isSyncing)
                        OnClick(fun _ -> onSyncRequested ())
                        $"Sync now ({pendingActionCount})"
                    }
                }

                match disabledReason with
                | Some reason ->
                    MudText'' {
                        Typo Typo.body2
                        Color Color.Warning
                        reason
                    }
                | None -> ()
            }

            match syncView.NotReadyReason with
            | Some _ -> ()
            | None ->
                if not (List.isEmpty syncView.Plan) then
                    fragment {
                        MudText'' {
                            Typo Typo.subtitle2
                            "Outstanding actions"
                        }

                        syncPlanPreview syncView.Plan
                    }
        }

        orphanedEventsView invoicesModule
        lastSyncOutcomesView invoicesModule
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
