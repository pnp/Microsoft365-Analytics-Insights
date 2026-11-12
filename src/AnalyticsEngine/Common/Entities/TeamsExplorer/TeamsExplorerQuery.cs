using Common.Entities.UserFilters;
using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// The reporting window and options every Teams Explorer section is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two windows, not one.</b> Teams Explorer reads from two kinds of source with different
    /// latency, and treating them as one window produces a chart that appears to collapse at the
    /// right-hand edge:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Call records</b> arrive from a Graph change-notification webhook within minutes, so
    /// they are reported right up to today (<see cref="FromUtc"/> / <see cref="ToExclusiveUtc"/>).</item>
    /// <item><b>Microsoft 365 usage reports</b> are published a couple of days in arrears and Graph
    /// keeps back-filling a report date for a short while after it first appears, so the usage window
    /// is shifted back by <see cref="UsageReportLagDays"/> (<see cref="UsageFromUtc"/> /
    /// <see cref="UsageToExclusiveUtc"/>).</item>
    /// </list>
    /// <para>
    /// The usage window is <i>shifted</i> rather than <i>truncated</i> so it still covers a full
    /// <see cref="Days"/> days. Truncating would silently compare a 25-day window against a 28-day one
    /// whenever the caller asked for 28 days, which is exactly the kind of quiet arithmetic error that
    /// makes a report untrustworthy.
    /// </para>
    /// <para>
    /// Windows are snapped to <see cref="AllowedWindowDays"/> rather than validated, so a hand-edited
    /// URL cannot ask for an arbitrary range and force a huge scan. This mirrors
    /// <c>DlpAPIController</c>. (<c>LicenceActivityQuery</c> throws instead, because that page offers a
    /// genuine custom range and rounding a user's explicit dates would be worse than refusing them.)
    /// </para>
    /// </remarks>
    public sealed class TeamsExplorerQuery
    {
        /// <summary>Windows the UI offers. Anything else snaps to the nearest of these.</summary>
        public static readonly int[] AllowedWindowDays = { 7, 28, 90, 180, 365 };

        /// <summary>The window used when the caller does not ask for one.</summary>
        public const int DefaultWindowDays = 28;

        /// <summary>
        /// How far behind "today" a Microsoft 365 usage report is considered settled. Matches
        /// <c>ReportsAPIController.UsageReportLagDays</c>; the two must agree or the Reports page and
        /// this page will disagree about the same week.
        /// </summary>
        public const int UsageReportLagDays = 3;

        /// <summary>Rows returned by a ranked table. Small - these are leaderboards, not exports.</summary>
        public const int DefaultTop = 20;

        /// <summary>Hard ceiling on a ranked table, including for the CSV exports.</summary>
        public const int MaximumTop = 500;

        /// <summary>Demographic dimensions the adoption breakdown can group by.</summary>
        public static readonly string[] Groupings = { "department", "country", "office", "jobTitle", "company" };

        /// <summary>The grouping used when the caller does not ask for one.</summary>
        public const string DefaultGrouping = "department";

        private TeamsExplorerQuery()
        {
        }

        /// <summary>The snapped window length in days.</summary>
        public int Days { get; private set; }

        /// <summary>The "now" this window was computed from, so a cached result can be reproduced.</summary>
        public DateTime NowUtc { get; private set; }

        /// <summary>Inclusive first UTC date of the live-data (call records) window.</summary>
        public DateTime FromUtc { get; private set; }

        /// <summary>Exclusive last UTC date of the live-data window.</summary>
        public DateTime ToExclusiveUtc { get; private set; }

        /// <summary>Inclusive first UTC report date of the usage-report window.</summary>
        public DateTime UsageFromUtc { get; private set; }

        /// <summary>Exclusive last UTC report date of the usage-report window.</summary>
        public DateTime UsageToExclusiveUtc { get; private set; }

        /// <summary>Demographic dimension for the adoption breakdown. Always one of <see cref="Groupings"/>.</summary>
        public string GroupBy { get; private set; }

        /// <summary>Rows to return from a ranked table.</summary>
        public int Top { get; private set; }

        /// <summary>
        /// The people the report may describe - the administrator's global filter, resolved for the reader.
        /// <see cref="ReportUserScope.Everyone"/> when no filter applies.
        /// </summary>
        public ReportUserScope UserScope { get; private set; } = ReportUserScope.Everyone;

        /// <summary>
        /// True when the importers are narrowed to members of some groups (a configured user group filter), so the
        /// usage reports are not imported for everyone in the directory. Somebody with no usage row may then never
        /// have been looked at, so nobody can be called dormant on the strength of having none.
        /// </summary>
        public bool UsageReportsGroupFiltered { get; private set; }

        /// <summary>This window, narrowed to <paramref name="scope"/>. Null is everyone.</summary>
        public TeamsExplorerQuery WithUserScope(ReportUserScope scope)
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.UserScope = scope ?? ReportUserScope.Everyone;
            return copy;
        }

        /// <summary>This window, for a deployment whose usage import is (or is not) narrowed to a group.</summary>
        public TeamsExplorerQuery WithUsageReportsGroupFiltered(bool groupFiltered)
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.UsageReportsGroupFiltered = groupFiltered;
            return copy;
        }

        /// <summary>
        /// This window reading every row, not the first <see cref="MaximumTop"/>: for the adoption breakdown a reader without
        /// See PII is sent folded (<c>TeamsAdoption.WithoutSmallGroups</c>). A group the cut left out would be left out of the
        /// fold too, and subtracting what is shown from the tab's tenant-wide totals would then recover it - one person, when
        /// the groups beyond the cut hold one. Only for a query whose rows are groups: a list of people read this way could be
        /// every person in the tenant.
        /// </summary>
        public TeamsExplorerQuery WithEveryRow()
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.Top = int.MaxValue;
            return copy;
        }

        /// <summary>
        /// True when a section's lists of groups are read in full, whatever <see cref="Top"/> its lists of people are read with.
        /// See <see cref="WithEveryGroup"/>.
        /// </summary>
        public bool EveryGroup { get; private set; }

        /// <summary>
        /// This window reading every group - the People tab's departments - while its lists of people keep <see cref="Top"/>. For
        /// a reader without See PII the departments are folded (<c>TeamsPeople.WithoutIndividualData</c>) and they add up to the
        /// power users published beside them, so a department the cut left out would be left out of the fold too, and the total
        /// less the rows shown would be it: one to four people, when the departments beyond the cut hold one such (#844, P4-1).
        /// </summary>
        public TeamsExplorerQuery WithEveryGroup()
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.EveryGroup = true;
            return copy;
        }

        /// <summary>
        /// This window reading one row more than the longest list a reader can ask for (<see cref="MaximumTop"/>): the lists a
        /// reader without See PII is shown are floored over that export-sized list and only then cut to the rows asked for, so
        /// that two requests with different tops never leave out different rows; the extra row says whether more exist beyond
        /// it, which the floor then treats as left out (<c>TeamsSmallGroups.WithoutSmallRowsOfList</c>).
        /// </summary>
        public TeamsExplorerQuery WithListProbe()
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.Top = MaximumTop + 1;
            return copy;
        }

        /// <summary>
        /// True when the usage-report figures are read over the directory population only. See <see cref="WithDirectoryPopulation"/>.
        /// </summary>
        public bool DirectoryPopulation { get; private set; }

        /// <summary>
        /// This window with every usage-report figure read over the directory population - the enabled (or unknown) directory
        /// users that reach is measured against and the Adoption breakdown is made of - for a reader without See PII (#844,
        /// S4-1). Read as written, the overview's totals and trend, the rhythm, the lifecycle, the engagement trend and the
        /// people per platform also count the activity of accounts disabled since, which the breakdown never contains, so a
        /// total less the breakdown's rows was those accounts' own: one person's, when one was disabled. A reader with See PII
        /// keeps the figures as they always were, and the two are cached apart (<see cref="CacheKey"/>).
        /// </summary>
        public TeamsExplorerQuery WithDirectoryPopulation()
        {
            var copy = (TeamsExplorerQuery)MemberwiseClone();
            copy.DirectoryPopulation = true;
            return copy;
        }

        /// <summary>Inclusive last UTC date of the live-data window, for display.</summary>
        public DateTime ToInclusiveUtc => ToExclusiveUtc.AddDays(-1);

        /// <summary>Inclusive last UTC report date of the usage-report window, for display.</summary>
        public DateTime UsageToInclusiveUtc => UsageToExclusiveUtc.AddDays(-1);

        /// <summary>
        /// Builds a window. <paramref name="days"/>, <paramref name="groupBy"/> and
        /// <paramref name="top"/> are all normalised rather than rejected.
        /// </summary>
        public static TeamsExplorerQuery Create(
            int days,
            DateTime nowUtc,
            string groupBy = DefaultGrouping,
            int top = DefaultTop)
        {
            var snappedDays = SnapDays(days);
            var today = nowUtc.Date;

            var toExclusive = today.AddDays(1);
            var usageToExclusive = today.AddDays(1 - UsageReportLagDays);

            return new TeamsExplorerQuery
            {
                Days = snappedDays,
                NowUtc = nowUtc,
                FromUtc = toExclusive.AddDays(-snappedDays),
                ToExclusiveUtc = toExclusive,
                UsageFromUtc = usageToExclusive.AddDays(-snappedDays),
                UsageToExclusiveUtc = usageToExclusive,
                GroupBy = NormaliseGrouping(groupBy),
                Top = NormaliseTop(top),
            };
        }

        /// <summary>
        /// The nearest allowed window to <paramref name="days"/>. Ties go to the SHORTER window: an
        /// ambiguous request should cost less, not more.
        /// </summary>
        public static int SnapDays(int days)
        {
            if (days <= 0) return DefaultWindowDays;

            var best = AllowedWindowDays[0];
            var bestDistance = Math.Abs(days - best);

            foreach (var candidate in AllowedWindowDays)
            {
                var distance = Math.Abs(days - candidate);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>The requested grouping if it is supported, otherwise <see cref="DefaultGrouping"/>.</summary>
        public static string NormaliseGrouping(string groupBy)
        {
            if (string.IsNullOrWhiteSpace(groupBy)) return DefaultGrouping;

            return Groupings.FirstOrDefault(
                g => string.Equals(g, groupBy.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? DefaultGrouping;
        }

        /// <summary>The requested row count clamped to 1..<see cref="MaximumTop"/>.</summary>
        public static int NormaliseTop(int top)
        {
            if (top < 1) return DefaultTop;
            return top > MaximumTop ? MaximumTop : top;
        }

        /// <summary>
        /// Cache key for one section of the page. Includes the UTC date rather than the full
        /// timestamp, so a cached entry is reused across a day but can never be served after the
        /// window has rolled over to a new day - and, when the report is narrowed, the scope, so two
        /// readers the global filter treats differently never share an entry; and the population, so a section read over the
        /// directory population for a reader without See PII is never served to a reader with it, or the other way round; and
        /// whether its groups were read in full (<see cref="WithEveryGroup"/>).
        /// </summary>
        public string CacheKey(string section)
        {
            var key = string.Format(
                CultureInfo.InvariantCulture,
                "TeamsExplorer::{0}::days={1}::group={2}::top={3}::asof={4:yyyy-MM-dd}",
                section ?? string.Empty,
                Days,
                GroupBy,
                Top,
                NowUtc.Date);

            return key + "::scope=" + (UserScope.IsRestricted ? UserScope.Key : "all")
                + (UsageReportsGroupFiltered ? "::groupFiltered" : string.Empty)
                + (DirectoryPopulation ? "::population=directory" : string.Empty)
                + (EveryGroup ? "::groups=all" : string.Empty);
        }
    }
}
