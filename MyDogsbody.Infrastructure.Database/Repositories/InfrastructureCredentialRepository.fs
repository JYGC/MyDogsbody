module MyDogsbody.Infrastructure.Database.Repositories.InfrastructureCredentialRepository

open LiteDB
open MyDogsbody.Infrastructure.Database.Models
open MyDogsBody.Dtos

let insertOne
  (getInfrastructureCredentialCollection: unit -> ILiteCollection<InfrastructureCredential>)
  (credential: InfrustructureCredentialDto)
  : unit =
    let infrastructureCredential = InfrastructureCredential()
    infrastructureCredential.InfrastructureType <- credential.InfrastructureType
    infrastructureCredential.Credentials <- credential.Credentials
    infrastructureCredential.ExternalUsername <- credential.Username
    getInfrastructureCredentialCollection().Insert infrastructureCredential |> ignore

let getAll
  (getInfrastructureCredentialCollection: unit -> ILiteCollection<InfrastructureCredential>)
  : InfrustructureCredentialDto list =
    getInfrastructureCredentialCollection()
        .Query()
        .ToEnumerable()
    |> Seq.map (fun ic ->
        InfrustructureCredentialDto(
            ic.InfrastructureType,
            ic.Credentials,
            ic.ExternalUsername
        )
    )
    |> Seq.toList

let getAllByType
  (getInfrastructureCredentialCollection: unit -> ILiteCollection<InfrastructureCredential>)
  (infrastructureType: MyDogsbody.Enums.InfrastructureType)
  : InfrustructureCredentialDto list =
    getInfrastructureCredentialCollection()
            .Query()
            .Where(fun ic -> ic.InfrastructureType = infrastructureType)
            .ToEnumerable()
    |> Seq.map (fun ic ->
        InfrustructureCredentialDto(
            ic.InfrastructureType,
            ic.Credentials,
            ic.ExternalUsername
        )
    )
    |> Seq.toList