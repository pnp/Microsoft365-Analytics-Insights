using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Common.Entities.CopilotAdoption
{
    public class CopilotAdoptionDigestMessage
    {
        [JsonProperty("subject")]
        public string Subject { get; set; }

        [JsonProperty("htmlBody")]
        public string HtmlBody { get; set; }
    }

    public static class CopilotAdoptionDigestRenderer
    {
        public const int DefaultActionCount = 3;

        public static CopilotAdoptionDigestMessage Render(CopilotAdoptionSummary summary, string portalBaseUrl, int actionCount = DefaultActionCount)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));

            var from = summary.FromUtc == default(DateTime) ? (DateTime?)null : summary.FromUtc.Date;
            var to = summary.ToUtc == default(DateTime) ? (DateTime?)null : summary.ToUtc.Date;
            var period = from.HasValue && to.HasValue
                ? $"{from.Value:yyyy-MM-dd} to {to.Value:yyyy-MM-dd}"
                : $"last {Math.Max(1, summary.WindowDays)} days";

            var subject = $"Copilot Adoption digest: {period}";
            var portalUrl = BuildPortalUrl(portalBaseUrl);
            var sb = new StringBuilder();
            sb.Append("<html><body>");
            sb.Append("<h1>Copilot Adoption digest</h1>");
            sb.Append("<p><strong>Period:</strong> ").Append(Html(period)).Append("</p>");
            sb.Append("<p><strong>Privacy:</strong> Aggregate-only digest. It intentionally contains no named people, no user principal names, no per-user rows and no attachments.</p>");
            sb.Append("<p><a href=\"").Append(HtmlAttr(portalUrl)).Append("\">Open the Copilot Adoption portal for detail</a></p>");

            sb.Append("<h2>Headlines</h2><table border=\"1\" cellpadding=\"4\" cellspacing=\"0\"><thead><tr><th>Metric</th><th>Current</th><th>Movement</th></tr></thead><tbody>");
            Row(sb, "Licensed users scored", summary.ScoredUsers.ToString("N0", CultureInfo.InvariantCulture), null);
            Row(sb, "Adoption rate", Pct(summary.AdoptionRatePct), Delta(summary, CopilotAdoptionTargetMetricCodes.AdoptionRatePct));
            Row(sb, "Habit rate", Pct(summary.HabitRatePct), Delta(summary, CopilotAdoptionTargetMetricCodes.HabitRatePct));
            Row(sb, "Reclaimable licences", Count(summary.ReclaimableSeats), Delta(summary, CopilotAdoptionTargetMetricCodes.ReclaimableSeats));
            Row(sb, "Never used", Count(summary.NeverUsedUsers), Delta(summary, CopilotAdoptionTargetMetricCodes.NeverUsedUsers));
            Row(sb, "Dormant", Count(summary.DormantUsers), Delta(summary, CopilotAdoptionTargetMetricCodes.DormantUsers));
            Row(sb, "Recommended for a licence", Count(summary.RecommendedForLicence), Delta(summary, CopilotAdoptionTargetMetricCodes.RecommendedForLicence));
            sb.Append("</tbody></table>");

            sb.Append("<h2>Targets</h2>");
            var targets = (summary.Targets ?? new List<CopilotAdoptionTarget>()).Where(t => t != null).Take(5).ToList();
            if (targets.Count == 0)
            {
                sb.Append("<p>No customer-defined targets are active for this period.</p>");
            }
            else
            {
                sb.Append("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\"><thead><tr><th>Target</th><th>Scope</th><th>Current</th><th>Goal</th><th>Progress</th></tr></thead><tbody>");
                foreach (var target in targets)
                {
                    Row(sb,
                        target.Label ?? target.Metric,
                        ScopeLabel(target),
                        target.Comparable && target.CurrentValue.HasValue ? FormatMetric(target.Metric, target.CurrentValue.Value) : "Not comparable under the current options",
                        FormatMetric(target.Metric, target.TargetValue),
                        target.ProgressPct.HasValue ? Pct(target.ProgressPct.Value) : "-");
                }
                sb.Append("</tbody></table>");
            }

            sb.Append("<h2>Cohort outcomes</h2>");
            sb.Append("<p>No intervention cohort outcome summary is available for this period. When cohort outcomes are published, this digest will include only aggregate counts.</p>");

            sb.Append("<h2>Top actions by size</h2>");
            var actions = (summary.ActionPlan ?? new List<AdoptionActionSummary>()).Where(a => a != null && a.Users > 0).OrderByDescending(a => a.Users).Take(Math.Max(1, actionCount)).ToList();
            if (actions.Count == 0)
            {
                sb.Append("<p>No recommended aggregate actions for this period.</p>");
            }
            else
            {
                sb.Append("<ol>");
                foreach (var action in actions)
                {
                    sb.Append("<li><strong>").Append(Html(action.Label)).Append(":</strong> ")
                        .Append(Html(Count(action.Users))).Append(" users (").Append(Html(Pct(action.SharePct))).Append("). ")
                        .Append(Html(action.Description)).Append("</li>");
                }
                sb.Append("</ol>");
            }

            var segments = (summary.AdoptionByDepartment ?? new List<AdoptionSegmentRow>()).Where(s => s != null).Take(3).ToList();
            if (segments.Count > 0)
            {
                sb.Append("<h2>Lowest-adoption departments above the minimum segment size</h2><ul>");
                foreach (var segment in segments)
                {
                    sb.Append("<li>").Append(Html(segment.Segment)).Append(": ")
                        .Append(Html(Pct(segment.AdoptionRatePct))).Append(" adoption across ")
                        .Append(Html(Count(segment.LicensedUsers))).Append(" licensed users</li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("<h2>Options and thresholds used</h2><p>Window: ").Append(Html(Math.Max(1, summary.WindowDays).ToString(CultureInfo.InvariantCulture)))
                .Append(" days; minimum seats per named segment: ").Append(Html((summary.Options?.MinSeatsPerSegment ?? CopilotAdoptionOptions.Default.MinSeatsPerSegment).ToString(CultureInfo.InvariantCulture)))
                .Append("; generated UTC: ").Append(Html(summary.GeneratedUtc == default(DateTime) ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) : summary.GeneratedUtc.ToString("O", CultureInfo.InvariantCulture)))
                .Append(".</p>");

            if (summary.Warnings != null && summary.Warnings.Count > 0)
            {
                sb.Append("<h2>Warnings</h2><ul>");
                foreach (var warning in summary.Warnings.Take(5)) sb.Append("<li>").Append(Html(warning)).Append("</li>");
                sb.Append("</ul>");
            }

            sb.Append("</body></html>");
            return new CopilotAdoptionDigestMessage { Subject = subject, HtmlBody = sb.ToString() };
        }

        private static string BuildPortalUrl(string baseUrl)
        {
            var root = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim();
            if (root.Length > 0 && !root.EndsWith("/", StringComparison.Ordinal)) root += "/";
            return root + "#/insights/copilot-adoption";
        }

        private static string Delta(CopilotAdoptionSummary summary, string metric)
        {
            var movement = summary.PeriodMovement;
            if (movement == null || !movement.Comparable || movement.Deltas == null) return "No comparable prior period";
            var d = movement.Deltas.FirstOrDefault(x => string.Equals(x.Metric, metric, StringComparison.Ordinal));
            if (d == null) return "No comparable prior period";
            var sign = d.Change > 0 ? "+" : string.Empty;
            return sign + FormatMetric(metric, d.Change) + " vs " + movement.ComparisonLabel;
        }

        private static string ScopeLabel(CopilotAdoptionTarget target)
        {
            if (target == null || string.Equals(target.ScopeType, "tenant", StringComparison.OrdinalIgnoreCase)) return "Tenant";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(target.ScopeType ?? "Scope") + ": " + (target.ScopeValue ?? string.Empty);
        }

        private static string FormatMetric(string metric, double value)
            => metric != null && metric.EndsWith("Pct", StringComparison.Ordinal) ? Pct(value) : Count(value);

        private static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string Pct(double value) => value.ToString("N1", CultureInfo.InvariantCulture) + "%";

        private static void Row(StringBuilder sb, params string[] cells)
        {
            sb.Append("<tr>");
            foreach (var cell in cells) sb.Append("<td>").Append(Html(cell ?? string.Empty)).Append("</td>");
            sb.Append("</tr>");
        }

        private static string Html(string text) => WebUtility.HtmlEncode(text ?? string.Empty);
        private static string HtmlAttr(string text) => WebUtility.HtmlEncode(text ?? string.Empty);
    }
}
