namespace MyDogsbody.UI.Types

/// Chooses a filesystem folder. The domain never opens dialogs, so this is a UI-level service
/// type, not a domain dependency - registered by the WPF host in production (using the native
/// folder dialog), substituted with a lambda in tests. See CLAUDE-project.md -> Architecture ->
/// "The folder picker is not a domain dependency".
type FolderPicker = unit -> string option

/// A thin conversion so the WPF host (C#) can hand over a plain `System.Func<string>` -
/// returning null for "the user cancelled" - without needing to construct an
/// FSharpFunc/FSharpOption by hand. Named separately from the `FolderPicker` type (rather than
/// as a same-named companion module) so its compiled class name is predictable from C#.
module FolderPickerInterop =
    open Microsoft.FSharp.Core

    /// `FSharpFunc<_,_>.FromConverter` rather than a bare `fun () -> ...` returned from a `let`:
    /// F# compiles a public `let` whose declared type has more than one `->` as a genuinely
    /// multi-argument CLR method (curry-flattened), which breaks "build once, invoke later from
    /// C#" - `ofFunc someFunc` alone would not even compile as a partial application from C#.
    /// FromConverter forces the result to be one opaque FSharpFunc object instead.
    let ofFunc (chooseFolder: System.Func<string>) : FolderPicker =
        FSharpFunc<unit, string option>.FromConverter(fun () ->
            match chooseFolder.Invoke() with
            | null -> None
            | path -> Some path)
