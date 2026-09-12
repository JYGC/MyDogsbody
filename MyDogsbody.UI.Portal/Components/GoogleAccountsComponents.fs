module MyDogsbody.UI.Portal.Components.GoogleAccountsComponents

open Fun.Blazor
open MudBlazor
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// What to say under an account's calendar picker when there is nothing in it to pick
/// (requirements.md: "WHEN an account has no calendars at all THE SYSTEM SHALL show an empty picker
/// with a message, not an error"). Only an account whose calendars have *loaded* and come back empty
/// earns it: `CalendarsByAccountIdAval` has no entry for an account whose calendars have not loaded
/// yet, or whose only fetch so far failed - and a failure is already the page's `MudAlert`, so
/// claiming "no calendars" there would state something nobody has checked. A reload keeps the last
/// list that did load until a new one replaces it (the picker's options too), so a re-fetch that
/// fails leaves the last answer showing beside the alert rather than blanking it.
let noCalendarsMessage (calendarsByAccountId: Map<string, CalendarUiType list>) (accountId: string) : string option =
    match Map.tryFind accountId calendarsByAccountId with
    | Some [] -> Some "No calendars were found for this account."
    | _ -> None

/// The remove confirmation's sentence (requirements.md: "SHALL say that access is still granted at
/// Google and can be revoked there - so the user is not left believing more happened than did",
/// Q3.6). Names the account by its email, the way the table tells accounts apart.
let removeConfirmationMessage (account: GoogleAccountUiType) : string =
    $"Remove '{account.EmailAddress}'? Its local token and record are deleted, but access remains granted at Google - you can revoke it there."

/// Asks before removing (requirements.md: "ask for confirmation, stating that access remains granted
/// at Google"), and removes only on a yes. The removal arrives as a callback - the page passes the
/// module's `RemoveAccount` - so this reaches no API itself, and an E2E test drives the same dialog
/// production shows.
let confirmAndRemove (dialogService: IDialogService) (removeAccount: string -> unit) (account: GoogleAccountUiType) : unit =
    task {
        let! confirmed =
            dialogService.ShowMessageBox(
                title = "Remove Google account",
                message = removeConfirmationMessage account,
                yesText = "Remove",
                cancelText = "Cancel"
            )

        if confirmed.HasValue && confirmed.Value then
            removeAccount account.Id
    }
    :> System.Threading.Tasks.Task
    |> ignore

let googleAccountsBrowser
    (m: GoogleAccountsBrowserModule)
    (confirmAndRemove: GoogleAccountUiType -> unit)
    =
    fragment {
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
        adapt {
            let! secret = m.ClientSecretAval
            let! isEditing = m.IsEditingClientSecretAval

            if isEditing then
                // requirements.md: pre-filled with the currently stored value, so a correction
                // does not require retyping the whole secret from memory.
                let mutable editedSecret = secret |> Option.defaultValue ""

                MudAlert'' {
                    Severity Severity.Info
                    fragment {
                        MudText'' { "Google client secret (JSON)" }
                        // requirements.md: replacing the secret states that existing accounts may
                        // need re-authorising - a replacement can belong to a different OAuth
                        // client, and Google will not refresh a token issued to the old one. Only
                        // when a secret is already stored: a first one has no accounts behind it.
                        if secret.IsSome then
                            MudText'' {
                                Typo Typo.caption
                                "Replacing the client secret may mean existing accounts need re-authorising."
                            }
                        MudStack'' {
                            Row true
                            class' "pt-2"
                            MudTextField'' {
                                Label "Client secret (JSON)"
                                Lines 3
                                Value(secret |> Option.defaultValue "")
                                ValueChanged(fun (v: string) -> editedSecret <- v)
                            }
                            MudButton'' {
                                Variant Variant.Filled
                                Color Color.Primary
                                OnClick(fun _ -> m.SetClientSecret editedSecret)
                                "Save"
                            }
                            MudButton'' {
                                Variant Variant.Text
                                OnClick(fun _ -> m.CancelEditingClientSecret())
                                "Cancel"
                            }
                        }
                    }
                }
            else
                match secret with
                | None ->
                    // requirements.md: shown first, with somewhere to paste it - account
                    // registration stays disabled below until this is supplied.
                    MudAlert'' {
                        Severity Severity.Info
                        fragment {
                            MudText'' { "No Google client secret has been supplied yet." }
                            MudButton'' {
                                Variant Variant.Filled
                                Color Color.Primary
                                class' "pt-2"
                                OnClick(fun _ -> m.StartEditingClientSecret())
                                "Add client secret"
                            }
                        }
                    }
                | Some currentSecret ->
                    // requirements.md: shown read-only, never open for editing by default - an
                    // explicit "Edit" click is needed before it becomes an editable field.
                    MudStack'' {
                        Row true
                        AlignItems AlignItems.Center
                        class' "py-2"
                        MudTextField'' {
                            Label "Google client secret (JSON)"
                            Lines 3
                            Value currentSecret
                            ReadOnly true
                        }
                        MudButton'' {
                            Variant Variant.Text
                            OnClick(fun _ -> m.StartEditingClientSecret())
                            "Edit"
                        }
                    }
        }
        adapt {
            let! secret = m.ClientSecretAval
            let! isRegistering = m.IsRegisteringAval
            let buttonText = if isRegistering then "Adding account..." else "Add account"

            MudButton'' {
                Variant Variant.Filled
                Color Color.Secondary
                class' "py-2"
                Disabled(secret.IsNone || isRegistering)
                OnClick(fun _ -> m.RegisterAccount())
                buttonText
            }
        }
        adapt {
            let! accounts = m.AccountsAval
            let! calendarsByAccountId = m.CalendarsByAccountIdAval
            let! isLoading = m.IsLoadingAval

            MudTable'' {
                Items accounts
                Breakpoint Breakpoint.Sm
                Loading isLoading
                FixedHeader true
                LoadingProgressColor Color.Info
                Striped true
                Height "70vh"
                NoRecordsContent(fragment { MudText'' { "No Google accounts registered yet." } })
                ToolBarContent(
                    fragment {
                        MudText'' {
                            Typo Typo.h3
                            "Google accounts"
                        }
                    }
                )
                HeaderContent(
                    fragment {
                        MudTh'' { "Account" }
                        MudTh'' { "Default invoice calendar" }
                        MudTh'' { "Status" }
                        MudTh'' { "" }
                    }
                )
                RowTemplate(fun (account: GoogleAccountUiType) ->
                    // Populated by every reload of the accounts table (module creator), for every
                    // account that is ready to have one - so it is normal for this to be empty
                    // for an account that still needs re-authorising.
                    let calendars = calendarsByAccountId |> Map.tryFind account.Id |> Option.defaultValue []

                    fragment {
                        MudTd'' { account.EmailAddress }
                        MudTd'' {
                            if account.NeedsReauthorisation then
                                MudText'' {
                                    Typo Typo.caption
                                    Color Color.Warning
                                    "Re-authorise to choose a calendar"
                                }
                            else
                                fragment {
                                    MudSelect'' {
                                        Dense true
                                        Value(account.DefaultInvoiceCalendarId |> Option.defaultValue "")
                                        ValueChanged(fun (calendarId: string) ->
                                            if not (System.String.IsNullOrEmpty calendarId) then
                                                m.SetDefaultInvoiceCalendar account.Id calendarId)
                                        fragment {
                                            for calendar in calendars do
                                                let label = if calendar.IsPrimary then $"{calendar.Name} (primary)" else calendar.Name

                                                MudSelectItem'' {
                                                    Value calendar.Id
                                                    label
                                                }
                                        }
                                    }

                                    match noCalendarsMessage calendarsByAccountId account.Id with
                                    | Some message ->
                                        MudText'' {
                                            Typo Typo.caption
                                            message
                                        }
                                    | None -> ()
                                }
                        }
                        MudTd'' {
                            if account.NeedsReauthorisation then
                                MudChip'' {
                                    Color Color.Warning
                                    "Needs re-authorisation"
                                }
                            elif account.DefaultInvoiceCalendarId.IsNone then
                                // Q2.11: genuinely has none yet - not an error, not a loading gap.
                                MudChip'' {
                                    Color Color.Default
                                    "Not ready - no calendar chosen"
                                }
                            else
                                MudChip'' {
                                    Color Color.Success
                                    "Ready"
                                }
                        }
                        MudTd'' {
                            MudStack'' {
                                Row true
                                if account.NeedsReauthorisation then
                                    MudButton'' {
                                        Variant Variant.Text
                                        Size Size.Small
                                        Color Color.Warning
                                        OnClick(fun _ -> m.ReauthoriseAccount account.Id)
                                        "Re-authorise"
                                    }
                                MudButton'' {
                                    Variant Variant.Text
                                    Size Size.Small
                                    Color Color.Error
                                    OnClick(fun _ -> confirmAndRemove account)
                                    "Remove"
                                }
                            }
                        }
                    })
            }
        }
    }
