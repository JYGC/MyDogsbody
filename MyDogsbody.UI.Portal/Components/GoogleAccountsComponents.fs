module MyDogsbody.UI.Portal.Components.GoogleAccountsComponents

open Fun.Blazor
open MudBlazor
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

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
