using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One row: the currently selected account's AccountId. Absent means none selected.
    public class SelectedAccountEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? AccountId { get; set; }
    }
}
