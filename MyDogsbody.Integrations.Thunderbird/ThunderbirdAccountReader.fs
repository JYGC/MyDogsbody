module MyDogsbody.Integrations.Thunderbird.ThunderbirdAccountReader

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open MyDogsbody.Domain.MailAccounts

/// Matches one `user_pref("key", value);` line. The key never contains an unescaped quote in
/// practice; the value is captured lazily up to the trailing `);` so a quoted string containing
/// its own escaped characters is captured whole.
let private userPrefPattern =
    Regex(
        @"^\s*user_pref\(\s*""(?<key>(?:[^""\\]|\\.)*)""\s*,\s*(?<value>.+?)\s*\);\s*$",
        RegexOptions.Compiled
    )

/// prefs.js escapes `"` and `\` with a leading backslash. This walks the string once, char by
/// char, so `\"` and `\\` are each resolved exactly once rather than double-processed.
let private unescapeJsString (raw: string) : string =
    let sb = Text.StringBuilder(raw.Length)
    let mutable i = 0

    while i < raw.Length do
        if raw.[i] = '\\' && i + 1 < raw.Length then
            sb.Append(raw.[i + 1]) |> ignore
            i <- i + 2
        else
            sb.Append(raw.[i]) |> ignore
            i <- i + 1

    sb.ToString()

let private parseValue (raw: string) : string =
    let trimmed = raw.Trim()

    if trimmed.Length >= 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("\"") then
        unescapeJsString (trimmed.Substring(1, trimmed.Length - 2))
    else
        trimmed // a bare number or boolean literal - kept as text, none of it is used as one

let private parsePrefs (lines: string seq) : IReadOnlyDictionary<string, string> =
    let prefs = Dictionary<string, string>()

    for line in lines do
        let m = userPrefPattern.Match line

        if m.Success then
            let key = unescapeJsString m.Groups.["key"].Value
            let value = parseValue m.Groups.["value"].Value
            prefs.[key] <- value

    prefs

let private tryGet (prefs: IReadOnlyDictionary<string, string>) (key: string) : string option =
    match prefs.TryGetValue key with
    | true, value -> Some value
    | false, _ -> None

let private splitCsv (value: string) : string list =
    value.Split(',')
    |> Array.map (fun v -> v.Trim())
    |> Array.filter (fun v -> v <> "")
    |> Array.toList

/// `directory-rel` looks like "[ProfD]ImapMail/imap.alpha.example.com". [ProfD] resolves
/// against `profileDir` - the profile's own folder - never against the absolute `directory`
/// value, which the measured profile showed can be stale (design.md -> "Reading the profile").
let private resolveProfD (profileDir: string) (directoryRel: string) : string =
    let marker = "[ProfD]"

    let relative =
        if directoryRel.StartsWith(marker, StringComparison.Ordinal) then
            directoryRel.Substring(marker.Length)
        else
            directoryRel

    let normalized = relative.Replace('/', Path.DirectorySeparatorChar)
    Path.GetFullPath(Path.Combine(profileDir, normalized))

/// From storeContractID, never guessed. Anything other than a recognised maildir contract id
/// is treated as mbox, which is this codebase's only real-world format (Finding: the measured
/// profile is 100% berkeleystore).
let private storeFormatOf (storeContractId: string) : StoreFormat =
    if storeContractId.IndexOf("maildirstore", StringComparison.OrdinalIgnoreCase) >= 0 then
        Maildir
    else
        Mbox

let private accountKeys (prefs: IReadOnlyDictionary<string, string>) : string list =
    tryGet prefs "mail.accountmanager.accounts" |> Option.map splitCsv |> Option.defaultValue []

/// One account's fields, taken from its own `mail.server.<server>.*` and every one of its
/// identities' `mail.identity.<id>.*` entries (Q: an account can have more than one). Folders
/// are filled in later by MailFolderEnumerator - reading prefs.js does not touch the filesystem
/// beyond checking the resolved store directory exists.
let private readAccount
    (profileDir: string)
    (prefs: IReadOnlyDictionary<string, string>)
    (accountKey: string)
    : DiscoveredMailAccount option =
    match tryGet prefs $"mail.account.{accountKey}.server" with
    | None -> None
    | Some serverKey ->
        match tryGet prefs $"mail.server.{serverKey}.directory-rel" with
        | None -> None
        | Some directoryRel ->
            let hostname = tryGet prefs $"mail.server.{serverKey}.hostname" |> Option.defaultValue ""
            let displayName = tryGet prefs $"mail.server.{serverKey}.name" |> Option.defaultValue hostname
            let storeContractId = tryGet prefs $"mail.server.{serverKey}.storeContractID" |> Option.defaultValue ""
            let storeDirectory = resolveProfD profileDir directoryRel

            let identityKeys =
                tryGet prefs $"mail.account.{accountKey}.identities" |> Option.map splitCsv |> Option.defaultValue []

            let emailAddresses =
                identityKeys |> List.choose (fun idKey -> tryGet prefs $"mail.identity.{idKey}.useremail")

            match MailAccountId.create $"{profileDir}|{accountKey}" with
            | Error _ -> None // profileDir and accountKey are never both empty here
            | Ok id ->
                Some
                    {
                        Id = id
                        ProfilePath = profileDir
                        DisplayName = displayName
                        EmailAddresses = emailAddresses
                        StoreFormat = storeFormatOf storeContractId
                        StoreDirectory = storeDirectory
                        StoreDirectoryExists = Directory.Exists storeDirectory
                        Folders = []
                        CachedMessageCount = None
                    }

/// Reads the accounts one profile's prefs.js declares. The account list comes from
/// `mail.accountmanager.accounts` - never a directory listing, and never `1..lastKey`, both of
/// which the measured profile disproved (design.md -> "Reading the profile"). A mail directory
/// with no account pointing at it is simply never visited from here.
let read (profileDir: string) : Result<DiscoveredMailAccount list, MailAccountError> =
    let prefsPath = Path.Combine(profileDir, "prefs.js")

    try
        let lines = File.ReadAllLines prefsPath
        let prefs = parsePrefs lines

        match accountKeys prefs with
        | [] -> Error(ProfileUnreadable(profileDir, "No accounts declared in mail.accountmanager.accounts."))
        | keys -> keys |> List.choose (readAccount profileDir prefs) |> Ok
    with ex ->
        Error(ProfileUnreadable(profileDir, ex.Message))
