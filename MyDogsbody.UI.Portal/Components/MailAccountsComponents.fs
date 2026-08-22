module MyDogsbody.UI.Portal.Components.MailAccountsComponents

open System
open Fun.Blazor
open MudBlazor
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

let private formatCachedCount (count: (int * DateTime) option) =
    match count with
    | Some(n, takenAt) -> $"{n} (as of {takenAt:g})"
    | None -> "Not counted yet"

let mailAccountsBrowser (mailAccountsBrowserModule: MailAccountsBrowserModule) (folderPicker: FolderPicker) =
    fragment {
        adapt {
            let! error = mailAccountsBrowserModule.ErrorAval

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
            let! profileRoot = mailAccountsBrowserModule.ProfileRootAval
            let! isScanning = mailAccountsBrowserModule.IsScanningAval
            let scanButtonText = if isScanning then "Scanning..." else "Scan for accounts"

            match profileRoot with
            | None ->
                // The app is unusable until this is set - an explicit invitation, not an empty
                // table (requirements.md -> "Choosing the profile folder").
                MudAlert''{
                    Severity Severity.Info
                    "No Thunderbird profile folder has been chosen yet. Choose one below to get started."
                }
            | Some path -> MudText''{ $"Profile folder: {path}" }

            MudStack''{
                Row true
                class' "py-2"
                MudButton''{
                    Variant Variant.Filled
                    Color Color.Primary
                    OnClick(fun _ ->
                        match folderPicker () with
                        | Some path -> mailAccountsBrowserModule.SetProfileRoot path
                        | None -> ())
                    "Browse"
                }
                MudButton''{
                    Variant Variant.Filled
                    Color Color.Secondary
                    Disabled(isScanning || profileRoot.IsNone)
                    OnClick(fun _ -> mailAccountsBrowserModule.ScanForAccounts())
                    scanButtonText
                }
            }
        }
        adapt {
            let! unreadable = mailAccountsBrowserModule.UnreadableAval

            if not unreadable.IsEmpty then
                MudAlert''{
                    Severity Severity.Warning
                    Variant Variant.Outlined
                    fragment {
                        MudText''{ "Some directories could not be read:" }

                        for u in unreadable do
                            MudText''{
                                Typo Typo.caption
                                $"{u.Path} - {u.Reason}"
                            }
                    }
                }
        }
        adapt {
            let! accounts = mailAccountsBrowserModule.AccountsAval
            let! selectedAccountId = mailAccountsBrowserModule.SelectedAccountIdAval
            let! isLoading = mailAccountsBrowserModule.IsLoadingAval

            MudTable''{
                Items accounts
                Breakpoint Breakpoint.Sm
                Loading isLoading
                FixedHeader true
                LoadingProgressColor Color.Info
                Striped true
                Height "70vh"
                NoRecordsContent(fragment { MudText''{ "No accounts discovered yet - choose a profile folder and scan." } })
                ToolBarContent(
                    fragment {
                        MudText''{
                            Typo Typo.h3
                            "Mail accounts"
                        }
                    }
                )
                HeaderContent(
                    fragment {
                        MudTh''{ "" }
                        MudTh''{ "Account" }
                        MudTh''{ "Email addresses" }
                        MudTh''{ "Format" }
                        MudTh''{ "Folders" }
                        MudTh''{ "Message count" }
                        MudTh''{ "" }
                    }
                )
                RowTemplate(fun (account: MailAccountUiType) ->
                    let isSelected = selectedAccountId = Some account.Id

                    fragment {
                        MudTd''{
                            MudCheckBox''{
                                Value isSelected
                                Color Color.Primary
                                Disabled(not account.StoreDirectoryExists)
                                ValueChanged(fun (_: bool) -> mailAccountsBrowserModule.SelectAccount account.Id)
                            }
                        }
                        MudTd''{
                            fragment {
                                MudText''{ account.DisplayName }

                                if not account.StoreDirectoryExists then
                                    MudText''{
                                        Color Color.Error
                                        Typo Typo.caption
                                        "Configured, but its store directory is missing"
                                    }
                            }
                        }
                        MudTd''{ String.concat ", " account.EmailAddresses }
                        MudTd''{ account.StoreFormat }
                        MudTd''{ $"{account.Folders.Length}" }
                        MudTd''{ formatCachedCount account.CachedMessageCount }
                        MudTd''{
                            MudStack''{
                                Row true
                                MudButton''{
                                    Variant Variant.Text
                                    Size Size.Small
                                    OnClick(fun _ -> mailAccountsBrowserModule.CountMessages account.Id)
                                    "Count messages"
                                }
                                MudButton''{
                                    Variant Variant.Text
                                    Size Size.Small
                                    Color Color.Warning
                                    OnClick(fun _ -> mailAccountsBrowserModule.ClearWatermarks account.Id)
                                    "Full rescan"
                                }
                            }
                        }
                    })
            }
        }
    }
