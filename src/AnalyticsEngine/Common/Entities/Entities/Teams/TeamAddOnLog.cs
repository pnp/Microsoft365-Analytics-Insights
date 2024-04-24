using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Teams
{
    public interface IAddOnLog
    {
        TeamAddOnDefinition AddOn { get; set; }

        int AddOnID { get; set; }
    }

    /// <summary>
    /// Team & AddOn (app) definition association for a given date
    /// </summary>
    [Table("teams_addons_log")]
    public class TeamAddOnLog : TeamLog, IAddOnLog
    {

        public TeamAddOnDefinition AddOn { get; set; }

        [ForeignKey("AddOn")]
        [Column("addon_id")]
        public int AddOnID { get; set; }

    }

    /// <summary>
    /// User + app definition for a given date
    /// </summary>
    [Table("teams_addons_user_installed_log")]
    public class UserAppsLog : AbstractEFEntity, IDateLog, IAddOnLog, IUserRelatedEntity
    {
        [Column("date")]
        public DateTime Date { get; set; }
        public TeamAddOnDefinition AddOn { get; set; }

        [ForeignKey("AddOn")]
        [Column("addon_id")]
        public int AddOnID { get; set; }

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserID { get; set; }
        public User User { get; set; }
    }
}
