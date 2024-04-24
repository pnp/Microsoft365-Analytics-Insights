using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Teams
{
    [Table("teams_addons")]
    public class TeamAddOnDefinition : AbstractGraphEFEntityWithName
    {

        [Column("addon_type")]
        public int AddOnType { get; set; }

        [MaxLength(50)]
        [Column("published_state")]
        public string PublishedState { get; set; }
    }
}
