extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    /// The days the report reads from its day-grained sources. A rolling window runs to now and includes today; a window
    /// with explicit dates ends on its chosen day, so the sweep and the Studio charges of the day after are not its own.
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

        private static CopilotInvestmentReportService NewService(RecordingFactsSource source)
        {
            var name = Guid.NewGuid().ToString("N");
            return new CopilotInvestmentReportService(
                new CopilotInvestmentFactsCache(source, new MemoryCache("investment-facts-" + name)),
                () => new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true),
                new NoDirectory(),
                () => new ImportTaskSettings { CopilotStudioCredits = true, CopilotCostManagement = true },
                () => true,
                new MemoryCache("investment-results-" + name));
        }

        private sealed class RecordingFactsSource : ICopilotInvestmentFactsSource
        {
            public List<(DateTime From, DateTime To)> Credits { get; } = new List<(DateTime, DateTime)>();
            public List<(DateTime From, DateTime To)> Studio { get; } = new List<(DateTime, DateTime)>();

            public Task<CreditFacts> GetCreditFactsAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
            {
                lock (Credits) Credits.Add((from, to));
                return Task.FromResult(new CreditFacts());
            }

            public Task<StudioFacts> GetStudioFactsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
            {
                lock (Studio) Studio.Add((from, toExclusive));
                return Task.FromResult(new StudioFacts());
            }

            public Task<List<CreditTrendPoint>> GetCreditTrendAsync(IReadOnlyList<DateTime> dates, ReportUserScope scope, CancellationToken cancellationToken) =>
                Task.FromResult(new List<CreditTrendPoint>());

            public Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from, DateTime toExclusive,
                DateTime lastDay, CancellationToken cancellationToken) => Task.FromResult(new PersonDetailFacts());
        }

        private sealed class NoDirectory : IUserDirectorySource
        {
            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult<UserDirectorySnapshot>(null);
            public void Prefetch() { }
            public void Invalidate() { }
        }
    }
}
