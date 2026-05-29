using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Email
{
    /// <summary>
    /// Intermediary table linking a <see cref="SentEmail"/> to one of its
    /// <see cref="EmailAddress"/> recipients. A sent message with several recipients
    /// produces a single <c>sent_emails</c> row plus one <c>sent_email_recipients</c>
    /// row per distinct recipient address, instead of duplicating message metadata
    /// for every recipient.
    /// </summary>
    [Table("sent_email_recipients")]
    public class SentEmailRecipient : AbstractEFEntity
    {
        [ForeignKey(nameof(SentEmail))]
        [Column("sent_email_id")]
        public int SentEmailID { get; set; }

        public SentEmail SentEmail { get; set; }

        [ForeignKey(nameof(RecipientAddress))]
        [Column("recipient_address_id")]
        public int RecipientAddressID { get; set; }

        public EmailAddress RecipientAddress { get; set; }
    }
}
