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
        public void CopilotQueries_PinMeasuredMergePlans()
        {
            var today = new DateTime(2026, 8, 14);
            var wideUsersSql = ReportsAPIController.BuildCopilotUsersQuery(
                today.AddDays(-90),
                today);

            StringAssert.Contains(wideUsersSql, "INNER MERGE JOIN dbo.audit_events");
            StringAssert.Contains(wideUsersSql, "OPTION (RECOMPILE)");
        }

        [TestMethod]
        public void CopilotJoinSelection_UsesMeasuredWindowAndFilterRules()
        {
            var today = new DateTime(2026, 8, 14);

            Assert.AreEqual(
                ReportsAPIController.CopilotAuditNaturalJoin,
                ReportsAPIController.SelectCopilotAuditJoin(
                    today.AddDays(-30),
                    hasAgentFilter: false,
                    today));
            StringAssert.Contains(
                ReportsAPIController.SelectCopilotAuditJoin(
                    today.AddDays(-90),
                    hasAgentFilter: false,
                    today),
                "INNER MERGE JOIN");
            Assert.AreEqual(
                ReportsAPIController.CopilotAuditNaturalJoin,
                ReportsAPIController.SelectCopilotAuditJoin(
                    today.AddDays(-180),
                    hasAgentFilter: true,
                    today));
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
