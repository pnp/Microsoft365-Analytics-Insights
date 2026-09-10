using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LicenceActivity
{
    public interface ILicenceActivityStore
    {
        Task<LicenceActivityOverview> LoadOverviewAsync(
            LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken);

        Task<LicenceActivityUsers> LoadUsersAsync(
            LicenceActivityOverview overview, LicenceActivityQuery query, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken);
    }

    public interface ILicenceActivityDiagnostics
    {
        void Stage(string stage, long elapsedMs = 0);
    }

    public sealed class NullLicenceActivityDiagnostics : ILicenceActivityDiagnostics
    {
        public static readonly NullLicenceActivityDiagnostics Instance = new NullLicenceActivityDiagnostics();
        private NullLicenceActivityDiagnostics() { }
        public void Stage(string stage, long elapsedMs = 0) { }
    }

    public sealed class LicenceActivitySources
    {
        public bool UserMetadata { get; set; }
        public bool UsageReports { get; set; }
        public bool CopilotUsageReports { get; set; }
        public bool CopilotAudit { get; set; }
        public bool CopilotInteractions { get; set; }

        /// <summary>
        /// True when <c>UserGroupsFilter</c> scopes the Microsoft 365 usage-report import to particular
        /// Entra groups.
        ///
        /// This matters because the USER import is not filtered while the usage-report import is, so the
        /// two populations differ. Normally a person with no rows across a fully imported week is proof
        /// that they did nothing; under a group filter they may simply never have been looked at, and
        /// reporting them as "No activity" would be a confident wrong answer. When this is set the
        /// report falls back to leaving those people Unknown.
        /// </summary>
        public bool UsageReportsGroupFiltered { get; set; }

        public DateTime NowUtc { get; set; }

        public string CacheKey => string.Join(":", UserMetadata, UsageReports, CopilotUsageReports,
            CopilotAudit, CopilotInteractions, UsageReportsGroupFiltered,
            NowUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static class LicenceActivityRules
    {
        public const string Method =
            "Activity levels describe how many of the period's weeks someone was active in: "
            + "No activity = none, Low = under a quarter, Moderate = a quarter to under three quarters, "
            + "High = three quarters or more. A week is only counted when every one of its days was "
            + "imported; where a week could not be measured in full the level is Unknown, not zero.";

        public const string AssignmentCaveat =
            "Past activity is shown against who holds each licence today, not who held it at the time. "
            + "One person can hold several licences, so adding the assignment figures together will count some people twice. "
            + "Assigned licences are not the same as the number of licences you have bought.";

        public const string InterpretationCaveat =
            "Activity by someone who holds a licence does not prove that this licence is what enabled it. These figures "
            + "do not measure productivity, return on investment or compliance, and are not enough on their own to justify "
            + "removing anyone's licence.";

        public static string Band(int activeSamples, int observedSamples, int expectedSamples)
        {
            if (expectedSamples <= 0 || observedSamples != expectedSamples || activeSamples < 0 || activeSamples > observedSamples)
                return "unknown";
            if (activeSamples == 0) return "zero";
            if ((long)activeSamples * 4 < expectedSamples) return "low";
            return (long)activeSamples * 4 < (long)expectedSamples * 3 ? "moderate" : "high";
        }

        /// <summary>
        /// Where a workload's figures came from, named the way a business reader or Microsoft 365
        /// administrator would recognise it.
        ///
        /// <see cref="LicenceActivityCoverage.Source"/> carries a STABLE IDENTIFIER (<c>LicenceActivitySql.M365ReportSource</c>
        /// and friends) because both the coverage SQL and <c>LicenceActivityReadModel.IsOfficialReport</c>
        /// match on it. Those identifiers are Graph API names, and this report is read by business
        /// leaders and M365 admins, so they are translated here at the point of display. Anything
        /// unrecognised is returned unchanged, so a new source degrades to showing its identifier
        /// rather than disappearing.
        ///
        /// Mirrored in the portal by <c>components/licenceActivity/sources.ts</c>, exactly as the
        /// coverage status vocabulary is mirrored by <c>statuses.ts</c>.
        /// </summary>
        public static string SourceLabel(string source)
        {
            switch (source)
            {
                case LicenceActivitySql.M365ReportSource: return "Microsoft 365 usage reports";
                case LicenceActivitySql.CopilotReportSource: return "Microsoft 365 Copilot usage report";
                case LicenceActivitySql.CopilotAuditSource: return "Copilot audit log";
                case LicenceActivitySql.CopilotInteractionSource: return "Copilot chat history";
                default: return source;
            }
        }

        /// <summary>How often that source was read, in plain English. See <see cref="SourceLabel"/>.</summary>
        public static string GranularityLabel(string granularity)
        {
            switch (granularity)
            {
                case "weeklySupportingSnapshot": return "one reading per week";
                case "singleRollingWindow": return "a single rolling report";
                case "weeklySampleOfRolling7DayReport": return "one 7-day report read per week";
                case "eventPositiveOnly": return "recorded activity only";
                case "unknown": return "not applicable";
                default: return granularity;
            }
        }

        /// <summary>
        /// The notes shown above the report. Defined here because there are TWO paths that build an
        /// overview - <c>SqlLicenceActivityStore</c> (direct) and <c>LicenceActivityReadModel</c> (the
        /// cached path the controller actually uses) - and they previously carried duplicate copies of
        /// these strings, so a wording fix applied to one silently missed production.
        /// </summary>
        public static class Notes
        {
            public const string NoLicences = "No licences have been imported yet.";

            public const string NobodyHoldsALicence =
                "Licences have been imported, but nobody in this selection currently holds one.";

            public const string NoDisplayNames =
                "Staff names aren't collected by this product, so people are listed by their sign-in address. "
                + "Search also checks their stored email address.";

            public const string DemographicsCapped =
                "The department and country breakdowns show only the 50 largest of each.";

            public const string UsageReportsGroupFiltered =
                "This deployment only collects Microsoft 365 usage for people in particular Entra groups, "
                + "but it lists everyone who holds a licence. Anyone outside those groups is shown as "
                + "Unknown rather than as doing nothing, because they were never measured.";

            public const string RankingMethod =
                "The most and least active lists rank people by how often they were active in the chosen service, "
                + "then by their average activity, then by when they were last active. In the most active list, "
                + "people with recorded activity across a fully measured period come first, then people with "
                + "recorded activity whose period was only partly measured, then people measured across the whole "
                + "period as doing nothing at all. Anyone whose activity could not be measured is left out of the "
                + "least active list rather than being assumed inactive.";

            public const string NobodyRankable =
                "Nobody could be ranked for this service and date range: there is no recorded activity, and no "
                + "complete measurement proving there was none.";

            /// <summary>A per-service coverage note, prefixed with the service's display name.</summary>
            public static string ForService(string workload, string message)
            {
                return WorkloadLabel(workload) + ": " + message;
            }
        }

        /// <summary>
        /// A Microsoft 365 service name as a reader would recognise it ("teams" -> "Teams"). The raw
        /// value is the backend workload key, which the SQL, the read model and the portal all match on.
        /// Mirrors <c>WORKLOADS</c> in the portal's <c>types/licenceActivity.ts</c>.
        /// </summary>
        public static string WorkloadLabel(string workload)
        {
            switch (workload)
            {
                case "teams": return "Teams";
                case "outlook": return "Outlook";
                case "onedrive": return "OneDrive";
                case "sharepoint": return "SharePoint";
                case "copilot": return "Copilot";
                default: return workload;
            }
        }

        /// <summary>
        /// The coverage/evidence status, spelled out for a business reader. Mirrors the portal's
        /// <c>components/licenceActivity/statuses.ts</c>; the raw value is a backend vocabulary word
        /// (<c>available | partial | missingCoverage | unmatchableIdentity | notImported | disabled</c>)
        /// that both the SQL and the read model compare on, so it is translated only for display.
        /// </summary>
        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "available": return "Available";
                case "partial": return "Partial";
                case "missingCoverage": return "Missing coverage";
                case "unmatchableIdentity": return "Identities could not be matched";
                case "notImported": return "Not imported";
                case "disabled": return "Import switched off";
                case "unknown": return "Unknown";
                default: return status;
            }
        }

        /// <summary>
        /// An activity level, spelled out for a business reader. Mirrors the portal's
        /// <c>components/licenceActivity/bands.ts</c>. The raw value is what <see cref="Band"/> returns.
        /// </summary>
        public static string BandLabel(string band)
        {
            switch (band)
            {
                case "high": return "High";
                case "moderate": return "Moderate";
                case "low": return "Low";
                case "zero": return "No activity";
                case "unknown": return "Unknown";
                default: return band;
            }
        }
    }
}
