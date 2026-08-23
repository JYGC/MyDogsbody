namespace MyDogsbody.UI.Types.Module

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types

type MailAccountsBrowserModule =
    {
        ProfileRootAval: aval<string option>
        AccountsAval: aval<MailAccountUiType list>
        SelectedAccountIdAval: aval<string option>
        UnreadableAval: aval<UnreadableDirectoryUiType list>
        /// The last scan cleared a selection naming an account it did not find. Shown until a
        /// later scan clears nothing, the same way ErrorAval is cleared by the next success.
        SelectionClearedAval: aval<bool>
        /// A scan is running - shown, but never blocks the interface (requirements.md).
        IsScanningAval: aval<bool>
        IsLoadingAval: aval<bool>
        /// The message from the last failed operation, cleared by the next successful one.
        ErrorAval: aval<string option>
        SetProfileRoot: string -> unit
        ScanForAccounts: unit -> unit
        SelectAccount: string -> unit
        CountMessages: string -> unit
        ClearWatermarks: string -> unit
    }
