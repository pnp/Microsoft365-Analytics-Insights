using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules
{
    /// <summary>
    /// The window of report dates a daily usage-report import may skip, and the bounds it looks in.
    /// </summary>
    public sealed class UsageReportSkipWindow
    {
        internal UsageReportSkipWindow(bool canSkipAnyDate, DateTime windowStartUtc, DateTime safeCutoffUtc, int daysBackMax)
        {
            CanSkipAnyDate = canSkipAnyDate;
            WindowStartUtc = windowStartUtc;
            SafeCutoffUtc = safeCutoffUtc;
            DaysBackMax = daysBackMax;
        }

        /// <summary>
        /// False when nothing may be skipped at all - no usage-report phase has ever completed, so any
        /// stored row could be from an interrupted import and must be re-downloaded.
        /// </summary>
        public bool CanSkipAnyDate { get; }

        /// <summary>Oldest date the import looks at (inclusive).</summary>
        public DateTime WindowStartUtc { get; }

        /// <summary>
        /// Exclusive upper bound on skippable dates: the earlier of "Graph may still change this" and
        /// "a completed import has not covered this yet". Equal to <see cref="WindowStartUtc"/> when
        /// nothing in the window is skippable.
        /// </summary>
        public DateTime SafeCutoffUtc { get; }

        /// <summary>The clamped look-back, in days.</summary>
        public int DaysBackMax { get; }
    }

    /// <summary>
    /// The finalized-date skip rule for the Graph daily usage reports, lifted out of
    /// <c>AbstractDailyActivityLoader</c> so it can be asserted with no SQL Server, no Graph and no wall
    /// clock. See issue #375.
    ///
    /// This decision governs whether a day's report is re-downloaded or trusted as already final. Getting
    /// it wrong is expensive in both directions: too eager and every report is re-downloaded and rewritten
    /// on every cycle; too lax and stale numbers are frozen in permanently. It previously existed only as
    /// inline statements around an EF query, with the instant read from <c>DateTime.UtcNow</c> in the
    /// middle of it.
    ///
    /// Follows the <c>ImportCadenceGate.ShouldRun(..., DateTime nowUtc)</c> convention: the instant, and
    /// the completed-phase marker, are parameters rather than dependencies.
    /// </summary>
    public static class UsageReportRefreshPolicy
    {
        /// <summary>Activity reports don't tend to refresh until a couple of days late; always collect something useful.</summary>
        public const int MinDaysBack = 3;

        /// <summary>Graph only retains ~28 days of daily detail.</summary>
        public const int MaxDaysBack = 28;

        /// <summary>
        /// Clamp the configured look-back to what Graph can actually answer. A value below
        /// <see cref="MinDaysBack"/> or above <see cref="MaxDaysBack"/> is corrected rather than rejected,
        /// so a bad app setting cannot stop the import.
        /// </summary>
        public static int ClampDaysBack(int daysBackMax)
        {
            if (daysBackMax < MinDaysBack) return MinDaysBack;
            if (daysBackMax > MaxDaysBack) return MaxDaysBack;
            return daysBackMax;
        }

        /// <summary>
        /// Which part of the import window is safe to skip.
        ///
        /// Two independent conditions have to hold for a stored date to be skippable, and the earlier of
        /// the two cutoffs wins:
        /// <list type="bullet">
        /// <item><b>Graph must be done changing it.</b> Usage reports have a ~2-3 day latency and are
        /// stable once finalized, so anything newer than
        /// <paramref name="refreshableRecentDays"/> can still move and is never skipped.</item>
        /// <item><b>A full import phase must have covered it.</b> Rows newer than the last completed
        /// phase may be from an interrupted save, so they are retried. With no completed phase at all
        /// (<paramref name="lastSuccessfulImport"/> is null) nothing is proven complete and
        /// <see cref="UsageReportSkipWindow.CanSkipAnyDate"/> is false.</item>
        /// </list>
        /// </summary>
        public static UsageReportSkipWindow ResolveSkipWindow(int daysBackMax, DateTime? lastSuccessfulImport,
            int refreshableRecentDays, DateTime nowUtc)
        {
            daysBackMax = ClampDaysBack(daysBackMax);
            var today = nowUtc.Date;
            var windowStart = today.AddDays(-daysBackMax);

            if (!lastSuccessfulImport.HasValue)
            {
                // Existing rows could be from an interrupted import. Until a full usage-report
                // phase completes, no stored date is proven complete enough to skip safely.
                return new UsageReportSkipWindow(false, windowStart, windowStart, daysBackMax);
            }

            var mutableCutoff = today.AddDays(-refreshableRecentDays);   // dates >= this can still change; never skip them
            var completedCutoff = lastSuccessfulImport.Value.ToUniversalTime().Date;
            var safeCutoff = completedCutoff < mutableCutoff ? completedCutoff : mutableCutoff;

            return new UsageReportSkipWindow(true, windowStart, safeCutoff, daysBackMax);
        }

        /// <summary>
        /// The dates in the window that are candidates for skipping, oldest first. Empty when the cutoff is
        /// at or before the window start, which is what makes "skip nothing" fall out naturally rather than
        /// needing a special case at the call site.
        /// </summary>
        public static IEnumerable<DateTime> EnumerateSkipCandidates(UsageReportSkipWindow window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (!window.CanSkipAnyDate) yield break;

            for (var date = window.WindowStartUtc; date < window.SafeCutoffUtc; date = date.AddDays(1))
            {
                yield return date;
            }
        }
    }
}
