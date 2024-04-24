using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    public abstract class BaseOfficeEvent
    {
        /// <summary>
        /// Foriegn key for "Event" only
        /// </summary>
        [Key]
        [ForeignKey("Event")]
        [Column("event_id")]
        public Guid EventID { get; set; }


        public Office365Event Event { get; set; }

    }

    public abstract class BaseExtendedPropertiesEvent<t> : BaseOfficeEvent
    {
        public BaseExtendedPropertiesEvent()
        {
            this.Properties = new List<t>();
        }
        public List<t> Properties { get; set; }
    }
}
