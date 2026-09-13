/// A `Google.Apis.Util.Store.IDataStore` backed by the integration's own `Credentials`
/// collection, so an OAuth token lands in `Google.db` rather than in a `FileDataStore` directory
/// (design decision 1) - change #5 already keyed a provider's own credentials in that provider's
/// own database, and a token is exactly that kind of fact.
///
/// `IDataStore`'s own doc comment says the "real" saved key should fold in type information "so
/// the data store will be able to store the same key for different types" - `Google.Apis`'s own
/// `FileDataStore` does exactly this with a file name; this does the same against
/// `GoogleExternalUsername`.
///
/// Implementing `IDataStore` is one of the codebase's declared unavoidable imperative shapes:
/// the interface is exception-based, not `Result`-based, so a failure from `GoogleCredentialStore`
/// is re-raised here rather than threaded through as a value.
module MyDogsbody.Integrations.Google.GoogleCredentialDataStore

open System.Threading.Tasks
open Google.Apis.Json
open Google.Apis.Util.Store
open MyDogsbody.Builders
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Types

let private valueOrRaise (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith reason

/// One document per (type, key) pair, addressed by `ExternalUsername` - the same key
/// `GoogleWebAuthorizationBroker.AuthorizeAsync` is given as its `userId` parameter.
let private storeKey<'T> (key: string) : GoogleExternalUsername =
    GoogleExternalUsername.create $"{typeof<'T>.FullName}:{key}" |> valueOrRaise

type GoogleCredentialDataStore
    (handleError: HandleErrorBuilder, getCredentialCollection: unit -> GoogleCredentialsCollection) =

    let findExisting (username: GoogleExternalUsername) : StoredGoogleCredential option =
        match GoogleCredentialStore.getAll handleError getCredentialCollection () with
        | Ok all -> all |> List.tryFind (fun c -> c.Username = username)
        | Error ex -> raise ex

    interface IDataStore with
        member _.StoreAsync<'T>(key: string, value: 'T) : Task =
            let username = storeKey<'T> key
            let json = NewtonsoftJsonSerializer.Instance.Serialize value
            let secret = GoogleCredentialSecret.create json |> valueOrRaise

            let saveResult =
                match findExisting username with
                | Some existing ->
                    let edit: ValidGoogleCredentialEdit = { Id = existing.Id; Secret = secret; Username = username }
                    GoogleCredentialStore.updateOne handleError getCredentialCollection edit |> Result.map ignore
                | None ->
                    let credential: ValidGoogleCredential = { Secret = secret; Username = username }
                    GoogleCredentialStore.insertOne handleError getCredentialCollection credential |> Result.map ignore

            match saveResult with
            | Ok () -> Task.CompletedTask
            | Error ex -> raise ex

        member _.GetAsync<'T>(key: string) : Task<'T> =
            let username = storeKey<'T> key

            match findExisting username with
            | Some existing ->
                let json = GoogleCredentialSecret.value existing.Secret
                Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<'T> json)
            | None -> Task.FromResult(Unchecked.defaultof<'T>)

        member _.DeleteAsync<'T>(key: string) : Task =
            let username = storeKey<'T> key

            match findExisting username with
            | Some existing ->
                let collection = getCredentialCollection ()
                collection.Delete(GoogleCredentialEntityMappers.toObjectId existing.Id) |> ignore
                Task.CompletedTask
            | None -> Task.CompletedTask

        member _.ClearAsync() : Task =
            getCredentialCollection().DeleteAll() |> ignore
            Task.CompletedTask
