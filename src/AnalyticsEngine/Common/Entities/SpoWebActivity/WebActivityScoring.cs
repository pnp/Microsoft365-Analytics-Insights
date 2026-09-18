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
        /// <para>
        /// Expressed as a share of the window's CALENDAR days, not its working days. Active days are
        /// counted over every date a visit happened on, weekends included, so a working-day
        /// denominator would divide one day population by another - a weekend-only visitor could
        /// exceed 100% of "working days".
        /// </para>
        /// <para>
        /// <b>Short windows cannot reach every band</b>, and the UI says so. Over 7 days a visitor
        /// can only have 1-7 active days, so "Occasional" (at least 8% of days) and "Rare" are
        /// unreachable - there is no day count between "one-off" and 2/7 = 29%. That is a property of
        /// the arithmetic rather than a bug, but a chart showing an empty band without explanation
        /// reads as "nobody is occasional" rather than "this period is too short to tell".
        /// </para>
        /// </remarks>
        public static double DailyVisitorShare => 0.6;

        /// <summary>Lower bound of the "regular" segment, as a share of the window's days.</summary>
        public static double RegularVisitorShare => 0.25;

        /// <summary>Lower bound of the "occasional" segment, as a share of the window's days.</summary>
        public static double OccasionalVisitorShare => 0.08;

        /// <summary>
        /// The segment a visitor with <paramref name="activeDays"/> visits falls in.
        /// </summary>
        /// <param name="activeDays">Distinct calendar dates on which the visitor visited.</param>
        /// <param name="windowDays">Calendar days in the reporting window - the same day population.</param>
        public static string VisitorSegment(int activeDays, int windowDays)
        {
            if (activeDays <= 0) return "None";
            if (activeDays == 1) return "One-off";

            var effectiveDays = Math.Max(1, windowDays);
            var share = (double)activeDays / effectiveDays;

            if (share >= DailyVisitorShare) return "Daily";
            if (share >= RegularVisitorShare) return "Regular";
            if (share >= OccasionalVisitorShare) return "Occasional";
            return "Rare";
        }

        /// <summary>
        /// True when <paramref name="windowDays"/> is long enough for every segment to be reachable.
        /// </summary>
        /// <remarks>
        /// "Rare" needs a visitor with at least two active days to fall STRICTLY below
        /// <see cref="OccasionalVisitorShare"/>, so the window must be longer than 2 / 0.08 = 25 days
        /// - at exactly 25 days the ratio equals the threshold and lands in "Occasional" instead.
        /// Below that the mix genuinely cannot distinguish the lower bands, and the UI explains that
        /// rather than presenting an unreachable empty band as a finding.
        /// </remarks>
        public static bool SegmentsFullyReachable(int windowDays) =>
            windowDays > 2 / OccasionalVisitorShare;

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

        /// <summary>
        /// How many distinct pages must have been viewed before a "busiest 10% of pages" figure means
        /// anything.
        /// </summary>
        /// <remarks>
        /// Below ten pages a decile cannot be expressed: the top-decile calculation clamps to at
        /// least one page, so a site with one viewed page reports that its "busiest 10% of pages took
        /// 100% of page views" - a contradiction dressed as a finding. Same reasoning as
        /// <see cref="SegmentsFullyReachable"/>: say nothing rather than say something the data
        /// cannot support.
        /// </remarks>
        public const int MinimumPagesForDecile = 10;

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
        /// <para>
        /// Takes the distribution as (value, how many items have it) pairs rather than one element
        /// per item. That is not a convenience: the caller's SQL deliberately returns page views
        /// grouped into frequency buckets so it never has to send a row per page, and expanding them
        /// back out would allocate one element per page on the intranet - millions on a large tenant,
        /// on the large object heap, and then sort them.
        /// </para>
        /// <para>
        /// At least one item always counts as the top decile, so a ten-page intranet still produces a
        /// meaningful answer.
        /// </para>
        /// </remarks>
        public static double TopDecileShareOfDistribution(IEnumerable<KeyValuePair<long, long>> distribution)
        {
            var ordered = (distribution ?? Enumerable.Empty<KeyValuePair<long, long>>())
                .Where(b => b.Key > 0 && b.Value > 0)
                .OrderByDescending(b => b.Key)
                .ToList();
            if (ordered.Count == 0) return 0;

            var items = ordered.Sum(b => b.Value);
            var total = ordered.Sum(b => b.Key * b.Value);
            if (items <= 0 || total <= 0) return 0;

            var take = Math.Max(1, (long)Math.Ceiling(items / 10.0));
            long taken = 0;
            long takenTotal = 0;

            foreach (var bucket in ordered)
            {
                if (taken >= take) break;

                var fromThisBucket = Math.Min(bucket.Value, take - taken);
                taken += fromThisBucket;
                takenTotal += bucket.Key * fromThisBucket;
            }

            return (double)takenTotal / total * 100.0;
        }

        /// <summary>
        /// The same measure over a flat list of values, for callers that genuinely have one.
        /// </summary>
        /// <remarks>
        /// Groups into a distribution and delegates, so there is only one implementation of the
        /// arithmetic to get wrong.
        /// </remarks>
        public static double TopDecileShare(IEnumerable<long> values)
        {
            var grouped = (values ?? Enumerable.Empty<long>())
                .GroupBy(v => v)
                .Select(g => new KeyValuePair<long, long>(g.Key, g.LongCount()));

            return TopDecileShareOfDistribution(grouped);
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
        /// The histogram bucket a percentile falls in, or null when nothing was measured.
        /// </summary>
        /// <remarks>
        /// Returns the bucket rather than a value so the caller can tell an ordinary result from one
        /// that landed in the overflow bucket - beyond the ceiling the estimate stops being an upper
        /// bound on the real load time, and reporting it as a number would flatter the slow tail
        /// exactly where it matters most.
        /// </remarks>
        public static int? PercentileBucket(IEnumerable<KeyValuePair<int, long>> buckets, double percentile)
        {
            var ordered = buckets?.Where(b => b.Value > 0).OrderBy(b => b.Key).ToList()
                ?? new List<KeyValuePair<int, long>>();
            if (ordered.Count == 0) return null;

            var total = ordered.Sum(b => b.Value);
            if (total <= 0) return null;

            var target = total * Math.Min(Math.Max(percentile, 0), 1);
            long running = 0;

            foreach (var bucket in ordered)
            {
                running += bucket.Value;
                if (running >= target) return bucket.Key;
            }

            return ordered[ordered.Count - 1].Key;
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
        /// The value returned is the UPPER edge of the bucket the percentile falls in, so within the
        /// measured range it never understates the slow tail. Beyond the histogram's ceiling every
        /// load is folded into one overflow bucket and this becomes a floor rather than an estimate -
        /// use <see cref="PercentileBucket"/> to detect that and report it as "at least".
        /// </para>
        /// </remarks>
        public static double PercentileFromBuckets(
            IEnumerable<KeyValuePair<int, long>> buckets,
            double percentile,
            double bucketWidthSeconds)
        {
            if (bucketWidthSeconds <= 0) return 0;

            var bucket = PercentileBucket(buckets, percentile);
            return bucket.HasValue ? (bucket.Value + 1) * bucketWidthSeconds : 0;
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
                // Historical hits are still reported when the toggle is off, so this must not claim
                // nothing can be measured while populated charts sit underneath it, and must not
                // suppress the judgements about the data that IS there.
                var hasData = inputs.PageViews > 0;

                judgements.Add(new WebActivityJudgement
                {
                    Key = "import-off",
                    Tone = "warning",
                    Headline = inputs.ConfigurationReadable
                        ? (hasData
                            ? "The web-traffic import is switched off, so these figures stop at the last collected date"
                            : "The web-traffic import is switched off")
                        : "Application configuration could not be read",
                    Detail = inputs.ConfigurationReadable
                        ? (hasData
                            ? "Everything below was measured from page views already in the database. No new "
                                + "hits are arriving, so the trend will not advance and recent weeks will look "
                                + "emptier than the intranet really is. Enable the web traffic import in the "
                                + "installer to resume collection."
                            : "Nothing on this page can be measured until the SharePoint page-view tracker is "
                                + "collecting hits. Enable the web traffic import in the installer and deploy the "
                                + "AI Tracker to the sites you want reported on.")
                        : "The import toggles cannot be read, so whether the web-traffic import is on is "
                            + "unknown - it is NOT necessarily switched off. Any figures below came from data "
                            + "that is already in the database. Fix the configuration before changing anything "
                            + "in the installer.",
                });

                if (!hasData) return judgements;
            }

            if (inputs.KpisUnavailable)
            {
                // A failed KPI query leaves every figure at its zero default, which is
                // indistinguishable from a genuinely empty window. Diagnosing a tracker from it
                // sends an admin to redeploy working software because an aggregate timed out.
                judgements.Add(new WebActivityJudgement
                {
                    Key = "kpis-unavailable",
                    Tone = "warning",
                    Headline = "The headline figures could not be calculated",
                    Detail = "The query behind them did not complete, so nothing on this tab can be judged. "
                        + "This is usually a timeout on a large window - try a shorter reporting period. The "
                        + "failing query and its error are on the section below.",
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

                    // Deliberately does NOT diagnose the tracker. Page views exist outside this
                    // window often enough that "nothing in the last 28 days" usually means a quiet
                    // period, not a broken deployment, and telling an admin to redeploy on that
                    // evidence is how a working tracker gets pulled apart.
                    Detail = inputs.HasEverCollected
                        ? "Page views have been collected before, so this is a quiet period rather than a "
                            + "broken tracker. Widen the reporting period to see the traffic that does exist."
                        : "No page view has ever been recorded, so the tracker is probably not deployed to any "
                            + "site, or the Application Insights resource it writes to is not the one this "
                            + "deployment reads. Check the Service health page.",
                });

                return judgements;
            }

            judgements.Add(ReachJudgement(inputs));

            // Guarded like the search judgement below it: hits carry a nullable session_id, so a
            // window can hold page views and no visits at all. Bounce rate and pages-per-visit are
            // both zero there, which lands on the "good" band and tells an admin "most visits go
            // beyond the first page" about visits that were never recorded.
            if (inputs.Visits > 0) judgements.Add(BounceJudgement(inputs));

            if (inputs.AverageLoadSeconds.HasValue) judgements.Add(LoadJudgement(inputs));
            if (inputs.Visits > 0 && inputs.SearchAvailable) judgements.Add(SearchJudgement(inputs));
            if (inputs.UniquePages >= MinimumPagesForDecile) judgements.Add(ConcentrationJudgement(inputs));
            if (inputs.MobilePageViewPct.HasValue) judgements.Add(MobileJudgement(inputs));

            return judgements;
        }

        private static WebActivityJudgement ReachJudgement(WebActivityJudgementInputs inputs)
        {
            // Reach needs a denominator that means "people who could have used this intranet". Without
            // the directory import, dbo.users is just the people an importer has already seen, so the
            // ratio would be close to 100% by construction.
            if (!inputs.DirectoryImported || inputs.KnownUsers <= 0)
            {
                return new WebActivityJudgement
                {
                    Key = "reach",
                    Tone = "neutral",
                    Headline = string.Format("{0:N0} people visited the intranet", inputs.Visitors),

                    // The two causes need different advice. Telling an admin whose import is ON to
                    // switch it on sends them to a toggle that is already correct and hides the real
                    // problem, which is that the import ran and produced nothing.
                    Detail = inputs.DirectoryImported
                        ? "There is no population to express that as a share of: the Graph user metadata "
                            + "import is switched on but no users are on record, so it has not completed "
                            + "successfully yet. Check the user metadata import on the Service health page "
                            + "rather than the installer."
                        : "There is no population to express that as a share of: the Graph user metadata "
                            + "import is off, so the only users on record are the ones an importer has already "
                            + "seen. Enable it to get a denominator.",
                };
            }

            var reach = Percentage(inputs.EnabledVisitors, inputs.KnownUsers);
            var tone = reach < LowReachPct ? "critical" : reach < GoodReachPct ? "warning" : "good";

            return new WebActivityJudgement
            {
                Key = "reach",
                Tone = tone,
                Headline = string.Format(
                    "{0:N1}% of enabled directory users had a tracked visit ({1:N0} of {2:N0})",
                    reach, inputs.EnabledVisitors, inputs.KnownUsers),
                Detail = reach < LowReachPct
                    ? "Check the measurement before the content. The denominator is every enabled directory "
                        + "user - guests, shared mailboxes and service accounts included - and the numerator "
                        + "only counts sites the tracker is deployed to, so both ends can be wrong. Once you "
                        + "trust the figure, the usual levers are the browser home page and the Microsoft 365 "
                        + "app-bar home site."
                    : reach < GoodReachPct
                        ? "A substantial minority have no tracked visit. Some of that is the denominator "
                            + "(guests and service accounts) and some is sites the tracker is not on; the rest "
                            + "is worth comparing against where you actually promote the intranet."
                        : "Most of the directory shows up here, so the useful questions now are about depth "
                            + "and speed rather than getting people through the door.",
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
                    ? "Judge this per page rather than in aggregate. A deep link straight to one policy "
                        + "document is a perfectly good one-page visit, and on an intranet those are common; "
                        + "the same rate on the home page is not. The Journeys tab names the pages it is "
                        + "happening on."
                    : inputs.BouncePct > HealthyBouncePct
                        ? "A meaningful share of visits stop at the first page. Look at which pages on the "
                            + "Journeys tab before drawing a conclusion - the answer differs by page."
                        : "Most visits record more than one page view. That is not quite the same as "
                            + "navigating: a refresh or a tracker firing twice counts as a second view, so "
                            + "check the Journeys tab for real page-to-page steps, which exclude "
                            + "same-page moves.",
            };
        }

        private static WebActivityJudgement LoadJudgement(WebActivityJudgementInputs inputs)
        {
            var average = inputs.AverageLoadSeconds ?? 0;
            var tone = average >= SlowLoadSeconds
                ? "critical"
                : average > FastLoadSeconds ? "warning" : "good";

            // A fast mean with a slow tail is the case this page exists to catch, so the tail can
            // raise the tone on its own. "1.2s on average" next to a 12-second p95 is not a fast site;
            // it is a fast site for most people and an unusable one for a minority.
            var tail = inputs.P95LoadSeconds;
            var tailIsBad = tail.HasValue && tail.Value >= SlowLoadSeconds * 2;
            if (tailIsBad && tone == "good") tone = "warning";

            var headline = tail.HasValue
                ? string.Format(
                    "Pages took {0:N2}s to load on average, and one view in twenty took {1:N2}s or more",
                    average, tail.Value)
                : string.Format("Pages took {0:N2}s to load on average", average);

            return new WebActivityJudgement
            {
                Key = "performance",
                Tone = tone,
                Headline = headline,
                Detail = average >= SlowLoadSeconds
                    ? "That is slow enough for people to feel it. The Page views tab ranks the slowest "
                        + "pages; the Technology tab shows whether it is specific to a browser or device, "
                        + "which is the first thing to rule out."
                    : tailIsBad
                        ? "The average is fine but the slow tail is not, and the tail is what a complaining "
                            + "minority is describing. Start from the slowest pages on the Page views tab."
                        : average > FastLoadSeconds
                            ? "Acceptable, with headroom. Check the slowest pages on the Page views tab before "
                                + "the list grows."
                            : tail.HasValue
                                // Only claim the tail is fine when the tail was actually measured. The
                                // percentile comes from its own query, which can fail while this one
                                // succeeds - and "comfortably fast in the slow tail" is exactly the
                                // reassurance a reader should not be given about a figure nobody read.
                                ? "Page load is comfortably fast, at the average and in the slow tail."
                                : "Page load is comfortably fast on average. The slow tail could not be "
                                    + "measured for this period, so check the Technology tab before "
                                    + "concluding it is fast for everyone.",
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
                    ? "More than a third of visits reach for search. That can mean the navigation is not "
                        + "getting people where they are going, or simply that the intranet is large and "
                        + "people know what they want. Either way the top terms are a list of what the menu "
                        + "could be offering."
                    : "Search is being used as a supplement rather than as the primary way around. The top "
                        + "terms are still worth reading as a demand signal.",
            };
        }

        private static WebActivityJudgement ConcentrationJudgement(WebActivityJudgementInputs inputs)
        {
            var tone = "neutral";

            return new WebActivityJudgement
            {
                Key = "concentration",
                Tone = tone,
                Headline = string.Format(
                    "The busiest 10% of pages took {0:N1}% of all page views, across {1:N0} pages viewed at all",
                    inputs.TopDecilePagePct, inputs.UniquePages),
                Detail = inputs.TopDecilePagePct >= 90
                    ? "Traffic is heavily concentrated, which is normal for an intranet with a strong home "
                        + "page and news feed - it is not on its own evidence that the rest should be "
                        + "retired. The Page views tab's quiet-page list is where to look, one page at a time."
                    : "Traffic is spread across a reasonable share of the site. The quiet-page list on the "
                        + "Page views tab is still the cheapest place to start a content review.",
            };
        }

        private static WebActivityJudgement MobileJudgement(WebActivityJudgementInputs inputs)
        {
            var share = inputs.MobilePageViewPct ?? 0;

            return new WebActivityJudgement
            {
                Key = "mobile",
                Tone = "neutral",
                Headline = string.Format(
                    "{0:N1}% of page views with a known device came from a phone or tablet", share),
                Detail = share >= 25
                    ? "A quarter or more of measured page views are mobile, so test page layouts on a phone "
                        + "before publishing - wide tables and image-heavy web parts are the usual casualties. "
                        + "Mobile visits are typically shorter than desktop ones, so the share of VISITS is "
                        + "likely higher than this."
                    : "Mobile is a minority of measured page views. That is normal for a desk-based "
                        + "workforce; check it against how many people you expect to be deskless before "
                        + "treating it as a finding. Mobile visits tend to be shorter, so the share of VISITS "
                        + "is likely higher than this.",
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

        /// <summary>
        /// True when the headline figures could not be calculated, so every number below is a zero
        /// default rather than a measurement.
        /// </summary>
        public bool KpisUnavailable { get; set; }

        /// <summary>
        /// True when page views exist somewhere in the database, whatever this window holds. Tells an
        /// empty window apart from a tracker that has never collected anything.
        /// </summary>
        public bool HasEverCollected { get; set; }

        /// <summary>
        /// False when configuration could not be read, so every toggle reads as off by default.
        /// </summary>
        public bool ConfigurationReadable { get; set; } = true;

        /// <summary>True when the directory is a real population rather than "users we have seen".</summary>
        public bool DirectoryImported { get; set; }

        public long PageViews { get; set; }
        public long Visits { get; set; }
        public int Visitors { get; set; }

        /// <summary>Visitors who are in the enabled directory - the numerator of reach.</summary>
        public int EnabledVisitors { get; set; }

        public int KnownUsers { get; set; }
        public int UniquePages { get; set; }
        public double BouncePct { get; set; }
        public double PagesPerVisit { get; set; }

        /// <summary>Mean page load seconds, or null when none was reported.</summary>
        public double? AverageLoadSeconds { get; set; }

        /// <summary>95th-percentile page load seconds, or null when none was reported.</summary>
        public double? P95LoadSeconds { get; set; }

        public long SessionsWithSearch { get; set; }
        public double TopDecilePagePct { get; set; }

        /// <summary>Mobile share of page views with a known device, or null when none had one.</summary>
        public double? MobilePageViewPct { get; set; }
    }
}
