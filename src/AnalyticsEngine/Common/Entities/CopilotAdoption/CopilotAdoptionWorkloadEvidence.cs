using Common.Entities.State;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Publication boundary for workload evidence. Positive retained observations survive, labelled partial;
    /// only verified whole-window outcomes support zero, weighted scores or prospective estimates.
    /// No missing component is reweighted into the remaining components.
    /// </summary>
    public static class CopilotAdoptionWorkloadEvidence
    {
        public static bool Complete(IReadOnlyCollection<UsageReportCoverage> coverage, string source)
            => coverage?.Any(c => c.Source == source && c.IsComplete) == true;

        public static bool Complete(IReadOnlyCollection<UsageReportCoverage> coverage)
            => UsageReportCoverageStore.Sources.All(source => Complete(coverage, source));

        private static long? Observed(long? value, bool complete) => complete || value > 0 ? value : null;

        public static LicenceOpportunityRow Apply(LicenceOpportunityRow row, IReadOnlyCollection<UsageReportCoverage> coverage)
        {
            var teams = Complete(coverage, "teams");
            var mail = Complete(coverage, "outlook");
            var files = Complete(coverage, "sharepoint") && Complete(coverage, "onedrive");
            row.TeamsMessages = Observed(row.TeamsMessages, teams);
            row.TeamsMeetings = Observed(row.TeamsMeetings, teams);
            row.EmailsSent = Observed(row.EmailsSent, mail);
            row.EmailsRead = Observed(row.EmailsRead, mail);
            row.FilesViewedOrEdited = Observed(row.FilesViewedOrEdited, files);
            if (!teams) row.CollaborationScore = null;
            if (!mail) row.EmailScore = null;
            if (!files) row.DocumentScore = null;
            if (teams && mail && files) return row;

            row.WorkloadUnavailableReason = "workloadCoverageUnverified";
            row.OpportunityScore = null;
            row.Recommended = row.QualificationTier == CopilotAdoptionScoring.OpportunityTiers.ProvenDemand;
            if (!row.Recommended)
            {
                row.QualificationTier = "workloadUnavailable";
                row.QualificationTierLabel = "Workload assessment unavailable";
            }
            row.Rationale = row.Recommended
                ? "Proven Copilot demand; workload coverage is unverified. Workload score and prospective hours are withheld."
                : "Workload coverage is unverified. No workload-fit classification or prospective hours are available.";
            return row;
        }

        public static CoworkReadinessRow Apply(CoworkReadinessRow row, IReadOnlyCollection<UsageReportCoverage> coverage)
        {
            var teams = Complete(coverage, "teams");
            var mail = Complete(coverage, "outlook");
            var files = Complete(coverage, "sharepoint") && Complete(coverage, "onedrive");
            row.TeamsMessages = Observed(row.TeamsMessages, teams);
            row.TeamsMeetings = Observed(row.TeamsMeetings, teams);
            row.EmailsSent = Observed(row.EmailsSent, mail);
            row.EmailsRead = Observed(row.EmailsRead, mail);
            row.FilesViewedOrEdited = Observed(row.FilesViewedOrEdited, files);
            if (!teams) { row.CollaborationScore = null; row.MeetingScore = null; }
            if (!mail) row.EmailScore = null;
            if (!files) row.DocumentScore = null;
            if (teams && mail && files) return row;

            row.WorkloadUnavailableReason = "workloadCoverageUnverified";
            row.CoordinationLoadScore = null;
            if (!row.UsedCowork)
            {
                row.Tier = "workloadUnavailable";
                row.TierLabel = "Workload assessment unavailable";
                row.Basis = "unavailable";
                row.RecommendForPolicy = false;
            }
            row.Rationale = row.UsedCowork
                ? "Observed Cowork use is retained; workload coverage is unverified. Coordination load and prospective hours are withheld."
                : "Workload coverage is unverified. No readiness classification or prospective hours are available.";
            return row;
        }
    }
}
