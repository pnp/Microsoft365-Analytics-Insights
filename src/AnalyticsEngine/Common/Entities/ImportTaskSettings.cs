using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Common.Entities
{
    /// <summary>
    /// What to import for the solution.
    /// All [ImportProp] flags default to <c>false</c> (opt-in) so a fresh / unconfigured
    /// install does not start writing data to the database unexpectedly.
    /// Each flag must be explicitly enabled via the settings string or property setter.
    /// </summary>
    public class ImportTaskSettings : IEquatable<ImportTaskSettings>
    {
        #region Constructors

        const string SEP = ";";
        public ImportTaskSettings()
        {
        }

        /// <summary>
        /// Load from string format. "GraphUsersMetadata=True;GraphUserApps=False;" etc
        /// </summary>
        /// <param name="settingsString"></param>
        public ImportTaskSettings(string settingsString)
        {
            if (string.IsNullOrEmpty(settingsString))
            {
                return;
            }

            var tokens = settingsString.Split(SEP.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                foreach (var p in GetImportProps())
                {
                    Parse(p, token);
                }
            }
        }

        private void Parse(PropertyInfo propertyInfo, string token)
        {
            // The field initializer is the source of truth for defaults.
            // Parse only overrides the default when the token explicitly specifies a value.
            var lowerToken = token.ToLower();
            var lowerName = propertyInfo.Name.ToLower();
            if (lowerToken.Contains($"{lowerName}=false"))
            {
                propertyInfo.SetValue(this, false);
            }
            else if (lowerToken.Contains($"{lowerName}=true"))
            {
                propertyInfo.SetValue(this, true);
            }
        }
        #endregion


        [ImportProp]
        public bool Calls { get; set; } = false;


        [ImportProp]
        public bool GraphUsersMetadata { get; set; } = false;

        /// <summary>
        /// User Teams apps for user refresh
        /// </summary>
        [ImportProp]
        public bool GraphUserApps { get; set; } = false;

        [ImportProp]
        public bool GraphUsageReports { get; set; } = false;

        [ImportProp]
        public bool GraphTeams { get; set; } = false;

        [ImportProp]
        public bool ActivityLog { get; set; } = false;

        /// <summary>
        /// SPO analytics with JS
        /// </summary>
        [ImportProp]
        public bool WebTraffic { get; set; } = false;

        /// <summary>
        /// Import sent emails from mailboxes via Graph.
        /// </summary>
        [ImportProp]
        public bool SentEmails { get; set; } = false;

        /// <summary>
        /// Import Microsoft 365 Copilot interactions (delivered via the Audit.General activity feed).
        /// </summary>
        [ImportProp]
        public bool Copilot { get; set; } = false;

        IEnumerable<PropertyInfo> GetImportProps()
        {
            return this.GetType().GetProperties().Where(p => Attribute.IsDefined(p, typeof(ImportPropAttribute)));
        }

        public string ToSettingsString()
        {
            var s = string.Empty;
            foreach (var p in GetImportProps())
            {
                s += $"{p.Name}={p.GetValue(this)}{SEP}";
            }
            return s.TrimEnd(SEP.ToCharArray());
        }

        /// <summary>
        /// Office 365 Management Activity API content-type that delivers Copilot interactions
        /// (and other "general" workloads such as Power Platform).
        /// </summary>
        public const string CONTENT_TYPE_AUDIT_GENERAL = "Audit.General";

        /// <summary>
        /// Office 365 Management Activity API content-type for SharePoint / OneDrive audit events.
        /// </summary>
        public const string CONTENT_TYPE_AUDIT_SHAREPOINT = "Audit.SharePoint";

        /// <summary>
        /// Builds the "ContentTypesListAsString" value (the Office 365 Management Activity API feeds
        /// to subscribe to) from the enabled audit-based imports: <see cref="Copilot"/> =&gt;
        /// Audit.General, <see cref="ActivityLog"/> (SharePoint audit) =&gt; Audit.SharePoint.
        /// Falls back to Audit.SharePoint when no audit source is selected so the runtime always has
        /// a valid (if unused) workload list.
        /// </summary>
        public string ToActivityApiContentTypesString()
        {
            var types = new List<string>();
            if (Copilot) types.Add(CONTENT_TYPE_AUDIT_GENERAL);
            if (ActivityLog) types.Add(CONTENT_TYPE_AUDIT_SHAREPOINT);
            return types.Count > 0 ? string.Join(SEP, types) : CONTENT_TYPE_AUDIT_SHAREPOINT;
        }

        public bool HaveSomethingToDo()
        {
            foreach (var p in GetImportProps())
            {
                var propVal = (bool)p.GetValue(this);
                if (propVal == true)
                {
                    return true;
                }
            }
            return false;
        }

        public bool Equals(ImportTaskSettings other)
        {
            if (ReferenceEquals(null, other)) return false;

            foreach (var p in GetImportProps())
            {
                var thisVal = p.GetValue(this);
                var otherVal = p.GetValue(other);
                var valuesMatch = false;

                if (thisVal is bool && otherVal is bool) valuesMatch = (bool)thisVal == (bool)otherVal;
                if (!valuesMatch) return false;
            }

            return true;
        }

        public class ImportPropAttribute : Attribute
        {
        }
    }
}
