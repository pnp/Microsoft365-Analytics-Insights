using Common.Entities.Teams;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    [Table("team_owners")]
    public class TeamOwners : TeamRelatedEntity
    {

        public User Owner { get; set; }

        [ForeignKey(nameof(Owner))]
        [Column("owner_id")]
        public int OwnerID { get; set; }

        [Column("discovered")]
        public DateTime Discovered { get; set; }
    }
}
