using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Entities.AgentCosts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// The demo exists to be looked at in the portal, so "the rows are present" is not the property that
    /// matters - "the report can show them" is. This runs the Agent costs page's own store against a
    /// generated database and asserts every panel on it has something to draw.
    ///
    /// <para>It also pins the one thing the data cannot fix: the "Neither agent cost import is switched on"
    /// banner is read from the web application's ImportJobSettings, never from the rows, so a demo database
    /// full of billing data still shows it until those toggles are set.</para>
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public class DemoAgentCostReportTests
    {
        private static readonly DateTime AsOf = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void GeneratedDemo_FillsEveryPanelOfTheAgentCostsReport()
        {
            string name = "ContosoDemo_AgentCosts_" + Guid.NewGuid().ToString("N");
            // A full-length window on purpose: the demo's later agents only appear further back in the
            // history, and a 35-day database would leave most of the report's pivots single-valued.
            var options = DemoOptions.Parse(new[] { "--database", name, "--users", "40", "--days", "180",
                "--as-of", "2026-09-01", "--batch-size", "1000", "--no-profiles" }, DateTime.UtcNow);
            try
            {
                using (var database = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    database.Open(null);
                    var summary = DemoCommand.NewSummary(options);
                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                        new DemoGenerator(options).Generate(sink, summary, null);
                    database.ValidateAndComplete(summary, null);
                }

                var store = new SqlAgentCostReportStore(
                    new ConnectionStringAnalyticsDbContextFactory(SqlDemoDatabase.LocalConnection(name)));
                var query = new AgentCostQuery { FromUtc = options.Start, ToUtc = options.AsOf };

                var availability = store.GetAvailabilityAsync(true, true).GetAwaiter().GetResult();
                Assert.IsTrue(availability.HasCopilotStudioCreditData);
                Assert.IsTrue(availability.HasPerUserCreditData);
                Assert.IsTrue(availability.HasAzureCostData);
                Assert.IsTrue(availability.CopilotStudioCreditsHasRunCleanly);
                Assert.IsTrue(availability.AzureCostsHaveRunCleanly);
                Assert.IsNull(availability.CopilotStudioCreditsLastError);
                Assert.IsNull(availability.AzureCostsLastError);
                Assert.IsNull(availability.PerUserCreditsLastError);
                Assert.IsNull(availability.CapacityLastError);
                Assert.IsTrue(availability.EarliestUsageDate >= options.Start);
                Assert.IsTrue(availability.LatestUsageDate < options.AsOf);
                CollectionAssert.AreEquivalent(AzureCostDimensions.All.ToList(), availability.AzureDimensionsWithData,
                    "Every Azure pivot the page offers must have a value behind it, or its picker is a dead end.");
                Assert.IsFalse(availability.Messages.Any(m => m.Contains("Neither agent cost import is switched on")));

                // The banner in an empty-looking report comes from the web app's settings, not the database:
                // no amount of generated data removes it, which is why the generator prints the toggles.
                var switchedOff = store.GetAvailabilityAsync(false, false).GetAwaiter().GetResult();
                Assert.IsTrue(switchedOff.HasCopilotStudioCreditData);
                Assert.IsTrue(switchedOff.Messages.Any(m => m.Contains("Neither agent cost import is switched on")));

                var summaryPanel = store.GetSummaryAsync(query).GetAwaiter().GetResult();
                Assert.IsTrue(summaryPanel.BilledCredits > 0m);
                Assert.IsTrue(summaryPanel.NonBilledCredits > 0m, "The 'credits not charged' tile needs a figure.");
                Assert.IsTrue(summaryPanel.DistinctAgents >= 2);
                Assert.IsTrue(summaryPanel.DistinctEnvironments >= 2);
                Assert.IsTrue(summaryPanel.DaysWithUsage > 0);
                Assert.IsTrue(summaryPanel.PeakDistinctUsersOnASlice >= 1);
                Assert.IsTrue(summaryPanel.UnclassifiedHarnessCredits > 0m,
                    "One feature name is deliberately unrecognised so this figure is exercised, not always zero.");

                Assert.IsNotNull(summaryPanel.Capacity, "The capacity tile must have a snapshot to show.");
                Assert.IsTrue(summaryPanel.Capacity.Entitled > 0m);
                Assert.IsTrue(summaryPanel.Capacity.Consumed > 0m);
                Assert.AreEqual("MonthToDate", summaryPanel.Capacity.ConsumptionType);

                var azureTotals = summaryPanel.AzureCost.Single();
                Assert.AreEqual(DemoAgentCosts.Currency, azureTotals.Currency);
                Assert.IsTrue(azureTotals.Cost > 0m);
                Assert.IsTrue(azureTotals.IncludesEstimates, "The trailing days are an open billing period.");

                var trend = store.GetDailyTrendAsync(query).GetAwaiter().GetResult();
                Assert.IsTrue(trend.Count > 1, "The 'Credits per day' chart needs more than a single point.");
                Assert.IsTrue(trend.All(p => p.Date >= options.Start && p.Date < options.AsOf && p.BilledCredits > 0m));

                foreach (var dimension in AgentCostDimensions.All)
                {
                    var rows = store.GetBreakdownAsync(query, dimension, 20).GetAwaiter().GetResult();
                    Assert.IsTrue(rows.Count > 0, "Nothing to break down by " + dimension);
                    Assert.IsTrue(rows.Any(r => r.BilledCredits > 0m), "No credits under any " + dimension);
                }

                foreach (var dimension in AzureCostDimensions.All)
                {
                    var rows = store.GetAzureBreakdownAsync(query, dimension, 20).GetAwaiter().GetResult();
                    Assert.IsTrue(rows.Count > 0, "Nothing to break down Azure cost by " + dimension);
                    Assert.IsTrue(rows.All(r => r.Cost > 0m && r.Currency == DemoAgentCosts.Currency));
                }

                var detail = store.GetDetailAsync(query).GetAwaiter().GetResult();
                Assert.IsTrue(detail.TotalRows > 0);
                Assert.IsTrue(detail.Rows.Count > 0);
                Assert.IsTrue(detail.Rows.All(r => !string.IsNullOrWhiteSpace(r.AgentName)
                    && !string.IsNullOrWhiteSpace(r.EnvironmentName) && !string.IsNullOrWhiteSpace(r.Harness)));

                var filters = store.GetFilterOptionsAsync(query).GetAwaiter().GetResult();
                Assert.IsTrue(filters.Agents.Count > 1);
                Assert.IsTrue(filters.Environments.Count > 1);
                Assert.IsTrue(filters.Harnesses.Count > 1);
                Assert.IsTrue(filters.Features.Count > 1);
                Assert.IsTrue(filters.Models.Count > 0);
                Assert.IsTrue(filters.Tools.Count > 0);
                Assert.IsTrue(filters.KnowledgeSources.Count > 0);
                Assert.IsTrue(filters.Channels.Count > 0);
                CollectionAssert.IsSubsetOf(filters.Agents.Select(a => a.Id).ToList(),
                    DemoAgentCosts.Agents.Select(a => a.AgentId).ToList());
                Assert.IsTrue(filters.KnowledgeSources.Any(k => k.Contains("Καλημέρα")),
                    "A knowledge source is customer-named text and must reach the picker with its Unicode intact.");

                var users = store.GetTopUsersAsync(query, 20).GetAwaiter().GetResult();
                Assert.IsTrue(users.Count > 1, "The per-user panel must rank more than one person.");
                Assert.IsTrue(users.All(u => u.BilledCredits > 0m && u.ActiveDays > 0));
                Assert.IsTrue(users.Any(u => !string.IsNullOrWhiteSpace(u.UserPrincipalName)),
                    "Most billed people resolve to a user, so the panel must show names rather than raw ids.");
            }
            finally { Drop(name); }
        }

        private static void Drop(string name)
        {
            using (var master = new SqlConnection(SqlDemoDatabase.LocalConnection("master")))
            {
                master.Open();
                using (var command = master.CreateCommand())
                {
                    command.CommandText =
                        "IF DB_ID(@name) IS NOT NULL EXEC('ALTER DATABASE [' + @name + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [' + @name + ']');";
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
