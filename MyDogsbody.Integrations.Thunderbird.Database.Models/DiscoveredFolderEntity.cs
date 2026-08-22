using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One folder belonging to one discovered account, keyed by that account's AccountId.
    public class DiscoveredFolderEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? AccountId { get; set; }
        public string? RelativePath { get; set; }
        public string? DisplayName { get; set; }
        public long SizeBytes { get; set; }
        public bool IsScannable { get; set; }
    }
}
