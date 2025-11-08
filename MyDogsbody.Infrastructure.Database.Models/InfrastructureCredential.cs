using MyDogsbody.Enums;

namespace MyDogsbody.Infrastructure.Database.Models
{
    public class InfrastructureCredential
    {
        public InfrastructureType InfrastructureType { get; set; }
        public string? Credentials { get; set; }
        public string? ExternalUsername { get; set; }
    }
}
