using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// The kinds of page a synthetic Contoso intranet has.
    /// </summary>
    /// <remarks>
    /// The demo database exists to make the portal's reports legible, and the SharePoint web
    /// activity report is about <i>navigation</i> - where people land, where they give up, what they
    /// search for when the menu fails them. A flat set of interchangeable pages cannot demonstrate
    /// any of that, so the catalogue models the handful of page roles a real intranet has and the
    /// routes between them.
    /// </remarks>
    internal enum DemoPageKind
    {
        /// <summary>The front door. Most visits start here and most routes pass back through it.</summary>
        IntranetHome = 0,
        News,
        HrPolicies,
        ItHelpdesk,
        Benefits,
        Directory,

        /// <summary>Deliberately the slowest page in the catalogue, and a frequent last page of a visit.</summary>
        ExpensesForm,

        /// <summary>The search results page. A hit here is what makes a search visible in the journey.</summary>
        SearchResults,

        DeptHome,
        DeptNews,
        DeptTeam,
        DeptHowWeWork,
        DeptProjects,

        /// <summary>Deliberately almost unvisited, so the report's quiet-page list is not empty.</summary>
        DeptArchive,
    }

    /// <summary>
    /// The synthetic intranet's page catalogue, URL id layout and navigation model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the id layout lives here.</b> Page-hit rows, SharePoint audit rows, page comments and
    /// likes, and element clicks all point at the same <c>dbo.urls</c> rows. Before this existed,
    /// four separate call sites each open-coded <c>department * 3 + page + 1</c>, so widening the
    /// catalogue meant finding all four and getting all four right. Everything now goes through
    /// <see cref="SiteUrlId"/> / <see cref="GlobalUrlId"/>.
    /// </para>
    /// <para>
    /// <b>Title ids equal URL ids.</b> Every page has exactly one title, so the two dimensions are
    /// written in lockstep and a hit's <c>page_title_id</c> is simply its <c>url_id</c>. Real page
    /// titles do change, but modelling that would add a moving part that no report on the portal
    /// currently distinguishes.
    /// </para>
    /// </remarks>
    internal static class DemoWebCatalogue
    {
        /// <summary>Page kinds that exist once, on the tenant's home site.</summary>
        public static readonly DemoPageKind[] GlobalKinds =
        {
            DemoPageKind.IntranetHome, DemoPageKind.News, DemoPageKind.HrPolicies, DemoPageKind.ItHelpdesk,
            DemoPageKind.Benefits, DemoPageKind.Directory, DemoPageKind.ExpensesForm, DemoPageKind.SearchResults,
        };

        /// <summary>Page kinds that exist once per department site.</summary>
        public static readonly DemoPageKind[] SiteKinds =
        {
            DemoPageKind.DeptHome, DemoPageKind.DeptNews, DemoPageKind.DeptTeam,
            DemoPageKind.DeptHowWeWork, DemoPageKind.DeptProjects, DemoPageKind.DeptArchive,
        };

        /// <summary>Pages on every department site.</summary>
        public static int PagesPerSite => SiteKinds.Length;

        /// <summary>Department sites, one per seeded department.</summary>
        public static int SiteCount => SeedDataCatalogue.Departments.Length;

        /// <summary>
        /// The site the tenant-wide pages live on: a dedicated intranet home site, NOT a department one.
        /// </summary>
        /// <remarks>
        /// It has to be its own site collection. When the tenant-wide pages were hosted on the first
        /// department site, its <c>Home.aspx</c> and the intranet's <c>Home.aspx</c> resolved to the
        /// same URL - and because migration <c>UniqueUrlsFullUrlIndex</c> builds
        /// <c>IX_urls_full_url</c> with <c>IGNORE_DUP_KEY = ON</c>, the second row was silently
        /// discarded rather than rejected. The first sign of it was a foreign-key failure several
        /// tables later.
        /// </remarks>
        public static int HomeSite => SiteCount + 1;

        /// <summary>Sites (SharePoint webs) the catalogue produces, including the intranet home site.</summary>
        public static int WebCount => SiteCount + 1;

        /// <summary>Total distinct URLs (and therefore page titles) the catalogue produces.</summary>
        public static int UrlCount => SiteCount * PagesPerSite + GlobalKinds.Length;

        /// <summary>The <c>dbo.urls</c> id of a department site's page.</summary>
        public static int SiteUrlId(int site, DemoPageKind kind)
        {
            var index = Array.IndexOf(SiteKinds, kind);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a per-site page.");
            if (site < 1 || site > SiteCount)
                throw new ArgumentOutOfRangeException(nameof(site), site, "Site is outside the seeded catalogue.");

            return (site - 1) * PagesPerSite + index + 1;
        }

        /// <summary>The <c>dbo.urls</c> id of a tenant-wide page.</summary>
        public static int GlobalUrlId(DemoPageKind kind)
        {
            var index = Array.IndexOf(GlobalKinds, kind);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a tenant-wide page.");

            return SiteCount * PagesPerSite + index + 1;
        }

        /// <summary>True when a page kind exists once per department site rather than once per tenant.</summary>
        public static bool IsSiteKind(DemoPageKind kind) => Array.IndexOf(SiteKinds, kind) >= 0;

        /// <summary>The URL id for a kind, resolving per-site kinds against <paramref name="site"/>.</summary>
        public static int UrlId(DemoPageKind kind, int site) =>
            IsSiteKind(kind) ? SiteUrlId(site, kind) : GlobalUrlId(kind);

        /// <summary>The site a URL id belongs to. Tenant-wide pages report <see cref="HomeSite"/>.</summary>
        public static int SiteOfUrl(int urlId)
        {
            if (urlId > SiteCount * PagesPerSite) return HomeSite;
            return (urlId - 1) / PagesPerSite + 1;
        }

        /// <summary>The site collection URL of a site.</summary>
        public static string SiteUrl(int site) =>
            site == HomeSite
                ? "https://contoso.sharepoint.com/sites/contoso-intranet"
                : "https://contoso.sharepoint.com/sites/demo-" + site.ToString("D2");

        /// <summary>The full page URL of a catalogue entry.</summary>
        public static string PageUrl(DemoPageKind kind, int site) =>
            SiteUrl(IsSiteKind(kind) ? site : HomeSite) + "/SitePages/" + RelativePath(kind, site);

        /// <summary>
        /// The page title, which is what the report's leaderboards actually show.
        /// </summary>
        /// <remarks>
        /// One site's pages are deliberately Greek. Page titles are customer text stored in
        /// <c>nvarchar</c>, and a demo database that only ever contains Latin text proves nothing
        /// about whether the report renders a real tenant's content correctly.
        /// </remarks>
        public static string Title(DemoPageKind kind, int site)
        {
            var department = DepartmentName(site);

            switch (kind)
            {
                case DemoPageKind.IntranetHome: return "Contoso intranet - Home";
                case DemoPageKind.News: return "Contoso news";
                case DemoPageKind.HrPolicies: return "HR policies and handbook";
                case DemoPageKind.ItHelpdesk: return "IT helpdesk";
                case DemoPageKind.Benefits: return "Pay, pension and benefits";
                case DemoPageKind.Directory: return "People directory";
                case DemoPageKind.ExpensesForm: return "Expenses claim form";
                case DemoPageKind.SearchResults: return "Search results";
                case DemoPageKind.DeptHome:
                    return UsesGreek(site) ? "Καλημέρα κόσμε - " + department : department + " - Home";
                case DemoPageKind.DeptNews: return department + " news";
                case DemoPageKind.DeptTeam: return department + " team";
                case DemoPageKind.DeptHowWeWork: return department + " - how we work";
                case DemoPageKind.DeptProjects: return department + " projects";
                case DemoPageKind.DeptArchive: return department + " archive (2019)";
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown page kind.");
            }
        }

        /// <summary>The department (or, for the home site, the intranet) a site represents.</summary>
        public static string DepartmentName(int site) =>
            site == HomeSite
                ? "Contoso intranet"
                : "Contoso " + SeedDataCatalogue.Departments[Math.Max(0, Math.Min(SiteCount, site) - 1)];

        /// <summary>True when this site's pages carry non-Latin text.</summary>
        private static bool UsesGreek(int site) => site == 3;

        private static string Localise(int site, string path) => UsesGreek(site) ? GreekFolder + path : path;

        private static string RelativePath(DemoPageKind kind, int site)
        {
            switch (kind)
            {
                case DemoPageKind.IntranetHome: return "Home.aspx";
                case DemoPageKind.News: return "Contoso-news.aspx";
                case DemoPageKind.HrPolicies: return "HR-policies.aspx";
                case DemoPageKind.ItHelpdesk: return "IT-helpdesk.aspx";
                case DemoPageKind.Benefits: return "Pay-and-benefits.aspx";
                case DemoPageKind.Directory: return "People-directory.aspx";
                case DemoPageKind.ExpensesForm: return "Expenses-claim.aspx";
                case DemoPageKind.SearchResults: return "Search-results.aspx";
                case DemoPageKind.DeptHome:
                    return UsesGreek(site) ? GreekFolder + "Home.aspx" : "Home.aspx";
                case DemoPageKind.DeptNews: return Localise(site, "News.aspx");
                case DemoPageKind.DeptTeam: return Localise(site, "Our-team.aspx");
                case DemoPageKind.DeptHowWeWork: return Localise(site, "How-we-work.aspx");
                case DemoPageKind.DeptProjects: return Localise(site, "Projects.aspx");
                case DemoPageKind.DeptArchive: return Localise(site, "Archive-2019.aspx");
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown page kind.");
            }
        }

        /// <summary>The folder every page on the non-Latin site lives under.</summary>
        private const string GreekFolder = "Καλημέρα-κόσμε/";

        /// <summary>
        /// URLs that contain non-Latin characters, so a test can assert the count rather than guess it.
        /// </summary>
        /// <remarks>
        /// One whole site is non-Latin rather than one page per site. A department that genuinely
        /// works in Greek names all of its pages in Greek; sprinkling a single Greek page across every
        /// site would be a shape no tenant produces, and it is the shape a report has to cope with -
        /// a site whose entire leaderboard is non-Latin - that is worth proving.
        /// </remarks>
        public static int UnicodeUrlCount => PagesPerSite;

        #region Behaviour

        /// <summary>
        /// Mean seconds a visitor spends on a page of this kind before moving on.
        /// </summary>
        /// <remarks>
        /// Varied on purpose: a report whose dwell column is the same number on every row cannot be
        /// used to demonstrate that the column means anything. A form is filled in slowly, a news
        /// item is skimmed, a directory is a stepping stone.
        /// </remarks>
        public static double DwellSeconds(DemoPageKind kind)
        {
            switch (kind)
            {
                case DemoPageKind.IntranetHome: return 24;
                case DemoPageKind.News: return 95;
                case DemoPageKind.HrPolicies: return 140;
                case DemoPageKind.ItHelpdesk: return 70;
                case DemoPageKind.Benefits: return 120;
                case DemoPageKind.Directory: return 30;
                case DemoPageKind.ExpensesForm: return 210;
                case DemoPageKind.SearchResults: return 26;
                case DemoPageKind.DeptHome: return 35;
                case DemoPageKind.DeptNews: return 80;
                case DemoPageKind.DeptTeam: return 45;
                case DemoPageKind.DeptHowWeWork: return 160;
                case DemoPageKind.DeptProjects: return 110;
                case DemoPageKind.DeptArchive: return 55;
                default: return 45;
            }
        }

        /// <summary>
        /// Mean seconds a page of this kind takes to load.
        /// </summary>
        /// <remarks>
        /// The expenses form and the 2019 archive are deliberately slow, so the report's
        /// slowest-pages table and its 95th-percentile load figure have something real to find. A
        /// demo where every page loads in 0.9 seconds makes the performance tab look broken.
        /// </remarks>
        public static double LoadSeconds(DemoPageKind kind)
        {
            switch (kind)
            {
                case DemoPageKind.ExpensesForm: return 5.4;
                case DemoPageKind.DeptArchive: return 3.6;
                case DemoPageKind.SearchResults: return 1.9;
                case DemoPageKind.HrPolicies: return 1.5;
                case DemoPageKind.DeptProjects: return 1.4;
                default: return 0.9;
            }
        }

        /// <summary>
        /// The chance, in percent, that a visit ends after a page of this kind.
        /// </summary>
        /// <remarks>
        /// Per page rather than a flat rate, because the whole point of the report's exit-page and
        /// bounce tables is that the answer differs by page. A completed expenses claim is a
        /// legitimate end to a visit; a bounce off the intranet home page is not.
        /// </remarks>
        public static int ExitPercent(DemoPageKind kind)
        {
            switch (kind)
            {
                case DemoPageKind.ExpensesForm: return 68;
                case DemoPageKind.DeptArchive: return 60;
                case DemoPageKind.SearchResults: return 40;
                case DemoPageKind.Benefits: return 38;
                case DemoPageKind.HrPolicies: return 32;
                case DemoPageKind.IntranetHome: return 14;
                case DemoPageKind.DeptHome: return 16;
                default: return 26;
            }
        }

        /// <summary>Where visits start, as weighted page kinds.</summary>
        /// <remarks>
        /// Not uniform. Most people arrive at the front door, a sizeable minority arrive by deep link
        /// straight to the thing they need, and those two groups behave completely differently - which
        /// is exactly what the entry-page and bounce tables are for.
        /// </remarks>
        public static readonly (DemoPageKind Kind, int Weight)[] EntryWeights =
        {
            (DemoPageKind.IntranetHome, 42),
            (DemoPageKind.DeptHome, 20),
            (DemoPageKind.News, 12),
            (DemoPageKind.HrPolicies, 8),
            (DemoPageKind.ItHelpdesk, 6),
            (DemoPageKind.ExpensesForm, 5),
            (DemoPageKind.DeptNews, 4),
            (DemoPageKind.Benefits, 2),
            (DemoPageKind.DeptProjects, 1),
        };

        private static readonly Dictionary<DemoPageKind, (DemoPageKind Kind, int Weight)[]> Routes =
            new Dictionary<DemoPageKind, (DemoPageKind, int)[]>
            {
                [DemoPageKind.IntranetHome] = new[]
                {
                    (DemoPageKind.News, 30), (DemoPageKind.DeptHome, 25), (DemoPageKind.HrPolicies, 15),
                    (DemoPageKind.ItHelpdesk, 10), (DemoPageKind.Benefits, 8), (DemoPageKind.Directory, 7),
                    (DemoPageKind.ExpensesForm, 5),
                },
                [DemoPageKind.News] = new[]
                {
                    (DemoPageKind.DeptNews, 30), (DemoPageKind.IntranetHome, 25), (DemoPageKind.DeptHome, 20),
                    (DemoPageKind.Directory, 15), (DemoPageKind.HrPolicies, 10),
                },
                [DemoPageKind.HrPolicies] = new[]
                {
                    (DemoPageKind.Benefits, 40), (DemoPageKind.ExpensesForm, 25),
                    (DemoPageKind.IntranetHome, 20), (DemoPageKind.SearchResults, 15),
                },
                [DemoPageKind.ItHelpdesk] = new[]
                {
                    (DemoPageKind.SearchResults, 40), (DemoPageKind.IntranetHome, 30), (DemoPageKind.Directory, 30),
                },
                [DemoPageKind.Benefits] = new[]
                {
                    (DemoPageKind.HrPolicies, 40), (DemoPageKind.IntranetHome, 35), (DemoPageKind.ExpensesForm, 25),
                },
                [DemoPageKind.Directory] = new[]
                {
                    (DemoPageKind.DeptTeam, 50), (DemoPageKind.IntranetHome, 30), (DemoPageKind.DeptHome, 20),
                },
                [DemoPageKind.ExpensesForm] = new[]
                {
                    (DemoPageKind.IntranetHome, 60), (DemoPageKind.HrPolicies, 40),
                },
                [DemoPageKind.SearchResults] = new[]
                {
                    (DemoPageKind.DeptHowWeWork, 30), (DemoPageKind.DeptProjects, 25),
                    (DemoPageKind.HrPolicies, 25), (DemoPageKind.ItHelpdesk, 20),
                },
                [DemoPageKind.DeptHome] = new[]
                {
                    (DemoPageKind.DeptNews, 30), (DemoPageKind.DeptTeam, 25), (DemoPageKind.DeptProjects, 20),
                    (DemoPageKind.DeptHowWeWork, 15), (DemoPageKind.IntranetHome, 10),
                },
                [DemoPageKind.DeptNews] = new[]
                {
                    (DemoPageKind.DeptHome, 45), (DemoPageKind.IntranetHome, 30), (DemoPageKind.DeptProjects, 25),
                },
                [DemoPageKind.DeptTeam] = new[]
                {
                    (DemoPageKind.DeptHome, 50), (DemoPageKind.Directory, 30), (DemoPageKind.IntranetHome, 20),
                },
                [DemoPageKind.DeptHowWeWork] = new[]
                {
                    (DemoPageKind.DeptProjects, 40), (DemoPageKind.DeptHome, 35), (DemoPageKind.SearchResults, 25),
                },
                [DemoPageKind.DeptProjects] = new[]
                {
                    (DemoPageKind.DeptHome, 40), (DemoPageKind.DeptHowWeWork, 30),
                    (DemoPageKind.IntranetHome, 20), (DemoPageKind.DeptArchive, 10),
                },
                [DemoPageKind.DeptArchive] = new[]
                {
                    (DemoPageKind.DeptHome, 70), (DemoPageKind.IntranetHome, 30),
                },
            };

        /// <summary>The next page a visitor moves to from <paramref name="from"/>.</summary>
        public static DemoPageKind NextKind(DemoPageKind from, uint draw) => Pick(Routes[from], draw);

        /// <summary>The page a visit starts on.</summary>
        public static DemoPageKind EntryKind(uint draw) => Pick(EntryWeights, draw);

        private static DemoPageKind Pick((DemoPageKind Kind, int Weight)[] options, uint draw)
        {
            var total = options.Sum(o => o.Weight);
            var target = (int)(draw % (uint)total);

            foreach (var option in options)
            {
                target -= option.Weight;
                if (target < 0) return option.Kind;
            }

            return options[options.Length - 1].Kind;
        }

        #endregion

        #region Client dimensions

        /// <summary>
        /// Browsers, with versions, as Application Insights records them.
        /// </summary>
        /// <remarks>
        /// Versions are part of the name in the real import (<c>browsers.browser_name</c> holds
        /// "Chrome 128.0", not "Chrome"), and the report's browser table is most useful for spotting
        /// an old build still in the estate - so the demo has to carry two versions of the same
        /// browser or that column demonstrates nothing.
        /// </remarks>
        public static readonly (string Name, int Weight)[] Browsers =
        {
            ("Edge 129.0", 34),
            ("Chrome 129.0", 26),
            ("Edge 127.0", 12),
            ("Chrome 126.0", 10),
            ("Safari 17.6", 9),
            ("Firefox 130.0", 6),
            ("Edge 118.0", 3),
        };

        /// <summary>
        /// Devices, as Application Insights' <c>client_Model</c> reports them.
        /// </summary>
        /// <remarks>
        /// The names matter: the report classifies mobile by matching this free text, so the demo has
        /// to use the shapes the real import produces ("Workstation", "iPhone") rather than tidy
        /// labels of its own. Two of these are mobile, which is what gives the mobile-share figure and
        /// the device-mix trend something to show.
        /// </remarks>
        public static readonly (string Name, int Weight)[] Devices =
        {
            ("Workstation", 55),
            ("Laptop", 26),
            ("iPhone", 11),
            ("Android Phone", 5),
            ("iPad", 3),
        };

        /// <summary>The operating system a device implies.</summary>
        public static string OperatingSystemFor(string device)
        {
            switch (device)
            {
                case "iPhone":
                case "iPad": return "iOS 18";
                case "Android Phone": return "Android 15";
                case "Laptop": return "Windows 11";
                default: return "Windows 10";
            }
        }

        /// <summary>Every operating system name <see cref="OperatingSystemFor"/> can return, in id order.</summary>
        public static readonly string[] OperatingSystems = { "Windows 10", "Windows 11", "macOS 15", "iOS 18", "Android 15" };

        /// <summary>True when a device name is a phone or tablet, matching the report's classification.</summary>
        public static bool IsMobile(string device) =>
            device == "iPhone" || device == "iPad" || device == "Android Phone";

        /// <summary>Extra seconds a mobile device adds to a page load.</summary>
        public const double MobileLoadPenaltySeconds = 0.8;

        /// <summary>Picks a weighted client attribute.</summary>
        public static string PickWeighted((string Name, int Weight)[] options, uint draw)
        {
            var total = options.Sum(o => o.Weight);
            var target = (int)(draw % (uint)total);

            foreach (var option in options)
            {
                target -= option.Weight;
                if (target < 0) return option.Name;
            }

            return options[options.Length - 1].Name;
        }

        #endregion

        #region Search and clicks

        /// <summary>
        /// What people search the intranet for.
        /// </summary>
        /// <remarks>
        /// The last four are deliberately things this synthetic intranet has no page for, so the
        /// report's "terms that lead nowhere" table - the one an intranet owner can act on - is not
        /// empty in a demo. One term is Greek for the same reason the page titles are.
        /// </remarks>
        public static readonly string[] SearchTerms =
        {
            "expenses", "holiday policy", "payslip", "org chart", "vpn",
            "password reset", "travel booking", "maternity leave", "Καλημέρα κόσμε",
            "car parking permit", "cycle to work scheme", "long service award", "tv licence refund",
        };

        /// <summary>Terms with no matching page, which therefore end the visit that searched for them.</summary>
        public static readonly string[] DeadEndSearchTerms =
        {
            "car parking permit", "cycle to work scheme", "long service award", "tv licence refund",
        };

        /// <summary>True when a term is one that leads nowhere on this synthetic intranet.</summary>
        public static bool IsDeadEndTerm(string term) => Array.IndexOf(DeadEndSearchTerms, term) >= 0;

        /// <summary>Titles of the page elements the tracker records clicks on.</summary>
        public static readonly string[] ClickTitles =
        {
            "Contoso guidance", "Contoso project workspace", "Contoso learning",
            "Book annual leave", "Raise an IT ticket", "Staff directory",
        };

        /// <summary>CSS class sets the clicked elements carry.</summary>
        public static readonly string[] ClickClasses =
        {
            "contoso-navigation-link", "contoso-quick-link", "contoso-learning-card",
            "contoso-hero-tile", "contoso-callout-button", "contoso-list-link",
        };

        #endregion
    }
}
