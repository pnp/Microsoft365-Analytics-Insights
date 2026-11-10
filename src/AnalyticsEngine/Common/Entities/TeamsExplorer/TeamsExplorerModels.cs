using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
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
    }

    #region Overview

    /// <summary>The Overview tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsOverviewKpis
    {
        /// <summary>Directory users the reach percentage is measured against.</summary>
        public int KnownUsers { get; set; }

        /// <summary>
        /// Enabled (or unknown) directory users with any Teams activity in the usage-report window - the same
        /// population as <see cref="KnownUsers"/>, so reach can never count a since-disabled account on one side
        /// only.
        /// </summary>
        public int ActiveUsers { get; set; }

        public double ReachPct { get; set; }

        /// <summary>Messages posted in channels (posts + replies + team chat).</summary>
        public long ChannelMessages { get; set; }

        /// <summary>Messages sent in private chats.</summary>
        public long PrivateMessages { get; set; }

        /// <summary>Channel share of all chat messages, 0-100.</summary>
        public double OpenCollaborationPct { get; set; }

        public long MeetingsAttended { get; set; }
        public long MeetingsOrganised { get; set; }
        public double MeetingsPerActiveUser { get; set; }

        /// <summary>
        /// Hours of audio in calls. Audio is used as the wall-clock proxy on purpose: audio, video and
        /// screenshare durations OVERLAP inside a single meeting, so adding them together would
        /// produce a number larger than the meeting itself.
        /// </summary>
        public double AudioHours { get; set; }

        /// <summary>Video hours as a share of audio hours, 0-100.</summary>
        public double VideoSharePct { get; set; }

        /// <summary>Screenshare hours as a share of audio hours, 0-100.</summary>
        public double ScreenShareSharePct { get; set; }

        /// <summary>Calls recorded by the call-records webhook in the live window.</summary>
        public long Calls { get; set; }

        public int TotalTeams { get; set; }
        public int ActiveTeams { get; set; }
        public int TotalChannels { get; set; }
        public int ActiveChannels { get; set; }
    }

    /// <summary>One week of the Overview trend.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsTrendPoint
    {
        public DateTime WeekStart { get; set; }
        public int ActiveUsers { get; set; }
        public long ChannelMessages { get; set; }
        public long PrivateMessages { get; set; }
        public long MeetingsAttended { get; set; }
        public long Calls { get; set; }
    }

    /// <summary>One slice of the engagement mix.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSegmentSlice
    {
        public string Segment { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public int Users { get; set; }
        public double SharePct { get; set; }
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
        /// <see cref="Known"/> is false. Always 0 when it is true, and on the People tab.
        /// </summary>
        public int UnmeasuredUsers { get; set; }

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
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsAdoptionRhythm
    {
        /// <summary>Mean daily active users across the window's working days.</summary>
        public double MeanDailyActiveUsers { get; set; }

        /// <summary>Distinct active users in the last 7 days of the usage window.</summary>
        public int WeeklyActiveUsers { get; set; }

        /// <summary>Distinct active users in the last 28 days of the usage window.</summary>
        public int MonthlyActiveUsers { get; set; }

        /// <summary>
        /// Mean DAU as a share of MAU, 0-100. The standard "stickiness" measure: 100% would mean
        /// everyone who uses Teams at all uses it every working day.
        /// </summary>
        public double StickinessPct { get; set; }
    }

    /// <summary>One week's segment counts, for the segment-mix trend.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsSegmentTrendPoint
    {
        public DateTime WeekStart { get; set; }
        public int Power { get; set; }
        public int Regular { get; set; }
        public int Light { get; set; }
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
        public string Platform { get; set; }
        public int Users { get; set; }

        /// <summary>Share of users with any device row, 0-100. Shares sum above 100: people use several platforms.</summary>
        public double SharePct { get; set; }
    }

    /// <summary>Movement in and out of Teams use across the window.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsLifecycle
    {
        /// <summary>Active in the second half of the window but not the first - newly on board.</summary>
        public int NewUsers { get; set; }

        /// <summary>Active in both halves.</summary>
        public int ReturningUsers { get; set; }

        /// <summary>Active in the first half but not the second - the ones to worry about.</summary>
        public int LapsedUsers { get; set; }
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
        /// The fewest people a breakdown row describes, for a reader without See PII; <c>null</c> when every group is
        /// shown. See <see cref="WithoutSmallGroups"/>.
        /// </summary>
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// How many groups were folded into the <see cref="TeamsDemographicRow.Other"/> row - or, when even all of
        /// them together are too few people, left out of <see cref="Breakdown"/> altogether. 0 when nothing was.
        /// </summary>
        public int OtherGroups { get; set; }

        /// <summary>
        /// This section for a reader who does not hold the portal's See PII permission (#661, #680): no breakdown row
        /// describes fewer than <paramref name="minimumPeople"/> people. A group is too small when it has fewer people,
        /// or when fewer than that - but some - of them were active, since its messages and meetings per active user
        /// are then those few people's own. Small groups are folded into one unnamed row; when that row is itself too
        /// small, the smallest shown group goes into it too, so that it cannot be worked out as the difference
        /// either. A shallow copy: the section is cached and shared between readers.
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
        /// none was counted, so its calls and minutes are those few people's own (#844). Sent only when true.
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
        /// <item>a week of the trend with calls but fewer attendees than that is withheld;</item>
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
            copy.Trend = (Trend ?? new List<TeamsCallTrendPoint>())
                .Select(p => p.DescribesFewerThan(minimumPeople) ? p.AsWithheld() : p)
                .ToList();

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
        /// team's channels, so the count is those few people's own (#844). Sent only when true.
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

        /// <summary>This row with its reactions withheld when one to four people are behind them. A new row when it changes.</summary>
        internal TeamsTeamRow WithoutFewReactors(int minimumPeople)
        {
            if (ReactingUsers <= 0 || ReactingUsers >= minimumPeople) return this;
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
        /// to four people reacted in the channel, so the count is those few people's own (#844). Sent only when true.
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

        /// <summary>This row with its reactions withheld when one to four people are behind them. A new row when it changes.</summary>
        internal TeamsChannelRow WithoutFewReactors(int minimumPeople)
        {
            var reacting = ReactingUsers ?? 0;
            if (reacting <= 0 || reacting >= minimumPeople) return this;
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

        /// <summary>Teams left out of <see cref="Teams"/> for a reader without See PII: fewer than the minimum members.</summary>
        public int WithheldTeams { get; set; }

        /// <summary>Channels left out of <see cref="Channels"/> for a reader without See PII: their team has fewer than the minimum members.</summary>
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
        /// out of the channel leaderboard - their messages, reactions and sentiment are those few people's own;</item>
        /// <item>a team's or channel's reactions are withheld when one to four people reacted;</item>
        /// <item>reactions used by fewer people are folded (<see cref="TeamsSmallGroups.FoldBuckets"/>);</item>
        /// <item>the tab list is withheld, since nothing stored counts the people behind a tab.</item>
        /// </list>
        /// A shallow copy: the section is cached and shared between readers.
        /// </summary>
        public TeamsCollaboration WithoutSmallGroups(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var teams = Teams ?? new List<TeamsTeamRow>();
            var channels = Channels ?? new List<TeamsChannelRow>();

            var copy = (TeamsCollaboration)MemberwiseClone();
            copy.MinimumGroupSize = minimumPeople;
            copy.Teams = teams.Where(t => t.Members >= minimumPeople).Select(t => t.WithoutFewReactors(minimumPeople)).ToList();
            copy.WithheldTeams = teams.Count - copy.Teams.Count;
            copy.Channels = channels.Where(c => c.TeamMembers >= minimumPeople).Select(c => c.WithoutFewReactors(minimumPeople)).ToList();
            copy.WithheldChannels = channels.Count - copy.Channels.Count;
            copy.ReactionMix = TeamsSmallGroups.FoldBuckets(ReactionMix, minimumPeople, out var folded);
            copy.OtherReactions = folded;
            copy.TabUsage = new List<TeamsNamedCountRow>();
            copy.TabUsageWithheld = true;
            return copy;
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

        /// <summary>Teams left out of <see cref="SentimentByTeam"/> for a reader without See PII: fewer than the minimum members.</summary>
        public int WithheldSentimentTeams { get; set; }

        /// <summary>Channels left out of <see cref="SentimentByChannel"/> for a reader without See PII: their team has fewer than the minimum members.</summary>
        public int WithheldSentimentChannels { get; set; }

        /// <summary>
        /// This section with no row of fewer than <paramref name="minimumPeople"/> people, for a reader who does not hold the
        /// portal's See PII permission (#844). The key phrases and languages are withheld, because the stored scores count
        /// mentions and channel-days, never the people behind them; a team's or channel's sentiment is left out when the
        /// team has fewer members, since its sentiment is then those few people's mood. The weekly sentiment is the whole
        /// tenant's and stays. A shallow copy: the section is cached and shared between readers.
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
            copy.SentimentByTeam = byTeam.Where(r => r.Members >= minimumPeople).ToList();
            copy.WithheldSentimentTeams = byTeam.Count - copy.SentimentByTeam.Count;
            copy.SentimentByChannel = byChannel.Where(r => r.Members >= minimumPeople).ToList();
            copy.WithheldSentimentChannels = byChannel.Count - copy.SentimentByChannel.Count;
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
        /// This section without the two lists that name people, for a reader who does not hold the portal's
        /// See PII permission (#661). The power users per department stay - they are a count, and every reader sees
        /// aggregates - except that a department with fewer than <paramref name="minimumPeople"/> of them is folded
        /// into one unnamed row (#680): in a small department, a count of power users says who they are. When that row
        /// is itself too few people, the smallest named department goes into it too, so that it cannot be worked
        /// out as the difference either. A shallow copy, because the section is cached and shared between readers.
        /// </summary>
        public TeamsPeople WithoutIndividualData(int minimumPeople)
        {
            if (minimumPeople < 1) throw new ArgumentOutOfRangeException(nameof(minimumPeople));

            var rows = ChampionsByDepartment ?? new List<TeamsNamedCountRow>();
            var total = rows.Sum(r => r.Count);
            var shown = rows.Where(r => r.Count >= minimumPeople).ToList();
            var folded = rows.Where(r => r.Count < minimumPeople).ToList();
            // A named row counts the minimum or more, so one is always enough.
            if (folded.Count > 0 && folded.Sum(r => r.Count) < minimumPeople && shown.Count > 0)
            {
                var smallest = shown.OrderBy(r => r.Count).ThenBy(r => r.Name, StringComparer.Ordinal).First();
                shown.Remove(smallest);
                folded.Add(smallest);
            }

            var otherCount = folded.Sum(r => r.Count);
            if (folded.Count > 0 && otherCount >= minimumPeople)
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
