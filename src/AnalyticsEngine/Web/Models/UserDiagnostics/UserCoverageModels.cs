using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Web.AnalyticsWeb.Models.UserDataLookup;
using Wf = Web.AnalyticsWeb.Models.UserDataLookup.UserDataLookupRules.Wf;

namespace Web.AnalyticsWeb.Models.UserDiagnostics
{
    /// <summary>
    /// For every kind of data held about a person: the newest record stored for them, and what is - and is not - known
    /// about the import that feeds it.
    /// </summary>
    public class UserCoverageModel
    {
        [JsonProperty("categories")]
        public List<CoverageCategoryModel> Categories { get; set; } = new List<CoverageCategoryModel>();

        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; }
    }

    public class CoverageCategoryModel
    {
        /// <summary>The User lookup category key, or <see cref="UserCoverageRules.CopilotUsageReportCategory"/>.</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>The import workload flags that feed the category.</summary>
        [JsonProperty("workloads")]
        public List<string> Workloads { get; set; } = new List<string>();

        [JsonProperty("workloadsEnabled")]
        public bool WorkloadsEnabled { get; set; }

        /// <summary>The newest stored row for this person. Null means none is stored - not that they were inactive.</summary>
        [JsonProperty("latestUtc")]
        public DateTime? LatestUtc { get; set; }

        /// <summary>One of <see cref="CoverageLatestKinds"/>: what <see cref="LatestUtc"/> measures.</summary>
        [JsonProperty("latestKind")]
        public string LatestKind { get; set; }

        /// <summary>Usage reports only: the last-activity date Microsoft reported on the newest row.</summary>
        [JsonProperty("lastActivityDateUtc")]
        public DateTime? LastActivityDateUtc { get; set; }

        /// <summary>
        /// One of <see cref="Common.Entities.UserDiagnostics.StampStates"/> for the feeding import's completion stamp.
        /// Tenant-wide: a completed import does not prove this person was in it.
        /// </summary>
        [JsonProperty("sourceStampState")]
        public string SourceStampState { get; set; }

        [JsonProperty("sourceLastSuccessUtc")]
        public DateTime? SourceLastSuccessUtc { get; set; }

        /// <summary>One of <see cref="PerUserReplayKinds"/>: whether this source could ever be re-read for one person.</summary>
        [JsonProperty("perUserReplay")]
        public string PerUserReplay { get; set; }

        /// <summary>Set when this one row could not be read; the rest of the matrix is unaffected.</summary>
        [JsonProperty("error")]
        public bool Error { get; set; }
    }

    public static class CoverageLatestKinds
    {
        /// <summary>When the event itself happened (audit, mail, call, page view...).</summary>
        public const string EventTime = "eventTime";

        /// <summary>The usage-report day the row describes - not an activity date. See the last-activity date.</summary>
        public const string ReportDate = "reportDate";

        /// <summary>When the import observed a state (a membership or ownership), not when it began.</summary>
        public const string ObservedDate = "observedDate";
    }

    public static class PerUserReplayKinds
    {
        /// <summary>The source answers per person, so a targeted re-read is possible in principle.</summary>
        public const string PerUserPossible = "perUserPossible";

        /// <summary>The source is tenant-wide (a usage report, an audit feed, a crawl): only a whole-source re-read exists.</summary>
        public const string SourceWideOnly = "sourceWideOnly";

        /// <summary>Browser telemetry that was never collected cannot be collected later.</summary>
        public const string NotReconstructable = "notReconstructable";
    }

    /// <summary>How each category's freshness is evidenced. Pure, so the mapping is unit tested.</summary>
    public static class UserCoverageRules
    {
        /// <summary>Microsoft's per-user Copilot usage report: shown in coverage, not a User lookup count category.</summary>
        public const string CopilotUsageReportCategory = "copilot-usage-report";

        private sealed class Rule
        {
            public string LatestKind;
            public string StampKey;
            public string Replay;
            public string[] Workloads;
        }

        private static readonly Dictionary<string, Rule> Rules = BuildRules();

        private static Dictionary<string, Rule> BuildRules()
        {
            var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);

            Rule Audit(string workload) => new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { workload } };
            Rule Usage(string loader) => new Rule
            {
                LatestKind = CoverageLatestKinds.ReportDate,
                StampKey = ImportScheduleKeys.UsageReport(loader),
                Replay = PerUserReplayKinds.SourceWideOnly,
                Workloads = new[] { Wf.UsageReports },
            };

            rules[UserDataLookupRules.CatAuditEvents] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.AuditLog, Wf.Copilot } };
            rules[UserDataLookupRules.CatCopilot] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatAuditSharePoint] = Audit(Wf.AuditLog);
            rules[UserDataLookupRules.CatAuditExchange] = Audit(Wf.AuditLog);
            rules[UserDataLookupRules.CatAuditEntra] = Audit(Wf.AuditLog);
            rules[UserDataLookupRules.CatAuditGeneral] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatPowerAppEvents] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatFlowEvents] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatPowerBiEvents] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatCopilotStudioEvents] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatPowerAppShares] = Audit(Wf.Copilot);
            rules[UserDataLookupRules.CatFlowShares] = Audit(Wf.Copilot);

            rules[UserDataLookupRules.CatSentEmails] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.PerUserPossible, Workloads = new[] { Wf.SentEmails } };
            rules[UserDataLookupRules.CatWebHits] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.NotReconstructable, Workloads = new[] { Wf.WebTraffic } };
            rules[UserDataLookupRules.CatPageLikes] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.WebTraffic } };
            rules[UserDataLookupRules.CatPageComments] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.WebTraffic } };

            rules[UserDataLookupRules.CatTeamMemberships] = new Rule { LatestKind = CoverageLatestKinds.ObservedDate, StampKey = ImportScheduleKeys.GraphTeams, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.Teams } };
            rules[UserDataLookupRules.CatTeamOwnerships] = new Rule { LatestKind = CoverageLatestKinds.ObservedDate, StampKey = ImportScheduleKeys.GraphTeams, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.Teams } };
            rules[UserDataLookupRules.CatTeamsReactions] = new Rule { LatestKind = CoverageLatestKinds.EventTime, StampKey = ImportScheduleKeys.GraphTeams, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.Teams } };

            rules[UserDataLookupRules.CatCallsOrganised] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.Calls } };
            rules[UserDataLookupRules.CatCallSessions] = new Rule { LatestKind = CoverageLatestKinds.EventTime, Replay = PerUserReplayKinds.SourceWideOnly, Workloads = new[] { Wf.Calls } };

            rules[UserDataLookupRules.CatUsageOutlook] = Usage("OutlookUserActivityLoader");
            rules[UserDataLookupRules.CatUsageOneDrive] = Usage("OneDriveUserActivityLoader");
            rules[UserDataLookupRules.CatUsageSharePoint] = Usage("SharePointUserActivityLoader");
            rules[UserDataLookupRules.CatUsageYammer] = Usage("YammerUserUsageLoader");
            rules[UserDataLookupRules.CatUsageTeams] = Usage("TeamsUserUsageLoader");
            rules[UserDataLookupRules.CatUsageTeamsDevice] = Usage("TeamsUserDeviceLoader");
            rules[UserDataLookupRules.CatUsageAppPlatform] = Usage("AppPlatformUserActivityLoader");

            rules[CopilotUsageReportCategory] = new Rule
            {
                LatestKind = CoverageLatestKinds.ReportDate,
                StampKey = ImportScheduleKeys.GraphCopilotUsageReports,
                Replay = PerUserReplayKinds.SourceWideOnly,
                Workloads = new[] { Wf.CopilotUsageReports },
            };
            return rules;
        }

        /// <summary>
        /// Every category the coverage matrix shows, in order: each User lookup category that has dated rows (call
        /// feedback has no date of its own, so it has no "newest"), plus Microsoft's Copilot usage report.
        /// </summary>
        public static IReadOnlyList<string> CategoryKeys { get; } = UserDataLookupRules.Categories
            .Where(c => c.SupportsDetail)
            .Select(c => c.Key)
            .Concat(new[] { CopilotUsageReportCategory })
            .ToList();

        public static bool IsKnown(string key) => Rules.ContainsKey(key);

        public static string LatestKind(string key) => Rules.TryGetValue(key, out var rule) ? rule.LatestKind : CoverageLatestKinds.EventTime;

        /// <summary>The completion stamp for the import feeding <paramref name="key"/>, or null when it keeps none.</summary>
        public static string StampKey(string key) => Rules.TryGetValue(key, out var rule) ? rule.StampKey : null;

        public static string PerUserReplay(string key) => Rules.TryGetValue(key, out var rule) ? rule.Replay : PerUserReplayKinds.SourceWideOnly;

        public static IReadOnlyList<string> Workloads(string key) => Rules.TryGetValue(key, out var rule) ? rule.Workloads : new string[0];
    }
}
