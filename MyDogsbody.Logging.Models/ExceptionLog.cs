namespace MyDogsbody.Logging.Models
{
    public class ExceptionLog
    {
        public string Id { get; set; }
        public string Message { get; set; }
        public string ActionName { get; set; }
        public Exception ExceptionDetails { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
