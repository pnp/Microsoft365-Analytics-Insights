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
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using CopilotAgentOriginKeys = Common.Entities.Copilot.CopilotAgentOriginKeys;
using CopilotInvestmentAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotInvestmentAPIController;
using CopilotInvestmentSettingsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotInvestmentSettingsAPIController;
using CopilotInvestmentFactsCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentFactsCache;
using CopilotInvestmentReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentReportService;
using CopilotInvestmentRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentRequest;
using CopilotSpendAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotSpendAPIController;
using CopilotSpendReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendReportService;
using CopilotSpendRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotSpend.CopilotSpendRequest;
using GlobalFilterApplication = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterApplication;
using GlobalFilterProviders = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterProviders;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;
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
            using var controller = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service, seePii: false);

            var page = await controller.Client.GetAsync("api/CopilotSpend/summary?windowDays=28&dimension=manager");
            Assert.AreEqual(HttpStatusCode.Forbidden, page.StatusCode);
            var workbook = await controller.Client.GetAsync("api/CopilotSpend/export/workbook?windowDays=28&dimension=manager&language=en");
            Assert.AreEqual(HttpStatusCode.Forbidden, workbook.StatusCode);

            // The control: a reader with See PII is served it.
            using var entitled = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service, seePii: true);
            var allowed = await entitled.Client.GetAsync("api/CopilotSpend/export/workbook?windowDays=28&dimension=manager&language=en");
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

        /// <summary>
        /// Person 1's own facts: turns in Teams with no agent and with agent 11 on the window's second day, in Word with agent 12
        /// on its third, five prompts in Teams, a chat reading and one balance.
        /// </summary>
        private static PersonDetailFacts PersonFacts(CopilotAdoptionDateRange range)
        {
            var day = range.FromUtc.Date;
            return new PersonDetailFacts
            {
                ActivityAssessed = true,
                PromptsAssessed = true,
                Activity =
                {
                    new PersonActivityRow { ActivityDate = day.AddDays(1), AppHost = "Teams", Turns = 4 },
                    new PersonActivityRow { ActivityDate = day.AddDays(1), AppHost = "Teams", AgentId = 11, Turns = 3 },
                    new PersonActivityRow { ActivityDate = day.AddDays(2), AppHost = "Word", AgentId = 12, Turns = 1 },
                },
                Prompts = { new PersonPromptRow { AppClass = "Teams", InteractionType = CopilotSpendSql.PromptInteractionType, Interactions = 5, Days = 2 } },
                CreditServices = { new CreditServiceFact { Category = "chat", Consumed = 10, Unit = CopilotInvestmentSql.CreditUnit, Allowed = true } },
                Balances = { new PersonBalancePoint { Date = Snapshot, Credits = 10, Quality = 2 } },
            };
        }

        [TestMethod]
        public async Task APersonsUsageThatCannotBeRead_LeavesTheRestOfTheirDrillDown_AndReadsAsNotAvailableRatherThanZero()
        {
            var range = Range();
            var facts = new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)) { PersonDetail = PersonFacts(range) };
            var spend = new FakeSpend
            {
                Usage = () => new CopilotSpendPersonFacts
                {
                    StudioAssessed = true,
                    StudioDays = { new CopilotSpendDay { Date = Snapshot, Credits = 1 } },
                    PromptsAssessed = true,
                    PromptTotals = new CopilotSpendPromptTotals { Prompts = 5, Days = 2 },
                    CategoriesAssessed = true,
                    PhrasesAssessed = true,
                    ResourcesAssessed = true,
                },
            };
            var service = SpendService(Directory(), facts, spend);
            var request = new CopilotSpendRequest { Range = range, Scope = ReportScope.Unrestricted };

            // The control: read, the person's usage is all there.
            var read = await service.PersonAsync(request, 1, CancellationToken.None);
            Assert.IsNull(read.UsageFailureCode);
            Assert.IsNotNull(read.StudioDays);
            Assert.AreEqual(5L, read.Usage.Prompts.Prompts);
            Assert.IsTrue(read.Usage.CategoriesAssessed && read.Usage.PhrasesAssessed && read.Usage.ResourcesAssessed);

            spend.Usage = () => throw new InvalidOperationException("Synthetic usage read failure.");
            var detail = await service.PersonAsync(request, 1, CancellationToken.None);

            Assert.IsNotNull(detail, "A failed usage read never takes the rest of the drill-down with it.");
            Assert.AreEqual("personUsageUnavailable", detail.UsageFailureCode);
            Assert.IsNull(detail.StudioDays, "Studio days that were not read are not a run of days with nothing billed.");
            Assert.IsNull(detail.Usage.Prompts, "Prompts that were not read are not zero prompts.");
            Assert.IsFalse(detail.Usage.CategoriesAssessed);
            Assert.IsFalse(detail.Usage.PhrasesAssessed);
            Assert.IsFalse(detail.Usage.ResourcesAssessed);

            // What comes with the person's own facts, which were read, is still there.
            Assert.AreEqual(1, detail.Person.UserId);
            Assert.IsTrue(detail.Usage.TurnsAssessed);
            Assert.AreEqual(8L, detail.Usage.Turns);
            Assert.IsTrue(detail.Usage.PromptsAssessed, "The prompts by app are read with the person's facts, not with their usage.");
            Assert.AreEqual(5L, detail.Usage.PromptApps.Single().Count);
            Assert.AreEqual(10d, detail.Balances.Single().Credits);
            Assert.AreEqual("chat", detail.Services.Single().Category);
            Assert.AreEqual(2, detail.Ranks.StudioRank, "Of the two Studio consumers, person 1 has the smaller amount.");
        }

        [TestMethod]
        public async Task AgentNamesThatCannotBeRead_LeaveThePersonsAgentsListed_Unnamed()
        {
            var range = Range();
            var facts = new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)) { PersonDetail = PersonFacts(range) };
            var spend = new FakeSpend
            {
                Agents = () => new List<CopilotSpendAgentFact>
                {
                    new CopilotSpendAgentFact { Id = 11, AgentId = "contoso-helpdesk", Name = "Contoso helpdesk" },
                    new CopilotSpendAgentFact { Id = 12, AgentId = "contoso-policies", Name = "\u03a0\u03bf\u03bb\u03b9\u03c4\u03b9\u03ba\u03ad\u03c2 Contoso" },
                },
            };
            var service = SpendService(Directory(), facts, spend);
            var request = new CopilotSpendRequest { Range = range, Scope = ReportScope.Unrestricted };

            // The control: the agents the person used are asked for by their stored ids, and named.
            var named = await service.PersonAsync(request, 1, CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { 11, 12 }, spend.AgentRequests.Single());
            CollectionAssert.AreEqual(new[] { "Contoso helpdesk", "\u03a0\u03bf\u03bb\u03b9\u03c4\u03b9\u03ba\u03ad\u03c2 Contoso" },
                named.Usage.Agents.Select(a => a.Name).ToList());

            spend.Agents = () => throw new InvalidOperationException("Synthetic agent read failure.");
            var detail = await service.PersonAsync(request, 1, CancellationToken.None);

            Assert.IsNotNull(detail, "Agent names that cannot be read never take the drill-down with them.");
            Assert.AreEqual(2, spend.AgentRequests.Count);
            CollectionAssert.AreEqual(new[] { 11, 12 }, detail.Usage.Agents.Select(a => a.Id).ToList(), "The agents are still listed.");
            CollectionAssert.AreEqual(new long[] { 3, 1 }, detail.Usage.Agents.Select(a => a.Turns).ToList());
            foreach (var agent in detail.Usage.Agents)
            {
                Assert.IsNull(agent.Name);
                Assert.IsNull(agent.AgentId);
                Assert.AreEqual(CopilotSpendAgentKindKeys.Unknown, agent.KindKey);
                Assert.AreEqual(CopilotAgentOriginKeys.Unknown, agent.OriginKey);
            }
            Assert.IsNull(detail.UsageFailureCode, "Unnamed agents are not a usage read that failed.");
        }

        [TestMethod]
        public async Task ADrillDownForSomeoneOutsideTheReadersView_IsNotFound_SoItIsNeverAWayRoundTheFilter()
        {
            var directory = Directory();
            var service = SpendService(directory, new FakeFacts(Credits(1, 2, 3, 4, 5, 6, 100), Studio(1, 2)));
            using var controller = Controller(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service, seePii: true);
            const string bossesReports = "[{\"d\":\"manager\",\"v\":[\"" + Boss + "\"]}]";

            Task<HttpResponseMessage> Read(int userId, string userFilter = null) =>
                controller.Client.GetAsync("api/CopilotSpend/person/" + userId + "?windowDays=28&userFilter=" + Uri.EscapeDataString(userFilter ?? string.Empty));

            // The controls: the manager is in the whole report, and one of their reports is in the filtered one.
            Assert.AreEqual(HttpStatusCode.OK, (await Read(100)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await Read(1, bossesReports)).StatusCode);

            foreach (var refused in new[] { await Read(100, bossesReports), await Read(99) })
            {
                Assert.AreEqual(HttpStatusCode.NotFound, refused.StatusCode);
                Assert.AreEqual("personNotFound", (string)JObject.Parse(await refused.Content.ReadAsStringAsync())["code"]);
            }
        }

        [TestMethod]
        public async Task TheSpendDrillDown_DoesNotAskForTheInvestmentGoalCounts_WhichTheInvestmentDrillDownStillReads()
        {
            var directory = Directory();
            var facts = new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2));

            var spend = await SpendService(directory, facts)
                .PersonAsync(new CopilotSpendRequest { Range = Range(), Scope = ReportScope.Unrestricted }, 1, CancellationToken.None);
            Assert.IsNotNull(spend);
            CollectionAssert.AreEqual(new[] { false }, facts.PersonDetailCategoryRequests, "The Spend drill-down reads goals with the person's usage.");
            CollectionAssert.AreEqual(new[] { true }, facts.PersonDetailEstimateRequests);

            var name = Guid.NewGuid().ToString("N");
            var investment = new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(facts, new MemoryCache("investment-facts-" + name)),
                () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true), new FixedDirectory(directory),
                () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                new MemoryCache("investment-results-" + name));
            using var host = InvestmentController(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), investment);
            var response = await host.Client.GetAsync("api/CopilotInvestment/person/1?windowDays=28");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { false, true }, facts.PersonDetailCategoryRequests, "The investment drill-down still shows the goal counts.");
            CollectionAssert.AreEqual(new[] { true, false }, facts.PersonDetailEstimateRequests);
        }

        /// <summary>
        /// What readers share is the summary without a row per person (see the report service), so the readers that need the rows -
        /// the people list and the workbook that holds the people sheet - must never be served from it, whichever is asked for first.
        /// A reader without See PII still gets the shared summary, and no one's name.
        /// </summary>
        [TestMethod]
        public async Task TheInvestmentPeopleList_AndThePeopleSheet_AreBuiltWithTheirRows_EvenWhenTheSharedSummaryWasBuiltFirst()
        {
            var directory = Directory();
            var scopes = new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory));
            var name = Guid.NewGuid().ToString("N");
            var service = new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(new FakeFacts(Credits(1, 2, 3, 4, 5, 6), Studio(1, 2)), new MemoryCache("investment-facts-" + name)),
                () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true), new FixedDirectory(directory),
                () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                new MemoryCache("investment-results-" + name));
            using var reader = InvestmentController(scopes, service);

            // The summary first: it is the request that leaves a summary without rows in the cache.
            var summary = await reader.Client.GetAsync("api/CopilotInvestment/summary?windowDays=28");
            Assert.AreEqual(HttpStatusCode.OK, summary.StatusCode, await summary.Content.ReadAsStringAsync());

            var people = await reader.Client.GetAsync("api/CopilotInvestment/people");
            Assert.AreEqual(HttpStatusCode.OK, people.StatusCode, await people.Content.ReadAsStringAsync());
            Assert.AreEqual(6, (int)JObject.Parse(await people.Content.ReadAsStringAsync())["total"], "Every person the report is about.");

            var workbook = await reader.Client.GetAsync("api/CopilotInvestment/export/workbook?windowDays=28&language=en");
            Assert.AreEqual(HttpStatusCode.OK, workbook.StatusCode);
            Assert.AreEqual(CopilotInvestmentWorkbook.ContentType, workbook.Content.Headers.ContentType.MediaType);
            Assert.AreEqual("attachment", workbook.Content.Headers.ContentDisposition.DispositionType);
            Assert.IsTrue(workbook.Headers.CacheControl.NoStore && workbook.Headers.CacheControl.Private);
            CollectionAssert.Contains(WorkbookTexts(await workbook.Content.ReadAsByteArrayAsync()), "person3@contoso.example",
                "A reader with See PII is given the people sheet.");

            // The control: a reader without See PII is given the same report and no one's name, from the same shared summary.
            using var plainReader = InvestmentController(scopes, service, seePii: false);
            var plain = await plainReader.Client.GetAsync("api/CopilotInvestment/export/workbook?windowDays=28&language=en");
            Assert.AreEqual(HttpStatusCode.OK, plain.StatusCode);
            var plainTexts = WorkbookTexts(await plain.Content.ReadAsByteArrayAsync());
            Assert.IsTrue(plainTexts.Count > 10, "The control is a real workbook.");
            Assert.IsFalse(plainTexts.Any(t => t != null && t.Contains("@contoso.example")), "No one's name leaves in a workbook for a reader without See PII.");
        }

        [TestMethod]
        public async Task InvestmentCoreQuery_UsesTheLastRepeatedWhatIf_AndLocalisesWorkbookErrors()
        {
            var directory = Directory();
            var name = Guid.NewGuid().ToString("N");
            var service = new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(new FakeFacts(Credits(1), Studio(1)), new MemoryCache("investment-facts-" + name)),
                () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true), new FixedDirectory(directory),
                () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true }, () => true,
                new MemoryCache("investment-results-" + name));
            using var reader = InvestmentController(new ReportScopeResolver(GlobalFilterProviders.None, new FixedDirectory(directory)), service);

            var response = await reader.Client.GetAsync("api/CopilotInvestment/summary?windowDays=7&currency=USD&seatPrice=12&SEATPRICE=24.5");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(7, (int)body["windowDays"]);
            Assert.IsTrue((bool)body["assumptions"]["scenarioApplied"]);
            Assert.AreEqual(24.5d, (double)body["assumptions"]["values"]["seatPricePerMonth"]);

            var invalid = await reader.Client.GetAsync("api/CopilotInvestment/summary?seatPrice=not-a-number");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            var error = JObject.Parse(await invalid.Content.ReadAsStringAsync());
            Assert.AreEqual(CopilotInvestmentErrorCodes.ScenarioInvalid, (string)error["code"]);
            Assert.AreEqual("seatPrice", (string)error["validationErrors"][0]);

            var workbook = await reader.Client.GetAsync("api/CopilotInvestment/export/workbook?seatPrice=not-a-number&language=es");
            Assert.AreEqual(HttpStatusCode.BadRequest, workbook.StatusCode);
            Assert.AreEqual("text/plain", workbook.Content.Headers.ContentType.MediaType);
            Assert.AreEqual("utf-8", workbook.Content.Headers.ContentType.CharSet);
            Assert.AreEqual(CopilotInvestmentAPIController.WhatIfWorkbookError(new CopilotInvestmentAssumptionsRejectedException(
                CopilotInvestmentErrorCodes.ScenarioInvalid, new[] { "seatPrice" }), PortalLanguage.Spanish).Content,
                await workbook.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task BothCoreReports_SendGlobalScopeRefusals_InsteadOfGenericErrorsOrSerialisedResponses()
        {
            var directory = Directory();
            var scopes = new ReportScopeResolver(GlobalFilterProviders.Fixed(new GlobalFilterRecord
            {
                StorageAvailable = true, FilterJson = "not-json",
            }), new FixedDirectory(directory));
            var service = SpendService(directory, new FakeFacts(Credits(1), Studio(1)));
            using var spend = Controller(scopes, service, seePii: true);
            using var investment = InvestmentController(scopes, null);
            foreach (var (host, route) in new[] { (spend, "CopilotSpend"), (investment, "CopilotInvestment") })
            foreach (var action in new[] { "summary", "export/workbook" })
            {
                var response = await host.Client.GetAsync("api/" + route + "/" + action);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.AreEqual("globalFilterInvalid", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
                Assert.IsTrue(response.Headers.CacheControl.NoStore && response.Headers.CacheControl.Private);
            }
        }

        [TestMethod]
        public async Task InvestmentSettings_CoreBindingRequiresSameOriginAndPreservesVersionConflicts()
        {
            var store = new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true);
            using var host = new PortalTestHost(new[] { typeof(CopilotInvestmentSettingsAPIController) },
                PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing,
                _ => new CopilotInvestmentSettingsAPIController(() => store));
            const string route = "api/CopilotInvestmentSettings";
            var json = JsonConvert.SerializeObject(new { expectedVersion = 0, assumptions = CopilotInvestmentCalculatorTests.Assumptions() });
            Task<HttpResponseMessage> Save() => host.Client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.Forbidden, (await Save()).StatusCode);
            Assert.AreEqual(0L, (await store.GetAsync()).Version);
            host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Save()).StatusCode);
            host.Client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
            host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            var saved = await Save();
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            Assert.AreEqual(1L, (long)JObject.Parse(await saved.Content.ReadAsStringAsync())["version"]);
            var stale = await Save();
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.AreEqual(CopilotInvestmentErrorCodes.VersionConflict, (string)JObject.Parse(await stale.Content.ReadAsStringAsync())["code"]);

            var malformed = await host.Client.PostAsync(route, new StringContent("{\"expectedVersion\":\"invalid\"}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.AreEqual(CopilotInvestmentErrorCodes.InvalidRequest, (string)JObject.Parse(await malformed.Content.ReadAsStringAsync())["code"]);
        }

        private static PortalTestHost InvestmentController(ReportScopeResolver resolver, CopilotInvestmentReportService service,
            bool seePii = true)
        {
            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim("upn", "reader@contoso.example"));
            if (seePii) identity.AddClaim(new Claim("roles", PortalRoles.SeePii));
            // No adoption evidence on this deployment, so the analysis coordinator is never asked.
            var coordinator = new AdoptionCoordinator(new NoAdoption(), new NoAdoption(), (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10));
            return new PortalTestHost(new[] { typeof(CopilotInvestmentAPIController) }, new ClaimsPrincipal(identity), PortalAccessPolicy.Enforcing,
                _ => new CopilotInvestmentAPIController(coordinator, resolver, () => service));
        }

        private sealed class NoAdoption : AdoptionRunner, AdoptionCache
        {
            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates,
                List<int> seatLicenceTypeIds, CopilotAdoptionEffectiveScoreSettings scoreSettings, ICopilotAdoptionRunTelemetry telemetry) =>
                throw new AssertFailedException("No adoption analysis is run on a deployment without adoption evidence.");

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis) =>
                throw new AssertFailedException("No adoption analysis is read on a deployment without adoption evidence.");

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl) =>
                throw new AssertFailedException("No adoption analysis is stored on a deployment without adoption evidence.");
        }

        private static PortalTestHost Controller(ReportScopeResolver resolver, CopilotSpendReportService service, bool seePii)
        {
            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim("upn", "reader@contoso.example"));
            if (seePii) identity.AddClaim(new Claim("roles", PortalRoles.SeePii));
            return new PortalTestHost(new[] { typeof(CopilotSpendAPIController) }, new ClaimsPrincipal(identity), PortalAccessPolicy.Enforcing,
                _ => new CopilotSpendAPIController(resolver, () => service));
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

            /// <summary>The <c>withPromptCategories</c> of each drill-down read, in order.</summary>
            internal List<bool> PersonDetailCategoryRequests { get; } = new List<bool>();

            public Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from, DateTime toExclusive,
                DateTime lastDay, bool withCreditEstimate, bool withPromptCategories, CancellationToken cancellationToken)
            {
                lock (PersonDetailEstimateRequests)
                {
                    PersonDetailEstimateRequests.Add(withCreditEstimate);
                    PersonDetailCategoryRequests.Add(withPromptCategories);
                }
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
