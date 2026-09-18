using Common.Entities.SpoWebActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The window, the thresholds and the judgements behind the SharePoint web activity page.
    /// </summary>
    /// <remarks>
    /// All of this is pure C# precisely so it can be tested here. The SQL returns raw distributions;
    /// every boundary that turns one into "bounced", "regular visitor" or "needs attention" lives in
    /// <see cref="WebActivityScoring"/>, where changing it is a visible, reviewable act rather than an
    /// edit to one of six <c>CASE</c> expressions.
    /// </remarks>
    [TestClass]
    [TestCategory("WebActivity")]
    public class WebActivityScoringTests
    {
        #region Query window

        [TestMethod]
        public void Window_SnapsToAnOfferedRangeAndBreaksTiesDownwards()
        {
            foreach (var days in WebActivityQuery.AllowedWindowDays)
            {
                Assert.AreEqual(days, WebActivityQuery.SnapDays(days));
            }

            Assert.AreEqual(WebActivityQuery.DefaultWindowDays, WebActivityQuery.SnapDays(0));
            Assert.AreEqual(WebActivityQuery.DefaultWindowDays, WebActivityQuery.SnapDays(-99));
            Assert.AreEqual(7, WebActivityQuery.SnapDays(1));
            Assert.AreEqual(365, WebActivityQuery.SnapDays(10_000));

            // A tie must cost less, not more. 17.5 is the midpoint of 7 and 28; an ambiguous request
            // that snapped UP would let a hand-edited URL scan four times as much of dbo.hits as the
            // UI ever asks for.
            Assert.AreEqual(7, WebActivityQuery.SnapDays(17));
        }

        [TestMethod]
        public void Window_IsHalfOpenAndEndsAtTheEndOfToday()
        {
            var now = new DateTime(2026, 3, 18, 14, 30, 0, DateTimeKind.Utc);
            var query = WebActivityQuery.Create(28, now);

            Assert.AreEqual(28, query.Days);
            Assert.AreEqual(new DateTime(2026, 3, 19), query.ToExclusiveUtc);
            Assert.AreEqual(new DateTime(2026, 3, 18), query.ToInclusiveUtc);
            Assert.AreEqual(new DateTime(2026, 2, 19), query.FromUtc);
            Assert.AreEqual(28, (query.ToExclusiveUtc - query.FromUtc).Days);

            // The midpoint splits the window in half, which is what the new-versus-returning measure
            // compares. An off-by-one here would silently bias every "new visitor" count.
            Assert.AreEqual(query.FromUtc.AddDays(14), query.MidpointUtc);
        }

        [TestMethod]
        public void Window_MondayBucketingMatchesTheSqlDayArithmetic()
        {
            // 1900-01-01 was a Monday; the SQL buckets with DATEDIFF(DAY, 0, col) % 7 and the C#
            // spine must agree or the trend chart's first bucket lands on the wrong week.
            Assert.AreEqual(DayOfWeek.Monday, new DateTime(1900, 1, 1).DayOfWeek);

            for (int offset = 0; offset < 14; offset++)
            {
                var day = new DateTime(2026, 3, 1).AddDays(offset);
                var monday = WebActivityQuery.MondayOf(day);
                Assert.AreEqual(DayOfWeek.Monday, monday.DayOfWeek);
                Assert.IsTrue(monday <= day && (day - monday).Days < 7);
            }
        }

        [TestMethod]
        public void CacheKey_CarriesEveryInputAndTheDateButNotTheTime()
        {
            var morning = WebActivityQuery.Create(28, new DateTime(2026, 3, 18, 8, 0, 0, DateTimeKind.Utc));
            var evening = WebActivityQuery.Create(28, new DateTime(2026, 3, 18, 20, 0, 0, DateTimeKind.Utc));
            var tomorrow = WebActivityQuery.Create(28, new DateTime(2026, 3, 19, 8, 0, 0, DateTimeKind.Utc));

            Assert.AreEqual(morning.CacheKey("overview"), evening.CacheKey("overview"));
            Assert.AreNotEqual(morning.CacheKey("overview"), tomorrow.CacheKey("overview"),
                "A cached section must never be served after the window has rolled over to a new day.");
            Assert.AreNotEqual(morning.CacheKey("overview"), morning.CacheKey("visits"));
            Assert.AreNotEqual(
                WebActivityQuery.Create(28, morning.NowUtc, 15).CacheKey("pages"),
                WebActivityQuery.Create(28, morning.NowUtc, 50).CacheKey("pages"),
                "A different row count is a different payload.");
        }

        [TestMethod]
        public void Window_ClampsRowCountsRatherThanRejectingThem()
        {
            Assert.AreEqual(WebActivityQuery.DefaultTop, WebActivityQuery.NormaliseTop(0));
            Assert.AreEqual(1, WebActivityQuery.NormaliseTop(1));
            Assert.AreEqual(WebActivityQuery.MaximumTop, WebActivityQuery.NormaliseTop(100_000));
            Assert.AreEqual(WebActivityQuery.DefaultMinimumViews, WebActivityQuery.NormaliseMinimumViews(0));
            Assert.AreEqual(1000, WebActivityQuery.NormaliseMinimumViews(999_999));
        }

        #endregion

        #region Periods and days

        [TestMethod]
        public void PeriodsOfDay_CoverEveryHourExactlyOnce()
        {
            var covered = new List<int>();
            foreach (var period in WebActivityScoring.PeriodsOfDay)
            {
                for (int hour = period.FirstHour; hour <= period.LastHour; hour++) covered.Add(hour);
            }

            CollectionAssert.AreEqual(Enumerable.Range(0, 24).ToArray(), covered.OrderBy(h => h).ToArray());
            Assert.AreEqual(24, covered.Distinct().Count(), "Overlapping periods would double-count traffic.");

            for (int hour = 0; hour < 24; hour++) Assert.IsNotNull(WebActivityScoring.PeriodFor(hour));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => WebActivityScoring.PeriodFor(24));
        }

        [TestMethod]
        public void DayNames_AreMondayFirstToMatchTheSqlDayIndex()
        {
            Assert.AreEqual("Monday", WebActivityScoring.DayName(0));
            Assert.AreEqual("Sunday", WebActivityScoring.DayName(6));
            Assert.IsFalse(WebActivityScoring.IsWeekend(4));
            Assert.IsTrue(WebActivityScoring.IsWeekend(5));
            Assert.IsTrue(WebActivityScoring.IsWeekend(6));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => WebActivityScoring.DayName(7));
        }

        [TestMethod]
        public void WorkingDays_CountWeekdaysInAHalfOpenRange()
        {
            // Monday 2 March 2026 to Sunday 8 March inclusive = five working days.
            var from = new DateTime(2026, 3, 2);
            Assert.AreEqual(5, WebActivityScoring.WorkingDaysBetween(from, from.AddDays(7)));
            Assert.AreEqual(0, WebActivityScoring.WorkingDaysBetween(from, from));
            Assert.AreEqual(20, WebActivityScoring.WorkingDaysBetween(from, from.AddDays(28)));
        }

        #endregion

        #region Visitor segments and depth

        [TestMethod]
        public void VisitorSegments_AreAShareOfTheWindowsOwnDays()
        {
            // Active days are counted over CALENDAR dates, so the denominator has to be calendar days
            // too. Dividing weekend-inclusive active days by weekday-only working days compares two
            // different day populations, and a weekend-only visitor could exceed 100% of it.
            Assert.AreEqual("Daily", WebActivityScoring.VisitorSegment(20, 28));
            Assert.AreEqual("Daily", WebActivityScoring.VisitorSegment(260, 365));

            Assert.AreEqual("None", WebActivityScoring.VisitorSegment(0, 28));
            Assert.AreEqual("One-off", WebActivityScoring.VisitorSegment(1, 28));
            Assert.AreEqual("Rare", WebActivityScoring.VisitorSegment(2, 28));
            Assert.AreEqual("Occasional", WebActivityScoring.VisitorSegment(4, 28));
            Assert.AreEqual("Regular", WebActivityScoring.VisitorSegment(10, 28));

            // A single visit is "One-off" whatever the window length: over a week it would otherwise
            // be 14% of the period and get called "Occasional", which is a claim about a habit that
            // one visit cannot support.
            Assert.AreEqual("One-off", WebActivityScoring.VisitorSegment(1, 7));

            // Never divides by zero.
            Assert.AreEqual("Daily", WebActivityScoring.VisitorSegment(2, 0));
        }

        [TestMethod]
        public void VisitorSegments_AdmitWhenTheWindowIsTooShortToSeparateThem()
        {
            // Over 7 days a visitor has 1-7 active days, so there is no count between "one-off" and
            // 2/7 = 29%: the Occasional and Rare bands cannot be reached at all. An empty band means
            // "this period cannot tell", and the page has to say which.
            var shortWindow = Enumerable.Range(1, 7)
                .Select(d => WebActivityScoring.VisitorSegment(d, 7))
                .Distinct()
                .ToList();
            CollectionAssert.DoesNotContain(shortWindow, "Occasional");
            CollectionAssert.DoesNotContain(shortWindow, "Rare");
            Assert.IsFalse(WebActivityScoring.SegmentsFullyReachable(7));

            // The boundary is STRICT: at exactly 25 days, 2/25 equals the Occasional threshold and
            // lands in that band, so Rare is still unreachable. 26 is the first window where the
            // helper's promise actually holds.
            Assert.IsFalse(WebActivityScoring.SegmentsFullyReachable(25));
            Assert.IsTrue(WebActivityScoring.SegmentsFullyReachable(26));
            Assert.AreEqual("Occasional", WebActivityScoring.VisitorSegment(2, 25));
            Assert.AreEqual("Rare", WebActivityScoring.VisitorSegment(2, 26));

            // Every window the UI actually offers, answered correctly.
            foreach (var days in WebActivityQuery.AllowedWindowDays)
            {
                var reachable = Enumerable.Range(1, days)
                    .Select(d => WebActivityScoring.VisitorSegment(d, days))
                    .Distinct()
                    .ToList();
                var everyBand = WebActivityScoring.VisitorSegments.All(s => reachable.Contains(s));
                Assert.AreEqual(everyBand, WebActivityScoring.SegmentsFullyReachable(days), days.ToString());
            }
        }

        [TestMethod]
        public void DepthBands_KeepSinglePageVisitsSeparate()
        {
            // The one-page band IS the bounce definition. Folding it into "1-2 pages" would make the
            // chart disagree with the bounce rate printed directly above it.
            Assert.AreEqual("1 page", WebActivityScoring.DepthBand(1));
            Assert.AreEqual("2 pages", WebActivityScoring.DepthBand(2));
            Assert.AreEqual("3-5 pages", WebActivityScoring.DepthBand(4));
            Assert.AreEqual("6-10 pages", WebActivityScoring.DepthBand(10));
            Assert.AreEqual("11-20 pages", WebActivityScoring.DepthBand(11));
            Assert.AreEqual("21+ pages", WebActivityScoring.DepthBand(500));

            // Bands must be contiguous and ordered, or a visit depth falls into a gap.
            var bounds = WebActivityScoring.DepthBands.Select(b => b.Item1).ToList();
            CollectionAssert.AreEqual(bounds.OrderBy(b => b).ToArray(), bounds.ToArray());
            Assert.AreEqual(bounds.Count, bounds.Distinct().Count());
        }

        #endregion

        #region Devices and percentiles

        [TestMethod]
        public void MobileClassification_MatchesTheShapesTheImportProduces()
        {
            foreach (var mobile in new[] { "iPhone", "iPad", "Android Phone", "Samsung Galaxy S24", "Pixel 9", "Windows Phone" })
            {
                Assert.IsTrue(WebActivityScoring.IsMobileDevice(mobile), mobile);
            }

            foreach (var desktop in new[] { "Workstation", "Laptop", "Other", "", null })
            {
                Assert.IsFalse(WebActivityScoring.IsMobileDevice(desktop), desktop ?? "(null)");
            }

            // An unrecognised device is NOT mobile. Guessing the other way would inflate the mobile
            // share with every unknown device and could fund a responsive-design project on noise.
            Assert.IsFalse(WebActivityScoring.IsMobileDevice("Contoso Kiosk 7"));
        }

        [TestMethod]
        public void PercentileFromBuckets_RoundsUpAndNeverUnderstatesTheSlowTail()
        {
            // 100 page views: 95 in the 0.00-0.25s bucket, 5 in the 7.50-7.75s bucket.
            var buckets = new[]
            {
                new KeyValuePair<int, long>(0, 95),
                new KeyValuePair<int, long>(30, 5),
            };

            // The p95 sits exactly on the boundary; rounding DOWN would report a quarter of a second
            // and tell an admin the site is fast for everyone, which is the opposite of the truth.
            Assert.AreEqual(0.25, WebActivityScoring.PercentileFromBuckets(buckets, 0.95, 0.25), 1e-9);
            Assert.AreEqual(7.75, WebActivityScoring.PercentileFromBuckets(buckets, 0.99, 0.25), 1e-9);
            Assert.AreEqual(0.25, WebActivityScoring.PercentileFromBuckets(buckets, 0.5, 0.25), 1e-9);

            Assert.AreEqual(0, WebActivityScoring.PercentileFromBuckets(null, 0.95, 0.25), 1e-9);
            Assert.AreEqual(0, WebActivityScoring.PercentileFromBuckets(
                new KeyValuePair<int, long>[0], 0.95, 0.25), 1e-9);
        }

        [TestMethod]
        public void PercentileBucket_DistinguishesNoDataFromTheOverflowBucket()
        {
            Assert.IsNull(WebActivityScoring.PercentileBucket(null, 0.95));
            Assert.IsNull(WebActivityScoring.PercentileBucket(new KeyValuePair<int, long>[0], 0.95));

            // Everything slower than the ceiling shares one bucket, so beyond it the "upper edge" is
            // a floor rather than an estimate - the real p95 could be minutes. The caller has to be
            // able to tell, or the page reports a precise-looking number for an unbounded tail.
            var overflowing = new[]
            {
                new KeyValuePair<int, long>(0, 90),
                new KeyValuePair<int, long>(WebActivitySql.LoadOverflowBucket, 10),
            };

            Assert.AreEqual(WebActivitySql.LoadOverflowBucket,
                WebActivityScoring.PercentileBucket(overflowing, 0.95));
            Assert.AreEqual(0, WebActivityScoring.PercentileBucket(overflowing, 0.5));
        }

        [TestMethod]
        public void TopDecileShare_HandlesSmallSitesAndIsAShareOfTheWhole()
        {
            // Ten pages, one of which took half the traffic: the top decile is one page.
            var values = new long[] { 100, 20, 20, 20, 20, 20, 20, 20, 20, 20 };
            Assert.AreEqual(35.7, Math.Round(WebActivityScoring.TopDecileShare(values), 1));

            // At least one row always counts as the top decile, so a three-page intranet still gets
            // a meaningful answer rather than a division by zero.
            Assert.AreEqual(50.0, WebActivityScoring.TopDecileShare(new long[] { 10, 5, 5 }), 1e-9);
            Assert.AreEqual(0, WebActivityScoring.TopDecileShare(new long[0]), 1e-9);
            Assert.AreEqual(0, WebActivityScoring.TopDecileShare((IEnumerable<long>)null), 1e-9);
        }

        [TestMethod]
        public void TopDecileShare_OverADistributionMatchesTheExpandedForm()
        {
            // The SQL returns (views, how many pages had that many views) precisely so it never has
            // to send a row per page - an intranet has millions. Expanding it back out to compute
            // this would allocate one element per page on the large object heap and then sort it, so
            // the distribution form is the real implementation and must agree with the flat one.
            var flat = new long[] { 100, 100, 50, 50, 50, 10, 10, 10, 10, 10, 1, 1 };
            var distribution = new[]
            {
                new KeyValuePair<long, long>(100, 2),
                new KeyValuePair<long, long>(50, 3),
                new KeyValuePair<long, long>(10, 5),
                new KeyValuePair<long, long>(1, 2),
            };

            Assert.AreEqual(
                WebActivityScoring.TopDecileShare(flat),
                WebActivityScoring.TopDecileShareOfDistribution(distribution),
                1e-9);

            // A bucket can be split by the decile boundary: 20 pages means the top decile is 2, both
            // of which come out of a bucket holding 5.
            var split = new[] { new KeyValuePair<long, long>(10, 5), new KeyValuePair<long, long>(1, 15) };
            Assert.AreEqual(20.0 / 65.0 * 100, WebActivityScoring.TopDecileShareOfDistribution(split), 1e-9);

            Assert.AreEqual(0, WebActivityScoring.TopDecileShareOfDistribution(null), 1e-9);
        }

        [TestMethod]
        public void Percentage_NeverDividesByZero()
        {
            Assert.AreEqual(0, WebActivityScoring.Percentage(5, 0), 1e-9);
            Assert.AreEqual(50, WebActivityScoring.Percentage(1, 2), 1e-9);
        }

        #endregion

        #region Judgements

        private static WebActivityJudgementInputs Healthy() => new WebActivityJudgementInputs
        {
            WebTrafficAvailable = true,
            SearchAvailable = true,
            DirectoryImported = true,
            PageViews = 50_000,
            Visits = 12_000,
            Visitors = 1_500,
            EnabledVisitors = 1_500,
            KnownUsers = 2_000,
            UniquePages = 900,
            BouncePct = 30,
            PagesPerVisit = 4.2,
            AverageLoadSeconds = 1.1,
            SessionsWithSearch = 1_200,
            TopDecilePagePct = 70,
            MobilePageViewPct = 12,
        };

        [TestMethod]
        public void Judgements_StopAtTheFirstThingThatMakesEverythingElseMeaningless()
        {
            var off = Healthy();
            off.WebTrafficAvailable = false;
            off.PageViews = 0;
            var result = WebActivityScoring.Judgements(off);
            Assert.AreEqual(1, result.Count, "Nothing else can be said when nothing is being collected.");
            Assert.AreEqual("import-off", result[0].Key);
            StringAssert.Contains(result[0].Headline, "switched off");

            // But an import switched off AFTER data was collected does not make that data
            // meaningless - the queries still return it, and the page still renders it. Suppressing
            // the rest of the judgements there left an admin looking at populated charts with no
            // interpretation of them at all.
            var offWithHistory = Healthy();
            offWithHistory.WebTrafficAvailable = false;
            var stillJudged = WebActivityScoring.Judgements(offWithHistory);
            Assert.IsTrue(stillJudged.Count > 1, "Collected data is still worth judging.");
            Assert.AreEqual("import-off", stillJudged[0].Key, "The stopped import is still the headline.");
            StringAssert.Contains(stillJudged[0].Headline, "stop at the last collected date");

            // Same false toggle, different cause: when configuration could not be READ, every flag
            // defaults to false, and asserting the import is switched off sends an admin to the
            // installer instead of to the configuration that will not load.
            var unreadable = Healthy();
            unreadable.WebTrafficAvailable = false;
            unreadable.ConfigurationReadable = false;
            unreadable.PageViews = 0;
            var unreadableResult = WebActivityScoring.Judgements(unreadable);
            Assert.AreEqual(1, unreadableResult.Count);
            Assert.AreEqual("import-off", unreadableResult[0].Key);
            StringAssert.Contains(unreadableResult[0].Headline, "could not be read");
            Assert.IsFalse(unreadableResult[0].Headline.Contains("switched off"));

            // ...and with data present it behaves like the switched-off case: the unreadable
            // configuration leads, but the figures on screen are still interpreted. Its detail text
            // already promises "any figures below came from data that is already in the database",
            // so suppressing the judgements about those figures contradicted it.
            var unreadableWithData = Healthy();
            unreadableWithData.WebTrafficAvailable = false;
            unreadableWithData.ConfigurationReadable = false;
            var unreadableJudged = WebActivityScoring.Judgements(unreadableWithData);
            Assert.IsTrue(unreadableJudged.Count > 1);
            Assert.AreEqual("import-off", unreadableJudged[0].Key);
            StringAssert.Contains(unreadableJudged[0].Headline, "could not be read");

            var empty = Healthy();
            empty.PageViews = 0;
            result = WebActivityScoring.Judgements(empty);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("no-traffic", result[0].Key);

            Assert.AreEqual(0, WebActivityScoring.Judgements(null).Count);
        }

        [TestMethod]
        public void Judgements_ToneMatchesTheBandTheFigureIsIn()
        {
            var healthy = WebActivityScoring.Judgements(Healthy()).ToDictionary(j => j.Key);
            Assert.AreEqual("good", healthy["reach"].Tone);
            Assert.AreEqual("good", healthy["bounce"].Tone);
            Assert.AreEqual("good", healthy["performance"].Tone);

            var struggling = Healthy();
            struggling.Visitors = 200;
            struggling.EnabledVisitors = 200;
            struggling.BouncePct = 72;
            struggling.AverageLoadSeconds = 4.5;
            struggling.SessionsWithSearch = 9_000;
            struggling.TopDecilePagePct = 95;

            var bad = WebActivityScoring.Judgements(struggling).ToDictionary(j => j.Key);
            Assert.AreEqual("critical", bad["reach"].Tone);
            Assert.AreEqual("critical", bad["bounce"].Tone);
            Assert.AreEqual("critical", bad["performance"].Tone);
            Assert.AreEqual("warning", bad["search-reliance"].Tone);

            // Concentration is never worse than neutral. Traffic concentrated on a home page and a
            // news feed is what a normal intranet looks like, and colouring it as a fault next to
            // genuinely broken figures invites someone to retire content on no evidence.
            Assert.AreEqual("neutral", bad["concentration"].Tone);

            foreach (var judgement in bad.Values)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(judgement.Headline), judgement.Key);
                Assert.IsFalse(string.IsNullOrWhiteSpace(judgement.Detail), judgement.Key);
            }
        }

        [TestMethod]
        public void Judgements_DoNotCallASiteFastWhenOnlyTheMeanIsFast()
        {
            // A 1.1s average next to a 12s 95th percentile is not a fast intranet: it is a fast one
            // for most people and an unusable one for a minority, and the minority is who complains.
            // Grading on the mean alone would answer that complaint with "performance is good".
            var slowTail = Healthy();
            slowTail.AverageLoadSeconds = 1.1;
            slowTail.P95LoadSeconds = 12;

            var judgement = WebActivityScoring.Judgements(slowTail).Single(j => j.Key == "performance");
            Assert.AreEqual("warning", judgement.Tone);
            StringAssert.Contains(judgement.Headline, "one view in twenty");

            // With no tail reported the headline must not invent one - and, more importantly, the
            // detail must not reassure the reader that the tail is fine. The percentile comes from
            // its own query, which can fail while the average's query succeeds.
            var meanOnly = Healthy();
            meanOnly.P95LoadSeconds = null;
            var plain = WebActivityScoring.Judgements(meanOnly).Single(j => j.Key == "performance");
            Assert.AreEqual("good", plain.Tone);
            Assert.IsFalse(plain.Headline.Contains("one view in twenty"));
            StringAssert.Contains(plain.Detail, "could not be measured");
        }

        [TestMethod]
        public void Judgements_OmitPerformanceAndMobileWhenNothingWasMeasured()
        {
            var unmeasured = Healthy();
            unmeasured.AverageLoadSeconds = null;
            unmeasured.MobilePageViewPct = null;

            var keys = WebActivityScoring.Judgements(unmeasured).Select(j => j.Key).ToList();

            // "0.00s average load" and "0.0% mobile" are both readable as findings. Absent telemetry
            // must produce no judgement at all rather than a flattering one.
            CollectionAssert.DoesNotContain(keys, "performance");
            CollectionAssert.DoesNotContain(keys, "mobile");
        }
        [TestMethod]
        public void Judgements_SayWhenReachHasNoDenominatorRatherThanReportingZeroPercent()
        {
            foreach (var noDirectory in new[] { Healthy(), Healthy() }.Select((i, n) =>
            {
                // Two ways to have no usable denominator: an empty directory, and a directory that
                // is only the users an importer has already seen. The second is the dangerous one -
                // KnownUsers is positive, so a naive check would happily print a circular ~100%.
                if (n == 0) i.KnownUsers = 0; else i.DirectoryImported = false;
                return i;
            }))
            {
                var reach = WebActivityScoring.Judgements(noDirectory).Single(j => j.Key == "reach");
                Assert.AreEqual("neutral", reach.Tone, "An unknown denominator is not a bad reach figure.");
                StringAssert.Contains(reach.Detail, "user metadata");
                Assert.IsFalse(reach.Headline.Contains("%"), "Never print a percentage with no denominator.");
            }
        }

        [TestMethod]
        public void Judgements_OmitSearchWhenNoSearchesWereRecorded()
        {
            var noSearch = Healthy();
            noSearch.SearchAvailable = false;

            Assert.IsFalse(WebActivityScoring.Judgements(noSearch).Any(j => j.Key == "search-reliance"),
                "A search verdict with no searches behind it is an assertion about nothing.");
        }

        [TestMethod]
        public void ConcentrationJudgement_IsSuppressedUntilADecileCanExist()
        {
            // A single page is more than a tenth of any site with fewer than ten pages, so the
            // top-decile calculation clamps to one page and reports that page's share under a
            // heading claiming it is the busiest 10%. Say nothing instead.
            var few = Healthy();
            few.UniquePages = WebActivityScoring.MinimumPagesForDecile - 1;
            Assert.IsFalse(
                WebActivityScoring.Judgements(few).Any(j => j.Key == "concentration"),
                "A decile is not expressible below the threshold.");

            var enough = Healthy();
            enough.UniquePages = WebActivityScoring.MinimumPagesForDecile;
            Assert.IsTrue(
                WebActivityScoring.Judgements(enough).Any(j => j.Key == "concentration"),
                "At the threshold the busiest tenth is a real tenth.");

            // The UI reads this threshold off the window rather than hard-coding its own copy, so
            // the Overview judgement and the Page views chart cannot disappear independently.
            var window = WebActivityWindow.From(WebActivityQuery.Create(28, DateTime.UtcNow));
            Assert.AreEqual(WebActivityScoring.MinimumPagesForDecile, window.MinimumPagesForDecile);
        }

        [TestMethod]
        public void BounceJudgement_IsSuppressedWhenThereAreNoVisitsToBounce()
        {
            // hits.session_id is nullable, so a window can hold page views and no visits at all.
            // Bounce rate and pages-per-visit are both zero there, which lands in the "good" band -
            // reassuring an admin that "most visits go beyond the first page" about visits that
            // were never recorded.
            var noVisits = Healthy();
            noVisits.Visits = 0;
            noVisits.BouncePct = 0;
            noVisits.PagesPerVisit = 0;

            var judgements = WebActivityScoring.Judgements(noVisits);

            Assert.IsFalse(
                judgements.Any(j => j.Key == "bounce"),
                "A bounce verdict needs visits to be about.");
            Assert.IsFalse(
                judgements.Any(j => j.Tone == "good" && j.Detail.Contains("Most visits go beyond")),
                "Nothing may report healthy engagement from zero visits.");
        }

        [TestMethod]
        public void FailedKpis_AreNotDiagnosedAsABrokenTracker()
        {
            // A failed KPI query leaves every figure at its zero default, which is indistinguishable
            // from a genuinely empty window. Diagnosing a tracker from it sends an admin to redeploy
            // working software because an aggregate timed out.
            var failed = Healthy();
            failed.KpisUnavailable = true;
            failed.PageViews = 0;

            var judgements = WebActivityScoring.Judgements(failed);

            Assert.AreEqual("kpis-unavailable", judgements[0].Key);
            Assert.AreEqual(1, judgements.Count, "Nothing else can be judged from figures that did not load.");
            Assert.IsFalse(
                judgements.Any(j => j.Detail.Contains("tracker is not deployed")),
                "A timeout must never be reported as a missing tracker.");
        }

        [TestMethod]
        public void AQuietWindow_IsNotDiagnosedAsABrokenTracker()
        {
            // Page views exist outside this window often enough that "nothing in the last 28 days"
            // usually means a quiet period. Telling an admin to redeploy on that evidence is how a
            // working tracker gets pulled apart.
            var quiet = Healthy();
            quiet.PageViews = 0;
            quiet.HasEverCollected = true;

            var quietJudgement = WebActivityScoring.Judgements(quiet).Single();
            Assert.AreEqual("no-traffic", quietJudgement.Key);
            Assert.IsFalse(quietJudgement.Detail.Contains("not deployed"));
            StringAssert.Contains(quietJudgement.Detail, "quiet period");

            // Never collected anything, though, and the tracker really is the first thing to check.
            var never = Healthy();
            never.PageViews = 0;
            never.HasEverCollected = false;

            var neverJudgement = WebActivityScoring.Judgements(never).Single();
            Assert.AreEqual("no-traffic", neverJudgement.Key);
            StringAssert.Contains(neverJudgement.Detail, "not deployed");
        }

        [TestMethod]
        public void AnEmptyDirectory_IsNotReportedAsTheImportBeingOff()
        {
            // The availability badge can say the user directory import is ON while this judgement
            // tells the admin to enable it, which hides the real problem: it ran and found nobody.
            var emptyDirectory = Healthy();
            emptyDirectory.DirectoryImported = true;
            emptyDirectory.KnownUsers = 0;

            var reach = WebActivityScoring.Judgements(emptyDirectory).Single(j => j.Key == "reach");

            Assert.IsFalse(reach.Detail.Contains("import is off"),
                "The toggle is on; saying it is off sends the admin to a setting that is already correct.");
            StringAssert.Contains(reach.Detail, "has not completed successfully");

            var importOff = Healthy();
            importOff.DirectoryImported = false;
            importOff.KnownUsers = 0;

            var offReach = WebActivityScoring.Judgements(importOff).Single(j => j.Key == "reach");
            StringAssert.Contains(offReach.Detail, "import is off");
        }

        #endregion
    }
}
