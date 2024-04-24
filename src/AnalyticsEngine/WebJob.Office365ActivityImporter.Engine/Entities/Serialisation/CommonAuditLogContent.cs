using Common.Entities;
using Common.Entities.Entities.AuditLog;
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
        public List<Dictionary<string, string>> ExtendedProperties { get; set; }

        /// <summary>
        /// JSon used to deserialise the content-set. Used in import-log, and generic events for all data. 
        /// </summary>
        public string OriginalImportFileContents { get; set; }

        public DateTime CreationTime { get; set; }
        public Guid Id { get; set; }

        public string Operation { get; set; }

        public string UserId { get; set; }

        public string ItemType { get; set; }
        public string ObjectId { get; set; }


        #endregion


        /// <summary>
        /// Save new common + specific event to SQL.
        /// </summary>
        public abstract Task<bool> ProcessExtendedProperties(SaveSession saveBatch, Office365Event relatedAuditEvent);

        /// <summary>
        /// Saves "property_name" and "property_value" records for a set of extended properties
        /// </summary>
        protected Dictionary<AuditPropertyName, AuditPropertyValue> GetPropertiesAndValues(SaveSession saveSession)
        {
            var dbProps = new Dictionary<AuditPropertyName, AuditPropertyValue>();

            // Read each extended property
            foreach (var item in this.ExtendedProperties)
            {
                if (item.Values.Count >= 2)
                {
                    string name = item.Values.First();
                    string val = item.Values.Skip(1).First();

                    // Try and find an existing name & value
                    AuditPropertyName nameRec = saveSession.SharePointLookupManager.GetAuditPropertyNames().Where(n => n.name == name).FirstOrDefault();
                    if (nameRec == null)
                    {
                        nameRec = new AuditPropertyName() { name = name };
                        saveSession.Database.audit_event_prop_names.Add(nameRec);
                    }

                    AuditPropertyValue valRec = saveSession.SharePointLookupManager.GetPropVals().Where(n => n.value == val).FirstOrDefault();
                    if (valRec == null)
                    {
                        valRec = new AuditPropertyValue() { value = val };
                        saveSession.Database.audit_event_prop_vals.Add(valRec);
                    }

                    // Add new propery with lookups
                    dbProps.Add(nameRec, valRec);
                }

            }
            return dbProps;
        }


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
        OutOfScope = 3          // Not to be imported. Usually because the SharePoint URL is for a site we don't care about.
    }
}
