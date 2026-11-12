extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.SpoWebActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AnalyticsWeb::Web.AnalyticsWeb;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The privacy floor on the SharePoint web activity page (#844): a reader without the portal's See PII permission is
    /// never sent search terms, or a site, page, step, flow, clicked element, place, platform or technology row of fewer
    /// than five people - on screen or in a file - nor the smallest row still shown beside a row left out; a See PII reader
    /// is sent the cached section exactly as it was built.
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
                Assert.AreEqual(2, store.GeographyCalls, "This reader is cut from the section built for every row, cached apart.");
                await host.Client.GetStringAsync("api/WebActivity/geography?days=28&top=15");
                Assert.AreEqual(2, store.GeographyCalls, "Whatever rows a reader without See PII asks for, it is the same section.");
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
            await WithHost(store, new[] { query.CacheKey("technology"), query.CacheKey("geography") }, async host =>
            {
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=43"));
                Assert.AreEqual(Minimum, (int)trimmed["minimumGroupSize"]);
                CollectionAssert.AreEqual(
                    new[] { "Edge/London", "Chrome/London" },
                    trimmed["detail"].Select(r => (string)r["browser"] + "/" + (string)r["city"]).ToArray(),
                    "Firefox in Lisbon was one person, Madrid is a city Geography does not show, and Safari in London, the smallest "
                    + "row shown, goes with them.");
                Assert.AreEqual(3, (int)trimmed["withheldDetailRows"]);
                CollectionAssert.AreEqual(new[] { "Edge" }, trimmed["browsers"].Select(r => (string)r["name"]).ToArray());
                Assert.AreEqual(2, (int)trimmed["withheldBrowsers"]);
                CollectionAssert.AreEquivalent(
                    new[] { "Desktop", "(other devices)" },
                    trimmed["deviceOverTime"].Select(p => (string)p["name"]).Distinct().ToArray());
                Assert.AreEqual(1000, (long)trimmed["kpis"]["pageViews"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=43"));
                Assert.AreEqual(5, ((JArray)full["detail"]).Count);
                Assert.AreEqual("Lisbon", (string)full["detail"][4]["city"]);
                Assert.AreEqual(3, ((JArray)full["browsers"]).Count);
                Assert.AreEqual(2, store.TechnologyCalls, "A See PII reader is served the section as asked for, never this reader's every-row entry.");
            });
        }

        /// <summary>
        /// The #876 cross-tab residual: Geography leaves Madrid out as the smallest city it still shows (8 visitors), so the
        /// cities' page views less the cities shown are Madrid and the small cities together. Chrome in Madrid (8 visitors) is
        /// big enough on its own, and its page views would take Madrid back out of that remainder, leaving the small cities'.
        /// </summary>
        [TestMethod]
        public async Task Technology_WithoutSeePii_NamesOnlyCitiesGeographyShows_OnScreenAndInTheExport()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 49);
            var export = WebActivityQuery.Create(28, DateTime.UtcNow, 50);
            var keys = new[] { query.CacheKey("technology"), query.CacheKey("geography"), export.CacheKey("geography") }
                .Concat(new[] { "export::technology", "export::technology::without-see-pii" }.Select(export.CacheKey));
            await WithHost(store, keys, async host =>
            {
                var geography = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/geography?days=28&top=49"));
                CollectionAssert.AreEqual(new[] { "London" }, geography["cities"].Select(c => (string)c["name"]).ToArray());

                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=49"));
                Assert.IsFalse(trimmed["detail"].Any(r => (string)r["city"] == "Madrid"), "Geography left Madrid out.");
                Assert.AreEqual(1, store.GeographyCalls, "Technology is floored against Geography's own cached section.");

                var file = await host.Client.GetStringAsync("api/WebActivity/export/technology?days=28&top=50");
                Assert.IsFalse(file.Contains("Madrid"), "The file is floored the same way.");
                StringAssert.Contains(file, "London");

                // A See PII reader still sees every city, Madrid included.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                StringAssert.Contains(await host.Client.GetStringAsync("api/WebActivity/export/technology?days=28&top=50"), "Madrid");
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/technology?days=28&top=49"));
                Assert.IsTrue(full["detail"].Any(r => (string)r["city"] == "Madrid"));
            });
        }

        [TestMethod]
        public void SiteOverTime_WithoutSeePii_IsFoldedBySite_NotByATitleTwoSitesShare()
        {
            var week = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
            var visits = new WebActivityVisits
            {
                BySite = new List<WebActivityNamedCount>
                {
                    new WebActivityNamedCount { Name = "Contoso Intranet", Count = 250, People = 50, Key = 1 },
                    new WebActivityNamedCount { Name = "Contoso Team Site", Count = 40, People = 9, Key = 2 },
                    new WebActivityNamedCount { Name = "Contoso HR", Count = 30, People = 7, Key = 3 },
                    new WebActivityNamedCount { Name = "Contoso Legal", Count = 5, People = 3, Key = 5 },
                },

                // Ranked by page views, the chart's busiest sites are not the list's: another site called Contoso Team Site,
                // of two visitors, made the chart, and the list's Contoso Team Site did not.
                SiteOverTime = new List<WebActivityStackPoint>
                {
                    new WebActivityStackPoint { WeekStart = week, Name = "Contoso Intranet", Count = 400, Key = 1 },
                    new WebActivityStackPoint { WeekStart = week, Name = "Contoso Team Site", Count = 60, Key = 4 },
                    new WebActivityStackPoint { WeekStart = week, Name = "Contoso HR", Count = 30, Key = 3 },
                    new WebActivityStackPoint { WeekStart = week, Name = "(other sites)", Count = 10 },
                    new WebActivityStackPoint { WeekStart = week, Name = "(unknown site)", Count = 2 },
                },
            };

            var trimmed = visits.WithoutSmallGroups(Minimum);

            CollectionAssert.AreEqual(new[] { "Contoso Intranet", "Contoso Team Site" }, trimmed.BySite.Select(s => s.Name).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "Contoso Intranet", "(other sites)", "(unknown site)" },
                trimmed.SiteOverTime.Select(p => p.Name).ToArray(),
                "The Contoso Team Site on the chart is not the one the list shows.");
            Assert.AreEqual(60 + 30 + 10, trimmed.SiteOverTime.Single(p => p.Name == "(other sites)").Count);
            Assert.AreEqual(visits.SiteOverTime.Sum(p => p.Count), trimmed.SiteOverTime.Sum(p => p.Count), "Each week's total is unchanged.");
            Assert.AreEqual(5, visits.SiteOverTime.Count, "The cached series are never edited.");
        }

        [TestMethod]
        public async Task PagesVisitsAndOverview_WithoutSeePii_LeaveOutPagesAndSitesOfFewerThanFivePeople_WithTheSmallestShown()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 44);
            var keys = new[] { "pages", "visits" }.Select(query.CacheKey)
                .Concat(new[] { WebActivityQuery.Create(28, DateTime.UtcNow).CacheKey("overview") })
                .ToArray();
            await WithHost(store, keys, async host =>
            {
                var pages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=44"));
                Assert.AreEqual(Minimum, (int)pages["minimumGroupSize"]);

                // Grievances had two visitors; HR policies, the smallest page still shown, goes with it, so the page views
                // less the pages shown are never one small page's.
                CollectionAssert.AreEqual(new[] { "Home", "News" }, Titles(pages["topPages"]));
                Assert.AreEqual(2, (int)pages["withheldTopPages"]);
                CollectionAssert.AreEqual(new[] { "Expenses" }, Titles(pages["slowestPages"]), "Archive had three people, and Forms goes with it.");
                Assert.AreEqual(2, (int)pages["withheldSlowestPages"]);
                Assert.AreEqual(0, ((JArray)pages["quietPages"]).Count, "A quiet page is viewed three times or fewer: never five people.");
                Assert.AreEqual(2, (int)pages["withheldQuietPages"]);
                CollectionAssert.AreEqual(new[] { "Contoso Intranet" }, pages["bySite"].Select(s => (string)s["name"]).ToArray());
                Assert.AreEqual(2, (int)pages["withheldSites"], "Contoso Legal's three visitors, and Contoso HR beside them.");
                foreach (var hidden in new[] { "Grievances", "HR policies", "Archive", "Forms", "Quiet" })
                    Assert.IsFalse(pages.ToString().Contains(PageUrl(hidden)), hidden);
                Assert.AreEqual(1000, (long)pages["kpis"]["pageViews"], "The scope's own totals are still there.");
                Assert.IsNull(pages["topPages"][0]["people"], "The people count only decides; it is never sent.");

                // No page on this tab carries a figure counted in visits: its unique page views are the Visits tab's visits by
                // page, and its entries, exits and bounces the Journeys tab's, each floored on its own tab.
                Assert.IsTrue((bool)pages["visitFiguresWithheld"]);
                foreach (var row in pages["topPages"].Concat(pages["slowestPages"]))
                {
                    foreach (var figure in new[] { "uniquePageViews", "entries", "exits", "bounces", "bouncePct" })
                        Assert.IsNull(row[figure], (string)row["title"] + " carries no " + figure + ".");
                }

                var visits = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/visits?days=28&top=44"));
                CollectionAssert.AreEqual(new[] { "Home" }, visits["byPage"].Select(p => (string)p["name"]).ToArray());
                Assert.AreEqual(2, (int)visits["withheldPages"], "Grievances, and News beside it.");
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

                // The engagement mix: three occasional visitors, and the rare ones, the smallest segment shown, go with them -
                // the visitors less the segments shown are both segments together. An empty segment is not listed either, so
                // the segments listed never say which ones were left out.
                CollectionAssert.AreEqual(new[] { "Daily", "Regular" }, overview["visitorSegments"].Select(b => (string)b["key"]).ToArray());
                Assert.AreEqual(2, (int)overview["withheldSegments"]);
                Assert.AreEqual(3 + 8, (long)overview["kpis"]["visitors"] - overview["visitorSegments"].Sum(b => (long)b["count"]));

                // The next reader holds See PII and is served the complete sections built for the rows they asked for.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var fullPages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=44"));
                CollectionAssert.AreEqual(new[] { "Home", "News", "HR policies", "Grievances" }, Titles(fullPages["topPages"]));
                Assert.AreEqual(500, (long)fullPages["topPages"][0]["entries"]);
                Assert.IsNotNull(fullPages["topPages"][0]["uniquePageViews"]);
                Assert.IsNull(fullPages["visitFiguresWithheld"], "Sent only to a reader whose figures are left out.");
                Assert.AreEqual(2, ((JArray)fullPages["quietPages"]).Count);
                Assert.AreEqual(0, (int)fullPages["withheldTopPages"]);
                Assert.AreEqual(3, ((JArray)fullPages["bySite"]).Count);
                var fullVisits = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/visits?days=28&top=44"));
                Assert.AreEqual(3, ((JArray)fullVisits["byPage"]).Count);
                var fullOverview = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/overview?days=28"));
                Assert.AreEqual(3, ((JArray)fullOverview["topSites"]).Count);
                Assert.AreEqual(5, ((JArray)fullOverview["visitorSegments"]).Count);
                Assert.AreEqual(0, (int)fullOverview["withheldSegments"]);
                Assert.AreEqual(2, store.PagesCalls, "Each kind of reader has its own cache entry.");
                Assert.AreEqual(2, store.VisitsCalls);
                Assert.AreEqual(2, store.OverviewCalls);
            });
        }

        [TestMethod]
        public async Task Journeys_WithoutSeePii_LeaveOutSmallRows_WithTheSmallestShown_AndASmallStepWithTheSmallestStepOutOfItsPage()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 46);
            await WithHost(store, new[] { query.CacheKey("journeys") }, async host =>
            {
                var journeys = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=46"));
                Assert.AreEqual(Minimum, (int)journeys["minimumGroupSize"]);
                CollectionAssert.AreEqual(new[] { "Home" }, Titles(journeys["entryPages"]), "Grievances, and News beside it.");
                Assert.AreEqual(2, (int)journeys["withheldEntryPages"]);
                CollectionAssert.AreEqual(new[] { "Home" }, Titles(journeys["exitPages"]), "Grievances, and HR policies beside it.");
                Assert.AreEqual(2, (int)journeys["withheldExitPages"]);
                Assert.AreEqual(0, ((JArray)journeys["bouncePages"]).Count, "With one page big enough, it would be the bounces less the small one.");
                Assert.AreEqual(2, (int)journeys["withheldBouncePages"]);

                // A step's share gives away the steps out of its page. The one-person step out of Home goes with the smallest
                // step out of Home (seven people) - not with News to HR policies (six), the smallest step overall, which
                // would leave Home's steps less the steps shown as that one person's.
                CollectionAssert.AreEqual(
                    new[] { "Home>News", "News>Home", "News>HR policies" },
                    journeys["transitions"].Select(s => (string)s["fromTitle"] + ">" + (string)s["toTitle"]).ToArray());
                Assert.AreEqual(2, (int)journeys["withheldTransitions"]);

                // Home to Grievances was two people, and News to News, the smallest flow shown, goes with it. Home to News
                // ends on News, a page the entry pages leave out: a page's flows add up to its entries and exits.
                CollectionAssert.AreEqual(
                    new[] { "Home>Home" },
                    journeys["flows"].Select(f => (string)f["startTitle"] + ">" + (string)f["endTitle"]).ToArray());
                Assert.AreEqual(3, (int)journeys["withheldFlows"]);
                Assert.AreEqual(40, (double)journeys["flowsCoveragePct"], 1e-9, "The coverage is of the flows shown.");

                CollectionAssert.AreEqual(new[] { "Book a desk" }, journeys["clickedElements"].Select(c => (string)c["name"]).ToArray());
                Assert.AreEqual(2, (int)journeys["withheldClickedElements"], "Report a colleague, and Search beside it.");
                Assert.IsFalse(journeys.ToString().Contains("Report a colleague"));
                Assert.IsFalse(journeys.ToString().Contains("Grievances"));
                Assert.AreEqual(300, (long)journeys["kpis"]["visits"], "The scope's own totals are still there.");
                Assert.AreEqual(2, ((JArray)journeys["depth"]).Count);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=46"));
                Assert.AreEqual(5, ((JArray)full["transitions"]).Count);
                Assert.AreEqual(4, ((JArray)full["flows"]).Count);
                Assert.AreEqual(71.33, (double)full["flowsCoveragePct"], 1e-9);
                Assert.AreEqual(3, ((JArray)full["clickedElements"]).Count);
                Assert.AreEqual(2, store.JourneysCalls, "A See PII reader is served the section as asked for, never this reader's every-row entry.");
            });
        }

        [TestMethod]
        public async Task SeePii_IsSentEveryPageAndPathListAsBuilt_AndNeverAPeopleCount()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 47);
            var keys = new[] { "pages", "journeys", "visits" }.Select(query.CacheKey).ToArray();
            await WithHost(store, keys, async host =>
            {
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                // net10: the settings the host serialises with (Web API 2's JsonMediaTypeFormatter defaults).
                var settings = WebApiCompatibleJson.Apply(new JsonSerializerSettings());
                foreach (var (tab, built) in new (string, object)[] { ("pages", store.Pages), ("journeys", store.Journeys), ("visits", store.Visits) })
                {
                    var sent = await host.Client.GetStringAsync("api/WebActivity/" + tab + "?days=28&top=47");
                    Assert.IsTrue(
                        JToken.DeepEquals(JObject.Parse(JsonConvert.SerializeObject(built, settings)), JObject.Parse(sent)),
                        tab + ": a See PII reader is sent the section exactly as it was built.");
                    Assert.IsFalse(sent.IndexOf("\"people\"", StringComparison.OrdinalIgnoreCase) >= 0, tab + " never carries a people count.");
                    Assert.IsTrue(sent.Contains("\"minimumGroupSize\":null"), tab);
                }

                // And the next reader, without See PII, is cut from the section built for every row - never sent the See PII
                // reader's entry.
                host.Principal = PortalTestHost.SignedIn();
                var trimmed = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=47"));
                Assert.AreEqual(3, ((JArray)trimmed["transitions"]).Count);
                Assert.AreEqual(2, store.JourneysCalls);
            });
        }

        [TestMethod]
        public async Task Export_WithoutSeePii_EveryFileCarriesItsScreensFlooredRows_AndOnlyTheSearchTermsAreRefused()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 45);
            var keys = WebActivityExports.Sections
                .SelectMany(s => new[] { query.CacheKey("export::" + s), query.CacheKey("export::" + s + "::without-see-pii") })
                .Concat(new[] { query.CacheKey("geography") })
                .ToArray();
            await WithHost(store, keys, async host =>
            {
                var refused = await host.Client.GetAsync("api/WebActivity/export/search-terms?days=28&top=45");
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "A search term is text a person typed, however many searched.");
                Assert.AreEqual("portalPermissionRequired", (string)JObject.Parse(await refused.Content.ReadAsStringAsync())["code"]);
                Assert.AreEqual(0, store.SearchCalls, "A refused file runs no query.");
                Assert.IsTrue(WebActivityExports.WithheldWithoutSeePii("SEARCH-TERMS"));
                foreach (var section in WebActivityExports.Sections.Where(s => s != "search-terms"))
                    Assert.IsFalse(WebActivityExports.WithheldWithoutSeePii(section), section);

                // Each file is the screen's rows: the small ones left out, with the smallest row shown beside them.
                async Task<string[]> Lines(string section) =>
                    (await host.Client.GetStringAsync("api/WebActivity/export/" + section + "?days=28&top=45"))
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

                CollectionAssert.AreEqual(new[] { "Home", "News" }, (await Lines("pages")).Select(FirstField).ToArray());
                var pagesHeader = (await host.Client.GetStringAsync("api/WebActivity/export/pages?days=28&top=45")).Split('\n')[0];
                Assert.IsFalse(
                    pagesHeader.Contains("Unique page views") || pagesHeader.Contains("Visits entering here") || pagesHeader.Contains("Bounces"),
                    "No figure counted in visits: " + pagesHeader);
                foreach (var file in new[] { "slow-pages", "quiet-pages" })
                {
                    var header = (await host.Client.GetStringAsync("api/WebActivity/export/" + file + "?days=28&top=45")).Split('\n')[0];
                    Assert.IsFalse(header.Contains("Unique page views"), file + ": " + header);
                }
                CollectionAssert.AreEqual(new[] { "Expenses" }, (await Lines("slow-pages")).Select(FirstField).ToArray());
                Assert.AreEqual(0, (await Lines("quiet-pages")).Length, "Every quiet page is a page of fewer than five people.");
                CollectionAssert.AreEqual(new[] { "Home" }, (await Lines("entry-pages")).Select(FirstField).ToArray());
                CollectionAssert.AreEqual(new[] { "Home" }, (await Lines("exit-pages")).Select(FirstField).ToArray());
                var steps = await Lines("transitions");
                Assert.AreEqual(3, steps.Length);
                Assert.IsFalse(steps.Any(s => s.Contains("Grievances") || s.StartsWith("Home," + PageUrl("Home") + ",HR policies,", StringComparison.Ordinal)));
                var flows = await Lines("flows");
                Assert.AreEqual(1, flows.Length, "Home to Home: every other flow is small, the smallest shown, or touches a page the entry pages leave out.");
                Assert.IsFalse(flows.Any(f => f.Contains("Grievances") || f.Contains("News")));
                var technology = string.Join("\n", await Lines("technology"));
                StringAssert.Contains(technology, "London");
                Assert.IsFalse(technology.Contains("Lisbon"), "A row of one visitor is left out of the file as it is on the screen.");
                Assert.IsFalse(technology.Contains("Madrid"));

                // A See PII reader next: the floored files must not have been cached for them.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                CollectionAssert.AreEqual(new[] { "Home", "News", "HR policies", "Grievances" }, (await Lines("pages")).Select(FirstField).ToArray());
                var fullHeader = (await host.Client.GetStringAsync("api/WebActivity/export/pages?days=28&top=45")).Split('\n')[0];
                StringAssert.Contains(fullHeader, "Unique page views");
                StringAssert.Contains(fullHeader, "Visits entering here");
                StringAssert.Contains((await host.Client.GetStringAsync("api/WebActivity/export/slow-pages?days=28&top=45")).Split('\n')[0], "Unique page views");
                Assert.AreEqual(2, (await Lines("quiet-pages")).Length);
                Assert.AreEqual(5, (await Lines("transitions")).Length);
                Assert.AreEqual(4, (await Lines("flows")).Length);
                StringAssert.Contains(string.Join("\n", await Lines("technology")), "Lisbon");
                StringAssert.Contains(await host.Client.GetStringAsync("api/WebActivity/export/search-terms?days=28&top=45"), "Alex Contoso");
            });
        }

        [TestMethod]
        public async Task Export_ASeePiiReadersFile_IsNeverServedToTheNextReaderWithoutSeePii()
        {
            var store = new FakeWebActivityStore();
            var query = WebActivityQuery.Create(28, DateTime.UtcNow, 48);
            var keys = WebActivityExports.Sections
                .SelectMany(s => new[] { query.CacheKey("export::" + s), query.CacheKey("export::" + s + "::without-see-pii") })
                .ToArray();
            await WithHost(store, keys, async host =>
            {
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                foreach (var section in new[] { "pages", "entry-pages", "transitions", "flows" })
                    StringAssert.Contains(await host.Client.GetStringAsync("api/WebActivity/export/" + section + "?days=28&top=48"), "Grievances", section);

                host.Principal = PortalTestHost.SignedIn();
                foreach (var section in new[] { "pages", "entry-pages", "transitions", "flows" })
                    Assert.IsFalse(
                        (await host.Client.GetStringAsync("api/WebActivity/export/" + section + "?days=28&top=48")).Contains("Grievances"),
                        section + ": the See PII reader's file is cached apart.");
            });
        }

        private static string FirstField(string csvLine) => csvLine.Split(',')[0];

        /// <summary>
        /// #881's decision 2, the leak: floored over only the rows asked for, a 15-row answer with no small row in it showed
        /// a row that the 500-row answer leaves out as its smallest shown - and that row's figures, taken off the longer
        /// answer's remainder, left the small row's. Every list here holds 20 rows: rank 18 is two people, rank 10 is five,
        /// the fewest of the rest, and none of ranks 1 to 15 is small.
        /// </summary>
        [TestMethod]
        public async Task EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut()
        {
            var store = TwentyRowStore();
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                var expected = Ranks.Where(r => r != 10 && r != 18).ToArray();
                foreach (var tab in TwentyRowLists.GroupBy(l => l.Tab))
                {
                    var path = "api/WebActivity/" + tab.Key + "?days=28";
                    var shortAnswer = JObject.Parse(await host.Client.GetStringAsync(path + (tab.Key == "overview" ? string.Empty : "&top=15")));
                    var longAnswer = tab.Key == "overview" ? null : JObject.Parse(await host.Client.GetStringAsync(path + "&top=500"));
                    Assert.AreEqual(15, (int)shortAnswer["window"]["top"], tab.Key + ": the window captions the rows asked for.");

                    foreach (var list in tab)
                    {
                        var label = tab.Key + "." + list.List;
                        var shortRows = Names(shortAnswer[list.List], list.NameField);
                        CollectionAssert.AreEqual(expected.Take(15).Select(r => list.Prefix + " " + r.ToString("00")).ToArray(), shortRows,
                            label + ": the first 15 rows of the one floored list - rank 10, the smallest the 20 rows leave out, is out of these 15 too.");
                        Assert.AreEqual(2, (int)shortAnswer[list.Withheld], label + ": the whole list's rows left out, ranks 10 and 18, however few are shown.");

                        if (longAnswer == null) continue;
                        var longRows = Names(longAnswer[list.List], list.NameField);
                        CollectionAssert.AreEqual(expected.Select(r => list.Prefix + " " + r.ToString("00")).ToArray(), longRows, label);
                        CollectionAssert.AreEqual(longRows.Take(shortRows.Length).ToArray(), shortRows, label + ": a shorter answer is a prefix of a longer one.");
                        Assert.AreEqual(2, (int)longAnswer[list.Withheld], label);

                        // Whatever the reader adds up from either answer, what is left is ranks 10 and 18 together: rank 10 is in
                        // neither answer, so it can never be taken off to leave rank 18's figure on its own.
                        var shown = longAnswer[list.List].Sum(r => (long)r[list.Figure]);
                        Assert.AreEqual(FigureAt(10) + FigureAt(18), Ranks.Sum(FigureAt) - shown, label);
                    }
                }

                // A file is cut the same way: its first 15 rows are the 500-row file's.
                async Task<string[]> Lines(int top) =>
                    (await host.Client.GetStringAsync("api/WebActivity/export/pages?days=28&top=" + top))
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                var file = await Lines(500);
                CollectionAssert.AreEqual(file.Take(15).ToArray(), await Lines(15));
                Assert.IsFalse(file.Any(l => l.StartsWith("Viewed page 10,", StringComparison.Ordinal)));

                // A See PII reader is sent the rows they asked for, as built, from their own entry.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                CollectionAssert.AreEqual(Ranks.Take(15).Select(r => "Viewed page " + r.ToString("00")).ToArray(), Names(full["topPages"], "title"));
            });
        }

        /// <summary>
        /// The final review of #881: a count of the rows left out of only the first <c>top</c> rows changed, top by top,
        /// exactly at the rank of each row left out - and a list ranked by its figure then brackets that row's figure between
        /// its shown neighbours, exactly on a tie. Every list here holds 20 rows, ranks 10 and 18 left out: each count is the
        /// whole list's, the same at every top a reader can ask for, so none of them says where a row is left out.
        /// </summary>
        [TestMethod]
        public async Task EveryLeftOutCount_WithoutSeePii_IsTheWholeListsWhateverTopIsAskedFor_SoNoneSaysWhereARowIsLeftOut()
        {
            var store = TwentyRowStore();
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                IEnumerable<JProperty> Counts(JObject section) => section.Properties().Where(p => p.Name.StartsWith("withheld", StringComparison.Ordinal));

                // The Overview takes no top - its busiest sites are always the first 15 - so it comes last, beside the others.
                foreach (var tab in TwentyRowLists.GroupBy(l => l.Tab).OrderBy(t => t.Key == "overview"))
                {
                    var tops = tab.Key == "overview" ? new[] { (int?)null } : new int?[] { 1, 15, 499, 500 };
                    var answers = new List<JObject>();
                    foreach (var top in tops)
                        answers.Add(JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/" + tab.Key + "?days=28" + (top == null ? string.Empty : "&top=" + top))));

                    var countsByTop = answers.Select(a => string.Join(", ", Counts(a).Select(p => p.Name + "=" + p.Value))).ToList();
                    Assert.AreEqual(
                        1,
                        countsByTop.Distinct().Count(),
                        tab.Key + ": every count is the same at every top - " + string.Join(" | ", tops.Zip(countsByTop, (top, counts) => "top=" + top + ": " + counts)));
                    foreach (var list in tab)
                        Assert.AreEqual(2, (int)answers[0][list.Withheld], tab.Key + "." + list.List + ": ranks 10 and 18, wherever they rank.");
                }

                // A See PII reader's answer is as it was: built for the rows they ask for, nothing left out at any top.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                foreach (var top in new[] { 1, 500 })
                {
                    var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/geography?days=28&top=" + top));
                    Assert.IsTrue(Counts(full).All(p => (int)p.Value == 0), "top=" + top);
                    Assert.AreEqual(Math.Min(top, Ranks.Length), ((JArray)full["cities"]).Count, "top=" + top);
                }
            });
        }

        /// <summary>
        /// The entry, exit and bounce pages share each page's entries, exits and bounces, and a page's flows add up to them.
        /// News is the entry pages' smallest page shown beside Grievances (two people): the bounce and exit pages have no
        /// small page of their own, so, floored apart, they would show News with its entries, bounces and exits - and those,
        /// taken off the entry pages' remainder, would leave Grievances'.
        /// </summary>
        [TestMethod]
        public async Task EntryExitAndBouncePages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt()
        {
            var store = new SectionStore
            {
                Journeys = q => new WebActivityJourneys
                {
                    Window = WebActivityWindow.From(q),
                    Kpis = new WebActivityJourneyKpis { Visits = 400 },
                    EntryPages = new List<WebActivityPageRow> { Endpoint("Home", 300, 30), Endpoint("News", 40, 8), Endpoint("Grievances", 3, 2) },
                    BouncePages = new List<WebActivityPageRow> { Endpoint("HR policies", 50, 12), Endpoint("News", 40, 8), Endpoint("Forms", 30, 6) },
                    ExitPages = new List<WebActivityPageRow>
                    {
                        Endpoint("Home", 200, 25), Endpoint("HR policies", 60, 20), Endpoint("News", 45, 12), Endpoint("Forms", 20, 9),
                    },
                    Flows = new List<WebActivityFlowRow>
                    {
                        FlowRow("Home", "Home", 120, 30, 40), FlowRow("News", "HR policies", 20, 5, 7), FlowRow("Home", "Forms", 15, 3.75, 6),
                    },
                },
                Pages = q => new WebActivityPages
                {
                    Window = WebActivityWindow.From(q),
                    TopPages = new List<WebActivityPageRow> { Endpoint("Home", 900, 60), Endpoint("News", 300, 30) },
                },
            };
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                var journeys = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home" }, Titles(journeys["entryPages"]), "Grievances, and News beside it.");
                CollectionAssert.AreEqual(new[] { "HR policies", "Forms" }, Titles(journeys["bouncePages"]), "News, which the entry pages leave out.");
                CollectionAssert.AreEqual(new[] { "Home", "HR policies", "Forms" }, Titles(journeys["exitPages"]));
                CollectionAssert.AreEqual(
                    new[] { "Home>Home", "Home>Forms" },
                    journeys["flows"].Select(f => (string)f["startTitle"] + ">" + (string)f["endTitle"]).ToArray(),
                    "News's flows would add up to its entries.");
                Assert.AreEqual(1, (int)journeys["withheldBouncePages"]);
                Assert.AreEqual(1, (int)journeys["withheldExitPages"]);
                Assert.AreEqual(1, (int)journeys["withheldFlows"]);
                Assert.IsFalse(journeys.ToString().Contains("News"), "News is left out of every list that shares its figures.");

                // The most viewed pages still show News - it had thirty viewers - but no page there carries its entries.
                var pages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home", "News" }, Titles(pages["topPages"]));
                Assert.IsNull(pages["topPages"][1]["entries"]);
                Assert.IsNull(pages["topPages"][1]["bounces"]);
                Assert.IsTrue((bool)pages["visitFiguresWithheld"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/journeys?days=28&top=15"));
                CollectionAssert.Contains(Titles(full["bouncePages"]), "News");
                CollectionAssert.Contains(Titles(full["exitPages"]), "News");
                Assert.AreEqual(300, (long)JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"))["topPages"][1]["entries"]);
            });
        }

        /// <summary>
        /// The most viewed, slowest and quiet pages share each page's page views and unique page views, so they too leave out
        /// one combined set of pages. Over 28 days News is the most viewed pages' smallest page shown beside Grievances (two
        /// people); the slowest pages have no small page of their own, so, floored apart, they would show News with its page
        /// views - and those, taken off the most viewed pages' remainder, would leave Grievances'. Over 7 days it is the other
        /// way round: Forms is the slowest pages' smallest shown beside Expenses (three people), and the most viewed pages,
        /// which have no small page, leave it out too.
        /// </summary>
        [TestMethod]
        public async Task MostViewedSlowestAndQuietPages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt()
        {
            WebActivityPageRow Viewed(string title, long views, int people) =>
                new WebActivityPageRow { Title = title, Url = PageUrl(title), PageViews = views, UniquePageViews = views / 2, People = people };

            var store = new SectionStore
            {
                Pages = q => q.Days == 7
                    ? new WebActivityPages
                    {
                        Window = WebActivityWindow.From(q),
                        TopPages = new List<WebActivityPageRow> { Viewed("Home", 900, 60), Viewed("HR policies", 250, 12), Viewed("Forms", 80, 6) },
                        SlowestPages = new List<WebActivityPageRow> { Viewed("Expenses", 20, 3), Viewed("Forms", 80, 6), Viewed("HR policies", 250, 12) },
                    }
                    : new WebActivityPages
                    {
                        Window = WebActivityWindow.From(q),
                        TopPages = new List<WebActivityPageRow>
                        {
                            Viewed("Home", 900, 60), Viewed("News", 300, 8), Viewed("HR policies", 250, 12), Viewed("Grievances", 4, 2),
                        },
                        SlowestPages = new List<WebActivityPageRow> { Viewed("News", 300, 8), Viewed("Forms", 80, 9), Viewed("HR policies", 250, 12) },
                        QuietPages = new List<WebActivityPageRow> { Viewed("Archive", 2, 1) },
                    },
            };
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                var month = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home", "HR policies" }, Titles(month["topPages"]), "Grievances, and News beside it.");
                CollectionAssert.AreEqual(new[] { "Forms", "HR policies" }, Titles(month["slowestPages"]), "News, which the most viewed pages leave out.");
                Assert.AreEqual(2, (int)month["withheldTopPages"]);
                Assert.AreEqual(1, (int)month["withheldSlowestPages"]);
                Assert.AreEqual(1, (int)month["withheldQuietPages"]);
                Assert.IsFalse(
                    (await host.Client.GetStringAsync("api/WebActivity/export/slow-pages?days=28&top=15")).Contains("News"),
                    "The file carries the screen's rows.");

                var week = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=7&top=15"));
                CollectionAssert.AreEqual(new[] { "HR policies" }, Titles(week["slowestPages"]), "Expenses, and Forms beside it.");
                CollectionAssert.AreEqual(new[] { "Home", "HR policies" }, Titles(week["topPages"]), "Forms, which the slowest pages leave out.");
                Assert.AreEqual(1, (int)week["withheldTopPages"]);

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home", "News", "HR policies", "Grievances" }, Titles(full["topPages"]));
                CollectionAssert.AreEqual(new[] { "News", "Forms", "HR policies" }, Titles(full["slowestPages"]));
            });
        }

        /// <summary>
        /// A page's unique page views are its visits - the Visits tab's visits by page - and each tab floors its own list
        /// (#881's decision 2). News is the visits by page's smallest page shown beside Grievances (two people); the most
        /// viewed pages have no small page, so they show News, and with its unique page views they would give News's visits
        /// back - and the unique page views less the visits shown, Grievances'. A reader without See PII is sent no page's
        /// unique page views on the Pages tab, on screen or in a file.
        /// </summary>
        [TestMethod]
        public async Task UniquePageViews_WithoutSeePii_AreLeftOffThePagesTab_SoAPageTheVisitsTabLeavesOutCannotBeReadThere()
        {
            WebActivityPageRow Viewed(string title, long views, long visits, int people) =>
                new WebActivityPageRow { Title = title, Url = PageUrl(title), PageViews = views, UniquePageViews = visits, People = people };

            var store = new SectionStore
            {
                Visits = q => new WebActivityVisits
                {
                    Window = WebActivityWindow.From(q),
                    ByPage = new List<WebActivityNamedCount>
                    {
                        new WebActivityNamedCount { Name = "Home", Count = 300, People = 40 },
                        new WebActivityNamedCount { Name = "News", Count = 40, People = 8 },
                        new WebActivityNamedCount { Name = "Grievances", Count = 3, People = 2 },
                    },
                },
                Pages = q => new WebActivityPages
                {
                    Window = WebActivityWindow.From(q),
                    Kpis = new WebActivityPageKpis { PageViews = 1024, UniquePageViews = 343 },
                    TopPages = new List<WebActivityPageRow> { Viewed("Home", 900, 300, 40), Viewed("News", 120, 40, 8) },
                    SlowestPages = new List<WebActivityPageRow> { Viewed("News", 120, 40, 8) },
                },
            };
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                var visits = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/visits?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home" }, visits["byPage"].Select(p => (string)p["name"]).ToArray(), "Grievances, and News beside it.");

                var pages = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                CollectionAssert.AreEqual(new[] { "Home", "News" }, Titles(pages["topPages"]), "News had eight viewers: shown, by its page views.");
                Assert.IsTrue((bool)pages["visitFiguresWithheld"]);
                foreach (var row in pages["topPages"].Concat(pages["slowestPages"]))
                    Assert.IsNull(row["uniquePageViews"], (string)row["title"] + " carries no unique page views.");
                Assert.AreEqual(
                    40 + 3,
                    (long)pages["kpis"]["uniquePageViews"] - visits["byPage"].Sum(p => (long)p["count"]),
                    "The unique page views less every page's visits shown are News's and Grievances' together.");
                foreach (var file in new[] { "pages", "slow-pages" })
                {
                    var csv = await host.Client.GetStringAsync("api/WebActivity/export/" + file + "?days=28&top=15");
                    Assert.IsFalse(csv.Split('\n')[0].Contains("Unique page views"), file);
                    StringAssert.Contains(csv, "News," + PageUrl("News") + ",,120,", file + ": News with its page views and no visits.");
                }

                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=15"));
                Assert.AreEqual(40, (long)full["topPages"][1]["uniquePageViews"]);
                Assert.IsNull(full["visitFiguresWithheld"]);
                StringAssert.Contains(
                    (await host.Client.GetStringAsync("api/WebActivity/export/pages?days=28&top=15")).Split('\n')[0], "Unique page views");
            });
        }

        /// <summary>
        /// #881's decision 4, the cap: every list ranks 501 rows - one more than any reader is shown - and the 501st, past the
        /// cap, is two people's. None of the 500 a reader can be shown is small, and rank 7 has the fewest people among them
        /// (six). Without the cap rule nothing would be left out of those 500, and the total less them would be the 501st
        /// row's figures alone.
        /// </summary>
        [TestMethod]
        public async Task EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo()
        {
            var ranks = Enumerable.Range(1, WebActivityQuery.FloorTop).ToArray();
            int People(int r) => r == WebActivityQuery.FloorTop ? 2 : r == 7 ? 6 : 20 + r % 7;
            long Figure(int r) => 10000 - 10 * r;
            var total = ranks.Sum(r => Figure(r));
            var store = RankedStore(ranks, People, Figure, r => Figure(r) * 100.0 / total);
            await WithHost(store, Enumerable.Empty<string>(), async host =>
            {
                var expected = ranks.Where(r => r != 7 && r != WebActivityQuery.FloorTop).ToArray();
                foreach (var tab in TwentyRowLists.GroupBy(l => l.Tab))
                {
                    var path = "api/WebActivity/" + tab.Key + "?days=28";
                    var shortAnswer = JObject.Parse(await host.Client.GetStringAsync(path + (tab.Key == "overview" ? string.Empty : "&top=15")));
                    var longAnswer = tab.Key == "overview" ? null : JObject.Parse(await host.Client.GetStringAsync(path + "&top=500"));
                    foreach (var list in tab)
                    {
                        var label = tab.Key + "." + list.List;
                        CollectionAssert.AreEqual(
                            expected.Take(15).Select(r => list.Prefix + " " + r.ToString("00")).ToArray(),
                            Names(shortAnswer[list.List], list.NameField),
                            label + ": rank 7 is left out with the row past the cap.");
                        Assert.AreEqual(1, (int)shortAnswer[list.Withheld], label);
                        if (longAnswer == null) continue;

                        CollectionAssert.AreEqual(
                            expected.Select(r => list.Prefix + " " + r.ToString("00")).ToArray(),
                            Names(longAnswer[list.List], list.NameField),
                            label + ": 499 of the 500 rows a reader can be shown, and never the row past them.");
                        Assert.AreEqual(1, (int)longAnswer[list.Withheld], label + ": rank 7; the row past the cap is not one of the list's.");
                        var shown = longAnswer[list.List].Sum(r => (long)r[list.Figure]);
                        Assert.AreEqual(Figure(7) + Figure(WebActivityQuery.FloorTop), total - shown, label + ": what is left is never the row past the cap alone.");
                    }
                }

                // The file is floored the same way: 499 rows, and neither rank 7 nor the row past the cap.
                var file = (await host.Client.GetStringAsync("api/WebActivity/export/pages?days=28&top=500"))
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                Assert.AreEqual(499, file.Length);
                Assert.IsFalse(file.Any(l => l.StartsWith("Viewed page 07,", StringComparison.Ordinal) || l.StartsWith("Viewed page 501,", StringComparison.Ordinal)));

                // A See PII reader is sent the 500 rows they asked for, as built: their section never holds the row past the cap.
                host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var full = JObject.Parse(await host.Client.GetStringAsync("api/WebActivity/pages?days=28&top=500"));
                CollectionAssert.AreEqual(ranks.Take(500).Select(r => "Viewed page " + r.ToString("00")).ToArray(), Names(full["topPages"], "title"));
            });
        }

        [TestMethod]
        public void WithoutSmallRowsWithinCap_TakesNoRowWhenNoneLiesPastTheCap_AndNoSecondRowWhenOneIsLeftOutAlready()
        {
            var rows = Enumerable.Range(1, WebActivityQuery.MaximumTop)
                .Select(r => new WebActivityNamedCount { Name = "Site " + r.ToString("000"), Count = 1000 - r, People = r == 7 ? 6 : 20 })
                .ToList();
            var pastTheCap = new WebActivityNamedCount { Name = "Site 501", Count = 1, People = 2 };

            // A list that ends at the cap holds every row: there is nothing past it to count as left out.
            Assert.AreEqual(500, WebActivitySmallGroups.WithoutSmallRowsWithinCap(rows, s => s.People, s => s.Name, 5).Count);

            // A small row inside the cap already takes the smallest shown with it; the row past the cap takes no second one.
            rows[99].People = 3;
            var shown = WebActivitySmallGroups.WithoutSmallRowsWithinCap(rows.Concat(new[] { pastTheCap }).ToList(), s => s.People, s => s.Name, 5);
            CollectionAssert.AreEquivalent(
                new[] { "Site 007", "Site 100", "Site 501" },
                rows.Concat(new[] { pastTheCap }).Except(shown).Select(s => s.Name).ToArray());
        }

        /// <summary>
        /// A step's share is of every step out of its page, so the steps a list holds tell whether their page has more past
        /// the cap. The list below is cut at the cap: Home's steps go on past it, so Home loses its smallest step shown; News's
        /// two steps are all of News's, so News loses none.
        /// </summary>
        [TestMethod]
        public void StepsOutOfAPage_LoseTheirSmallestShownOnlyWhenThatPagesStepsGoOnPastTheCap()
        {
            WebActivityTransitionRow Step(string from, string to, long count, long ofSteps, int people) => new WebActivityTransitionRow
            {
                FromTitle = from,
                FromUrl = PageUrl(from),
                ToTitle = to,
                ToUrl = PageUrl(to),
                Count = count,
                SharePct = count * 100.0 / ofSteps,
                People = people,
            };

            var homeCounts = Enumerable.Range(1, 499).Select(r => (long)(5000 - 10 * r)).ToList();
            var homeSteps = homeCounts.Sum();
            var steps = homeCounts.Take(498)
                .Select((count, i) => Step("Home", "Page " + (i + 1).ToString("000"), count, homeSteps, i + 1 == 7 ? 6 : 20))
                .Concat(new[] { Step("News", "Home", 90, 150, 9), Step("News", "Forms", 60, 150, 8) })
                .Concat(new[] { Step("Home", "Page 499", homeCounts[498], homeSteps, 2) })
                .ToList();

            var floored = new WebActivityJourneys { Transitions = steps }.WithoutSmallGroups(5, WebActivityQuery.MaximumTop);

            Assert.AreEqual(499, floored.Transitions.Count, "500 steps in the list, less Home's smallest shown; the 501st is past the cap.");
            Assert.IsFalse(floored.Transitions.Any(s => s.ToTitle == "Page 007" || s.ToTitle == "Page 499"));
            CollectionAssert.AreEqual(new[] { "Home", "Forms" }, floored.Transitions.Where(s => s.FromTitle == "News").Select(s => s.ToTitle).ToArray());
            Assert.AreEqual(1, floored.WithheldTransitions);
        }

        private static readonly int[] Ranks = Enumerable.Range(1, 20).ToArray();

        private static int PeopleAt(int rank) => rank == 10 ? 5 : rank == 18 ? 2 : 20 + rank % 7;

        private static long FigureAt(int rank) => 1000 - 10 * rank;

        private static readonly (string Tab, string List, string NameField, string Figure, string Withheld, string Prefix)[] TwentyRowLists =
        {
            ("overview", "topSites", "name", "count", "withheldSites", "Site"),
            ("visits", "bySite", "name", "count", "withheldSites", "Site"),
            ("visits", "byPage", "name", "count", "withheldPages", "Visited page"),
            ("visits", "byDevice", "name", "count", "withheldDevices", "Device"),
            ("visits", "byBrowser", "name", "count", "withheldBrowsers", "Browser"),
            ("pages", "topPages", "title", "pageViews", "withheldTopPages", "Viewed page"),
            ("pages", "slowestPages", "title", "pageViews", "withheldSlowestPages", "Slow page"),
            ("pages", "quietPages", "title", "pageViews", "withheldQuietPages", "Quiet page"),
            ("pages", "bySite", "name", "pageViews", "withheldSites", "Site"),
            ("journeys", "entryPages", "title", "pageViews", "withheldEntryPages", "Entry page"),
            ("journeys", "exitPages", "title", "pageViews", "withheldExitPages", "Exit page"),
            ("journeys", "bouncePages", "title", "pageViews", "withheldBouncePages", "Bounce page"),
            ("journeys", "transitions", "toTitle", "count", "withheldTransitions", "Step"),
            ("journeys", "flows", "startTitle", "visits", "withheldFlows", "Flow"),
            ("journeys", "clickedElements", "name", "count", "withheldClickedElements", "Button"),
            ("geography", "countries", "name", "pageViews", "withheldCountries", "Country"),
            ("geography", "cities", "name", "pageViews", "withheldCities", "City"),
            ("geography", "provinces", "name", "pageViews", "withheldProvinces", "Province"),
            ("technology", "browsers", "name", "pageViews", "withheldBrowsers", "Browser"),
            ("technology", "operatingSystems", "name", "pageViews", "withheldOperatingSystems", "OS"),
            ("technology", "devices", "name", "pageViews", "withheldDevices", "Device"),
            ("technology", "detail", "browser", "pageViews", "withheldDetailRows", "Detail"),
        };

        private static string[] Names(JToken rows, string field) => rows.Select(r => (string)r[field]).ToArray();

        /// <summary>A store whose every floored list ranks 20 rows and returns as many as the query asks for, as SQL's TOP does.</summary>
        private static SectionStore TwentyRowStore() => RankedStore(Ranks, PeopleAt, FigureAt, null);

        /// <summary>
        /// A store whose every floored list ranks <paramref name="ranks"/> and returns as many as the query asks for, as SQL's
        /// TOP does. A step's share is <paramref name="shareAt"/>'s, or none.
        /// </summary>
        private static SectionStore RankedStore(IReadOnlyList<int> ranks, Func<int, int> peopleAt, Func<int, long> figureAt, Func<int, double> shareAt)
        {
            List<T> Rows<T>(WebActivityQuery q, Func<int, string, T> row, string prefix) =>
                ranks.Take(q.Top).Select(r => row(r, prefix + " " + r.ToString("00"))).ToList();
            WebActivityNamedCount Named(int r, string name) => new WebActivityNamedCount { Name = name, Count = figureAt(r), People = peopleAt(r), Key = r };
            WebActivityPageRow PageAt(int r, string name) => new WebActivityPageRow { Title = name, Url = PageUrl(name), PageViews = figureAt(r), People = peopleAt(r) };
            WebActivityPlaceRow Place(int r, string name) => new WebActivityPlaceRow { Name = name, Country = "Contoso", PageViews = figureAt(r), Visitors = peopleAt(r) };
            WebActivityPlatformRow Platform(int r, string name) => new WebActivityPlatformRow { Name = name, PageViews = figureAt(r), Visitors = peopleAt(r) };

            return new SectionStore
            {
                Overview = q => new WebActivityOverview { Window = WebActivityWindow.From(q), TopSites = Rows(q, Named, "Site") },
                Visits = q => new WebActivityVisits
                {
                    Window = WebActivityWindow.From(q),
                    BySite = Rows(q, Named, "Site"),
                    ByPage = Rows(q, Named, "Visited page"),
                    ByDevice = Rows(q, Named, "Device"),
                    ByBrowser = Rows(q, Named, "Browser"),
                },
                Pages = q => new WebActivityPages
                {
                    Window = WebActivityWindow.From(q),
                    TopPages = Rows(q, PageAt, "Viewed page"),
                    SlowestPages = Rows(q, PageAt, "Slow page"),
                    QuietPages = Rows(q, PageAt, "Quiet page"),
                    BySite = Rows(q, (r, name) => new WebActivitySiteRow { Name = name, PageViews = figureAt(r), Visitors = peopleAt(r) }, "Site"),
                },
                Journeys = q => new WebActivityJourneys
                {
                    Window = WebActivityWindow.From(q),
                    EntryPages = Rows(q, PageAt, "Entry page"),
                    ExitPages = Rows(q, PageAt, "Exit page"),
                    BouncePages = Rows(q, PageAt, "Bounce page"),
                    Transitions = Rows(q, (r, name) => new WebActivityTransitionRow
                    {
                        FromTitle = "Home",
                        FromUrl = PageUrl("Home"),
                        ToTitle = name,
                        ToUrl = PageUrl(name),
                        Count = figureAt(r),
                        SharePct = shareAt?.Invoke(r) ?? 0,
                        People = peopleAt(r),
                    }, "Step"),
                    Flows = Rows(q, (r, name) => FlowRow(name, "End of " + name, figureAt(r), 1, peopleAt(r)), "Flow"),
                    ClickedElements = Rows(q, Named, "Button"),
                },
                Geography = q => new WebActivityGeography
                {
                    Window = WebActivityWindow.From(q),
                    Countries = Rows(q, Place, "Country"),
                    Cities = Rows(q, Place, "City"),
                    Provinces = Rows(q, Place, "Province"),
                },
                Technology = q => new WebActivityTechnology
                {
                    Window = WebActivityWindow.From(q),
                    Browsers = Rows(q, Platform, "Browser"),
                    OperatingSystems = Rows(q, Platform, "OS"),
                    Devices = Rows(q, Platform, "Device"),
                    Detail = Rows(q, (r, name) => new WebActivityTechnologyDetailRow
                    {
                        Browser = name, Device = "Desktop", OperatingSystem = "Windows", City = "(unknown)", PageViews = figureAt(r), Visitors = peopleAt(r),
                    }, "Detail"),
                },
            };
        }

        private static WebActivityPageRow Endpoint(string title, long count, int people) => new WebActivityPageRow
        {
            Title = title,
            Url = PageUrl(title),
            PageViews = count,
            Entries = count,
            Exits = count / 2,
            Bounces = count / 4,
            People = people,
        };

        private static WebActivityFlowRow FlowRow(string start, string end, long visits, double share, int people) => new WebActivityFlowRow
        {
            StartTitle = start,
            StartUrl = PageUrl(start),
            EndTitle = end,
            EndUrl = PageUrl(end),
            Visits = visits,
            SharePct = share,
            People = people,
        };

        /// <summary>A store that builds each section from the query it is asked with.</summary>
        private sealed class SectionStore : IWebActivityStore
        {
            internal Func<WebActivityQuery, WebActivityOverview> Overview = q => new WebActivityOverview { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivityVisits> Visits = q => new WebActivityVisits { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivityPages> Pages = q => new WebActivityPages { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivityJourneys> Journeys = q => new WebActivityJourneys { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivityGeography> Geography = q => new WebActivityGeography { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivitySearch> Search = q => new WebActivitySearch { Window = WebActivityWindow.From(q) };
            internal Func<WebActivityQuery, WebActivityTechnology> Technology = q => new WebActivityTechnology { Window = WebActivityWindow.From(q) };

            public Task<WebActivityCollectionStatus> GetCollectionStatusAsync() =>
                Task.FromResult(new WebActivityCollectionStatus { Readable = true, LastHitUtc = DateTime.UtcNow });

            public Task<Tuple<bool, bool>> GetOptionalFeatureUseAsync() => Task.FromResult(Tuple.Create(true, true));

            public Task<WebActivityOverview> GetOverviewAsync(WebActivityQuery query, WebActivitySources sources) => Task.FromResult(Overview(query));

            public Task<WebActivityVisits> GetVisitsAsync(WebActivityQuery query) => Task.FromResult(Visits(query));

            public Task<WebActivityPages> GetPagesAsync(WebActivityQuery query) => Task.FromResult(Pages(query));

            public Task<WebActivityJourneys> GetJourneysAsync(WebActivityQuery query) => Task.FromResult(Journeys(query));

            public Task<WebActivityGeography> GetGeographyAsync(WebActivityQuery query) => Task.FromResult(Geography(query));

            public Task<WebActivitySearch> GetSearchAsync(WebActivityQuery query) => Task.FromResult(Search(query));

            public Task<WebActivityTechnology> GetTechnologyAsync(WebActivityQuery query) => Task.FromResult(Technology(query));
        }

        private static string[] Titles(JToken rows) => rows.Select(r => (string)r["title"]).ToArray();

        private static string PageUrl(string title) => "https://contoso.sharepoint.com/sites/intranet/SitePages/" + title.Replace(' ', '-') + ".aspx";

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
        public void WithoutSmallRowsInEachGroup_LeavesOutTheSmallestShownRowOfTheSmallRowsOwnGroup_AndKeepsTheOrder()
        {
            var store = new FakeWebActivityStore();
            var steps = store.Journeys.Transitions;

            var shown = WebActivitySmallGroups.WithoutSmallRowsInEachGroup(
                steps, s => s.FromUrl, s => s.People, s => s.ToTitle + "\u0000" + s.ToUrl, Minimum, out var leftOut);

            // Out of Home: Grievances (one person) and HR policies (seven, the smallest step out of Home shown). Out of News,
            // where nothing is small, both steps stay - HR policies there too, although six people is the fewest overall.
            CollectionAssert.AreEqual(new[] { steps[0], steps[3], steps[4] }, shown);
            Assert.AreEqual(2, leftOut);
            Assert.AreEqual(5, steps.Count, "The cached list is never edited.");

            // Out of Home, the steps less the steps shown are Grievances and HR policies together, never Grievances alone.
            var outOfHome = steps.Where(s => s.FromUrl == steps[0].FromUrl).Sum(s => s.Count);
            Assert.AreEqual(steps[1].Count + steps[2].Count, outOfHome - shown.Where(s => s.FromUrl == steps[0].FromUrl).Sum(s => s.Count));

            // The rule over the whole list would have taken News to HR policies instead, leaving Home's remainder one person's.
            var wholeList = WebActivitySmallGroups.WithoutSmallRows(steps, s => s.People, s => s.ToTitle + "\u0000" + s.ToUrl, Minimum, out _);
            CollectionAssert.DoesNotContain(wholeList, steps[4]);
            CollectionAssert.Contains(wholeList, steps[1]);
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

            store.Technology.WithoutSmallGroups(Minimum, new HashSet<string> { "London" });
            Assert.AreEqual(5, store.Technology.Detail.Count);
            store.Pages.WithoutSmallGroups(Minimum);
            Assert.AreEqual(4, store.Pages.TopPages.Count);
            Assert.AreEqual(2, store.Pages.QuietPages.Count);
            Assert.AreEqual(0, store.Pages.WithheldTopPages);
            var journeys = store.Journeys.WithoutSmallGroups(Minimum);
            Assert.AreEqual(4, store.Journeys.Flows.Count);
            Assert.AreEqual(71.33, store.Journeys.FlowsCoveragePct, 1e-9);
            Assert.AreEqual(5, store.Journeys.Transitions.Count);
            Assert.AreEqual(3, store.Journeys.ClickedElements.Count);
            Assert.AreNotSame(store.Journeys.Flows, journeys.Flows);
            store.Visits.WithoutSmallGroups(Minimum);
            Assert.AreEqual(3, store.Visits.BySite.Count);
            Assert.AreEqual(3, store.Visits.ByPage.Count);
            store.Overview.WithoutSmallGroups(Minimum);
            Assert.AreEqual(3, store.Overview.TopSites.Count);
            Assert.AreEqual(5, store.Overview.VisitorSegments.Count);
            Assert.AreEqual(0, store.Overview.WithheldSegments);
        }

        private static async Task WithHost(FakeWebActivityStore store, IEnumerable<string> cacheKeys, Func<PortalTestHost, Task> test) =>
            await WithHost((IWebActivityStore)store, cacheKeys, test);

        /// <summary>
        /// Runs <paramref name="test"/> against a host over <paramref name="store"/>, with no Web activity section cached before
        /// or after it: a reader without See PII is served from a section cached for every row (MaximumTop), and a test that
        /// leaves one behind would serve the next test's reader from it.
        /// </summary>
        private static async Task WithHost(IWebActivityStore store, IEnumerable<string> cacheKeys, Func<PortalTestHost, Task> test)
        {
            var keys = cacheKeys.ToList();
            ClearWebActivityCache(keys);
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
                ClearWebActivityCache(keys);
            }
        }

        private static void ClearWebActivityCache(IEnumerable<string> keys)
        {
            foreach (var key in keys.Concat(MemoryCache.Default.Select(e => e.Key).Where(k => k.StartsWith("WebActivity::", StringComparison.Ordinal))).ToList())
            {
                MemoryCache.Default.Remove(key);
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
                Kpis = new WebActivityOverviewKpis { PageViews = 1000, Visits = 300, Visitors = 53 },
                TopSites = Sites().Select(s => new WebActivityNamedCount { Name = s.Name, Count = s.PageViews, People = s.Visitors }).ToList(),
                VisitorSegments = new List<WebActivityBucket>
                {
                    new WebActivityBucket { Key = "Daily", Label = "Daily", Count = 30, SharePct = 56.6 },
                    new WebActivityBucket { Key = "Regular", Label = "Regular", Count = 12, SharePct = 22.6 },
                    new WebActivityBucket { Key = "Occasional", Label = "Occasional", Count = 3, SharePct = 5.7 },
                    new WebActivityBucket { Key = "Rare", Label = "Rare", Count = 8, SharePct = 15.1 },
                    new WebActivityBucket { Key = "One-off", Label = "One-off", Count = 0, SharePct = 0 },
                },
            };

            internal WebActivityVisits Visits { get; } = new WebActivityVisits
            {
                Kpis = new WebActivityVisitKpis { Visits = 300, Visitors = 60 },
                BySite = Sites().Select((s, i) => new WebActivityNamedCount { Name = s.Name, Count = s.Visits, People = s.Visitors, Key = i + 1 }).ToList(),
                ByPage = new List<WebActivityNamedCount>
                {
                    new WebActivityNamedCount { Name = "Home", Count = 250, SharePct = 83.3, People = 40 },
                    new WebActivityNamedCount { Name = "News", Count = 80, SharePct = 26.7, People = 12 },
                    new WebActivityNamedCount { Name = "Grievances", Count = 3, SharePct = 1, People = 2 },
                },
                ByDevice = new List<WebActivityNamedCount>
                {
                    new WebActivityNamedCount { Name = "Desktop", Count = 250, People = 50 },
                    new WebActivityNamedCount { Name = "Phone", Count = 40, People = 8 },
                    new WebActivityNamedCount { Name = "Tablet", Count = 10, People = 2 },
                },
                SiteOverTime = new List<WebActivityStackPoint>
                {
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso Intranet", Count = 400, Key = 1 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso Legal", Count = 30, Key = 3 },
                    new WebActivityStackPoint { WeekStart = Week1, Name = "Contoso HR", Count = 70, Key = 2 },
                },
            };

            internal WebActivityPages Pages { get; } = new WebActivityPages
            {
                Kpis = new WebActivityPageKpis { PageViews = 1000, UniquePageViews = 600 },
                TopPages = new List<WebActivityPageRow>
                {
                    Page("Home", 500, people: 40),
                    Page("News", 200, people: 12),
                    Page("HR policies", 60, people: 6),
                    Page("Grievances", 9, people: 2),
                },
                SlowestPages = new List<WebActivityPageRow>
                {
                    Page("Expenses", 40, people: 9),
                    Page("Archive", 7, people: 3),
                    Page("Forms", 30, people: 7),
                },
                QuietPages = new List<WebActivityPageRow> { Page("Quiet", 2, people: 2), Page("Older quiet", 1, people: 1) },
                BySite = Sites(),
            };

            internal WebActivityJourneys Journeys { get; } = new WebActivityJourneys
            {
                Kpis = new WebActivityJourneyKpis { Visits = 300, Clicks = 20 },
                EntryPages = new List<WebActivityPageRow>
                {
                    Page("Home", 300, people: 30),
                    Page("News", 40, people: 8),
                    Page("Grievances", 3, people: 2),
                },
                ExitPages = new List<WebActivityPageRow>
                {
                    Page("Home", 100, people: 25),
                    Page("HR policies", 50, people: 20),
                    Page("Grievances", 2, people: 1),
                },
                BouncePages = new List<WebActivityPageRow> { Page("News", 40, people: 8), Page("Grievances", 3, people: 2) },
                Transitions = new List<WebActivityTransitionRow>
                {
                    Step("Home", "News", 60, 60, people: 20),
                    Step("Home", "HR policies", 30, 30, people: 7),
                    Step("Home", "Grievances", 10, 10, people: 1),
                    Step("News", "Home", 15, 60, people: 9),
                    Step("News", "HR policies", 10, 40, people: 6),
                },
                Flows = new List<WebActivityFlowRow>
                {
                    Flow("Home", "Home", 120, 40, people: 50),
                    Flow("Home", "News", 60, 20, people: 10),
                    Flow("News", "News", 30, 10, people: 6),
                    Flow("Home", "Grievances", 4, 1.33, people: 2),
                },
                FlowsCoveragePct = 71.33,
                Depth = new List<WebActivityBucket>
                {
                    new WebActivityBucket { Key = "1", Label = "1", Count = 200 },
                    new WebActivityBucket { Key = "2-3", Label = "2-3", Count = 100 },
                },
                ClickedElements = new List<WebActivityNamedCount>
                {
                    new WebActivityNamedCount { Name = "Book a desk", Count = 12, SharePct = 60, People = 9 },
                    new WebActivityNamedCount { Name = "Search", Count = 6, SharePct = 30, People = 5 },
                    new WebActivityNamedCount { Name = "Report a colleague", Count = 2, SharePct = 10, People = 1 },
                },
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
                    Detail("Chrome", "Madrid", 8),
                    Detail("Safari", "London", 6),
                    Detail("Firefox", "Lisbon", 1),
                },
            };

            private static List<WebActivitySiteRow> Sites() => new List<WebActivitySiteRow>
            {
                new WebActivitySiteRow { Name = "Contoso Intranet", Url = "https://contoso.sharepoint.com/sites/intranet", PageViews = 800, Visits = 250, Visitors = 50 },
                new WebActivitySiteRow { Name = "Contoso HR", Url = "https://contoso.sharepoint.com/sites/hr", PageViews = 150, Visits = 40, Visitors = 7 },
                new WebActivitySiteRow { Name = "Contoso Legal", Url = "https://contoso.sharepoint.com/sites/legal", PageViews = 50, Visits = 10, Visitors = 3 },
            };

            private static WebActivityPageRow Page(string title, long views, int people) => new WebActivityPageRow
            {
                Title = title,
                Url = PageUrl(title),
                Site = "Contoso Intranet",
                PageViews = views,
                UniquePageViews = views / 2,
                Entries = views,
                Exits = views / 3,
                Bounces = views / 4,
                People = people,
            };

            private static WebActivityTransitionRow Step(string from, string to, long count, double share, int people) => new WebActivityTransitionRow
            {
                FromTitle = from,
                FromUrl = PageUrl(from),
                ToTitle = to,
                ToUrl = PageUrl(to),
                Count = count,
                SharePct = share,
                People = people,
            };

            private static WebActivityFlowRow Flow(string start, string end, long visits, double share, int people) => new WebActivityFlowRow
            {
                StartTitle = start,
                StartUrl = PageUrl(start),
                EndTitle = end,
                EndUrl = PageUrl(end),
                Visits = visits,
                SharePct = share,
                EndedWhereItStarted = start == end,
                People = people,
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
