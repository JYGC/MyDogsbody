module MyDogsbody.Tests.Integrations.Thunderbird.ThunderbirdAccountReaderTests

open System
open System.IO
open Xunit
open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

let private byHostname (accounts: DiscoveredMailAccount list) (hostFragment: string) =
    accounts |> List.find (fun a -> a.StoreDirectory.Contains(hostFragment, StringComparison.OrdinalIgnoreCase))

[<Fact; Trait("Level", "Integration")>]
let ``read finds exactly the ten accounts prefs.js declares and none of the orphan directories`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        Assert.Equal(10, accounts.Length)

        for account in accounts do
            Assert.DoesNotContain("orphan", account.StoreDirectory, StringComparison.OrdinalIgnoreCase)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read never invents an account at a gapped key`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let ids = accounts |> List.map (fun a -> MailAccountId.value a.Id)
        // Ids are "{profileDir}|account<N>" - the gapped keys 4,5,7,8,11-16 must never appear.
        for gap in [ 4; 5; 7; 8; 11; 12; 13; 14; 15; 16 ] do
            Assert.DoesNotContain(ids, fun id -> id.EndsWith($"|account{gap}"))
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read resolves directory-rel against the profile folder and ignores the stale absolute directory`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let beta = byHostname accounts "imap.beta.example.com"
        Assert.True(beta.StoreDirectoryExists)
        Assert.DoesNotContain("OldUser", beta.StoreDirectory)
        Assert.Contains(measuredShapeProfile, beta.StoreDirectory)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read maps storeContractID to the store format`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts -> Assert.All(accounts, fun a -> Assert.Equal(Mbox, a.StoreFormat))
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read returns every identity for an account with two`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let gamma = byHostname accounts "imap.gamma.example.com"
        Assert.Equal<string list>(
            [ "frank@gamma.example.com"; "frank.alt@gamma.example.com" ] |> List.sort,
            gamma.EmailAddresses |> List.sort
        )
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read lists an account with no identities with no email address`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let localFolders = accounts |> List.find (fun a -> a.StoreDirectory.EndsWith("Local Folders"))
        Assert.Empty localFolders.EmailAddresses
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read reports a configured-but-missing store directory rather than dropping the account`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let delta = byHostname accounts "imap.delta.example.com"
        Assert.False(delta.StoreDirectoryExists)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read decodes an escaped embedded quote in a display name`` () =
    let actual = ThunderbirdAccountReader.read measuredShapeProfile

    match actual with
    | Ok accounts ->
        let epsilon = byHostname accounts "imap.epsilon.example.com"
        Assert.Equal("Heidi's \"Backup\" Mail", epsilon.DisplayName)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Integration")>]
let ``read reports a malformed prefs.js as unreadable`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"mdb-tbreader-{Guid.NewGuid()}")
    Directory.CreateDirectory tempDir |> ignore

    try
        File.WriteAllText(Path.Combine(tempDir, "prefs.js"), "this is not a valid prefs.js file at all")

        let actual = ThunderbirdAccountReader.read tempDir

        match actual with
        | Error(ProfileUnreadable(path, _)) -> Assert.Equal(Path.GetFullPath tempDir, Path.GetFullPath path)
        | other -> Assert.Fail($"Expected Error(ProfileUnreadable _), but got: {other}")
    finally
        Directory.Delete(tempDir, true)

[<Fact; Trait("Level", "Integration")>]
let ``a malformed profile does not stop another profile from being read`` () =
    // ThunderbirdFolderScanner finds each profile independently, so one profile's read failure
    // has no bearing on another's - this asserts that at the reader level directly.
    let tempDir = Path.Combine(Path.GetTempPath(), $"mdb-tbreader-{Guid.NewGuid()}")
    Directory.CreateDirectory tempDir |> ignore

    try
        File.WriteAllText(Path.Combine(tempDir, "prefs.js"), "garbage")

        let malformedResult = ThunderbirdAccountReader.read tempDir
        let goodResult = ThunderbirdAccountReader.read measuredShapeProfile

        Assert.True(Result.isError malformedResult)
        Assert.True(Result.isOk goodResult)
    finally
        Directory.Delete(tempDir, true)
