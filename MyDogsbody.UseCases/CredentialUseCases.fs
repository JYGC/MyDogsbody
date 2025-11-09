namespace MyDogsbody.UseCases

open MyDogsBody.Dtos
open MyDogsbody.UseCases.Interfaces
open SetupDatabases
open MyDogsbody.Infrastructure.Database.Repositories

type CredentialUseCases() =
    interface ICredentialUseCases with
        member _.AddNewCredential(credential: InfrustructureCredentialDto) =
            InfrastructureCredentialRepository.insertOne
                infrastructureDatabaseContext.GetInfrastructureCredentialCollection
                credential

        member _.GetAllCredentials() =
            InfrastructureCredentialRepository.getAll
                infrastructureDatabaseContext.GetInfrastructureCredentialCollection
