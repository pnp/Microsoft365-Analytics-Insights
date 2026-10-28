extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.Localisation;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using CopilotInvestmentFactsCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentFactsCache;
using CopilotInvestmentReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentReportService;
using CopilotInvestmentRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentRequest;
using CopilotSpendAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotSpendAPIController;
using CopilotSpendReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendReportService;
using CopilotSpendRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendRequest;
using GlobalFilterApplication = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterApplication;
using GlobalFilterProviders = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterProviders;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using PortalAccessPolicy = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalAccessPolicy;
using PortalRoles = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalRoles;
using ReportScope = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScope;
using ReportScopeResolver = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScopeResolver;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot Spend and investment services between the fact sources and the controllers: what is cached is the same for
    /// every reader who covers the same people, and what differs by permission (the administrator's filter wording, people's
    /// names) is attached per request; groupings that name people show every group, and nothing beyond the page's top groups
    /// is lost from the export. Synthetic Contoso data throughout.
    /// </summary>
    [TestClass]
    public class CopilotSpendServiceTests
    {
        private const string Boss = "boss.person@contoso.example";
        private static readonly DateTime Now = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Snapshot = new DateTime(2026, 10, 26);

        /// <summary>A directory of six reports to one manager, and the manager.</summary>
        private static UserDirectorySnapshot Directory()
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 100, UserPrincipalName = Boss, Department = "Contoso Leadership", AccountEnabled = true });
            for (var id = 1; id <= 6; id++)
            {
                builder.AddUser(new UserDirectoryEntry
                {
                    UserId = id,
                    UserPrincipalName = $"person{id}@contoso.example",
                    Department = "\u0391\u03b8\u03ae\u03bd\u03b1 Sales",
                    ManagerUserId = 100,
                    AccountEnabled = true,
                });
            }
            return builder.Build(Now);
        }

        /// <summary>
        /// The administrator's filter "manager is Boss", resolved for a reader with or without See PII exactly as the resolver
        /// does it: the same people, so the same cache key, but the echo and description withhold the name from the second.
        /// </summary>
        private static ReportScope ManagerFilterScope(UserDirectorySnapshot snapshot, bool seePii)
        {
            var resolved = GlobalFilterResolver.Resolve(GlobalFilterCodec.Parse("[{\"d\":\"manager\",\"v\":[\"" + Boss + "\"]}]"), snapshot, null);
            var compiled = UserFilterCompiler.Compile(resolved.Expression, snapshot);
            var echo = GlobalFilterEcho.From(resolved, compiled, snapshot);
            var global = new GlobalFilterApplication
            {
                Defined = true,
                SeesIndividuals = seePii,
                Resolved = resolved,
                Compiled = compiled,
                Echo = seePii ? echo : echo.WithoutPeople(),
            };
            return new ReportScope(global, compiled, null, "d:" + snapshot.LoadedUtc.Ticks + "|g:" + resolved.Key + "|u:");
        }

        private static CreditFacts Credits(params int[] users)
        {
            var credits = new CreditFacts { TablesPresent = true, SnapshotDate = Snapshot, SnapshotBandFrom = Snapshot.AddDays(-2) };
            foreach (var id in users)
                credits.Users.Add(new CreditUserFact
                {
                    ObjectId = new Guid(id, 0, 0, new byte[8]), UserId = id, ObservationDate = Snapshot, Quality = 2, PeriodStatus = "unverified",
                    ServiceRows = 1, CreditRows = 1, Credits = 10 * id,
                });
            return credits;
        }

        private static StudioFacts Studio(params int[] users)
        {
            var studio = new StudioFacts { HasData = users.Length > 0, EarliestDate = Snapshot.AddDays(-90), LatestDate = Snapshot };
            foreach (var id in users) studio.Users.Add(new StudioUserFact { UserId = id, Credits = id, ActiveDays = 1 });
            return studio;
        }

        private static CopilotSpendReportService SpendService(UserDirectorySnapshot directory, FakeFacts facts, FakeSpend spend = null)
        {
            var name = Guid.NewGuid().ToString("N");
            return new CopilotSpendReportService(new CopilotInvestmentFactsCache(facts, new MemoryCache("spend-facts-" + name)), spend ?? new FakeSpend(),
                new FixedDirectory(directory), () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                new MemoryCache("spend-results-" + name));
        }

        private static CopilotAdoptionDateRange Range(int days = 28) => CopilotAdoptionDateRange.Create(days, null, null, Now);

        private static List<string> WorkbookTexts(byte[] workbook) =>
            CopilotInvestmentWorkbookLanguageTests.Texts(CopilotInvestmentWorkbookLanguageTests.Sheets(workbook));

        [TestMethod]
        public async Task TheSpendSummary_CarriesEachReadersOwnFilterEcho_WhicheverReaderFillsTheCache()
        {
            var directory = Directory();
            var withNames = ManagerFilterScope(directory, seePii: true);
            var withoutNames = ManagerFilterScope(directory, seePii: false);
            Assert.AreEqual(withNames.Key, withoutNames.Key, "The control: both readers cover the same people, so they share the cache.");

            foreach (var piiFirst in new[] { true, false })
            {
                var service = SpendService(directory, new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)));
                CopilotSpendSummary named = null, anonymous = null;
                if (piiFirst) named = await service.BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = withNames }, CancellationToken.None);
                anonymous = await service.BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = withoutNames }, CancellationToken.None);
                if (!piiFirst) named = await service.BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = withNames }, CancellationToken.None);

                // The reader without See PII: no name in the echo, the page's JSON or the workbook, whoever filled the cache.
                var forPage = anonymous.WithoutIndividualData();
                Assert.AreEqual(0, forPage.GlobalFilter.Clauses.Single().Values.Count, "piiFirst: " + piiFirst);
                Assert.AreEqual(1, forPage.GlobalFilter.Clauses.Single().HiddenValues);
                StringAssert.DoesNotMatch(JsonConvert.SerializeObject(forPage), new System.Text.RegularExpressions.Regex("boss\\.person"));
                Assert.IsFalse(WorkbookTexts(CopilotSpendWorkbook.Build(forPage, false, "test", PortalLanguage.English)).Any(t => t.Contains(Boss)));

                // The reader with See PII still sees the name they are entitled to, in the echo and the workbook.
                CollectionAssert.AreEqual(new[] { Boss }, named.GlobalFilter.Clauses.Single().Values);
                Assert.IsTrue(WorkbookTexts(CopilotSpendWorkbook.Build(named, true, "test", PortalLanguage.English)).Any(t => t.Contains(Boss)));
                Assert.AreEqual(named.Studio.Credits, anonymous.Studio.Credits, "The figures are the shared, cached ones.");

                // And a third read from the cache is still right for each: the cached object never took either echo.
                var again = await service.BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = withoutNames }, CancellationToken.None);
                Assert.AreEqual(0, again.GlobalFilter.Clauses.Single().Values.Count);
                var againNamed = await service.BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = withNames }, CancellationToken.None);
                CollectionAssert.AreEqual(new[] { Boss }, againNamed.GlobalFilter.Clauses.Single().Values);
            }
        }

        [TestMethod]
        public async Task TheInvestmentSummary_CarriesEachReadersOwnFilterEchoAndDescription_WhicheverReaderFillsTheCache()
        {
            var directory = Directory();
            var withNames = ManagerFilterScope(directory, seePii: true);
            var withoutNames = ManagerFilterScope(directory, seePii: false);

            foreach (var piiFirst in new[] { true, false })
            {
                var name = Guid.NewGuid().ToString("N");
                var service = new CopilotInvestmentReportService(
                    new CopilotInvestmentFactsCache(new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)), new MemoryCache("investment-facts-" + name)),
                    () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true), new FixedDirectory(directory),
                    () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                    new MemoryCache("investment-results-" + name));
                CopilotInvestmentSummary named = null, anonymous = null;
                if (piiFirst) named = await service.BuildAsync(new CopilotInvestmentRequest { Range = Range(), Scope = withNames }, null, CancellationToken.None);
                anonymous = await service.BuildAsync(new CopilotInvestmentRequest { Range = Range(), Scope = withoutNames }, null, CancellationToken.None);
                if (!piiFirst) named = await service.BuildAsync(new CopilotInvestmentRequest { Range = Range(), Scope = withNames }, null, CancellationToken.None);

                var forPage = anonymous.WithoutIndividualData();
                Assert.AreEqual(0, forPage.GlobalFilter.Clauses.Single().Values.Count, "piiFirst: " + piiFirst);
                Assert.IsFalse(forPage.GlobalFilterDescription.Contains(Boss), forPage.GlobalFilterDescription);
                StringAssert.DoesNotMatch(JsonConvert.SerializeObject(forPage), new System.Text.RegularExpressions.Regex("boss\\.person"));
                Assert.IsFalse(WorkbookTexts(CopilotInvestmentWorkbook.Build(forPage, false, "test", PortalLanguage.English)).Any(t => t.Contains(Boss)));

                CollectionAssert.AreEqual(new[] { Boss }, named.GlobalFilter.Clauses.Single().Values);
                StringAssert.Contains(named.GlobalFilterDescription, Boss);
            }
        }

        [TestMethod]
        public void AManagerGrouping_ShowsEveryManager_AndNoGroupIsLostBeyondThePagesTop100()
        {
            // 104 managers with six consuming reports each, and one with a single consuming report.
            var people = Enumerable.Range(1, 104 * 6 + 1).ToArray();
            string ManagerOf(int id) => id <= 104 * 6 ? $"manager{(id - 1) / 6:D3}@contoso.example" : "solo.manager@contoso.example";
            var input = new CopilotSpendInput
            {
                FromUtc = Snapshot.AddDays(-27), ToUtc = Snapshot, LastDay = Snapshot, WindowDays = 28,
                Directory = id => new CopilotInvestmentDirectoryEntry { UserPrincipalName = $"person{id}@contoso.example", Manager = ManagerOf(id) },
                Dimension = "manager",
                GroupOf = ManagerOf,
                Credits = Credits(people),
                Studio = Studio(people),
                StudioBreakdown = new CopilotSpendStudioBreakdown(),
                StudioImportEnabled = true,
                CreditCollectionEnabled = true,
            };
            var summary = CopilotSpendCalculator.Calculate(input);
            var organisation = summary.Organisation;

            Assert.AreEqual(1, organisation.MinGroupSize, "Only a reader with See PII is served a manager grouping, so no manager is folded away.");
            Assert.AreEqual(0, organisation.FoldedGroups);
            Assert.AreEqual(105, organisation.AllGroups.Count, "The export lists every manager, the one with a single consumer included.");
            Assert.IsTrue(organisation.AllGroups.Any(g => g.Label == "solo.manager@contoso.example" && g.People == 1 && !g.Withheld));
            Assert.AreEqual(101, organisation.Groups.Count, "The page lists the top 100 and one labelled row for the rest.");
            var more = organisation.Groups.Last();
            Assert.AreEqual(CopilotSpendGroupKeys.MoreGroups, more.EmptyKey);
            Assert.AreEqual(5, more.Folded);
            Assert.AreEqual(5, organisation.MoreGroups);
            Assert.IsFalse(more.Withheld, "These are large groups, not small ones.");

            // Totals reconcile on the page and in the export.
            var total = summary.People.Sum(p => p.StudioCredits ?? 0);
            Assert.AreEqual(total, organisation.Groups.Sum(g => g.StudioCredits ?? 0), 1e-6);
            Assert.AreEqual(total, organisation.AllGroups.Sum(g => g.StudioCredits ?? 0), 1e-6);
            Assert.AreEqual(summary.People.Sum(p => p.BalanceCredits ?? 0), organisation.AllGroups.Sum(g => g.BalanceCredits ?? 0), 1e-6);

            var sheet = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(summary, true, "test", PortalLanguage.English))[1].Cells;
            var labels = sheet.Where(c => c.Ref != null && c.Ref.StartsWith("A", StringComparison.Ordinal) && char.IsDigit(c.Ref[1])).Select(c => c.Text).ToList();
            foreach (var group in organisation.AllGroups) CollectionAssert.Contains(labels, group.Label);
            Assert.IsFalse(labels.Any(l => l != null && l.StartsWith("(more groups", StringComparison.Ordinal)), "Nothing in the export is folded away for length.");

            // Grouped by a dimension any reader may see, the single-consumer group is folded and shows no figures.
            input.Dimension = "department";
            var byDepartment = CopilotSpendCalculator.Calculate(input).Organisation;
            Assert.AreEqual(5, byDepartment.MinGroupSize);
            var small = byDepartment.AllGroups.Single(g => g.EmptyKey == CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups);
            Assert.IsTrue(small.Withheld && small.StudioCredits == null && small.People == 1);

            // A reader without See PII never receives a manager grouping, from the summary or the workbook path.
            var trimmed = summary.WithoutIndividualData();
            Assert.IsTrue(trimmed.Organisation.Hidden && trimmed.Organisation.Groups.Count == 0 && trimmed.Organisation.AllGroups.Count == 0);
            Assert.AreEqual(105, summary.Organisation.AllGroups.Count, "The cached summary is never edited.");
        }

        [TestMethod]
        public async Task AReaderWithoutSeePii_IsRefusedAManagerGrouping_OnThePageAndInTheWorkbook()
        {
            var directory = Directory();
            var service = SpendService(directory, new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)));
            var controller = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service, seePii: false);

            var page = await (await controller.Summary(28, null, "manager", CancellationToken.None)).ExecuteAsync(CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.Forbidden, page.StatusCode);
            var workbook = await controller.ExportWorkbook(28, null, "manager", "en", CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.Forbidden, workbook.StatusCode);

            // The control: a reader with See PII is served it.
            var entitled = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service, seePii: true);
            var allowed = await entitled.ExportWorkbook(28, null, "manager", "en", CancellationToken.None);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        }

        [TestMethod]
        public async Task WithoutADirectory_OrADimensionItHolds_TheGroupingSaysSo_RatherThanCallingEveryoneNotSet()
        {
            var noDirectory = await SpendService(null, new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)))
                .BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = ReportScope.Unrestricted }, CancellationToken.None);
            Assert.AreEqual(CopilotSpendGroupingUnavailable.Directory, noDirectory.Organisation.UnavailableCode);
            Assert.AreEqual(0, noDirectory.Organisation.Groups.Count);
            Assert.AreEqual(3, noDirectory.Studio.Credits.Value, 1e-9, "The consumption itself is still reported.");

            var unknown = await SpendService(Directory(), new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)))
                .BuildAsync(new CopilotSpendRequest { Range = Range(), Scope = ReportScope.Unrestricted, Dimension = "org:999" }, CancellationToken.None);
            Assert.AreEqual(CopilotSpendGroupingUnavailable.Dimension, unknown.Organisation.UnavailableCode);
            Assert.AreEqual(0, unknown.Organisation.Groups.Count);
            Assert.AreEqual(6, unknown.PeopleCount);

            var texts = WorkbookTexts(CopilotSpendWorkbook.Build(noDirectory, true, "test", PortalLanguage.Spanish));
            CollectionAssert.Contains(texts, PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish)
                .Text("copilotSpend.workbook.org.unavailable.directoryUnavailable"));
        }

        private static CopilotSpendAPIController Controller(ReportScopeResolver resolver, CopilotSpendReportService service, bool seePii)
        {
            var configuration = new HttpConfiguration();
            configuration.Properties[typeof(PortalAccessPolicy)] = PortalAccessPolicy.Enforcing;
            var request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.example/api/CopilotSpend/summary");
            request.SetConfiguration(configuration);
            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim("upn", "reader@contoso.example"));
            if (seePii) identity.AddClaim(new Claim("roles", PortalRoles.SeePii));
            return new CopilotSpendAPIController(resolver, () => service)
            {
                Request = request,
                Configuration = configuration,
                User = new ClaimsPrincipal(identity),
            };
        }

        internal sealed class FakeFacts : ICopilotInvestmentFactsSource
        {
            private readonly CreditFacts _credits;
            private readonly StudioFacts _studio;

            internal FakeFacts(CreditFacts credits, StudioFacts studio)
            {
                _credits = credits;
                _studio = studio;
            }

            public Task<CreditFacts> GetCreditFactsAsync(DateTime from, DateTime to, CancellationToken cancellationToken) => Task.FromResult(_credits);

            public Task<StudioFacts> GetStudioFactsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken) => Task.FromResult(_studio);

            public Task<List<CreditTrendPoint>> GetCreditTrendAsync(IReadOnlyList<DateTime> dates, ReportUserScope scope, CancellationToken cancellationToken) =>
                Task.FromResult(new List<CreditTrendPoint>());

            /// <summary>What a person's drill-down reads; empty unless a test sets it.</summary>
            internal PersonDetailFacts PersonDetail { get; set; } = new PersonDetailFacts();

            /// <summary>The <c>withCreditEstimate</c> of each drill-down read, in order.</summary>
            internal List<bool> PersonDetailEstimateRequests { get; } = new List<bool>();

            public Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from, DateTime toExclusive,
                DateTime lastDay, bool withCreditEstimate, CancellationToken cancellationToken)
            {
                PersonDetailEstimateRequests.Add(withCreditEstimate);
                return Task.FromResult(PersonDetail);
            }
        }

        private sealed class FakeSpend : ICopilotSpendFactsSource
        {
            public Task<CopilotSpendStudioBreakdown> GetStudioBreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope,
                CancellationToken cancellationToken) => Task.FromResult(new CopilotSpendStudioBreakdown { Agents = new List<CopilotSpendAgent>() });

            /// <summary>A person's prompts and Studio days; a test makes it throw to stand for a failed read.</summary>
            internal Func<CopilotSpendPersonFacts> Usage { get; set; } = () => new CopilotSpendPersonFacts();

            internal Func<List<CopilotSpendAgentFact>> Agents { get; set; } = () => new List<CopilotSpendAgentFact>();

            internal List<int[]> AgentRequests { get; } = new List<int[]>();

            public Task<CopilotSpendPersonFacts> GetPersonUsageAsync(int userId, DateTime from, DateTime toExclusive, DateTime lastDay,
                CancellationToken cancellationToken) => Task.FromResult(Usage());

            public Task<List<CopilotSpendAgentFact>> GetAgentsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
            {
                AgentRequests.Add(ids.ToArray());
                return Task.FromResult(Agents());
            }
        }

        internal sealed class FixedDirectory : IUserDirectorySource
        {
            private readonly UserDirectorySnapshot _snapshot;

            internal FixedDirectory(UserDirectorySnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);
            public void Prefetch() { }
            public void Invalidate() { }
        }
    }
}
