module MyDogsbody.Integrations.Thunderbird.ThunderbirdFolderScanner

open System
open System.Collections.Generic
open System.IO
open MyDogsbody.Domain.MailAccounts

/// Stops an unexpectedly large or looping tree from running indefinitely - friction #13.
[<Literal>]
let MaxDepth = 12

type ScanOutcome =
    {
        /// Every directory that directly contains a prefs.js, treated as a profile root.
        ProfileDirectories: string list
        Unreadable: UnreadableDirectory list
    }

/// Walks `rootPath` recursively for `prefs.js` files - the chosen folder may itself be one
/// profile, the parent of several, or a backup copy, and this handles all three without
/// configuration (Q4.2). Visited directories are tracked by their canonical (fully resolved)
/// path, not the path string reached through, so a Windows junction cannot produce a loop.
let scan (rootPath: string) : ScanOutcome =
    let visited = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let profiles = ResizeArray<string>()
    let unreadable = ResizeArray<UnreadableDirectory>()

    // Path.GetFullPath only normalises `.`/`..` and separators - it does NOT resolve a
    // directory junction or symlink to what it actually points at, so two different path
    // strings can reach the same physical directory without this ever noticing. Resolving the
    // reparse point to its final target is what makes a junction pointing at an ancestor
    // collapse onto the same visited entry as that ancestor.
    let canonicalize (dir: string) : string option =
        try
            let info = DirectoryInfo(Path.GetFullPath dir)

            match info.ResolveLinkTarget true with
            | null -> Some info.FullName
            | target -> Some target.FullName
        with _ ->
            None

    let rec walk (dir: string) (depth: int) =
        if depth > MaxDepth then
            ()
        else
            let canonical = canonicalize dir

            match canonical with
            | None -> ()
            | Some canonicalPath ->
                if not (visited.Add canonicalPath) then
                    () // already visited by another path - a junction loop, most likely
                else
                    let entries =
                        try
                            Ok(Directory.GetFileSystemEntries dir)
                        with
                        | :? UnauthorizedAccessException as ex -> Error ex.Message
                        | :? IOException as ex -> Error ex.Message

                    match entries with
                    | Error reason -> unreadable.Add { Path = dir; Reason = reason }
                    | Ok entries ->
                        let hasPrefsJs =
                            entries
                            |> Array.exists (fun entry ->
                                not (Directory.Exists entry)
                                && String.Equals(Path.GetFileName entry, "prefs.js", StringComparison.OrdinalIgnoreCase))

                        if hasPrefsJs then
                            profiles.Add dir

                        for entry in entries do
                            if Directory.Exists entry then
                                walk entry (depth + 1)

    walk rootPath 0

    {
        ProfileDirectories = List.ofSeq profiles
        Unreadable = List.ofSeq unreadable
    }
