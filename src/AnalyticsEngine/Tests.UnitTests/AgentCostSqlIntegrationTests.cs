using Common.Entities;
using Common.Entities.AgentCosts;
using Common.Entities.Entities.AgentCosts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.AgentCosts;

namespace Tests.UnitTests
{
    /// <summary>
    /// SQL-backed tests for the agent-cost store.
    ///
    /// The property under test is the one the whole design rests on: <b>both importers re-read a trailing
    /// window on every run</b>, because Azure re-estimates an open billing period several times a day and
    /// Copilot Studio recalculates consumption as usage days settle. If the write were an append rather than
    /// an upsert, every restated day would be multiplied by the number of runs that had seen it - and the
    /// customer's reported spend would climb on its own. That cannot be proven with an in-memory fake,
    /// because it is the unique index and the lookup-by-key that enforce it.
    /// </summary>
    [TestClass]
    public class AgentCostSqlIntegrationTests
    {
        /// <summary>
        /// A synthetic marker written into the scope / environment of every row this fixture creates, so
        /// clean-up can remove exactly its own rows and nothing else. The test database is shared.
        /// </summary>
        private static readonly string TestMarker = "agentcost-test-" + Guid.NewGuid().ToString("N");

        private static readonly DateTime Day1 = new DateTime(2001, 1, 8);
        private static readonly DateTime Day2 = new DateTime(2001, 1, 9);

        private SqlAgentCostStore _store;

        [TestInitialize]
        public void Setup()
        {
            _store = new SqlAgentCostStore(DefaultAnalyticsDbContextFactory.Instance, NullLogger.Instance);
        }

        [TestCleanup]
        public void Cleanup()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.CopilotStudioCreditDaily.RemoveRange(db.CopilotStudioCreditDaily.Where(r => r.EnvironmentId == TestMarker));
                db.AzureCostDaily.RemoveRange(db.AzureCostDaily.Where(r => r.Scope == TestMarker));
                db.AgentCostImportLogs.RemoveRange(db.AgentCostImportLogs.Where(l => l.ImportName == TestMarker));
                db.SaveChanges();
            }
        }

        [TestMethod]
        public async Task Credits_ReImportingTheSameWindow_UpdatesInPlaceInsteadOfDuplicating()
        {
            var first = CreditRow(Day1, "agent-1", "Generative answer", credits: 10m, users: 4);
            await _store.UpsertCopilotStudioCreditsAsync(new[] { first });

            // The same usage day, re-read on the next run with a restated (higher) figure - exactly what the
            // trailing window exists to pick up.
            var restated = CreditRow(Day1, "agent-1", "Generative answer", credits: 17.5m, users: 9);
            await _store.UpsertCopilotStudioCreditsAsync(new[] { restated });

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.CopilotStudioCreditDaily.Where(r => r.EnvironmentId == TestMarker).ToListAsync();

                Assert.AreEqual(1, stored.Count, "A re-read of the same slice must UPDATE, not append a second row.");
                Assert.AreEqual(17.5m, stored[0].BilledCredits, "The restated figure must win.");
                Assert.AreEqual(9, stored[0].DistinctUsers);
            }
        }

        [TestMethod]
        public async Task Credits_DifferentDimensionsOnTheSameDay_AreStoredSeparately()
        {
            await _store.UpsertCopilotStudioCreditsAsync(new[]
            {
                CreditRow(Day1, "agent-1", "Generative answer", credits: 3m),
                CreditRow(Day1, "agent-1", "Tenant graph grounding", credits: 5m),
                CreditRow(Day1, "agent-2", "Generative answer", credits: 7m),
            });

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.CopilotStudioCreditDaily.Where(r => r.EnvironmentId == TestMarker).ToListAsync();

                Assert.AreEqual(3, stored.Count, "Feature and agent are both part of the billing tuple.");
                Assert.AreEqual(15m, stored.Sum(r => r.BilledCredits));
            }
        }

        [TestMethod]
        public async Task Credits_DuplicateSlicesInsideOneBatch_DoNotViolateTheUniqueIndex()
        {
            // The importer aggregates before writing, but the store must not depend on that: two rows with
            // the same key in one batch would otherwise both be Added and blow up on SaveChanges.
            var duplicateA = CreditRow(Day1, "agent-1", "Generative answer", credits: 2m);
            var duplicateB = CreditRow(Day1, "agent-1", "Generative answer", credits: 6m);

            await _store.UpsertCopilotStudioCreditsAsync(new[] { duplicateA, duplicateB });

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.CopilotStudioCreditDaily.Where(r => r.EnvironmentId == TestMarker).ToListAsync();
                Assert.AreEqual(1, stored.Count);
                Assert.AreEqual(6m, stored[0].BilledCredits, "The later row in the batch wins, as it would on a re-read.");
            }
        }

        [TestMethod]
        public async Task AzureCosts_ReImportingARestatedDay_UpdatesInPlace()
        {
            await _store.UpsertAzureCostsAsync(new[] { CostRow(Day1, "Copilot Studio", "GBP", 1.20m, estimated: true) });

            // Azure finalised the period: same identity, corrected amount, no longer an estimate.
            await _store.UpsertAzureCostsAsync(new[] { CostRow(Day1, "Copilot Studio", "GBP", 1.35m, estimated: false) });

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.AzureCostDaily.Where(r => r.Scope == TestMarker).ToListAsync();

                Assert.AreEqual(1, stored.Count, "Azure restates history; a re-read must correct the row, not add one.");
                Assert.AreEqual(1.35m, stored[0].Cost);
                Assert.IsFalse(stored[0].IsEstimated, "The estimate flag must be corrected too, or the UI keeps warning.");
            }
        }

        [TestMethod]
        public async Task AzureCosts_SameMeterInTwoCurrencies_AreSeparateRows()
        {
            await _store.UpsertAzureCostsAsync(new[]
            {
                CostRow(Day1, "Copilot Studio", "GBP", 1m, estimated: false),
                CostRow(Day1, "Copilot Studio", "USD", 1m, estimated: false),
            });

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(2, await db.AzureCostDaily.CountAsync(r => r.Scope == TestMarker),
                    "Currency is part of the identity - amounts in different currencies are different quantities.");
            }
        }

        [TestMethod]
        public async Task Credits_StoresFullUnicodeAgentNames()
        {
            // Agent display names come from a customer tenant. The column is nvarchar, and this test crosses
            // the real storage boundary rather than asserting against a scratch table of its own design.
            const string greek = "Καλημέρα κόσμε";

            var row = CreditRow(Day1, "agent-greek", "Generative answer", credits: 1m);
            row.AgentName = greek;
            await _store.UpsertCopilotStudioCreditsAsync(new[] { row });

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.CopilotStudioCreditDaily.SingleAsync(r => r.EnvironmentId == TestMarker);
                Assert.AreEqual(greek, stored.AgentName, "A non-Latin agent name must survive the round trip to SQL.");
            }
        }

        [TestMethod]
        public async Task ImportLog_AnOverlongErrorIsTruncatedRatherThanLost()
        {
            // The error column is nvarchar(1000) and the message can carry an API response fragment. An
            // over-length value would throw on SaveChanges, losing the very diagnostic the row exists for.
            await _store.SaveImportLogAsync(new AgentCostImportLog
            {
                ImportName = TestMarker,
                ImportedUtc = DateTime.UtcNow,
                Error = new string('x', 5000),
            });

            using (var db = new AnalyticsEntitiesContext())
            {
                var log = await db.AgentCostImportLogs.SingleAsync(l => l.ImportName == TestMarker);
                Assert.AreEqual(1000, log.Error.Length);
            }
        }

        [TestMethod]
        public async Task ReportStore_SummaryTakesTheMaxUserCountNeverTheSum()
        {
            // Summing distinct-user counts across slices would double-count anyone who used two agents, and
            // would report more "users" than the tenant has. This is the single easiest number to get wrong.
            await _store.UpsertCopilotStudioCreditsAsync(new[]
            {
                CreditRow(Day1, "agent-1", "Generative answer", credits: 1m, users: 4),
                CreditRow(Day1, "agent-2", "Generative answer", credits: 1m, users: 6),
                CreditRow(Day2, "agent-1", "Generative answer", credits: 1m, users: 2),
            });

            var reportStore = new SqlAgentCostReportStore(DefaultAnalyticsDbContextFactory.Instance);
            var summary = await reportStore.GetSummaryAsync(new AgentCostQuery
            {
                FromUtc = Day1,
                ToUtc = Day2,
                EnvironmentId = TestMarker,
            });

            Assert.AreEqual(6, summary.PeakDistinctUsersOnASlice, "Expected MAX (6), not SUM (12).");
            Assert.AreEqual(3m, summary.BilledCredits, "Credits, by contrast, DO add up.");
            Assert.AreEqual(2, summary.DistinctAgents);
            Assert.AreEqual(2, summary.DaysWithUsage);
        }

        [TestMethod]
        public async Task ReportStore_BreakdownGroupsByTheRequestedDimension()
        {
            await _store.UpsertCopilotStudioCreditsAsync(new[]
            {
                CreditRow(Day1, "agent-1", "Generative answer", credits: 10m),
                CreditRow(Day2, "agent-1", "Generative answer", credits: 5m),
                CreditRow(Day1, "agent-2", "Generative answer", credits: 2m),
            });

            var reportStore = new SqlAgentCostReportStore(DefaultAnalyticsDbContextFactory.Instance);
            var rows = await reportStore.GetBreakdownAsync(
                new AgentCostQuery { FromUtc = Day1, ToUtc = Day2, EnvironmentId = TestMarker },
                AgentCostDimensions.Agent, 10);

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("agent-1", rows[0].Key, "Biggest spender first.");
            Assert.AreEqual(15m, rows[0].BilledCredits);
            Assert.AreEqual(2, rows[0].ActiveDays, "agent-1 was active on both days.");
        }

        [TestMethod]
        public async Task ReportStore_DetailPagingIsStableAcrossPages()
        {
            // Equal sort keys with no tie-breaker let SQL Server return a different order per call, which
            // makes paging silently drop and repeat rows.
            var rows = Enumerable.Range(0, 10)
                .Select(i => CreditRow(Day1, "agent-" + i, "Generative answer", credits: 1m))
                .ToArray();
            await _store.UpsertCopilotStudioCreditsAsync(rows);

            var reportStore = new SqlAgentCostReportStore(DefaultAnalyticsDbContextFactory.Instance);
            var query = new AgentCostQuery { FromUtc = Day1, ToUtc = Day2, EnvironmentId = TestMarker, PageSize = 4, Sort = "credits" };

            query.Page = 1;
            var page1 = await reportStore.GetDetailAsync(query);
            query.Page = 2;
            var page2 = await reportStore.GetDetailAsync(query);
            query.Page = 3;
            var page3 = await reportStore.GetDetailAsync(query);

            Assert.AreEqual(10, page1.TotalRows);

            var seen = page1.Rows.Concat(page2.Rows).Concat(page3.Rows).Select(r => r.AgentId).ToList();
            Assert.AreEqual(10, seen.Count, "Every row must appear exactly once across the pages.");
            Assert.AreEqual(10, seen.Distinct().Count(), "No row may be repeated on a later page.");
        }

        [TestMethod]
        public async Task AzureCosts_Replace_RemovesRowsTheLatestQueryNoLongerReturns()
        {
            // A Cost Management answer is a complete snapshot of its scope and window. After the meter filter
            // is narrowed, the rows it no longer returns are no longer true - leaving them behind would
            // overstate spend and look like a rise that never happened.
            await _store.ReplaceAzureCostsAsync(
                new[]
                {
                    CostRow(Day1, "Meter A", "GBP", 1m, estimated: false),
                    CostRow(Day1, "Meter B", "GBP", 2m, estimated: false),
                },
                TestMarker, Day1, Day1);

            // The next run's filter matches only Meter A.
            await _store.ReplaceAzureCostsAsync(
                new[] { CostRow(Day1, "Meter A", "GBP", 1m, estimated: false) },
                TestMarker, Day1, Day1);

            using (var db = new AnalyticsEntitiesContext())
            {
                var stored = await db.AzureCostDaily.Where(r => r.Scope == TestMarker).ToListAsync();
                Assert.AreEqual(1, stored.Count, "Meter B is no longer returned, so it must no longer be stored.");
                Assert.AreEqual("Meter A", stored[0].MeterName);
            }
        }

        [TestMethod]
        public async Task AzureCosts_Replace_WithNoRows_ClearsTheWindow()
        {
            // "The filter now matches nothing" has to remove what was there before. An early return on an
            // empty result would leave the old rows visible for ever while the run logged cleanly.
            await _store.ReplaceAzureCostsAsync(
                new[] { CostRow(Day1, "Meter A", "GBP", 1m, estimated: false) }, TestMarker, Day1, Day1);

            await _store.ReplaceAzureCostsAsync(new List<AzureCostDaily>(), TestMarker, Day1, Day1);

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(0, await db.AzureCostDaily.CountAsync(r => r.Scope == TestMarker));
            }
        }

        [TestMethod]
        public async Task AzureCosts_Replace_NeverTouchesAnotherScopeOrAnotherDay()
        {
            // The delete is the most dangerous operation in this feature. It must be bounded by BOTH the
            // scope and the window, or one subscription's import would wipe another's rows.
            var otherScope = TestMarker + "-other";

            await _store.ReplaceAzureCostsAsync(
                new[] { CostRow(Day1, "Meter A", "GBP", 1m, estimated: false) }, TestMarker, Day1, Day1);
            await _store.ReplaceAzureCostsAsync(
                new[] { CostRow(Day2, "Meter A", "GBP", 3m, estimated: false) }, TestMarker, Day2, Day2);

            var otherRow = CostRow(Day1, "Meter A", "GBP", 9m, estimated: false);
            otherRow.Scope = otherScope;
            otherRow.RowHash = AgentCostRowHasher.Hash(Day1.ToString("yyyy-MM-dd"), otherScope, "Meter A", "GBP");
            await _store.ReplaceAzureCostsAsync(new[] { otherRow }, otherScope, Day1, Day1);

            try
            {
                // Re-run day 1 for the first scope with nothing returned.
                await _store.ReplaceAzureCostsAsync(new List<AzureCostDaily>(), TestMarker, Day1, Day1);

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual(0, await db.AzureCostDaily.CountAsync(r => r.Scope == TestMarker && r.UsageDate == Day1),
                        "The targeted scope and day should have been cleared.");
                    Assert.AreEqual(1, await db.AzureCostDaily.CountAsync(r => r.Scope == TestMarker && r.UsageDate == Day2),
                        "A DIFFERENT DAY in the same scope must be untouched.");
                    Assert.AreEqual(1, await db.AzureCostDaily.CountAsync(r => r.Scope == otherScope),
                        "A DIFFERENT SCOPE must be untouched.");
                }
            }
            finally
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.AzureCostDaily.RemoveRange(db.AzureCostDaily.Where(r => r.Scope == otherScope));
                    await db.SaveChangesAsync();
                }
            }
        }

        private static CopilotStudioCreditDaily CreditRow(DateTime day, string agentId, string feature, decimal credits, int? users = null)
        {
            var hash = AgentCostRowHasher.Hash(day.ToString("yyyy-MM-dd"), TestMarker, agentId, feature);

            return new CopilotStudioCreditDaily
            {
                UsageDate = day,
                EnvironmentId = TestMarker,
                AgentId = agentId,
                AgentName = agentId,
                FeatureName = feature,
                Harness = CopilotStudioHarnessClassifier.Classify(feature),
                BilledCredits = credits,
                DistinctUsers = users,
                DimensionHash = hash,
                ImportedUtc = DateTime.UtcNow,
            };
        }

        private static AzureCostDaily CostRow(DateTime day, string meter, string currency, decimal cost, bool estimated)
        {
            var hash = AgentCostRowHasher.Hash(day.ToString("yyyy-MM-dd"), TestMarker, meter, currency);

            return new AzureCostDaily
            {
                UsageDate = day,
                Scope = TestMarker,
                MeterName = meter,
                Currency = currency,
                Cost = cost,
                IsEstimated = estimated,
                RowHash = hash,
                ImportedUtc = DateTime.UtcNow,
            };
        }
    }
}
