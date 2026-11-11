extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.SpoWebActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The privacy floor on the SharePoint web activity page (#844, G2): a reader without the portal's See PII permission is
    /// never sent search terms, a list that counts no people, or a site, place, platform or technology row of fewer than five
    /// visitors - on screen or in a file - while a See PII reader is sent the cached section exactly as it was built.
    /// </summary>
    [TestClass]
    public class WebActivityPrivacyFloorTests
    {
        private const int Minimum = 5;

        [TestMethod]
        public async Task Search_WithoutSeePii_WithholdsEveryTermAndTheSitesSearched_AndTheCachedSectionKeepsThemForSeePii()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 41);
            await WithHost(store, new[] { query.CacheKey("search") }, async host =>
            {
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/search?days=28&top=41"));
                Assert.AreEqual(0, ((JArray)trimmed["topTerms"]).Count, "A search term is text a person typed.");
                Assert.AreEqual(0, ((JArray)trimmed["deadEndTerms"]).Count);
                Assert.IsTrue((bool)trimmed["termsWithheld"]);
                Assert.AreEqual(0, ((JArray)trimmed["bySite"]).Count, "Nothing counts the people who searched on a site.");
                Assert.IsTrue((bool)trimmed["sitesWithheld"]);
                Assert.AreEqual(Minimum, (int)trimmed["minimumGroupSize"]);
                Assert.IsFalse(trimmed.ToString().Contains("Alex Contoso"), "Not one typed term reaches the reader.");
                Assert.IsFalse(trimmed.ToString().Contains("parental leave"));
                Assert.AreEqual(17, (long)trimmed["kpis"]["searches"], "The scope's own totals are still there.");
                Assert.AreEqual(10, (int)trimmed["kpis"]["searchers"]);
                Assert.AreEqual(2, ((JArray)trimmed["trend"]).Count);

                // The next reader holds the permission and is served the complete section from the same cache entry.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/search?days=28&top=41"));
                CollectionAssert.AreEqual(
                    new[] { "payroll form", "Alex Contoso" },
                    full["topTerms"].Select(t => (string)t["term"]).ToArray());
                Assert.AreEqual("parental leave", (string)full["deadEndTerms"][0]["term"]);
                Assert.AreEqual(1, ((JArray)full["bySite"]).Count);
                Assert.IsFalse((bool)full["termsWithheld"]);
                Assert.AreEqual(JTokenType.Null, full["minimumGroupSize"].Type);
                Assert.AreEqual(1, store.SearchCalls, "The second reader must have been served from the cache.");
            });
        }

        [TestMethod]
        public async Task Geography_WithoutSeePii_LeavesOutPlacesOfFewerThanFiveVisitors_AndWhatIsLeftOutIsNeverOneSmallPlace()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 42);
            await WithHost(store, new[] { query.CacheKey("geography") }, async host =>
            {
                // A See PII reader first: the cache must not keep anything trimmed for the reader after them.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/geography?days=28&top=42"));
                Assert.AreEqual(3, ((JArray)full["countries"]).Count);
                Assert.AreEqual(4, ((JArray)full["cities"]).Count);
                Assert.AreEqual(0, (int)full["withheldCities"]);
                Assert.AreEqual(JTokenType.Null, full["minimumGroupSize"].Type);

                host.Principal = PortalTestHost.SignedIn();
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/geography?days=28&top=42"));
                Assert.AreEqual(1, store.GeographyCalls, "The second reader must have been served from the cache.");
                Assert.AreEqual(Minimum, (int)trimmed["minimumGroupSize"]);

                // Portugal had one visitor. Spain, the smallest country shown, goes with it, so the country page views less
                // the countries shown are Spain and Portugal together - never Portugal's own figure.
                CollectionAssert.AreEqual(new[] { "United Kingdom" }, trimmed["countries"].Select(c => (string)c["name"]).ToArray());
                Assert.AreEqual(2, (int)trimmed["withheldCountries"]);
                var remainder = (long)trimmed["kpis"]["countryPageViews"] - trimmed["countries"].Sum(c => (long)c["pageViews"]);
                Assert.AreEqual(9 + 120, remainder);
                CollectionAssert.AreEqual(new[] { "London" }, trimmed["cities"].Select(c => (string)c["name"]).ToArray());
                Assert.AreEqual(3, (int)trimmed["withheldCities"], "Lisbon and Θεσσαλονίκη, and Madrid beside them.");
                CollectionAssert.AreEqual(new[] { "England" }, trimmed["provinces"].Select(c => (string)c["name"]).ToArray());
                Assert.AreEqual(2, (int)trimmed["withheldProvinces"]);
                foreach (var place in new[] { "Lisbon", "Θεσσαλονίκη", "Madrid", "Portugal", "Spain", "Attica" })
                    Assert.IsFalse(trimmed.ToString().Contains(place), place + " is a place too few people visited from.");

                // The countries over time draw no series of their own for a country the list leaves out.
                CollectionAssert.AreEquivalent(
                    new[] { "United Kingdom", "(other countries)", "(unknown country)" },
                    trimmed["countryOverTime"].Select(p => (string)p["name"]).Distinct().ToArray());
                Assert.AreEqual(
                    60 + 5,
                    trimmed["countryOverTime"].Where(p => (string)p["name"] == "(other countries)").Sum(p => (long)p["count"]),
                    "Spain's and Portugal's weeks are folded together, with the week's total unchanged.");
                Assert.AreEqual(
                    store.Geography.CountryOverTime.Sum(p => p.Count),
                    trimmed["countryOverTime"].Sum(p => (long)p["count"]));
                Assert.AreEqual(529, (long)trimmed["kpis"]["countryPageViews"], "The scope's own totals are still there.");
            });
        }

        [TestMethod]
        public async Task Technology_WithoutSeePii_LeavesOutDetailRowsAndPlatformsOfFewerThanFiveVisitors()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 43);
            await WithHost(store, new[] { query.CacheKey("technology") }, async host =>
            {
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=43"));
                Assert.AreEqual(Minimum, (int)trimmed["minimumGroupSize"]);
                CollectionAssert.AreEqual(
                    new[] { "Edge/London", "Chrome/London" },
                    trimmed["detail"].Select(r => (string)r["browser"] + "/" + (string)r["city"]).ToArray(),
                    "Firefox in Lisbon was one person; Chrome in Madrid, the smallest row shown, goes with it.");
                Assert.AreEqual(2, (int)trimmed["withheldDetailRows"]);
                CollectionAssert.AreEqual(new[] { "Edge" }, trimmed["browsers"].Select(r => (string)r["name"]).ToArray());
                Assert.AreEqual(2, (int)trimmed["withheldBrowsers"]);
                CollectionAssert.AreEquivalent(
                    new[] { "Desktop", "(other devices)" },
                    trimmed["deviceOverTime"].Select(p => (string)p["name"]).Distinct().ToArray());
                Assert.AreEqual(1000, (long)trimmed["kpis"]["pageViews"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=43"));
                Assert.AreEqual(4, ((JArray)full["detail"]).Count);
                Assert.AreEqual("Lisbon", (string)full["detail"][3]["city"]);
                Assert.AreEqual(3, ((JArray)full["browsers"]).Count);
                Assert.AreEqual(1, store.TechnologyCalls, "The second reader must have been served from the cache.");
            });
        }

        [TestMethod]
        public async Task PagesJourneysVisitsAndOverview_WithoutSeePii_WithholdTheListsThatCountNoPeople_AndLeaveOutSmallSites()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 44);
            var keys = new[] { "pages", "journeys", "visits" }.Select(query.CacheKey)
                .Concat(new[] { WebActivityQuery.Create(28, DateTime.UtcNow).CacheKey("overview") })
                .ToArray();
            await WithHost(store, keys, async host =>
            {
                var pages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=44"));
                Assert.IsTrue((bool)pages["pageListsWithheld"]);
                foreach (var list in new[] { "topPages", "slowestPages", "quietPages" })
                    Assert.AreEqual(0, ((JArray)pages[list]).Count, list + " counts no people.");
                CollectionAssert.AreEqual(new[] { "Contoso Intranet" }, pages["bySite"].Select(s => (string)s["name"]).ToArray());
                Assert.AreEqual(2, (int)pages["withheldSites"], "Contoso Legal's three visitors, and Contoso HR beside them.");
                Assert.IsFalse(pages.ToString().Contains("https://contoso.sharepoint.com/sites/legal/SitePages/Quiet.aspx"));
                Assert.AreEqual(1000, (long)pages["kpis"]["pageViews"]);

                var journeys = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=44"));
                Assert.IsTrue((bool)journeys["pageListsWithheld"]);
                Assert.IsTrue((bool)journeys["pathsWithheld"]);
                Assert.IsTrue((bool)journeys["clickedElementsWithheld"]);
                foreach (var list in new[] { "entryPages", "exitPages", "bouncePages", "transitions", "flows", "clickedElements" })
                    Assert.AreEqual(0, ((JArray)journeys[list]).Count, list + " counts no people.");
                Assert.AreEqual(300, (long)journeys["kpis"]["visits"]);
                Assert.AreEqual(2, ((JArray)journeys["depth"]).Count);

                var visits = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/visits?days=28&top=44"));
                Assert.IsTrue((bool)visits["pagesWithheld"]);
                Assert.AreEqual(0, ((JArray)visits["byPage"]).Count);
                CollectionAssert.AreEqual(new[] { "Contoso Intranet" }, visits["bySite"].Select(s => (string)s["name"]).ToArray());
                Assert.AreEqual(2, (int)visits["withheldSites"]);
                CollectionAssert.AreEqual(new[] { "Desktop" }, visits["byDevice"].Select(s => (string)s["name"]).ToArray());
                Assert.AreEqual(2, (int)visits["withheldDevices"]);
                CollectionAssert.AreEquivalent(
                    new[] { "Contoso Intranet", "(other sites)" },
                    visits["siteOverTime"].Select(p => (string)p["name"]).Distinct().ToArray());
                Assert.IsNull(visits["bySite"][0]["people"], "The people count only decides; it is never sent.");

                var overview = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/overview?days=28"));
                CollectionAssert.AreEqual(new[] { "Contoso Intranet" }, overview["topSites"].Select(s => (string)s["name"]).ToArray());
                Assert.AreEqual(2, (int)overview["withheldSites"]);
                Assert.AreEqual(1000, (long)overview["kpis"]["pageViews"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var fullPages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=44"));
                Assert.AreEqual(1, ((JArray)fullPages["quietPages"]).Count);
                Assert.IsFalse((bool)fullPages["pageListsWithheld"]);
                Assert.AreEqual(3, ((JArray)fullPages["bySite"]).Count);
                var fullJourneys = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=44"));
                Assert.AreEqual(1, ((JArray)fullJourneys["transitions"]).Count);
                Assert.AreEqual(1, ((JArray)fullJourneys["clickedElements"]).Count);
                var fullOverview = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/overview?days=28"));
                Assert.AreEqual(3, ((JArray)fullOverview["topSites"]).Count);
                Assert.AreEqual(1, store.PagesCalls);
                Assert.AreEqual(1, store.JourneysCalls);
                Assert.AreEqual(1, store.OverviewCalls);
            });
        }

        [TestMethod]
        public async Task Export_WithoutSeePii_RefusesEveryFileItsScreenWithholds_AndTheTechnologyFileCarriesTheScreensRows()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 45);
            var keys = WebActivityExports.Sections
                .SelectMany(s => new[] { query.CacheKey("export::" + s), query.CacheKey("export::" + s + "::without-see-pii") })
                .ToArray();
            await WithHost(store, keys, async host =>
            {
                foreach (var section in WebActivityExports.Sections.Where(s => s != "technology"))
                {
                    var refused = await host.Client.GetAsync("api/WebActivity/export/" + section + "?days=28&top=45");
                    Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, section + " is withheld on screen, so the file is too.");
                    var body = JObject.Parse(await refused.Content.ReadAsStringAsync());
                    Assert.AreEqual("portalPermissionRequired", (string)body["code"], section);
                }
                Assert.AreEqual(0, store.SearchCalls + store.PagesCalls + store.JourneysCalls, "A refused file runs no query.");
                Assert.IsTrue(WebActivityExports.WithheldWithoutSeePii("SEARCH-TERMS"));
                Assert.IsFalse(WebActivityExports.WithheldWithoutSeePii("technology"));

                var floored = await host.Client.GetStringAsync("api/WebActivity/export/technology?days=28&top=45");
                StringAssert.Contains(floored, "London");
                Assert.IsFalse(floored.Contains("Lisbon"), "A row of one visitor is left out of the file as it is on the screen.");
                Assert.IsFalse(floored.Contains("Madrid"));

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = await host.Client.GetStringAsync("api/WebActivity/export/technology?days=28&top=45");
                StringAssert.Contains(full, "Lisbon", "The floored file must not be cached for a See PII reader.");
                StringAssert.Contains(full, "Madrid");
                var terms = await host.Client.GetStringAsync("api/WebActivity/export/search-terms?days=28&top=45");
                StringAssert.Contains(terms, "Alex Contoso");

                // And the other way round: the full file must not be served to the next reader without See PII.
                host.Principal = PortalTestHost.SignedIn();
                var again = await host.Client.GetStringAsync("api/WebActivity/export/technology?days=28&top=45");
                Assert.IsFalse(again.Contains("Lisbon"));
            });
        }

        [TestMethod]
        public void WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut()
        {
            var a = new WebActivityPlatformRow { Name = "Contoso A", Visits = 90, PageViews = 100, Visitors = 40 };
            var b = new WebActivityPlatformRow { Name = "Contoso B", Visits = 10, PageViews = 300, Visitors = 6 };
            var c = new WebActivityPlatformRow { Name = "Contoso C", Visits = 30, PageViews = 20, Visitors = 6 };
            var d = new WebActivityPlatformRow { Name = "Contoso D", Visits = 50, PageViews = 9, Visitors = 4 };
            var byVisits = new List<WebActivityPlatformRow> { a, d, c, b };
            var byPageViews = new List<WebActivityPlatformRow> { b, a, c, d };

            var first = WebActivitySmallGroups.WithoutSmallRows(byVisits, r => r.Visitors, r => r.Name, Minimum, out var leftOutFirst);
            var second = WebActivitySmallGroups.WithoutSmallRows(byPageViews, r => r.Visitors, r => r.Name, Minimum, out var leftOutSecond);

            // D has four visitors. B and C tie on six; B goes with D on either ranking, so neither list gives away what the
            // other leaves out.
            CollectionAssert.AreEqual(new[] { a, c }, first);
            CollectionAssert.AreEqual(new[] { a, c }, second.OrderBy(r => r.Name).ToList());
            Assert.AreEqual(2, leftOutFirst);
            Assert.AreEqual(2, leftOutSecond);
            Assert.AreEqual(4, byVisits.Count, "The cached list is never edited.");

            // The total less the rows shown is D and B together, never D alone, and at least five people.
            var total = byVisits.Sum(r => r.PageViews);
            Assert.AreEqual(d.PageViews + b.PageViews, total - first.Sum(r => r.PageViews));
            Assert.IsTrue(b.Visitors >= Minimum);

            Assert.AreEqual(0, WebActivitySmallGroups.WithoutSmallRows(new[] { a, d }, r => r.Visitors, r => r.Name, Minimum, out var both).Count,
                "With one row big enough, it would be the total less the small ones: none is shown.");
            Assert.AreEqual(2, both);
            CollectionAssert.AreEqual(new[] { a, c }, WebActivitySmallGroups.WithoutSmallRows(new[] { a, c }, r => r.Visitors, r => r.Name, Minimum, out var none));
            Assert.AreEqual(0, none);
            Assert.AreEqual(0, WebActivitySmallGroups.WithoutSmallRows(
                new[] { new WebActivityNamedCount { Name = "Contoso", Count = 500 } }, r => r.People, r => r.Name, Minimum, out var uncounted).Count,
                "A row whose people were not counted is treated as small.");
            Assert.AreEqual(1, uncounted);
        }

        [TestMethod]
        public void WithoutSmallGroups_ReturnsACopy_AndLeavesTheCachedSectionAsItWasBuilt()
        {
            var store = new FakeWebActivityStore();

            var search = store.Search.WithoutSmallGroups(Minimum);
            Assert.AreEqual(2, store.Search.TopTerms.Count);
            Assert.IsFalse(store.Search.TermsWithheld);
            Assert.IsNull(store.Search.MinimumGroupSize);
            Assert.AreNotSame(store.Search, search);

            store.Geography.WithoutSmallGroups(Minimum);
            Assert.AreEqual(3, store.Geography.Countries.Count);
            Assert.AreEqual(0, store.Geography.WithheldCountries);
            Assert.IsTrue(store.Geography.CountryOverTime.Any(p => p.Name == "Portugal"));

            store.Technology.WithoutSmallGroups(Minimum);
            Assert.AreEqual(4, store.Technology.Detail.Count);
            store.Pages.WithoutSmallGroups(Minimum);
            Assert.AreEqual(1, store.Pages.QuietPages.Count);
            store.Journeys.WithoutSmallGroups(Minimum);
            Assert.AreEqual(1, store.Journeys.Flows.Count);
            store.Visits.WithoutSmallGroups(Minimum);
            Assert.AreEqual(3, store.Visits.BySite.Count);
            store.Overview.WithoutSmallGroups(Minimum);
            Assert.AreEqual(3, store.Overview.TopSites.Count);
        }

        private static async Task WithHost(FakeWebActivityStore store, IEnumerable<string> cacheKeys, Func<PortalTestHost, Task> test)
        {
            var keys = cacheKeys.ToList();
            foreach (var key in keys) MemoryCache.Default.Remove(key);
            try
            {
                using (var host = new PortalTestHost(
                    new[] { typeof(WebActivityAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new WebActivityAPIController(store, () => new WebActivitySources { Readable = true, WebTraffic = true })))
                {
                    await test(host);
                }
            }
            finally
            {
                foreach (var key in keys) MemoryCache.Default.Remove(key);
            }
        }

        /// <summary>One synthetic tenant: every list has rows of five people or more, and rows of one to four.</summary>
        private sealed class FakeWebActivityStore : IWebActivityStore
        {
            private int _overview, _visits, _pages, _journeys, _geography, _search, _technology;

            internal int OverviewCalls => _overview;
            internal int VisitsCalls => _visits;
            internal int PagesCalls => _pages;
            internal int JourneysCalls => _journeys;
            internal int GeographyCalls => _geography;
            internal int SearchCalls => _search;
            internal int TechnologyCalls => _technology;

            private static readonly DateTime Week1 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
            private static readonly DateTime Week2 = Week1.AddDays(7);

            internal WebActivityOverview Overview { get; } = new WebActivityOverview
            {
                Kpis = new WebActivityOverviewKpis { PageViews = 1000, Visits = 300, Visitors = 60 },
                TopSites = Sites().Select(s => new WebActivityNamedCount { Name = s.Name, Count = s.PageViews, People = s.Visitors }).ToList(),
            };

            internal WebActivityVisits Visits { get; } = new WebActivityVisits
            {
                Kpis = new WebActivityVisitKpis { Visits = 300, Visitors = 60 },
                BySite = Sites().Select(s => new WebActivityNamedCount { Name = s.Name, Count = s.Visits, People = s.Visitors }).ToList(),
                ByPage = new List<WebActivityNamedCount> { new WebActivityNamedCount { Name = "Contoso quiet page", Count = 1 } },
                ByDevice = new List<WebActivityNamedCount>
                {
                    new WebActivityNamedCount { Name = "Desktop", Count = 250, People = 50 },
                    new WebActivityNamedCount { Name = "Phone", Count = 40, People = 8 },
                    new WebActivityNamedCount { Name = "Tablet", Count = 10, People = 2 },
                },
                SiteOverTime = new List<WebActivityStackPoint>
                {
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso Intranet", Count = 400 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso Legal", Count = 30 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso HR", Count = 70 },
                },
            };

            internal WebActivityPages Pages { get; } = new WebActivityPages
            {
                Kpis = new WebActivityPageKpis { PageViews = 1000 },
                TopPages = new List<WebActivityPageRow> { Page("Home") },
                SlowestPages = new List<WebActivityPageRow> { Page("Slow") },
                QuietPages = new List<WebActivityPageRow> { Page("Quiet") },
                BySite = Sites(),
            };

            internal WebActivityJourneys Journeys { get; } = new WebActivityJourneys
            {
                Kpis = new WebActivityJourneyKpis { Visits = 300, Clicks = 12 },
                EntryPages = new List<WebActivityPageRow> { Page("Home") },
                ExitPages = new List<WebActivityPageRow> { Page("Home") },
                BouncePages = new List<WebActivityPageRow> { Page("Home") },
                Transitions = new List<WebActivityTransitionRow>
                {
                    new WebActivityTransitionRow { FromTitle = "Home", ToTitle = "Quiet", Count = 1, SharePct = 1 },
                },
                Flows = new List<WebActivityFlowRow> { new WebActivityFlowRow { StartTitle = "Home", EndTitle = "Quiet", Visits = 1, SharePct = 1 } },
                FlowsCoveragePct = 1,
                Depth = new List<WebActivityBucket>
                {
                    new WebActivityBucket { Key = "1", Label = "1", Count = 200 },
                    new WebActivityBucket { Key = "2-3", Label = "2-3", Count = 100 },
                },
                ClickedElements = new List<WebActivityNamedCount> { new WebActivityNamedCount { Name = "Book a desk", Count = 12 } },
            };

            internal WebActivityGeography Geography { get; } = new WebActivityGeography
            {
                Kpis = new WebActivityGeographyKpis { Visits = 300, Visitors = 60, Countries = 3, Cities = 4, Provinces = 3, CountryPageViews = 529 },
                Countries = new List<WebActivityPlaceRow>
                {
                    new WebActivityPlaceRow { Name = "United Kingdom", PageViews = 400, Visits = 120, Visitors = 40 },
                    new WebActivityPlaceRow { Name = "Spain", PageViews = 120, Visits = 30, Visitors = 12 },
                    new WebActivityPlaceRow { Name = "Portugal", PageViews = 9, Visits = 2, Visitors = 1 },
                },
                Cities = new List<WebActivityPlaceRow>
                {
                    new WebActivityPlaceRow { Name = "London", Country = "United Kingdom", PageViews = 300, Visits = 90, Visitors = 30 },
                    new WebActivityPlaceRow { Name = "Madrid", Country = "Spain", PageViews = 80, Visits = 20, Visitors = 8 },
                    new WebActivityPlaceRow { Name = "Θεσσαλονίκη", Country = "Greece", PageViews = 15, Visits = 3, Visitors = 2 },
                    new WebActivityPlaceRow { Name = "Lisbon", Country = "Portugal", PageViews = 9, Visits = 2, Visitors = 1 },
                },
                Provinces = new List<WebActivityPlaceRow>
                {
                    new WebActivityPlaceRow { Name = "England", Country = "United Kingdom", PageViews = 380, Visits = 110, Visitors = 35 },
                    new WebActivityPlaceRow { Name = "Comunidad de Madrid", Country = "Spain", PageViews = 80, Visits = 20, Visitors = 8 },
                    new WebActivityPlaceRow { Name = "Attica", Country = "Greece", PageViews = 15, Visits = 3, Visitors = 2 },
                },
                CountryOverTime = new List<WebActivityStackPoint>
                {
                    new WebActivityStackPoint { WeekStart = Week1, Name = "United Kingdom", Count = 200 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Spain", Count = 60 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Portugal", Count = 5 },
                    new WebActivityStackPoint { WeekStart = Week2, Name = "United Kingdom", Count = 200 },
                    new WebActivityStackPoint { WeekStart = Week2, Name = "(unknown country)", Count = 3 },
                },
            };

            internal WebActivitySearch Search { get; } = new WebActivitySearch
            {
                Kpis = new WebActivitySearchKpis { Searches = 17, Terms = 3, Searchers = 10 },
                TopTerms = new List<WebActivitySearchTermRow>
                {
                    new WebActivitySearchTermRow { Term = "payroll form", Searches = 12, Searchers = 9 },
                    new WebActivitySearchTermRow { Term = "Alex Contoso", Searches = 2, Searchers = 1 },
                },
                DeadEndTerms = new List<WebActivitySearchTermRow>
                {
                    new WebActivitySearchTermRow { Term = "parental leave", Searches = 3, DeadEnds = 3, DeadEndPct = 100 },
                },
                Trend = new List<WebActivityTrendPoint>
                {
                    new WebActivityTrendPoint { WeekStart = Week1, Searches = 9 },
                    new WebActivityTrendPoint { WeekStart = Week2, Searches = 8 },
                },
                BySite = new List<WebActivityNamedCount> { new WebActivityNamedCount { Name = "Contoso HR", Count = 17 } },
            };

            internal WebActivityTechnology Technology { get; } = new WebActivityTechnology
            {
                Kpis = new WebActivityTechnologyKpis { Browsers = 3, PageViews = 1000 },
                Browsers = new List<WebActivityPlatformRow>
                {
                    new WebActivityPlatformRow { Name = "Edge", PageViews = 800, Visits = 240, Visitors = 50 },
                    new WebActivityPlatformRow { Name = "Chrome", PageViews = 190, Visits = 55, Visitors = 9 },
                    new WebActivityPlatformRow { Name = "Firefox", PageViews = 10, Visits = 5, Visitors = 1 },
                },
                Devices = new List<WebActivityPlatformRow>
                {
                    new WebActivityPlatformRow { Name = "Desktop", PageViews = 900, Visits = 250, Visitors = 50 },
                    new WebActivityPlatformRow { Name = "Phone", PageViews = 90, Visits = 40, Visitors = 8 },
                    new WebActivityPlatformRow { Name = "Tablet", PageViews = 10, Visits = 10, Visitors = 2 },
                },
                DeviceOverTime = new List<WebActivityStackPoint>
                {
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Desktop", Count = 450 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Phone", Count = 45 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Tablet", Count = 5 },
                },
                Detail = new List<WebActivityTechnologyDetailRow>
                {
                    Detail("Edge", "London", 25),
                    Detail("Chrome", "London", 9),
                    Detail("Chrome", "Madrid", 6),
                    Detail("Firefox", "Lisbon", 1),
                },
            };

            private static List<WebActivitySiteRow> Sites() => new List<WebActivitySiteRow>
            {
                new WebActivitySiteRow { Name = "Contoso Intranet", Url = "https://contoso.sharepoint.com/sites/intranet", PageViews = 800, Visits = 250, Visitors = 50 },
                new WebActivitySiteRow { Name = "Contoso HR", Url = "https://contoso.sharepoint.com/sites/hr", PageViews = 150, Visits = 40, Visitors = 7 },
                new WebActivitySiteRow { Name = "Contoso Legal", Url = "https://contoso.sharepoint.com/sites/legal", PageViews = 50, Visits = 10, Visitors = 3 },
            };

            private static WebActivityPageRow Page(string title) => new WebActivityPageRow
            {
                Title = title,
                Url = "https://contoso.sharepoint.com/sites/legal/SitePages/" + title + ".aspx",
                Site = "Contoso Legal",
                PageViews = 2,
            };

            private static WebActivityTechnologyDetailRow Detail(string browser, string city, int visitors) => new WebActivityTechnologyDetailRow
            {
                Browser = browser,
                Device = "Desktop",
                OperatingSystem = "Windows",
                City = city,
                Visits = visitors * 3,
                Visitors = visitors,
                PageViews = visitors * 10,
            };

            public Task<WebActivityCollectionStatus> GetCollectionStatusAsync() =>
                Task.FromResult(new WebActivityCollectionStatus { Readable = true, LastHitUtc = DateTime.UtcNow });

            public Task<Tuple<bool, bool>> GetOptionalFeatureUseAsync() => Task.FromResult(Tuple.Create(true, true));

            public Task<WebActivityOverview> GetOverviewAsync(WebActivityQuery query, WebActivitySources sources)
            {
                Interlocked.Increment(ref _overview);
                return Task.FromResult(Overview);
            }

            public Task<WebActivityVisits> GetVisitsAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _visits);
                return Task.FromResult(Visits);
            }

            public Task<WebActivityPages> GetPagesAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _pages);
                return Task.FromResult(Pages);
            }

            public Task<WebActivityJourneys> GetJourneysAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _journeys);
                return Task.FromResult(Journeys);
            }

            public Task<WebActivityGeography> GetGeographyAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _geography);
                return Task.FromResult(Geography);
            }

            public Task<WebActivitySearch> GetSearchAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _search);
                return Task.FromResult(Search);
            }

            public Task<WebActivityTechnology> GetTechnologyAsync(WebActivityQuery query)
            {
                Interlocked.Increment(ref _technology);
                return Task.FromResult(Technology);
            }
        }
    }
}
