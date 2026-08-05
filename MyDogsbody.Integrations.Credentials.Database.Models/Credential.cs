using LiteDB;
using MyDogsbody.Enums;

namespace MyDogsbody.Integrations.Credentials.Database.Models
{
    public class Credential
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public InfrastructureType InfrastructureType { get; set; }
        public string? Credentials { get; set; }
        public string? ExternalUsername { get; set; }
    }
}
