using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    public partial class AppInsightsSearchReconciliationTests
    {
        [TestMethod]
        public async Task SupplementalPass_RechecksCurrentUserAndSiteScopeForOnlyMarkedHoursWithoutAdvancingTheNormalWatermark()
        {
            var clock = new FixedClock(Start);
            var state = new InMemoryKeyValueStore(() => clock.UtcNow);
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var hours = Enumerable.Range(0, 13).Select(i => Start.AddDays(-2).AddHours(i)).ToArray();
            const string revoked = "outside@contoso.com";
            const string retiredSite = "https://contoso.sharepoint.com/sites/retired";
            string Session(DateTime t) => "visit-" + Array.IndexOf(hours, t);
            string Viewer(DateTime t) => t == hours[9] ? revoked : Reader;
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t =>
                {
                    var view = Page(t, Session(t));
                    view.Username = Viewer(t);
                    if (t == hours[10])
                    {
                        view.CustomProperties.SiteUrl = view.CustomProperties.WebUrl = retiredSite;
                        view.Url = retiredSite + "/SitePages/Home.aspx";
                    }
                    return view;
                },
                EventAt = t =>
                {
                    var term = Viewer(t) == revoked ? "Excluded" : t == hours[10] ? "Retired site" : "Example";
                    var search = Search(t, Session(t), term, Viewer(t));
                    search.CustomProperties.PageRequestId = Page(t, Session(t)).CustomProperties.PageRequestId;
                    return search;
                },
            };
            _database.Execute("INSERT dbo.org_urls(url_base) VALUES (N'https://contoso.sharepoint.com/');");
            foreach (var t in hours) source.ExtraPageViews.Add((t, t));
            await RunCycle(clock, source, state);
            foreach (var t in hours) source.ExtraCustomEvents.Add((t, t));
            await cursor.WriteSearchRetriesAsync(hours.Take(12).Select(h => new AppInsightsSearchRetry(h, Start)).ToArray());
            clock.Advance(TimeSpan.FromMinutes(5));
            var normal = await RunCycle(clock, source, state);
            Assert.AreEqual(8, normal.ReconciledSearches);
            Assert.IsTrue(normal.HasDueSearchRetries);
            var members = new UserScopeMembers();
            members.Add(null, Reader, null);
            var freshScope = UserImportScope.ForMembers(members, "Contoso pilot");
            _database.Execute("UPDATE dbo.org_urls SET url_base=N'https://contoso.sharepoint.com/sites/example/', exact_match=1;");
            source.CountRangesRequested.Clear();
            clock.Advance(TimeSpan.FromMinutes(1));
            var pass = await new AppInsightsImporter(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer(),
                clock, source, contextFactory: _factory, cursorStore: cursor)
                .RecoverPendingSearchesAsync(normal.CursorAdvancedToUtc.Value, clock.UtcNow.AddDays(1), default, _ => Task.FromResult(freshScope));
            Assert.AreEqual(2, pass.ReconciledSearches, "The removed user and the removed site must not get facts.");
            Assert.AreEqual((10L, 10L), SearchKpis());
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term IN (N'Excluded', N'Retired site')"));
            Assert.AreEqual(0, pass.PendingSearchHours);
            Assert.IsFalse(pass.HasDueSearchRetries);
            Assert.IsNull(pass.CursorAdvancedToUtc);
            Assert.AreEqual(normal.CursorAdvancedToUtc, await cursor.ReadAsync());
            CollectionAssert.AreEqual(hours.Skip(8).Take(4).ToArray(),
                source.CountRangesRequested.Where(w => w.SearchesOnly).Select(w => w.IngestedFromUtc.Value).ToArray());
            Assert.IsTrue(source.CountRangesRequested.All(w => w.SearchesOnly), "Neither unmarked searches nor normal data are requested.");
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task SupplementalPass_InterruptsBlockedSqlForHostStopOrNormalDeadlineAndKeepsItsCheckpoint(bool hostStop)
        {
            var clock = new FixedClock(Start);
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var hour = Start.AddHours(-1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "eligible"),
                EventAt = t => SearchOnRecordedPage(t, hour, "eligible")
            };
            source.ExtraPageViews.Add((hour, hour));
            await RunCycle(clock, source, state);
            source.ExtraCustomEvents.Add((hour, hour));
            await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(hour, Start) });
            var importer = new AppInsightsImporter(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer(),
                clock, source, contextFactory: _factory, cursorStore: cursor);
            using (var stop = new CancellationTokenSource())
            using (var blocker = new SqlConnection(_database.ConnectionString))
            {
                await blocker.OpenAsync();
                using (var tx = blocker.BeginTransaction())
                using (var command = blocker.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = "SELECT COUNT(*) FROM dbo.searches WITH (TABLOCKX, HOLDLOCK);";
                    await command.ExecuteScalarAsync();
                    var pass = importer.RecoverPendingSearchesAsync(Start, Start.AddSeconds(hostStop ? 60 : 10), stop.Token);
                    bool interrupted;
                    try
                    {
                        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@@SPID AND database_id=DB_ID();";
                        while ((int)await command.ExecuteScalarAsync() == 0)
                        {
                            Assert.IsFalse(pass.IsCompleted, "Wait for the production search merge before testing its cancellation.");
                            await Task.Delay(25);
                        }
                        if (hostStop) stop.Cancel();
                        interrupted = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(20))) == pass;
                    }
                    finally
                    {
                        tx.Rollback();
                        if (!pass.IsCompleted) stop.Cancel();
                        try { await pass; } catch (OperationCanceledException) { }
                    }
                    Assert.IsTrue(interrupted, "Neither stop nor the next normal poll waits for the retry SQL timeout.");
                    if (hostStop) await Assert.ThrowsAsync<OperationCanceledException>(() => pass);
                    else
                    {
                        var yielded = await pass;
                        Assert.AreEqual(AppInsightsCycleOutcome.Degraded, yielded.Outcome);
                        CollectionAssert.Contains(yielded.Reasons, "SearchRecoveryYielded");
                    }
                }
            }
            Assert.AreEqual(Start, await cursor.ReadAsync());
            Assert.AreEqual(Start, (await cursor.ReadSearchRetriesAsync()).Single().RetryAfterUtc);
            Assert.AreEqual((0L, 0L), SearchKpis());
            var recovered = await importer.RecoverPendingSearchesAsync(Start, Start.AddDays(1), default);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, recovered.PendingSearchHours);
        }
    }
}
