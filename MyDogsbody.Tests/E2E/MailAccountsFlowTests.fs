module MyDogsbody.Tests.E2E.MailAccountsFlowTests

open System
open System.IO
open Xunit
open Bunit
open Fun.Blazor
open MyDogsbody.UI.Portal.Components
open MyDogsbody.UI.Portal.ModuleCreators
open MyDogsbody.UI.Types
open MyDogsbody.Tests.E2E.MailAccountsTestHarness
open MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

// User-visible flows, driven through a rendered component down to a real temp LiteDB file and
// back into what the component renders. Everything between is the real thing: the module
// creator, the API record, the workflows, the two adapters (discovery and the LiteDB store).
//
// The folder picker is a lambda, so no window opens - proving the wiring from Phase 7 works
// without driving the real WPF BlazorWebView window, which is out of scope (the manual check is
// recorded in outcome.md instead).

/// Renders the mail accounts browser over the harness's real API. Work runs on the calling
/// thread, so the test never waits on a background thread.
let private renderBrowser (harness: MailAccountsHarness) (folderPicker: FolderPicker) =
    let browserModule = MailAccountsBrowserModuleCreators.getMailAccountsBrowserModule (fun work -> work ()) harness.Api

    let view = MailAccountsComponents.mailAccountsBrowser browserModule folderPicker

    let rendered =
        harness.Render<FunFragmentComponent>(fun builder ->
            builder.OpenComponent<FunFragmentComponent>(0)
            builder.AddAttribute(1, "Fragment", view)
            builder.CloseComponent())

    browserModule, rendered

[<Fact; Trait("Level", "E2E")>]
let ``no folder chosen shows the invitation, not an empty table`` () =
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let _, rendered = renderBrowser harness (fun () -> None)

        Assert.Contains("No Thunderbird profile folder has been chosen yet", rendered.Markup)
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``choosing a folder through the picker shows the path, and scanning shows the discovered accounts`` () =
    withMailAccountsHarness (fun () -> Some measuredShapeProfile) (fun harness ->
        let folderPicker: FolderPicker = fun () -> Some measuredShapeProfile
        let browserModule, rendered = renderBrowser harness folderPicker

        // Drives the real Browse button, exercising the FolderPicker wiring end to end - the
        // point of Phase 7's host change - rather than calling SetProfileRoot directly.
        let browseButton = rendered.Find("button")
        browseButton.Click()

        rendered.WaitForAssertion(fun () -> Assert.Contains(measuredShapeProfile, rendered.Markup))

        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Alpha Mail", rendered.Markup)
            Assert.Contains("Beta Mail", rendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``selecting an account shows it selected and the selection persists across a reload`` () =
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let browserModule, rendered = renderBrowser harness (fun () -> None)
        browserModule.SetProfileRoot measuredShapeProfile
        rendered.WaitForAssertion(fun () -> Assert.Contains(measuredShapeProfile, rendered.Markup))

        browserModule.ScanForAccounts()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Alpha Mail", rendered.Markup))

        let alphaAccountId = $"{measuredShapeProfile}|account1"
        browserModule.SelectAccount alphaAccountId

        rendered.WaitForAssertion(fun () ->
            let accounts, selected = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
            Assert.Equal(Some alphaAccountId, selected)
            Assert.NotEmpty accounts)

        // "Persists across a reload" - a fresh module creator instance re-reads from the same
        // store rather than carrying in-memory state forward.
        let _, reloadedRendered = renderBrowser harness (fun () -> None)

        reloadedRendered.WaitForAssertion(fun () -> Assert.Contains("Alpha Mail", reloadedRendered.Markup)))

[<Fact; Trait("Level", "E2E")>]
let ``a scan that clears a selection naming a vanished account says so on the page`` () =
    // requirements.md -> "Selecting an account": "WHEN the selected account no longer appears in a
    // fresh discovery THE SYSTEM SHALL clear the selection AND SAY SO, rather than leaving a
    // selection pointing at nothing." Clearing it silently and letting the tick disappear is not
    // saying so - the user is never told why the account they picked is no longer chosen.
    let otherProfile = Path.Combine(Path.GetTempPath(), $"mdb-e2e-{Guid.NewGuid()}")
    Directory.CreateDirectory otherProfile |> ignore

    try
        // A valid profile that declares no accounts at all, so nothing the first scan found survives.
        File.WriteAllText(
            Path.Combine(otherProfile, "prefs.js"),
            "user_pref(\"mail.account.lastKey\", 0);\nuser_pref(\"mail.accountmanager.accounts\", \"account1\");"
        )

        withMailAccountsHarness (fun () -> None) (fun harness ->
            let browserModule, rendered = renderBrowser harness (fun () -> None)

            browserModule.SetProfileRoot measuredShapeProfile
            browserModule.ScanForAccounts()
            rendered.WaitForAssertion(fun () -> Assert.Contains("Alpha Mail", rendered.Markup))

            browserModule.SelectAccount $"{measuredShapeProfile}|account1"

            rendered.WaitForAssertion(fun () ->
                let _, selected = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
                Assert.Equal(Some $"{measuredShapeProfile}|account1", selected))

            browserModule.SetProfileRoot otherProfile
            browserModule.ScanForAccounts()

            rendered.WaitForAssertion(fun () ->
                // The selection really is gone from the store...
                let _, selected = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
                Assert.Equal(None, selected)
                // ...and the page says so, rather than only un-ticking a row.
                Assert.Contains("selected mail account was not found", rendered.Markup))

            // Saying so is not an error: nothing is logged for a reconciliation the system did on
            // purpose, and the alert channel stays free for real failures.
            Assert.Empty harness.Logged

            // A later scan that clears nothing takes the notice back down again.
            browserModule.SetProfileRoot measuredShapeProfile
            browserModule.ScanForAccounts()

            rendered.WaitForAssertion(fun () ->
                Assert.Contains("Alpha Mail", rendered.Markup)
                Assert.DoesNotContain("selected mail account was not found", rendered.Markup)))
    finally
        Directory.Delete(otherProfile, true)

[<Fact; Trait("Level", "E2E")>]
let ``choosing an account takes the cleared-selection notice down`` () =
    // The notice ends "Choose an account below." Once the user has, its premise is false. Leaving
    // it up until some later scan happens to clear nothing tells the user their selection is gone
    // while the row beside it is ticked - two answers to the same question, on the same screen.
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let browserModule, rendered = renderBrowser harness (fun () -> None)

        // One profile's account is selected, then the root moves to a profile that does not
        // contain it - the same reconciliation the test above covers, but this one has accounts
        // left to choose from afterwards.
        browserModule.SetProfileRoot maildirShapeProfile
        browserModule.ScanForAccounts()
        rendered.WaitForAssertion(fun () -> Assert.Contains("Maildir Mail", rendered.Markup))

        browserModule.SelectAccount $"{maildirShapeProfile}|account1"

        rendered.WaitForAssertion(fun () ->
            let _, selected = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
            Assert.Equal(Some $"{maildirShapeProfile}|account1", selected))

        browserModule.SetProfileRoot measuredShapeProfile
        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("selected mail account was not found", rendered.Markup)
            Assert.Contains("Alpha Mail", rendered.Markup))

        browserModule.SelectAccount $"{measuredShapeProfile}|account1"

        rendered.WaitForAssertion(fun () ->
            let _, selected = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
            Assert.Equal(Some $"{measuredShapeProfile}|account1", selected)
            Assert.DoesNotContain("selected mail account was not found", rendered.Markup))

        // Answering a reconciliation is not a failure - nothing is logged and no alert appears.
        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``counting messages for an account whose store directory is missing says so instead of answering zero`` () =
    // The table already marks such an account "Configured, but its store directory is missing" -
    // and then answered "0 (as of ...)" when the user pressed Count messages beside that very
    // label, because enumeration gave it no folders and a fold over none is zero. Two
    // contradictory answers on one row, the second of them a number the user has no reason to
    // doubt.
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let browserModule, rendered = renderBrowser harness (fun () -> None)

        browserModule.SetProfileRoot measuredShapeProfile
        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("Delta Mail", rendered.Markup)
            Assert.Contains("Configured, but its store directory is missing", rendered.Markup))

        browserModule.CountMessages $"{measuredShapeProfile}|account17"

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("does not exist", rendered.Markup)
            Assert.Contains("imap.delta.example.com", rendered.Markup)
            // The cached-count cell must be left alone rather than showing a confident zero.
            Assert.DoesNotContain("0 (as of", rendered.Markup))

        // An expected failure: the user pointed at a store that is gone, so it reaches the alert
        // channel without reaching the log.
        Assert.Empty harness.Logged

        // A neighbouring account whose store IS there still counts, so the guard refuses the one
        // account rather than the page.
        browserModule.CountMessages $"{measuredShapeProfile}|account1"

        rendered.WaitForAssertion(fun () ->
            Assert.Contains("(as of", rendered.Markup)
            Assert.DoesNotContain("does not exist", rendered.Markup))

        Assert.Empty harness.Logged)

[<Fact; Trait("Level", "E2E")>]
let ``the accounts table states each account's on-disk size and what a scan of it will cost`` () =
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let browserModule, rendered = renderBrowser harness (fun () -> None)
        browserModule.SetProfileRoot measuredShapeProfile
        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () -> Assert.Contains("Alpha Mail", rendered.Markup))

        let accounts, _ = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
        let alpha = accounts |> List.find (fun a -> a.DisplayName = "Alpha Mail")

        let total = MailAccountsComponents.accountSizeBytes alpha
        let scannable = MailAccountsComponents.scannableSizeBytes alpha

        // The fixture's Trash/Junk/Sent/Drafts carry bytes, so the two figures must differ -
        // a table showing only the total would not say what a scan is going to cost.
        Assert.True(total > scannable, $"the fixture no longer distinguishes the two figures: {total} vs {scannable}")

        Assert.Contains("Size", rendered.Markup)
        Assert.Contains(MailAccountsComponents.formatSizeBytes total, rendered.Markup)
        Assert.Contains($"{MailAccountsComponents.formatSizeBytes scannable} to scan", rendered.Markup))

[<Fact; Trait("Level", "E2E")>]
let ``a walk hitting an unreadable directory lists it and the other accounts still appear`` () =
    let root = Path.Combine(Path.GetTempPath(), $"mdb-e2e-{Guid.NewGuid()}")
    Directory.CreateDirectory root |> ignore

    try
        let profileDir = Path.Combine(root, "profile")
        Directory.CreateDirectory profileDir |> ignore

        File.WriteAllText(
            Path.Combine(profileDir, "prefs.js"),
            "user_pref(\"mail.account.lastKey\", 1);\n"
            + "user_pref(\"mail.accountmanager.accounts\", \"account1\");\n"
            + "user_pref(\"mail.account.account1.identities\", \"id1\");\n"
            + "user_pref(\"mail.account.account1.server\", \"server1\");\n"
            + "user_pref(\"mail.server.server1.hostname\", \"imap.e2e.example.com\");\n"
            + "user_pref(\"mail.server.server1.name\", \"E2E Mail\");\n"
            + "user_pref(\"mail.server.server1.storeContractID\", \"@mozilla.org/msgstore/berkeleystore;1\");\n"
            + "user_pref(\"mail.server.server1.type\", \"imap\");\n"
            + "user_pref(\"mail.server.server1.userName\", \"e2e\");\n"
            + "user_pref(\"mail.server.server1.directory-rel\", \"[ProfD]ImapMail/imap.e2e.example.com\");\n"
            + "user_pref(\"mail.identity.id1.useremail\", \"e2e@example.com\");\n"
        )

        let storeDir = Path.Combine(profileDir, "ImapMail", "imap.e2e.example.com")
        Directory.CreateDirectory storeDir |> ignore
        File.WriteAllText(Path.Combine(storeDir, "INBOX"), "")

        let deniedDir = Path.Combine(root, "denied")
        Directory.CreateDirectory deniedDir |> ignore

        let currentAccount = $"{Environment.UserDomainName}\\{Environment.UserName}"

        let icacls =
            Diagnostics.ProcessStartInfo("icacls", $"\"{deniedDir}\" /deny \"{currentAccount}:(OI)(CI)RX\"", UseShellExecute = true, CreateNoWindow = true)

        use denyProcess = Diagnostics.Process.Start icacls
        denyProcess.WaitForExit()

        // Same reason as ThunderbirdFolderScannerTests' copy of this setup: if icacls did not
        // apply the deny, the directory is readable, "could not be read" never appears, and the
        // failure reads as a defect in the walk rather than in the test's own setup.
        Assert.True(
            denyProcess.ExitCode = 0,
            $"icacls could not deny access to '{deniedDir}' (exit code {denyProcess.ExitCode}); the test setup, not the walk, is what failed"
        )

        try
            withMailAccountsHarness (fun () -> None) (fun harness ->
                let browserModule, rendered = renderBrowser harness (fun () -> None)
                browserModule.SetProfileRoot root
                rendered.WaitForAssertion(fun () -> Assert.Contains(root, rendered.Markup))

                browserModule.ScanForAccounts()

                rendered.WaitForAssertion(fun () ->
                    Assert.Contains("E2E Mail", rendered.Markup)
                    Assert.Contains("could not be read", rendered.Markup)))
        finally
            let reset =
                Diagnostics.ProcessStartInfo("icacls", $"\"{deniedDir}\" /reset", UseShellExecute = true, CreateNoWindow = true)

            use resetProcess = Diagnostics.Process.Start reset
            resetProcess.WaitForExit()
    finally
        Directory.Delete(root, true)

/// One profile directory declaring exactly one account, whose every displayed field is fixed by
/// the caller - so two of these differ in nothing the table renders except the profile they came
/// from.
let private writeDuplicateProfile (profileDir: string) =
    Directory.CreateDirectory profileDir |> ignore

    File.WriteAllText(
        Path.Combine(profileDir, "prefs.js"),
        "user_pref(\"mail.account.lastKey\", 1);\n"
        + "user_pref(\"mail.accountmanager.accounts\", \"account1\");\n"
        + "user_pref(\"mail.account.account1.identities\", \"id1\");\n"
        + "user_pref(\"mail.account.account1.server\", \"server1\");\n"
        + "user_pref(\"mail.server.server1.hostname\", \"imap.dup.example.com\");\n"
        + "user_pref(\"mail.server.server1.name\", \"Duplicated Mail\");\n"
        + "user_pref(\"mail.server.server1.storeContractID\", \"@mozilla.org/msgstore/berkeleystore;1\");\n"
        + "user_pref(\"mail.server.server1.directory-rel\", \"[ProfD]ImapMail/imap.dup.example.com\");\n"
        + "user_pref(\"mail.identity.id1.useremail\", \"dup@example.com\");\n"
    )

    let storeDir = Path.Combine(profileDir, "ImapMail", "imap.dup.example.com")
    Directory.CreateDirectory storeDir |> ignore
    File.WriteAllText(Path.Combine(storeDir, "INBOX"), "")

[<Fact; Trait("Level", "E2E")>]
let ``two profiles declaring the same account are told apart on the page by the profile they came from`` () =
    // requirements.md -> "Walking the chosen folder": "WHEN several profiles are found THE SYSTEM
    // SHALL list all of their accounts, QUALIFIED BY THE PROFILE PATH THEY CAME FROM, so two
    // profiles containing the same account are distinguishable (Q4.9)" - and again under "Edge
    // cases": "WHEN two profiles declare accounts with the same email address THE SYSTEM SHALL list
    // both, qualified by profile path."
    //
    // The chosen folder being "a backup copy" alongside the live profile is one of the three
    // shapes the walk is required to handle, so this is the ordinary case, not a contrived one.
    // Both rows carry the same display name, the same address, the same format, the same folder
    // count and the same size; the only thing that separates them is the profile - and the user is
    // being asked to pick ONE of them for import.
    let root = Path.Combine(Path.GetTempPath(), $"mdb-e2e-{Guid.NewGuid()}")
    Directory.CreateDirectory root |> ignore

    try
        let liveProfile = Path.Combine(root, "live")
        let backupProfile = Path.Combine(root, "backup")
        writeDuplicateProfile liveProfile
        writeDuplicateProfile backupProfile

        withMailAccountsHarness (fun () -> None) (fun harness ->
            let browserModule, rendered = renderBrowser harness (fun () -> None)
            browserModule.SetProfileRoot root
            browserModule.ScanForAccounts()

            rendered.WaitForAssertion(fun () ->
                // Both are listed, as they must be...
                let accounts, _ = harness.Api.GetAccounts() |> Result.defaultWith (fun _ -> failwith "expected Ok")
                Assert.Equal(2, accounts.Length)
                Assert.All(accounts, fun a -> Assert.Equal("Duplicated Mail", a.DisplayName))

                // ...and the page says which profile each one came from, so the two rows are not
                // the same row twice.
                Assert.Contains(liveProfile, rendered.Markup)
                Assert.Contains(backupProfile, rendered.Markup))

            Assert.Empty harness.Logged)
    finally
        Directory.Delete(root, true)

[<Fact; Trait("Level", "E2E")>]
let ``a failure is shown as an alert, cleared by the next success`` () =
    withMailAccountsHarness (fun () -> None) (fun harness ->
        let browserModule, rendered = renderBrowser harness (fun () -> None)

        // Scanning with no profile root chosen is a failure the workflow reports.
        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () -> Assert.Contains("No Thunderbird profile folder has been chosen yet", rendered.Markup))

        browserModule.SetProfileRoot measuredShapeProfile
        browserModule.ScanForAccounts()

        rendered.WaitForAssertion(fun () ->
            Assert.DoesNotContain("No Thunderbird profile folder has been chosen yet", rendered.Markup)
            Assert.Contains("Alpha Mail", rendered.Markup)))
