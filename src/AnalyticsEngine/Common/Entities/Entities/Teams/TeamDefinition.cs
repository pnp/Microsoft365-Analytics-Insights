using Common.Entities.Entities.Teams;
using Common.Entities.Teams;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    [Table("teams")]
    public class TeamDefinition : AbstractGraphEFEntityWithName
    {
        public TeamDefinition()
        {
            this.Owners = new List<TeamOwners>();
        }

        public List<TeamChannel> Channels { get; set; }
        public List<TeamAddOnLog> AddOns { get; set; }

        /// <summary>
        /// Date Team was first discovered
        /// </summary>
        [Column("discovered")]
        [Required]
        public DateTime FirstDiscovered { get; set; }

        [Column("has_refresh_token")]
        [Required]
        public bool HasRefreshToken { get; set; }

        [Column("last_refresh")]
        public DateTime? LastRefreshed { get; set; }

        public List<TeamOwners> Owners { get; set; }

    }

}
