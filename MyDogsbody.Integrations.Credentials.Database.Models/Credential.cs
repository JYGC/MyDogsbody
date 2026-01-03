using MyDogsbody.Enums;

namespace MyDogsbody.Integrations.Credentials.Database.Models
{
    public class Credential
    {
        public string Id { get; set; } = string.Empty;
        public InfrastructureType InfrastructureType { get; set; }
        public string? Credentials { get; set; }
        public string? ExternalUsername { get; set; }
    }
}
