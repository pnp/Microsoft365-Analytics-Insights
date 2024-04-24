
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A search done by a user. Has a lookup to search-term.
    /// </summary>
    [Table("searches")]
    public class Search : AbstractEFEntity
    {

        public virtual SearchTerm search_term { get; set; }
        public virtual UserSession session { get; set; }


        [Column("date_time")]
        public DateTime? DateTime { get; set; }

    }
}
