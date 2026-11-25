using Common.Entities;
using Common.Entities.Config;
using Common.Entities.SpoWebActivity;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("SqlIntegration")]
    [DoNotParallelize]
    public partial class AppInsightsSearchReconciliationTests
    {
        private static readonly DateTime Start = new DateTime(2000, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        private const string Reader = "reader@contoso.com";
        private static ScratchDatabase _database;
        private static ContextFactory _factory;

        [TestMethod]
        public async Task SearchReport_ScoresLogicalVisitsAndTheirHitsWithoutRewritingHistoricalRows()
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "Pair") };
            source.ExtraPageViews.Add((Start, Start));
            await RunCycle(new FixedClock(Start.AddMinutes(1)), source, new InMemoryKeyValueStore());
            _database.Execute(@"
DECLARE @owner int = (SELECT id FROM dbo.users WHERE user_name='reader@contoso.com'),
        @canonical int = (SELECT id FROM dbo.sessions WHERE ai_session_id='Pair');
DECLARE @url int, @web int;
SELECT @url=url_id, @web=web_id FROM dbo.hits WHERE session_id=@canonical;
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('Pair',@owner);
DECLARE @historical int=SCOPE_IDENTITY();
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('pair',@owner);
DECLARE @differentCase int=SCOPE_IDENTITY();
INSERT dbo.sessions(ai_session_id,user_id) VALUES (NULL,@owner);
DECLARE @noTracker int=SCOPE_IDENTITY();
INSERT dbo.sessions(ai_session_id,user_id) VALUES (NULL,@owner);
DECLARE @otherNoTracker int=SCOPE_IDENTITY();
IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE user_name='other@contoso.com')
    INSERT dbo.users(user_name) VALUES ('other@contoso.com');
INSERT dbo.sessions(ai_session_id,user_id) SELECT 'Pair',id FROM dbo.users WHERE user_name='other@contoso.com';
DECLARE @otherUser int=SCOPE_IDENTITY();
INSERT dbo.hits(url_id,web_id,session_id,page_request_id,hit_timestamp) VALUES
(@url,NULL,@historical,NEWID(),'20000101 09:50'), (@url,@web,@canonical,NEWID(),'20000101 11:00'),
(@url,@web,@otherNoTracker,NEWID(),'20000101 12:00'), (@url,@web,@differentCase,NEWID(),'20000102 00:00'),
(@url,NULL,@differentCase,NEWID(),'20000101 09:20'), (@url,NULL,@noTracker,NEWID(),'20000101 09:20'),
(@url,NULL,@otherUser,NEWID(),'20000101 09:20'), (@url,@web,NULL,NEWID(),'20000101 12:30');
INSERT dbo.search_terms(search_term) VALUES (N'Example');
DECLARE @term int=SCOPE_IDENTITY();
INSERT dbo.searches(session_id,search_term_id,date_time) VALUES
(@historical,@term,'20000101 10:01'), (@canonical,@term,'20000101 10:02'), (@canonical,@term,'20000101 10:03');
INSERT dbo.search_terms(search_term) VALUES (N'Control');
SET @term=SCOPE_IDENTITY();
INSERT dbo.searches(session_id,search_term_id,date_time) VALUES
(@differentCase,@term,'20000101 10:01'), (@otherUser,@term,'20000101 10:01'),
(@noTracker,@term,'20000101 10:01'), (@otherNoTracker,@term,'20000101 10:01');");
            var store = new SqlWebActivityStore(_factory);
            var query = WebActivityQuery.Create(7, Start);
            var report = await store.GetSearchAsync(query);
            Assert.IsTrue(report.Queries.All(q => q.Error == null), string.Join("; ", report.Queries.Select(q => q.Error)));
            Assert.AreEqual((7L, 5L, 1L, 3L), (report.Kpis.Searches, report.Kpis.SessionsWithSearch, report.Kpis.StrugglingVisits, report.Kpis.DeadEndSearches));
            var pair = report.TopTerms.Single(t => t.Term == "Example");
            Assert.AreEqual((3L, 1, 0L), (pair.Searches, pair.Searchers, pair.DeadEnds));
            Assert.AreEqual(("Control", 3L), (report.DeadEndTerms.Single().Term, report.DeadEndTerms.Single().DeadEnds));
            Assert.AreEqual(3L, report.BySite.Single().Count, "The last prior page across equivalent sessions supplies context; other people/ids cannot.");
            var scoped = await store.GetSearchAsync(query.WithUserScope(ReportUserScope.ForUsers(new[] { (int)_database.Scalar("SELECT id FROM dbo.users WHERE user_name='reader@contoso.com'") })));
            Assert.AreEqual((6L, 4L, 1L, 2L), (scoped.Kpis.Searches, scoped.Kpis.SessionsWithSearch, scoped.Kpis.StrugglingVisits, scoped.Kpis.DeadEndSearches));
            Assert.IsTrue(scoped.TopTerms.All(t => t.Searchers == 1));
            Assert.AreEqual(100d, scoped.Kpis.SearchReliancePct, "The visit denominator must also collapse the two physical ids and omit the anonymous hit.");
            Assert.AreEqual(1, _database.Scalar(@"SELECT COUNT(*) FROM dbo.searches WHERE session_id=(SELECT MAX(s.id) FROM dbo.sessions s
JOIN dbo.users u ON u.id=s.user_id WHERE s.ai_session_id='Pair' AND u.user_name='reader@contoso.com')"),
                "Reporting must not move the old search off its physical session.");
            _database.Execute(@"DECLARE @canonical int=(SELECT MIN(s.id) FROM dbo.sessions s JOIN dbo.users u ON u.id=s.user_id
WHERE ai_session_id='Pair' AND u.user_name='reader@contoso.com'), @url int, @web int;
SELECT TOP (1) @url=url_id,@web=web_id FROM dbo.hits WHERE session_id=@canonical AND web_id IS NOT NULL;
INSERT dbo.hits(url_id,web_id,session_id,page_request_id,hit_timestamp) VALUES
(@url,@web,@canonical,NEWID(),'20000101 10:00:30'),(@url,NULL,@canonical,NEWID(),'20000101 10:00:30');");
            var tied = await store.GetSearchAsync(query);
            Assert.IsTrue(tied.Queries.All(q => q.Error == null));
            Assert.AreEqual(0, tied.BySite.Count, "The highest hit id at the same timestamp has no web; it must not inherit the earlier site.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SearchReport_CountsDistinctFactsAndMeasuresRelianceOnlyAmongHitVisits(bool scoped)
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "Pair") };
            source.ExtraPageViews.Add((Start, Start));
            await RunCycle(new FixedClock(Start.AddMinutes(1)), source, new InMemoryKeyValueStore());
            _database.Execute(@"
DECLARE @reader int = (SELECT id FROM dbo.users WHERE user_name='reader@contoso.com'),
        @visit int = (SELECT id FROM dbo.sessions WHERE ai_session_id='Pair'), @url int, @web int;
SELECT @url=url_id, @web=web_id FROM dbo.hits WHERE session_id=@visit;
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('Pair',@reader);
DECLARE @copy int=SCOPE_IDENTITY();
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('Browse',@reader);
DECLARE @browse int=SCOPE_IDENTITY();
IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE user_name='other@contoso.com')
    INSERT dbo.users(user_name) VALUES ('other@contoso.com');
DECLARE @other int=(SELECT id FROM dbo.users WHERE user_name='other@contoso.com');
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('Pair',@other);
DECLARE @early int=SCOPE_IDENTITY();
INSERT dbo.sessions(ai_session_id,user_id) VALUES ('Second',@other);
DECLARE @second int=SCOPE_IDENTITY();
INSERT dbo.hits(url_id,web_id,session_id,page_request_id,hit_timestamp) VALUES
(@url,@web,@browse,NEWID(),'20000101 10:00'),(@url,@web,@early,NEWID(),'19991231 23:59'),
(@url,@web,@second,NEWID(),'19991231 23:59');
INSERT dbo.search_terms(search_term) VALUES (N'Guide');
DECLARE @term int=SCOPE_IDENTITY();
INSERT dbo.searches(session_id,search_term_id,date_time) VALUES
(@visit,@term,'20000101 10:01'),(@copy,@term,'20000101 10:01'),(@copy,@term,'20000101 10:02'),
(@early,@term,'20000101 00:01'),(@second,@term,'20000101 00:01');");
            var query = WebActivityQuery.Create(7, Start.AddDays(6));
            if (scoped) query = query.WithUserScope(ReportUserScope.ForUsers(new[] {
                (int)_database.Scalar("SELECT id FROM dbo.users WHERE user_name='reader@contoso.com'") }));
            var report = await new SqlWebActivityStore(_factory).GetSearchAsync(query);
            Assert.IsTrue(report.Queries.All(q => q.Error == null), string.Join("; ", report.Queries.Select(q => q.Error)));
            var count = scoped ? 2L : 4L;
            var k = report.Kpis;
            Assert.AreEqual((count, scoped ? 1L : 3L, 0L, count, 50d),
                (k.Searches, k.SessionsWithSearch, k.StrugglingVisits, k.DeadEndSearches, k.SearchReliancePct));
            var term = report.TopTerms.Single();
            Assert.AreEqual((count, scoped ? 1 : 2, count), (term.Searches, term.Searchers, term.DeadEnds));
            Assert.AreEqual(scoped ? 0L : count, report.DeadEndTerms.Sum(t => t.DeadEnds));
            Assert.AreEqual(2L, report.BySite.Single().Count);
            Assert.AreEqual((count, count, count), (report.ByDay.Sum(d => d.Count),
                report.ByPeriodOfDay.Sum(p => p.Count), report.Trend.Sum(w => w.Searches)));
            Assert.AreEqual(5, _database.Scalar("SELECT COUNT(*) FROM dbo.searches"));
        }

        [TestMethod]
        public async Task RestrictedSearch_AdmitsOnlyItsOwnPageAndDoesNotQueueKnownExclusions()
        {
            const string site = "https://contoso.sharepoint.com/sites/example";
            _database.Execute($"INSERT dbo.org_urls(url_base, exact_match) VALUES (N'{site}/', 1);");
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddMinutes(10));
            var events = Enumerable.Range(1, 6).Select(n => Start.AddMinutes(n)).ToArray();
            source.PageViewAt = t =>
            {
                var page = Page(t, t == events[3] ? "TRACKER" : "tracker");
                if (t == events[1]) page.Url = page.CustomProperties.SiteUrl = page.CustomProperties.WebUrl = site + "-other";
                if (t == events[2]) page.Username = "other@contoso.com";
                return page;
            };
            source.EventAt = t =>
            {
                var search = Search(t, "tracker", t == events[0] ? "Καλημέρα" : "Excluded", Reader);
                if (t == events[4]) search.CustomProperties.PageRequestId = null;
                if (t == events[5]) search.CustomProperties.PageRequestId = Guid.Empty;
                return search;
            };
            foreach (var t in events.Take(4)) source.ExtraPageViews.Add((t, t));
            foreach (var t in events) source.ExtraCustomEvents.Add((t, t));

            var result = await RunCycle(clock, source, state);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term=N'Καλημέρα'"), "The allowed request is a positive control.");
            Assert.AreEqual(1, result.Events, "Only an admitted request with the same visitor and case-sensitive tracker id qualifies.");
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term=N'Excluded'"));
            Assert.AreEqual(0, result.PendingSearchHours, "A positively rejected page, wrong owner or unusable request id cannot arrive later.");
        }

        [TestMethod]
        public async Task SearchAdmission_EmptyAndMalformedConfigurationsAreNotInterchangeable()
        {
            const string site = "https://contoso.sharepoint.com/sites/δοκιμή";
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t =>
                {
                    var page = Page(t, "legacy");
                    page.CustomProperties.SiteUrl = page.CustomProperties.WebUrl = site;
                    page.Url = site + "/page.aspx";
                    return page;
                }
            };
            source.ExtraPageViews.Add((Start, Start));
            await RunCycle(new FixedClock(Start.AddMinutes(1)), source, new InMemoryKeyValueStore());
            var batch = new CustomEventsResultCollection();
            var search = Search(Start, "legacy", "Καλημέρα", Reader);
            batch.Rows.Add(search);
            using (var db = _factory.Create())
            {
                Task<SearchSaveResult> Save() => batch.SaveSearchBatchAsync(AnalyticsLogger.ConsoleOnlyTracer(), db);
                _database.Execute("INSERT dbo.org_urls(url_base) VALUES (N'https://'), (N' ');");
                var denied = await Save();
                Assert.AreEqual((0, 1, 0), (denied.FactsInserted, denied.Skipped, denied.Unmatched));
                Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));

                _database.Execute($"INSERT dbo.org_urls(url_base,exact_match) VALUES (N'{site}/',1);");
                Assert.AreEqual(1, (await Save()).FactsInserted, "The configured Unicode site and the escaped stored page are the same site.");
                _database.Execute("UPDATE dbo.hits SET web_id=NULL; DELETE dbo.searches; DELETE dbo.search_terms;");
                Assert.AreEqual(1, (await Save()).Skipped, "A historical hit with no stored site cannot prove an exact-site rule.");
                _database.Execute("UPDATE dbo.org_urls SET exact_match=0 WHERE exact_match=1;");
                Assert.AreEqual(1, (await Save()).FactsInserted, "A literal page URL prefix can still verify a web-less historical hit.");
                search.CustomProperties.PageRequestId = Guid.NewGuid();
                search.SearchIngestionHourUtc = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified);
                var pending = (await Save()).UnmatchedByHour.Single();
                Assert.AreEqual((Start, DateTimeKind.Utc, 1), (pending.Key, pending.Key.Kind, pending.Value),
                    "A restricted request awaiting its page must produce the same UTC marker as a SQL visit miss.");
                search.CustomProperties.PageRequestId = null;
                search.CustomProperties.SearchText = "Legacy";
                Assert.AreEqual(1, (await Save()).Skipped);
                _database.Execute("DELETE dbo.org_urls;");
                Assert.AreEqual(1, (await Save()).FactsInserted, "A genuinely unfiltered import can still resolve a legacy search by its visit.");
            }
        }

        [DataTestMethod]
        [DataRow("/", "/SitePages/Home.aspx", "/", true, true)]
        [DataRow("/", "/sites/example/SitePages/Home.aspx", "/", true, false)]
        [DataRow("/", "/Teams/example/SitePages/Home.aspx", "/", true, false)]
        [DataRow("/sites/example", "/sites/example/SitePages/Home.aspx", "/", false, true)]
        [DataRow("/sites/Équipe", "/sites/%C3%A9quipe/SitePages/Home.aspx", "/sites/équipe/", true, true)]
        [DataRow("/sites/%C3%89quipe", "/sites/équipe/SitePages/Home.aspx", "/sites/Éq", false, true)]
        [DataRow("/sites/Équipe", "/sites/équipe/SitePages/Home.aspx", "/sites/Éq", true, false)]
        public async Task SearchAdmission_ChecksStoredCollectionAndConfiguredRule(string storedSite, string pagePath,
            string rulePath, bool exact, bool expected)
        {
            const string origin = "https://contoso.sharepoint.com";
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t =>
                {
                    var page = Page(t, "site-case");
                    page.Url = origin + pagePath;
                    page.CustomProperties.SiteUrl = page.CustomProperties.WebUrl = origin + storedSite;
                    return page;
                }
            };
            source.ExtraPageViews.Add((Start, Start));
            await RunCycle(new FixedClock(Start.AddMinutes(1)), source, new InMemoryKeyValueStore());
            _database.Execute($"INSERT dbo.org_urls(url_base, exact_match) VALUES (N'{origin}{rulePath}', {(exact ? 1 : 0)});");
            var batch = new CustomEventsResultCollection();
            batch.Rows.Add(Search(Start, "site-case", "Guide", Reader));
            using (var db = _factory.Create())
            {
                var result = await batch.SaveSearchBatchAsync(AnalyticsLogger.ConsoleOnlyTracer(), db);
                Assert.AreEqual((expected ? 1 : 0, expected ? 0 : 1, 0), (result.FactsInserted, result.Skipped, result.Unmatched));
            }
        }

        [TestMethod]
        public async Task SearchAdmission_SiteNarrowedDuringStagingMustNotCommitTheOldDecision()
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "example") };
            source.ExtraPageViews.Add((Start, Start));
            await RunCycle(new FixedClock(Start.AddMinutes(1)), source, new InMemoryKeyValueStore());
            var batch = new CustomEventsResultCollection();
            batch.Rows.Add(Search(Start, "example", "Contoso", Reader));
            using (var db = _factory.Create())
            {
                // An admin adds the first restriction after the unrestricted batch staged its row.
                var logger = new AfterStagingLogger(() => _database.Execute("INSERT dbo.org_urls(url_base) VALUES (N'https://contoso.example/');"));
                var error = await Assert.ThrowsExceptionAsync<SqlException>(() => batch.SaveSearchBatchAsync(logger, db));
                Assert.AreEqual(51000, error.Number);
                Assert.AreEqual(1, (await batch.SaveSearchBatchAsync(AnalyticsLogger.ConsoleOnlyTracer(), db)).Skipped);
            }
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.searches"));
        }

        private sealed class AfterStagingLogger : ILogger
        {
            private readonly Action _afterStaging;
            internal AfterStagingLogger(Action afterStaging) => _afterStaging = afterStaging;
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> format)
            {
                if (format(state, error).EndsWith("Running merge script...", StringComparison.Ordinal)) _afterStaging();
            }
        }

        [TestMethod]
        public async Task RestrictedRetry_RequiresTheActualDelayedHitAndRechecksTheCurrentSiteRules()
        {
            const string site = "https://contoso.sharepoint.com/sites/example";
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddMinutes(10));
            var historical = Start.AddMinutes(-10);
            var delayed = Start.AddMinutes(1);
            var nowExcluded = Start.AddMinutes(2);
            source.PageViewAt = t =>
            {
                var page = Page(t, "shared");
                if (t == nowExcluded)
                {
                    page.CustomProperties.SiteUrl = page.CustomProperties.WebUrl = site + "-retired";
                    page.Url = page.CustomProperties.SiteUrl + "/SitePages/Home.aspx";
                }
                return page;
            };
            source.EventAt = t => Search(t, "shared", t == delayed ? "Delayed" : "Retired", Reader);
            _database.Execute("INSERT dbo.org_urls(url_base) VALUES (N'https://contoso.sharepoint.com/');");
            source.ExtraPageViews.Add((historical, historical));
            source.ExtraCustomEvents.Add((delayed, delayed));
            source.ExtraCustomEvents.Add((nowExcluded, nowExcluded));
            var first = await RunCycle(clock, source, state);
            Assert.AreEqual(2, first.Days.Sum(d => d.UnmatchedSearches), "Another allowed page in this visit proves neither search.");
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
            Assert.AreEqual(1, first.PendingSearchHours);

            // Both actual pages arrive after the event overlap. Before the search-only pass one site is removed.
            clock.Advance(TimeSpan.FromMinutes(16));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            clock.Advance(TimeSpan.FromMinutes(10));
            source.ExtraPageViews.Add((delayed, clock.UtcNow.AddMinutes(-1)));
            source.ExtraPageViews.Add((nowExcluded, clock.UtcNow.AddMinutes(-1)));
            await RunCycle(clock, source, state);
            Assert.AreEqual(3, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
            _database.Execute($"UPDATE dbo.org_urls SET url_base=N'{site}/', exact_match=1;");
            clock.Advance(TimeSpan.FromMinutes(15));
            var retry = await RunCycle(clock, source, state);
            Assert.AreEqual(1, retry.ReconciledSearches);
            Assert.AreEqual(0, retry.PendingSearchHours);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term=N'Delayed'"));
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term=N'Retired'"));
        }

        [ClassInitialize]
        public static void CreateDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("SearchArrival");
            new DbMigrator(new Common.Entities.Migrations.Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
            _factory = new ContextFactory(_database.ConnectionString);
        }

        [ClassCleanup]
        public static void DropDatabase() => _database?.Dispose();

        [TestInitialize]
        public void ClearVisits() => _database.Execute("DELETE dbo.searches; DELETE dbo.hits; DELETE dbo.sessions; DELETE dbo.search_terms; DELETE dbo.org_urls;");

        private sealed class ContextFactory : IAnalyticsDbContextFactory
        {
            private readonly string _connection;
            public ContextFactory(string connection) => _connection = connection;
            public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(_connection, true, false);
        }

        [TestMethod]
        public async Task SearchReport_RecoversSearchesWhenEligibleVisitsArriveInsideAndOutsideCursorOverlap()
        {
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var seedTime = Start.AddMinutes(-10);
            var nearTime = Start.AddSeconds(-5);
            var farTime = Start.Date.AddSeconds(-5);
            source.PageViewAt = time => Page(time, time == seedTime ? "seed" : time == nearTime ? "near" : "far");
            source.EventAt = time => Search(time, time == nearTime ? "near" : "far", "Καλημέρα", Reader);
            Task<AppInsightsImportCycleResult> Run() => RunCycle(clock, source, state);

            source.ExtraPageViews.Add((seedTime, seedTime));
            Assert.AreEqual(Start, (await Run()).CursorAdvancedToUtc, "Bootstrap must write a real hit and the cursor.");
            source.ExtraCustomEvents.Add((nearTime, Start.AddMinutes(1)));
            source.ExtraCustomEvents.Add((farTime, Start.AddMinutes(1)));
            source.ExtraPageViews.Add((nearTime, Start.AddMinutes(17)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(35)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var first = await Run();
            Assert.AreEqual((0L, 0L), SearchKpis());
            Assert.AreEqual(0, first.Events, "Neither unmatched search is saved.");
            Assert.AreEqual(2, first.Days.Sum(d => d.UnmatchedSearches));
            Assert.AreEqual(1, first.PendingSearchHours, "Both events arrived in the same ingestion hour despite different timestamp days.");
            var marker = (JObject)JArray.Parse(await state.GetStringAsync("IngestionCursorSearchRetries:contoso-test")).Single();
            CollectionAssert.AreEquivalent(new[] { "HourUtc", "RetryAfterUtc" }, marker.Properties().Select(p => p.Name).ToArray(),
                "No identity, search text, event or other raw telemetry belongs in the durable checkpoint.");
            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, first.Outcome);
            Assert.AreEqual(clock.UtcNow, first.CursorAdvancedToUtc, "Other telemetry need not wait for either visit.");
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions"), "A search must not invent a visit.");
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            Assert.AreEqual((1L, 1L), SearchKpis(), "The visit arriving at 10:17 is inside the ordinary overlap.");
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            clock.Advance(TimeSpan.FromMinutes(10));
            var recovered = await Run();
            Assert.AreEqual(3, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "Both delayed views reached SQL.");
            Assert.AreEqual((2L, 2L), SearchKpis(), "The visit arriving at 10:35 must also recover its search.");
            Assert.AreEqual(1, recovered.ReconciledSearches, "The near search was replayed, not counted as recovered.");
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, recovered.Outcome);
            clock.Advance(TimeSpan.FromMinutes(10));
            await Run();
            Assert.AreEqual(1, source.CountRangesRequested.Count(w => w.SearchesOnly), "A completed retry must be removed from the checkpoint.");
            Assert.AreEqual((2L, 2L), SearchKpis());
        }

        [TestMethod]
        public async Task HistoricalCatchUp_VisitsOnLaterTimestampDaysResolveEarlierSearchesBeforeTheyExpire()
        {
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = time => Page(time, "old"),
                EventAt = time => Search(time, "old", "Old", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start.AddDays(6));
            source.ExtraCustomEvents.Add((Start.Date.AddSeconds(-1), Start.AddMinutes(1)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(35)));

            var caughtUp = await RunCycle(clock, source, state);
            Assert.AreEqual(1, caughtUp.Days.Sum(d => d.UnmatchedSearches));
            Assert.AreEqual(1, caughtUp.ReconciledSearches, "The search's three-day retry window elapsed before this first import.");
            Assert.AreEqual(0, caughtUp.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow, caughtUp.CursorAdvancedToUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [DataTestMethod]
        [DataRow("page-view")]
        [DataRow("normal-search")]
        [DataRow("page-update-after-search")]
        [DataRow("search-only")]
        public async Task StopDuringBlockedMerge_PreservesEarlierCommitsAndReplaysOnce(string boundary)
        {
            var searchesOnly = boundary == "search-only";
            var stopInPageView = boundary == "page-view";
            var stopAfterSearch = boundary == "page-update-after-search";
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "eligible"),
                EventAt = t => Search(t, "eligible", "Example", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            var arrival = searchesOnly ? Start.AddHours(-1) : Start.AddHours(1);
            if (searchesOnly)
                await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(arrival, Start) });
            source.ExtraCustomEvents.Add((arrival, arrival));
            source.ExtraPageViews.Add((Start.AddHours(1), Start.AddHours(1)));
            if (stopAfterSearch)
            {
                var updateTime = arrival.AddSeconds(1);
                source.ExtraCustomEvents.Add((updateTime, updateTime));
                source.EventAt = t => t == updateTime
                    ? new PageUpdateEventAppInsightsQueryResult { AppInsightsTimestamp = t, Username = Reader,
                        CustomProperties = new PageUpdateEventCustomProps { Url = Page(t, "eligible").Url, PropsString = "{\"ExampleField\":\"Example value\"}" } }
                    : (BaseCustomEventAppInsightsQueryResult)Search(t, "eligible", "Example", Reader);
            }
            clock.Advance(TimeSpan.FromHours(2));

            using (var stop = new CancellationTokenSource())
            using (var blocker = new SqlConnection(_database.ConnectionString))
            {
                await blocker.OpenAsync();
                Task<AppInsightsImportCycleResult> import;
                bool interrupted;
                using (var tx = blocker.BeginTransaction())
                using (var command = blocker.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = stopInPageView ? "SELECT COUNT(*) FROM dbo.page_titles WITH (TABLOCKX, HOLDLOCK);"
                        : stopAfterSearch ? "SELECT COUNT(*) FROM dbo.file_field_definitions WITH (TABLOCKX, HOLDLOCK);"
                        : "SELECT COUNT(*) FROM dbo.searches WITH (TABLOCKX, HOLDLOCK);";
                    await command.ExecuteScalarAsync();
                    import = RunCycle(clock, source, state, token: stop.Token);
                    try
                    {
                        // Cancel only after the production merge is blocked, not while starting EF.
                        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @@SPID AND database_id = DB_ID();";
                        var wait = Stopwatch.StartNew();
                        while ((int)await command.ExecuteScalarAsync() == 0)
                        {
                            Assert.IsFalse(import.IsCompleted || wait.Elapsed > TimeSpan.FromSeconds(30), "The merge did not reach the lock.");
                            await Task.Delay(25);
                        }
                        stop.Cancel();
                        interrupted = await Task.WhenAny(import, Task.Delay(TimeSpan.FromSeconds(5))) == import;
                    }
                    finally
                    {
                        tx.Rollback();
                        stop.Cancel();
                        try { await import; } catch (OperationCanceledException) { }
                    }
                }
                Assert.IsTrue(interrupted, "The host stop must interrupt the blocked SQL command without releasing its lock.");
                await Assert.ThrowsAsync<OperationCanceledException>(() => import);
            }
            Assert.AreEqual(Start, await cursor.ReadAsync());
            var markers = await cursor.ReadSearchRetriesAsync();
            Assert.AreEqual(searchesOnly ? 1 : 0, markers.Count);
            if (searchesOnly) Assert.AreEqual(Start, markers.Single().RetryAfterUtc, "The stop must not reschedule the hour.");
            Assert.AreEqual(stopInPageView ? 1 : 2, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "Earlier SQL commits survive.");
            var committed = stopAfterSearch ? 1 : 0;
            Assert.AreEqual(committed, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"), "A lookup and its search fact commit together.");
            Assert.AreEqual(((long)committed, (long)committed), SearchKpis());
            if (stopAfterSearch) Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.file_metadata_property_values"));

            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(clock.UtcNow, recovered.CursorAdvancedToUtc);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual(searchesOnly ? 1 : 0, recovered.ReconciledSearches);
            Assert.AreEqual(2, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            Assert.AreEqual((1L, 1L), SearchKpis());
            if (stopAfterSearch)
            {
                Assert.AreEqual(1, recovered.Events, "Only the page update counts; the search was already saved.");
                Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.file_metadata_property_values WHERE field_value = N'Example value'"));
            }
        }

        [TestMethod]
        public async Task StopAfterFirstSearchWindow_RetriesTheSameHourWithoutCountingCommittedFactsAgain()
        {
            var hour = Start.AddHours(-2);
            var earlier = Start.Date.AddSeconds(-1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, "eligible"),
                EventAt = t => Search(t, "eligible", t == earlier ? "Earlier" : "Later", Reader)
            };
            var state = new InMemoryKeyValueStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            await cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(hour, Start) });
            source.ExtraCustomEvents.Add((earlier, hour));
            source.ExtraCustomEvents.Add((hour, hour));
            clock.Advance(TimeSpan.FromMinutes(20));

            using (var stop = new CancellationTokenSource())
            {
                var windows = 0;
                source.OnEventWindow = w => { if (w.SearchesOnly && ++windows == 2) stop.Cancel(); };
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
                Assert.AreEqual(2, windows);
            }
            Assert.AreEqual(Start, await cursor.ReadAsync());
            Assert.AreEqual(Start, (await cursor.ReadSearchRetriesAsync()).Single().RetryAfterUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
            source.OnEventWindow = null;
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches, "Only the second window inserted a fact on replay.");
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((2L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task Retry_DoesNotClaimAnEmptySourceOrSaveSearchesForSomeoneNowOutsideTheFilter()
        {
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var seed = Start.AddMinutes(-5);
            var search = Start.AddMinutes(1);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = time => Page(time, time == seed ? "seed" : "later"),
                EventAt = time => Search(time, "later", "Later", Reader),
            };
            source.ExtraPageViews.Add((seed, seed));
            await RunCycle(clock, source, state);
            source.ExtraCustomEvents.Add((search, search));
            source.ExtraPageViews.Add((search, Start.AddMinutes(33)));
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            clock.Advance(TimeSpan.FromMinutes(15));
            await RunCycle(clock, source, state);
            clock.Advance(TimeSpan.FromMinutes(10));
            await RunCycle(clock, source, state);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions WHERE ai_session_id = 'later'"));

            source.ExtraCustomEvents.Clear();
            clock.Advance(TimeSpan.FromMinutes(5));
            var empty = await RunCycle(clock, source, state);
            Assert.IsTrue(empty.Reasons.Contains("SearchRetrySourceEmpty"));
            Assert.AreEqual(1, empty.PendingSearchHours);
            source.ExtraCustomEvents.Add((search, search));
            clock.Advance(TimeSpan.FromDays(4));
            var members = new UserScopeMembers();
            members.Add(null, "member@contoso.com", null);
            var removed = await RunCycle(clock, source, state, UserImportScope.ForMembers(members, "Contoso pilot"));
            Assert.AreEqual(0, removed.PendingSearchHours, "A positively excluded user does not need further retries.");
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, removed.Outcome);
            Assert.AreEqual((0L, 0L), SearchKpis(), "The visit exists, but the user is no longer in scope.");
        }

        [DataTestMethod]
        [DataRow("marker-read")]
        [DataRow("marker-write")]
        [DataRow("between-writes")]
        [DataRow("after-newest-hit")]
        [DataRow("cursor-write")]
        [DataRow("after-cursor")]
        public async Task StopAtCheckpoint_RestartReadsOnlyDurableProgressAndDoesNotDuplicateFacts(string boundary)
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "eligible") };
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            var clock = new FixedClock(Start);
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(clock, source, state);
            var matched = Start.AddMinutes(1);
            var missing = Start.AddMinutes(2);
            source.EventAt = t => Search(t, t == matched ? "eligible" : "delayed", "Example", Reader);
            source.ExtraCustomEvents.Add((matched, matched));
            source.ExtraCustomEvents.Add((missing, missing));
            clock.Advance(TimeSpan.FromMinutes(10));
            bool markerCommitted = boundary != "marker-read" && boundary != "marker-write";
            using (var stop = new CancellationTokenSource())
            {
                bool receivedToken = false;
                state.AtBoundary = (operation, token) =>
                {
                    if (operation != boundary) return;
                    receivedToken = token == stop.Token;
                    stop.Cancel();
                    // A provider may throw its own error, throw cancellation, or finish a write despite the stop.
                    if (operation == "marker-read") throw new IOException("synthetic interrupted read");
                    if (operation == "marker-write" || operation == "cursor-write")
                        token.ThrowIfCancellationRequested();
                };
                var output = await AppInsightsImporterOrchestrationTests.CaptureConsole(async () =>
                    await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token)));
                Assert.IsTrue(receivedToken, "The production checkpoint must receive the cycle token.");
                Assert.IsFalse(output.Contains("checkpoint could not") || output.Contains("AppInsightsCursor: Outcome=WriteFailed")
                    || output.Contains("OperationName=Hits import") || output.Contains("AppInsightsCursor: Outcome=Advanced"));
            }
            state.AtBoundary = null;
            Assert.AreEqual(markerCommitted ? 1 : 0, (await cursor.ReadSearchRetriesAsync()).Count);
            Assert.AreEqual(boundary == "after-cursor" ? clock.UtcNow : Start, await cursor.ReadAsync());
            Assert.AreEqual(boundary == "marker-read" ? (0L, 0L) : (1L, 1L), SearchKpis());

            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(boundary == "marker-read" ? 1 : 0, replay.Events, "An existing search fact is not a new save.");
            Assert.AreEqual(clock.UtcNow, replay.CursorAdvancedToUtc);
            Assert.AreEqual(1, replay.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.AreEqual(1, (await RunCycle(clock, source, state)).PendingSearchHours);
            source.PageViewAt = t => Page(t, "delayed");
            source.ExtraPageViews.Clear();
            source.ExtraPageViews.Add((Start.AddMinutes(32), Start.AddMinutes(32)));
            clock.Advance(TimeSpan.FromMinutes(16));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((2L, 2L), SearchKpis());
        }

        private sealed class CheckpointStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _state = new InMemoryKeyValueStore();
            public Action<string, CancellationToken> AtBoundary;
            public string Description => _state.Description;

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                if (key.StartsWith(KeyValueIngestionCursorStore.KeyPrefix + ":", StringComparison.Ordinal)) AtBoundary?.Invoke("cursor-read", cancellationToken);
                if (key.StartsWith(KeyValueIngestionCursorStore.NewestHitKeyPrefix + ":", StringComparison.Ordinal)) AtBoundary?.Invoke("newest-read", cancellationToken);
                if (key.StartsWith("IngestionCursorSearchRetries:", StringComparison.Ordinal)) AtBoundary?.Invoke("marker-read", cancellationToken);
                if (key.StartsWith("IngestionCursorSearchReplayHours:", StringComparison.Ordinal)) AtBoundary?.Invoke("receipt-read", cancellationToken);
                if (key.StartsWith("IngestionCursorReplayStart:", StringComparison.Ordinal)) AtBoundary?.Invoke("replay-read", cancellationToken);
                return _state.GetStringAsync(key, cancellationToken);
            }

            public async Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                bool marker = key.StartsWith("IngestionCursorSearchRetries:", StringComparison.Ordinal);
                bool cursor = key.StartsWith(KeyValueIngestionCursorStore.KeyPrefix + ":", StringComparison.Ordinal);
                if (marker || cursor) AtBoundary?.Invoke(marker ? "marker-write" : "cursor-write", cancellationToken);
                if (key.StartsWith("IngestionCursorSearchReplayHours:", StringComparison.Ordinal)) AtBoundary?.Invoke("receipt-write", cancellationToken);
                if (key.StartsWith("IngestionCursorReplayStart:", StringComparison.Ordinal)) AtBoundary?.Invoke("replay-write", cancellationToken);
                await _state.SetStringAsync(key, value, timeToLive, cancellationToken);
                if (marker || cursor) AtBoundary?.Invoke(marker ? "between-writes" : "after-cursor", cancellationToken);
                if (key.StartsWith(KeyValueIngestionCursorStore.NewestHitKeyPrefix + ":", StringComparison.Ordinal))
                    AtBoundary?.Invoke("after-newest-hit", cancellationToken);
            }

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _state.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _state.ExistsAsync(key, cancellationToken);
        }

        [DataTestMethod]
        [DataRow("replay-read", "ReplayStartReadFailed")]
        [DataRow("replay-write", "ReplayStartWriteFailed")]
        public async Task BootstrapStartStoreOutage_DoesNotLetSqlChangeTheOnlyResumePoint(string fault, string reason)
        {
            var clock = new FixedClock(Start.AddMinutes(1));
            var source = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "example") };
            source.ExtraPageViews.Add((Start, Start));
            var state = new CheckpointStore { AtBoundary = (op, _) => { if (op == fault) throw new IOException("synthetic outage"); } };
            var failed = await RunCycle(clock, source, state);
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, failed.Outcome);
            CollectionAssert.Contains(failed.Reasons, reason);
            Assert.IsNull(failed.CursorAdvancedToUtc);
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
            state.AtBoundary = null;
            Assert.AreEqual(clock.UtcNow, (await RunCycle(clock, source, state)).CursorAdvancedToUtc);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));
        }

        [DataTestMethod]
        [DataRow("cursor-read")]
        [DataRow("newest-read")]
        public async Task UnreadableCursor_DoesNotLetFallbackSqlHideAnOlderSearch(string fault)
        {
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            if (fault == "newest-read") await cursor.WriteAsync(Start.AddMinutes(20), Start);
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, t == Start ? "seed" : "delayed"),
                EventAt = t => Search(t, "delayed", "Example", Reader)
            };
            var clock = new FixedClock(Start.AddHours(1));
            source.ExtraCustomEvents.Add((Start.AddDays(-1), Start.AddMinutes(-2)));
            source.ExtraPageViews.Add((Start, Start));
            state.AtBoundary = (op, _) => { if (op == fault) throw new IOException("synthetic checkpoint outage"); };
            var outage = await RunCycle(clock, source, state);
            CollectionAssert.Contains(outage.Reasons, "CursorReadFailed");
            Assert.AreEqual(AppInsightsCycleOutcome.Failed, outage.Outcome);
            Assert.IsNull(outage.CursorAdvancedToUtc);
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"));

            state.AtBoundary = null;
            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(1, replay.PendingSearchHours, "An unreadable cursor must not let fallback SQL move the next run past the search.");
            source.ExtraPageViews.Add((clock.UtcNow, clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(31));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [DataTestMethod]
        [DataRow("bootstrap")]
        [DataRow("empty-restore")]
        [DataRow("rewind")]
        public async Task BootstrapOrRestoreStopBeforeMarker_NewerSqlHitDoesNotHideAnOlderSearch(string mode)
        {
            var clock = new FixedClock(Start.AddHours(1));
            var state = new CheckpointStore();
            var cursor = new KeyValueIngestionCursorStore(state, "contoso-test");
            DateTime? previousCursor = null;
            if (mode != "bootstrap")
            {
                previousCursor = Start.AddMinutes(20);
                await cursor.WriteAsync(previousCursor.Value, Start);
            }
            if (mode == "rewind")
            {
                var old = Start.AddDays(-6);
                var restoredSource = new FakeAppInsightsSourceLoader { PageViewAt = t => Page(t, "old") };
                restoredSource.ExtraPageViews.Add((old, old));
                await RunCycle(new FixedClock(old.AddMinutes(1)), restoredSource, new InMemoryKeyValueStore());
            }
            var source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => Page(t, t == Start ? "seed" : "delayed"),
                EventAt = t => Search(t, "delayed", "Example", Reader)
            };
            source.ExtraCustomEvents.Add((Start.AddDays(-1), Start.AddMinutes(-2)));
            source.ExtraPageViews.Add((Start, Start.AddMinutes(-1)));

            using (var stop = new CancellationTokenSource())
            {
                state.AtBoundary = (operation, token) =>
                {
                    if (operation == "marker-write") { stop.Cancel(); token.ThrowIfCancellationRequested(); }
                };
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunCycle(clock, source, state, token: stop.Token));
            }
            Assert.AreEqual(previousCursor, await cursor.ReadAsync());
            Assert.AreEqual(0, (await cursor.ReadSearchRetriesAsync()).Count);
            var anchor = JObject.Parse(await state.GetStringAsync("IngestionCursorReplayStart:contoso-test"));
            Assert.IsTrue(anchor.Properties().All(p => p.Value.Type == JTokenType.Date || p.Value.Type == JTokenType.Null),
                "A pinned start must never contain searches, user/session identities or other telemetry.");
            Assert.AreEqual(mode == "rewind" ? 2 : 1, _database.Scalar("SELECT COUNT(*) FROM dbo.hits"), "The later page view is already committed.");
            state.AtBoundary = null;
            source.ExtraPageViews.Add((Start.AddHours(1), Start.AddHours(1)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var replay = await RunCycle(clock, source, state);
            Assert.AreEqual(mode == "rewind" ? AppInsightsReadMode.Incremental : AppInsightsReadMode.Bootstrap, replay.Mode);
            Assert.AreEqual(1, replay.PendingSearchHours, "The newer stored hit must not hide an uncheckpointed search from an earlier timestamp day.");
            clock.Advance(TimeSpan.FromMinutes(31));
            var recovered = await RunCycle(clock, source, state);
            Assert.AreEqual(1, recovered.ReconciledSearches);
            Assert.AreEqual(0, recovered.PendingSearchHours);
            Assert.AreEqual((1L, 1L), SearchKpis());
        }

        [TestMethod]
        public async Task RetryMerge_TimeoutRollsBackTheLookupAndALaterAttemptCanSaveTheSearch()
        {
            var source = new FakeAppInsightsSourceLoader { PageViewAt = time => Page(time, "eligible") };
            source.ExtraPageViews.Add((Start.AddMinutes(-5), Start.AddMinutes(-5)));
            await RunCycle(new FixedClock(Start), source, new InMemoryKeyValueStore());
            var batch = new CustomEventsResultCollection();
            batch.Rows.Add(Search(Start, "eligible", "New search", Reader));
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            using (var db = _factory.Create())
            using (var blocker = new SqlConnection(_database.ConnectionString))
            {
                await blocker.OpenAsync();
                using (var transaction = blocker.BeginTransaction())
                using (var takeLock = new SqlCommand("SELECT COUNT(*) FROM dbo.searches WITH (TABLOCKX, HOLDLOCK);", blocker, transaction))
                {
                    await takeLock.ExecuteScalarAsync();
                    var ex = await Assert.ThrowsExceptionAsync<SqlException>(() => batch.SaveSearchBatchAsync(logger, db, mergeTimeoutSeconds: 1));
                    Assert.AreEqual(-2, ex.Number, "A blocked retry merge has a finite SQL command deadline.");
                }
                Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms"));
                var recovered = await batch.SaveSearchBatchAsync(logger, db, mergeTimeoutSeconds: 5);
                Assert.AreEqual(2, recovered.RowsAffected);
                Assert.AreEqual((1L, 1L), SearchKpis());
            }
        }

        [TestMethod]
        public async Task PermanentlyMissingVisit_ExpiresWithoutCreatingUsersOrBypassingTheSiteOrUserFilter()
        {
            var source = new FakeAppInsightsSourceLoader();
            var state = new InMemoryKeyValueStore();
            var clock = new FixedClock(Start);
            var members = new UserScopeMembers();
            members.Add(null, Reader, null);
            var scope = UserImportScope.ForMembers(members, "Contoso pilot");
            Task<AppInsightsImportCycleResult> Run() => RunCycle(clock, source, state, scope);
            const string other = "outside@contoso.com";
            var seed = Start.AddMinutes(-5);
            var deniedSite = Start.AddMinutes(1);
            var deniedUser = Start.AddMinutes(2);
            var late = Start.AddMinutes(3);
            source.PageViewAt = time =>
            {
                var pv = Page(time, time == seed ? "seed" : time == deniedSite ? "no-site" : time == deniedUser ? "no-user" : "late");
                if (time == deniedSite) pv.Url = pv.CustomProperties.SiteUrl = "https://contoso.sharepoint.com/sites/elsewhere";
                if (time == deniedUser) pv.Username = other;
                return pv;
            };
            source.EventAt = time => time == deniedSite ? Search(time, "no-site", "No site", Reader)
                : time == deniedUser ? Search(time, "no-user", "No user", other) : Search(time, "late", "Later", Reader);
            _database.Execute("INSERT dbo.org_urls(url_base) VALUES(N'https://contoso.sharepoint.com/sites/example');");
            source.ExtraPageViews.Add((seed, seed));
            await Run();
            foreach (var time in new[] { deniedSite, deniedUser, late }) source.ExtraCustomEvents.Add((time, time));
            source.ExtraPageViews.Add((deniedSite, Start.AddMinutes(35)));
            source.ExtraPageViews.Add((deniedUser, Start.AddMinutes(35)));
            source.ExtraPageViews.Add((late, Start.AddHours(12)));
            clock.Advance(TimeSpan.FromMinutes(10));
            var missed = await Run();
            Assert.AreEqual(2, missed.Days.Sum(d => d.UnmatchedSearches), "An explicitly excluded user is not a retryable search.");
            clock.Advance(TimeSpan.FromMinutes(30));
            var early = await Run();
            Assert.AreEqual(0, early.ReconciledSearches, "Neither an out-of-scope page view nor an excluded user creates an eligible visit.");
            Assert.AreEqual(1, early.PendingSearchHours);
            Assert.AreEqual(1, _database.Scalar("SELECT COUNT(*) FROM dbo.sessions"));
            clock.Advance(TimeSpan.FromHours(12));
            await Run();
            clock.Advance(TimeSpan.FromDays(3));
            var final = await Run();
            Assert.AreEqual(1, final.ReconciledSearches, "The final pass recovers the visit that arrived after the first retry.");
            Assert.IsTrue(final.Reasons.Contains("SearchRetriesStopped"));
            Assert.AreEqual(0, final.PendingSearchHours);
            Assert.AreEqual(clock.UtcNow, final.CursorAdvancedToUtc);
            Assert.AreEqual((1L, 1L), SearchKpis());
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.search_terms WHERE search_term IN (N'No site', N'No user')"));
            Assert.AreEqual(0, _database.Scalar("SELECT COUNT(*) FROM dbo.users WHERE user_name = 'outside@contoso.com'"));
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual(AppInsightsCycleOutcome.Succeeded, (await Run()).Outcome, "A permanently absent visit does not poison later cycles.");
            Assert.AreEqual(2, source.CountRangesRequested.Count(w => w.SearchesOnly));
        }

        private static Task<AppInsightsImportCycleResult> RunCycle(FixedClock clock, FakeAppInsightsSourceLoader source,
            IKeyValueStore state, UserImportScope scope = null, CancellationToken token = default) =>
            new AppInsightsImporter(new AppConfig { CognitiveEndpoint = null }, AnalyticsLogger.ConsoleOnlyTracer(), clock, source,
                contextFactory: _factory, userScope: scope, cursorStore: new KeyValueIngestionCursorStore(state, "contoso-test"))
                .ImportAndSave(false, null, token);

        private static SearchEventAppInsightsQueryResult Search(DateTime sourceTime, string session, string term, string user) =>
            new SearchEventAppInsightsQueryResult
            {
                AppInsightsTimestamp = sourceTime, Username = user,
                CustomProperties = new SearchCustomProps { PageRequestId = RequestId(sourceTime), SessionId = session, SearchText = term, EventTimestamp = Start.AddMilliseconds(3) }
            };

        private static Guid RequestId(DateTime time) => new Guid((int)(time - Start.Date).TotalSeconds, 0, 0, new byte[8]);

        private static PageViewAppInsightsQueryResult Page(DateTime time, string session) => new PageViewAppInsightsQueryResult
        {
            AppInsightsTimestamp = time, Username = Reader, Browser = "Edge", DeviceModel = "PC", ClientOS = "Windows",
            Url = "https://contoso.sharepoint.com/sites/example/SitePages/Home.aspx",
            CustomProperties = new PageViewCustomProps
            {
                PageRequestId = RequestId(time), SessionId = session, SiteUrl = "https://contoso.sharepoint.com/sites/example",
                WebUrl = "https://contoso.sharepoint.com/sites/example"
            }
        };

        private static (long Searches, long Visits) SearchKpis()
        {
            using (var connection = new SqlConnection(_database.ConnectionString))
            using (var command = new SqlCommand(WebActivitySql.SearchKpis, connection))
            {
                command.Parameters.AddWithValue("@from", Start.Date);
                command.Parameters.AddWithValue("@to", Start.Date.AddDays(1));
                command.Parameters.AddWithValue("@searchGrace", WebActivitySql.SearchDeadEndGraceSeconds);
                command.Parameters.AddWithValue("@strugglingSearches", 3);
                connection.Open();
                using (var r = command.ExecuteReader())
                {
                    Assert.IsTrue(r.Read());
                    return ((long)r["Searches"], (long)r["SessionsWithSearch"]);
                }
            }
        }
    }
}
