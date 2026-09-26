using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Report versions supported by the Graph Microsoft 365 Copilot usage reports.
    /// </summary>
    public static class CopilotReportVersions
    {
        /// <summary>The original report shape. This is what Graph returns when <c>version</c> is omitted.</summary>
        public const string V1 = "v1";

        /// <summary>
        /// Adds prompt counts, active usage days, and the Edge / Microsoft 365 Copilot / Copilot Chat
        /// (work) / Copilot Chat (web) / Copilot Agent columns.
        /// </summary>
        public const string V2 = "v2";
    }

    /// <summary>
    /// Names of the Graph Microsoft 365 Copilot usage report functions this importer understands.
    /// </summary>
    /// <remarks>
    /// Aliases <see cref="Common.Entities.Entities.UsageReports.CopilotUsageReportNames"/>, which is where
    /// the values live because they are persisted on the import log and read by the web app's Health page.
    /// </remarks>
    public static class CopilotReportNames
    {
        /// <summary>Tenant aggregate: enabled vs active users per app, rolled up over the requested period.</summary>
        public const string UserCountSummary = Common.Entities.Entities.UsageReports.CopilotUsageReportNames.UserCountSummary;

        /// <summary>Tenant aggregate: enabled vs active users per app, one row per calendar day.</summary>
        public const string UserCountTrend = Common.Entities.Entities.UsageReports.CopilotUsageReportNames.UserCountTrend;

        /// <summary>Per-user detail. Licensed users only, and affected by the tenant's concealed-user-information setting.</summary>
        public const string UsageUserDetail = Common.Entities.Entities.UsageReports.CopilotUsageReportNames.UsageUserDetail;

        /// <summary>Per-user Cowork task detail from the first-party Cowork usage report.</summary>
        public const string CoworkUsageUserDetail = Common.Entities.Entities.UsageReports.CopilotUsageReportNames.CoworkUsageUserDetail;
    }

    /// <summary>
    /// A request for one of the Graph Microsoft 365 Copilot usage reports this importer understands.
    ///
    /// Two things about these endpoints are easy to get wrong and silently produce a worse dataset rather
    /// than an error, so they are enforced here rather than left to each caller:
    ///
    /// 1. <c>version</c> is OPTIONAL and defaults to <see cref="CopilotReportVersions.V1"/>. Omit it and
    ///    every prompt-count and active-usage-days field is simply absent from the response - no error, no
    ///    warning, just missing data. We therefore always send it explicitly.
    /// 2. The valid <c>period</c> values differ by version: v1 accepts D30, v2 replaced it with <b>D28</b>.
    ///    Sending D30 with v2 (or D28 with v1) is rejected by Graph, so the pairing is validated up front.
    ///
    /// Also worth knowing when reading the data these produce: the reports cover <b>licensed users only</b>
    /// and are global-cloud only, and the data runs roughly 48 hours behind.
    /// </summary>
    public class CopilotReportRequest
    {
        /// <summary>The GA Microsoft 365 Copilot usage-report endpoint Microsoft now recommends.</summary>
        public const string GraphV10CopilotBaseUrl = "https://graph.microsoft.com/v1.0/copilot/reports";

        /// <summary>Preview Microsoft 365 Copilot usage-report endpoint used before the GA v1.0 CSV stream.</summary>
        public const string GraphBetaCopilotBaseUrl = "https://graph.microsoft.com/beta/copilot/reports";

        /// <summary>Superseded root-level beta reports endpoint, kept only as the final v1 fallback.</summary>
        public const string GraphBetaLegacyReportsBaseUrl = "https://graph.microsoft.com/beta/reports";

        /// <summary>Periods Graph accepts for report version 1. Note D30.</summary>
        public static readonly IReadOnlyList<string> V1Periods = new[] { "D7", "D30", "D90", "D180", "ALL" };

        /// <summary>Periods Graph accepts for report version 2. Note D28, NOT D30.</summary>
        public static readonly IReadOnlyList<string> V2Periods = new[] { "D7", "D28", "D90", "D180", "ALL" };

        /// <summary>
        /// The widest single (non-ALL) window, used for the first import so a new install starts with six
        /// months of history. Our audit-log pipeline has a hard 7-day retrieval ceiling, so this backfill is
        /// history we cannot obtain any other way.
        /// </summary>
        public const string MaxHistoryPeriod = "D180";

        /// <summary>
        /// The routine refresh window. D28 matches the "last 28 days" active-user figure the Microsoft 365
        /// admin centre shows, which is the number customers will compare us against.
        /// </summary>
        public const string DefaultRefreshPeriod = "D28";

        public CopilotReportRequest(string reportName, string period, string version = CopilotReportVersions.V2)
        {
            if (string.IsNullOrWhiteSpace(reportName)) throw new ArgumentException($"'{nameof(reportName)}' is required.", nameof(reportName));
            if (string.IsNullOrWhiteSpace(period)) throw new ArgumentException($"'{nameof(period)}' is required.", nameof(period));
            if (version != CopilotReportVersions.V1 && version != CopilotReportVersions.V2)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version,
                    $"Unsupported Copilot report version. Expected '{CopilotReportVersions.V1}' or '{CopilotReportVersions.V2}'.");
            }

            var allowed = version == CopilotReportVersions.V2 ? V2Periods : V1Periods;
            if (!allowed.Contains(period, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentOutOfRangeException(nameof(period), period,
                    $"Period '{period}' is not valid for Copilot report {version}. Valid values: {string.Join(", ", allowed)}. " +
                    (version == CopilotReportVersions.V2
                        ? "Note that v2 uses D28 where v1 used D30."
                        : "Note that v1 uses D30 where v2 uses D28."));
            }

            ReportName = reportName;
            Period = period.ToUpperInvariant();
            Version = version;
        }

        /// <summary>Graph function name, e.g. <see cref="CopilotReportNames.UsageUserDetail"/>.</summary>
        public string ReportName { get; }

        public string Period { get; }

        public string Version { get; }

        /// <summary>
        /// The requested window as a number of days, or null for <c>ALL</c> (which returns every supported
        /// window in one response, each row carrying its own period). Used to fill in the period when a row
        /// doesn't state one, since the period is part of the per-user table's key.
        /// </summary>
        public int? PeriodDays
        {
            get
            {
                if (Period.Length > 1 && Period[0] == 'D' && int.TryParse(Period.Substring(1), out var days))
                {
                    return days;
                }
                return null;
            }
        }

        /// <summary>Primary GA URL. v1.0 returns a CSV stream, so the importer parses CSV explicitly.</summary>
        public string Url => BuildUrl(GraphV10CopilotBaseUrl, Period, Version, "text/csv");

        /// <summary>GA /copilot fallback URL for version 1 when a tenant has not rolled out version 2 yet.</summary>
        public string V1FallbackUrl => BuildUrl(GraphV10CopilotBaseUrl, V1PeriodFor(Period), CopilotReportVersions.V1, "text/csv");

        /// <summary>
        /// Preview /copilot JSON fallback for tenants that have v2 data but have not rolled out the GA CSV
        /// stream yet. This is the endpoint the importer used before #541.
        /// </summary>
        public string BetaV2JsonFallbackUrl => BuildUrl(GraphBetaCopilotBaseUrl, Period, CopilotReportVersions.V2, "application/json");

        /// <summary>Legacy beta /reports JSON URL, kept as the last mid-rollout fallback.</summary>
        public string LegacyJsonFallbackUrl => $"{GraphBetaLegacyReportsBaseUrl}/{ReportName}(period='{V1PeriodFor(Period)}')?$format=application/json";

        private string BuildUrl(string baseUrl, string period, string version, string format)
        {
            return $"{baseUrl}/{ReportName}(period='{period}',version='{version}')?$format={format}";
        }

        internal static string V1PeriodFor(string period)
        {
            return string.Equals(period, "D28", StringComparison.OrdinalIgnoreCase) ? "D30" : period;
        }

        public override string ToString() => $"{ReportName}(period='{Period}',version='{Version}')";
    }
}
