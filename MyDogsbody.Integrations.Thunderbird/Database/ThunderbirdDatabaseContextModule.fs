module MyDogsbody.Integrations.Thunderbird.Database.ThunderbirdDatabaseContextModule

open LiteDB
open MyDogsbody.Integrations.Thunderbird.Database.Types
open MyDogsbody.Integrations.Thunderbird.Database.Models

let getDatabaseContext (databasePath: string) (connectionType: string) : ThunderbirdDatabaseContext =
    // Force every entity's mapping to be built here, on one thread, before the context is
    // handed out. LiteDB caches it on the global BsonMapper and builds it lazily on first use,
    // so two threads mapping the same entity for the first time at once can observe a
    // half-built mapping and silently drop a property. This was a 6-in-10 intermittent failure
    // once already (CLAUDE-project.md -> Per-integration databases) - it does not fully close
    // the race, but it still serialises the common path.
    BsonMapper.Global.ToDocument(ThunderbirdProfileRoot()) |> ignore
    BsonMapper.Global.ToDocument(DiscoveredAccountEntity()) |> ignore
    BsonMapper.Global.ToDocument(DiscoveredFolderEntity()) |> ignore
    BsonMapper.Global.ToDocument(SelectedAccountEntity()) |> ignore
    BsonMapper.Global.ToDocument(ScanWatermarkEntity()) |> ignore

    let connectionString = $"Filename={databasePath};connection={connectionType}"
    let db = new LiteDatabase(connectionString)

    {
        GetProfileRootCollection = fun () -> db.GetCollection<ThunderbirdProfileRoot> "ProfileRoot"
        GetAccountsCollection = fun () -> db.GetCollection<DiscoveredAccountEntity> "Accounts"
        GetFoldersCollection = fun () -> db.GetCollection<DiscoveredFolderEntity> "Folders"
        GetSelectedAccountCollection = fun () -> db.GetCollection<SelectedAccountEntity> "SelectedAccount"
        GetWatermarksCollection = fun () -> db.GetCollection<ScanWatermarkEntity> "Watermarks"

        Dispose = fun () -> db.Dispose()
    }
