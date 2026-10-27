extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Tests.FakeDataGen.Demo;
using CachedUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.CachedUserDirectorySource;
using CopilotInvestmentFactsCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentFactsCache;
using CopilotInvestmentReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentReportService;
using CopilotInvestmentRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentRequest;
using ReportScope = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScope;

namespace Tests.UnitTests
{
    /// <summary>
    /// The demo is only worth generating if the report can tell its stories from it. This runs the real chain - the
    /// generated database, the adoption analysis, the report's own SQL and the report service - and checks the
    /// figures an executive would read, the honesty rules behind them and the scope they are narrowed to.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public class CopilotInvestmentDemoReportTests
    {
        public TestContext TestContext { get; set; }

        private static readonly DateTime AsOf = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Test inputs only - illustrative numbers an administrator would replace, never product defaults.</summary>
        private static CopilotInvestmentAssumptions TestAssumptions() => new CopilotInvestmentAssumptions
        {
            Currency = "EUR",
            SeatPricePerMonth = 28,
            CreditPrice = 0.01,
            StudioCreditPrice = 0.01,
            HourlyCost = 45,
            MinutesPerActiveDay = new CopilotInvestmentRange { Conservative = 8, Base = 15, Upside = 25 },
            Realisation = new CopilotInvestmentRange { Conservative = 0.3, Base = 0.5, Upside = 0.7 },
            Note = "Synthetic test inputs \u2013 \u0394\u03bf\u03ba\u03b9\u03bc\u03ae",
        };

        [TestMethod]
        public async Task GeneratedDemo_TellsSeveralInvestmentStories_ThroughTheRealReportChain()
        {
            string name = "ContosoDemo_Investment_" + Guid.NewGuid().ToString("N");
            var options = DemoOptions.Parse(new[] { "--database", name, "--users", "400", "--days", "100",
                "--as-of", AsOf.ToString("yyyy-MM-dd"), "--batch-size", "1000", "--no-profiles" }, DateTime.UtcNow);
            try
            {
                var watch = Stopwatch.StartNew();
                using (var database = new SqlDemoDatabase(options, CancellationToken.None))
                {
                    database.Open(null);
                    var summary = DemoCommand.NewSummary(options);
                    using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                        new DemoGenerator(options).Generate(sink, summary, null);
                    database.ValidateAndComplete(summary, null);
                    TestContext.WriteLine($"Generated {summary.TotalRows:N0} rows ({summary.Rows["copilot_cost_observations"]:N0} credit observations) in {watch.Elapsed.TotalSeconds:F1}s.");
                }
                var connection = SqlDemoDatabase.LocalConnection(name);

                var range = CopilotAdoptionDateRange.Create(28, "2026-09-29", "2026-10-26", AsOf.AddDays(1));
                var analysis = await new CopilotAdoptionService(new CopilotAdoptionOptions
                {
                    WindowDays = 28,
                    UsesExplicitDates = true,
                    FromUtc = range.FromUtc,
                    ToUtc = range.ToInclusiveUtc,
                    ToExclusiveUtc = range.ToExclusiveUtc,
                }, new ConnectionStringAnalyticsDbContextFactory(connection)).AnalyseAsync();
                Assert.IsTrue(analysis.LicensedUsers.Count > 0, string.Join(" | ", analysis.Summary.Warnings));

                var store = new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true);
                await store.SaveAsync(TestAssumptions(), 0, "admin@contoso.example");
                var source = new SqlCopilotInvestmentFactsSource(() => new SqlConnection(connection));
                var directory = new CachedUserDirectorySource(() => UserFilterStores.CreateDirectoryLoader(connection),
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
                var service = new CopilotInvestmentReportService(
                    new CopilotInvestmentFactsCache(source, new MemoryCache("investment-facts-" + name)),
                    () => store, directory,
                    () => new ImportTaskSettings { GraphUsersMetadata = true, Copilot = true, GraphCopilotUsageReports = true,
                        CopilotStudioCredits = true, CopilotCostManagement = true },
                    () => true, new MemoryCache("investment-results-" + name));

                watch.Restart();
                var report = await service.BuildAsync(new CopilotInvestmentRequest { Range = range, Scope = ReportScope.Unrestricted },
                    analysis, CancellationToken.None);
                TestContext.WriteLine($"Report built in {watch.Elapsed.TotalSeconds:F2}s for {report.People.Count:N0} people.");

                // What is being invested: seats from the assumptions, Studio from its ledger, balances kept apart.
                var spend = report.Investment;
                Assert.IsTrue(spend.Seats.Holders > 0 && spend.Seats.AnalysedWindowCost > 0);
                Assert.IsTrue(spend.Studio.Credits > 0, "The demo's Studio ledger must reach the report.");
                Assert.AreEqual(Math.Round(spend.Seats.AnalysedWindowCost.Value + (spend.Studio.HolderWindowValue ?? 0), 2), spend.ModelledWindowCost,
                    "The cost a return is measured against is seats plus the holders' own billed Studio credits - never the balances.");
                Assert.IsTrue(spend.Credits.Credits > 0 && spend.Credits.BalanceValue > 0);
                Assert.AreEqual("unverified", spend.Credits.PeriodStatus);

                // Sources and honesty.
                var credits = report.Sources.Credits;
                Assert.IsTrue(credits.HasData && credits.CollectionEnabled);
                Assert.AreEqual(new DateTime(2026, 10, 26), credits.SnapshotDate);
                Assert.IsTrue(credits.ObservedUsers >= 380, "Nearly every person is observed on the latest sweep: " + credits.ObservedUsers);
                Assert.IsTrue(credits.UnresolvedObjects > 0 && credits.UnresolvedCredits > 0);
                Assert.IsTrue(credits.SweepsInWindow > credits.CompletedSweepsInWindow, "Some sweeps in the window did not complete.");
                Assert.IsTrue(report.Trust.Reasons.Any(r => r.Code == CopilotInvestmentTrustCodes.BalancesUnverified));
                Assert.AreNotEqual("high", report.Trust.Level);
                Assert.IsTrue(spend.Credits.OtherUnitUsers > 0, "The service in an unknown unit must be counted apart.");

                // No double count: every credit-unit service quantity is either an imported person's or an unlinked account's.
                var serviceCredits = report.Services.Where(s => s.Unit == CopilotInvestmentSql.CreditUnit).Sum(s => s.Quantity);
                Assert.AreEqual(Math.Round(spend.Credits.Credits + credits.UnresolvedCredits.Value, 3), Math.Round(serviceCredits, 3));
                Assert.IsTrue(report.Services.Any(s => s.Category == DemoCopilotCredits.PreviewService && s.Unit == DemoCopilotCredits.FutureUnit));

                // Value, break-even and sensitivity exist only as scenarios over observed active days.
                Assert.IsTrue(report.Value.Available);
                var scenarios = report.Value.Scenarios.ToDictionary(s => s.Scenario);
                Assert.IsTrue(scenarios["conservative"].Value <= scenarios["base"].Value && scenarios["base"].Value <= scenarios["upside"].Value);
                Assert.IsTrue(report.BreakEven.Available && report.BreakEven.MinutesPerActiveDay > 0);
                Assert.AreEqual(9, report.Sensitivity.Grid.Count);

                // The stories: idle seats, evidenced seats, high spend on thin evidence, metered people without a seat.
                foreach (var row in report.People.OrderByDescending(p => p.Credits ?? 0).Take(15))
                    TestContext.WriteLine($"{row.UserId} licensed={row.Licensed} band={row.Band} days/month={row.ActiveDaysPerMonth} credits={row.Credits} signal={row.Signal} flags={string.Join(",", row.Flags)}");
                Assert.IsTrue(report.Evidence.IdleHolders > 0);
                Assert.IsTrue(report.People.Any(p => p.Licensed && p.Signal == CopilotInvestmentSignals.Supported));
                Assert.IsTrue(report.People.Any(p => p.Flags.Contains(CopilotInvestmentFlags.HighSpendThinEvidence)),
                    "A lightly-evidenced seat holder running a heavy automation must be flagged for review.");
                Assert.IsTrue(report.People.Any(p => !p.Licensed && (p.Credits ?? 0) > 0));
                Assert.IsTrue(report.People.Any(p => p.Flags.Contains(CopilotInvestmentFlags.LimitExhausted)));
                Assert.IsTrue(report.People.Count < options.Users, "Quiet people with no seat and a zero balance are counted, not listed.");
                Assert.IsNotNull(report.Concentration.Credits);
                Assert.IsTrue(report.Organisation.Groups.Count >= 2);
                Assert.IsTrue(report.Actions.Count > 0 && report.Findings.Count > 0);

                // Tenant context is reported as tenant-wide, with its Unicode intact.
                Assert.AreEqual(3, report.Policies.Count);
                Assert.IsTrue(report.Policies.Any(p => p.DisplayName == DemoCopilotCredits.PausedPolicyName && p.Status == "disabled"));
                Assert.AreEqual(2, report.TenantBalances.Count);

                // A balance trend is a set of snapshots: the period reset shows as a drop, and a day without a sweep has no point.
                var trend = await source.GetCreditTrendAsync(new[] { new DateTime(2026, 9, 30), new DateTime(2026, 10, 1), new DateTime(2026, 10, 2) },
                    ReportUserScope.Everyone, CancellationToken.None);
                var september = trend.SingleOrDefault(p => p.Date == new DateTime(2026, 9, 30));
                var october = trend.SingleOrDefault(p => p.Date == new DateTime(2026, 10, 1));
                if (september != null) Assert.IsTrue(september.Credits > 0);
                if (october != null) Assert.AreEqual(0, october.Credits ?? 0, 1e-9, "Nothing is in a period that started today.");

                // Narrowed to Sales: the people, the balances and the coverage all follow; tenant-only facts are withheld.
                var snapshot = await directory.GetAsync(CancellationToken.None);
                var sales = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]"), snapshot);
                var scoped = await service.BuildAsync(new CopilotInvestmentRequest { Range = range, Scope = new ReportScope(null, null, sales, "sales") },
                    analysis, CancellationToken.None);
                Assert.IsTrue(scoped.Filtered);
                Assert.IsTrue(scoped.People.Count > 0 && scoped.People.All(p => p.Department == "Sales"));
                Assert.IsTrue(scoped.Sources.Credits.ObservedUsers < credits.ObservedUsers);
                Assert.IsNull(scoped.Sources.Credits.UnresolvedObjects, "An unlinked account cannot be shown to be in Sales.");
                Assert.IsTrue(scoped.Investment.Credits.Credits < spend.Credits.Credits);

                // The person drill-down reads one person's own evidence.
                var person = report.People.First(p => p.Licensed && p.ActiveDays > 3 && (p.Credits ?? 0) > 0);
                var detail = await source.GetPersonDetailAsync(person.UserId, person.CreditObjectId, person.CreditObservationDate,
                    range.FromUtc, range.ToExclusiveUtc, CancellationToken.None);
                Assert.IsTrue(detail.ActivityAssessed && detail.Activity.Count > 0, "The drill-down must show where the person used Copilot.");
                Assert.IsTrue(detail.CreditServices.Count > 0);
                Assert.IsTrue(detail.Balances.Count > 1, "A person's balance history is a curve, not one reading.");
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
