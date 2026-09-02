using Common.Entities.Entities.UsageReports;
using DataUtils.Health;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// The pure decision logic behind the Health "Data overview" section: turning what
    /// <see cref="IHealthDataSource"/> managed to read (and what it didn't) into the section payload and
    /// its traffic-light. No SQL, no App Insights, no caching - so every partial-failure combination is
    /// unit testable. See issues #379 / #381.
    /// </summary>
    public static class HealthDataSectionRules
    {
        /// <summary>
        /// Builds the Data section from the raw figures. A hard <see cref="DatabaseCountsResult.DataError"/>
        /// suppresses the recent-volume figures (they were never read); everything else is kept even when
        /// part of the read failed, so one broken metric doesn't blank the rest of the section.
        /// </summary>
        public static DataOverviewSection BuildDataSection(DatabaseCountsResult counts, RecentVolumeResult hits, RecentVolumeResult audit)
        {
            var section = new DataOverviewSection();
            if (counts != null)
            {
                section.ActivityCount = counts.ActivityCount;
                section.HitCount = counts.HitCount;
                section.TeamsCount = counts.TeamsCount;
                section.SentEmailCount = counts.SentEmailCount;
                section.CallRecordCount = counts.CallRecordCount;
                section.CopilotChatCount = counts.CopilotChatCount;
                section.UserCount = counts.UserCount;
                section.DatabaseSizeMb = counts.DatabaseSizeMb;
                section.CountsError = counts.CountsError;
                section.TeamsBeingTrackedCount = counts.TeamsBeingTrackedCount;
                section.DataError = counts.DataError;

                if (counts.CopilotUsageReportImports != null)
                {
                    ApplyCopilotImports(section, counts.CopilotUsageReportImports);
                }
            }

            // Recent volume + freshness on the two biggest fact tables. Their timestamp columns are NOT
            // indexed (all indexes on hits/audit_events are FK indexes - see Create DB.sql), so these are
            // clustered-index scans that can time out on a huge tenant; when they do we keep the cheap
            // figures above and report RecentVolumeError. (DataError is only for a hard connection failure,
            // in which case the scans were never attempted.)
            if (string.IsNullOrEmpty(section.DataError))
            {
                if (hits != null && string.IsNullOrEmpty(hits.Error))
                {
                    section.HitsLast24h = hits.Last24h;
                    section.HitsLast7d = hits.Last7d;
                    section.NewestHitUtc = hits.Newest;
                }
                if (audit != null && string.IsNullOrEmpty(audit.Error))
                {
                    section.AuditEventsLast24h = audit.Last24h;
                    section.AuditEventsLast7d = audit.Last7d;
                    section.NewestAuditEventUtc = audit.Newest;
                }

                var volumeErrors = new[] { hits?.Error, audit?.Error }.Where(e => !string.IsNullOrEmpty(e)).Distinct().ToList();
                if (volumeErrors.Count > 0) section.RecentVolumeError = string.Join("; ", volumeErrors);
            }

            ComputeDataStatus(section);
            return section;
        }

        /// <summary>
        /// Folds the latest import of each Copilot usage report into the section: the newest import time,
        /// whether the tenant conceals user identities, and any per-report errors.
        /// </summary>
        private static void ApplyCopilotImports(DataOverviewSection section, IReadOnlyList<CopilotUsageReportImportRow> imports)
        {
            foreach (var import in imports.Where(i => i != null))
            {
                if (!section.CopilotUsageReportLastImportUtc.HasValue
                    || import.ImportedUtc > section.CopilotUsageReportLastImportUtc.Value)
                {
                    section.CopilotUsageReportLastImportUtc = import.ImportedUtc;
                }

                if (import.ReportName == CopilotUsageReportNames.UsageUserDetail && import.IsUpnObfuscated)
                {
                    section.CopilotUsageReportsIdentitiesConcealed = true;
                }

                if (!string.IsNullOrEmpty(import.Error))
                {
                    section.CopilotUsageReportErrors.Add($"{import.ReportName}: {import.Error}");
                }
            }
        }

        /// <summary>Sets the Data section's own traffic-light from what did and didn't load.</summary>
        public static void ComputeDataStatus(DataOverviewSection s)
        {
            if (!string.IsNullOrEmpty(s.DataError))
            {
                s.Status = HealthStatusNames.Unhealthy;
                s.Reasons = new List<string> { "Database query failed: " + s.DataError };
                return;
            }

            var reasons = new List<string>();
            if (!string.IsNullOrEmpty(s.CountsError)) reasons.Add("Approximate counts unavailable: " + s.CountsError);
            if (!string.IsNullOrEmpty(s.RecentVolumeError)) reasons.Add("Recent-volume scan didn't complete: " + s.RecentVolumeError);
            if (s.CopilotUsageReportsIdentitiesConcealed)
            {
                reasons.Add("Per-user Microsoft 365 Copilot usage from Graph is not being imported because this tenant conceals user identities in Microsoft 365 usage reports: "
                    + "Graph returns a hash instead of each user principal name, which cannot be linked to a user. "
                    + "Tenant-level Copilot user counts and the audit-log Copilot import are unaffected. "
                    + "To enable it, turn off 'Display concealed user, group and site names in all reports' in the Microsoft 365 admin centre (Settings > Org settings > Reports).");
            }
            foreach (var copilotError in s.CopilotUsageReportErrors)
            {
                reasons.Add("Graph Copilot usage report import failed - " + copilotError);
            }

            if (reasons.Count > 0)
            {
                s.Status = HealthStatusNames.Degraded;
                s.Reasons = reasons;
            }
            else
            {
                s.Status = HealthStatusNames.Healthy;
                s.Reasons = new List<string> { "All checks passing." };
            }
        }

        /// <summary>
        /// The Overview grid's Data row. It comes from the cheap reachability probe (the real counts load
        /// only when the Data tab is opened), so the Overview stays cheap on a big tenant.
        /// </summary>
        public static SectionStatus DataProbeStatus(string dataError)
        {
            return string.IsNullOrEmpty(dataError)
                ? new SectionStatus { Key = "data", Label = "Data overview", Status = HealthStatusNames.Healthy, Reasons = new List<string> { "Database reachable (open the Data tab for counts)." } }
                : new SectionStatus { Key = "data", Label = "Data overview", Status = HealthStatusNames.Unhealthy, Reasons = new List<string> { "Database query failed: " + dataError } };
        }

        /// <summary>Sets a section's own traffic-light from the pure, unit-tested <see cref="HealthRollup"/> so every section + the overall use one rule set.</summary>
        public static void SetStatusFromRollup(HealthSection section, HealthRollupInput input)
        {
            section.Status = HealthRollup.Evaluate(input, out var reasons).ToString();
            section.Reasons = reasons;
        }

        /// <summary>Bumps a section to at least Degraded (used when a section partially failed to load).</summary>
        public static void RaiseAtLeastDegraded(HealthSection section, string reason)
        {
            if (!string.Equals(section.Status, HealthStatusNames.Unhealthy, StringComparison.OrdinalIgnoreCase))
                section.Status = HealthStatusNames.Degraded;
            section.Reasons.RemoveAll(r => r == "All checks passing.");
            if (!section.Reasons.Contains(reason)) section.Reasons.Add(reason);
        }

        /// <summary>EF wraps SQL errors; the innermost message (the SqlException) is the useful one.</summary>
        public static string InnermostMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
        }
    }
}
