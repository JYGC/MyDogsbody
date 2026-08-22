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
