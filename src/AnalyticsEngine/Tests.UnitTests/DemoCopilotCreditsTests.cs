using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// The synthetic Copilot credit balances must look like what the preview collector stores - balances that reset,
    /// an accounting period nobody verified, gaps that stay gaps - and must tell more than one investment story.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoCopilotCreditsTests
    {
        private static DemoOptions Options(params string[] extra) => DemoOptions.Parse(
            new[] { "--preview", "--users", "400", "--days", "62", "--as-of", "2026-09-01", "--areas", "copilot-credits" }
                .Concat(extra).ToArray(), DateTime.UtcNow);

        private static DemoGeneratorTests.RecordingSink Generate(DemoOptions options, out DemoSummary summary)
        {
            var recording = new DemoGeneratorTests.RecordingSink(t => t.Name.StartsWith("copilot_cost_", StringComparison.Ordinal));
            summary = DemoCommand.NewSummary(options);
            using (var sink = new CountingDemoSink(summary, recording)) new DemoGenerator(options).Generate(sink, summary, null);
            return recording;
        }

        private static int Col(DemoTable table, string column) => table.Columns.ToList().FindIndex(c => c.Name == column);

        private static object V(object[] row, DemoTable table, string column) => row[Col(table, column)];

        [TestMethod]
        public void SameOptions_GenerateIdenticalRows_AndThePreviewCountsWhatSqlWouldReceive()
        {
            var first = Generate(Options(), out var summary);
            var second = Generate(Options(), out _);
            Assert.AreEqual(JsonConvert.SerializeObject(first.Rows), JsonConvert.SerializeObject(second.Rows));
            foreach (var table in first.Rows)
                Assert.AreEqual(table.Value.Count, summary.Rows[table.Key], table.Key);
            Assert.AreEqual(62, summary.CreditHistoryDays);
            Assert.AreNotEqual(JsonConvert.SerializeObject(first.Rows), JsonConvert.SerializeObject(Generate(Options("--seed", "7"), out _).Rows));
        }

        [TestMethod]
        public void Balances_KeepTheCollectorsShape_AndResetAtTheStartOfEachMonth()
        {
            var sink = Generate(Options(), out _);
            var observations = sink.For(DemoTables.CostObservations);
            var services = sink.For(DemoTables.CostServices);
            Assert.IsTrue(observations.Count > 0);
            Assert.IsTrue(observations.All(r => (string)V(r, DemoTables.CostObservations, "period_status") == "unverified"),
                "The live collector cannot verify the accounting period, so neither may the demo.");
            Assert.AreEqual(observations.Count, observations.Select(r => (DateTime)r[0] + "|" + (Guid)r[1]).Distinct().Count());

            // Month-to-date totals of complete readings: never down within a month, zero on the first.
            var totals = services.Where(r => (string)V(r, DemoTables.CostServices, "unit") == DemoCopilotCredits.CreditUnit)
                .GroupBy(r => new { Date = (DateTime)r[0], Object = (Guid)r[1] })
                .ToDictionary(g => g.Key, g => new { Total = g.Sum(r => (double)V(r, DemoTables.CostServices, "consumed_quantity")), Count = g.Count() });
            var complete = observations.Where(r => (byte)V(r, DemoTables.CostObservations, "quality") == 2)
                .Select(r => new { Date = (DateTime)r[0], Object = (Guid)r[1] }).ToList();
            var firstOfMonth = complete.Where(o => o.Date.Day == 1).ToList();
            Assert.IsTrue(firstOfMonth.Count > 0);
            Assert.IsTrue(firstOfMonth.All(o => !totals.ContainsKey(o) || totals[o].Total == 0),
                "On the first of the month the new period has nothing in it yet.");
            var lastOfJuly = complete.Where(o => o.Date == new DateTime(2026, 7, 31)).ToList();
            Assert.IsTrue(lastOfJuly.Any(o => totals.ContainsKey(o) && totals[o].Total > 0), "The reset must be visible: July ended with spend.");

            var decreases = complete.GroupBy(o => o.Object).Sum(person =>
            {
                var ordered = person.OrderBy(o => o.Date).ToList();
                var count = 0;
                for (int i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];
                    if (previous.Date.Month != current.Date.Month || !totals.ContainsKey(previous) || !totals.ContainsKey(current)) continue;
                    if (totals[previous].Count != totals[current].Count) continue;
                    if (totals[current].Total + 1e-6 < totals[previous].Total) count++;
                }
                return count;
            });
            Assert.AreEqual(0, decreases, "A month-to-date balance cannot go down within the month.");
        }

        [TestMethod]
        public void Sweeps_HaveGapsThrottlingPartialsAndSameDayCorrections_LikeARealCollector()
        {
            var sink = Generate(Options(), out _);
            var runs = sink.For(DemoTables.CostRuns);
            var dates = runs.Select(r => (DateTime)V(r, DemoTables.CostRuns, "inventory_date")).ToList();
            Assert.IsTrue(dates.Count < 62, "At least one day has no sweep at all - absent, not zero.");
            Assert.AreEqual(new DateTime(2026, 8, 31), dates.Max(), "The latest sweep is the day before the as-of date.");
            Assert.IsNotNull(V(runs.Single(r => (DateTime)V(r, DemoTables.CostRuns, "inventory_date") == dates.Max()), DemoTables.CostRuns, "last_completed"),
                "The latest sweep completed, so the headline snapshot is current.");
            Assert.IsTrue(runs.Any(r => V(r, DemoTables.CostRuns, "last_completed") == null), "Some sweeps never completed.");
            Assert.IsTrue(sink.For(DemoTables.CostWork).Any(r => (string)V(r, DemoTables.CostWork, "outcome") == "throttled"));
            Assert.IsTrue(sink.For(DemoTables.CostWork).Any(r => (string)V(r, DemoTables.CostWork, "outcome") == "partial"));

            var observations = sink.For(DemoTables.CostObservations);
            var observedDates = new HashSet<DateTime>(observations.Select(r => (DateTime)r[0]));
            Assert.IsTrue(observedDates.SetEquals(dates), "Every sweep observed people; no observation without a sweep.");
            Assert.IsTrue(observations.Any(r => (byte)V(r, DemoTables.CostObservations, "quality") == 1), "Some readings stay partial.");
            Assert.IsTrue(sink.For(DemoTables.CostUnevaluated).Any(r => (string)V(r, DemoTables.CostUnevaluated, "reason") == "upstreamFailure"));

            // A corrected reading: two archived publications for one person-day, the current row complete.
            var history = sink.For(DemoTables.CostHistory);
            Assert.AreEqual(history.Count, history.Select(r => (Guid)V(r, DemoTables.CostHistory, "publication_id")).Distinct().Count());
            var corrected = history.GroupBy(r => (DateTime)V(r, DemoTables.CostHistory, "observation_date") + "|" + (Guid)V(r, DemoTables.CostHistory, "object_id"))
                .Where(g => g.Count() == 2).ToList();
            Assert.IsTrue(corrected.Count > 0);
            foreach (var pair in corrected)
                CollectionAssert.AreEquivalent(new byte[] { 1, 2 }, pair.Select(r => (byte)V(r, DemoTables.CostHistory, "quality")).ToArray());
            Assert.IsTrue(history.Count > observations.Count);
        }

        [TestMethod]
        public void Spend_IsDrivenByActivity_WithCounterExamplesWhereMoreSpendIsNotMoreEvidence()
        {
            var options = Options();
            var sink = Generate(options, out _);
            var population = new DemoPopulation(options);
            var credits = new DemoCopilotCredits(options, new DemoGeneratorTests.RecordingSink(_ => false));
            var users = Enumerable.Range(1, options.Users).Select(population.User).ToList();
            var byObject = users.ToDictionary(u => Guid.Parse(DemoPopulation.AzureAdObjectId(options.Seed, u.Id)));
            var latest = sink.For(DemoTables.CostServices).Where(r => (DateTime)r[0] == new DateTime(2026, 8, 31)).ToList();

            double Credits(DemoUser user) => latest.Where(r => (Guid)r[1] == Guid.Parse(DemoPopulation.AzureAdObjectId(options.Seed, user.Id))
                && (string)V(r, DemoTables.CostServices, "unit") == DemoCopilotCredits.CreditUnit).Sum(r => (double)V(r, DemoTables.CostServices, "consumed_quantity"));

            var automation = users.Where(u => credits.ProfileFor(u) == DemoCopilotCredits.Profile.AutomationHeavy).ToList();
            var power = users.Where(u => credits.ProfileFor(u) == DemoCopilotCredits.Profile.AgentPowerUser).ToList();
            var metered = users.Where(u => credits.ProfileFor(u) == DemoCopilotCredits.Profile.MeteredHeavy).ToList();
            Assert.IsTrue(automation.Count > 0 && power.Count > 0 && metered.Count > 0, "Every story needs at least one person.");
            Assert.IsTrue(automation.All(u => u.CopilotLicensed && u.Persona.ExpectedBand <= Common.Entities.CopilotAdoption.AdoptionBand.Developing),
                "The heavy-automation story is a lightly-evidenced seat holder.");
            var standardSpenders = users.Where(u => u.CopilotLicensed && credits.ProfileFor(u) == DemoCopilotCredits.Profile.Standard)
                .Select(Credits).Where(c => c > 0).ToList();
            Assert.IsTrue(automation.Average(Credits) > 5 * standardSpenders.Average(),
                "Scheduled automation spends far more than an ordinary seat holder despite thinner evidence.");
            Assert.IsTrue(metered.All(u => !u.CopilotLicensed));

            var reasons = latest.Select(r => (string)V(r, DemoTables.CostServices, "decision_reason")).Distinct().ToList();
            CollectionAssert.IsSubsetOf(new[] { "allowed", "limitExhausted" }, reasons);
            Assert.IsTrue(sink.For(DemoTables.CostServices).Any(r => (string)V(r, DemoTables.CostServices, "decision_reason") == "notEntitled"
                && !byObject[(Guid)r[1]].Profile.AccountEnabled), "Disabled accounts are refused, not charged.");
            Assert.IsTrue(latest.Any(r => (string)V(r, DemoTables.CostServices, "unit") == DemoCopilotCredits.FutureUnit),
                "A service in a unit this build does not know must stay distinguishable from credits.");
            Assert.IsTrue(latest.Any(r => V(r, DemoTables.CostServices, "remaining") == null), "Some capacity is unknown, not zero.");
        }

        [TestMethod]
        public void UnlinkedAccountsAndPolicyContext_AreTenantFactsWithUnicodeNames()
        {
            var options = Options();
            var sink = Generate(options, out _);
            var userObjects = new HashSet<Guid>(Enumerable.Range(1, options.Users).Select(id => Guid.Parse(DemoPopulation.AzureAdObjectId(options.Seed, id))));
            var unlinked = sink.For(DemoTables.CostObservations).Where(r => V(r, DemoTables.CostObservations, "user_id") == null).ToList();
            Assert.IsTrue(unlinked.Count > 0);
            Assert.IsTrue(unlinked.All(r => !userObjects.Contains((Guid)r[1])));
            Assert.IsTrue(sink.For(DemoTables.CostUnevaluated).Any(r => (string)V(r, DemoTables.CostUnevaluated, "reason") == "noApplicablePolicy"));

            var context = sink.For(DemoTables.CostContext);
            CollectionAssert.AreEquivalent(new[] { "groups", "policies", "services", "tenant" },
                context.Select(r => (string)V(r, DemoTables.CostContext, "kind")).Distinct().ToArray());
            Assert.IsTrue(context.Any(r => ((string)V(r, DemoTables.CostContext, "document")).Contains("\u0395\u03bb\u03bb\u03ac\u03b4\u03b1")),
                "A policy name is customer text and must carry non-Latin characters.");
            Assert.AreEqual(1, sink.For(DemoTables.CostControl).Count);
        }

        [TestMethod]
        public void LargePopulations_KeepTheHistoryBounded()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--users", "1000000", "--days", "180", "--as-of", "2026-09-01",
                "--areas", "copilot-credits" }, DateTime.UtcNow);
            var credits = new DemoCopilotCredits(options, new DemoGeneratorTests.RecordingSink(_ => false));
            Assert.AreEqual(DemoCopilotCredits.MinHistoryDays, credits.HistoryDays);
            var small = new DemoCopilotCredits(DemoOptions.Parse(new[] { "--preview", "--users", "1000", "--days", "180", "--as-of", "2026-09-01" },
                DateTime.UtcNow), new DemoGeneratorTests.RecordingSink(_ => false));
            Assert.AreEqual(DemoCopilotCredits.MaxHistoryDays, small.HistoryDays);
        }
    }
}
