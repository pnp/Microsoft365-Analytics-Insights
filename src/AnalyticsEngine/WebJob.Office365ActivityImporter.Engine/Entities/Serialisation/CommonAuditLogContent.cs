using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// JSon entity for audit-log response. Deserialised in AuditLogContentSet.LoadFromWeb
    /// Reference: https://docs.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema
    /// </summary>
    public abstract class AbstractAuditLogContent : WorkloadOnlyAuditLogContent, IEquatable<AbstractAuditLogContent>
    {
        /// <summary>
        /// Default constructor. Must be left so deserialisation can work
        /// </summary>
        public AbstractAuditLogContent()
        {
            this.ExtendedProperties = new List<Dictionary<string, string>>();
        }

        #region Props

        // Serialisation properties 

        /// <summary>
        /// The Management Activity API common-schema "ExtendedProperties" bag. Still deserialised (and carried
        /// forward when a unified PowerPlatform record is mapped to a workload-specific record), but no longer
        /// persisted: the audit_event_prop_names / audit_event_prop_vals / audit_event_*_props tables that used
        /// to hold it were retired because nothing read them. The raw record is still kept verbatim in
        /// event_meta_general.json for the workloads that write it.
        /// </summary>
        public List<Dictionary<string, string>> ExtendedProperties { get; set; }

        /// <summary>
        /// JSon used to deserialise the content-set. Used in import-log, and generic events for all data. 
        /// </summary>
        public string OriginalImportFileContents { get; set; }

        /// <summary>
        /// Transient (not persisted, not serialised) id of the Activity API content blob this event was
        /// downloaded from. Set by the importer after loading a blob so the blob-level checkpoint can tell
        /// when every event of a blob has been committed. Never sent by the API.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string SourceContentId { get; set; }

        public DateTime CreationTime { get; set; }
        public Guid Id { get; set; }

        public string UserId { get; set; }

        public string ItemType { get; set; }
        public string ObjectId { get; set; }


        #endregion


        /// <summary>
        /// Save new common + specific event to SQL.
        /// </summary>
        public abstract Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger);


        #region IEquatable<AuditLogContent>

        public bool Equals(AbstractAuditLogContent other)
        {
            if (other == null)
            {
                return false;
            }

            IList diffProperties = new ArrayList();
            foreach (var prop in other.GetType().GetProperties())
            {
                if (!Object.Equals(
                    prop.GetValue(other, null),
                    this.GetType().GetProperty(prop.Name).GetValue(this, null)))
                {
                    diffProperties.Add(prop);
                }
            }

            return diffProperties.Count == 0;

        }

        #endregion
    }


    public enum SaveResultEnum
    {
        NotSaved = 0,           // Default
        ProcessedAlready = 1,   // Event ignored previously
        Imported = 2,           // Already imported
        UrlOutOfScope = 3,          // Not to be imported. Usually because the SharePoint URL is for a site we don't care about.
        UserOutOfScope = 4,          // Not to be imported. Usually because the user is outside group filter
    }
}
