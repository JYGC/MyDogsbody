module MyDogsbody.Integrations.Thunderbird.MailFolderEnumerator

open System
open System.IO
open MyDogsbody.Domain.MailAccounts

/// Excluded from the scannable set - Q4.8. On the measured profile this removes 9.0 GB of
/// 15.2 GB, the difference between a feasible scan and an infeasible one.
let private nonScannableFolderNames =
    set [ "Trash"; "Deleted"; "Junk"; "Sent"; "Drafts" ]

/// Decided by the folder's own name, not its position in the tree.
let private isScannable (displayName: string) = not (nonScannableFolderNames.Contains displayName)

/// An extensionless file is a folder's mbox; a sibling `<name>.sbd` directory holds its
/// children, repeating to arbitrary depth. `.msf` files are ignored entirely - they are Mork,
/// and the measured profile has `.msf` files with no matching mbox at all (friction #4). A bare
/// directory that is not a `.sbd` companion of a file found here is never walked directly - it
/// is reached only via `<name>.sbd`.
let rec private enumerateMboxLevel (directory: string) (relativePrefix: string) : MailFolder list =
    if not (Directory.Exists directory) then
        []
    else
        Directory.GetFileSystemEntries directory
        |> Array.toList
        |> List.collect (fun entry ->
            let name = Path.GetFileName entry

            if Directory.Exists entry then
                [] // reached via its sibling file's <name>.sbd lookup below, not walked directly
            elif String.Equals(Path.GetExtension entry, ".msf", StringComparison.OrdinalIgnoreCase) then
                []
            else
                let relativePath = if relativePrefix = "" then name else relativePrefix + "/" + name
                let sizeBytes = FileInfo(entry).Length

                let folder =
                    {
                        RelativePath = relativePath
                        DisplayName = name
                        SizeBytes = sizeBytes
                        IsScannable = isScannable name
                    }

                let children = enumerateMboxLevel (entry + ".sbd") relativePath
                folder :: children)

let private enumerateMbox (storeDirectory: string) : MailFolder list = enumerateMboxLevel storeDirectory ""

/// A directory holding `cur`, `new` and `tmp` is a folder (synthetic-only, Q4.11 - the measured
/// profile is 100% mbox). Its size is the sum of the files actually delivered (`cur` and `new`;
/// `tmp` holds only in-progress deliveries). Any other subdirectory is checked the same way,
/// recursively, so nested maildir folders are found without a separate case.
let rec private enumerateMaildirLevel
    (directory: string)
    (relativePrefix: string)
    (displayNameOverride: string option)
    : MailFolder list =
    let hasAllThree =
        [ "cur"; "new"; "tmp" ] |> List.forall (fun sub -> Directory.Exists(Path.Combine(directory, sub)))

    let ownFolder =
        if hasAllThree then
            let displayName = displayNameOverride |> Option.defaultValue (Path.GetFileName directory)
            let relativePath = if relativePrefix = "" then displayName else relativePrefix

            let sizeBytes =
                [ "cur"; "new" ]
                |> List.sumBy (fun sub ->
                    Directory.GetFiles(Path.Combine(directory, sub)) |> Array.sumBy (fun f -> FileInfo(f).Length))

            [
                {
                    RelativePath = relativePath
                    DisplayName = displayName
                    SizeBytes = sizeBytes
                    IsScannable = isScannable displayName
                }
            ]
        else
            []

    let childFolders =
        Directory.GetDirectories directory
        |> Array.filter (fun d ->
            let name = Path.GetFileName d
            name <> "cur" && name <> "new" && name <> "tmp")
        |> Array.toList
        |> List.collect (fun d ->
            let name = Path.GetFileName d
            let relativePath = if relativePrefix = "" then name else relativePrefix + "/" + name
            enumerateMaildirLevel d relativePath (Some name))

    ownFolder @ childFolders

let private enumerateMaildir (storeDirectory: string) : MailFolder list =
    if not (Directory.Exists storeDirectory) then
        []
    else
        enumerateMaildirLevel storeDirectory "" None

/// Enumerates one account's folders, dispatching on its store format. `storeDirectory` not
/// existing (a configured-but-missing account) is not this function's concern to report - it
/// simply has no folders.
let enumerate (storeDirectory: string) (format: StoreFormat) : MailFolder list =
    match format with
    | Mbox -> enumerateMbox storeDirectory
    | Maildir -> enumerateMaildir storeDirectory

/// The inverse of enumeration: turns a folder's logical `RelativePath` back into its real
/// on-disk path. For mbox this is NOT a plain separator substitution - every segment except the
/// last names a `<segment>.sbd` directory, because mbox nests a folder's children under a
/// `.sbd`-suffixed SIBLING directory rather than one with the same name. For maildir, nesting
/// already uses plain directory names, so the relative path already is the filesystem path.
let resolvePath (storeDirectory: string) (format: StoreFormat) (relativePath: string) : string =
    match format with
    | Maildir -> Path.Combine(storeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar))
    | Mbox ->
        let segments = relativePath.Split('/')

        let sbdDirectories =
            if segments.Length <= 1 then [||] else segments.[.. segments.Length - 2] |> Array.map (fun s -> s + ".sbd")

        let allParts = Array.append sbdDirectories [| segments.[segments.Length - 1] |]
        Array.fold (fun acc part -> Path.Combine(acc, part)) storeDirectory allParts
