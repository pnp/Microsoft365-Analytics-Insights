extern alias AnalyticsWeb;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
