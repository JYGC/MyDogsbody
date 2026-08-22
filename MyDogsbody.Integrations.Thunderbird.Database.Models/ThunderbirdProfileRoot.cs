using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One row: the folder the user chose to search for Thunderbird profiles.
    public class ThunderbirdProfileRoot
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? Path { get; set; }
    }
}
