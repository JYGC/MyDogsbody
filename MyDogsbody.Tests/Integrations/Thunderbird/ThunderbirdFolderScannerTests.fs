module MyDogsbody.Tests.Integrations.Thunderbird.ThunderbirdFolderScannerTests

open System
open System.IO
open System.Diagnostics
open Xunit
open MyDogsbody.Integrations.Thunderbird
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

let private freshTempDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), $"mdb-tbscan-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore
    dir

[<Fact; Trait("Level", "Integration")>]
let ``scan finds the profile directly when the chosen folder is itself one profile`` () =
    let outcome = ThunderbirdFolderScanner.scan measuredShapeProfile

    let found = Assert.Single outcome.ProfileDirectories
    Assert.Equal(Path.GetFullPath measuredShapeProfile, Path.GetFullPath found)

[<Fact; Trait("Level", "Integration")>]
let ``scan finds every profile when the chosen folder is the parent of several`` () =
    let parent = freshTempDirectory ()

    try
        let profileA = Path.Combine(parent, "profileA")
        let profileB = Path.Combine(parent, "profileB")
        Directory.CreateDirectory profileA |> ignore
        Directory.CreateDirectory profileB |> ignore
        File.WriteAllText(Path.Combine(profileA, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")
        File.WriteAllText(Path.Combine(profileB, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")

        let outcome = ThunderbirdFolderScanner.scan parent

        Assert.Equal<string list>(
            [ Path.GetFullPath profileA; Path.GetFullPath profileB ] |> List.sort,
            outcome.ProfileDirectories |> List.map Path.GetFullPath |> List.sort
        )
    finally
        Directory.Delete(parent, true)

[<Fact; Trait("Level", "Integration")>]
let ``scan finds a profile nested inside a backup copy`` () =
    let root = freshTempDirectory ()

    try
        let backupProfile = Path.Combine(root, "backup-2026-01-01", "Profiles", "abc.default")
        Directory.CreateDirectory backupProfile |> ignore
        File.WriteAllText(Path.Combine(backupProfile, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")

        let outcome = ThunderbirdFolderScanner.scan root

        let found = Assert.Single outcome.ProfileDirectories
        Assert.Equal(Path.GetFullPath backupProfile, Path.GetFullPath found)
    finally
        Directory.Delete(root, true)

[<Fact; Trait("Level", "Integration")>]
let ``scan records an unreadable directory and continues the walk`` () =
    let root = freshTempDirectory ()

    try
        let deniedDir = Path.Combine(root, "denied")
        let okProfile = Path.Combine(root, "ok-profile")
        Directory.CreateDirectory deniedDir |> ignore
        Directory.CreateDirectory okProfile |> ignore
        File.WriteAllText(Path.Combine(okProfile, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")

        let currentAccount = $"{Environment.UserDomainName}\\{Environment.UserName}"
        let icacls = ProcessStartInfo("icacls", $"\"{deniedDir}\" /deny \"{currentAccount}:(OI)(CI)RX\"")
        icacls.UseShellExecute <- true
        icacls.CreateNoWindow <- true
        use denyProcess = Process.Start icacls
        denyProcess.WaitForExit()

        try
            let outcome = ThunderbirdFolderScanner.scan root

            Assert.Contains(outcome.Unreadable, fun u -> Path.GetFullPath u.Path = Path.GetFullPath deniedDir)
            let found = Assert.Single outcome.ProfileDirectories
            Assert.Equal(Path.GetFullPath okProfile, Path.GetFullPath found)
        finally
            let reset = ProcessStartInfo("icacls", $"\"{deniedDir}\" /reset")
            reset.UseShellExecute <- true
            reset.CreateNoWindow <- true
            use resetProcess = Process.Start reset
            resetProcess.WaitForExit()
    finally
        Directory.Delete(root, true)

[<Fact; Trait("Level", "Integration")>]
let ``scan does not loop through a junction pointing at an ancestor`` () =
    let root = freshTempDirectory ()

    try
        let profileDir = Path.Combine(root, "profile")
        Directory.CreateDirectory profileDir |> ignore
        File.WriteAllText(Path.Combine(profileDir, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")

        let junctionPath = Path.Combine(profileDir, "loop-back-to-root")

        let mklink = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{root}\"")
        mklink.UseShellExecute <- false
        mklink.CreateNoWindow <- true
        use mklinkProcess = Process.Start mklink
        mklinkProcess.WaitForExit()

        try
            if not (Directory.Exists junctionPath) then
                // Junction creation can require privileges this environment does not have; skip
                // rather than fail on an environment-dependent precondition.
                ()
            else
                let outcome = ThunderbirdFolderScanner.scan root
                // The walk must terminate at all (proving the loop guard worked) and must still
                // find the one real profile exactly once.
                Assert.Single(outcome.ProfileDirectories) |> ignore
        finally
            // The junction must be removed as a reparse point (non-recursive) before root is
            // deleted recursively below - otherwise the recursive delete tries to follow the
            // junction back into root's own tree and fails.
            if Directory.Exists junctionPath then
                Directory.Delete(junctionPath, false)
    finally
        Directory.Delete(root, true)

[<Fact; Trait("Level", "Integration")>]
let ``scan stops descending past the depth bound`` () =
    let root = freshTempDirectory ()

    try
        let mutable current = root

        for _ in 1 .. ThunderbirdFolderScanner.MaxDepth + 5 do
            current <- Path.Combine(current, "d")
            Directory.CreateDirectory current |> ignore

        File.WriteAllText(Path.Combine(current, "prefs.js"), "user_pref(\"mail.account.lastKey\", 0);")

        let outcome = ThunderbirdFolderScanner.scan root

        Assert.Empty outcome.ProfileDirectories
        Assert.Empty outcome.Unreadable
    finally
        Directory.Delete(root, true)

[<Fact; Trait("Level", "Integration")>]
let ``scan reports no profiles found for a folder with no prefs.js anywhere`` () =
    let root = freshTempDirectory ()

    try
        Directory.CreateDirectory(Path.Combine(root, "just-some-folder")) |> ignore

        let outcome = ThunderbirdFolderScanner.scan root

        Assert.Empty outcome.ProfileDirectories
        Assert.Empty outcome.Unreadable
    finally
        Directory.Delete(root, true)
