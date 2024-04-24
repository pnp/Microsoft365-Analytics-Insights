using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    /// <summary>
    /// All "extended properties" classes have this implementation. A "extended properties" class is a EF class that has a list of "prop/val" lookups.
    /// </summary>
    /// <typeparam name="t">Type of class to link back to</typeparam>
    public abstract class BaseExtendedProperties<t>
    {
        public int id { get; set; }

        [ForeignKey("PropNameID")]
        public AuditPropertyName name { get; set; }

        /// <summary>
        /// Foreign key for PropName lookup.
        /// </summary>
        [Column("prop_name_id")]
        public int PropNameID { get; set; }

        /// <summary>
        /// Foreign key for Value lookup
        /// </summary>
        [Column("prop_val_id")]
        public int PropValID { get; set; }

        [Column("event_id")]
        public Guid ParentEventID { get; set; }

        [ForeignKey("ParentEventID")]
        public t ParentEvent { get; set; }

        [ForeignKey("PropValID")]
        public AuditPropertyValue value { get; set; }

        public override string ToString()
        {
            return $"{this.name.name}: '{this.value.value}'";
        }
    }

    [Table("audit_event_exchange_props")]
    public class ExchangeExtendedProperties : BaseExtendedProperties<ExchangeEventMetadata>
    {
    }

    [Table("audit_event_azure_ad_props")]
    public class AzureADExtendedProperties : BaseExtendedProperties<AzureADEventMetadata>
    {
    }
}
