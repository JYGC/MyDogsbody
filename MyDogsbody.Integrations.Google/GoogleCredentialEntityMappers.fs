/// The bottom mapping point: LiteDB entity (C#, ObjectId, nullable strings) <-> the integration's
/// own credential types.
///
/// Pure - no I/O, no handleError, no LiteDB calls. GoogleCredentialStore does the talking; this
/// file only translates, so it can be asserted field-for-field without a database.
///
/// There is no InfrastructureType here and there is no toInfrastructureType / fromInfrastructureType
/// pair: the retired shared store needed a discriminator because one collection held every
/// provider's rows. This collection holds only Google's, so the database is the provider.
module MyDogsbody.Integrations.Google.GoogleCredentialEntityMappers

open LiteDB
open MyDogsbody.Integrations.Google
open MyDogsbody.Integrations.Google.Database.Models

/// Persistence -> the integration's type, whole row.
///
/// Returns Result because LiteDB is schemaless: a document written by an older build, or edited
/// by hand, can carry a null where a constrained type is required. The store turns a failure
/// here into a logged MyDogsbodyException.
let toStoredCredential (entity: GoogleCredential) : Result<StoredGoogleCredential, string> =
    match GoogleCredentialId.create (string entity.Id) with
    | Error reason -> Error reason
    | Ok id ->

    match GoogleCredentialSecret.create entity.Credentials with
    | Error reason -> Error reason
    | Ok secret ->

    match GoogleExternalUsername.create entity.ExternalUsername with
    | Error reason -> Error reason
    | Ok username ->

    Ok
        {
            Id = id
            Secret = secret
            Username = username
        }

/// The integration's type -> persistence, for a row the store has not seen before. No Id: LiteDB
/// assigns it.
let toNewEntity (credential: ValidGoogleCredential) : GoogleCredential =
    GoogleCredential(
        Credentials = GoogleCredentialSecret.value credential.Secret,
        ExternalUsername = GoogleExternalUsername.value credential.Username
    )

/// Copies a validated edit onto the entity the store already holds.
///
/// Mutates, because LiteDB entities are C# classes with settable properties - one of the
/// codebase's declared unavoidable cases. The mutation is confined to the row just fetched.
let applyEdit (edit: ValidGoogleCredentialEdit) (entity: GoogleCredential) : GoogleCredential =
    entity.Credentials <- GoogleCredentialSecret.value edit.Secret
    entity.ExternalUsername <- GoogleExternalUsername.value edit.Username
    entity

/// The identifier the integration carries, as the store's own key type.
let toObjectId (id: GoogleCredentialId) : ObjectId = ObjectId(GoogleCredentialId.value id)
