extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using ReportAreaData = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportAreaData;
using ReportChart = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportChart;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    [TestClass]
    public class ReportsAPIControllerTests
    {
        [TestMethod]
        public void FirstWorkloadTimeout_DoesNotAbandonTheRemainingWorkloads()
        {
            // The workloads run in a fixed order and the first one is not special. Bailing out on the
            // first timeout left rowsBySeries empty, so the chart errored anyway - defeating the whole
            // point of partial rendering. Keep going until something has actually been retrieved.
            var noRowsYet = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>();

            Assert.IsFalse(
                ReportsAPIController.ShouldStopAfterSeriesFailure(TimeoutException(), noRowsYet),
                "A timeout on the first workload must not stop the others - that would blank the chart.");

            // A workload that returned zero rows is not data to draw either.
            var emptySeries = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                new KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>(
                    "Teams", new List<ReportsAPIController.WeekValueRow>()),
            };
            Assert.IsFalse(
                ReportsAPIController.ShouldStopAfterSeriesFailure(TimeoutException(), emptySeries),
                "An empty series is nothing to render, so keep trying the remaining workloads.");
        }

        [TestMethod]
        public void TimeoutStopsRemainingWorkloads_OnceSomethingCanBeDrawn()
        {
            var week = new DateTime(2026, 8, 10);
            var populated = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                Series("Teams", week),
            };

            Assert.IsTrue(
                ReportsAPIController.ShouldStopAfterSeriesFailure(TimeoutException(), populated),
                "With data already retrieved, stop rather than waiting on more timeouts.");
        }

        [TestMethod]
        public void NonTimeoutFailure_NeverStopsTheRemainingWorkloads()
        {
            var week = new DateTime(2026, 8, 10);
            var populated = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                Series("Teams", week),
            };

            Assert.IsFalse(
                ReportsAPIController.ShouldStopAfterSeriesFailure(
                    new InvalidOperationException("bad column"), populated),
                "A workload-specific error says nothing about the other workloads.");
        }

        private static Exception TimeoutException()
        {
            return new Exception("outer", new TimeoutException("The wait operation timed out"));
        }

        [TestMethod]
        public void CompleteMultiTimeSeries_QueryFailureKeepsSuccessfulWorkloads()
        {
            var week = new DateTime(2026, 8, 10);
            var chart = Chart();
            var rows = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                Series("Teams", week),
            };

            var result = ReportsAPIController.CompleteMultiTimeSeries(
                chart,
                rows,
                new List<string> { "Outlook: query timed out" },
                new List<DateTime> { week });

            Assert.IsNull(result.Error);
            Assert.AreEqual(1, result.Series.Count);
            Assert.AreEqual("Teams", result.Series[0].Name);
            StringAssert.Contains(result.Warning, "Outlook: query timed out");
        }

        [TestMethod]
        public void CompleteMultiTimeSeries_EmptyWorkloadDoesNotBlockPopulatedWorkloads()
        {
            var week = new DateTime(2026, 8, 10);
            var chart = Chart();
            var rows = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                Series("Teams", week),
                new KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>(
                    "Viva Engage",
                    new List<ReportsAPIController.WeekValueRow>()),
            };

            var result = ReportsAPIController.CompleteMultiTimeSeries(
                chart,
                rows,
                new List<string>(),
                new List<DateTime> { week });

            Assert.IsNull(result.Error);
            Assert.AreEqual(1, result.Series.Count);
            Assert.AreEqual("Teams", result.Series[0].Name);
            StringAssert.Contains(result.Warning, "Viva Engage: no settled usage data");
        }

        [TestMethod]
        public void CompleteMultiTimeSeries_AllWorkloadsEmptyReturnsError()
        {
            var week = new DateTime(2026, 8, 10);
            var chart = Chart();
            var rows = new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>>
            {
                new KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>(
                    "Viva Engage",
                    new List<ReportsAPIController.WeekValueRow>()),
            };

            var result = ReportsAPIController.CompleteMultiTimeSeries(
                chart,
                rows,
                new List<string>(),
                new List<DateTime> { week });

            Assert.IsNotNull(result.Error);
            Assert.IsNull(result.Series);
        }

        [TestMethod]
        public void CompleteMultiTimeSeries_StaleWorkloadDoesNotTruncateTheOthers()
        {
            // A workload with history but no recent rows (e.g. its loader keeps failing) used to drag
            // the whole chart back to ITS last week, silently cutting months off every other workload
            // with no warning at all. It must now keep its own gap instead.
            var week1 = new DateTime(2026, 6, 1);
            var week2 = new DateTime(2026, 6, 8);
            var week3 = new DateTime(2026, 6, 15);
            var chart = Chart();

            var teams = new KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>(
                "Teams",
                new List<ReportsAPIController.WeekValueRow>
                {
                    new ReportsAPIController.WeekValueRow { WeekStart = week1, Value = 1 },
                    new ReportsAPIController.WeekValueRow { WeekStart = week2, Value = 2 },
                    new ReportsAPIController.WeekValueRow { WeekStart = week3, Value = 3 },
                });

            // Stale: stopped after the first week.
            var stale = Series("Viva Engage", week1);

            var result = ReportsAPIController.CompleteMultiTimeSeries(
                chart,
                new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>> { teams, stale },
                new List<string>(),
                new List<DateTime> { week1, week2, week3 });

            Assert.IsNull(result.Error);
            Assert.AreEqual(2, result.Series.Count);

            var teamsSeries = result.Series.Single(s => s.Name == "Teams");
            Assert.AreEqual(3, teamsSeries.Points.Count,
                "The healthy workload must keep every week; a stale workload must not truncate it.");
            Assert.AreEqual(3, teamsSeries.Points[2].Value,
                "The healthy workload's most recent week must still be charted.");

            var staleSeries = result.Series.Single(s => s.Name == "Viva Engage");
            Assert.AreEqual(3, staleSeries.Points.Count);
            Assert.IsNull(staleSeries.Points[2].Value,
                "The stale workload's missing weeks must be gaps, not zeroes.");

            StringAssert.Contains(result.Warning, "Viva Engage",
                "A workload that stopped producing data must be reported, not silently truncated.");
        }

        [TestMethod]
        public void CompleteMultiTimeSeries_TrimsTrailingWeeksNoWorkloadHasSettled()
        {
            // The trailing trim still has to happen: the newest week(s) commonly have no settled data
            // for anyone yet, and charting them would draw a false collapse to zero.
            var week1 = new DateTime(2026, 6, 1);
            var unsettled = new DateTime(2026, 6, 8);
            var chart = Chart();

            var result = ReportsAPIController.CompleteMultiTimeSeries(
                chart,
                new List<KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>> { Series("Teams", week1) },
                new List<string>(),
                new List<DateTime> { week1, unsettled });

            Assert.IsNull(result.Error);
            Assert.AreEqual(1, result.Series[0].Points.Count,
                "A trailing week no workload has settled data for must be trimmed off the spine.");
        }

        [TestMethod]
        public void CopilotQueries_NeverForceAJoinHint()
        {
            // Measured at 4m audit_events rows: a forced INNER MERGE JOIN degrades to a full scan
            // (a flat 73,857 logical reads) at every window from 30d up, versus 5,210-14,799 for the
            // natural join. Pin the natural join so a hint can't be reintroduced without re-measuring.
            var today = new DateTime(2026, 8, 14);
            var wideUsersSql = ReportsAPIController.BuildCopilotUsersQuery(
                today.AddDays(-90),
                today);

            StringAssert.Contains(wideUsersSql, "INNER JOIN dbo.audit_events");
            Assert.IsFalse(wideUsersSql.Contains("MERGE JOIN"),
                "A forced MERGE JOIN scans all of audit_events; the optimiser must be left to choose.");
            StringAssert.Contains(wideUsersSql, "OPTION (RECOMPILE)");
        }

        [TestMethod]
        public void CopilotJoinSelection_IsAlwaysTheNaturalJoin()
        {
            var today = new DateTime(2026, 8, 14);

            foreach (var days in new[] { 30, 45, 90, 180, 365 })
            {
                foreach (var hasAgentFilter in new[] { true, false })
                {
                    Assert.AreEqual(
                        ReportsAPIController.CopilotAuditNaturalJoin,
                        ReportsAPIController.SelectCopilotAuditJoin(
                            today.AddDays(-days),
                            hasAgentFilter,
                            today),
                        $"Window {days}d (agent filter: {hasAgentFilter}) must use the natural join.");
                }
            }
        }

        #region Copilot prompt insight charts (issue #312)

        [TestMethod]
        public void KeyPhrasesQuery_AggregatesAndCapsInSql()
        {
            // copilot_interaction_keywords is ~10 rows per scored prompt, so a million prompts is ~10M
            // rows. This MUST be a SQL-side TOP N aggregate - fetching rows to count them client-side
            // would pull the whole link table into the web app.
            var sql = ReportsAPIController.BuildCopilotKeyPhrasesQuery(top: 40);

            StringAssert.Contains(sql, "TOP 40");
            StringAssert.Contains(sql, "GROUP BY");
            StringAssert.Contains(sql, "COUNT_BIG(*)");
            StringAssert.Contains(sql, "dbo.copilot_interaction_keywords");
            StringAssert.Contains(sql, "dbo.keywords");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");

            // Bounded by the reporting window, not the whole table.
            StringAssert.Contains(sql, "i.created_utc >= @from");
        }

        [TestMethod]
        public void SentimentQuery_ExcludesUnscoredPrompts()
        {
            // An unscored prompt is not a neutral prompt. Counting NULL as 0 would drag the average
            // towards neutral in proportion to how much of the data was never scored.
            var sql = ReportsAPIController.BuildCopilotSentimentQuery();

            StringAssert.Contains(sql, "AVG(i.sentiment_score)");
            StringAssert.Contains(sql, "i.sentiment_score IS NOT NULL");
            StringAssert.Contains(sql, "i.created_utc >= @from");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");
        }

        [TestMethod]
        public void LanguagesQuery_IsBoundedAndIgnoresUndetected()
        {
            var sql = ReportsAPIController.BuildCopilotLanguagesQuery(top: 8);

            StringAssert.Contains(sql, "TOP 8");
            StringAssert.Contains(sql, "dbo.languages");
            StringAssert.Contains(sql, "i.language_id IS NOT NULL");
            StringAssert.Contains(sql, "i.created_utc >= @from");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");
        }

        [TestMethod]
        public void PromptInsightQueries_ReadInteractionHistoryNotTheAuditLog()
        {
            // These three are the ONLY Copilot charts sourced from the opt-in Graph interaction-history
            // import. The rest of the tab reads copilot_chats/audit_events. Mixing them up would silently
            // report on a different population.
            var queries = new[]
            {
                ReportsAPIController.BuildCopilotKeyPhrasesQuery(),
                ReportsAPIController.BuildCopilotSentimentQuery(),
                ReportsAPIController.BuildCopilotLanguagesQuery(),
            };

            foreach (var sql in queries)
            {
                StringAssert.Contains(sql, "dbo.copilot_interactions");
                Assert.IsFalse(sql.Contains("copilot_chats"),
                    "Prompt insight charts must not read the audit-log Copilot tables.");
                Assert.IsFalse(sql.Contains("audit_events"),
                    "Prompt insight charts must not read the audit-log Copilot tables.");
            }
        }

        [TestMethod]
        public void CognitiveConfigured_IsSentToTheUiAsAJsonFlag()
        {
            // Issue #312 requires the three prompt-insight charts to be hidden WITH AN EXPLANATION when
            // cognitive services are not configured. The controller omits the charts; this flag is what
            // lets the page say why instead of three panels silently disappearing. The JSON name is a
            // contract with ReportsPage.tsx, which reads data.cognitiveConfigured - renaming the property
            // without the attribute would break the explanation and leave the original silent behaviour.
            var json = JsonConvert.SerializeObject(new ReportAreaData
            {
                Area = "copilot",
                Months = 3,
                CognitiveConfigured = false,
            });

            StringAssert.Contains(json, "\"cognitiveConfigured\":false");

            var round = JsonConvert.DeserializeObject<ReportAreaData>(json);
            Assert.IsFalse(round.CognitiveConfigured);

            // It must serialise when true as well - the UI distinguishes "configured but no data yet"
            // (charts present, each carrying its own no-data warning) from "not configured at all".
            StringAssert.Contains(
                JsonConvert.SerializeObject(new ReportAreaData { Area = "copilot", CognitiveConfigured = true }),
                "\"cognitiveConfigured\":true");
        }

        #endregion

        [TestMethod]
        public void QueryTimeoutDetection_RecognizesNestedTimeout()
        {
            var error = new InvalidOperationException(
                "EF wrapper",
                new TimeoutException("Query timed out"));

            Assert.IsTrue(ReportsAPIController.IsQueryTimeout(error));
            Assert.IsFalse(ReportsAPIController.IsQueryTimeout(new InvalidOperationException("Other failure")));
        }

        private static ReportChart Chart() => new ReportChart
        {
            Description = "Distinct users active in each completed week.",
        };

        private static KeyValuePair<string, List<ReportsAPIController.WeekValueRow>> Series(
            string name,
            DateTime week) =>
            new KeyValuePair<string, List<ReportsAPIController.WeekValueRow>>(
                name,
                new List<ReportsAPIController.WeekValueRow>
                {
                    new ReportsAPIController.WeekValueRow { WeekStart = week, Value = 1 },
                });
    }
}
