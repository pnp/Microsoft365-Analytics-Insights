using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// One of each shape the classifier from #639 resolves: customer-built by id, Microsoft's by id, of
        /// unknown origin, and the stored-flag cases - only a stored 1 settles an id the classifier cannot
        /// place, and a stored 0 is never evidence. Synthetic ids only.
        /// </summary>
        private static List<AgentGrowthAgentRow> ClassifierShapes()
        {
            return new List<AgentGrowthAgentRow>
            {
                new AgentGrowthAgentRow { AgentId = 1, AgentKey = "CopilotStudio.Declarative.00000000-0000-0000-0000-000000000001", IsCustomAgent = null },
                new AgentGrowthAgentRow { AgentId = 2, AgentKey = "CopilotStudio.CustomEngine.00000000-0000-0000-0000-000000000002", IsCustomAgent = false },
                new AgentGrowthAgentRow { AgentId = 3, AgentKey = "Copilot.M365Copilot.CoworkChat", IsCustomAgent = null },
                new AgentGrowthAgentRow { AgentId = 4, AgentKey = "SPO_00000000-0000-0000-0000-000000000004", IsCustomAgent = null },
                new AgentGrowthAgentRow { AgentId = 5, AgentKey = "BuiltIn_Contoso", IsCustomAgent = false },
                new AgentGrowthAgentRow { AgentId = 6, AgentKey = "T_00000000-0000-0000-0000-000000000006", IsCustomAgent = true },
            };
        }

        [TestMethod]
        public void Scope_CountsCustomerBuiltAgentsOnly_ThroughTheOriginClassifier()
        {
            Assert.AreEqual(AgentGrowthScopes.CustomerBuilt, CopilotAdoptionAgentGrowth.Scope,
                "The Work Trend Index counts customer-built agents only, and so does the series.");
            Assert.AreEqual(AgentGrowthScopes.CustomerBuilt, new AgentEstateSummary().GrowthScope,
                "The estate names the scope the series was counted under.");

            var agents = ClassifierShapes().ToDictionary(a => a.AgentId);
            var scope = CopilotAdoptionAgentGrowth.Scope;

            Assert.IsTrue(CopilotAdoptionAgentGrowth.Counts(agents[1], scope), "CopilotStudio.Declarative.* is customer-built.");
            Assert.IsTrue(CopilotAdoptionAgentGrowth.Counts(agents[2], scope),
                "CopilotStudio.CustomEngine.* is customer-built, and a stored 0 does not overrule the id.");
            Assert.IsFalse(CopilotAdoptionAgentGrowth.Counts(agents[3], scope), "Cowork is Microsoft's, so it is left out.");
            Assert.IsFalse(CopilotAdoptionAgentGrowth.Counts(agents[4], scope), "A SharePoint agent's origin is unknown, so it is left out.");
            Assert.IsFalse(CopilotAdoptionAgentGrowth.Counts(agents[5], scope), "An unplaceable id with a stored 0 stays unknown.");
            Assert.IsTrue(CopilotAdoptionAgentGrowth.Counts(agents[6], scope),
                "Only a stored is_custom_agent = 1 settles an id the classifier cannot place.");
            Assert.IsFalse(CopilotAdoptionAgentGrowth.Counts(null, scope));
        }

        [TestMethod]
        public void Exclusions_LeaveOutMicrosoftAndUnknownOriginAgents_AndSayWhichOriginIsUnknown()
        {
            var exclusions = CopilotAdoptionAgentGrowth.Exclusions(ClassifierShapes(), AgentGrowthScopes.CustomerBuilt);

            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, exclusions.Select(x => x.AgentId).ToArray(),
                "Every agent the scope does not count is excluded, in id order.");
            Assert.IsFalse(exclusions.Single(x => x.AgentId == 3).UnknownOrigin,
                "Cowork is out of scope because it is Microsoft's - not part of the gap.");
            Assert.IsTrue(exclusions.Single(x => x.AgentId == 4).UnknownOrigin,
                "An agent nobody can place is the gap, counted on its own.");
            Assert.IsTrue(exclusions.Single(x => x.AgentId == 5).UnknownOrigin);

            var duplicated = ClassifierShapes();
            duplicated.Add(new AgentGrowthAgentRow { AgentId = 3, AgentKey = "Copilot.M365Copilot.CoworkChat" });
            Assert.AreEqual(3, CopilotAdoptionAgentGrowth.Exclusions(duplicated, AgentGrowthScopes.CustomerBuilt).Count,
                "Each agent is excluded once, so the scope table's primary key holds.");

            Assert.AreEqual(0, CopilotAdoptionAgentGrowth.Exclusions(ClassifierShapes(), AgentGrowthScopes.AllAgents).Count,
                "Under the all-agents scope nothing is excluded.");
            Assert.AreEqual(0, CopilotAdoptionAgentGrowth.Exclusions(null, AgentGrowthScopes.CustomerBuilt).Count);
        }

        [TestMethod]
        public void Build_CarriesTheUnknownOriginCount_BlankWhenTheWindowWasNotMeasured()
        {
            var usage = Covered(window: 0, activeAgents: 4, users: 9, interactions: 30);
            usage.Single(r => r.WindowsAgo == 0).UnknownOriginAgents = 2;
            usage.Single(r => r.WindowsAgo == 13).UnknownOriginAgents = 1;
            usage.Single(r => r.WindowsAgo == 6).HasCopilotData = false;
            usage.Single(r => r.WindowsAgo == 6).UnknownOriginAgents = 5;

            var windows = CopilotAdoptionAgentGrowth.Build(LastSettled, usage, billing: null);

            Assert.AreEqual(4, windows[0].ActiveAgents, "Agents of unknown origin are never added to the active agents.");
            Assert.AreEqual(2, windows[0].UnknownOriginAgents);
            Assert.AreEqual(1, windows[13].UnknownOriginAgents);
            Assert.AreEqual(0, windows[1].UnknownOriginAgents, "A covered window with no such agent is a real zero.");
            Assert.IsNull(windows[6].UnknownOriginAgents, "An unmeasured window is blank throughout.");
        }

        #endregion

        #region SQL shape

        [TestMethod]
        public void AgentGrowthSql_ReadsTheSeriesInOneSeekWithNoTwoDistinctCountsInAGrouping()
        {
            var sql = CopilotAdoptionSql.AgentGrowthSql;

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
            StringAssert.Contains(sql, "AS UnknownOriginAgents");
        }

        [TestMethod]
        public void AgentGrowthSql_TakesTheScopeAsJsonParameters_IntoAKeyedTempTable_NeverAsLiterals()
        {
            var sql = CopilotAdoptionSql.AgentGrowthSql;

            StringAssert.Contains(sql, "CREATE TABLE #agent_growth_excluded (agent_id int NOT NULL PRIMARY KEY, unknown_origin bit NOT NULL);");
            StringAssert.Contains(sql, "SELECT CAST([value] AS int), 0 FROM OPENJSON(@agentGrowthExcludedAgents);");
            StringAssert.Contains(sql, "SELECT CAST([value] AS int), 1 FROM OPENJSON(@agentGrowthUnknownOriginAgents);");
            StringAssert.Contains(sql,
                "AND NOT EXISTS (SELECT 1 FROM #agent_growth_excluded AS x WHERE x.agent_id = c.agent_id AND x.unknown_origin = 0)",
                "An agent out of scope because it is Microsoft's never enters the grain...");
            StringAssert.Contains(sql, "SUM(CASE WHEN x.unknown_origin = 1 THEN 1 ELSE 0 END) AS UnknownOriginAgents",
                "...while one of unknown origin does, so it can be counted on its own...");
            StringAssert.Contains(sql, "WHERE NOT EXISTS (SELECT 1 FROM #agent_growth_excluded AS x WHERE x.agent_id = g.agent_id)",
                "...and is then kept out of the agent users and interactions.");
            StringAssert.Contains(sql, "DROP TABLE #agent_growth_excluded;");
            Assert.AreEqual(0, CountOccurrences(sql, " IN ("), "No IN-list, however large the agent table.");
            Assert.AreEqual(1, CountOccurrences(sql, "VALUES ("),
                "The window spine is the only VALUES list: no agent id is written into the statement, so its text and "
                + "cached plan never vary with the agent table.");
        }

        [TestMethod]
        public void AgentGrowthScopeParameters_SplitTheExclusionsIntoTwoJsonArrays_EachAgentOnce()
        {
            var excluded = Enumerable.Range(1, 2500)
                .Select(i => new AgentGrowthExclusion { AgentId = i * 3, UnknownOrigin = i % 2 == 0 })
                .ToList();
            excluded.Add(new AgentGrowthExclusion { AgentId = 3, UnknownOrigin = true }); // a duplicate must not break the PRIMARY KEY
            excluded.Add(null);

            var parameters = CopilotAdoptionSql.AgentGrowthScopeParameters(excluded);

            CollectionAssert.AreEqual(
                new[] { "@agentGrowthExcludedAgents", "@agentGrowthUnknownOriginAgents" },
                parameters.Select(p => p.ParameterName).ToArray());
            Assert.IsTrue(parameters.All(p => p.SqlDbType == System.Data.SqlDbType.NVarChar && p.Size == -1),
                "nvarchar(max), so the parameter declaration - and the plan cache entry - never varies with the list.");

            var leftOut = JArray.Parse((string)parameters[0].Value).Select(t => (int)t).ToList();
            var unknownOrigin = JArray.Parse((string)parameters[1].Value).Select(t => (int)t).ToList();

            Assert.AreEqual(1250, leftOut.Count);
            Assert.AreEqual(1250, unknownOrigin.Count);
            Assert.AreEqual(3, leftOut[0], "The first entry for an agent wins, and the duplicate is dropped.");
            Assert.IsFalse(unknownOrigin.Contains(3), "An agent is in one list only.");
            CollectionAssert.AreEqual(leftOut.OrderBy(i => i).ToList(), leftOut, "In id order.");
            Assert.AreEqual(6, unknownOrigin[0]);

            var none = CopilotAdoptionSql.AgentGrowthScopeParameters(null);
            Assert.AreEqual("[]", none[0].Value, "Nothing left out is an empty array, never NULL.");
            Assert.AreEqual("[]", none[1].Value);
            Assert.AreNotSame(none[0], CopilotAdoptionSql.AgentGrowthScopeParameters(null)[0],
                "A parameter belongs to one command, so every call makes new ones.");
        }

        [TestMethod]
        public void AgentGrowthForDisplay_DeclaresTheListsItRanWith_UntilTheyAreTooLongToCarry()
        {
            var parameters = new Dictionary<string, object> { { "@lastSettledDay", LastSettled } };
            var few = new[]
            {
                new AgentGrowthExclusion { AgentId = 3, UnknownOrigin = false },
                new AgentGrowthExclusion { AgentId = 4, UnknownOrigin = true },
                new AgentGrowthExclusion { AgentId = 5, UnknownOrigin = true },
            };

            var shown = CopilotAdoptionSql.AgentGrowthForDisplay(few, parameters);

            StringAssert.Contains(shown, "1 left out of every figure and 2 of unknown origin");
            StringAssert.Contains(shown, "DECLARE @agentGrowthExcludedAgents nvarchar(max) = N'[3]';");
            StringAssert.Contains(shown, "DECLARE @agentGrowthUnknownOriginAgents nvarchar(max) = N'[4,5]';");
            StringAssert.Contains(shown, "DECLARE @lastSettledDay datetime = '2026-10-04 00:00:00';");
            Assert.IsTrue(shown.EndsWith(CopilotAdoptionSql.AgentGrowthSql, StringComparison.Ordinal),
                "The popover shows the statement that ran, unchanged.");

            var many = Enumerable.Range(1, CopilotAdoptionSql.MaxDisplayedAgentGrowthExclusions + 1)
                .Select(i => new AgentGrowthExclusion { AgentId = i, UnknownOrigin = true })
                .ToList();

            var described = CopilotAdoptionSql.AgentGrowthForDisplay(many, parameters);

            StringAssert.Contains(described, "0 left out of every figure and 1,001 of unknown origin");
            StringAssert.Contains(described, "Their ids are not shown here; set @agentGrowthExcludedAgents and @agentGrowthUnknownOriginAgents");
            StringAssert.Contains(described, "DECLARE @agentGrowthUnknownOriginAgents nvarchar(max) = N'[]';");
            Assert.IsFalse(described.Contains("[1,2,3"), "A list too long to read is described, not carried.");
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
                "agentGrowthUnknownOriginAgentsWindow0", "agentGrowthUnknownOriginAgentsWindow13",
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

            Assert.AreEqual(AgentGrowthScopes.CustomerBuilt, (string)emptyJson["agentGrowthScope"]);

            var usage = Covered(window: 0, activeAgents: 12, users: 40, interactions: 100);
            usage.Single(r => r.WindowsAgo == 0).UnknownOriginAgents = 4;
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
            Assert.AreEqual(4, summary.AgentGrowthUnknownOriginAgentsWindow0);
            Assert.AreEqual(0, summary.AgentGrowthUnknownOriginAgentsWindow13);
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
