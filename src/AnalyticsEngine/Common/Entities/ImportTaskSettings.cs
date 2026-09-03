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
        /// Load from string format. "GraphUsersMetadata=True;GraphTeams=False;" etc
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

        /// <summary>
        /// Import the Power Platform workload - PowerApps / Power Automate / Power BI / Copilot Studio
        /// (also delivered via the Audit.General activity feed). Opt-in (default false) as it is a newer
        /// workload; when off, these events are dropped at dispatch (not imported, and no staging merges run).
        /// </summary>
        [ImportProp]
        public bool ImportPowerPlatform { get; set; } = false;

        /// <summary>
        /// Import the three Microsoft Graph Microsoft 365 Copilot usage reports (user-count summary,
        /// user-count trend and per-user usage detail). Independent of <see cref="Copilot"/>, which imports
        /// Copilot interactions from the Audit.General feed: this one is Microsoft's own official usage
        /// reporting, which is what the Microsoft 365 admin centre shows and therefore what customers compare
        /// our numbers against. Opt-in (default false) because it needs the Reports.Read.All application
        /// permission and is only available in the global cloud.
        /// </summary>
        [ImportProp]
        public bool GraphCopilotUsageReports { get; set; } = false;

        /// <summary>
        /// Import Microsoft 365 Copilot AI interaction history from Microsoft Graph
        /// (<c>/copilot/users/{id}/interactionHistory/getAllEnterpriseInteractions</c>).
        /// </summary>
        /// <remarks>
        /// Opt-in and off by default, deliberately more so than the other flags. The endpoint is
        /// <b>one HTTP call per user</b>, so at the ~200k-user design target an unscoped run would mean 200k
        /// Graph calls per cycle - it must be pointed at a pilot group via <c>UserGroupsFilter</c> and is
        /// additionally capped per cycle. It also needs the <c>AiEnterpriseInteraction.Read.All</c>
        /// application permission, which is not granted by the installer and requires explicit admin consent,
        /// and the <c>M365_COPILOT_BUSINESS_CHAT</c> service plan on each user.
        /// <para>
        /// Note the importer never stores prompt or response text - only derived statistics, plus sentiment
        /// and key phrases when cognitive services are configured.
        /// </para>
        /// </remarks>
        [ImportProp]
        public bool CopilotInteractionHistory { get; set; } = false;

        IEnumerable<PropertyInfo> GetImportProps()
        {
            return this.GetType().GetProperties().Where(p => Attribute.IsDefined(p, typeof(ImportPropAttribute)));
        }

        /// <summary>
        /// Every <see cref="ImportPropAttribute"/> toggle on this class, for callers that need to reason
        /// about the whole set generically rather than naming toggles one at a time - such as the installer's
        /// Test Configuration coverage check, which asserts no toggle ships without a verification decision.
        /// </summary>
        public static IReadOnlyList<PropertyInfo> GetImportPropertyInfos()
        {
            return typeof(ImportTaskSettings).GetProperties()
                .Where(p => Attribute.IsDefined(p, typeof(ImportPropAttribute)))
                .ToList();
        }

        /// <summary>Names of every <see cref="ImportPropAttribute"/> toggle on this class.</summary>
        public static IReadOnlyList<string> GetImportPropertyNames()
        {
            return GetImportPropertyInfos().Select(p => p.Name).ToList();
        }

        /// <summary>
        /// Whether the named <see cref="ImportPropAttribute"/> toggle is enabled. Throws for an unknown name
        /// rather than silently answering false, so a rename cannot quietly turn a check into a no-op.
        /// </summary>
        public bool IsImportEnabled(string importPropertyName)
        {
            var prop = GetImportPropertyInfos().SingleOrDefault(p => p.Name == importPropertyName)
                ?? throw new ArgumentException($"'{importPropertyName}' is not an [ImportProp] on {nameof(ImportTaskSettings)}.", nameof(importPropertyName));

            return (bool)prop.GetValue(this);
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
            // Copilot and Power Platform are both delivered via the Audit.General feed.
            if (Copilot || ImportPowerPlatform) types.Add(CONTENT_TYPE_AUDIT_GENERAL);
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
