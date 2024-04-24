using Common.Entities.Teams;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    // https://docs.microsoft.com/en-us/graph/api/resources/teamstab?view=graph-rest-1.0
    [Table("teams_tabs")]
    public class TeamTabDefinition : AbstractGraphEFEntityWithName
    {
        [Column("url")]
        public string WebUrl { get; set; }

        [Column("teams_addon_id")]
        [ForeignKey(nameof(TeamAddOnDefinition))]
        public int? TeamsAppDefinitionID { get; set; }

        public TeamAddOnDefinition TeamAddOnDefinition { get; set; }
    }
}
