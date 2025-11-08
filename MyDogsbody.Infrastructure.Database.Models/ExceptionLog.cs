namespace MyDogsbody.Infrastructure.Database.Models
{
    public class ExceptionLog
    {
        public string? Message { get; set; }
        public string? ActionName { get; set; }
        public string? ExceptionDetails { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
