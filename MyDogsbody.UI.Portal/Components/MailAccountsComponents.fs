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

/// Binary units, one decimal above bytes. Public so the figure the table shows is asserted
/// directly rather than re-derived by a test that could drift from it.
let formatSizeBytes (bytes: int64) : string =
    let kb = 1024.0
    let mb = kb * 1024.0
    let gb = mb * 1024.0
    let value = float bytes

    if value < kb then $"{bytes} bytes"
    elif value < mb then $"%.1f{value / kb} KB"
    elif value < gb then $"%.1f{value / mb} MB"
    else $"%.1f{value / gb} GB"

/// The account's whole on-disk size - every folder, scannable or not.
let accountSizeBytes (account: MailAccountUiType) : int64 =
    account.Folders |> List.sumBy (fun folder -> folder.SizeBytes)

/// What a scan will actually read: Trash/Deleted/Junk/Sent/Drafts are excluded from the
/// scannable set (Q4.8), and on the measured profile that is 9.0 GB of 15.2 GB - the difference
/// between a feasible scan and an infeasible one, so the page states it before the scan is run.
let scannableSizeBytes (account: MailAccountUiType) : int64 =
    account.Folders |> List.filter (fun folder -> folder.IsScannable) |> List.sumBy (fun folder -> folder.SizeBytes)

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
                        MudTh''{ "Size" }
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
                        MudTd''{
                            fragment {
                                MudText''{ formatSizeBytes (accountSizeBytes account) }

                                // What the scan will actually read, stated before it is run -
                                // the excluded folders are most of the bytes on a real profile.
                                MudText''{
                                    Typo Typo.caption
                                    $"{formatSizeBytes (scannableSizeBytes account)} to scan"
                                }
                            }
                        }
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
