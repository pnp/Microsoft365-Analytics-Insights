using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Email
{
    /// <summary>
    /// Common lookup table for email addresses
    /// </summary>
    [Table("email_addresses")]
    public class EmailAddress : AbstractEFEntity
    {
        [Column("address")]
        [MaxLength(450)]
        [Required]
        public string Address { get; set; }

        public override string ToString()
        {
            return $"{base.ToString()},{nameof(Address)}={Address}";
        }
    }
}
