using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A user page-hit. Contains lookups to metadata. 
    /// </summary>
    [Table("hits")]
    public class Hit : AbstractEFEntity
    {
        public DateTime hit_timestamp { get; set; }

        public Nullable<double> seconds_on_page { get; set; }

        public Guid page_request_id { get; set; }

        public Nullable<double> page_load_time { get; set; }

        [Column("sp_request_duration")]
        public int? SPRequestDuration { get; set; }
        // Lookups
        public virtual Web web { get; set; }

        public virtual Browser agent { get; set; }
        public virtual City city { get; set; }
        public virtual Country country { get; set; }
        public virtual Device device { get; set; }
        public virtual PageTitle page_title { get; set; }
        public virtual OperatingSystem os { get; set; }
        public virtual UserSession session { get; set; }
        public virtual Url url { get; set; }
        public virtual Province location_province { get; set; }
    }
}
