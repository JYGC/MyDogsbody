/// The bottom mapping point: LiteDB entity (C#, ObjectId, nullable strings) <-> domain type.
///
/// One of the architecture's two mappers. Everything between this file and
/// Startup/CredentialApiMappers.fs travels as domain types, unmapped.
///
/// Pure - no I/O, no handleError, no LiteDB calls. CredentialStore does the talking; this file
/// only translates, so it can be asserted field-for-field without a database.
module MyDogsbody.Integrations.Credentials.CredentialEntityMappers

open LiteDB
open MyDogsbody.Enums
open MyDogsbody.Domain.Credentials
open MyDogsbody.Integrations.Credentials.Database.Models

/// Domain -> persistence. Exhaustive: adding a case to Infrastructure breaks this build, which
/// is exactly the reminder the new case needs a persisted representation.
let toInfrastructureType (infrastructure: Infrastructure) : InfrastructureType =
    match infrastructure with
    | Google -> InfrastructureType.Google
    | Microsoft -> InfrastructureType.Microsoft

/// Persistence -> domain.
///
/// Cannot be exhaustive in the compiler's eyes - InfrastructureType is a C# enum, so it can hold
/// any integer, including one written by a build that knew a member this one does not. That is a
/// data-integrity failure rather than something the user did, so it fails loudly here and the
/// contract test walks every declared member to catch the mismatch before production does.
let fromInfrastructureType (infrastructureType: InfrastructureType) : Result<Infrastructure, string> =
    match infrastructureType with
    | InfrastructureType.Google -> Ok Google
    | InfrastructureType.Microsoft -> Ok Microsoft
    | unknown -> Error $"Stored InfrastructureType '{unknown}' has no domain equivalent."

/// Persistence -> domain, whole row.
///
/// Returns Result because LiteDB is schemaless: a document written by an older build, or edited
/// by hand, can carry a null where a constrained type is required. The store turns a failure
/// here into a logged MyDogsbodyException.
let toStoredCredential (entity: Credential) : Result<StoredCredential, string> =
    match CredentialId.create (string entity.Id) with
    | Error reason -> Error reason
    | Ok id ->

    match fromInfrastructureType entity.InfrastructureType with
    | Error reason -> Error reason
    | Ok infrastructure ->

    match CredentialSecret.create entity.Credentials with
    | Error reason -> Error reason
    | Ok credentials ->

    match ExternalUsername.create entity.ExternalUsername with
    | Error reason -> Error reason
    | Ok externalUsername ->

    Ok
        {
            Id = id
            Infrastructure = infrastructure
            Credentials = credentials
            ExternalUsername = externalUsername
        }

/// Domain -> persistence, for a row the store has not seen before. No Id: LiteDB assigns it.
let toNewEntity (credential: ValidCredential) : Credential =
    Credential(
        InfrastructureType = toInfrastructureType credential.Infrastructure,
        Credentials = CredentialSecret.value credential.Credentials,
        ExternalUsername = ExternalUsername.value credential.ExternalUsername
    )

/// Copies a validated edit onto the entity the store already holds.
///
/// Mutates, because LiteDB entities are C# classes with settable properties - one of the
/// codebase's declared unavoidable cases. The mutation is confined to the row just fetched and
/// goes no further than this function.
let applyEdit (edit: ValidCredentialEdit) (entity: Credential) : Credential =
    entity.InfrastructureType <- toInfrastructureType edit.Infrastructure
    entity.Credentials <- CredentialSecret.value edit.Credentials
    entity.ExternalUsername <- ExternalUsername.value edit.ExternalUsername
    entity

/// The identifier the domain carries, as the store's own key type.
let toObjectId (id: CredentialId) : ObjectId = ObjectId(CredentialId.value id)
