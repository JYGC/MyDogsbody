using LiteDB;

namespace MyDogsbody.Integrations.Google.Database.Models
{
    /// <summary>
    /// A credential for the Google integration, stored in a <c>Credentials</c> collection inside
    /// the integration's own LiteDB database. The database identifies the provider, so there is
    /// no discriminator column - carrying one would be a second source of truth for a fact the
    /// file path already states.
    /// </summary>
    public class GoogleCredential
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;

        /// <summary>The secret itself - in change #6 an OAuth refresh token - stored byte-for-byte as entered.</summary>
        public string? Credentials { get; set; }

        /// <summary>The username the credential authenticates as at Google.</summary>
        public string? ExternalUsername { get; set; }
    }
}
