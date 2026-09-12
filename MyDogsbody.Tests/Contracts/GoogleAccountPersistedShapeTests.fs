module MyDogsbody.Tests.Contracts.GoogleAccountPersistedShapeTests

open System
open System.IO
open Xunit
open LiteDB
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain.Calendar
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database

// LiteDB is schemaless, so renaming a property on GoogleAccountEntity or GoogleClientSecretEntity
// silently orphans every row already stored. This asserts the persisted documents' field names,
// not just that an object round trips.

let private handleError = HandleErrorBuilder(fun _ -> ())

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private withStoreAndRawAccess
    (test: (unit -> Database.Types.GoogleAccountsCollection) -> (unit -> Database.Types.GoogleClientSecretCollection) -> LiteDatabase -> unit)
    =
    let databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")
    let context = GoogleDatabaseContextModule.getDatabaseContext databasePath "shared"
    use rawDatabase = new LiteDatabase($"Filename={databasePath};connection=shared")

    try
        test context.GetAccountCollection context.GetClientSecretCollection rawDatabase
    finally
        context.Dispose()
        try File.Delete databasePath with _ -> ()

let private okOrFail label result =
    match result with
    | Ok value -> value
    | Error (ex: MyDogsbodyException) -> failwith $"{label} expected Ok, but got Error: {ex.Message}"

[<Fact; Trait("Level", "Contract")>]
let ``a Google account is persisted under the documented field names`` () =
    withStoreAndRawAccess (fun getAccounts _ rawDatabase ->
        let account: RegisteredGoogleAccount =
            {
                Id = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
                EmailAddress = GoogleEmail.create "person@gmail.com" |> valueOrFail
                DefaultInvoiceCalendar = Some (CalendarId.create "cal-1" |> valueOrFail)
                NeedsReauthorisation = true
            }

        account |> GoogleAccountStore.saveOne handleError getAccounts |> okOrFail "saveOne" |> ignore

        let document = rawDatabase.GetCollection("Accounts").FindAll() |> Seq.exactlyOne

        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "EmailAddress", "expected an EmailAddress field")
        Assert.True(document.ContainsKey "DefaultInvoiceCalendarId", "expected a DefaultInvoiceCalendarId field")
        Assert.True(document.ContainsKey "NeedsReauthorisation", "expected a NeedsReauthorisation field")

        Assert.Equal("person@gmail.com", document.["EmailAddress"].AsString)
        Assert.Equal("cal-1", document.["DefaultInvoiceCalendarId"].AsString)
        Assert.True(document.["NeedsReauthorisation"].AsBoolean)
    )

[<Fact; Trait("Level", "Contract")>]
let ``the account collection is named Accounts`` () =
    withStoreAndRawAccess (fun getAccounts _ rawDatabase ->
        let account: RegisteredGoogleAccount =
            {
                Id = GoogleAccountId.create "507f1f77bcf86cd799439011" |> valueOrFail
                EmailAddress = GoogleEmail.create "person@gmail.com" |> valueOrFail
                DefaultInvoiceCalendar = None
                NeedsReauthorisation = false
            }

        account |> GoogleAccountStore.saveOne handleError getAccounts |> okOrFail "saveOne" |> ignore

        Assert.Contains("Accounts", rawDatabase.GetCollectionNames() |> List.ofSeq)
    )

[<Fact; Trait("Level", "Contract")>]
let ``a Google client secret is persisted under the documented field name, in the ClientSecret collection`` () =
    withStoreAndRawAccess (fun _ getSecret rawDatabase ->
        GoogleAccountStore.saveClientSecret handleError getSecret "pasted-secret"
        |> okOrFail "saveClientSecret"
        |> ignore

        Assert.Contains("ClientSecret", rawDatabase.GetCollectionNames() |> List.ofSeq)

        let document = rawDatabase.GetCollection("ClientSecret").FindAll() |> Seq.exactlyOne
        Assert.True(document.ContainsKey "_id", "expected an _id field")
        Assert.True(document.ContainsKey "Secret", "expected a Secret field")
        Assert.Equal("pasted-secret", document.["Secret"].AsString)
    )
