using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

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

        /// <summary>The innermost error message when the section failed; null when it succeeded.</summary>
        public string Error { get; set; }

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
        public string Name { get; set; }
        public long Count { get; set; }

        /// <summary>Share of the section's total, 0-100. Null where a share is meaningless.</summary>
        public double? SharePct { get; set; }
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
    }

    #region Overview

    /// <summary>The Overview tab's headline figures.</summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsOverviewKpis
    {
        /// <summary>Directory users the reach percentage is measured against.</summary>
        public int KnownUsers { get; set; }

        /// <summary>Distinct users with any Teams activity in the usage-report window.</summary>
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
        public string Name { get; set; }
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
        public long Calls { get; set; }
        public double Minutes { get; set; }
        public int Attendees { get; set; }
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
        public long Reactions { get; set; }

        /// <summary>
        /// Chat-count weighted mean sentiment: 0 negative, 0.5 NEUTRAL, 1 positive. Null when
        /// cognitive services never scored this team. Not a percentage.
        /// </summary>
        public double? Sentiment { get; set; }

        /// <summary>Days in the window on which any channel in the team saw a message.</summary>
        public int ActiveDays { get; set; }

        /// <summary>False when the team has never been authorised, so silence means "not measured".</summary>
        public bool Authorised { get; set; }
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
        public long Reactions { get; set; }

        /// <summary>
        /// Distinct people who reacted to something in the channel.
        /// </summary>
        /// <remarks>
        /// Reactions are used because they are the only per-user, per-channel signal the schema
        /// actually stores. There is an unused <c>ChannelUserLog</c> entity that looks like it counts
        /// posters, but nothing creates or writes its table, so a "contributors" column would be a
        /// guaranteed query failure rather than a better measure.
        /// </remarks>
        public int ReactingUsers { get; set; }

        /// <summary>See <see cref="TeamsTeamRow.Sentiment"/> - 0.5 is neutral, not 50% positive.</summary>
        public double? Sentiment { get; set; }

        public int ActiveDays { get; set; }
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
    }

    #endregion
}
