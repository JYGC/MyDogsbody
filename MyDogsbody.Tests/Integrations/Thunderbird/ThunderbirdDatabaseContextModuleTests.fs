module MyDogsbody.Tests.Integrations.Thunderbird.ThunderbirdDatabaseContextModuleTests

open System
open System.IO
open Xunit
open MyDogsbody.Integrations.Thunderbird.Database
open MyDogsbody.Integrations.Thunderbird.Database.Models

let private withTempPath (test: string -> unit) =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")

    try
        test databasePath
    finally
        try
            File.Delete databasePath
        with _ ->
            ()

[<Fact; Trait("Level", "Integration")>]
let ``getDatabaseContext exposes a working collection getter for every one of the five entities`` () =
    withTempPath (fun databasePath ->
        let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"

        try
            ThunderbirdProfileRoot(Path = @"C:\Thunderbird") |> context.GetProfileRootCollection().Insert |> ignore
            Assert.Single(context.GetProfileRootCollection().FindAll()) |> ignore

            DiscoveredAccountEntity(AccountId = "a1", StoreFormat = "Mbox")
            |> context.GetAccountsCollection().Insert
            |> ignore

            Assert.Single(context.GetAccountsCollection().FindAll()) |> ignore

            DiscoveredFolderEntity(AccountId = "a1", RelativePath = "INBOX")
            |> context.GetFoldersCollection().Insert
            |> ignore

            Assert.Single(context.GetFoldersCollection().FindAll()) |> ignore

            SelectedAccountEntity(AccountId = "a1") |> context.GetSelectedAccountCollection().Insert |> ignore
            Assert.Single(context.GetSelectedAccountCollection().FindAll()) |> ignore

            ScanWatermarkEntity(AccountId = "a1", RelativePath = "INBOX")
            |> context.GetWatermarksCollection().Insert
            |> ignore

            Assert.Single(context.GetWatermarksCollection().FindAll()) |> ignore
        finally
            context.Dispose()
    )

[<Fact; Trait("Level", "Integration")>]
let ``Dispose releases the file so it can be deleted`` () =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = ThunderbirdDatabaseContextModule.getDatabaseContext databasePath "direct"
    context.GetAccountsCollection().FindAll() |> Seq.toList |> ignore

    context.Dispose()

    // No try/with here on purpose: the delete must actually succeed.
    File.Delete databasePath
    Assert.False(File.Exists databasePath)
