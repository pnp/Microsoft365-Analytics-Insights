
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A user session, containing 1+ hits.
    /// </summary>
    [Table("sessions")]
    public partial class UserSession : AbstractEFEntity
    {
        public string ai_session_id { get; set; }

        public virtual User user { get; set; }
    }
}
