using DataUtils;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// What the Graph user metadata mapping decided for one user: the normalised lookup names to
    /// resolve (a null name means "clear this lookup and its foreign key"), the fields copied straight
    /// across, and the timestamp to stamp.
    /// </summary>
    /// <remarks>
    /// This is a value, not an EF entity: it can be built and asserted without a database, a Graph
    /// call, or the lookup caches. <c>UserDataMapper</c> is what applies it to a
    /// <see cref="Common.Entities.User"/>.
    /// </remarks>
    public sealed class UserMetadataChangePlan
    {
        public bool? AccountEnabled { get; set; }
        public string PostalCode { get; set; }
        public string AzureAdId { get; set; }
        public string Mail { get; set; }

        /// <summary>Normalised department name, or null to clear the department and its FK.</summary>
        public string DepartmentName { get; set; }

        /// <summary>Normalised job title, or null to clear the job title and its FK.</summary>
        public string JobTitleName { get; set; }

        /// <summary>Normalised office location, or null to clear it and its FK.</summary>
        public string OfficeLocationName { get; set; }

        /// <summary>Normalised usage location, or null to clear it and its FK.</summary>
        public string UsageLocationName { get; set; }

        /// <summary>Normalised country / region, or null to clear it and its FK.</summary>
        public string CountryName { get; set; }

        /// <summary>Normalised state or province, or null to clear it and its FK.</summary>
        public string StateOrProvinceName { get; set; }

        /// <summary>Normalised company name, or null to clear it and its FK.</summary>
        public string CompanyName { get; set; }

        /// <summary>The manager's Entra (AAD) object id, or null when Graph reports no manager.</summary>
        public string ManagerAadId { get; set; }
    }

    /// <summary>
    /// The pure mapping rules between a Graph user and the analytics <c>users</c> row: trimming and
    /// capping the de-normalised lookup values, deciding which ones should be cleared, and copying the
    /// fields that live directly on the row.
    ///
    /// Extracted from <c>UserDataMapper</c> so the rules can be tested with zero SQL Server and zero
    /// Graph dependency - they were previously interleaved with EF change tracking and the lookup
    /// caches, and had no test of their own. See issues #371 / #381.
    /// </summary>
    public static class UserMetadataMappingRules
    {
        /// <summary>
        /// Maximum length of a de-normalised lookup value (department, job title, office location,
        /// usage location, country, state, company). Matches the width of those lookup tables' name
        /// columns.
        /// </summary>
        public const int LookupNameMaxLength = 100;

        /// <summary>
        /// Trims a value from Graph and caps it at <see cref="LookupNameMaxLength"/>, returning null
        /// when there is nothing left. Null is the signal to clear that lookup rather than resolve one.
        /// </summary>
        /// <remarks>
        /// Over-length values are truncated with a trailing ellipsis by
        /// <see cref="StringUtils.EnsureMaxLength(string, int)"/>, which is what the pipeline has always
        /// done. Trimming happens first, so a value that is only whitespace clears the lookup.
        /// </remarks>
        public static string NormaliseLookupName(string valueFromGraph)
        {
            var normalised = StringUtils.EnsureMaxLength(valueFromGraph?.Trim(), LookupNameMaxLength);
            return string.IsNullOrEmpty(normalised) ? null : normalised;
        }

        /// <summary>
        /// Builds the change plan for one Graph user.
        /// </summary>
        /// <remarks>
        /// <c>users.last_updated</c> is deliberately NOT part of the plan. The pipeline stamps it with
        /// <c>DateTime.Now</c> (local, not UTC) at the very end of the per-user update, after manager
        /// resolution - which can hit the database - so moving the read into this rule would change the
        /// stored value. <c>IClock</c> only exposes <c>UtcNow</c>, so converting it is a behavioural
        /// change and out of scope for #381.
        /// </remarks>
        public static UserMetadataChangePlan BuildPlan(GraphUser graphUser)
        {
            if (graphUser == null) throw new ArgumentNullException(nameof(graphUser));

            return new UserMetadataChangePlan
            {
                AccountEnabled = graphUser.AccountEnabled,
                PostalCode = graphUser.PostalCode,
                AzureAdId = graphUser.Id,
                Mail = graphUser.Mail,

                DepartmentName = NormaliseLookupName(graphUser.Department),
                JobTitleName = NormaliseLookupName(graphUser.JobTitle),
                OfficeLocationName = NormaliseLookupName(graphUser.OfficeLocation),
                UsageLocationName = NormaliseLookupName(graphUser.UsageLocation),
                CountryName = NormaliseLookupName(graphUser.Country),
                StateOrProvinceName = NormaliseLookupName(graphUser.State),
                CompanyName = NormaliseLookupName(graphUser.CompanyName),

                ManagerAadId = graphUser.DefaultManagerInfo?.Id,
            };
        }
    }
}
