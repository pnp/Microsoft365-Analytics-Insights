using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// Every threshold, band and piece of judgement the SharePoint web-activity page applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of this lives in SQL. The queries return raw distributions - page views per session,
    /// visits per hour, active days per visitor - and the boundaries that turn them into "bounced",
    /// "regular visitor" or "needs attention" are applied here, once, where they can be unit tested.
    /// A threshold written into a <c>CASE</c> expression exists in as many places as it is pasted.
    /// </para>
    /// <para>
    /// The judgements are deliberately written for an intranet owner rather than a DBA: "two in three
    /// visits are a single page" is a content problem, and saying so is the point of the page.
    /// </para>
    /// </remarks>
    public static class WebActivityScoring
    {
        #region Period of day

        /// <summary>
        /// The six parts of the day the Power BI report bucketed by, kept because they are how
        /// intranet owners actually talk about traffic ("nothing happens before the Monday stand-up").
        /// </summary>
        /// <remarks>
        /// Derived from the per-hour counts the queries return rather than bucketed in SQL, so the
        /// same result set drives the heatmap, the popular-hours list and this.
        /// </remarks>
        public static readonly IReadOnlyList<PeriodOfDay> PeriodsOfDay = new[]
        {
            new PeriodOfDay("afterMidnight", "After midnight", 0, 4),
            new PeriodOfDay("earlyMorning", "Early morning", 5, 8),
            new PeriodOfDay("lateMorning", "Late morning", 9, 11),
            new PeriodOfDay("afternoon", "Afternoon", 12, 16),
            new PeriodOfDay("evening", "Evening", 17, 20),
            new PeriodOfDay("lateNight", "Late night", 21, 23),
        };

        /// <summary>The period of the day an hour (0-23) falls in.</summary>
        public static PeriodOfDay PeriodFor(int hour)
        {
            foreach (var period in PeriodsOfDay)
            {
                if (hour >= period.FirstHour && hour <= period.LastHour) return period;
            }

            // Only reachable for an hour outside 0-23, which SQL cannot produce. Bucketing it into
            // "after midnight" would quietly corrupt the distribution, so it is refused instead.
            throw new ArgumentOutOfRangeException(nameof(hour), hour, "Hour of day must be 0-23.");
        }

        /// <summary>Day names for a 0 = Monday ... 6 = Sunday index, matching the SQL day arithmetic.</summary>
        public static readonly IReadOnlyList<string> DayNames =
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        /// <summary>The day name for a 0 = Monday index.</summary>
        public static string DayName(int mondayBasedIndex)
        {
            if (mondayBasedIndex < 0 || mondayBasedIndex >= DayNames.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mondayBasedIndex), mondayBasedIndex, "Day index must be 0 (Monday) to 6 (Sunday).");
            }

            return DayNames[mondayBasedIndex];
        }

        /// <summary>True when a 0 = Monday day index is a Saturday or Sunday.</summary>
        public static bool IsWeekend(int mondayBasedIndex) => mondayBasedIndex >= 5;

        #endregion

        #region Visitor engagement

        /// <summary>
        /// Visitor segments by how many distinct days they visited in the window.
        /// </summary>
        /// <remarks>
        /// Expressed as a share of the working days in the window rather than as fixed day counts, so
        /// the same boundaries mean the same thing over 7 days and over 365. A "daily" visitor on a
        /// 7-day window who had to hit 60 days would be uncategorisable.
        /// </remarks>
        public static double DailyVisitorShare => 0.6;

        /// <summary>Lower bound of the "regular" segment, as a share of working days.</summary>
        public static double RegularVisitorShare => 0.25;

        /// <summary>Lower bound of the "occasional" segment, as a share of working days.</summary>
        public static double OccasionalVisitorShare => 0.08;

        /// <summary>The segment a visitor with <paramref name="activeDays"/> visits falls in.</summary>
        public static string VisitorSegment(int activeDays, int workingDays)
        {
            if (activeDays <= 0) return "None";
            if (activeDays == 1) return "One-off";

            var effectiveWorkingDays = Math.Max(1, workingDays);
            var share = (double)activeDays / effectiveWorkingDays;

            if (share >= DailyVisitorShare) return "Daily";
            if (share >= RegularVisitorShare) return "Regular";
            if (share >= OccasionalVisitorShare) return "Occasional";
            return "Rare";
        }

        /// <summary>Segments in the order they should be displayed - most engaged first.</summary>
        public static readonly IReadOnlyList<string> VisitorSegments =
            new[] { "Daily", "Regular", "Occasional", "Rare", "One-off" };

        #endregion

        #region Bands

        /// <summary>A bounce rate at or above this is a content/landing-page problem worth raising.</summary>
        public const double HighBouncePct = 60;

        /// <summary>A bounce rate at or below this is healthy for an intranet.</summary>
        public const double HealthyBouncePct = 40;

        /// <summary>Reach (visitors as a share of enabled directory users) below this is poor.</summary>
        public const double LowReachPct = 25;

        /// <summary>Reach at or above this is a well-used intranet.</summary>
        public const double GoodReachPct = 60;

        /// <summary>Average page load above this many seconds is slow enough for people to notice.</summary>
        public const double SlowLoadSeconds = 3.0;

        /// <summary>Average page load at or below this many seconds feels instant.</summary>
        public const double FastLoadSeconds = 1.5;

        /// <summary>
        /// Share of visits that ran a search, above which navigation is the suspect rather than
        /// search being popular.
        /// </summary>
        /// <remarks>
        /// Search is a fallback on an intranet. When most visits need it, the information architecture
        /// is not getting people where they are going - that is a navigation finding, not a search one.
        /// </remarks>
        public const double HighSearchReliancePct = 35;

        /// <summary>A page viewed this many times or fewer in the window is a pruning candidate.</summary>
        public const int QuietPageViewCeiling = 3;

        #endregion

        #region Helpers

        /// <summary>A part as a percentage of a whole, returning 0 rather than dividing by zero.</summary>
        public static double Percentage(double part, double whole)
        {
            return whole <= 0 ? 0 : part / whole * 100.0;
        }

        /// <summary>Working days (Mon-Fri) in a half-open UTC date range.</summary>
        public static int WorkingDaysBetween(DateTime fromInclusive, DateTime toExclusive)
        {
            var days = 0;
            for (var day = fromInclusive.Date; day < toExclusive.Date; day = day.AddDays(1))
            {
                var mondayBased = (int)(day - new DateTime(1900, 1, 1)).TotalDays % 7;
                if (!IsWeekend(mondayBased)) days++;
            }

            return days;
        }

        /// <summary>
        /// The share of a total held by the top decile of a distribution, as a percentage.
        /// </summary>
        /// <remarks>
        /// Used for "is this intranet actually a handful of pages?". At least one row always counts as
        /// the top decile, so a ten-page intranet still produces a meaningful answer.
        /// </remarks>
        public static double TopDecileShare(IEnumerable<long> values)
        {
            var ordered = values?.Where(v => v > 0).OrderByDescending(v => v).ToList() ?? new List<long>();
            if (ordered.Count == 0) return 0;

            var take = Math.Max(1, (int)Math.Ceiling(ordered.Count / 10.0));
            var total = ordered.Sum();
            if (total <= 0) return 0;

            return (double)ordered.Take(take).Sum() / total * 100.0;
        }

        #endregion

        #region Devices

        /// <summary>
        /// Device names the App Insights import writes that mean "a phone or tablet".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>dbo.devices.device_name</c> comes from Application Insights' <c>client_Model</c>, which
        /// the JavaScript SDK derives from the user agent. It is free text, not an enumeration: a
        /// desktop browser typically reports <c>Workstation</c> or <c>Laptop</c>, and a phone reports
        /// something model-specific. Matching is therefore a documented heuristic rather than a lookup,
        /// which is exactly why it lives here in C# where it can be unit tested and corrected, instead
        /// of being pasted into a <c>CASE</c> expression in six queries.
        /// </para>
        /// <para>
        /// Unrecognised names are NOT mobile. Guessing the other way would inflate the mobile share
        /// with every unknown device and could talk an intranet team into a responsive-design project
        /// they do not need.
        /// </para>
        /// </remarks>
        private static readonly string[] MobileDeviceTokens =
        {
            "phone", "mobile", "tablet", "ipad", "ipod", "iphone", "android", "pixel",
            "galaxy", "surface duo", "kindle", "nexus",
        };

        /// <summary>True when a device name from the tracker denotes a phone or tablet.</summary>
        public static bool IsMobileDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return false;

            var lowered = deviceName.ToLowerInvariant();
            foreach (var token in MobileDeviceTokens)
            {
                if (lowered.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
            }

            return false;
        }

        #endregion

        #region Distributions

        /// <summary>The visit-depth bands, as an inclusive lower bound and a label.</summary>
        /// <remarks>
        /// "1 page" is its own band rather than being folded into "1-2". It is the bounce definition,
        /// and a band that hid it would make the chart disagree with the headline bounce figure
        /// immediately above it.
        /// </remarks>
        public static readonly IReadOnlyList<Tuple<long, string>> DepthBands = new[]
        {
            Tuple.Create(1L, "1 page"),
            Tuple.Create(2L, "2 pages"),
            Tuple.Create(3L, "3-5 pages"),
            Tuple.Create(6L, "6-10 pages"),
            Tuple.Create(11L, "11-20 pages"),
            Tuple.Create(21L, "21+ pages"),
        };

        /// <summary>The band label for a visit that saw <paramref name="pages"/> pages.</summary>
        public static string DepthBand(long pages)
        {
            var label = DepthBands[0].Item2;
            foreach (var band in DepthBands)
            {
                if (pages >= band.Item1) label = band.Item2;
            }

            return label;
        }

        /// <summary>
        /// A percentile of page load time, interpolated from the fixed-width histogram buckets.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Accurate to the bucket width (a quarter of a second), which is far finer than any decision
        /// this figure informs. The alternative - <c>PERCENTILE_CONT</c> - has no aggregate form and so
        /// sorts every page view in the window, making it the one query on this page that would
        /// reliably time out on a large tenant.
        /// </para>
        /// <para>
        /// The value returned is the UPPER edge of the bucket the percentile falls in, so it never
        /// understates the slow tail. Rounding a p95 down is the failure mode that matters here: it
        /// would tell an admin the site is faster than their users are experiencing.
        /// </para>
        /// </remarks>
        public static double PercentileFromBuckets(
            IEnumerable<KeyValuePair<int, long>> buckets,
            double percentile,
            double bucketWidthSeconds)
        {
            if (buckets == null || bucketWidthSeconds <= 0) return 0;

            var ordered = buckets.Where(b => b.Value > 0).OrderBy(b => b.Key).ToList();
            if (ordered.Count == 0) return 0;

            var total = ordered.Sum(b => b.Value);
            if (total <= 0) return 0;

            var target = total * Math.Min(Math.Max(percentile, 0), 1);
            long running = 0;

            foreach (var bucket in ordered)
            {
                running += bucket.Value;
                if (running >= target) return (bucket.Key + 1) * bucketWidthSeconds;
            }

            return (ordered[ordered.Count - 1].Key + 1) * bucketWidthSeconds;
        }

        /// <summary>The display label for a load-time histogram bucket.</summary>
        public static string LoadBucketLabel(int bucket, double bucketWidthSeconds, int overflowBucket)
        {
            if (bucket >= overflowBucket)
            {
                return string.Format("{0:N0}s+", overflowBucket * bucketWidthSeconds);
            }

            var from = bucket * bucketWidthSeconds;
            return string.Format("{0:N2}-{1:N2}s", from, from + bucketWidthSeconds);
        }

        #endregion

        #region Judgements

        /// <summary>
        /// Reads the headline figures and says, in words an intranet owner can act on, what they mean.
        /// </summary>
        /// <remarks>
        /// Every judgement names the figure it is based on. A verdict that does not show its working is
        /// an opinion, and an admin cannot reasonably be asked to re-platform a landing page on one.
        /// </remarks>
        public static List<WebActivityJudgement> Judgements(WebActivityJudgementInputs inputs)
        {
            var judgements = new List<WebActivityJudgement>();
            if (inputs == null) return judgements;

            if (!inputs.WebTrafficAvailable)
            {
                judgements.Add(new WebActivityJudgement
                {
                    Key = "import-off",
                    Tone = "warning",
                    Headline = "The web-traffic import is switched off",
                    Detail = "Nothing on this page can be measured until the SharePoint page-view tracker is "
                        + "collecting hits. Enable the web traffic import in the installer and deploy the "
                        + "AI Tracker to the sites you want reported on.",
                });

                return judgements;
            }

            if (inputs.PageViews == 0)
            {
                judgements.Add(new WebActivityJudgement
                {
                    Key = "no-traffic",
                    Tone = "warning",
                    Headline = "No page views were recorded in this window",
                    Detail = "The import is switched on but no hits arrived. Either the tracker is not deployed "
                        + "to any site, or the Application Insights resource it writes to is not the one this "
                        + "deployment reads. Check the Service health page.",
                });

                return judgements;
            }

            judgements.Add(ReachJudgement(inputs));
            judgements.Add(BounceJudgement(inputs));

            if (inputs.AverageLoadSeconds > 0) judgements.Add(LoadJudgement(inputs));
            if (inputs.Visits > 0 && inputs.SearchAvailable) judgements.Add(SearchJudgement(inputs));
            if (inputs.UniquePages > 0) judgements.Add(ConcentrationJudgement(inputs));
            if (inputs.Visits > 0) judgements.Add(MobileJudgement(inputs));

            return judgements;
        }

        private static WebActivityJudgement ReachJudgement(WebActivityJudgementInputs inputs)
        {
            if (inputs.KnownUsers <= 0)
            {
                return new WebActivityJudgement
                {
                    Key = "reach",
                    Tone = "neutral",
                    Headline = string.Format("{0:N0} people visited the intranet", inputs.Visitors),
                    Detail = "Reach cannot be expressed as a share of the organisation because no directory "
                        + "users have been imported. Enable the Graph user metadata import to get a denominator.",
                };
            }

            var reach = Percentage(inputs.Visitors, inputs.KnownUsers);
            var tone = reach < LowReachPct ? "critical" : reach < GoodReachPct ? "warning" : "good";

            return new WebActivityJudgement
            {
                Key = "reach",
                Tone = tone,
                Headline = string.Format(
                    "{0:N1}% of the organisation visited the intranet ({1:N0} of {2:N0})",
                    reach, inputs.Visitors, inputs.KnownUsers),
                Detail = reach < LowReachPct
                    ? "Most people never arrive. Before tuning content, check the obvious plumbing: is the "
                        + "intranet the browser home page and the Microsoft 365 app-bar home site, and is the "
                        + "tracker deployed to every site you expect to see here?"
                    : reach < GoodReachPct
                        ? "A substantial minority never arrive. Compare the entry pages with where you actually "
                            + "promote the intranet - the gap is usually a link nobody clicks."
                        : "Reach is healthy. The useful questions now are about depth and speed rather than "
                            + "getting people through the door.",
            };
        }

        private static WebActivityJudgement BounceJudgement(WebActivityJudgementInputs inputs)
        {
            var tone = inputs.BouncePct >= HighBouncePct
                ? "critical"
                : inputs.BouncePct > HealthyBouncePct ? "warning" : "good";

            return new WebActivityJudgement
            {
                Key = "bounce",
                Tone = tone,
                Headline = string.Format(
                    "{0:N1}% of visits were a single page, and the average visit saw {1:N1} pages",
                    inputs.BouncePct, inputs.PagesPerVisit),
                Detail = inputs.BouncePct >= HighBouncePct
                    ? "Most visits end where they start. That is usually one of two things: people arrive by "
                        + "deep link for one document and leave, or the landing page does not lead anywhere. The "
                        + "Journeys tab names the pages it is happening on."
                    : inputs.BouncePct > HealthyBouncePct
                        ? "A meaningful share of visits stop at the first page. Look at the entry pages on the "
                            + "Journeys tab - a high bounce on a news article is normal, on the home page it is not."
                        : "Visitors move through several pages per visit, which is what a working intranet looks "
                            + "like.",
            };
        }

        private static WebActivityJudgement LoadJudgement(WebActivityJudgementInputs inputs)
        {
            var tone = inputs.AverageLoadSeconds >= SlowLoadSeconds
                ? "critical"
                : inputs.AverageLoadSeconds > FastLoadSeconds ? "warning" : "good";

            return new WebActivityJudgement
            {
                Key = "performance",
                Tone = tone,
                Headline = string.Format("Pages took {0:N2}s to load on average", inputs.AverageLoadSeconds),
                Detail = inputs.AverageLoadSeconds >= SlowLoadSeconds
                    ? "That is slow enough for people to feel it and is a common reason an intranet stops being "
                        + "used. The Technology tab ranks the slowest pages and shows whether it is specific to a "
                        + "browser or device - web parts calling a slow API are the usual culprit."
                    : inputs.AverageLoadSeconds > FastLoadSeconds
                        ? "Acceptable, but there is headroom. Check the slowest pages on the Technology tab before "
                            + "the list grows."
                        : "Page load is comfortably fast.",
            };
        }

        private static WebActivityJudgement SearchJudgement(WebActivityJudgementInputs inputs)
        {
            var reliance = Percentage(inputs.SessionsWithSearch, inputs.Visits);
            var tone = reliance >= HighSearchReliancePct ? "warning" : "neutral";

            return new WebActivityJudgement
            {
                Key = "search-reliance",
                Tone = tone,
                Headline = string.Format("{0:N1}% of visits used search", reliance),
                Detail = reliance >= HighSearchReliancePct
                    ? "Search is a fallback, so when most visits need it the navigation is not getting people "
                        + "where they are going. The top search terms are effectively a list of the pages your "
                        + "menu should already be offering."
                    : "Search is being used as a supplement rather than as the primary way around, which is what "
                        + "you want. The top terms are still worth reading as a demand signal.",
            };
        }

        private static WebActivityJudgement ConcentrationJudgement(WebActivityJudgementInputs inputs)
        {
            var tone = inputs.TopDecilePagePct >= 90 ? "warning" : "neutral";

            return new WebActivityJudgement
            {
                Key = "concentration",
                Tone = tone,
                Headline = string.Format(
                    "The busiest 10% of pages took {0:N1}% of all page views, across {1:N0} pages viewed at all",
                    inputs.TopDecilePagePct, inputs.UniquePages),
                Detail = inputs.TopDecilePagePct >= 90
                    ? "Almost all traffic lands on a handful of pages. The rest is either genuinely unwanted - in "
                        + "which case the Pages tab's quiet-page list is your pruning backlog - or is good content "
                        + "nobody can find, which is a navigation problem."
                    : "Traffic is spread across a reasonable share of the site. The quiet-page list on the Pages "
                        + "tab is still the cheapest content-cleanup backlog you will get.",
            };
        }

        private static WebActivityJudgement MobileJudgement(WebActivityJudgementInputs inputs)
        {
            var tone = "neutral";

            return new WebActivityJudgement
            {
                Key = "mobile",
                Tone = tone,
                Headline = string.Format("{0:N1}% of visits came from a mobile device", inputs.MobileVisitPct),
                Detail = inputs.MobileVisitPct >= 25
                    ? "A quarter or more of visits are mobile, so test page layouts on a phone before publishing - "
                        + "wide tables and image-heavy web parts are the usual casualties."
                    : "Mobile is a minority of visits. That is normal for a desk-based workforce, but check it "
                        + "against how many people you expect to be deskless before treating it as a finding.",
            };
        }

        #endregion
    }

    /// <summary>One part of the day, as a closed range of hours.</summary>
    public sealed class PeriodOfDay
    {
        public PeriodOfDay(string key, string label, int firstHour, int lastHour)
        {
            Key = key;
            Label = label;
            FirstHour = firstHour;
            LastHour = lastHour;
        }

        public string Key { get; }
        public string Label { get; }
        public int FirstHour { get; }
        public int LastHour { get; }
    }

    /// <summary>The headline figures <see cref="WebActivityScoring.Judgements"/> reasons over.</summary>
    public sealed class WebActivityJudgementInputs
    {
        public bool WebTrafficAvailable { get; set; }
        public bool SearchAvailable { get; set; }
        public long PageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }
        public int KnownUsers { get; set; }
        public int UniquePages { get; set; }
        public double BouncePct { get; set; }
        public double PagesPerVisit { get; set; }
        public double AverageLoadSeconds { get; set; }
        public long SessionsWithSearch { get; set; }
        public double TopDecilePagePct { get; set; }
        public double MobileVisitPct { get; set; }
    }
}
