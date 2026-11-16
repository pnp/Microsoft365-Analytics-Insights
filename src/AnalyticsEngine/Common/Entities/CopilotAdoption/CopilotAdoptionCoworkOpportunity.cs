using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>Separates prospective adoption from observed use without changing the policy audience.</summary>
    internal static class CopilotAdoptionCoworkOpportunity
    {
        internal static void Apply(CopilotAdoptionAnalysis analysis, CopilotAdoptionOptions options)
        {
            options = options ?? CopilotAdoptionOptions.Default;
            var summary = analysis.Summary;
            var signals = analysis.CoworkSignals ?? new List<CoworkReadinessSignalRow>();
            var rows = analysis.CoworkReadiness ?? new List<CoworkReadinessRow>();
            var audit = summary.DataSources?.AuditAvailable == true;
            var historyStart = summary.DataSources?.AuditHistoryStartUtc;
            var historyLimited = !historyStart.HasValue || historyStart.Value.Date > summary.FromUtc.Date;
            var capped = analysis.CoworkAssessmentCapped || signals.Count >= options.MaxCoworkUsersScored;
            var observed = new CoworkObservedUsage();

            if (audit && (signals.Count > 0 || analysis.CoworkAuditAssessed))
            {
                var users = signals.Where(s => s.CoworkInteractions > 0).ToList();
                // Incomplete evidence can prove positive observations, never a known zero.
                if (users.Count > 0 || (!summary.FiguresIncomplete && !historyLimited
                    && (signals.Count > 0 || !capped)))
                {
                    observed.Available = true;
                    observed.AssessedUsers = signals.Count;
                    observed.Users = users.Count;
                    observed.Interactions = users.Sum(s => s.CoworkInteractions);
                    observed.ActiveUserDays = users.Sum(s => (long)s.CoworkActiveDays);
                    observed.LastInteractionUtc = users.Select(s => s.LastCoworkInteractionUtc).Max();
                    observed.Partial = summary.FiguresIncomplete || historyLimited || capped;
                    var assessedIds = new HashSet<int>(users.Select(s => s.UserId));
                    var licensedIds = new HashSet<int>((analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
                        .Where(u => u.CoworkInteractions > 0).Select(u => u.UserId));
                    var shared = assessedIds.Count(licensedIds.Contains);
                    observed.AuditPopulationParts = new[]
                    {
                        shared, assessedIds.Count - shared, licensedIds.Count - shared,
                    };
                }
            }
            summary.CoworkObservedUsage = observed;

            var reason = !audit ? "auditMissing"
                : summary.FiguresIncomplete || summary.WarningDetails?.Any(d =>
                    d.Key == CopilotAdoptionWarningKeys.CoworkFluencyPartial) == true ? "incomplete"
                : !historyStart.HasValue ? "historyUnknown"
                : historyLimited ? "historyLimited"
                : !summary.CoworkReadinessAvailable ? "notAssessed"
                : summary.DataSources?.M365UsageReportsAvailable != true
                    || !summary.DataSources.M365UsageReportDate.HasValue
                    || summary.DataSources.M365UsageReportDate.Value.Date < summary.FromUtc.Date ? "workloadMissing"
                : null;

            summary.CoworkAdoptionUnavailableReason = reason;
            summary.CoworkNewAdoptionEstimate = null;
            summary.CoworkNonUserCeilingEstimate = null;
            if (reason != null) return;

            var nonUsers = rows.Where(r => !r.UsedCowork && r.CoworkInteractions == 0
                && r.AccountEnabled != false).ToList();
            var ready = nonUsers.Where(r => r.RecommendForPolicy
                && r.Tier == CopilotAdoptionScoring.CoworkTiers.PrimeCandidate).ToList();
            summary.CoworkNewAdoptionEstimate = CopilotAdoptionScoring.EstimateCoworkValue(
                ready, options, CoworkEstimateCohort.NewAdoption);
            summary.CoworkNonUserCeilingEstimate = CopilotAdoptionScoring.EstimateCoworkValue(
                nonUsers, options, CoworkEstimateCohort.NonUserCeiling);
            foreach (var estimate in new[] { summary.CoworkNewAdoptionEstimate, summary.CoworkNonUserCeilingEstimate })
            {
                estimate.Assumptions.Add($"Reporting window: {summary.FromUtc:yyyy-MM-dd} to {summary.ToUtc:yyyy-MM-dd}. "
                    + $"Drawn from {rows.Count:N0} assessed seat holders. Workload report date: "
                    + $"{summary.DataSources.M365UsageReportDate:yyyy-MM-dd}. Retained audit boundaries do not "
                    + "prove continuous import coverage.");
            }
        }
    }
}
