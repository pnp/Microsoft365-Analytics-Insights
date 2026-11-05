extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.SpoWebActivity;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// A failed report query reaches every signed-in reader - on the page, in its CSV and in its workbook - as a stable
    /// reason and an error reference, never as the exception's text, which SqlClient fills with server, database, login
    /// and object names. The reference is what the server log carries.
    /// </summary>
    [TestClass]
    public class ReportQueryFailureTests
    {
        /// <summary>What SqlClient puts in a message, synthetic: the innermost message is the one the reports used to show.</summary>
        private const string Sensitive = "contoso-sql-01.database.windows.net";

        private static Exception TimedOut() =>
            new InvalidOperationException("An error occurred while executing the command definition.",
                new TimeoutException($"Execution Timeout Expired on server '{Sensitive}', database 'ContosoAnalytics'."));

        private static Exception LoginFailed() =>
            new InvalidOperationException("The underlying provider failed on Open.",
                new InvalidOperationException($"Login failed for user 'contoso-sql-admin' on server '{Sensitive}'."));

        [TestMethod]
        public void AFailure_IsAReasonAndAReference_NeverTheMessage()
        {
            var failure = ReportQueryFailure.From(TimedOut());

            Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, failure.Reason);
            Assert.IsTrue(Guid.TryParse(failure.Reference, out _));
            Assert.AreEqual($"the query timed out (error reference {failure.Reference})", failure.EnglishText);
            Assert.AreNotEqual(failure.Reference, ReportQueryFailure.From(TimedOut()).Reference, "Each failure has its own reference.");

            var logger = new CapturingLogger();
            failure.Log(logger, "Synthetic", "section-key", 42);
            var line = logger.Lines.Single().Message;
            StringAssert.Contains(line, "errorReference=" + failure.Reference);
            StringAssert.Contains(line, "reason=Timeout");
            Assert.IsFalse(line.Contains(Sensitive), line);
        }

        [TestMethod]
        public async Task AWebActivitySection_ThatFails_ShowsItsReaderAReasonAndAReference()
        {
            var logger = new CapturingLogger();
            var store = new SqlWebActivityStore(new global::UnitTests.FakeLoaderClasses.ThrowingAnalyticsDbContextFactory(LoginFailed), 30, logger);

            var pages = await store.GetPagesAsync(WebActivityQuery.Create(28, DateTime.UtcNow));

            Assert.IsTrue(pages.Queries.Count > 0);
            foreach (var query in pages.Queries)
            {
                Assert.IsFalse(string.IsNullOrEmpty(query.Error), "The section must still say it failed.");
                Assert.IsFalse(query.Error.Contains(Sensitive), query.Error);
                Assert.AreEqual(CopilotAdoptionFailureKinds.Other, query.ErrorReason);
                StringAssert.Contains(query.Error, query.ErrorReference);
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("errorReference=" + query.ErrorReference)),
                    "The reference the reader is shown must find the server's log line.");
            }
            Assert.IsFalse(JsonConvert.SerializeObject(pages).Contains(Sensitive));
        }

        [TestMethod]
        public async Task ATeamsExplorerSection_ThatFails_ShowsItsReaderAReasonAndAReference()
        {
            var logger = new CapturingLogger();
            var store = new SqlTeamsExplorerStore(new global::UnitTests.FakeLoaderClasses.ThrowingAnalyticsDbContextFactory(TimedOut), 30, logger);

            var adoption = await store.GetAdoptionAsync(TeamsExplorerQuery.Create(28, DateTime.UtcNow));

            Assert.IsTrue(adoption.Queries.Count > 0);
            foreach (var query in adoption.Queries)
            {
                Assert.IsFalse(string.IsNullOrEmpty(query.Error), "The section must still say it failed.");
                Assert.IsFalse(query.Error.Contains(Sensitive), query.Error);
                Assert.AreEqual(CopilotAdoptionFailureKinds.Timeout, query.ErrorReason);
                StringAssert.Contains(query.Error, "the query timed out");
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("key=" + query.Key + " ") && l.Message.Contains("errorReference=" + query.ErrorReference)));
            }
            Assert.IsFalse(JsonConvert.SerializeObject(adoption).Contains(Sensitive));
        }

        [TestMethod]
        public void AReportChart_ThatFails_ShowsItsReaderAReasonAndAReference()
        {
            var chart = new ReportChart { Key = "synthetic-chart", Title = "Synthetic" };

            ReportsAPIController.MarkQueryFailed(chart, LoginFailed());

            Assert.AreEqual(ReportsAPIController.QueryFailedErrorKey, chart.ErrorKey);
            Assert.AreEqual(CopilotAdoptionFailureKinds.Other, chart.ErrorReason);
            Assert.IsFalse(string.IsNullOrEmpty(chart.ErrorReference));
            Assert.AreEqual($"the query failed (error reference {chart.ErrorReference})", chart.Error);
            Assert.IsFalse(JsonConvert.SerializeObject(chart).Contains(Sensitive));
        }
    }
}
