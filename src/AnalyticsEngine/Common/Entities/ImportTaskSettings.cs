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
