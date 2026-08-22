namespace MyDogsbody.Integrations.Thunderbird.Database.Types

open System
open LiteDB
open MyDogsbody.Integrations.Thunderbird.Database.Models

type ProfileRootCollection = ILiteCollection<ThunderbirdProfileRoot>
type AccountsCollection = ILiteCollection<DiscoveredAccountEntity>
type FoldersCollection = ILiteCollection<DiscoveredFolderEntity>
type SelectedAccountCollection = ILiteCollection<SelectedAccountEntity>
type WatermarksCollection = ILiteCollection<ScanWatermarkEntity>

/// The integration's private store, handed out as a record of getters. The collection getter
/// stops here: MyDogsbody.Domain never names ILiteCollection, LiteDatabase, ObjectId or
/// BsonValue.
type ThunderbirdDatabaseContext =
    {
        GetProfileRootCollection: unit -> ProfileRootCollection
        GetAccountsCollection: unit -> AccountsCollection
        GetFoldersCollection: unit -> FoldersCollection
        GetSelectedAccountCollection: unit -> SelectedAccountCollection
        GetWatermarksCollection: unit -> WatermarksCollection

        /// Releases the underlying database handle. Windows keeps a LiteDB file locked until
        /// this runs.
        Dispose: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Dispose()
