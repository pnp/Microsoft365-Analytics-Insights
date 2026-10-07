using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Stable keys for which agents the growth series counts, sent beside the figures so the portal and
    /// the workbook can each name the scope in their own words rather than in the server's.
    /// </summary>
    public static class AgentGrowthScopes
    {
        /// <summary>Every agent the audit log attributes an interaction to, Microsoft's own included.</summary>
        public const string AllAgents = "allAgents";

        /// <summary>
        /// Agents the organisation built - the Work Trend Index's scope (Agent Builder, the Agents Toolkit and
        /// SharePoint agents). Needs the agent-origin classifier from #639.
        /// </summary>
        public const string CustomerBuilt = "customerBuilt";
    }

    /// <summary>
    /// Year-on-year agent growth over consecutive 28-day windows (#645): the tenant's own version of the
    /// 2026 Work Trend Index's "monthly active agents" measure, compared with itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The windows.</b> <see cref="WindowCount"/> consecutive, closed windows of
    /// <see cref="WindowDays"/> whole UTC days, ending on the last settled day - the same
    /// <see cref="CopilotAdoptionOptions.UsageReportLagDays"/> margin the rest of the analysis uses for
    /// data Microsoft is still delivering, because the audit feed and the Copilot Studio billing both
    /// arrive late. Window 0 is the most recent; window 13 starts 364 days earlier, so the two cover the
    /// same weekdays a year apart. That pair is the year-on-year comparison.</para>
    /// <para><b>A window is measured or it is blank.</b> Its user-initiated figures are reported only when
    /// the Copilot audit log holds at least one interaction inside it and its history reaches back to the
    /// window's first day. Otherwise every figure is null - never zero - because a window the import never
    /// covered would otherwise read as "nobody used an agent", and in the year-ago window that is the
    /// denominator of a growth ratio.</para>
    /// <para><b>Autonomous runs are a separate series.</b> The audit log records user-initiated use only, so
    /// the evidence of autonomous runs comes from Copilot Studio billing and is never added to the
    /// user-initiated count. Cowork scheduled tasks would be the other evidence, but Microsoft reports them
    /// only in its Cowork usage report, which this product does not import (#692).</para>
    /// </remarks>
    public static class CopilotAdoptionAgentGrowth
    {
        /// <summary>Length of each window, in days. The Work Trend Index's 28-day period.</summary>
        public const int WindowDays = 28;

        /// <summary>How many windows the series holds: a year apart, plus the window being compared.</summary>
        public const int WindowCount = 14;

        /// <summary>The window that covers the same 28 days as window 0, one year (364 days) earlier.</summary>
        public const int YearAgoWindow = WindowCount - 1;

        /// <summary>
        /// Which agents the series counts - the ONE place that decides it, together with
        /// <see cref="Counts"/>. Every agent until the agent-origin classifier from #639 lands; then
        /// customer-built agents only, which is the Work Trend Index's scope.
        /// </summary>
        public static readonly string Scope = AgentGrowthScopes.AllAgents;

        /// <summary>
        /// Whether the series counts this agent. Applied in C# to the whole agent table before the windows
        /// are queried, and the agents it rejects are left out in SQL - see
        /// <see cref="CopilotAdoptionSql.AgentGrowthSql"/>.
        /// </summary>
        public static bool Counts(AgentGrowthAgentRow agent)
        {
            return agent != null;
        }

        /// <summary>The agents <see cref="Counts"/> rejects, or an empty list when it counts all of them.</summary>
        public static List<int> ExcludedAgentIds(IEnumerable<AgentGrowthAgentRow> agents)
        {
            return (agents ?? Enumerable.Empty<AgentGrowthAgentRow>())
                .Where(a => a != null && !Counts(a))
                .Select(a => a.AgentId)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        /// <summary>
        /// The last day whose data is treated as complete: today less
        /// <see cref="CopilotAdoptionOptions.UsageReportLagDays"/>. The analysis already uses this day as
        /// the latest settled usage-report snapshot; the growth series ends on it for the same reason.
        /// </summary>
        public static DateTime LastSettledDay(DateTime nowUtc, CopilotAdoptionOptions options)
        {
            var lagDays = (options ?? CopilotAdoptionOptions.Default).UsageReportLagDays;
            return DateTime.SpecifyKind(nowUtc.Date.AddDays(-Math.Max(0, lagDays)), DateTimeKind.Utc);
        }

        /// <summary>
        /// The windows ending on <paramref name="lastSettledDay"/>, most recent first, with no figures yet.
        /// Closed and consecutive: each window's last day is the day before the next one's first.
        /// </summary>
        public static List<AgentGrowthWindow> Windows(DateTime lastSettledDay)
        {
            var last = DateTime.SpecifyKind(lastSettledDay.Date, DateTimeKind.Utc);
            var windows = new List<AgentGrowthWindow>(WindowCount);

            for (var n = 0; n < WindowCount; n++)
            {
                var to = last.AddDays(-WindowDays * n);
                windows.Add(new AgentGrowthWindow
                {
                    WindowsAgo = n,
                    FromUtc = to.AddDays(-(WindowDays - 1)),
                    ToUtc = to,
                });
            }

            return windows;
        }

        /// <summary>The first instant the series reads: window 13's first day.</summary>
        public static DateTime SeriesFromUtc(DateTime lastSettledDay)
        {
            return DateTime.SpecifyKind(lastSettledDay.Date.AddDays(-(WindowDays * WindowCount - 1)), DateTimeKind.Utc);
        }

        /// <summary>The instant after the last one the series reads: the day after the last settled day.</summary>
        public static DateTime SeriesToExclusiveUtc(DateTime lastSettledDay)
        {
            return DateTime.SpecifyKind(lastSettledDay.Date.AddDays(1), DateTimeKind.Utc);
        }

        /// <summary>
        /// Puts the query results onto the windows. Pure, so the window rules can be tested from hand-built
        /// rows.
        /// </summary>
        /// <param name="lastSettledDay">The last day of window 0.</param>
        /// <param name="usage">
        /// One row per window from <see cref="CopilotAdoptionSql.AgentGrowthSql"/>, or null when that query
        /// did not run or failed - which leaves every user-initiated figure blank.
        /// </param>
        /// <param name="billing">
        /// Rows from <see cref="CopilotAdoptionSql.AgentGrowthBillingSql"/> for the windows the billing import
        /// holds any rows for, or null when that query did not run or failed. A window without a row is
        /// blank, not zero.
        /// </param>
        public static List<AgentGrowthWindow> Build(
            DateTime lastSettledDay,
            IEnumerable<AgentGrowthQueryRow> usage,
            IEnumerable<AgentGrowthBillingRow> billing)
        {
            var windows = Windows(lastSettledDay);

            var usageByWindow = (usage ?? Enumerable.Empty<AgentGrowthQueryRow>())
                .Where(r => r != null)
                .GroupBy(r => r.WindowsAgo)
                .ToDictionary(g => g.Key, g => g.First());
            var billingByWindow = (billing ?? Enumerable.Empty<AgentGrowthBillingRow>())
                .Where(r => r != null)
                .GroupBy(r => r.WindowsAgo)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var window in windows)
            {
                if (usageByWindow.TryGetValue(window.WindowsAgo, out var row) && IsMeasured(window, row))
                {
                    window.ActiveAgents = row.ActiveAgents;
                    window.AgentUsers = row.AgentUsers;
                    window.AgentInteractions = row.AgentInteractions;
                    window.InteractionsPerAgentUser = row.AgentUsers > 0
                        ? Math.Round(row.AgentInteractions / (double)row.AgentUsers, 1, MidpointRounding.AwayFromZero)
                        : (double?)null;
                }

                if (billingByWindow.TryGetValue(window.WindowsAgo, out var billed))
                {
                    window.CopilotStudioBilledAgents = billed.BilledAgents;
                }
            }

            return windows;
        }

        /// <summary>
        /// Whether the audit log covers the whole window: it holds an interaction inside the window, and
        /// its first interaction falls on or before the window's first day. A window the import only partly
        /// covered - the one it was switched on in, say - would otherwise be read as a real drop or rise.
        /// </summary>
        public static bool IsMeasured(AgentGrowthWindow window, AgentGrowthQueryRow row)
        {
            if (window == null || row == null || !row.HasCopilotData || !row.FirstCopilotInteractionUtc.HasValue)
            {
                return false;
            }

            return row.FirstCopilotInteractionUtc.Value.Date <= window.FromUtc.Date;
        }

        /// <summary>The window a year before the most recent one, or null when the series is empty.</summary>
        public static AgentGrowthWindow YearAgo(IEnumerable<AgentGrowthWindow> windows)
        {
            return windows?.FirstOrDefault(w => w != null && w.WindowsAgo == YearAgoWindow);
        }

        /// <summary>The most recent window, or null when the series is empty.</summary>
        public static AgentGrowthWindow Latest(IEnumerable<AgentGrowthWindow> windows)
        {
            return windows?.FirstOrDefault(w => w != null && w.WindowsAgo == 0);
        }

        /// <summary>The scope in words, for the English-only workbook. The portal words it from its catalog.</summary>
        public static string ScopeNoun(string scope)
        {
            return string.Equals(scope, AgentGrowthScopes.CustomerBuilt, StringComparison.Ordinal)
                ? "customer-built agents"
                : "agents";
        }
    }

    /// <summary>One agent, as the growth series' scope decision sees it. Every agent the audit log ever named.</summary>
    public class AgentGrowthAgentRow
    {
        public int AgentId { get; set; }

        /// <summary>The agent's identifier from the audit payload (<c>copilot_agents.agent_id</c>).</summary>
        public string AgentKey { get; set; }

        public string Name { get; set; }

        public bool? IsCustomAgent { get; set; }
    }

    /// <summary>One window's user-initiated agent use, as <see cref="CopilotAdoptionSql.AgentGrowthSql"/> returns it.</summary>
    public class AgentGrowthQueryRow
    {
        public int WindowsAgo { get; set; }

        public int ActiveAgents { get; set; }

        public int AgentUsers { get; set; }

        public long AgentInteractions { get; set; }

        /// <summary>True when the audit log holds any Copilot interaction in the window, agent or not.</summary>
        public bool HasCopilotData { get; set; }

        /// <summary>The first Copilot interaction in the whole audit log; the same on every row.</summary>
        public DateTime? FirstCopilotInteractionUtc { get; set; }
    }

    /// <summary>One window's Copilot Studio billed agents, as <see cref="CopilotAdoptionSql.AgentGrowthBillingSql"/> returns it.</summary>
    public class AgentGrowthBillingRow
    {
        public int WindowsAgo { get; set; }

        public int BilledAgents { get; set; }
    }
}
