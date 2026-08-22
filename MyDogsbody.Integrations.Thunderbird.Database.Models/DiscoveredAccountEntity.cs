using System;
using System.Collections.Generic;
using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One account a scan discovered. AccountId is "{profileDir}|{accountKey}" - matches the
    /// domain's MailAccountId exactly, so no separate identifier scheme is needed.
    public class DiscoveredAccountEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? AccountId { get; set; }
        public string? ProfilePath { get; set; }
        public string? DisplayName { get; set; }
        public List<string> EmailAddresses { get; set; } = new();
        public string? StoreFormat { get; set; }
        public string? StoreDirectory { get; set; }
        public bool StoreDirectoryExists { get; set; }
        public int? CachedMessageCount { get; set; }
        public DateTime? CachedMessageCountTakenAt { get; set; }
    }
}
