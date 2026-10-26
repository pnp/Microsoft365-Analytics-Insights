using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// A DLP.All record together with its <c>SharePointMetaData</c>, which the org-URL whitelist needs to scope a match
    /// detected in SharePoint or OneDrive. <c>AuditLogContentDispatcher</c> deserialises every DLP record as this type.
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#sharepointmetadata-complex-type
    /// </summary>
    public class DlpAuditLogContentWithSharePointMetadata : DlpAuditLogContent
    {
    }

    /// <summary>The fields of the DLP schema's SharePointMetadata complex type that the importer reads.</summary>
    public class DlpSharePointMetadata
    {
        /// <summary>
        /// The site collection holding the document - the only site the record names. There is no subsite (web) URL.
        /// </summary>
        public string SiteCollectionUrl { get; set; }
    }
}
