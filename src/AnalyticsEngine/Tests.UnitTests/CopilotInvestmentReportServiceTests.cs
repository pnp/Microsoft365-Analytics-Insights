extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using CopilotInvestmentFactsCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentFactsCache;
using CopilotInvestmentReportService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentReportService;
using CopilotInvestmentRequest = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotInvestment.CopilotInvestmentRequest;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using ReportScope = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScope;

namespace Tests.UnitTests
{
    /// <summary>
    /// The investment report service between the fact sources and the controller. The days it reads from its day-grained
    /// sources: a rolling window runs to now and includes today; a window with explicit dates ends on its chosen day, so the
    /// sweep and the Studio charges of the day after are not its own. And how it shares what it builds: one build at a time per
    /// summary, no failure kept, and no row per person in the summary every reader shares.
    /// </summary>
    [TestClass]
    public class CopilotInvestmentReportServiceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 27, 10, 15, 0, DateTimeKind.Utc);

        [TestMethod]
        public async Task AWindowWithExplicitDates_ReadsAndShowsItsOwnLastDay_NeverTheDayAfter()
        {
            var source = new RecordingFactsSource();
            var range = CopilotAdoptionDateRange.Create(28, "2026-09-29", "2026-10-26", Now.AddDays(1));

            var summary = await NewService(source).BuildAsync(new CopilotInvestmentRequest { Range = range, Scope = ReportScope.Unrestricted },
                null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { (new DateTime(2026, 9, 29), new DateTime(2026, 10, 26)) }, source.Credits, "Balances up to and including the 26th.");
            CollectionAssert.AreEqual(new[] { (new DateTime(2026, 9, 29), new DateTime(2026, 10, 27)) }, source.Studio, "Studio charges before the 27th.");
            Assert.AreEqual(new DateTime(2026, 10, 26), summary.ToUtc, "The window says it ends on the day it ends.");
            Assert.AreEqual(28, summary.WindowDays);
        }

        [TestMethod]
        public async Task ARollingWindow_RunsToNow_SoTodaysSweepAndChargesAreItsOwn()
        {
            var source = new RecordingFactsSource();
            var range = CopilotAdoptionDateRange.Create(28, null, null, Now);

            var summary = await NewService(source).BuildAsync(new CopilotInvestmentRequest { Range = range, Scope = ReportScope.Unrestricted },
                null, CancellationToken.None);

            Assert.AreEqual(Now.Date, source.Credits.Single().To, "Today's sweep is the newest a rolling window can have.");
            Assert.AreEqual(Now.Date.AddDays(1), source.Studio.Single().To, "Today's Studio charges are in the window.");
            Assert.AreEqual(Now, summary.ToUtc);
            Assert.AreEqual(Now.Date, CopilotInvestmentWindow.LastDay(range));
        }

        /// <summary>
        /// Building the summary is the report's costly step, and every page load and filter change asks for it. A request that
        /// arrives while one is being built takes that one; it does not run a second, and one that arrives afterwards (inside
        /// the cache's two minutes) is given the finished summary.
        /// </summary>
        [TestMethod]
        public async Task ARequestThatArrivesWhileTheSummaryIsBeingBuilt_WaitsForThatBuild_InsteadOfRunningItsOwn()
        {
            var builds = 0;
            var firstIsBuilding = new ManualResetEventSlim();
            var letTheFirstFinish = new ManualResetEventSlim();
            var anotherBuildStarted = new ManualResetEventSlim();
            // Read once by every build, after the cache has been checked: it counts the builds and is where the first is held.
            bool CreditCollection()
            {
                if (Interlocked.Increment(ref builds) > 1)
                {
                    anotherBuildStarted.Set();
                }
                else
                {
                    firstIsBuilding.Set();
                    letTheFirstFinish.Wait(TimeSpan.FromSeconds(30));
                }
                return true;
            }

            var service = NewService(new RecordingFactsSource(), CreditCollection);
            try
            {
                var first = Task.Run(() => service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false));
                Assert.IsTrue(firstIsBuilding.Wait(TimeSpan.FromSeconds(30)), "The first request never reached its build.");
                var second = Task.Run(() => service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false));

                Assert.IsFalse(anotherBuildStarted.Wait(TimeSpan.FromSeconds(1)), "The second request started a build of its own.");
                Assert.IsFalse(second.IsCompleted, "The second request is waiting for the first build, not finished without it.");

                letTheFirstFinish.Set();
                var shared = await Task.WhenAll(first, second);
                Assert.AreEqual(1, builds);
                Assert.AreSame(shared[0].Organisation, shared[1].Organisation, "Both were given the one summary.");

                var later = await service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false);
                Assert.AreEqual(1, builds, "A request after the build is served from the cache.");
                Assert.AreSame(shared[0].Organisation, later.Organisation);
            }
            finally
            {
                letTheFirstFinish.Set();
            }
        }

        /// <summary>
        /// The entry that makes requests share a build must not outlive a build that failed: the requests waiting on it are given
        /// the failure (not left waiting for a summary that is never coming), and the next request builds again.
        /// </summary>
        [TestMethod]
        public async Task AFailedBuild_FailsTheRequestsWaitingOnIt_AndIsNotKept_SoTheNextRequestBuildsAgain()
        {
            var builds = 0;
            var failing = 1;
            var firstIsBuilding = new ManualResetEventSlim();
            var letTheFirstFail = new ManualResetEventSlim();
            var anotherBuildStarted = new ManualResetEventSlim();
            bool CreditCollection()
            {
                if (Interlocked.Increment(ref builds) == 1)
                {
                    firstIsBuilding.Set();
                    letTheFirstFail.Wait(TimeSpan.FromSeconds(30));
                }
                else
                {
                    anotherBuildStarted.Set();
                }
                if (Volatile.Read(ref failing) == 1) throw new InvalidOperationException("Synthetic build failure.");
                return true;
            }

            var service = NewService(new RecordingFactsSource(), CreditCollection);
            try
            {
                var first = Task.Run(() => service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false));
                Assert.IsTrue(firstIsBuilding.Wait(TimeSpan.FromSeconds(30)), "The first request never reached its build.");
                var waiting = Task.Run(() => service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false));
                // It must be waiting on the first build. A request that built for itself would fail at once, the build
                // failing for everyone, and so look just like one that was given the first's failure afterwards.
                Assert.IsFalse(anotherBuildStarted.Wait(TimeSpan.FromSeconds(1)), "The second request started a build of its own instead of waiting on the first.");
                Assert.IsFalse(waiting.IsCompleted, "The second request is waiting on the first build, not finished without it.");
                letTheFirstFail.Set();

                foreach (var request in new[] { first, waiting })
                {
                    Assert.AreSame(request, await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(10))), "A request was left waiting on a build that failed.");
                    var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => request);
                    Assert.AreEqual("Synthetic build failure.", failure.Message);
                }

                Interlocked.Exchange(ref failing, 0);
                var buildsSoFar = Volatile.Read(ref builds);
                var recovered = await service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false);
                Assert.IsNotNull(recovered);
                Assert.AreEqual(buildsSoFar + 1, builds, "A new build, not the failure replayed from the cache.");
            }
            finally
            {
                letTheFirstFail.Set();
            }
        }

        /// <summary>
        /// The summary every reader shares is kept for two minutes, and a row per person is the larger part of it at 200,000
        /// users. Only the people views need those rows; a summary built without them has every figure the one with them has.
        /// </summary>
        [TestMethod]
        public async Task TheSummaryEveryoneShares_HoldsNoRowPerPerson_WhileAPeopleBuildIsGivenThem_WithTheSameFigures()
        {
            var service = NewService(new RecordingFactsSource { Balances = Balances(1, 2, 3, 4, 5, 6) });

            var aggregates = await service.BuildAsync(Request(), null, CancellationToken.None, withPeople: false);
            var withPeople = await service.BuildAsync(Request(), null, CancellationToken.None, withPeople: true);
            var unspecified = await service.BuildAsync(Request(), null, CancellationToken.None);

            Assert.AreEqual(0, aggregates.People.Count, "No row per person in what readers share.");
            Assert.AreEqual(6, withPeople.People.Count, "The people views are given every row.");
            Assert.AreEqual(6, unspecified.People.Count, "A caller that does not say is given the rows, as it always was.");
            Assert.AreEqual(JsonConvert.SerializeObject(withPeople.Sources), JsonConvert.SerializeObject(aggregates.Sources));
            Assert.AreEqual(JsonConvert.SerializeObject(withPeople.Investment), JsonConvert.SerializeObject(aggregates.Investment));
            Assert.AreEqual(JsonConvert.SerializeObject(withPeople.Organisation), JsonConvert.SerializeObject(aggregates.Organisation));
            Assert.AreEqual(JsonConvert.SerializeObject(withPeople.Concentration), JsonConvert.SerializeObject(aggregates.Concentration));
            Assert.AreEqual(6, aggregates.Sources.Credits.ObservedUsers, "The control: the figures are about the six people, though the summary holds none of their rows.");
        }

        private static CopilotInvestmentRequest Request() =>
            new CopilotInvestmentRequest { Range = CopilotAdoptionDateRange.Create(28, null, null, Now), Scope = ReportScope.Unrestricted };

        private static CreditFacts Balances(params int[] users)
        {
            var credits = new CreditFacts { TablesPresent = true, SnapshotDate = Now.Date, SnapshotBandFrom = Now.Date.AddDays(-2) };
            foreach (var id in users)
                credits.Users.Add(new CreditUserFact
                {
                    ObjectId = new Guid(id, 0, 0, new byte[8]), UserId = id, ObservationDate = Now.Date, Quality = 2, PeriodStatus = "unverified",
                    ServiceRows = 1, CreditRows = 1, Credits = 10 * id,
                });
            return credits;
        }

        private static CopilotInvestmentReportService NewService(RecordingFactsSource source, Func<bool> creditCollectionEnabled = null)
        {
            var name = Guid.NewGuid().ToString("N");
            return new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(source, new MemoryCache("investment-facts-" + name)),
                () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true),
                new NoDirectory(),
                () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true },
                creditCollectionEnabled ?? (() => true),
                new MemoryCache("investment-results-" + name));
        }

        private sealed class RecordingFactsSource : ICopilotInvestmentFactsSource
        {
            public List<(DateTime From, DateTime To)> Credits { get; } = new List<(DateTime, DateTime)>();
            public List<(DateTime From, DateTime To)> Studio { get; } = new List<(DateTime, DateTime)>();

            /// <summary>What the balances read returns; nothing, unless a test sets it.</summary>
            public CreditFacts Balances { get; set; } = new CreditFacts();

            public Task<CreditFacts> GetCreditFactsAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
            {
                lock (Credits) Credits.Add((from, to));
                return Task.FromResult(Balances);
            }

            public Task<StudioFacts> GetStudioFactsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
            {
                lock (Studio) Studio.Add((from, toExclusive));
                return Task.FromResult(new StudioFacts());
            }

            public Task<List<CreditTrendPoint>> GetCreditTrendAsync(IReadOnlyList<DateTime> dates, ReportUserScope scope, CancellationToken cancellationToken) =>
                Task.FromResult(new List<CreditTrendPoint>());

            public Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from, DateTime toExclusive,
                DateTime lastDay, bool withCreditEstimate, bool withPromptCategories, CancellationToken cancellationToken) =>
                Task.FromResult(new PersonDetailFacts());
        }

        private sealed class NoDirectory : IUserDirectorySource
        {
            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult<UserDirectorySnapshot>(null);
            public void Prefetch() { }
            public void Invalidate() { }
        }
    }
}
