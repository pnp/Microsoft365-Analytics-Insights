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
        public async Task SupplementalPass_UsesCurrentUserScopeAndOnlyMarkedHoursWithoutAdvancingTheNormalWatermark()
        {
            var clock = new FixedClock(Start);
            var state = new InMemoryKeyValueStore(() => clock.UtcNow);
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var hours = Enumerable.Range(0, 13).Select(i => Start.AddDays(-2).AddHours(i)).ToArray();
            const string revoked = "outside@contoso.com";
            string Session(DateTime t) => "visit-" + Array.IndexOf(hours, t);
            string Viewer(DateTime t) => t == hours[9] ? revoked : Reader;
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t =>
                {
                    var view = Page(t, Session(t)); view.Username = Viewer(t); return view;
                },
                EventAt = t =>
                {
                    var search = Search(t, Session(t), Viewer(t) == revoked ? "Excluded" : "Example", Viewer(t));
                    search.CustomProperties.PageRequestId = Page(t, Session(t)).CustomProperties.PageRequestId;
                    return search;
                },
            };
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
            source.CountRangesRequested.Clear();
            clock.Advance(TimeSpan.FromMinutes(1));
            var pass = await new AppInsightsImporter(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer(),
                clock, source, contextFactory: _factory, userScope: freshScope, cursorStore: cursor)
                .RecoverPendingSearchesAsync(normal.CursorAdvancedToUtc.Value, clock.UtcNow.AddDays(1), default);
            Assert.AreEqual(3, pass.ReconciledSearches, "The user removed since the normal cycle must not get a fact.");
            Assert.AreEqual((11L, 11L), SearchKpis());
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term=N'Excluded'"));
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
                EventAt = t => Search(t, "eligible", "Example", Reader)
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
