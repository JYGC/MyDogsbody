using LiteDB;

namespace MyDogsbody.Integrations.Google.Database.Models
{
    /// <summary>
    /// The application-wide OAuth client secret, pasted once and stored as a single row in a
    /// <c>ClientSecret</c> collection inside the Google integration's own LiteDB database.
    /// </summary>
    public class GoogleClientSecretEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;

        public string? Secret { get; set; }
    }
}
