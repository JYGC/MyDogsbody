using System;
using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One folder's watermark: its size and modification time when last read, and the byte
    /// offset reached - keyed by account AccountId and the folder's RelativePath.
    public class ScanWatermarkEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? AccountId { get; set; }
        public string? RelativePath { get; set; }
        public long SizeBytes { get; set; }
        public DateTime ModifiedAt { get; set; }
        public long OffsetReached { get; set; }
    }
}
