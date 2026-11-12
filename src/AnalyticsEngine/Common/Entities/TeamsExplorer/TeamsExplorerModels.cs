using Common.Entities.SpoWebActivity;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// One executed query, so the page can show the SQL behind a figure and report a failure per
    /// section rather than failing the whole tab.
    /// </summary>
    /// <remarks>
    /// Every section of every tab runs its own query. A tab that died because one leaderboard timed
    /// out would be far less useful than a tab that renders nine sections and says why the tenth is
    /// missing, so failures are captured here rather than thrown - the same choice
    /// <c>ReportsAPIController</c> makes per chart.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsExplorerQueryInfo
    {
        /// <summary>Section identifier, matching the UI's lookup key.</summary>
        public string Key { get; set; }

        /// <summary>The SQL that produced the section, with its parameters declared, ready to paste into SSMS.</summary>
        public string Sql { get; set; }

        /// <summary>
        /// When the section failed, what its reader is told in English: for a query that ran and failed, the reason and
        /// an error reference, never the exception's text (<see cref="ReportQueryFailure"/>); for a section the server
        /// did not run, the English fallback for <see cref="ErrorCode"/>. Null when it succeeded.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// A stable code for a failure the server itself decided on, so the page can word it in the reader's language;
        /// null for a query that ran and failed, which carries <see cref="ErrorReason"/> and <see cref="ErrorReference"/>
        /// instead. <see cref="Error"/> carries an English fallback whenever this is set.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>The failure's stable reason (<see cref="ReportQueryFailure.Reason"/>), which the portal words.</summary>
        public string ErrorReason { get; set; }

        /// <summary>The error reference the server log carries for this failure.</summary>
        public string ErrorReference { get; set; }

        /// <summary>How long the query took, for spotting the section that needs an index.</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>The reporting window, in the two forms the page needs.</summary>
    /// <remarks>
    /// Both windows are published because they genuinely differ: the usage-report window ends
    /// <see cref="TeamsExplorerQuery.UsageReportLagDays"/> days earlier than the live-data window, and
    /// a reader comparing "calls this week" with "chat messages this week" needs to be told that.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsExplorerWindow
    {
        public int Days { get; set; }

        /// <summary>Inclusive first UTC date covered by the call-record figures.</summary>
        public DateTime FromUtc { get; set; }

        /// <summary>Inclusive last UTC date covered by the call-record figures.</summary>
        public DateTime ToUtc { get; set; }

        /// <summary>Inclusive first UTC report date covered by the usage-report figures.</summary>
        public DateTime UsageFromUtc { get; set; }

        /// <summary>Inclusive last UTC report date covered by the usage-report figures.</summary>
        public DateTime UsageToUtc { get; set; }

        /// <summary>Working days (Mon-Fri) in the window - the denominator of the segment thresholds.</summary>
        public int WorkingDays { get; set; }

        public static TeamsExplorerWindow From(TeamsExplorerQuery query)
        {
            return new TeamsExplorerWindow
            {
                Days = query.Days,
                FromUtc = query.FromUtc,
                ToUtc = query.ToInclusiveUtc,
                UsageFromUtc = query.UsageFromUtc,
                UsageToUtc = query.UsageToInclusiveUtc,
                WorkingDays = TeamsExplorerScoring.WorkingDaysBetween(query.UsageFromUtc, query.UsageToExclusiveUtc),
            };
        }
    }

    /// <summary>Base class carrying the window and the per-section query diagnostics.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public abstract class TeamsExplorerSection
    {
        public TeamsExplorerWindow Window { get; set; }

        public List<TeamsExplorerQueryInfo> Queries { get; set; } = new List<TeamsExplorerQueryInfo>();

        /// <summary>
        /// The most rows the section's floored lists were read with (the statements' <c>@top</c>); 0 when not set. A list that
        /// came back with that many rows may have been cut short, so a reader without See PII's floor treats what lies beyond
        /// as left out (<see cref="TeamsSmallGroups.WithoutSmallRowsOfList{T}"/>). Never sent.
        /// </summary>
        [JsonIgnore]
        public int ReadTop { get; set; }

        /// <summary>True when <paramref name="rows"/> filled <see cref="ReadTop"/>: the list may have been cut short, and more rows may exist.</summary>
        internal bool MayBeCutShort(int rows) => ReadTop > 0 && rows >= ReadTop;
    }

    /// <summary>A named value in a ranked list.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsNamedCountRow
    {
        /// <summary>The value's name; <c>null</c> for the folded <see cref="Other"/> row.</summary>
        public string Name { get; set; }
        public long Count { get; set; }

        /// <summary>Share of the section's total, 0-100. Null where a share is meaningless.</summary>
        public double? SharePct { get; set; }

        /// <summary>
        /// True for the single row a reader without See PII gets in place of every count too small to show by name
        /// (see <see cref="TeamsPeople.WithoutIndividualData"/>). Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Other { get; set; }
    }

    /// <summary>One bucket of a distribution (meeting size, duration, period of day, rating...).</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsBucketRow
    {
        /// <summary>Stable ordering/identity key.</summary>
        public string Key { get; set; }
        public string Label { get; set; }
        public long Count { get; set; }
        public double SharePct { get; set; }

        /// <summary>
        /// True for the single row a reader without See PII gets in place of every bucket that fewer than the minimum
        /// number of people are behind (see <see cref="TeamsSmallGroups.FoldBuckets"/>). Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Other { get; set; }

        /// <summary>
        /// Distinct people behind the bucket, where its query counts them; null where it does not. It only decides what a
        /// reader without See PII is shown, so it is never sent.
        /// </summary>
        [JsonIgnore]
        public int? People { get; set; }
    }

    /// <summary>
    /// The small-group rules the Teams Explorer's lists share, for a reader without the portal's See PII permission (#844).
    /// </summary>
    public static class TeamsSmallGroups
    {
        /// <summary>
        /// <paramref name="rows"/> with every bucket of fewer than <paramref name="minimumPeople"/> people folded into one
        /// <see cref="TeamsBucketRow.Other"/> row. A bucket whose people were not counted is treated as small. When the
        /// folded buckets' people could be fewer than the minimum - always, since each is small - the smallest bucket shown
        /// goes into the row too, so that a combined row of at least the minimum people is published and the small buckets
        /// cannot be worked out as the difference from the shown ones. When no bucket is big enough, none is shown.
        /// People are counted per bucket, and one person can be behind several, so the combined row's people are bounded
        /// below by its biggest bucket, never by their sum. New rows: the list is cached and shared between readers.
        /// </summary>
        /// <param name="folded">How many buckets were folded into the combined row, or left out with it.</param>
        public static List<TeamsBucketRow> FoldBuckets(List<TeamsBucketRow> rows, int minimumPeople, out int folded)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = rows ?? new List<TeamsBucketRow>();
            var shown = all.Where(r => (r.People ?? 0) >= minimumPeople).ToList();
            var small = all.Where(r => (r.People ?? 0) < minimumPeople).ToList();
            folded = small.Count;
            if (small.Count == 0) return all.ToList();
            if (shown.Count == 0) return new List<TeamsBucketRow>();

            var smallest = shown
                .OrderBy(r => r.People ?? 0)
                .ThenBy(r => r.Count)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .First();
            shown.Remove(smallest);
            small.Add(smallest);
            folded = small.Count;

            var total = all.Sum(r => r.Count);
            var count = small.Sum(r => r.Count);
            shown.Add(new TeamsBucketRow
            {
                Key = null,
                Label = null,
                Other = true,
                Count = count,
                SharePct = TeamsExplorerScoring.Percentage(count, total),
                People = small.Max(r => r.People ?? 0),
            });
            return shown;
        }

        /// <summary>
        /// True for a figure about 1 to <paramref name="minimumPeople"/> - 1 people: too few to show. Nobody (0) is not a
        /// group, so a zero is shown as it is; null is a figure already withheld.
        /// </summary>
        internal static bool IsFew(long? people, int minimumPeople) => people.HasValue && people.Value > 0 && people.Value < minimumPeople;

        /// <summary>
        /// Which of <paramref name="people"/> - the people behind each part of a total the page publishes beside them - a reader
        /// without See PII is not shown: every part of 1 to <paramref name="minimumPeople"/> - 1 people and, whenever there is
        /// one, the smallest part still shown as well. The total less the parts shown is then a combined figure of at least the
        /// minimum people, never one small part's. It is <see cref="WebActivitySmallGroups.WithoutSmallRows{T}"/>' rule (and
        /// <see cref="FoldBuckets"/>'), for parts withheld in place rather than left out or folded into a row; a part of nobody
        /// takes no part in it and is shown as the zero it is. Ties go to the first part.
        /// </summary>
        /// <returns>The indexes of the parts to withhold.</returns>
        internal static HashSet<int> WithheldParts(IReadOnlyList<long> people, int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var parts = people ?? new List<long>();
            var somebody = Enumerable.Range(0, parts.Count).Where(i => parts[i] > 0).ToList();
            var shown = WebActivitySmallGroups.WithoutSmallRows(
                somebody,
                i => (int?)Math.Min(parts[i], int.MaxValue),
                i => i.ToString("D10", CultureInfo.InvariantCulture),
                minimumPeople,
                out _);
            return new HashSet<int>(somebody.Except(shown));
        }

        /// <summary>
        /// The engagement mix for a reader without See PII: a segment of 1 to <paramref name="minimumPeople"/> - 1 people is
        /// withheld. Power, Regular and Light add up to the active people the page publishes, so withholding one of them
        /// withholds the smallest of them shown too (<see cref="WithheldParts"/>). Dormant is the rest of the measured people,
        /// so once anything is withheld - or <paramref name="hideShares"/> says a figure beside the mix was - every share is
        /// withheld too: a share is a fraction of the measured total, which less the active people would give Dormant back.
        /// New slices: the mix is cached and shared between readers.
        /// </summary>
        internal static List<TeamsSegmentSlice> WithoutSmallSegments(List<TeamsSegmentSlice> slices, int minimumPeople, bool hideShares)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = slices ?? new List<TeamsSegmentSlice>();
            var dormant = TeamsUserSegment.Dormant.ToString();
            var active = Enumerable.Range(0, all.Count).Where(i => all[i].Segment != dormant).ToList();
            var withheld = new HashSet<int>(
                WithheldParts(active.Select(i => (long)(all[i].Users ?? 0)).ToList(), minimumPeople).Select(j => active[j]));
            foreach (var i in Enumerable.Range(0, all.Count).Where(i => all[i].Segment == dormant && IsFew(all[i].Users, minimumPeople)))
            {
                withheld.Add(i);
            }

            var sharesWithheld = hideShares || withheld.Count > 0;
            return all
                .Select((slice, i) => withheld.Contains(i) ? slice.AsWithheld() : sharesWithheld ? slice.WithoutShare() : slice)
                .ToList();
        }

        /// <summary>
        /// The weekly trend for a reader without See PII: a week in which 1 to <paramref name="minimumPeople"/> - 1 people were
        /// active is withheld - its messages and meetings are those few people's - and so is the week with the fewest active
        /// people still shown, since the weeks add up to the period's totals published beside them
        /// (<see cref="WithheldParts"/>). Every week's calls are withheld: they are the Meetings tab's weekly calls, which that
        /// tab floors on the attendees this query does not count. New points: the trend is cached and shared between readers.
        /// </summary>
        internal static List<TeamsTrendPoint> WithoutSmallWeeks(List<TeamsTrendPoint> trend, int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var points = trend ?? new List<TeamsTrendPoint>();
            var withheld = WithheldParts(points.Select(p => (long)(p.ActiveUsers ?? 0)).ToList(), minimumPeople);
            return points.Select((p, i) => withheld.Contains(i) ? p.AsWithheld() : p.WithoutCalls()).ToList();
        }

        /// <summary>
        /// The Meetings tab's weekly trend for a reader without See PII: a week whose calls 1 to
        /// <paramref name="minimumPeople"/> - 1 people attended, or whose attendees were not counted, is withheld
        /// (<see cref="TeamsCallTrendPoint.DescribesFewerThan"/>), and whenever one is, so is the week with the fewest attendees
        /// still shown. The weeks add up to the calls and call hours the tab publishes beside them (and the mean length times
        /// the calls), so with the small week alone withheld, those totals less the weeks shown would be its calls and minutes.
        /// <see cref="WebActivitySmallGroups.WithoutSmallRows{T}"/>' rule; ties go to the earliest week. A week without calls is
        /// nobody's: it is shown as it is and is never the one that goes with a small week, since withholding it would hide
        /// nothing. New points: the trend is cached and shared between readers.
        /// </summary>
        internal static List<TeamsCallTrendPoint> WithoutSmallCallWeeks(List<TeamsCallTrendPoint> trend, int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var points = trend ?? new List<TeamsCallTrendPoint>();
            var withCalls = points.Where(p => (p.Calls ?? 0) > 0).ToList();
            var shown = new HashSet<TeamsCallTrendPoint>(WebActivitySmallGroups.WithoutSmallRows(
                withCalls,
                p => p.DescribesFewerThan(minimumPeople) ? 0 : p.Attendees,
                p => p.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                minimumPeople,
                out _));
            return points.Select(p => (p.Calls ?? 0) > 0 && !shown.Contains(p) ? p.AsWithheld() : p).ToList();
        }

        /// <summary>
        /// The rows of a ranked list - teams, channels, their sentiment - a reader without See PII is shown. Decided over the
        /// export-sized list (the first <see cref="TeamsExplorerQuery.MaximumTop"/> rows) before any cut to the rows asked for,
        /// so that a longer answer only ever adds rows to a shorter one: two requests with different tops never leave out
        /// different rows (#844). A row of fewer than <paramref name="minimumPeople"/> people is left out. Then, whenever what
        /// is left out adds to the totals published beside the list - a left-out row that <paramref name="adds"/>, or rows
        /// beyond a list that <paramref name="mayBeCutShort"/> (or runs past the export size), unseen - so is the smallest row
        /// still shown that adds to them, so that those totals less the rows shown are never one small row's or the unseen
        /// rows' alone. It is <see cref="WebActivitySmallGroups.WithoutSmallRows{T}"/>' rule, except that a row adding nothing
        /// (a team with no messages) is never the one that goes with them, since leaving it out would hide nothing. Ties go to
        /// the first by <paramref name="name"/>. The list's order is kept. A new list: the rows are cached and shared between
        /// readers, and are never edited.
        /// </summary>
        /// <param name="leftOut">How many of the export-sized list's rows were left out.</param>
        internal static List<T> WithoutSmallRowsOfList<T>(
            IEnumerable<T> rows, Func<T, int?> people, Func<T, bool> adds, Func<T, string> name, int minimumPeople, bool mayBeCutShort,
            out int leftOut)
            where T : class
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = rows?.ToList() ?? new List<T>();
            var cutShort = mayBeCutShort || all.Count > TeamsExplorerQuery.MaximumTop;
            var list = all.Take(TeamsExplorerQuery.MaximumTop).ToList();
            var shown = list.Where(r => (people(r) ?? 0) >= minimumPeople).ToList();
            if (cutShort || list.Any(r => (people(r) ?? 0) < minimumPeople && adds(r)))
            {
                var smallest = shown
                    .Where(adds)
                    .OrderBy(r => people(r) ?? 0)
                    .ThenBy(r => name(r) ?? string.Empty, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (smallest != null) shown.Remove(smallest);
            }
            leftOut = list.Count - shown.Count;
            return shown;
        }

        /// <summary>
        /// Which of the <paramref name="shown"/> rows of a list have their reactions withheld from a reader without See PII:
        /// every row whose reactions 1 to <paramref name="minimumPeople"/> - 1 people made; and, when the rows whose reactions
        /// are not shown - those <paramref name="leftOut"/>, those withheld and, when the list <paramref name="mayBeCutShort"/>,
        /// those beyond it - hold somebody's reactions but no row's that the minimum or more people made, the shown row whose
        /// reactions the fewest people made, of the minimum or more (ties to the first by <paramref name="name"/>). The
        /// reactions total published beside the list less the reactions shown is then never a handful of people's.
        /// </summary>
        internal static HashSet<T> ReactionsWithheld<T>(
            IReadOnlyList<T> shown, IEnumerable<T> leftOut, Func<T, int> reactors, Func<T, string> name, int minimumPeople, bool mayBeCutShort)
            where T : class
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var rows = shown ?? new List<T>();
            var withheld = new HashSet<T>(rows.Where(r => IsFew(reactors(r), minimumPeople)));
            var hidden = (leftOut ?? Enumerable.Empty<T>()).Concat(withheld).ToList();
            var somebodyHidden = mayBeCutShort || hidden.Any(r => reactors(r) > 0);
            if (somebodyHidden && !hidden.Any(r => reactors(r) >= minimumPeople))
            {
                var complement = rows
                    .Where(r => !withheld.Contains(r) && reactors(r) >= minimumPeople)
                    .OrderBy(reactors)
                    .ThenBy(r => name(r) ?? string.Empty, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (complement != null) withheld.Add(complement);
            }
            return withheld;
        }

        /// <summary>
        /// The people per platform for a reader without See PII: a platform 1 to <paramref name="minimumPeople"/> - 1 people
        /// used is withheld, and so is <see cref="TeamsDeviceRow.AnyMobile"/> whenever iOS or Android is, since it less the
        /// other would bound the withheld one. Once anything is withheld every share is too: a share is a fraction of the
        /// people with any device row, a total the withheld platforms are part of. New rows: the list is cached and shared.
        /// </summary>
        internal static List<TeamsDeviceRow> WithoutSmallPlatforms(List<TeamsDeviceRow> rows, int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var all = rows ?? new List<TeamsDeviceRow>();
            var withheld = new HashSet<string>(all.Where(r => IsFew(r.Users, minimumPeople)).Select(r => r.Platform), StringComparer.Ordinal);
            if (withheld.Contains(TeamsDeviceRow.Ios) || withheld.Contains(TeamsDeviceRow.Android)) withheld.Add(TeamsDeviceRow.AnyMobile);
            if (withheld.Count == 0) return all.ToList();

            return all.Select(r => withheld.Contains(r.Platform) ? r.AsWithheld() : r.WithoutShare()).ToList();
        }

        /// <summary>
        /// <paramref name="dormancy"/> for a reader without See PII: licensed people left out of the engagement mix are, like a
        /// Dormant segment, people with no recorded activity, so a count of 1 to <paramref name="minimumPeople"/> - 1 of them is
        /// withheld. A copy when it changes: the answer is cached and shared between readers.
        /// </summary>
        internal static TeamsDormancy WithoutFewUnmeasured(TeamsDormancy dormancy, int minimumPeople)
        {
            if (dormancy == null || !IsFew(dormancy.UnmeasuredUsers, minimumPeople)) return dormancy;
            var copy = dormancy.Copy();
            copy.UnmeasuredUsers = null;
            return copy;
        }
    }

    #region Overview

    /// <summary>The Overview tab's headline figures.</summary>
    /// <remarks>
    /// The usage-report figures - everything but the calls, teams and channels - are null for a reader without See PII when
    /// they would describe fewer than <see cref="TeamsOverview.MinimumGroupSize"/> people (<see cref="TeamsOverview.WithoutSmallGroups"/>):
    /// withheld, never 0. Every other reader is always sent a number, so they default to 0 rather than null.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsOverviewKpis
    {
        /// <summary>Directory users the reach percentage is measured against.</summary>
        public int? KnownUsers { get; set; } = 0;

        /// <summary>
        /// Enabled (or unknown) directory users with any Teams activity in the usage-report window - the same
        /// population as <see cref="KnownUsers"/>, so reach can never count a since-disabled account on one side
        /// only.
        /// </summary>
        public int? ActiveUsers { get; set; } = 0;

        public double? ReachPct { get; set; } = 0;

        /// <summary>Messages posted in channels (posts + replies + team chat).</summary>
        public long? ChannelMessages { get; set; } = 0;

        /// <summary>Messages sent in private chats.</summary>
        public long? PrivateMessages { get; set; } = 0;

        /// <summary>Channel share of all chat messages, 0-100.</summary>
        public double? OpenCollaborationPct { get; set; } = 0;

        public long? MeetingsAttended { get; set; } = 0;
        public long? MeetingsOrganised { get; set; } = 0;
        public double? MeetingsPerActiveUser { get; set; } = 0;

        /// <summary>
        /// Hours of audio in calls. Audio is used as the wall-clock proxy on purpose: audio, video and
        /// screenshare durations OVERLAP inside a single meeting, so adding them together would
        /// produce a number larger than the meeting itself.
        /// </summary>
        public double? AudioHours { get; set; } = 0;

        /// <summary>Video hours as a share of audio hours, 0-100.</summary>
        public double? VideoSharePct { get; set; } = 0;

        /// <summary>Screenshare hours as a share of audio hours, 0-100.</summary>
        public double? ScreenShareSharePct { get; set; } = 0;

        /// <summary>Calls recorded by the call-records webhook in the live window.</summary>
        public long Calls { get; set; }

        public int TotalTeams { get; set; }
        public int ActiveTeams { get; set; }
        public int TotalChannels { get; set; }
        public int ActiveChannels { get; set; }

        /// <summary>These figures with every usage-report figure withheld. A copy: the section is cached and shared.</summary>
        internal TeamsOverviewKpis WithoutUsageFigures()
        {
            var copy = (TeamsOverviewKpis)MemberwiseClone();
            copy.KnownUsers = null;
            copy.ActiveUsers = null;
            copy.ReachPct = null;
            copy.ChannelMessages = null;
            copy.PrivateMessages = null;
            copy.OpenCollaborationPct = null;
            copy.MeetingsAttended = null;
            copy.MeetingsOrganised = null;
            copy.MeetingsPerActiveUser = null;
            copy.AudioHours = null;
            copy.VideoSharePct = null;
            copy.ScreenShareSharePct = null;
            return copy;
        }
    }

    /// <summary>One week of the Overview trend - and, with only <see cref="ActiveUsers"/> set, of the membership trend.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsTrendPoint
    {
        public DateTime WeekStart { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? ActiveUsers { get; set; } = 0;

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public long? ChannelMessages { get; set; } = 0;

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public long? PrivateMessages { get; set; } = 0;

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public long? MeetingsAttended { get; set; } = 0;

        /// <summary>Null for a reader without See PII: see <see cref="TeamsOverview.WeeklyCallsWithheld"/>.</summary>
        public long? Calls { get; set; } = 0;

        /// <summary>
        /// True when, for a reader without See PII, the week's figures are null: 1 to 4 people were active in it, or it is the
        /// week that keeps such a week from being worked out from the period's totals (<see cref="TeamsSmallGroups.WithoutSmallWeeks"/>).
        /// Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }

        /// <summary>This week with every figure withheld. A new point: the trend is cached and shared between readers.</summary>
        internal TeamsTrendPoint AsWithheld() => new TeamsTrendPoint
        {
            WeekStart = WeekStart,
            ActiveUsers = null,
            ChannelMessages = null,
            PrivateMessages = null,
            MeetingsAttended = null,
            Calls = null,
            Withheld = true,
        };

        /// <summary>This week without its calls. A copy.</summary>
        internal TeamsTrendPoint WithoutCalls()
        {
            var copy = (TeamsTrendPoint)MemberwiseClone();
            copy.Calls = null;
            return copy;
        }
    }

    /// <summary>One slice of the engagement mix.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSegmentSlice
    {
        public string Segment { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Users { get; set; } = 0;

        /// <summary>
        /// Share of the measured people, 0-100. Null when <see cref="Withheld"/>, and on every slice when, for a reader without
        /// See PII, any slice is (<see cref="TeamsSmallGroups.WithoutSmallSegments"/>).
        /// </summary>
        public double? SharePct { get; set; } = 0;

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Users"/> and <see cref="SharePct"/> are null: 1 to 4 people are in
        /// the segment, or it keeps such a segment from being worked out from the active people published beside it. Sent only
        /// when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }

        /// <summary>This slice with its figures withheld. A new slice: the mix is cached and shared between readers.</summary>
        internal TeamsSegmentSlice AsWithheld() => new TeamsSegmentSlice
        {
            Segment = Segment,
            Label = Label,
            Description = Description,
            Users = null,
            SharePct = null,
            Withheld = true,
        };

        /// <summary>This slice without its share. A copy.</summary>
        internal TeamsSegmentSlice WithoutShare()
        {
            var copy = (TeamsSegmentSlice)MemberwiseClone();
            copy.SharePct = null;
            return copy;
        }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsOverview : TeamsExplorerSection
    {
        public TeamsOverviewKpis Kpis { get; set; } = new TeamsOverviewKpis();
        public List<TeamsTrendPoint> Trend { get; set; } = new List<TeamsTrendPoint>();
        public List<TeamsSegmentSlice> SegmentMix { get; set; } = new List<TeamsSegmentSlice>();
        public List<TeamsJudgement> Judgements { get; set; } = new List<TeamsJudgement>();

        /// <summary>Whether the engagement mix's Dormant share could be measured, and why not.</summary>
        public TeamsDormancy Dormancy { get; set; } = new TeamsDormancy();

        /// <summary>
        /// The fewest people a figure of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, every usage-report KPI is null: 1 to 4 people were active in the period,
        /// or are in the directory at all, so each of those figures is theirs. Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool UsageWithheld { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, the trend's weekly calls are null: they are the Meetings tab's weekly
        /// calls, which that tab withholds by the attendees a week had, and this tab does not count them. Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool WeeklyCallsWithheld { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#844, S4-1). It must have been built
        /// over the directory population (<see cref="TeamsExplorerQuery.WithDirectoryPopulation"/>) - the enabled people the
        /// Adoption breakdown is made of - so that no total here less that breakdown's rows leaves a since-disabled account's
        /// activity. Then no figure describes fewer than <paramref name="minimumPeople"/> people:
        /// <list type="bullet">
        /// <item>when 1 to 4 people were active, or are in the directory, every usage-report KPI is withheld, and with it
        /// the judgements worded from them (<see cref="TeamsExplorerScoring.UsageJudgementKeys"/>);</item>
        /// <item>a week of 1 to 4 active people is withheld, with the week that keeps it from being worked out from the
        /// totals, and every week's calls (<see cref="TeamsSmallGroups.WithoutSmallWeeks"/>);</item>
        /// <item>an engagement segment of 1 to 4 people is withheld, with the segment that keeps it from being worked out
        /// from the active people, and the shares (<see cref="TeamsSmallGroups.WithoutSmallSegments"/>);</item>
        /// <item>a count of 1 to 4 licensed people left out of the mix is withheld (<see cref="TeamsSmallGroups.WithoutFewUnmeasured"/>).</item>
        /// </list>
        /// The calls KPI, the call judgements and the team counts are the Meetings and Collaboration tabs' own figures, and
        /// are left as those tabs show them. A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public TeamsOverview WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var kpis = Kpis ?? new TeamsOverviewKpis();
            var usageWithheld = TeamsSmallGroups.IsFew(kpis.KnownUsers, minimumPeople) || TeamsSmallGroups.IsFew(kpis.ActiveUsers, minimumPeople);

            var copy = (TeamsOverview)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.UsageWithheld = usageWithheld;
            copy.Kpis = usageWithheld ? kpis.WithoutUsageFigures() : kpis;
            copy.Trend = TeamsSmallGroups.WithoutSmallWeeks(Trend, minimumPeople);
            copy.WeeklyCallsWithheld = true;
            copy.Dormancy = TeamsSmallGroups.WithoutFewUnmeasured(Dormancy, minimumPeople);
            var unmeasuredWithheld = !ReferenceEquals(copy.Dormancy, Dormancy);
            copy.SegmentMix = TeamsSmallGroups.WithoutSmallSegments(SegmentMix, minimumPeople, hideShares: usageWithheld || unmeasuredWithheld);
            copy.Judgements = (Judgements ?? new List<TeamsJudgement>())
                .Where(j => !usageWithheld || !TeamsExplorerScoring.UsageJudgementKeys.Contains(j.Key))
                .ToList();
            return copy;
        }
    }

    /// <summary>
    /// Whether the usage reports can prove that somebody did nothing in Teams in the window, and the facts behind
    /// the answer, so the page can say why when they cannot.
    /// </summary>
    /// <remarks>
    /// The importer reads Graph's Teams user-activity report one date at a time, and Microsoft documents that form
    /// as listing the users who performed activity on that date. A licensed person with no row is therefore dormant
    /// only when every day of the window was imported, the import covers everyone, and the reports carry real
    /// names (see <see cref="TeamsExplorerSql.UsageCoverageDeclarations"/>). When any of those fails, such people
    /// are left out of the Dormant segment and list - and counted in <see cref="UnmeasuredUsers"/> - rather than
    /// called dormant on evidence that cannot show it.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsDormancy
    {
        /// <summary><see cref="Reason"/> when a day of the window has no usage report imported.</summary>
        public const string IncompleteImport = "incompleteImport";

        /// <summary><see cref="Reason"/> when the usage import is narrowed to members of some groups.</summary>
        public const string GroupFiltered = "groupFiltered";

        /// <summary><see cref="Reason"/> when the usage reports carry concealed names.</summary>
        public const string NamesConcealed = "namesConcealed";

        /// <summary><see cref="Reason"/> when nobody in scope holds a licence that includes Teams, as far as the directory shows.</summary>
        public const string NoTeamsLicences = "noTeamsLicences";

        /// <summary><see cref="Reason"/> when the check itself failed, so nothing can be said.</summary>
        public const string NotChecked = "notChecked";

        /// <summary>True when a licensed person with no Teams activity can be called dormant.</summary>
        public bool Known { get; set; }

        /// <summary>A stable key for why <see cref="Known"/> is false (the page writes the sentence); null when it is true.</summary>
        public string Reason { get; set; }

        /// <summary>Days in the usage window.</summary>
        public int ExpectedDays { get; set; }

        /// <summary>Days in the usage window with a Teams usage report imported - any row at all, from anyone.</summary>
        public int ImportedDays { get; set; }

        /// <summary>Enabled people in scope holding a licence that includes Teams.</summary>
        public int TeamsLicensedUsers { get; set; }

        /// <summary>
        /// Licensed people with no usage row in the window, left out of the engagement mix because
        /// <see cref="Known"/> is false. Always 0 when it is true, and on the People tab. Null when, for a reader without See
        /// PII, it is 1 to 4 people (<see cref="TeamsSmallGroups.WithoutFewUnmeasured"/>).
        /// </summary>
        public int? UnmeasuredUsers { get; set; } = 0;

        /// <summary>A copy, for a reader whose answer differs: the answer is cached and shared between readers.</summary>
        internal TeamsDormancy Copy() => (TeamsDormancy)MemberwiseClone();

        /// <summary>The answer for the facts <see cref="TeamsExplorerSql.UsageCoverage"/> returned; null when it failed.</summary>
        public static TeamsDormancy From(TeamsExplorerRows.UsageCoverageRow row)
        {
            if (row == null) return new TeamsDormancy { Known = false, Reason = NotChecked };

            string reason = null;
            if (row.ImportedDays < row.ExpectedDays) reason = IncompleteImport;
            else if (row.GroupFiltered) reason = GroupFiltered;
            else if (row.NamesConcealed) reason = NamesConcealed;
            else if (row.TeamsLicensedUsers == 0) reason = NoTeamsLicences;

            return new TeamsDormancy
            {
                Known = reason == null,
                Reason = reason,
                ExpectedDays = row.ExpectedDays,
                ImportedDays = row.ImportedDays,
                TeamsLicensedUsers = row.TeamsLicensedUsers,
            };
        }
    }

    #endregion

    #region Adoption

    /// <summary>How habitual Teams use is across the tenant.</summary>
    /// <remarks>
    /// Each figure is null for a reader without See PII when it would describe fewer than
    /// <see cref="TeamsAdoption.MinimumGroupSize"/> people (<see cref="WithoutSmallGroups"/>); every other reader is always
    /// sent a number.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsAdoptionRhythm
    {
        /// <summary>Mean daily active users across the window's working days.</summary>
        public double? MeanDailyActiveUsers { get; set; } = 0;

        /// <summary>Distinct active users in the last 7 days of the usage window.</summary>
        public int? WeeklyActiveUsers { get; set; } = 0;

        /// <summary>Distinct active users in the last 28 days of the usage window.</summary>
        public int? MonthlyActiveUsers { get; set; } = 0;

        /// <summary>
        /// Mean DAU as a share of MAU, 0-100. The standard "stickiness" measure: 100% would mean
        /// everyone who uses Teams at all uses it every working day.
        /// </summary>
        public double? StickinessPct { get; set; } = 0;

        /// <summary>
        /// These figures for a reader without See PII: a weekly or monthly count of 1 to <paramref name="minimumPeople"/> - 1
        /// active people is withheld, and so is the daily mean when that few were active in the whole period - it averages
        /// their days. <paramref name="periodActiveUsers"/> is that count, or null when it could not be read, in which case
        /// the mean is shown only when the monthly count alone proves enough people. Stickiness is the daily mean over the
        /// monthly count, so it goes with either. A copy: the section is cached and shared between readers.
        /// </summary>
        internal TeamsAdoptionRhythm WithoutSmallGroups(int minimumPeople, int? periodActiveUsers)
        {
            var copy = (TeamsAdoptionRhythm)MemberwiseClone();
            if (TeamsSmallGroups.IsFew(WeeklyActiveUsers, minimumPeople)) copy.WeeklyActiveUsers = null;
            if (TeamsSmallGroups.IsFew(MonthlyActiveUsers, minimumPeople)) copy.MonthlyActiveUsers = null;

            var dailyFew = (MeanDailyActiveUsers ?? 0) > 0 && (periodActiveUsers.HasValue
                ? TeamsSmallGroups.IsFew(periodActiveUsers, minimumPeople)
                : (MonthlyActiveUsers ?? 0) < minimumPeople);
            if (dailyFew) copy.MeanDailyActiveUsers = null;
            if (copy.MeanDailyActiveUsers == null || copy.MonthlyActiveUsers == null) copy.StickinessPct = null;
            return copy;
        }
    }

    /// <summary>One week's segment counts, for the segment-mix trend.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSegmentTrendPoint
    {
        public DateTime WeekStart { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Power { get; set; } = 0;

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Regular { get; set; } = 0;

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Light { get; set; } = 0;

        /// <summary>
        /// True when, for a reader without See PII, the week's segments are null: one of them had 1 to 4 people, and the
        /// week's active people - published on the Overview's trend - less the others would give it back. Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }

        /// <summary>This week with every segment withheld when any is 1 to <paramref name="minimumPeople"/> - 1 people. A new point when it changes.</summary>
        internal TeamsSegmentTrendPoint WithoutSmallSegments(int minimumPeople)
        {
            if (!TeamsSmallGroups.IsFew(Power, minimumPeople)
                && !TeamsSmallGroups.IsFew(Regular, minimumPeople)
                && !TeamsSmallGroups.IsFew(Light, minimumPeople))
            {
                return this;
            }

            return new TeamsSegmentTrendPoint { WeekStart = WeekStart, Power = null, Regular = null, Light = null, Withheld = true };
        }
    }

    /// <summary>Adoption for one demographic group (department, country, office, job title, company).</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsDemographicRow
    {
        /// <summary>Tenant data, never translated. <c>null</c> for the folded <see cref="Other"/> row.</summary>
        public string Name { get; set; }

        /// <summary>
        /// True for the single row a reader without See PII gets in place of every group too small to show by name
        /// (see <see cref="TeamsAdoption.WithoutSmallGroups"/>).
        /// </summary>
        public bool Other { get; set; }

        public int KnownUsers { get; set; }
        public int ActiveUsers { get; set; }
        public double ReachPct { get; set; }
        public double MessagesPerActiveUser { get; set; }
        public double MeetingsPerActiveUser { get; set; }
    }

    /// <summary>Users seen on one platform in the window.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsDeviceRow
    {
        /// <summary>The <see cref="Platform"/> of iOS.</summary>
        public const string Ios = "iOS";

        /// <summary>The <see cref="Platform"/> of Android.</summary>
        public const string Android = "Android";

        /// <summary>The <see cref="Platform"/> of the people seen on iOS or Android or both.</summary>
        public const string AnyMobile = "Mobile (any)";

        /// <summary>
        /// The platform's English name: what the portal shows only for a <see cref="PlatformKey"/> it does not know.
        /// </summary>
        public string Platform { get; set; }

        /// <summary>
        /// A stable key the portal words the platform from, in the reader's language (#850): <c>windows</c>, <c>mac</c>,
        /// <c>web</c>, <c>ios</c>, <c>android</c>, <c>linux</c>, <c>chromeOs</c>, or <c>mobile</c> for
        /// <see cref="AnyMobile"/>. The API reports facts; the UI writes the sentences.
        /// </summary>
        public string PlatformKey { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Users { get; set; } = 0;

        /// <summary>
        /// Share of users with any device row, 0-100. Shares sum above 100: people use several platforms. Null when
        /// <see cref="Withheld"/>, and on every row when, for a reader without See PII, any row is
        /// (<see cref="TeamsSmallGroups.WithoutSmallPlatforms"/>).
        /// </summary>
        public double? SharePct { get; set; } = 0;

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Users"/> and <see cref="SharePct"/> are null: 1 to 4 people used
        /// the platform, or - for <see cref="AnyMobile"/> - iOS or Android is withheld. Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }

        /// <summary>This row with its figures withheld. A new row: the list is cached and shared between readers.</summary>
        internal TeamsDeviceRow AsWithheld() =>
            new TeamsDeviceRow { Platform = Platform, PlatformKey = PlatformKey, Users = null, SharePct = null, Withheld = true };

        /// <summary>This row without its share. A copy.</summary>
        internal TeamsDeviceRow WithoutShare()
        {
            var copy = (TeamsDeviceRow)MemberwiseClone();
            copy.SharePct = null;
            return copy;
        }
    }

    /// <summary>Movement in and out of Teams use across the window.</summary>
    /// <remarks>
    /// The three counts add up to the people active in the period, so for a reader without See PII a count of 1 to 4 is
    /// null, with the smallest other count beside it (<see cref="WithoutSmallGroups"/>); every other reader is always sent a
    /// number.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsLifecycle
    {
        /// <summary>Active in the second half of the window but not the first - newly on board.</summary>
        public int? NewUsers { get; set; } = 0;

        /// <summary>Active in both halves.</summary>
        public int? ReturningUsers { get; set; } = 0;

        /// <summary>Active in the first half but not the second - the ones to worry about.</summary>
        public int? LapsedUsers { get; set; } = 0;

        /// <summary>
        /// These counts for a reader without See PII: a count of 1 to <paramref name="minimumPeople"/> - 1 people is withheld,
        /// and so is the smallest count shown, since together they are the active people the page publishes
        /// (<see cref="TeamsSmallGroups.WithheldParts"/>). A copy: the section is cached and shared between readers.
        /// </summary>
        internal TeamsLifecycle WithoutSmallGroups(int minimumPeople)
        {
            var withheld = TeamsSmallGroups.WithheldParts(new long[] { NewUsers ?? 0, ReturningUsers ?? 0, LapsedUsers ?? 0 }, minimumPeople);
            return new TeamsLifecycle
            {
                NewUsers = withheld.Contains(0) ? null : NewUsers,
                ReturningUsers = withheld.Contains(1) ? null : ReturningUsers,
                LapsedUsers = withheld.Contains(2) ? null : LapsedUsers,
            };
        }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsAdoption : TeamsExplorerSection
    {
        /// <summary>The demographic dimension the breakdown is grouped by.</summary>
        public string GroupBy { get; set; }

        public TeamsAdoptionRhythm Rhythm { get; set; } = new TeamsAdoptionRhythm();
        public List<TeamsSegmentSlice> SegmentMix { get; set; } = new List<TeamsSegmentSlice>();
        public List<TeamsSegmentTrendPoint> SegmentTrend { get; set; } = new List<TeamsSegmentTrendPoint>();
        public List<TeamsDemographicRow> Breakdown { get; set; } = new List<TeamsDemographicRow>();
        public List<TeamsDeviceRow> Devices { get; set; } = new List<TeamsDeviceRow>();
        public TeamsLifecycle Lifecycle { get; set; } = new TeamsLifecycle();

        /// <summary>Whether the segment mix's Dormant share could be measured, and why not.</summary>
        public TeamsDormancy Dormancy { get; set; } = new TeamsDormancy();

        /// <summary>
        /// The people active in the period - the lifecycle's three counts together - or null when that query failed. Only
        /// decides what a reader without See PII is shown (<see cref="TeamsAdoptionRhythm.WithoutSmallGroups"/>), so it is
        /// never sent.
        /// </summary>
        [JsonIgnore]
        public int? PeriodActiveUsers { get; set; }

        /// <summary>
        /// The fewest people a figure of this section describes, for a reader without See PII; <c>null</c> when every group is
        /// shown. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// How many groups were folded into the <see cref="TeamsDemographicRow.Other"/> row - or, when even all of
        /// them together are too few people, left out of <see cref="Breakdown"/> altogether. 0 when nothing was.
        /// </summary>
        public int OtherGroups { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#661, #680, #844): no figure describes
        /// fewer than <paramref name="minimumPeople"/> people.
        /// <para>
        /// The breakdown: a group is too small when it has fewer people, or when fewer than that - but some - of them were
        /// active, since its messages and meetings per active user are then those few people's own. Small groups are folded
        /// into one unnamed row; when that row is itself too small, the smallest shown group goes into it too, so that it
        /// cannot be worked out as the difference either.
        /// </para>
        /// <para>
        /// The rest (#844, S4-1) must have been read over the breakdown's own population
        /// (<see cref="TeamsExplorerQuery.WithDirectoryPopulation"/>), so that a total here less the breakdown's rows never
        /// leaves a since-disabled account's activity. Then the rhythm (<see cref="TeamsAdoptionRhythm.WithoutSmallGroups"/>),
        /// the lifecycle (<see cref="TeamsLifecycle.WithoutSmallGroups"/>), the engagement mix
        /// (<see cref="TeamsSmallGroups.WithoutSmallSegments"/>) and its weekly trend
        /// (<see cref="TeamsSegmentTrendPoint.WithoutSmallSegments"/>), the people per platform
        /// (<see cref="TeamsSmallGroups.WithoutSmallPlatforms"/>) and the licensed people left out of the mix
        /// (<see cref="TeamsSmallGroups.WithoutFewUnmeasured"/>) withhold what 1 to 4 people are behind, and what would give it
        /// back.
        /// </para>
        /// A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public TeamsAdoption WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var shown = new List<TeamsDemographicRow>();
            var folded = new List<TeamsDemographicRow>();
            foreach (var row in Breakdown ?? new List<TeamsDemographicRow>())
            {
                if (IsSmall(row, minimumPeople)) folded.Add(row);
                else shown.Add(row);
            }

            var other = Combine(folded);
            while (folded.Count > 0 && IsSmall(other, minimumPeople))
            {
                // A shown group has the minimum or more people, and none or the minimum or more of them active. So
                // when the folded row is short of active people, only a group with active people can make it up.
                var candidates = other.ActiveUsers > 0 ? shown.Where(r => r.ActiveUsers > 0) : shown;
                var smallest = candidates
                    .OrderBy(r => r.KnownUsers)
                    .ThenBy(r => r.ActiveUsers)
                    .ThenBy(r => r.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (smallest == null) break;
                shown.Remove(smallest);
                folded.Add(smallest);
                other = Combine(folded);
            }

            // Still too few people when everything that could be folded has been: the row is left out.
            if (folded.Count > 0 && !IsSmall(other, minimumPeople)) shown.Add(other);

            var copy = (TeamsAdoption)MemberwiseClone();
            copy.Breakdown = shown;
            copy.MinimumGroupSize = minimumPeople;
            copy.OtherGroups = folded.Count;

            copy.Rhythm = (Rhythm ?? new TeamsAdoptionRhythm()).WithoutSmallGroups(minimumPeople, PeriodActiveUsers);
            copy.Lifecycle = (Lifecycle ?? new TeamsLifecycle()).WithoutSmallGroups(minimumPeople);
            copy.Dormancy = TeamsSmallGroups.WithoutFewUnmeasured(Dormancy, minimumPeople);
            copy.SegmentMix = TeamsSmallGroups.WithoutSmallSegments(SegmentMix, minimumPeople, hideShares: !ReferenceEquals(copy.Dormancy, Dormancy));
            copy.SegmentTrend = (SegmentTrend ?? new List<TeamsSegmentTrendPoint>()).Select(p => p.WithoutSmallSegments(minimumPeople)).ToList();
            copy.Devices = TeamsSmallGroups.WithoutSmallPlatforms(Devices, minimumPeople);
            return copy;
        }

        private static bool IsSmall(TeamsDemographicRow row, int minimumPeople) =>
            row.KnownUsers < minimumPeople || (row.ActiveUsers > 0 && row.ActiveUsers < minimumPeople);

        /// <summary>
        /// This section with its breakdown cut to the first <paramref name="top"/> named groups, keeping the combined row.
        /// For a reader without See PII, <see cref="WithoutSmallGroups"/> runs over every group (the query is read with
        /// <see cref="TeamsExplorerQuery.WithEveryRow"/>) and this then cuts the result to the rows the page asked for. When
        /// the fold ran over only the rows a reader-chosen <c>top</c> had returned, two requests one row apart folded
        /// different groups, and subtracting one combined row from the other recovered a small group. Cutting after the
        /// fold means nothing the reader can vary changes what was folded. A shallow copy, like WithoutSmallGroups.
        /// </summary>
        public TeamsAdoption WithFirstGroups(int top)
        {
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var rows = Breakdown ?? new List<TeamsDemographicRow>();
            var copy = (TeamsAdoption)MemberwiseClone();
            copy.Breakdown = rows.Where(r => !r.Other).Take(top).Concat(rows.Where(r => r.Other)).ToList();
            return copy;
        }

        private static TeamsDemographicRow Combine(List<TeamsDemographicRow> rows)
        {
            var known = rows.Sum(r => r.KnownUsers);
            var active = rows.Sum(r => r.ActiveUsers);
            // Each row's averages are over its own active users, so its totals are the average times those users.
            var messages = rows.Sum(r => r.MessagesPerActiveUser * r.ActiveUsers);
            var meetings = rows.Sum(r => r.MeetingsPerActiveUser * r.ActiveUsers);
            return new TeamsDemographicRow
            {
                Name = null,
                Other = true,
                KnownUsers = known,
                ActiveUsers = active,
                ReachPct = TeamsExplorerScoring.Percentage(active, known),
                MessagesPerActiveUser = active > 0 ? messages / active : 0,
                MeetingsPerActiveUser = active > 0 ? meetings / active : 0,
            };
        }
    }

    #endregion

    #region Meetings and calls

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsCallKpis
    {
        public long Calls { get; set; }
        public long GroupCalls { get; set; }
        public long PeerToPeerCalls { get; set; }

        /// <summary>Distinct people who joined at least one call.</summary>
        public int Attendees { get; set; }

        /// <summary>Total call wall-clock hours (call start to call end, counted once per call).</summary>
        public double CallHours { get; set; }

        /// <summary>Total attendee-hours - the real cost of the meeting load.</summary>
        public double AttendeeHours { get; set; }

        public double MeanDurationMinutes { get; set; }
        public double MeanAttendees { get; set; }

        /// <summary>Calls starting outside working hours or at the weekend, 0-100.</summary>
        public double AfterHoursPct { get; set; }

        /// <summary>Calls starting on a Saturday or Sunday, 0-100.</summary>
        public double WeekendPct { get; set; }

        /// <summary>Share of meetings organised by the busiest tenth of organisers, 0-100.</summary>
        public double OrganiserConcentrationPct { get; set; }

        /// <summary>
        /// Mean share of a call each attendee was actually present for, 0-100. Low values mean
        /// habitual late joining or early leaving.
        /// </summary>
        public double AttendeeEngagementPct { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsCallTrendPoint
    {
        public DateTime WeekStart { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public long? Calls { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public double? Minutes { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        public int? Attendees { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, the week's figures are null: one to four people attended its calls, or
        /// none was counted, so its calls and minutes are those few people's own - or it is the week that keeps such a week
        /// from being worked out from the totals (<see cref="TeamsSmallGroups.WithoutSmallCallWeeks"/>) (#844). Sent only when
        /// true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }

        /// <summary>True when the week had calls and fewer than <paramref name="minimumPeople"/> attendees were counted.</summary>
        internal bool DescribesFewerThan(int minimumPeople) => (Calls ?? 0) > 0 && (Attendees ?? 0) < minimumPeople;

        /// <summary>This week with its figures withheld. A new point: the trend is cached and shared between readers.</summary>
        internal TeamsCallTrendPoint AsWithheld() => new TeamsCallTrendPoint { WeekStart = WeekStart, Withheld = true };
    }

    /// <summary>One cell of the day-of-week x hour heatmap.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsHeatCell
    {
        /// <summary>0 = Monday ... 6 = Sunday. Computed with day arithmetic, so DATEFIRST-independent.</summary>
        public int DayOfWeek { get; set; }

        /// <summary>UTC hour, 0-23.</summary>
        public int Hour { get; set; }

        public long Calls { get; set; }
    }

    /// <summary>What the call-quality tables can say - which is often "nothing", honestly.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsCallQuality
    {
        /// <summary>Feedback rows in the window. Zero is the normal case in most tenants.</summary>
        public long FeedbackCount { get; set; }

        /// <summary>Failure rows in the window.</summary>
        public long FailureCount { get; set; }

        public List<TeamsBucketRow> Ratings { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsBucketRow> FailureReasons { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsBucketRow> FailureStages { get; set; } = new List<TeamsBucketRow>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsMeetings : TeamsExplorerSection
    {
        /// <summary>First hour of the assumed working day (UTC), so the UI can state the assumption.</summary>
        public int WorkingDayStartHour { get; set; }

        /// <summary>First hour after the assumed working day (UTC).</summary>
        public int WorkingDayEndHour { get; set; }

        public TeamsCallKpis Kpis { get; set; } = new TeamsCallKpis();
        public List<TeamsCallTrendPoint> Trend { get; set; } = new List<TeamsCallTrendPoint>();
        public List<TeamsBucketRow> SizeDistribution { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsBucketRow> DurationDistribution { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsHeatCell> Heatmap { get; set; } = new List<TeamsHeatCell>();
        public List<TeamsBucketRow> PeriodOfDay { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsBucketRow> ModalityMix { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsNamedCountRow> TopOrganisers { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsNamedCountRow> TopAttendees { get; set; } = new List<TeamsNamedCountRow>();
        public TeamsCallQuality Quality { get; set; } = new TeamsCallQuality();

        /// <summary>
        /// The fewest people a figure of this section describes, for a reader without See PII; <c>null</c> when nothing is
        /// held back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, the distributions - meeting size and length, the heatmap, time of day,
        /// the modalities and the call experience - are empty because fewer than <see cref="MinimumGroupSize"/> people
        /// attended the calls in view. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public bool DistributionsWithheld { get; set; }

        /// <summary>
        /// How many modalities were folded into the <see cref="TeamsBucketRow.Other"/> row of <see cref="ModalityMix"/> - or,
        /// when no modality had enough people, left out of it. 0 when none were.
        /// </summary>
        public int OtherModalities { get; set; }

        /// <summary>
        /// This section without the two leaderboards that name people, for a reader who does not hold the
        /// portal's See PII permission (#661). A shallow copy: the section is cached and shared between
        /// readers, so the instance itself must never be edited for one of them.
        /// </summary>
        public TeamsMeetings WithoutIndividualData()
        {
            var copy = (TeamsMeetings)MemberwiseClone();
            copy.TopOrganisers = new List<TeamsNamedCountRow>();
            copy.TopAttendees = new List<TeamsNamedCountRow>();
            return copy;
        }

        /// <summary>
        /// This section with no figure of fewer than <paramref name="minimumPeople"/> people, for a reader who does not hold
        /// the portal's See PII permission (#844). Applied where the data says how many people a figure describes:
        /// <list type="bullet">
        /// <item>a week of the trend with calls but fewer attendees than that is withheld, and with it the week with the
        /// fewest attendees still shown, so that the calls and call hours less the weeks shown are never one week's
        /// (<see cref="TeamsSmallGroups.WithoutSmallCallWeeks"/>);</item>
        /// <item>when fewer than that attended every call in view, the distributions are withheld, since each of their
        /// buckets describes some of those few people;</item>
        /// <item>modalities are counted in distinct people each, and those used by fewer are folded
        /// (<see cref="TeamsSmallGroups.FoldBuckets"/>).</item>
        /// </list>
        /// The meeting size and length buckets, the heatmap's cells and time of day carry no people count, so they are
        /// shown whenever the section as a whole describes enough people. A shallow copy: the section is cached and shared
        /// between readers.
        /// </summary>
        public TeamsMeetings WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var copy = (TeamsMeetings)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.Trend = TeamsSmallGroups.WithoutSmallCallWeeks(Trend, minimumPeople);

            if ((Kpis?.Calls ?? 0) > 0 && (Kpis?.Attendees ?? 0) < minimumPeople)
            {
                copy.DistributionsWithheld = true;
                copy.SizeDistribution = new List<TeamsBucketRow>();
                copy.DurationDistribution = new List<TeamsBucketRow>();
                copy.Heatmap = new List<TeamsHeatCell>();
                copy.PeriodOfDay = new List<TeamsBucketRow>();
                copy.ModalityMix = new List<TeamsBucketRow>();
                copy.OtherModalities = (ModalityMix ?? new List<TeamsBucketRow>()).Count;
                copy.Quality = new TeamsCallQuality();
                return copy;
            }

            copy.ModalityMix = TeamsSmallGroups.FoldBuckets(ModalityMix, minimumPeople, out var folded);
            copy.OtherModalities = folded;
            return copy;
        }
    }

    #endregion

    #region Collaboration

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsCollaborationKpis
    {
        public int TotalTeams { get; set; }
        public int ActiveTeams { get; set; }
        public int DormantTeams { get; set; }

        /// <summary>Teams with no row in <c>team_owners</c> - nobody can govern them.</summary>
        public int OwnerlessTeams { get; set; }

        /// <summary>Teams authorised for deep analytics, so channel content can be read at all.</summary>
        public int AuthorisedTeams { get; set; }

        public int TotalChannels { get; set; }
        public int ActiveChannels { get; set; }
        public long ChannelMessages { get; set; }
        public long Reactions { get; set; }
    }

    /// <summary>A team on the leaderboard.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsTeamRow
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Members { get; set; }
        public int Owners { get; set; }
        public int Channels { get; set; }
        public int Tabs { get; set; }
        public long Messages { get; set; }

        /// <summary>Null when <see cref="ReactionsWithheld"/>.</summary>
        public long? Reactions { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Reactions"/> is null: one to four people reacted in the
        /// team's channels, so the count is those few people's own - or it keeps such a count from being worked out from the
        /// tenant's reactions (<see cref="TeamsSmallGroups.ReactionsWithheld{T}"/>) (#844). Sent only when true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ReactionsWithheld { get; set; }

        /// <summary>Distinct people who reacted in the team's channels. Only decides what is withheld, so it is never sent.</summary>
        [JsonIgnore]
        public int ReactingUsers { get; set; }

        /// <summary>
        /// Chat-count weighted mean sentiment: 0 negative, 0.5 NEUTRAL, 1 positive. Null when
        /// cognitive services never scored this team. Not a percentage.
        /// </summary>
        public double? Sentiment { get; set; }

        /// <summary>Days in the window on which any channel in the team saw a message.</summary>
        public int ActiveDays { get; set; }

        /// <summary>False when the team has never been authorised, so silence means "not measured".</summary>
        public bool Authorised { get; set; }

        /// <summary>This row with its reactions withheld. A new row: the list is cached and shared between readers.</summary>
        internal TeamsTeamRow AsReactionsWithheld()
        {
            var copy = (TeamsTeamRow)MemberwiseClone();
            copy.Reactions = null;
            copy.ReactionsWithheld = true;
            return copy;
        }
    }

    /// <summary>A channel on the leaderboard.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsChannelRow
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string TeamName { get; set; }
        public int Tabs { get; set; }
        public long Messages { get; set; }

        /// <summary>Null when <see cref="ReactionsWithheld"/>.</summary>
        public long? Reactions { get; set; }

        /// <summary>
        /// Distinct people who reacted to something in the channel. Null when <see cref="ReactionsWithheld"/>.
        /// </summary>
        /// <remarks>
        /// Reactions are used because they are the only per-user, per-channel signal the schema
        /// actually stores. There is an unused <c>ChannelUserLog</c> entity that looks like it counts
        /// posters, but nothing creates or writes its table, so a "contributors" column would be a
        /// guaranteed query failure rather than a better measure.
        /// </remarks>
        public int? ReactingUsers { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Reactions"/> and <see cref="ReactingUsers"/> are null: one
        /// to four people reacted in the channel, so the count is those few people's own - or it keeps such a count from being
        /// worked out from the tenant's reactions (<see cref="TeamsSmallGroups.ReactionsWithheld{T}"/>) (#844). Sent only when
        /// true.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ReactionsWithheld { get; set; }

        /// <summary>
        /// Members of the channel's team at its latest membership snapshot of the last week (<see cref="TeamsExplorerSql.LatestTeamMembers"/>;
        /// 0 when there is none): the people a channel row describes. Only decides what a reader without See PII is shown, so it
        /// is never sent.
        /// </summary>
        [JsonIgnore]
        public int TeamMembers { get; set; }

        /// <summary>See <see cref="TeamsTeamRow.Sentiment"/> - 0.5 is neutral, not 50% positive.</summary>
        public double? Sentiment { get; set; }

        public int ActiveDays { get; set; }

        /// <summary>This row with its reactions withheld. A new row: the list is cached and shared between readers.</summary>
        internal TeamsChannelRow AsReactionsWithheld()
        {
            var copy = (TeamsChannelRow)MemberwiseClone();
            copy.Reactions = null;
            copy.ReactingUsers = null;
            copy.ReactionsWithheld = true;
            return copy;
        }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsCollaboration : TeamsExplorerSection
    {
        public TeamsCollaborationKpis Kpis { get; set; } = new TeamsCollaborationKpis();
        public List<TeamsTeamRow> Teams { get; set; } = new List<TeamsTeamRow>();
        public List<TeamsChannelRow> Channels { get; set; } = new List<TeamsChannelRow>();
        public List<TeamsNamedCountRow> OwnerlessTeams { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsNamedCountRow> DormantTeams { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsBucketRow> ReactionMix { get; set; } = new List<TeamsBucketRow>();
        public List<TeamsNamedCountRow> TabUsage { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsTrendPoint> MembershipTrend { get; set; } = new List<TeamsTrendPoint>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>Teams left out of <see cref="Teams"/> for a reader without See PII: fewer than the minimum members, or left out with them so that none can be worked out from the totals.</summary>
        public int WithheldTeams { get; set; }

        /// <summary>Channels left out of <see cref="Channels"/> for a reader without See PII: their team has fewer than the minimum members, or they are left out with such channels so that none can be worked out from the totals.</summary>
        public int WithheldChannels { get; set; }

        /// <summary>
        /// How many reactions were folded into the <see cref="TeamsBucketRow.Other"/> row of <see cref="ReactionMix"/> - or,
        /// when no reaction was used by enough people, left out of it. 0 when none were.
        /// </summary>
        public int OtherReactions { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="TabUsage"/> is empty. A tab's name can be whatever a member
        /// typed, and nothing stored says how many people pinned or use it.
        /// </summary>
        public bool TabUsageWithheld { get; set; }

        /// <summary>
        /// This section with no row of fewer than <paramref name="minimumPeople"/> people, for a reader who does not hold the
        /// portal's See PII permission (#844):
        /// <list type="bullet">
        /// <item>a team with fewer members is left out of the team leaderboard, and a channel whose team has fewer members
        /// out of the channel leaderboard - their messages, reactions and sentiment are those few people's own - with the
        /// smallest row shown that has messages, so that the channel messages published beside each list less its rows are
        /// never one small team's (<see cref="TeamsSmallGroups.WithoutSmallRowsOfList{T}"/>);</item>
        /// <item>a team's or channel's reactions are withheld when one to four people reacted, with whatever would give them
        /// back from the reactions published beside the list (<see cref="TeamsSmallGroups.ReactionsWithheld{T}"/>);</item>
        /// <item>reactions used by fewer people are folded (<see cref="TeamsSmallGroups.FoldBuckets"/>);</item>
        /// <item>the tab list is withheld, since nothing stored counts the people behind a tab.</item>
        /// </list>
        /// The two leaderboards are decided over the export-sized list, the rows beyond it counted as left out; the controller
        /// reads one row more than that (<see cref="TeamsExplorerQuery.WithListProbe"/>) and then cuts each to the rows asked for
        /// (<see cref="WithFirstRows"/>), so that what is left out never depends on the top asked for (#844, P4-1).
        /// A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public TeamsCollaboration WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var teams = Teams ?? new List<TeamsTeamRow>();
            var channels = Channels ?? new List<TeamsChannelRow>();
            var teamsCutShort = MayBeCutShort(teams.Count) || teams.Count > TeamsExplorerQuery.MaximumTop;
            var channelsCutShort = MayBeCutShort(channels.Count) || channels.Count > TeamsExplorerQuery.MaximumTop;

            var shownTeams = TeamsSmallGroups.WithoutSmallRowsOfList(
                teams, t => t.Members, t => t.Messages > 0, TeamKey, minimumPeople, teamsCutShort, out var withheldTeams);
            var teamReactions = TeamsSmallGroups.ReactionsWithheld(
                shownTeams, LeftOut(teams, shownTeams), t => t.ReactingUsers, TeamKey, minimumPeople, teamsCutShort);

            var shownChannels = TeamsSmallGroups.WithoutSmallRowsOfList(
                channels, c => c.TeamMembers, c => c.Messages > 0, ChannelKey, minimumPeople, channelsCutShort, out var withheldChannels);
            var channelReactions = TeamsSmallGroups.ReactionsWithheld(
                shownChannels, LeftOut(channels, shownChannels), c => c.ReactingUsers ?? 0, ChannelKey, minimumPeople, channelsCutShort);

            var copy = (TeamsCollaboration)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.Teams = shownTeams.Select(t => teamReactions.Contains(t) ? t.AsReactionsWithheld() : t).ToList();
            copy.WithheldTeams = withheldTeams;
            copy.Channels = shownChannels.Select(c => channelReactions.Contains(c) ? c.AsReactionsWithheld() : c).ToList();
            copy.WithheldChannels = withheldChannels;
            copy.ReactionMix = TeamsSmallGroups.FoldBuckets(ReactionMix, minimumPeople, out var folded);
            copy.OtherReactions = folded;
            copy.TabUsage = new List<TeamsNamedCountRow>();
            copy.TabUsageWithheld = true;
            return copy;
        }

        /// <summary>
        /// This section with each ranked list cut to its first <paramref name="top"/> rows: for a reader without See PII, after
        /// <see cref="WithoutSmallGroups"/> has decided what is left out over the export-sized list, so that a shorter answer is
        /// always the start of a longer one. A shallow copy.
        /// </summary>
        public TeamsCollaboration WithFirstRows(int top)
        {
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var copy = (TeamsCollaboration)MemberwiseClone();
            copy.Teams = (Teams ?? new List<TeamsTeamRow>()).Take(top).ToList();
            copy.Channels = (Channels ?? new List<TeamsChannelRow>()).Take(top).ToList();
            copy.OwnerlessTeams = (OwnerlessTeams ?? new List<TeamsNamedCountRow>()).Take(top).ToList();
            copy.DormantTeams = (DormantTeams ?? new List<TeamsNamedCountRow>()).Take(top).ToList();
            return copy;
        }

        private static string TeamKey(TeamsTeamRow team) =>
            (team.Name ?? string.Empty) + "\u0001" + team.Id.ToString("D10", CultureInfo.InvariantCulture);

        private static string ChannelKey(TeamsChannelRow channel) =>
            (channel.TeamName ?? string.Empty) + "\u0001" + (channel.Name ?? string.Empty) + "\u0001"
            + channel.Id.ToString("D10", CultureInfo.InvariantCulture);

        /// <summary>The export-sized list's rows that <paramref name="shown"/> does not hold.</summary>
        internal static List<T> LeftOut<T>(IEnumerable<T> rows, IEnumerable<T> shown)
            where T : class
        {
            var kept = new HashSet<T>(shown);
            return rows.Take(TeamsExplorerQuery.MaximumTop).Where(r => !kept.Contains(r)).ToList();
        }
    }

    #endregion

    #region Conversations

    /// <summary>One week of channel sentiment.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSentimentPoint
    {
        public DateTime WeekStart { get; set; }

        /// <summary>Chat-count weighted mean, 0-1, where 0.5 is neutral. Null for an unscored week.</summary>
        public double? Sentiment { get; set; }

        public long Messages { get; set; }
    }

    /// <summary>Sentiment for one team or channel.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSentimentRow
    {
        public string Name { get; set; }
        public double Sentiment { get; set; }
        public long Messages { get; set; }

        /// <summary>
        /// Members of the team (for a channel, of its team) at its latest membership snapshot of the last week
        /// (<see cref="TeamsExplorerSql.LatestTeamMembers"/>; 0 when there is none): the people the row describes. Only decides
        /// what a reader without See PII is shown, so it is never sent.
        /// </summary>
        [JsonIgnore]
        public int Members { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsConversations : TeamsExplorerSection
    {
        /// <summary>False when cognitive services are not configured, so nothing here can exist.</summary>
        public bool CognitiveAvailable { get; set; }

        /// <summary>Channel-day rows that carry a sentiment score. Zero means "enabled but nothing scored yet".</summary>
        public long ScoredChannelDays { get; set; }

        public List<TeamsNamedCountRow> Keywords { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsNamedCountRow> Languages { get; set; } = new List<TeamsNamedCountRow>();
        public List<TeamsSentimentPoint> SentimentTrend { get; set; } = new List<TeamsSentimentPoint>();
        public List<TeamsSentimentRow> SentimentByTeam { get; set; } = new List<TeamsSentimentRow>();
        public List<TeamsSentimentRow> SentimentByChannel { get; set; } = new List<TeamsSentimentRow>();

        /// <summary>
        /// The fewest people a row of this section describes, for a reader without See PII; <c>null</c> when nothing is held
        /// back for the reader. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Keywords"/> is empty: key phrases are text taken from what
        /// people wrote, and nothing stored says how many people wrote each one.
        /// </summary>
        public bool KeywordsWithheld { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Languages"/> is empty: nothing stored says how many people
        /// wrote in each language, so a language one person writes in would be that person's channel activity.
        /// </summary>
        public bool LanguagesWithheld { get; set; }

        /// <summary>Teams left out of <see cref="SentimentByTeam"/> for a reader without See PII: fewer than the minimum members, or left out with them so that none can be worked out from the totals.</summary>
        public int WithheldSentimentTeams { get; set; }

        /// <summary>Channels left out of <see cref="SentimentByChannel"/> for a reader without See PII: their team has fewer than the minimum members, or they are left out with such channels so that none can be worked out from the totals.</summary>
        public int WithheldSentimentChannels { get; set; }

        /// <summary>
        /// This section with no row of fewer than <paramref name="minimumPeople"/> people, for a reader who does not hold the
        /// portal's See PII permission (#844). The key phrases and languages are withheld, because the stored scores count
        /// mentions and channel-days, never the people behind them; a team's or channel's sentiment is left out when the
        /// team has fewer members, since its sentiment is then those few people's mood - and so is the smallest row still
        /// shown, so that the weekly messages less the rows shown are never one small team's
        /// (<see cref="TeamsSmallGroups.WithoutSmallRowsOfList{T}"/>). Both lists are decided over the export-sized list, the
        /// rows beyond it counted as left out, and only then cut to the rows asked for (<see cref="WithFirstRows"/>; #844, P4-1).
        /// The weekly sentiment is the whole tenant's and stays. A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public TeamsConversations WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var byTeam = SentimentByTeam ?? new List<TeamsSentimentRow>();
            var byChannel = SentimentByChannel ?? new List<TeamsSentimentRow>();

            var copy = (TeamsConversations)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.Keywords = new List<TeamsNamedCountRow>();
            copy.KeywordsWithheld = true;
            copy.Languages = new List<TeamsNamedCountRow>();
            copy.LanguagesWithheld = true;
            copy.SentimentByTeam = TeamsSmallGroups.WithoutSmallRowsOfList(
                byTeam, r => r.Members, r => r.Messages > 0, r => r.Name, minimumPeople, MayBeCutShort(byTeam.Count), out var withheldTeams);
            copy.WithheldSentimentTeams = withheldTeams;
            copy.SentimentByChannel = TeamsSmallGroups.WithoutSmallRowsOfList(
                byChannel, r => r.Members, r => r.Messages > 0, r => r.Name, minimumPeople, MayBeCutShort(byChannel.Count), out var withheldChannels);
            copy.WithheldSentimentChannels = withheldChannels;
            return copy;
        }

        /// <summary>
        /// This section with the sentiment per team and per channel cut to their first <paramref name="top"/> rows: for a reader
        /// without See PII, after <see cref="WithoutSmallGroups"/> has decided what is left out over the export-sized list. A
        /// shallow copy.
        /// </summary>
        public TeamsConversations WithFirstRows(int top)
        {
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var copy = (TeamsConversations)MemberwiseClone();
            copy.SentimentByTeam = (SentimentByTeam ?? new List<TeamsSentimentRow>()).Take(top).ToList();
            copy.SentimentByChannel = (SentimentByChannel ?? new List<TeamsSentimentRow>()).Take(top).ToList();
            return copy;
        }
    }

    #endregion

    #region People

    /// <summary>One person on a leaderboard.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsPersonRow
    {
        public string UserPrincipalName { get; set; }
        public string Department { get; set; }
        public int ActiveDays { get; set; }
        public long ChannelMessages { get; set; }
        public long PrivateMessages { get; set; }
        public long MeetingsOrganised { get; set; }
        public long MeetingsAttended { get; set; }
        public long CallsHosted { get; set; }
        public long CallsAttended { get; set; }

        /// <summary>The segment this person's active-day count puts them in.</summary>
        public string Segment { get; set; }

        /// <summary>Last date Graph saw any Teams activity for this person.</summary>
        public DateTime? LastActivity { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsPeople : TeamsExplorerSection
    {
        /// <summary>
        /// True when the usage reports appear to carry obfuscated user names, so the leaderboards
        /// cannot name anyone. Set from the data, not from configuration - the tenant setting is not
        /// readable from here.
        /// </summary>
        public bool NamesObfuscated { get; set; }

        public List<TeamsPersonRow> Champions { get; set; } = new List<TeamsPersonRow>();
        public List<TeamsPersonRow> Dormant { get; set; } = new List<TeamsPersonRow>();
        public List<TeamsNamedCountRow> ChampionsByDepartment { get; set; } = new List<TeamsNamedCountRow>();

        /// <summary>
        /// Whether <see cref="Dormant"/> can include licensed people with no Teams activity at all, and why not.
        /// An aggregate, so it stays for a reader without See PII.
        /// </summary>
        public TeamsDormancy Dormancy { get; set; } = new TeamsDormancy();

        /// <summary>
        /// The fewest power users a <see cref="ChampionsByDepartment"/> row counts, for a reader without See PII;
        /// <c>null</c> when every department is shown. See <see cref="WithoutIndividualData"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// How many departments were folded into the <see cref="TeamsNamedCountRow.Other"/> row of
        /// <see cref="ChampionsByDepartment"/> - or, when even all of them together are too few people, left out. 0 when
        /// none were.
        /// </summary>
        public int OtherChampionDepartments { get; set; }

        /// <summary>
        /// For a reader without See PII, the engagement mix the Overview and Adoption tabs publish over the same people
        /// (<see cref="TeamsExplorerSql.SegmentHistogram"/>): the departments add up to its Power segment, so they must not
        /// give back a Power segment those tabs withhold (<see cref="WithoutIndividualData"/>). Null when it was not read;
        /// empty when reading it failed. Only decides what is withheld, so it is never sent.
        /// </summary>
        [JsonIgnore]
        public List<TeamsSegmentSlice> SegmentMix { get; set; }

        /// <summary>
        /// This section without the two lists that name people, for a reader who does not hold the portal's
        /// See PII permission (#661). The power users per department stay - they are a count, and every reader sees
        /// aggregates - except that a department with fewer than <paramref name="minimumPeople"/> of them is folded
        /// into one unnamed row (#680): in a small department, a count of power users says who they are. When that row
        /// is itself too few people, the smallest named department goes into it too, so that it cannot be worked
        /// out as the difference either. A shallow copy, because the section is cached and shared between readers.
        /// </summary>
        /// <remarks>
        /// The departments add up to the Power segment the Overview and Adoption tabs publish, so they must be every
        /// department: the controller reads them all, over the same population as that segment
        /// (<see cref="TeamsExplorerQuery.WithEveryGroup"/>, <see cref="TeamsExplorerSql.JoinedUserPopulation"/>), and only
        /// then cuts the named ones to the top asked for (<see cref="WithFirstDepartments"/>; #844, P4-1). Their total must not
        /// be published when those tabs withhold the Power segment - with a Regular or Light segment of 1 to 4 people
        /// (<see cref="TeamsSmallGroups.WithoutSmallSegments"/>), or when this section could not tell (<see cref="SegmentMix"/>)
        /// - nor when the list still filled the rows it was read with (<see cref="TeamsExplorerSection.ReadTop"/>), since the
        /// departments beyond it are unread. Then the folded departments are left out with no combined row - the smallest named
        /// department too, when they are too few people - and so is every share, which would give the total away: the Power
        /// segment less the departments shown is a group of at least <paramref name="minimumPeople"/>, never one known figure.
        /// </remarks>
        public TeamsPeople WithoutIndividualData(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var rows = ChampionsByDepartment ?? new List<TeamsNamedCountRow>();
            var totalWithheld = MayBeCutShort(rows.Count) || PowerSegmentWithheld(minimumPeople);
            var total = rows.Sum(r => r.Count);
            var shown = rows.Where(r => r.Count >= minimumPeople).ToList();
            var folded = rows.Where(r => r.Count < minimumPeople).ToList();
            // A named row counts the minimum or more, so one is always enough. A total withheld needs one too.
            if ((folded.Count > 0 || totalWithheld) && folded.Sum(r => r.Count) < minimumPeople && shown.Count > 0)
            {
                var smallest = shown.OrderBy(r => r.Count).ThenBy(r => r.Name, StringComparer.Ordinal).First();
                shown.Remove(smallest);
                folded.Add(smallest);
            }

            var otherCount = folded.Sum(r => r.Count);
            if (totalWithheld)
            {
                shown = shown.Select(r => new TeamsNamedCountRow { Name = r.Name, Count = r.Count }).ToList();
            }
            else if (folded.Count > 0 && otherCount >= minimumPeople)
            {
                shown.Add(new TeamsNamedCountRow
                {
                    Name = null,
                    Other = true,
                    Count = otherCount,
                    SharePct = total > 0 ? TeamsExplorerScoring.Percentage(otherCount, total) : (double?)null,
                });
            }

            var copy = (TeamsPeople)MemberwiseClone();
            copy.Champions = new List<TeamsPersonRow>();
            copy.Dormant = new List<TeamsPersonRow>();
            copy.ChampionsByDepartment = shown;
            copy.MinimumGroupSize = minimumPeople;
            copy.OtherChampionDepartments = folded.Count;
            return copy;
        }

        /// <summary>
        /// True when the Overview and Adoption tabs withhold, from a reader without See PII, the Power segment these
        /// departments add up to (<see cref="TeamsSmallGroups.WithoutSmallSegments"/>), or when <see cref="SegmentMix"/> was
        /// read and failed, so nothing says they show it. False when it was not read: a reader with See PII.
        /// </summary>
        internal bool PowerSegmentWithheld(int minimumPeople)
        {
            if (SegmentMix == null) return false;
            if (SegmentMix.Count == 0) return true;

            var power = TeamsUserSegment.Power.ToString();
            return TeamsSmallGroups.WithoutSmallSegments(SegmentMix, minimumPeople, hideShares: false)
                .Any(s => s.Segment == power && s.Withheld);
        }

        /// <summary>
        /// This section with its departments cut to the first <paramref name="top"/> named ones, keeping the combined row.
        /// Applied after <see cref="WithoutIndividualData"/> has folded every department the query can return, for the
        /// reason <see cref="TeamsAdoption.WithFirstGroups"/> gives. A shallow copy.
        /// </summary>
        public TeamsPeople WithFirstDepartments(int top)
        {
            if (top < 1) throw new ArgumentOutOfRangeException(nameof(top));

            var rows = ChampionsByDepartment ?? new List<TeamsNamedCountRow>();
            var copy = (TeamsPeople)MemberwiseClone();
            copy.ChampionsByDepartment = rows.Where(r => !r.Other).Take(top).Concat(rows.Where(r => r.Other)).ToList();
            return copy;
        }
    }

    #endregion
}
