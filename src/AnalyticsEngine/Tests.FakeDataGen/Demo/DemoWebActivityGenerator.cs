using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// Writes the synthetic SharePoint page-hit stream: sessions, hits, searches and element clicks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of <c>DemoGenerator</c> and <c>DemoCollaborationGenerator</c>, which between them
    /// used to emit a fixed three-page path per user per day. That was enough to prove the importer's
    /// tables were populated and not much else: every visit had the same depth, the same entry page
    /// and the same exit page, so the web-activity report's bounce rate, entry/exit tables, journeys,
    /// load-time distribution and quiet-page list were all either constant or empty.
    /// </para>
    /// <para>
    /// This walks a real navigation model instead (see <see cref="DemoWebCatalogue"/>): a weighted
    /// entry page, a weighted route from each page, and a per-page chance the visit ends there. The
    /// resulting data has the properties the report is built to find - a home page with a low bounce
    /// rate, an expenses form that is slow and is where visits stop, an archive nobody opens, and
    /// searches that lead nowhere.
    /// </para>
    /// <para>
    /// <b>Ids are running counters, not formulae.</b> Sessions and hits are numbered as they are
    /// written. The previous arithmetic ids (<c>(user - 1) * days + day + 1</c>) forced exactly one
    /// session per user per day and exactly three hits per session; any variation in either would
    /// have silently collided. The counters are still deterministic for a given seed because the
    /// generator walks users and days in a fixed order.
    /// </para>
    /// </remarks>
    internal sealed class DemoWebActivityGenerator
    {
        /// <summary>Deepest visit the walk will produce, so one unlucky draw cannot run away.</summary>
        private const int MaxPagesPerVisit = 9;

        /// <summary>Chance, in percent, that a visit is a single page - the bounce floor.</summary>
        private const int BouncePercent = 24;

        /// <summary>Chance, in percent, that a visitor comes back for a second visit the same day.</summary>
        private const int SecondVisitPercent = 22;

        /// <summary>Chance, in percent, that a page view records a click on a tracked element.</summary>
        private const int ClickPercent = 35;

        /// <summary>
        /// Chance, in percent, that a page view has no resolved location.
        /// </summary>
        /// <remarks>
        /// Real geo-IP resolution fails for VPN and proxy traffic, and the report shows the
        /// unresolved share precisely so a reader knows the location breakdown is a sample. A demo
        /// where it is always zero hides the caveat that matters most on that tab.
        /// </remarks>
        private const int UnlocatedPercent = 7;

        /// <summary>
        /// First salt in this generator's private range.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Salt ranges must not overlap between generators.</b> <see cref="DemoRandom.Value"/> is a
        /// pure hash of (seed, user, day, salt), so two generators drawing the same salt for the same
        /// user and day get the SAME number - which silently correlates two decisions that are
        /// supposed to be independent.
        /// </para>
        /// <para>
        /// This is not hypothetical: this generator originally used 2010-2081, and
        /// <see cref="DemoPowerPlatformGenerator"/> uses 2010-2015 for the same (user, day). The
        /// browser choice and the "did this user use Power Apps today?" draw were the same number, so
        /// every user assigned Edge 129 generated Power Apps activity and every user on Safari or
        /// Firefox never did. Both data sets still looked plausible on their own.
        /// </para>
        /// <para>
        /// Allocated elsewhere: 1-6, 50-51, 60, 71, 90, 110, 130 (base generator, Copilot, DLP),
        /// 1010-1060 (collaboration), 2010-2015 (Power Platform).
        /// </para>
        /// </remarks>
        private const int SaltBase = 5000;

        private readonly DemoOptions _options;
        private readonly DemoCalendar _calendar;
        private readonly IDemoSink _sink;
        private readonly Dictionary<string, int> _browsers = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _devices = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _operatingSystems = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _countries = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _cities = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _provinces = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _searchTerms = new Dictionary<string, int>(StringComparer.Ordinal);

        private int _sessions;
        private int _hits;

        public DemoWebActivityGenerator(DemoOptions options, DemoCalendar calendar, IDemoSink sink)
        {
            _options = options;
            _calendar = calendar;
            _sink = sink;
        }

        /// <summary>Sessions written so far - used by the summary and by the tests.</summary>
        public int SessionCount => _sessions;

        /// <summary>Page views written so far.</summary>
        public int HitCount => _hits;

        #region Dimensions

        /// <summary>
        /// Writes the URL, title and client lookups the hit stream points at.
        /// </summary>
        /// <remarks>
        /// Called even when the Web area is switched off, because <c>dbo.urls</c> is shared: the
        /// SharePoint audit rows, page comments and likes all reference the same pages. Only the
        /// client-side lookups (browser, device, OS, location) are web-specific.
        /// </remarks>
        public void WriteSharedDimensions()
        {
            for (int site = 1; site <= DemoWebCatalogue.WebCount; site++)
            {
                _sink.Write(DemoTables.Sites, site, DemoWebCatalogue.SiteUrl(site),
                    DemoRandom.Id(_options.Seed, 2, site).ToString());
                _sink.Write(DemoTables.Webs, site, DemoWebCatalogue.SiteUrl(site),
                    DemoWebCatalogue.DepartmentName(site), site);
            }

            for (int site = 1; site <= DemoWebCatalogue.SiteCount; site++)
            {
                foreach (var kind in DemoWebCatalogue.SiteKinds)
                {
                    int id = DemoWebCatalogue.SiteUrlId(site, kind);
                    _sink.Write(DemoTables.Urls, id, DemoWebCatalogue.PageUrl(kind, site));
                    _sink.Write(DemoTables.Titles, id, DemoWebCatalogue.Title(kind, site));
                }
            }

            foreach (var kind in DemoWebCatalogue.GlobalKinds)
            {
                int id = DemoWebCatalogue.GlobalUrlId(kind);
                _sink.Write(DemoTables.Urls, id, DemoWebCatalogue.PageUrl(kind, DemoWebCatalogue.HomeSite));
                _sink.Write(DemoTables.Titles, id, DemoWebCatalogue.Title(kind, DemoWebCatalogue.HomeSite));
            }
        }

        /// <summary>Writes the client lookups the page-hit stream points at.</summary>
        /// <remarks>
        /// Written whether or not the Web area is selected, matching the behaviour these lookups have
        /// always had. They are small, and a demo database whose <c>browsers</c> table is empty looks
        /// like a broken schema rather than an unselected area.
        /// </remarks>
        public void WriteClientDimensions()
        {
            foreach (var browser in DemoWebCatalogue.Browsers)
                Register(_browsers, DemoTables.Browsers, browser.Name);
            foreach (var device in DemoWebCatalogue.Devices)
                Register(_devices, DemoTables.Devices, device.Name);
            foreach (var os in DemoWebCatalogue.OperatingSystems)
                Register(_operatingSystems, DemoTables.OperatingSystems, os);

            foreach (var country in SeedDataCatalogue.Countries)
                Register(_countries, DemoTables.WebCountries, Ascii(country));
            foreach (var city in SeedDataCatalogue.Locales.Select(l => l.City).Distinct(StringComparer.Ordinal))
                Register(_cities, DemoTables.WebCities, Ascii(city));
            foreach (var province in SeedDataCatalogue.StatesOrProvinces)
                Register(_provinces, DemoTables.WebProvinces, province);
        }

        /// <summary>Writes the search-term and clicked-element lookups, which only the Web area uses.</summary>
        public void WriteSearchAndClickDimensions()
        {
            foreach (var term in DemoWebCatalogue.SearchTerms)
                Register(_searchTerms, DemoTables.SearchTerms, term);

            for (int i = 0; i < DemoWebCatalogue.ClickTitles.Length; i++)
                _sink.Write(DemoTables.ClickTitles, i + 1, DemoWebCatalogue.ClickTitles[i]);
            for (int i = 0; i < DemoWebCatalogue.ClickClasses.Length; i++)
                _sink.Write(DemoTables.ClickClasses, i + 1, DemoWebCatalogue.ClickClasses[i]);
        }

        private void Register(Dictionary<string, int> lookup, DemoTable table, string name)
        {
            if (lookup.ContainsKey(name)) return;
            int id = lookup.Count + 1;
            lookup.Add(name, id);
            _sink.Write(table, id, name);
        }

        /// <summary>
        /// Legacy web-traffic location names are <c>varchar</c>; do not pretend that boundary is Unicode.
        /// </summary>
        /// <remarks>
        /// <c>dbo.cities.city_name</c> and <c>dbo.countries.country_name</c> in the shipped schema are
        /// single-code-page columns, so a synthetic "Sao Paulo" is honest and a synthetic "São Paulo"
        /// would be a test that asserts a round-trip the database cannot actually perform. Page titles
        /// and URLs are <c>nvarchar</c> and keep their real Unicode samples.
        /// </remarks>
        private static string Ascii(string value) =>
            value.Replace("São Paulo", "Sao Paulo").Replace("Zürich", "Zurich");

        #endregion

        #region Facts

        /// <summary>
        /// Writes one day of browsing for one user.
        /// </summary>
        /// <param name="intensity">
        /// How busy the user's day was, from the shared activity timeline. Used only to decide whether
        /// they browsed at all and how deep they went - the navigation itself comes from the catalogue.
        /// </param>
        public void WriteDay(DemoUser user, int day, int intensity)
        {
            if (intensity <= 0) return;

            int visits = Draw(user, day, SaltBase) % 100 < SecondVisitPercent ? 2 : 1;

            for (int visit = 0; visit < visits; visit++)
            {
                WriteVisit(user, day, visit, intensity);
            }
        }

        private void WriteVisit(DemoUser user, int day, int visit, int intensity)
        {
            int session = ++_sessions;
            _sink.Write(DemoTables.Sessions, session,
                DemoRandom.Id(_options.Seed, 5, user.Id, day, visit).ToString("N"), user.Id);

            // One client per visit. Someone does not change browser or device half-way through a
            // session, and a report that grouped by device would count such a visit twice.
            string browser = DemoWebCatalogue.PickWeighted(DemoWebCatalogue.Browsers, Draw(user, day, SaltBase + 10 + visit));
            string device = DemoWebCatalogue.PickWeighted(DemoWebCatalogue.Devices, Draw(user, day, SaltBase + 20 + visit));
            string operatingSystem = DemoWebCatalogue.OperatingSystemFor(device);
            bool mobile = DemoWebCatalogue.IsMobile(device);

            bool located = Draw(user, day, SaltBase + 30 + visit) % 100 >= UnlocatedPercent;
            int? country = located ? (int?)_countries[Ascii(user.Profile.Country)] : null;
            int? city = located ? (int?)_cities[Ascii(user.Profile.City)] : null;
            int? province = located ? (int?)_provinces[user.Profile.StateOrProvince] : null;

            var start = _calendar.Timestamp(user.Zone, day, Draw(user, day, SaltBase + 40 + visit), visit * 3);

            // The bounce floor is applied first so a single-page visit is a deliberate outcome rather
            // than an accident of the walk - the report's bounce rate is a headline figure and it has
            // to be stable enough to demonstrate.
            bool bounce = Draw(user, day, SaltBase + 50 + visit) % 100 < BouncePercent;
            int budget = bounce ? 1 : Math.Min(MaxPagesPerVisit, 2 + (int)(Draw(user, day, SaltBase + 60 + visit) % (uint)Math.Max(1, Math.Min(6, intensity + 2))));

            var kind = DemoWebCatalogue.EntryKind(Draw(user, day, SaltBase + 70 + visit));
            int site = SiteFor(user, kind, Draw(user, day, SaltBase + 80 + visit));
            var stamp = start;

            for (int step = 0; step < budget; step++)
            {
                int salt = SaltBase + 100 + visit * 200 + step * 10;
                int urlId = DemoWebCatalogue.UrlId(kind, site);
                double dwell = Jitter(DemoWebCatalogue.DwellSeconds(kind), Draw(user, day, salt + 1), 0.55);
                double load = Jitter(
                    DemoWebCatalogue.LoadSeconds(kind) + (mobile ? DemoWebCatalogue.MobileLoadPenaltySeconds : 0),
                    Draw(user, day, salt + 2),
                    0.35);

                int hitId = ++_hits;
                _sink.Write(DemoTables.Hits,
                    urlId, stamp, session, urlId, DemoWebCatalogue.SiteOfUrl(urlId),
                    _browsers[browser], _devices[device], _operatingSystems[operatingSystem],
                    Math.Round(dwell, 2), Math.Round(load, 3),
                    DemoRandom.Id(_options.Seed, 7, user.Id, day, visit * 16 + step),
                    country, city, province, hitId);

                if (Draw(user, day, salt + 3) % 100 < ClickPercent)
                {
                    int element = 1 + (int)(Draw(user, day, salt + 4) % (uint)DemoWebCatalogue.ClickTitles.Length);
                    _sink.Write(DemoTables.Clicks, urlId, element, element, hitId,
                        stamp.AddSeconds(Math.Max(1, Math.Round(dwell / 3, 0))));
                }

                // A search is what PUT the visitor on the results page, so it is timestamped just
                // before that hit. Writing it after would make every search look like a dead end.
                if (kind == DemoPageKind.SearchResults)
                {
                    string term = SearchTerm(user, day, salt + 5);
                    _sink.Write(DemoTables.Searches, session, _searchTerms[term], stamp.AddSeconds(-2));

                    // A term this intranet has no page for ends the visit: that is precisely what the
                    // report's "searches leading nowhere" measure is looking for.
                    if (DemoWebCatalogue.IsDeadEndTerm(term)) break;
                }

                stamp = stamp.AddSeconds(Math.Max(3, Math.Round(dwell, 0)) + Math.Round(load, 0));
                if (stamp >= start.AddHours(3)) break;

                if (step + 1 >= budget) break;
                if (Draw(user, day, salt + 6) % 100 < DemoWebCatalogue.ExitPercent(kind)) break;

                var next = DemoWebCatalogue.NextKind(kind, Draw(user, day, salt + 7));
                site = SiteFor(user, next, Draw(user, day, salt + 8));
                kind = next;
            }
        }

        /// <summary>
        /// The site a per-site page belongs to for this visitor.
        /// </summary>
        /// <remarks>
        /// Usually their own department, but one visit in five wanders onto another department's site.
        /// Without that, every site's traffic would come from exactly one department and the report's
        /// site leaderboard would be a restatement of the org chart rather than a measure of reach.
        /// </remarks>
        private int SiteFor(DemoUser user, DemoPageKind kind, uint draw)
        {
            if (!DemoWebCatalogue.IsSiteKind(kind)) return DemoWebCatalogue.HomeSite;

            int own = user.Department + 1;
            if (draw % 5 != 0) return own;

            return (int)(draw / 5 % (uint)DemoWebCatalogue.SiteCount) + 1;
        }

        /// <summary>
        /// Picks a search term, weighting the dead-end terms down.
        /// </summary>
        /// <remarks>
        /// They have to be a minority. If a third of all searches led nowhere the demo would look
        /// like a broken intranet rather than a normal one with a handful of content gaps, and the
        /// report's dead-end percentage would stop being a signal.
        /// </remarks>
        private string SearchTerm(DemoUser user, int day, int salt)
        {
            uint draw = Draw(user, day, salt);
            var terms = DemoWebCatalogue.SearchTerms;

            if (draw % 5 == 0)
            {
                var deadEnds = DemoWebCatalogue.DeadEndSearchTerms;
                return deadEnds[(int)(draw / 5 % (uint)deadEnds.Length)];
            }

            var usable = terms.Where(t => !DemoWebCatalogue.IsDeadEndTerm(t)).ToArray();
            return usable[(int)(draw % (uint)usable.Length)];
        }

        private uint Draw(DemoUser user, int day, int salt) => DemoRandom.Value(_options.Seed, user.Id, day, salt);

        /// <summary>
        /// Spreads a mean value over a band around it.
        /// </summary>
        /// <remarks>
        /// Every dwell and load figure in the demo used to be the same handful of numbers, so the
        /// report's averages, percentiles and load-time histogram were all degenerate. A proportional
        /// band keeps a slow page slow and a fast page fast while giving the distribution a shape.
        /// </remarks>
        private static double Jitter(double mean, uint draw, double spread)
        {
            double factor = 1 - spread + (draw % 1000) / 1000.0 * spread * 2;
            return Math.Max(0.05, mean * factor);
        }

        #endregion
    }
}
