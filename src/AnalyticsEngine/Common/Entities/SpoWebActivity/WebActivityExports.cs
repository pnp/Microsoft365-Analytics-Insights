using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// CSV column definitions for the web-activity page's tabular sections.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="CsvSerialiser"/> rather than formatting CSV here. That is deliberate: it
    /// already handles the three things that silently ruin an exported list - a UTF-8 BOM so Excel
    /// renders non-Latin page titles and city names instead of mojibake, neutralised formula injection
    /// for values that come from the customer's own site (a page literally titled <c>=cmd</c> is a
    /// valid SharePoint page), and invariant number and date formatting.
    /// </remarks>
    public static class WebActivityExports
    {
        /// <summary>The sections that can be exported, as their URL segment.</summary>
        public static readonly string[] Sections =
        {
            "pages", "quiet-pages", "slow-pages", "entry-pages", "exit-pages",
            "transitions", "search-terms", "technology",
        };

        /// <summary>True when <paramref name="section"/> names a real export.</summary>
        public static bool IsKnownSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return false;

            foreach (var known in Sections)
            {
                if (string.Equals(known, section, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Every page export shares one column set.
        /// </summary>
        /// <remarks>
        /// The top-pages, quiet-pages, slow-pages, entry-pages and exit-pages exports differ only in
        /// which rows they carry and how they are ordered. Giving them different shapes would make them
        /// impossible to compare in a spreadsheet, which is the only reason anyone exports them.
        /// </remarks>
        public static IReadOnlyList<CsvColumn<WebActivityPageRow>> PageColumns()
        {
            return new List<CsvColumn<WebActivityPageRow>>
            {
                new CsvColumn<WebActivityPageRow>("Page title", r => r.Title),
                new CsvColumn<WebActivityPageRow>("URL", r => r.Url),
                new CsvColumn<WebActivityPageRow>("Site", r => r.Site),
                new CsvColumn<WebActivityPageRow>("Page views", r => r.PageViews),
                new CsvColumn<WebActivityPageRow>("Unique page views (once per visit)", r => r.UniquePageViews),
                new CsvColumn<WebActivityPageRow>("Average seconds on page", r => r.AverageSecondsOnPage),
                new CsvColumn<WebActivityPageRow>("Average load seconds", r => r.AverageLoadSeconds),
                new CsvColumn<WebActivityPageRow>("Visits entering here", r => r.Entries),
                new CsvColumn<WebActivityPageRow>("Visits ending here", r => r.Exits),
                new CsvColumn<WebActivityPageRow>("Bounces (entered and left without going further)", r => r.Bounces),
                new CsvColumn<WebActivityPageRow>("Bounce rate (% of entries)", r => r.BouncePct),
            };
        }

        /// <summary>The page-to-page steps.</summary>
        public static IReadOnlyList<CsvColumn<WebActivityTransitionRow>> TransitionColumns()
        {
            return new List<CsvColumn<WebActivityTransitionRow>>
            {
                new CsvColumn<WebActivityTransitionRow>("From page", r => r.FromTitle),
                new CsvColumn<WebActivityTransitionRow>("From URL", r => r.FromUrl),
                new CsvColumn<WebActivityTransitionRow>("To page", r => r.ToTitle),
                new CsvColumn<WebActivityTransitionRow>("To URL", r => r.ToUrl),
                new CsvColumn<WebActivityTransitionRow>("Times taken", r => r.Count),
                new CsvColumn<WebActivityTransitionRow>("Share of steps out of the From page (%)", r => r.SharePct),
            };
        }

        /// <summary>
        /// The search-term list.
        /// </summary>
        /// <remarks>
        /// The dead-end column carries its definition in the heading. "Dead ends" on its own reads as
        /// "searches that returned nothing", which is not what it measures and not something this
        /// import can know - so the heading says what it actually counted.
        /// </remarks>
        public static IReadOnlyList<CsvColumn<WebActivitySearchTermRow>> SearchTermColumns()
        {
            return new List<CsvColumn<WebActivitySearchTermRow>>
            {
                new CsvColumn<WebActivitySearchTermRow>("Search term", r => r.Term),
                new CsvColumn<WebActivitySearchTermRow>("Searches", r => r.Searches),
                new CsvColumn<WebActivitySearchTermRow>("People who searched", r => r.Searchers),
                new CsvColumn<WebActivitySearchTermRow>(
                    "Searches with no further page view in the visit", r => r.DeadEnds),
                new CsvColumn<WebActivitySearchTermRow>("Share of that term's searches (%)", r => r.DeadEndPct),
            };
        }

        /// <summary>The browser / device / OS / city breakdown.</summary>
        public static IReadOnlyList<CsvColumn<WebActivityTechnologyDetailRow>> TechnologyColumns()
        {
            return new List<CsvColumn<WebActivityTechnologyDetailRow>>
            {
                new CsvColumn<WebActivityTechnologyDetailRow>("Browser", r => r.Browser),
                new CsvColumn<WebActivityTechnologyDetailRow>("Device", r => r.Device),
                new CsvColumn<WebActivityTechnologyDetailRow>("Operating system", r => r.OperatingSystem),
                new CsvColumn<WebActivityTechnologyDetailRow>("City", r => r.City),
                new CsvColumn<WebActivityTechnologyDetailRow>("Visits", r => r.Visits),
                new CsvColumn<WebActivityTechnologyDetailRow>("Visitors", r => r.Visitors),
                new CsvColumn<WebActivityTechnologyDetailRow>("Page views", r => r.PageViews),
                new CsvColumn<WebActivityTechnologyDetailRow>("Page views per visit", r => r.PageViewsPerVisit),
                new CsvColumn<WebActivityTechnologyDetailRow>("Average seconds on page", r => r.AverageSecondsOnPage),
                new CsvColumn<WebActivityTechnologyDetailRow>("Average load seconds", r => r.AverageLoadSeconds),
            };
        }

        /// <summary>The download file name prefix for a section.</summary>
        public static string FileNamePrefix(string section)
        {
            switch ((section ?? string.Empty).ToLowerInvariant())
            {
                case "quiet-pages": return "web-activity-quiet-pages";
                case "slow-pages": return "web-activity-slow-pages";
                case "entry-pages": return "web-activity-entry-pages";
                case "exit-pages": return "web-activity-exit-pages";
                case "transitions": return "web-activity-page-journeys";
                case "search-terms": return "web-activity-search-terms";
                case "technology": return "web-activity-technology";
                default: return "web-activity-pages";
            }
        }
    }
}
