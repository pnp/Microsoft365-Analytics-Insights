extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.Localisation;
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
using CopilotSpendReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendReportService;
using CopilotSpendRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendRequest;
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
                // A sweep and a Copilot Studio charge on the 27th - the day after the window ends. A window ending on the
                // 26th must read neither: not in its balances, not in its Studio cost, not in a person's drill-down.
                var (sentinelUser, sentinelObject) = AddNextDaySentinels(connection);

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
                Assert.AreEqual(new DateTime(2026, 10, 26), credits.SnapshotDate, "The sweep of the 27th is after the window.");
                Assert.AreEqual(new DateTime(2026, 10, 26), report.ToUtc, "The window says it ends on the 26th.");
                var sentinelRow = report.People.Single(p => p.UserId == sentinelUser);
                Assert.IsTrue(sentinelRow.StudioCredits < SentinelCredits && (sentinelRow.Credits ?? 0) < SentinelCredits,
                    "Neither the Studio charge nor the balance of the 27th belongs to a window ending on the 26th.");
                Assert.IsTrue(spend.Studio.Credits < SentinelCredits);
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

                // The real report's workbook, in both languages: every finding, action and caveat the demo produces has its
                // Spanish words, with the same typed cells as the English one.
                CopilotInvestmentWorkbookLanguageTests.AssertSpanishCounterpart(
                    CopilotInvestmentWorkbook.Build(report, true, "test", PortalLanguage.English),
                    CopilotInvestmentWorkbook.Build(report, true, "test", PortalLanguage.Spanish));

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
                var lastDay = CopilotInvestmentWindow.LastDay(range);
                var detail = await source.GetPersonDetailAsync(person.UserId, person.CreditObjectId, person.CreditObservationDate,
                    range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: false, withPromptCategories: true, CancellationToken.None);
                Assert.IsTrue(detail.ActivityAssessed && detail.Activity.Count > 0, "The drill-down must show where the person used Copilot.");
                Assert.IsTrue(detail.Activity.All(a => a.EstimateCredits == null && a.PricedTurns == 0),
                    "The Investment drill-down does not ask for the estimate, so its rows carry none.");
                Assert.IsTrue(detail.CreditServices.Count > 0);
                Assert.IsTrue(detail.Balances.Count > 1, "A person's balance history is a curve, not one reading.");

                var sentinelDetail = await source.GetPersonDetailAsync(sentinelUser, sentinelObject, sentinelRow.CreditObservationDate,
                    range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: false, withPromptCategories: true, CancellationToken.None);
                Assert.IsTrue(sentinelDetail.Balances.Count > 0 && sentinelDetail.Balances.All(b => b.Date <= lastDay),
                    "The drill-down's balance curve stops at the window's last day.");
                Assert.IsFalse(sentinelDetail.StudioEnvironments.Any(e => e.EnvironmentId == SentinelEnvironment), "Nor does its Studio ledger reach the day after.");
                Assert.IsTrue(detail.StudioEnvironments.All(e => e.EnvironmentId != null),
                    "The per-person ledger is kept by environment: the drill-down groups by what it stores.");

                // Copilot Spend over the same generated database, through its own SQL and the shared fact cache: no adoption
                // analysis and no assumptions needed. The two sources stay apart and each adds up the way it should.
                var spendService = new CopilotSpendReportService(
                    new CopilotInvestmentFactsCache(source, new MemoryCache("spend-facts-" + name)),
                    new SqlCopilotSpendFactsSource(() => new SqlConnection(connection)), directory,
                    () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                    new MemoryCache("spend-results-" + name));
                watch.Restart();
                var spendReport = await spendService.BuildAsync(new CopilotSpendRequest { Range = range, Scope = ReportScope.Unrestricted }, CancellationToken.None);
                TestContext.WriteLine($"Spend report built in {watch.Elapsed.TotalSeconds:F2}s for {spendReport.People.Count:N0} people.");
                var billed = spendReport.Studio;
                Assert.IsTrue(billed.Credits > 0 && billed.Days.Count > 0 && billed.Environments.Count > 1, "The demo's Studio ledger must reach the spend report.");
                Assert.AreEqual(billed.Credits.Value, billed.Days.Sum(d => d.Credits ?? 0), 0.01, "Billed usage is a daily ledger: the days add up to the period.");
                Assert.AreEqual(billed.Credits.Value, billed.Environments.Sum(e => e.Credits ?? 0), 0.01, "And so do the environments.");
                Assert.IsTrue(billed.Environments.All(e => e.EnvironmentName != null), "Each environment keeps the name the import recorded.");
                Assert.AreEqual(billed.Credits.Value, spendReport.People.Sum(p => p.StudioCredits ?? 0) + (billed.UnmatchedCredits ?? 0), 0.01, "And so do the people.");
                Assert.IsFalse(billed.Days.Any(d => d.Date > lastDay), "No billed day after the window's last day.");
                Assert.IsFalse(billed.Environments.Any(e => e.EnvironmentId == SentinelEnvironment), "Nor the next day's charge.");
                // The agent ledger is its own route: named agents with its own total, never added to the people's.
                Assert.IsTrue(billed.Agents != null && billed.Agents.Count > 1 && billed.Agents.All(a => a.AgentName != null));
                Assert.IsTrue(billed.Agents.Any(a => a.AgentName.Contains("\u039a\u03b1\u03bb\u03b7\u03bc\u03ad\u03c1\u03b1")), "Agent names keep their Unicode.");
                Assert.AreEqual(billed.AgentLedgerCredits.Value, billed.Agents.Sum(a => a.Credits), 0.01);
                Assert.IsFalse(billed.Agents.Any(a => a.AgentId == SentinelAgent), "The agent ledger stops at the window's last day too.");
                Assert.IsTrue(spendReport.Balances.Credits < SentinelCredits && spendReport.Balances.SnapshotDate == lastDay, "Balances read on the window's last day.");
                Assert.AreEqual(spendReport.Balances.Credits.Value,
                    spendReport.People.Sum(p => p.BalanceCredits ?? 0) + (spendReport.Balances.UnresolvedCredits ?? 0), 0.01,
                    "Latest balances: the people's readings plus the accounts no user matched - and never a sum of days.");
                Assert.AreEqual(spendReport.Balances.UnknownAmountPeople, spendReport.People.Count(p => p.UnknownAmounts));
                Assert.IsTrue(spendReport.Organisation.Groups.Count >= 2);
                Assert.AreEqual(spendReport.People.Sum(p => p.StudioCredits ?? 0), spendReport.Organisation.Groups.Sum(g => g.StudioCredits ?? 0), 0.01);
                Assert.IsTrue(spendReport.Balances.Policies.Any(p => p.DisplayName == DemoCopilotCredits.PausedPolicyName), "Tenant context, with its Unicode intact.");

                var spendSales = await spendService.BuildAsync(new CopilotSpendRequest { Range = range, Scope = new ReportScope(null, null, sales, "sales") },
                    CancellationToken.None);
                Assert.IsTrue(spendSales.Filtered && spendSales.People.Count > 0 && spendSales.People.All(p => p.Department == "Sales"));
                Assert.IsTrue(spendSales.Studio.Credits < billed.Credits && spendSales.Studio.UnmatchedCredits == null);
                Assert.AreEqual(spendSales.Studio.Credits.Value, spendSales.Studio.Days.Sum(d => d.Credits ?? 0), 0.01, "The scoped ledger in SQL matches the scoped people.");
                Assert.AreEqual(spendSales.Studio.Credits.Value, spendSales.Studio.Environments.Sum(e => e.Credits ?? 0), 0.01, "So does its environment breakdown.");
                Assert.IsNull(spendSales.Studio.Agents, "The agent ledger is not kept per person, so a filtered view cannot have its share of it.");
                Assert.IsNull(spendSales.Studio.AgentLedgerCredits);
                Assert.IsTrue(spendSales.Balances.Policies.Count == 0 && spendSales.Balances.TenantBalances.Count == 0 && spendSales.Balances.UnresolvedAccounts == null);

                // One person's Spend drill-down: their turns with the importer's estimate, and what they used Copilot for, read by
                // the drill-down's own SQL and checked against the same tables counted directly.
                var usageTotals = PersonUsageTotals(connection, range.FromUtc, range.ToExclusiveUtc);
                var spendPerson = spendReport.People
                    .Where(p => usageTotals.TryGetValue(p.UserId, out var totals) && totals.Priced > 0 && totals.Prompts > 0)
                    .OrderByDescending(p => usageTotals[p.UserId].Turns).ThenBy(p => p.UserId).FirstOrDefault();
                Assert.IsNotNull(spendPerson, "The demo must have someone in the spend report who used a priced agent and wrote prompts.");
                var expected = usageTotals[spendPerson.UserId];

                var plain = await source.GetPersonDetailAsync(spendPerson.UserId, spendPerson.CreditObjectId, spendPerson.BalanceDate,
                    range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: false, withPromptCategories: false, CancellationToken.None);
                var estimated = await source.GetPersonDetailAsync(spendPerson.UserId, spendPerson.CreditObjectId, spendPerson.BalanceDate,
                    range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: true, withPromptCategories: false, CancellationToken.None);
                Assert.AreEqual(0, plain.PromptCategories.Count + estimated.PromptCategories.Count,
                    "The Spend drill-down reads goals by taxonomy version itself, so it does not ask for the investment goal counts.");
                CollectionAssert.AreEqual(plain.Activity.Select(ActivityKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    estimated.Activity.Select(ActivityKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    "Asking for the estimate changes the plan, never the turns.");
                Assert.AreEqual(expected.Turns, estimated.Activity.Sum(a => a.Turns));
                Assert.AreEqual(expected.Estimate, estimated.Activity.Sum(a => a.EstimateCredits ?? 0), 1e-6 * Math.Max(1, expected.Estimate));
                Assert.AreEqual(expected.Priced, estimated.Activity.Sum(a => a.PricedTurns));

                var usage = await new SqlCopilotSpendFactsSource(() => new SqlConnection(connection))
                    .GetPersonUsageAsync(spendPerson.UserId, range.FromUtc, range.ToExclusiveUtc, lastDay, CancellationToken.None);
                Assert.IsTrue(usage.StudioAssessed && usage.PromptsAssessed && usage.CategoriesAssessed && usage.PhrasesAssessed && usage.ResourcesAssessed,
                    "Every table the demo writes is read.");
                Assert.AreEqual(expected.Prompts, usage.PromptTotals.Prompts);
                Assert.AreEqual(Math.Min(SqlCopilotSpendFactsSource.RecentPromptCount, expected.Prompts), usage.Recent.Count);
                Assert.IsTrue(usage.Recent.Zip(usage.Recent.Skip(1), (newer, older) => newer.CreatedUtc >= older.CreatedUtc).All(ordered => ordered),
                    "The latest prompts, newest first.");
                Assert.IsTrue(usage.Recent.All(r => r.CreatedUtc >= range.FromUtc && r.CreatedUtc < range.ToExclusiveUtc
                    && r.Phrases.Count <= SqlCopilotSpendFactsSource.PhrasesPerPrompt));
                Assert.IsTrue(usage.Categories.Count > 0 && usage.Taxonomies.Count > 0 && usage.Categories.Sum(c => c.Prompts) <= expected.Prompts);
                var withGoals = await source.GetPersonDetailAsync(spendPerson.UserId, spendPerson.CreditObjectId, spendPerson.BalanceDate,
                    range.FromUtc, range.ToExclusiveUtc, lastDay, withCreditEstimate: false, withPromptCategories: true, CancellationToken.None);
                Assert.AreEqual(usage.Categories.Sum(c => c.Prompts), withGoals.PromptCategories.Sum(c => c.Prompts),
                    "The control: the same person has classified prompts, which the Investment drill-down still reads.");
                Assert.IsTrue(usage.Phrases.Count > 0 && usage.Phrases.Count <= SqlCopilotSpendFactsSource.TopPhraseCount);
                Assert.IsTrue(usage.ResourceTypes.Count > 0, "Copilot read files and sites for them.");

                var personDetail = await spendService.PersonAsync(new CopilotSpendRequest { Range = range, Scope = ReportScope.Unrestricted },
                    spendPerson.UserId, CancellationToken.None);
                Assert.IsNotNull(personDetail);
                Assert.IsNull(personDetail.UsageFailureCode);
                Assert.AreEqual(expected.Turns, personDetail.Usage.Turns);
                Assert.AreEqual(expected.Estimate, personDetail.Usage.EstimateCredits ?? 0, 1e-5 * Math.Max(1, expected.Estimate));
                Assert.AreEqual(personDetail.Usage.EstimateCredits ?? 0, personDetail.Usage.Agents.Sum(a => a.EstimateCredits ?? 0),
                    1e-5 * Math.Max(1, expected.Estimate), "The importer prices agents' turns only, so the agents carry the whole estimate.");
                Assert.IsTrue(personDetail.Usage.Agents.Where(a => a.EstimateCredits > 0)
                    .All(a => a.Name != null && a.KindKey == CopilotSpendAgentKindKeys.CustomEngine), "A priced agent is a named custom-engine agent.");
                Assert.AreEqual((lastDay.Date - range.FromUtc.Date).Days + 1, personDetail.Usage.Days.Count, "Every day of the window keeps its place.");
                Assert.AreEqual(expected.Turns, personDetail.Usage.Days.Sum(d => d.Turns));
                Assert.AreEqual(expected.Prompts, personDetail.Usage.Prompts.Prompts);
                Assert.IsTrue(personDetail.Usage.Recent.All(r => r.CategoryId == null
                    || r.CategoryId == CopilotSpendPersonDetailBuilder.NotClassifiedCategoryId || r.CategoryName != null),
                    "A classified prompt is named from the taxonomy that classified it.");
                if ((spendPerson.StudioCredits ?? 0) > 0)
                {
                    Assert.IsNotNull(personDetail.StudioDays);
                    Assert.AreEqual(spendPerson.StudioCredits.Value, personDetail.StudioDays.Sum(d => d.Credits ?? 0), 0.01,
                        "Their billed days add up to their Studio credits.");
                }
                if ((spendPerson.BalanceCredits ?? 0) > 0)
                    Assert.IsTrue(personDetail.Ranks.BalanceRank >= 1 && personDetail.Ranks.BalanceShare > 0);

                // Never a way round the filter: someone outside Sales has no drill-down in the Sales view.
                var outsider = spendReport.People.First(p => p.Department != "Sales");
                Assert.IsNull(await spendService.PersonAsync(new CopilotSpendRequest { Range = range, Scope = new ReportScope(null, null, sales, "sales") },
                    outsider.UserId, CancellationToken.None));

                CopilotInvestmentWorkbookLanguageTests.AssertSpanishCounterpart(
                    CopilotSpendWorkbook.Build(spendReport, true, "test", PortalLanguage.English),
                    CopilotSpendWorkbook.Build(spendReport, true, "test", PortalLanguage.Spanish), CopilotSpendWorkbook.CatalogModule);
            }
            finally { Drop(name); }
        }

        private const double SentinelCredits = 999999;
        private const string SentinelAgent = "sentinel-next-day";
        private const string SentinelEnvironment = "env-sentinel-next-day";

        /// <summary>
        /// Copies one consuming person's sweep of the 26th to the 27th with an outsized balance, and bills them an outsized
        /// Copilot Studio charge on the 27th, in both the per-person ledger and the agent ledger. Returns that person.
        /// </summary>
        private static (int UserId, Guid ObjectId) AddNextDaySentinels(string connection)
        {
            using (var db = new SqlConnection(connection))
            {
                db.Open();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = @"
SET NOCOUNT ON;
DECLARE @object uniqueidentifier, @user int;
SELECT TOP (1) @object = o.object_id, @user = o.user_id
FROM dbo.copilot_cost_observations AS o
JOIN dbo.copilot_cost_services AS s ON s.observation_date = o.observation_date AND s.object_id = o.object_id
WHERE o.observation_date = '2026-10-26' AND o.user_id IS NOT NULL AND s.consumed_quantity > 0
ORDER BY o.user_id;
INSERT dbo.copilot_cost_observations (observation_date, object_id, user_id, observed_at, quality, source_month, period_status, selection_status)
SELECT '2026-10-27', o.object_id, o.user_id, '2026-10-27T06:00:00+00:00', o.quality, o.source_month, o.period_status, o.selection_status
FROM dbo.copilot_cost_observations AS o WHERE o.observation_date = '2026-10-26' AND o.object_id = @object;
INSERT dbo.copilot_cost_services (observation_date, object_id, category_hash, category, policy_id, consumed_quantity, unit, allowed,
       decision_reason, source_modified, period_limit, per_user_limit, capacity, remaining, data_status)
SELECT '2026-10-27', s.object_id, s.category_hash, s.category, s.policy_id, @credits, s.unit, s.allowed,
       s.decision_reason, s.source_modified, s.period_limit, s.per_user_limit, s.capacity, s.remaining, s.data_status
FROM dbo.copilot_cost_services AS s WHERE s.observation_date = '2026-10-26' AND s.object_id = @object;
INSERT dbo.copilot_studio_credit_user_daily (usage_date, entra_object_id, user_id, environment_id, environment_name, agent_id,
       billed_credits, unit, dimension_hash, imported_utc)
VALUES ('2026-10-27', CONVERT(nvarchar(200), @object), @user, @environment, N'Contoso sentinel', NULL, @credits, N'credit', @environment, SYSUTCDATETIME());
INSERT dbo.copilot_studio_credit_daily (usage_date, environment_id, environment_name, agent_id, agent_name, harness, feature_name,
       billed_credits, non_billed_credits, distinct_users, last_refreshed_utc, dimension_hash, imported_utc)
VALUES ('2026-10-27', @environment, N'Contoso sentinel', @agent, N'Contoso sentinel agent', NULL, N'Generative answer', @credits, 0, 1, NULL, @agent, SYSUTCDATETIME());
SELECT @user, @object;";
                    command.Parameters.AddWithValue("@credits", SentinelCredits);
                    command.Parameters.AddWithValue("@agent", SentinelAgent);
                    command.Parameters.AddWithValue("@environment", SentinelEnvironment);
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.IsTrue(reader.Read() && !reader.IsDBNull(0), "The demo must have a consuming person on the 26th.");
                        return (reader.GetInt32(0), reader.GetGuid(1));
                    }
                }
            }
        }

        private static string ActivityKey(PersonActivityRow row) => $"{row.ActivityDate:yyyy-MM-dd}|{row.AppHost}|{row.AgentId}|{row.Turns}";

        /// <summary>
        /// Each person's counted Copilot turns, estimate and priced turns over the window, and their prompts, counted straight
        /// from the tables: what the drill-down's own SQL must agree with.
        /// </summary>
        private static Dictionary<int, (long Turns, double Estimate, long Priced, long Prompts)> PersonUsageTotals(string connection,
            DateTime from, DateTime toExclusive)
        {
            var totals = new Dictionary<int, (long Turns, double Estimate, long Priced, long Prompts)>();
            using (var db = new SqlConnection(connection))
            {
                db.Open();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = @"
SELECT a.user_id, COUNT_BIG(*), ISNULL(SUM(CAST(c.copilot_credit_estimate_total AS float)), 0),
       COUNT_BIG(CASE WHEN c.copilot_credit_estimate_total > 0 THEN 1 END)
FROM dbo.copilot_chats AS c
JOIN dbo.audit_events AS a ON a.id = c.event_id
WHERE a.user_id IS NOT NULL AND a.time_stamp >= @from AND a.time_stamp < @to AND c.time_stamp >= @from AND c.time_stamp < @to
  AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.time_stamp = c.time_stamp AND d.event_id = c.event_id)
GROUP BY a.user_id;
SELECT i.user_id, COUNT_BIG(*)
FROM dbo.copilot_interactions AS i
JOIN dbo.copilot_interaction_types AS t ON t.id = i.interaction_type_id
WHERE t.name = N'userPrompt' AND i.created_utc >= @from AND i.created_utc < @to
GROUP BY i.user_id;";
                    command.Parameters.AddWithValue("@from", from);
                    command.Parameters.AddWithValue("@to", toExclusive);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            totals[reader.GetInt32(0)] = (reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3), 0L);
                        reader.NextResult();
                        while (reader.Read())
                        {
                            totals.TryGetValue(reader.GetInt32(0), out var row);
                            totals[reader.GetInt32(0)] = (row.Turns, row.Estimate, row.Priced, reader.GetInt64(1));
                        }
                    }
                }
            }
            return totals;
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
