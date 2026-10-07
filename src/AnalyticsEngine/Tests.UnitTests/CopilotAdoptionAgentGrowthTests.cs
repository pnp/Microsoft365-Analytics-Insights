using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// Year-on-year agent growth over consecutive 28-day windows (#645): the window arithmetic, the rule
    /// that an unmeasured window is blank rather than zero, the separate autonomous-run evidence, the scope
    /// seam #639 plugs into, the shape of the SQL and the fixed Snapshot facts keys. Hand-built rows only;
    /// the SQL itself runs against a real database in <see cref="CopilotAdoptionAgentGrowthSqlTests"/>.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentGrowthTests
    {
        /// <summary>A Wednesday mid-morning, so the last settled day (three days earlier) is a Sunday.</summary>
        private static readonly DateTime Now = new DateTime(2026, 10, 7, 10, 30, 0, DateTimeKind.Utc);

        private static readonly DateTime LastSettled = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Long before the series, so every window is inside the audit history unless a test says otherwise.</summary>
        private static readonly DateTime HistoryStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        #region Windows

        [TestMethod]
        public void LastSettledDay_IsTodayLessTheUsageReportLag()
        {
            Assert.AreEqual(LastSettled, CopilotAdoptionAgentGrowth.LastSettledDay(Now, CopilotAdoptionOptions.Default),
                "The default lag is three days: today and the two days before it are still arriving.");

            var noLag = new CopilotAdoptionOptions { UsageReportLagDays = 0 };
            Assert.AreEqual(Now.Date, CopilotAdoptionAgentGrowth.LastSettledDay(Now, noLag));

            var negative = new CopilotAdoptionOptions { UsageReportLagDays = -5 };
            Assert.AreEqual(Now.Date, CopilotAdoptionAgentGrowth.LastSettledDay(Now, negative),
                "A negative lag must not push the series into the future.");

            Assert.AreEqual(DateTimeKind.Utc, CopilotAdoptionAgentGrowth.LastSettledDay(Now, null).Kind);
        }

        [TestMethod]
        public void Windows_AreClosedConsecutiveAndLeaveOutTheUnsettledDays()
        {
            var windows = CopilotAdoptionAgentGrowth.Windows(LastSettled);

            Assert.AreEqual(14, windows.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 14).ToList(), windows.Select(w => w.WindowsAgo).ToList(),
                "Most recent first.");

            Assert.AreEqual(LastSettled, windows[0].ToUtc, "Window 0 ends on the last settled day.");
            Assert.AreEqual(new DateTime(2026, 9, 7), windows[0].FromUtc);

            foreach (var window in windows)
            {
                Assert.AreEqual(27, (window.ToUtc - window.FromUtc).Days,
                    $"Window {window.WindowsAgo} must be 28 whole days, first and last inclusive.");
                Assert.IsNull(window.ActiveAgents, "A window with no figures yet is unmeasured, not zero.");
            }

            for (var n = 1; n < windows.Count; n++)
            {
                Assert.AreEqual(windows[n - 1].FromUtc.AddDays(-1), windows[n].ToUtc,
                    $"Window {n} must end the day before window {n - 1} starts: no gap and no overlap.");
            }

            var yearAgo = windows[13];
            Assert.AreEqual(364, (windows[0].FromUtc - yearAgo.FromUtc).Days,
                "Window 13 starts exactly 52 weeks earlier, so the comparison is like-for-like weekday for weekday.");
            Assert.AreEqual(windows[0].FromUtc.DayOfWeek, yearAgo.FromUtc.DayOfWeek);

            Assert.AreEqual(yearAgo.FromUtc, CopilotAdoptionAgentGrowth.SeriesFromUtc(LastSettled));
            Assert.AreEqual(LastSettled.AddDays(1), CopilotAdoptionAgentGrowth.SeriesToExclusiveUtc(LastSettled),
                "The series stops at the end of the last settled day...");
            Assert.IsTrue(CopilotAdoptionAgentGrowth.SeriesToExclusiveUtc(LastSettled) <= Now.Date.AddDays(-2),
                "...so the three unsettled days, today included, are never read.");
            Assert.AreEqual(14 * 28,
                (CopilotAdoptionAgentGrowth.SeriesToExclusiveUtc(LastSettled) - CopilotAdoptionAgentGrowth.SeriesFromUtc(LastSettled)).Days);
        }

        #endregion

        #region Measured or blank

        [TestMethod]
        public void Build_AnAgentUsedOnlyAYearAgo_CountsInWindow13AndNowhereElse()
        {
            var usage = Covered(window: 13, activeAgents: 1, users: 1, interactions: 4);

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            Assert.AreEqual(1, windows[13].ActiveAgents);
            Assert.AreEqual(1, windows[13].AgentUsers);
            Assert.AreEqual(4L, windows[13].AgentInteractions);
            Assert.AreEqual(4d, windows[13].InteractionsPerAgentUser);

            foreach (var window in windows.Where(w => w.WindowsAgo != 13))
            {
                Assert.AreEqual(0, window.ActiveAgents,
                    $"Window {window.WindowsAgo} is covered by the audit log and had no agent use: a measured zero.");
                Assert.AreEqual(0, window.AgentUsers);
                Assert.AreEqual(0L, window.AgentInteractions);
                Assert.IsNull(window.InteractionsPerAgentUser,
                    "Interactions per agent user is undefined with nobody to divide by - blank, not zero.");
            }

            Assert.AreEqual(1, CopilotAdoptionAgentGrowth.YearAgo(windows).ActiveAgents);
            Assert.AreEqual(0, CopilotAdoptionAgentGrowth.Latest(windows).ActiveAgents);
        }

        [TestMethod]
        public void Build_AWindowTheAuditLogHasNoDataFor_IsBlankNotZero()
        {
            var usage = Covered(window: 0, activeAgents: 3, users: 10, interactions: 25);
            usage.Single(r => r.WindowsAgo == 6).HasCopilotData = false;

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            Assert.IsNull(windows[6].ActiveAgents,
                "No Copilot interaction of any kind in the window means the import did not cover it: unknown, not none.");
            Assert.IsNull(windows[6].AgentUsers);
            Assert.IsNull(windows[6].AgentInteractions);
            Assert.IsNull(windows[6].InteractionsPerAgentUser);

            Assert.AreEqual(0, windows[5].ActiveAgents, "A covered window with no agent use is a real zero.");
            Assert.AreEqual(3, windows[0].ActiveAgents);
            Assert.AreEqual(2.5, windows[0].InteractionsPerAgentUser);
        }

        [TestMethod]
        public void Build_AuditHistoryStartingInsideAWindow_LeavesThatWindowAndEveryOlderOneBlank()
        {
            var windows = CopilotAdoptionAgentGrowth.Windows(LastSettled);
            var historyStart = windows[5].FromUtc.AddDays(10).AddHours(9);
            var usage = Covered(window: 0, activeAgents: 2, users: 2, interactions: 2, firstInteraction: historyStart);
            // The SQL reports data in window 5 - the interactions after the history began - but the window
            // as a whole was only partly covered, so its count is a floor and must not be read as a measure.
            usage.Single(r => r.WindowsAgo == 5).ActiveAgents = 1;

            var built = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            for (var n = 5; n <= 13; n++)
            {
                Assert.IsNull(built[n].ActiveAgents, $"Window {n} starts before the audit history does.");
            }

            for (var n = 0; n < 5; n++)
            {
                Assert.IsNotNull(built[n].ActiveAgents, $"Window {n} is wholly inside the audit history.");
            }

            var startsOnTheFirstDay = Covered(window: 0, activeAgents: 1, users: 1, interactions: 1,
                firstInteraction: windows[4].FromUtc.AddHours(23));
            Assert.IsNotNull(CopilotAdoptionAgentGrowth.Build(LastSettled, startsOnTheFirstDay, null)[4].ActiveAgents,
                "History that begins on the window's first day covers the window.");
        }

        [TestMethod]
        public void Build_WithNoAuditDataAtAll_LeavesEveryWindowBlank()
        {
            var usage = Covered(window: 0, activeAgents: 0, users: 0, interactions: 0);
            foreach (var row in usage)
            {
                row.HasCopilotData = false;
                row.FirstCopilotInteractionUtc = null;
            }

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            Assert.IsTrue(windows.All(w => w.ActiveAgents == null && w.AgentUsers == null && w.AgentInteractions == null));
            Assert.AreEqual(14, windows.Count, "The windows and their dates are still known.");
        }

        [TestMethod]
        public void Build_AFailedUsageQuery_LeavesEveryUserFigureBlankButKeepsTheBillingEvidence()
        {
            var billing = new List<AgentGrowthBillingRow> { new AgentGrowthBillingRow { WindowsAgo = 0, BilledAgents = 4 } };

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage: null, billing: billing);

            Assert.IsTrue(windows.All(w => w.ActiveAgents == null && w.AgentUsers == null
                && w.AgentInteractions == null && w.InteractionsPerAgentUser == null));
            Assert.AreEqual(4, windows[0].CopilotStudioBilledAgents,
                "The two series are independent: one failing must not blank the other.");
        }

        [TestMethod]
        public void InteractionsPerAgentUser_IsRoundedToOneDecimalAwayFromZero()
        {
            var usage = Covered(window: 0, activeAgents: 1, users: 4, interactions: 5);
            usage.Single(r => r.WindowsAgo == 1).AgentUsers = 3;
            usage.Single(r => r.WindowsAgo == 1).AgentInteractions = 10;
            usage.Single(r => r.WindowsAgo == 1).ActiveAgents = 1;

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            Assert.AreEqual(1.3, windows[0].InteractionsPerAgentUser, "1.25 rounds away from zero.");
            Assert.AreEqual(3.3, windows[1].InteractionsPerAgentUser);
        }

        #endregion

        #region Autonomous-run evidence

        [TestMethod]
        public void Build_AnAbsentAutonomousSource_IsBlankNotZero_AndNeverAddedToActiveAgents()
        {
            var usage = Covered(window: 0, activeAgents: 5, users: 9, interactions: 30);

            var unavailable = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);
            Assert.IsTrue(unavailable.All(w => w.CopilotStudioBilledAgents == null),
                "No billing data at all - the import is not configured, or the query failed - is unknown, not zero.");

            var billing = new List<AgentGrowthBillingRow>
            {
                new AgentGrowthBillingRow { WindowsAgo = 0, BilledAgents = 7 },
                // Rows for the window, none of them billed: the import ran and Microsoft charged nothing.
                new AgentGrowthBillingRow { WindowsAgo = 2, BilledAgents = 0 },
            };

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing);

            Assert.AreEqual(7, windows[0].CopilotStudioBilledAgents);
            Assert.AreEqual(5, windows[0].ActiveAgents,
                "Billed agents are separate evidence and must never be merged into the user-initiated count.");
            Assert.AreEqual(0, windows[2].CopilotStudioBilledAgents, "Billing rows with nothing billed are a measured zero.");
            Assert.IsNull(windows[1].CopilotStudioBilledAgents, "No billing rows for the window: blank.");
            Assert.IsNull(windows[13].CopilotStudioBilledAgents);
        }

        #endregion

        #region Scope

        [TestMethod]
        public void Scope_CountsEveryAgentUntilTheOriginClassifierLands()
        {
            Assert.AreEqual(AgentGrowthScopes.AllAgents, CopilotAdoptionAgentGrowth.Scope);

            var agents = new List<AgentGrowthAgentRow>
            {
                new AgentGrowthAgentRow { AgentId = 1, AgentKey = "CopilotStudio.Declarative.00000000-0000-0000-0000-000000000000", Name = "Contoso Expenses", IsCustomAgent = true },
                new AgentGrowthAgentRow { AgentId = 2, AgentKey = "Copilot.M365Copilot.CoworkChat", Name = "Copilot Cowork", IsCustomAgent = null },
                new AgentGrowthAgentRow { AgentId = 3, AgentKey = null, Name = "Καλημέρα κόσμε", IsCustomAgent = false },
            };

            Assert.IsTrue(agents.All(CopilotAdoptionAgentGrowth.Counts));
            Assert.AreEqual(0, CopilotAdoptionAgentGrowth.ExcludedAgentIds(agents).Count,
                "Nothing excluded means the windowing query reads every agent, with no scope table at all.");
            Assert.AreEqual(0, CopilotAdoptionAgentGrowth.ExcludedAgentIds(null).Count);

            Assert.AreEqual(AgentGrowthScopes.AllAgents, new AgentEstateSummary().GrowthScope,
                "The estate names the scope the series was counted under.");
            Assert.AreEqual("agents", CopilotAdoptionAgentGrowth.ScopeNoun(AgentGrowthScopes.AllAgents));
            Assert.AreEqual("customer-built agents", CopilotAdoptionAgentGrowth.ScopeNoun(AgentGrowthScopes.CustomerBuilt));
        }

        #endregion

        #region SQL shape

        [TestMethod]
        public void AgentGrowthSql_ReadsTheSeriesInOneSeekWithNoTwoDistinctCountsInAGrouping()
        {
            var sql = CopilotAdoptionSql.AgentGrowthSql(null);

            Assert.AreEqual(0, CountOccurrences(sql, "COUNT(DISTINCT"),
                "Two COUNT(DISTINCT ...) in one grouping spool the input and process each distinct separately - "
                + "the shape LicensedUsersQuery_NeverAsksForSeveralDistinctCountsInOneGrouping forbids. Collapse to a grain first.");
            Assert.AreEqual(0, CountOccurrences(sql, "dbo.audit_events"),
                "Copilot queries read the denormalised columns on copilot_chats, never audit_events.");

            // One range over the whole series, not one per window, filtered the way the inventory is.
            Assert.AreEqual(1, CountOccurrences(sql, "c.time_stamp >= @seriesFrom"));
            StringAssert.Contains(sql, "c.time_stamp < @seriesToExclusive");
            StringAssert.Contains(sql, "c.agent_id IS NOT NULL");
            StringAssert.Contains(sql, "c.user_id IS NOT NULL");
            StringAssert.Contains(sql, "DATEDIFF(DAY, c.time_stamp, @lastSettledDay) / 28");
            StringAssert.Contains(sql, "INTO #agent_growth_grain");
            StringAssert.Contains(sql, "DROP TABLE #agent_growth_grain;");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");

            // Every window gets a row, data or not, so the C# can tell an empty window from a missing one.
            StringAssert.Contains(sql, "(VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9), (10), (11), (12), (13)) AS w(n)");
            StringAssert.Contains(sql, "AS HasCopilotData");
            StringAssert.Contains(sql, "AS FirstCopilotInteractionUtc");

            Assert.IsFalse(sql.Contains("#agent_growth_excluded"),
                "With nothing excluded the query must be exactly the unscoped one: no scope table, no anti-join.");
        }

        [TestMethod]
        public void AgentGrowthSql_ExcludedAgents_GoThroughAKeyedTempTableInChunksOfAtMostAThousand()
        {
            var excluded = Enumerable.Range(1, 2500).Select(i => i * 3).ToList();
            excluded.Add(3); // a duplicate must not break the PRIMARY KEY

            var sql = CopilotAdoptionSql.AgentGrowthSql(excluded);

            StringAssert.Contains(sql, "CREATE TABLE #agent_growth_excluded (agent_id int NOT NULL PRIMARY KEY);");
            StringAssert.Contains(sql, "AND NOT EXISTS (SELECT 1 FROM #agent_growth_excluded AS x WHERE x.agent_id = c.agent_id)");
            StringAssert.Contains(sql, "DROP TABLE #agent_growth_excluded;");

            var inserts = Regex.Matches(sql, @"INSERT INTO #agent_growth_excluded \(agent_id\) VALUES ([^;]*);")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.Split(',').Length)
                .ToList();

            CollectionAssert.AreEqual(new List<int> { 1000, 1000, 500 }, inserts,
                "A VALUES list holds at most 1,000 rows, and each id is written once.");
            Assert.AreEqual(0, CountOccurrences(sql, " IN ("), "No IN-list, however large the agent table.");
        }

        [TestMethod]
        public void AgentGrowthBillingSql_IsGuardedSeparateAndCountsOneDistinctPerGrouping()
        {
            var sql = CopilotAdoptionSql.AgentGrowthBillingSql;

            StringAssert.Contains(sql, "IF OBJECT_ID(N'dbo.copilot_studio_credit_daily', N'U') IS NULL",
                "A database that predates the agent-cost migration must get an empty result, not an error.");
            StringAssert.Contains(sql, "WHERE 1 = 0");
            Assert.AreEqual(1, CountOccurrences(sql, "COUNT(DISTINCT"));
            StringAssert.Contains(sql, "d.billed_credits > 0");
            StringAssert.Contains(sql, "d.usage_date >= @seriesFrom");
            StringAssert.Contains(sql, "d.usage_date < @seriesToExclusive");
            Assert.IsFalse(sql.Contains("copilot_chats"), "The billing evidence is never joined into the user-initiated series.");
            Assert.IsFalse(sql.Contains("cowork_usage_user_activity_log"),
                "Nothing writes the Cowork usage-report table (#692), so nothing may read it.");
        }

        #endregion

        #region Snapshot facts keys

        [TestMethod]
        public void Summary_CarriesWindows0And13AsFixedTopLevelKeys_BlankWhenUnknown()
        {
            var empty = new CopilotAdoptionSummary();
            var keys = new[]
            {
                "agentGrowthSettledThroughUtc",
                "agentGrowthActiveAgentsWindow0", "agentGrowthActiveAgentsWindow13",
                "agentGrowthAgentUsersWindow0", "agentGrowthAgentUsersWindow13",
                "agentGrowthInteractionsWindow0", "agentGrowthInteractionsWindow13",
                "agentGrowthInteractionsPerAgentUserWindow0", "agentGrowthInteractionsPerAgentUserWindow13",
                "agentGrowthCopilotStudioBilledAgentsWindow0", "agentGrowthCopilotStudioBilledAgentsWindow13",
            };

            var emptyJson = JObject.FromObject(empty);
            foreach (var key in keys)
            {
                Assert.IsTrue(emptyJson.TryGetValue(key, out var value), $"{key} must be a top-level property.");
                Assert.AreEqual(JTokenType.Null, value.Type, $"{key} must be null - a blank cell - when the series was not computed, never 0.");
            }

            Assert.AreEqual(AgentGrowthScopes.AllAgents, (string)emptyJson["agentGrowthScope"]);

            var usage = Covered(window: 0, activeAgents: 12, users: 40, interactions: 100);
            var yearAgo = usage.Single(r => r.WindowsAgo == 13);
            yearAgo.ActiveAgents = 3;
            yearAgo.AgentUsers = 8;
            yearAgo.AgentInteractions = 20;

            var summary = new CopilotAdoptionSummary();
            summary.Agents.Growth = CopilotAdoptionAgentGrowth.Build(LastSettled, usage,
                new List<AgentGrowthBillingRow> { new AgentGrowthBillingRow { WindowsAgo = 0, BilledAgents = 2 } });

            Assert.AreEqual(LastSettled, summary.AgentGrowthSettledThroughUtc);
            Assert.AreEqual(12, summary.AgentGrowthActiveAgentsWindow0);
            Assert.AreEqual(3, summary.AgentGrowthActiveAgentsWindow13);
            Assert.AreEqual(40, summary.AgentGrowthAgentUsersWindow0);
            Assert.AreEqual(8, summary.AgentGrowthAgentUsersWindow13);
            Assert.AreEqual(100L, summary.AgentGrowthInteractionsWindow0);
            Assert.AreEqual(20L, summary.AgentGrowthInteractionsWindow13);
            Assert.AreEqual(2.5, summary.AgentGrowthInteractionsPerAgentUserWindow0);
            Assert.AreEqual(2.5, summary.AgentGrowthInteractionsPerAgentUserWindow13);
            Assert.AreEqual(2, summary.AgentGrowthCopilotStudioBilledAgentsWindow0);
            Assert.IsNull(summary.AgentGrowthCopilotStudioBilledAgentsWindow13, "No billing rows a year ago: blank.");

            // A filtered view carries the tenant's estate whole, and these keys must follow it.
            var narrowed = new CopilotAdoptionSummary { Agents = summary.Agents };
            Assert.AreEqual(12, narrowed.AgentGrowthActiveAgentsWindow0);
            Assert.AreEqual(12, summary.WithoutIndividualData().AgentGrowthActiveAgentsWindow0);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Fourteen query rows for windows the audit log fully covers, all with no agent use except
        /// <paramref name="window"/>.
        /// </summary>
        private static List<AgentGrowthQueryRow> Covered(
            int window, int activeAgents, int users, long interactions, DateTime? firstInteraction = null)
        {
            return Enumerable.Range(0, CopilotAdoptionAgentGrowth.WindowCount)
                .Select(n => new AgentGrowthQueryRow
                {
                    WindowsAgo = n,
                    ActiveAgents = n == window ? activeAgents : 0,
                    AgentUsers = n == window ? users : 0,
                    AgentInteractions = n == window ? interactions : 0,
                    HasCopilotData = true,
                    FirstCopilotInteractionUtc = firstInteraction ?? HistoryStart,
                })
                .ToList();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        #endregion
    }
}
