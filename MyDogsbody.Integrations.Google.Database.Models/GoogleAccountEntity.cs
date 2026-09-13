using LiteDB;

namespace MyDogsbody.Integrations.Google.Database.Models
{
    /// <summary>
    /// A registered Google account, stored in an <c>Accounts</c> collection inside the Google
    /// integration's own LiteDB database. <see cref="Id"/> is minted by the authorisation adapter
    /// before the browser opens, and doubles as the join key into the <c>Credentials</c>
    /// collection's <c>ExternalUsername</c> field - the two rows for one account share one id.
    /// </summary>
    public class GoogleAccountEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;

        public string? EmailAddress { get; set; }

        /// <summary>Null when the account genuinely has no default calendar chosen yet - Q2.11.</summary>
        public string? DefaultInvoiceCalendarId { get; set; }

        public bool NeedsReauthorisation { get; set; }
    }
}
