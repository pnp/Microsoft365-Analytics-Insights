using Common.Entities;
using Common.Entities.Entities.WebTraffic;
using Common.Entities.SpoWebActivity;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs every SharePoint web-activity query against a real, throwaway SQL Server database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store executes hand-written SQL, so a typo, a wrong column name, or a result type EF
    /// cannot materialise is invisible to the compiler and only appears when an admin opens the
    /// page. This test exists to make that failure happen in CI instead - and it covers more than
    /// smoke: <c>Database.SqlQuery&lt;T&gt;</c> reads each column with a TYPED getter, so a
    /// <c>COUNT(*)</c> (int) landing in a <c>long</c> property throws at runtime.
    /// </para>
    /// <para>
    /// It also pins the definitions the page's credibility rests on, each of which is easy to get
    /// quietly wrong:
    /// </para>
    /// <list type="number">
    /// <item>A bounce is a visit with exactly ONE page view, not a visit with a short dwell time.</item>
    /// <item>Unique page views count a page once per VISIT, so a refresh inflates page views but not
    /// unique page views.</item>
    /// <item>A page-to-page step never counts a move to the same page, or a refresh dominates the
    /// journey table.</item>
    /// <item>A search is a dead end only when no page view follows it outside the grace window - the
    /// search results page is itself a page view.</item>
    /// </list>
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class WebActivitySqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>
        /// "Καλημέρα κόσμε" on a genuinely Unicode column.
        /// </summary>
        /// <remarks>
        /// Deliberately a PAGE TITLE and a URL, not a user principal name. <c>dbo.users.user_name</c>
        /// is <c>varchar(250)</c> because Entra restricts a UPN to ASCII, so a non-Latin value there
        /// would be corrupted at rest and a test asserting it round-trips would be asserting a bug.
        /// <c>page_titles.title</c> and <c>urls.full_url</c> are <c>nvarchar</c>, so this crosses a
        /// real encoding boundary - and SharePoint page names genuinely are non-Latin in plenty of
        /// tenants.
        /// </remarks>
        private const string GreekTitle = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";

        private static DateTime Today => DateTime.UtcNow.Date;

        /// <summary>
        /// The day the seeded traffic happens on: a weekday, and inside the reporting window.
        /// </summary>
        /// <remarks>
        /// Pinned to a weekday because the out-of-hours measure treats the weekend as out of hours.
        /// Seeding on "today minus four days" would make the expected value depend on which day the
        /// test happened to run.
        /// </remarks>
        private static DateTime TrafficDay
        {
            get
            {
                var day = Today.AddDays(-4);
                while (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                {
                    day = day.AddDays(-1);
                }

                return day;
            }
        }

        private static int _homeUrlId, _newsUrlId, _policyUrlId, _greekUrlId, _quietUrlId, _slowUrlId;

        private const string SiteUrl = "https://contoso.sharepoint.com/sites/intranet";

        /// <summary>
        /// The home page's exact URL.
        /// </summary>
        /// <remarks>
        /// Matched exactly rather than by suffix. The Greek site page is also called
        /// <c>Home.aspx</c> - it just lives in a different folder - so an <c>EndsWith</c> match
        /// silently selects two rows, which is how this test first failed.
        /// </remarks>
        private const string HomeUrl = SiteUrl + "/SitePages/Home.aspx";

        private const string NewsUrl = SiteUrl + "/SitePages/News.aspx";
        private const string PolicyUrl = SiteUrl + "/SitePages/HR-policies.aspx";
        private const string QuietUrl = SiteUrl + "/SitePages/Archive-2019.aspx";
        private const string SlowUrl = SiteUrl + "/SitePages/Expenses.aspx";

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = "WebActivityIntegration_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, $"CREATE DATABASE [{_database}];");

            // The full migration chain, so the queries run against the schema customers actually get
            // rather than a hand-built subset that could omit the very column a query needs.
            var migrationConfig = new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(
                    _connectionString, "Microsoft.Data.SqlClient"),
            };
            new DbMigrator(migrationConfig).Update();

            Seed();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            Execute(MasterConnection,
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        private static SqlWebActivityStore NewStore() =>
            new SqlWebActivityStore(new ConnectionStringAnalyticsDbContextFactory(_connectionString));

        private static WebActivityQuery NewQuery(int days = 28) =>
            WebActivityQuery.Create(days, DateTime.UtcNow);

        #region Seeding

        /// <summary>
        /// A tiny but deliberately awkward intranet.
        /// </summary>
        /// <remarks>
        /// <para>Six visits, chosen so every measure on the page has something non-trivial to find:</para>
        /// <list type="bullet">
        /// <item>a three-page visit that ends on the policy page;</item>
        /// <item>a two-page visit that refreshes the home page, so total and unique page views differ
        /// and a same-page step exists to be excluded from the journey table;</item>
        /// <item>two single-page bounces, one of them on a mobile device;</item>
        /// <item>a visit that searches and then keeps browsing;</item>
        /// <item>a visit that searches and stops, which is both the dead end and a third bounce.</item>
        /// </list>
        /// </remarks>
        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                // UPNs are ASCII by Entra policy - the Unicode samples are on the page title and URL.
                var ada = new User { UserPrincipalName = "ada@contoso.com", AccountEnabled = true };
                var grace = new User { UserPrincipalName = "grace@contoso.com", AccountEnabled = true };
                var alan = new User { UserPrincipalName = "alan@contoso.com", AccountEnabled = true };
                var absent = new User { UserPrincipalName = "katherine@contoso.com", AccountEnabled = true };
                db.users.AddRange(new[] { ada, grace, alan, absent });

                var site = new Site { UrlBase = SiteUrl, SiteId = Guid.NewGuid().ToString() };
                db.sites.Add(site);
                db.SaveChanges();

                var web = new Web { url_base = site.UrlBase, title = "Contoso intranet", site = site, site_id = site.ID };
                db.webs.Add(web);

                var edge = new Browser { browser_name = "Edge 129.0" };
                var chrome = new Browser { browser_name = "Chrome 129.0" };
                db.browsers.AddRange(new[] { edge, chrome });

                var workstation = new Device { device_name = "Workstation" };
                var iphone = new Device { device_name = "iPhone" };
                db.devices.AddRange(new[] { workstation, iphone });

                var windows = new Common.Entities.OperatingSystem { os_name = "Windows 11" };
                var ios = new Common.Entities.OperatingSystem { os_name = "iOS 18" };
                db.operating_systems.AddRange(new[] { windows, ios });

                var uk = new Country { country_name = "United Kingdom" };
                var london = new City { city_name = "London" };
                var england = new Province { province_name = "England" };
                db.countries.Add(uk);
                db.cities.Add(london);
                db.provinces.Add(england);

                db.SaveChanges();

                _homeUrlId = AddPage(db, HomeUrl, "Contoso intranet - Home");
                _newsUrlId = AddPage(db, NewsUrl, "Contoso news");
                _policyUrlId = AddPage(db, PolicyUrl, "HR policies");
                _greekUrlId = AddPage(db, SiteUrl + "/SitePages/" + GreekTitle + "/Home.aspx", GreekTitle);
                _quietUrlId = AddPage(db, QuietUrl, "Archive 2019");
                _slowUrlId = AddPage(db, SlowUrl, "Expenses claim form");
                db.SaveChanges();

                var context = new SeedContext
                {
                    Db = db,
                    Web = web,
                    Browser = edge,
                    MobileBrowser = chrome,
                    Device = workstation,
                    MobileDevice = iphone,
                    Os = windows,
                    MobileOs = ios,
                    Country = uk,
                    City = london,
                    Province = england,
                };

                // Visit 1: Home -> News -> HR policies. Ends on the policy page.
                var deep = NewSession(db, ada, "s-deep");
                AddHit(context, deep, _homeUrlId, TrafficDay.AddHours(9), 20, 0.8);
                AddHit(context, deep, _newsUrlId, TrafficDay.AddHours(9).AddMinutes(1), 60, 0.9);
                AddHit(context, deep, _policyUrlId, TrafficDay.AddHours(9).AddMinutes(3), 120, 1.4);

                // Visit 2: Home, refreshed, then the Greek page. The refresh is why total and unique
                // page views must differ, and why a same-page step must be excluded from journeys.
                var refreshed = NewSession(db, grace, "s-refresh");
                AddHit(context, refreshed, _homeUrlId, TrafficDay.AddHours(10), 15, 0.7);
                AddHit(context, refreshed, _homeUrlId, TrafficDay.AddHours(10).AddMinutes(1), 15, 0.7);
                AddHit(context, refreshed, _greekUrlId, TrafficDay.AddHours(10).AddMinutes(2), 45, 1.0);

                // Visits 3 and 4: single-page bounces, one from a phone.
                var bounce = NewSession(db, alan, "s-bounce");
                AddHit(context, bounce, _homeUrlId, TrafficDay.AddHours(11), 8, 0.6);

                var mobileBounce = NewSession(db, alan, "s-mobile");
                AddHit(context, mobileBounce, _slowUrlId, TrafficDay.AddHours(12), 200, 6.5, mobile: true);

                // Visit 5: searched, then kept going. Well outside the dead-end grace window.
                var searched = NewSession(db, ada, "s-search");
                AddHit(context, searched, _homeUrlId, TrafficDay.AddHours(13), 10, 0.7);
                AddHit(context, searched, _policyUrlId, TrafficDay.AddHours(13).AddMinutes(5), 90, 1.3);

                // Visit 6: searched and stopped. The search results page view lands INSIDE the grace
                // window, which is exactly the case a naive "was there a later hit?" test gets wrong.
                var deadEnd = NewSession(db, grace, "s-deadend");
                AddHit(context, deadEnd, _quietUrlId, TrafficDay.AddHours(14), 12, 0.9, withoutCountry: true);

                db.SaveChanges();

                var policyTerm = new SearchTerm { search_term = "holiday policy" };
                var missingTerm = new SearchTerm { search_term = "car parking permit" };
                db.search_terms.AddRange(new[] { policyTerm, missingTerm });
                db.SaveChanges();

                db.searches.Add(new Search
                {
                    session = searched,
                    search_term = policyTerm,
                    DateTime = TrafficDay.AddHours(13).AddMinutes(1),
                });

                db.searches.Add(new Search
                {
                    session = deadEnd,
                    search_term = missingTerm,
                    DateTime = TrafficDay.AddHours(14).AddSeconds(-5),
                });

                db.SaveChanges();

                // One element click, on the home page's first view.
                var firstHomeHit = db.hits
                    .Where(h => h.url.ID == _homeUrlId)
                    .OrderBy(h => h.hit_timestamp)
                    .First();

                db.Clicks.Add(new Clicks
                {
                    UrlId = _homeUrlId,
                    HitID = firstHomeHit.ID,
                    Title = new ClickedElementTitle { Name = "Book annual leave" },
                    TimeStamp = TrafficDay.AddHours(9).AddSeconds(5),
                });

                db.SaveChanges();
            }
        }

        private sealed class SeedContext
        {
            public AnalyticsEntitiesContext Db;
            public Web Web;
            public Browser Browser, MobileBrowser;
            public Device Device, MobileDevice;
            public Common.Entities.OperatingSystem Os, MobileOs;
            public Country Country;
            public City City;
            public Province Province;
        }

        /// <summary>Adds a page and its title, keeping the two ids paired for <see cref="AddHit"/>.</summary>
        private static readonly Dictionary<int, PageTitle> Titles = new Dictionary<int, PageTitle>();

        private static int AddPage(AnalyticsEntitiesContext db, string url, string title)
        {
            var page = new Url { FullUrl = url };
            var pageTitle = new PageTitle { title = title };
            db.urls.Add(page);
            db.page_titles.Add(pageTitle);
            db.SaveChanges();
            Titles[page.ID] = pageTitle;
            return page.ID;
        }

        private static UserSession NewSession(AnalyticsEntitiesContext db, User user, string id)
        {
            var session = new UserSession { ai_session_id = id, user = user };
            db.sessions.Add(session);
            db.SaveChanges();
            return session;
        }

        private static void AddHit(
            SeedContext context,
            UserSession session,
            int urlId,
            DateTime when,
            double seconds,
            double load,
            bool mobile = false,
            bool withoutCountry = false)
        {
            var db = context.Db;

            db.hits.Add(new Hit
            {
                url = db.urls.Single(u => u.ID == urlId),
                page_title = Titles[urlId],
                web = context.Web,
                session = session,
                hit_timestamp = when,
                seconds_on_page = seconds,
                page_load_time = load,
                page_request_id = Guid.NewGuid(),
                agent = mobile ? context.MobileBrowser : context.Browser,
                device = mobile ? context.MobileDevice : context.Device,
                os = mobile ? context.MobileOs : context.Os,
                // One seeded page view resolves to a city but NOT a country. Geo-IP genuinely does
                // this, and it is the case that tells a per-attribute denominator apart from a single
                // "located" total - without it the fixture cannot detect that regression.
                country = withoutCountry ? null : context.Country,
                city = context.City,
                location_province = context.Province,
            });
        }

        #endregion

        #region Tests

        [TestMethod]
        public async Task EverySectionRunsAgainstTheRealSchemaWithoutError()
        {
            var store = NewStore();
            var query = NewQuery();
            var sources = new WebActivitySources { WebTraffic = true, UserMetadata = true, AppInsightsConfigured = true };

            var sections = new List<Common.Entities.SpoWebActivity.WebActivitySection>
            {
                await store.GetOverviewAsync(query, sources),
                await store.GetVisitsAsync(query),
                await store.GetPagesAsync(query),
                await store.GetJourneysAsync(query),
                await store.GetGeographyAsync(query),
                await store.GetSearchAsync(query),
                await store.GetTechnologyAsync(query),
            };

            var failures = sections
                .SelectMany(s => s.Queries)
                .Where(q => q.Error != null)
                .Select(q => q.Key + ": " + q.Error)
                .ToList();

            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

            // Every section must also publish its SQL, or the "show me the query" popover the page
            // relies on to be checkable is empty.
            foreach (var section in sections)
            {
                Assert.IsTrue(section.Queries.Count > 0);
                foreach (var info in section.Queries)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(info.Sql), info.Key);
                    StringAssert.Contains(info.Sql, "DECLARE @from", info.Key);
                }
            }
        }

        [TestMethod]
        public async Task Overview_CountsVisitsVisitorsAndBouncesFromTheSeededTraffic()
        {
            var overview = await NewStore().GetOverviewAsync(
                NewQuery(), new WebActivitySources { WebTraffic = true, UserMetadata = true });

            var kpis = overview.Kpis;

            Assert.AreEqual(6, kpis.Visits);
            Assert.AreEqual(11, kpis.PageViews);
            Assert.AreEqual(3, kpis.Visitors, "The fourth user never visited.");
            Assert.AreEqual(4, kpis.KnownUsers, "Reach is measured against the whole enabled directory.");
            Assert.AreEqual(6, kpis.UniquePages);
            Assert.AreEqual(1, kpis.Sites);

            // The refreshed home page is one unique page view but two page views.
            Assert.AreEqual(10, kpis.UniquePageViews);

            // Three of six visits saw exactly one page.
            Assert.AreEqual(3, overview.VisitDepth.Single(b => b.Key == "1 page").Count);
            Assert.AreEqual(50.0, kpis.BouncePct, 0.01);
            Assert.AreEqual(11.0 / 6, kpis.PagesPerVisit, 0.01);

            Assert.IsTrue(kpis.AverageLoadSeconds > 0);
            Assert.IsTrue(kpis.AverageSecondsOnPage > 0);

            // One of the eleven page views came from a phone, and every page view has a device.
            Assert.AreEqual(100.0 / 11, kpis.MobilePageViewPct.Value, 0.01);

            // Reach is the ENABLED directory visitors over the enabled directory, so it can never
            // exceed 100% even when someone who visited has since been disabled.
            Assert.AreEqual(75.0, kpis.ReachPct.Value, 0.01);
            Assert.IsTrue(kpis.DirectoryImported);

            Assert.IsTrue(overview.Judgements.Count > 0);
            Assert.IsTrue(overview.Heatmap.Sum(c => c.Visits) == 6,
                "Visits are counted where they STARTED, so the heatmap cells sum to the visit total.");
        }

        [TestMethod]
        public async Task Pages_SeparateTotalFromUniqueViewsAndRankEntriesAndBounces()
        {
            var pages = await NewStore().GetPagesAsync(NewQuery());

            var home = pages.TopPages.Single(p => p.Url == HomeUrl);
            Assert.AreEqual(5, home.PageViews, "Four visits started here, one of which refreshed.");
            Assert.AreEqual(4, home.UniquePageViews, "A refresh does not make a second unique page view.");
            Assert.AreEqual(4, home.Entries);
            Assert.AreEqual(1, home.Bounces);
            Assert.AreEqual(25.0, home.BouncePct.Value, 0.01);

            // The Unicode page title must survive the round trip through nvarchar.
            Assert.IsTrue(pages.TopPages.Any(p => p.Title == GreekTitle),
                "A non-Latin page title must render, not arrive as question marks.");

            Assert.AreEqual(11, pages.Kpis.PageViews);
            Assert.AreEqual(10, pages.Kpis.UniquePageViews);

            var quiet = pages.QuietPages.Select(p => p.Url).ToList();
            Assert.IsTrue(quiet.Contains(QuietUrl), "A page viewed once belongs on the pruning list.");
            Assert.IsFalse(quiet.Contains(HomeUrl), "The busiest page must never appear on the pruning list.");

            Assert.AreEqual(1, pages.BySite.Count);
            Assert.AreEqual(11, pages.BySite[0].PageViews);
            Assert.AreEqual(10, pages.BySite[0].UniquePageViews);
        }

        [TestMethod]
        public async Task Journeys_ExcludeSamePageStepsAndRankEntryAndExitPages()
        {
            var journeys = await NewStore().GetJourneysAsync(NewQuery());

            Assert.AreEqual(6, journeys.Kpis.Visits);
            Assert.AreEqual(3, journeys.Kpis.Bounces);
            Assert.AreEqual(1, journeys.Kpis.Clicks);
            Assert.IsTrue(journeys.Kpis.AverageVisitSeconds.HasValue);

            var entry = journeys.EntryPages.Single(p => p.Url == HomeUrl);
            Assert.AreEqual(4, entry.Entries);

            var exits = journeys.ExitPages.ToDictionary(p => p.Url, p => p.Exits);
            Assert.AreEqual(2, exits[PolicyUrl], "Two visits ended on the policy page.");

            // The refreshed home page produced a Home -> Home step. If it were counted, a refresh
            // would out-rank every real route on a busy intranet.
            Assert.IsFalse(journeys.Transitions.Any(t => t.FromUrl == t.ToUrl),
                "A step to the same page is a refresh, not a journey.");

            var homeToNews = journeys.Transitions.Single(t => t.FromUrl == HomeUrl && t.ToUrl == NewsUrl);
            Assert.AreEqual(1, homeToNews.Count);

            // Shares are of the steps OUT of the From page, so they answer "where do people go from
            // here?" rather than "what fraction of all journeys is this?".
            var fromHome = journeys.Transitions.Where(t => t.FromUrl == HomeUrl).Sum(t => t.SharePct);
            Assert.AreEqual(100, fromHome, 0.01);
        }

        [TestMethod]
        public async Task Search_CountsADeadEndOnlyWhenNothingFollowedOutsideTheGraceWindow()
        {
            var search = await NewStore().GetSearchAsync(NewQuery());

            Assert.AreEqual(2, search.Kpis.Searches);
            Assert.AreEqual(2, search.Kpis.Terms);
            Assert.AreEqual(2, search.Kpis.Searchers);
            Assert.AreEqual(2, search.Kpis.SessionsWithSearch);

            // Exactly one dead end. The other search was followed by a page view five minutes later;
            // if the grace window were missing, BOTH searches would look successful because the
            // results page view itself is a hit.
            Assert.AreEqual(1, search.Kpis.DeadEndSearches);

            var missing = search.TopTerms.Single(t => t.Term == "car parking permit");
            Assert.AreEqual(1, missing.Searches);
            Assert.AreEqual(1, missing.DeadEnds);
            Assert.AreEqual(100, missing.DeadEndPct, 0.01);

            var found = search.TopTerms.Single(t => t.Term == "holiday policy");
            Assert.AreEqual(0, found.DeadEnds);

            // Six visits, two of which searched.
            Assert.AreEqual(100.0 * 2 / 6, search.Kpis.SearchReliancePct, 0.01);

            // The UI states the grace window, so it has to come from the same constant the SQL uses
            // rather than being re-typed into a tooltip - which is how it went stale once already.
            Assert.AreEqual(WebActivitySql.SearchDeadEndGraceSeconds, search.Kpis.DeadEndGraceSeconds);
        }

        [TestMethod]
        public async Task Technology_SplitsPlatformsAndEstimatesTheSlowTail()
        {
            var technology = await NewStore().GetTechnologyAsync(NewQuery());

            Assert.AreEqual(2, technology.Kpis.Browsers);
            Assert.AreEqual(2, technology.Kpis.OperatingSystems);
            Assert.AreEqual(2, technology.Kpis.Devices);
            Assert.AreEqual(100.0 / 11, technology.Kpis.MobilePct.Value, 0.01);
            Assert.AreEqual(11, technology.Kpis.KnownDevicePageViews);
            Assert.AreEqual(11, technology.Kpis.PageViews);

            // One page view took 6.5 seconds, so the 95th percentile must land on the slow tail
            // rather than near the mean.
            Assert.IsTrue(technology.Kpis.P95LoadSeconds >= 6.5,
                "The percentile estimate must never understate the slow tail: got "
                + technology.Kpis.P95LoadSeconds);
            Assert.IsFalse(technology.Kpis.P95AtCeiling, "6.5s is well inside the histogram.");
            Assert.IsTrue(technology.Kpis.AverageLoadSeconds < 2.0);

            Assert.IsTrue(technology.Detail.Count > 0);
            Assert.AreEqual(11, technology.Detail.Sum(d => d.PageViews));
            Assert.IsTrue(technology.Browsers.Sum(b => b.SharePct) > 99.9);
        }

        [TestMethod]
        public async Task Geography_ResolvesPlacesAndReportsWhatItCouldNotResolve()
        {
            var geography = await NewStore().GetGeographyAsync(NewQuery());

            Assert.AreEqual(1, geography.Kpis.Countries);
            Assert.AreEqual(1, geography.Kpis.Cities);
            Assert.AreEqual(1, geography.Kpis.Provinces);
            Assert.AreEqual(11, geography.Kpis.LocatedPageViews);

            // Each list's share is against the page views that resolved to THAT attribute. A page
            // view with a city but no country belongs to neither a country row nor the country
            // denominator, so a single 'located' total would inflate every country's share.
            // The three denominators must genuinely differ, or this fixture could not tell a
            // per-attribute total from the looser 'located' one - which is the bug being guarded.
            Assert.AreEqual(10, geography.Kpis.CountryPageViews, "One page view has a city but no country.");
            Assert.AreEqual(11, geography.Kpis.CityPageViews);
            Assert.AreEqual(11, geography.Kpis.ProvincePageViews);
            Assert.AreEqual(0, geography.Kpis.UnknownLocationPageViews);
            Assert.AreEqual(6, geography.Kpis.Visits);

            Assert.AreEqual("United Kingdom", geography.Countries[0].Name);
            Assert.AreEqual(10, geography.Countries[0].PageViews);

            // 100% of the page views that RESOLVED TO A COUNTRY, not of all located traffic - the
            // city-only page view belongs to neither this row nor its denominator.
            Assert.AreEqual(100, geography.Countries[0].SharePct, 0.01);

            // A city carries its country so two same-named cities can be told apart.
            Assert.AreEqual("United Kingdom", geography.Cities[0].Country);
        }

        [TestMethod]
        public async Task Visits_ReportTheFirstAndLastVisitHourAndTheOutOfHoursShare()
        {
            var visits = await NewStore().GetVisitsAsync(NewQuery());

            Assert.AreEqual(6, visits.Kpis.Visits);
            Assert.AreEqual(3, visits.Kpis.Visitors);
            Assert.AreEqual(9, visits.Kpis.EarliestVisitHour);
            Assert.AreEqual(14, visits.Kpis.LatestVisitHour);
            Assert.AreEqual(0, visits.Kpis.OutOfHoursVisits, "All seeded traffic is on a weekday, 09:00-14:00 UTC.");

            // Derived views of the same day/hour grid must agree with each other and with the total.
            Assert.AreEqual(6, visits.ByDay.Sum(b => b.Count));
            Assert.AreEqual(6, visits.ByHour.Sum(b => b.Count));
            Assert.AreEqual(6, visits.ByPeriodOfDay.Sum(b => b.Count));

            Assert.AreEqual(1, visits.BySite.Count);
            Assert.IsTrue(visits.ByBrowser.Count >= 2);
        }

        [TestMethod]
        public async Task AWindowThatExcludesTheTraffic_ReturnsZerosRatherThanFailing()
        {
            // The seeded traffic is four days old, so a window ending well before it must be empty -
            // and empty must mean zeros, not nulls the UI would render as "NaN".
            var store = NewStore();
            var past = WebActivityQuery.Create(7, DateTime.UtcNow.AddDays(-60));

            var overview = await store.GetOverviewAsync(past, new WebActivitySources { WebTraffic = true });

            Assert.AreEqual(0, overview.Kpis.PageViews);
            Assert.AreEqual(0, overview.Kpis.Visits);
            Assert.AreEqual(0, overview.Kpis.BouncePct, 1e-9);
            Assert.AreEqual(0, overview.Kpis.PagesPerVisit, 1e-9);
            Assert.IsNull(overview.Kpis.AverageLoadSeconds, "No data is not a zero-second page load.");
            Assert.IsNull(overview.Kpis.AverageSecondsOnPage);
            Assert.IsNull(overview.Kpis.MobilePageViewPct);
            Assert.IsNull(overview.Kpis.ReachPct, "No visitors is not 0% of the directory... it is 0%.");
            Assert.AreEqual(0, overview.Queries.Count(q => q.Error != null));

            // The directory is not windowed, so reach still has a denominator.
            Assert.AreEqual(4, overview.Kpis.KnownUsers);

            var judgement = overview.Judgements.Single();
            Assert.AreEqual("no-traffic", judgement.Key);
        }

        [TestMethod]
        public async Task Availability_ReadsTheLatestHitIgnoringTheReportingWindow()
        {
            var store = NewStore();

            var collection = await store.GetCollectionStatusAsync();
            Assert.IsTrue(collection.Readable, "A successful read must say so, not just return a date.");
            Assert.IsNotNull(collection.LastHitUtc);
            Assert.AreEqual(TrafficDay.AddHours(14), collection.LastHitUtc.Value);
            Assert.AreEqual(DateTimeKind.Utc, collection.LastHitUtc.Value.Kind,
                "An unstamped timestamp serialises with no timezone and is parsed as local time.");

            var optional = await store.GetOptionalFeatureUseAsync();
            Assert.IsNotNull(optional);
            Assert.IsTrue(optional.Item1, "Searches exist.");
            Assert.IsTrue(optional.Item2, "Element clicks exist.");
        }

        #endregion
    }
}
