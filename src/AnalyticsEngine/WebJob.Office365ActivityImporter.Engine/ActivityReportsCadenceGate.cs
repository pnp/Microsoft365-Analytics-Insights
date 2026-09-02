using System;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Pure decision logic for the once-a-day throttle on the activity/usage-report phase, factored out so it
    /// can be unit tested without Redis (issue #376). The sibling of <see cref="ImportCadenceGate"/>, which
    /// gates the per-section Graph imports.
    ///
    /// <para>
    /// <b>Why this takes a UTC "now" while the stored timestamp may be local.</b> Both
    /// <see cref="ISingleDateStore"/> implementations stamp <c>DateTime.Now</c> - <c>InMemorySingleDateStore</c>
    /// stores it directly, and <c>RedisSingleDateLoader</c> round-trips it through <c>"o"</c> format, which
    /// keeps the offset - so <paramref name="lastImported"/> comes back with <see cref="DateTimeKind.Local"/>.
    /// The original comparison was <c>DateTime.Now.Subtract(lastImported) &gt; minWait</c>: local minus local.
    /// That is right in the ordinary case and wrong across a daylight-saving transition, where subtracting two
    /// local wall-clock readings gives the wall-clock difference rather than the elapsed time - so twice a year
    /// the phase either re-ran an hour early or waited an hour too long.
    /// </para>
    /// <para>
    /// Normalising to UTC compares the two <i>instants</i>, which is what "has a day passed?" actually means,
    /// and lets the caller supply the clock (<see cref="DataUtils.IClock"/>) instead of reading wall time.
    /// <see cref="DateTime.ToUniversalTime"/> is a no-op on a <see cref="DateTimeKind.Utc"/> value and treats
    /// <see cref="DateTimeKind.Unspecified"/> as local - which is exactly how the previous expression treated
    /// an offset-less value parsed out of Redis.
    /// </para>
    /// </summary>
    public static class ActivityReportsCadenceGate
    {
        /// <summary>
        /// Whether the activity/usage-report phase is due to run.
        /// </summary>
        /// <param name="lastImported">
        /// When the phase last completed, as returned by <see cref="ISingleDateStore.GetLastDT"/>, or null if
        /// it never has (or no store is configured). Local, UTC and unspecified kinds are all accepted.
        /// </param>
        /// <param name="minWait">Minimum time between runs.</param>
        /// <param name="nowUtc">Current time (UTC), from the injected clock.</param>
        public static bool ShouldRun(DateTime? lastImported, TimeSpan minWait, DateTime nowUtc)
        {
            if (lastImported == null) return true;

            // Strictly greater than, matching the original expression: at exactly minWait the phase waits.
            return nowUtc.Subtract(lastImported.Value.ToUniversalTime()) > minWait;
        }

        /// <summary>
        /// The instant (UTC) at which <see cref="ShouldRun"/> starts returning true for
        /// <paramref name="lastImported"/>. Exists so the "will import again after ..." message an operator
        /// reads is derived from the same arithmetic as the decision: adding <paramref name="minWait"/> to the
        /// stored LOCAL value instead would report a wall-clock time that is an hour out from the real
        /// threshold across a daylight-saving transition.
        /// </summary>
        public static DateTime NextRunUtc(DateTime lastImported, TimeSpan minWait)
        {
            return lastImported.ToUniversalTime().Add(minWait);
        }
    }
}
