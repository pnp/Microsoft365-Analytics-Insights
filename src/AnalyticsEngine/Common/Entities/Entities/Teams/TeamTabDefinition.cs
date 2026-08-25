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

        // Note: the underlying table still has a nullable "teams_addon_id" column left over from the
        // deprecated Teams add-on tracking (see migration DeprecateTeamsAddons). It is deliberately not
        // mapped: the add-on entities are gone, and dropping the column would rewrite a table we keep.
    }
}
