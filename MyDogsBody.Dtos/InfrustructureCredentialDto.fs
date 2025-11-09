namespace MyDogsBody.Dtos

open MyDogsbody.Enums

type InfrustructureCredentialDto(
    infrastructureType: InfrastructureType,
    credentials: string,
    username: string
) =
    member val InfrastructureType: InfrastructureType = infrastructureType with get
    member val Credentials: string = credentials with get
    member val Username: string = username with get
