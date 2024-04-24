using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{

    [Table("event_meta_azure_ad")]
    public class AzureADEventMetadata : BaseExtendedPropertiesEvent<AzureADExtendedProperties>
    {
    }
}
