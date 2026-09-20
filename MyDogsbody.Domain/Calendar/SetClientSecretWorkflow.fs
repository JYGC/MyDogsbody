module MyDogsbody.Domain.Calendar.SetClientSecretWorkflow

open MyDogsbody.Domain.Calendar

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - SetClientSecretWorkflow.fs: setClientSecret
let setClientSecret (saveClientSecret: SaveClientSecret) (secret: string) : Result<unit, CalendarError> =
    if System.String.IsNullOrWhiteSpace secret then
        Error(ClientSecretInvalid "Google client secret must not be empty.")
    else
        saveClientSecret secret
