using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// The App Insights import-window rule, lifted out of <see cref="AppInsightsImporter"/> so it can be
    /// asserted without a database, an HTTP client or the wall clock. See issue #374.
    ///
    /// This decides whether hits are missed or double-imported at day boundaries, and it was previously
    /// four inline statements with no test at all - despite a code comment warning that getting the
    /// time zone wrong silently loses or duplicates hits near midnight.
    /// </summary>
    public static class AppInsightsImportWindow
    {
        /// <summary>How far back to scan when the database holds no hits yet.</summary>
        public const int NoHitsFallbackDays = 31;

        /// <summary>
        /// The window is rewound slightly so a hit written moments after the previous run's watermark is
        /// still picked up. Re-importing a few seconds of overlap is harmless (the merge de-duplicates on
        /// page-request id); missing them is not.
        /// </summary>
        public const int EdgeHitRewindMinutes = 1;

        /// <summary>
        /// Turn the <c>--daysBefore</c> style override into an absolute start instant, or null when no
        /// override was supplied.
        ///
        /// App Insights timestamps are UTC, so <paramref name="nowUtc"/> must be UTC: on a non-UTC host a
        /// local "now" shifts every day boundary and the per-day KQL filter with it.
        /// </summary>
        public static DateTime? ResolveOverrideStartUtc(int? daysBeforeOverride, DateTime nowUtc)
        {
            if (!daysBeforeOverride.HasValue)
            {
                return null;
            }

            return nowUtc.AddDays(daysBeforeOverride.Value * -1);
        }

        /// <summary>
        /// Where the scan starts: the explicit override, else the newest hit already stored, else
        /// <see cref="NoHitsFallbackDays"/> days back - always rewound by
        /// <see cref="EdgeHitRewindMinutes"/>.
        /// </summary>
        public static DateTime ResolveStartDateUtc(DateTime? overrideStartUtc, DateTime? newestHitUtc, DateTime nowUtc)
        {
            var startDate = overrideStartUtc ?? newestHitUtc ?? nowUtc.AddDays(-NoHitsFallbackDays);

            return startDate.AddMinutes(-EdgeHitRewindMinutes);
        }

        /// <summary>
        /// The days the importer will request, inclusive of both the start and end day.
        ///
        /// Delegates to <c>DateTimeUtils.EachDay</c> so the behaviour is exactly what the importer had.
        /// Note that includes its quirk: when <paramref name="startUtc"/> is AFTER
        /// <paramref name="endUtc"/> the sequence runs BACKWARDS rather than being empty. That cannot
        /// normally happen (the end is "now" and the start is derived from a stored timestamp), but a
        /// future-dated hit_timestamp - clock skew on the writing host, or bad data - would produce it.
        /// Preserved deliberately rather than changed here; see the PR for #374.
        /// </summary>
        public static IReadOnlyList<DateTime> EnumerateDays(DateTime startUtc, DateTime endUtc)
        {
            return startUtc.EachDay(endUtc).ToList();
        }
    }
}
