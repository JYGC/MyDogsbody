namespace MyDogsbody.Integrations.Credentials.Database.Types

open LiteDB
open MyDogsbody.Integrations.Credentials.Database.Models

type CredentialsCollection = ILiteCollection<Credential>

type CredentialsDatabaseContext =
    {
        GetCredentialCollection: unit -> CredentialsCollection
    }