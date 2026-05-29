using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Email
{
    /// <summary>
    /// A sent email record from a mailbox. Recipients (the "To" addresses) live in
    /// <see cref="SentEmailRecipient"/> so message-level fields are not duplicated when a
    /// message is sent to several people.
    /// </summary>
    [Table("sent_emails")]
    public class SentEmail : AbstractEFEntity
    {
        [Column("subject")]
        [MaxLength(1000)]
        public string Subject { get; set; }

        [Column("sent_date")]
        public DateTime SentDate { get; set; }

        // 450 chars keeps the unique index under SQL Server's 900-byte legacy
        // key limit (450 * 2-byte nvarchar = 900). Graph message IDs are well
        // inside that, but the wider 500-char setting would only work on
        // SQL Server 2016+/Azure SQL where the limit was raised to 1700 bytes.
        [Column("graph_message_id")]
        [MaxLength(450)]
        [Required]
        public string GraphMessageId { get; set; }

        [Column("cognitive_score")]
        public double? CognitiveScore { get; set; }

        #region From Address

        [ForeignKey(nameof(FromAddress))]
        [Column("from_address_id")]
        public int FromAddressID { get; set; }

        public EmailAddress FromAddress { get; set; }

        #endregion

        #region User

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserID { get; set; }

        public User User { get; set; }

        #endregion

        /// <summary>
        /// Recipients for this message. Stored in the <c>sent_email_recipients</c>
        /// intermediary table so a message with N recipients persists as one
        /// <c>sent_emails</c> row plus N <c>sent_email_recipients</c> rows.
        /// </summary>
        public virtual ICollection<SentEmailRecipient> Recipients { get; set; } = new List<SentEmailRecipient>();
    }
}
