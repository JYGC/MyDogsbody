module MyDogsbody.UI.Portal.ModuleCreators.CredentialsBrowserModuleCreators

open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module

/// Builds the credentials browser state.
///
/// `startWork` is how the module gets off the render thread. Production passes an
/// Async.Start equivalent; a test passes `fun work -> work ()` and never has to wait.
let getCredentialsBrowserModule
  (startWork: (unit -> unit) -> unit)
  (credentialApi: CredentialApi)
  : CredentialsBrowserModule =
    let isLoadingCval = cval false
    let errorCval = cval<string option> None
    let credentialsListCval = cval<IntegrationCredentialUiType list> []

    let loadCredentials () =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            let result = credentialApi.GetAllCredentials()
            transact (fun _ ->
                match result with
                | Ok credentials ->
                    credentialsListCval.Value <- credentials
                    errorCval.Value <- None
                | Error ex ->
                    errorCval.Value <- Some ex.Message
                isLoadingCval.Value <- false
            )
        )

    /// Runs a write and reloads, so the table shows what was actually stored rather than
    /// what the dialog was holding.
    let write operation =
        transact (fun _ -> isLoadingCval.Value <- true)
        startWork (fun () ->
            match operation () with
            | Ok () ->
                transact (fun _ ->
                    errorCval.Value <- None
                    isLoadingCval.Value <- false
                )
                loadCredentials ()
            | Error (ex: MyDogsbody.Exceptions.Types.MyDogsbodyException) ->
                transact (fun _ ->
                    errorCval.Value <- Some ex.Message
                    isLoadingCval.Value <- false
                )
        )

    loadCredentials ()

    {
        CredentialsListAval = credentialsListCval
        IsLoadingAval = isLoadingCval
        ErrorAval = errorCval
        LoadCredentials = loadCredentials
        AddCredential =
            fun credential -> write (fun () -> credentialApi.AddCredential credential)
        EditCredential =
            fun credential -> write (fun () -> credentialApi.EditCredential credential)
    }
