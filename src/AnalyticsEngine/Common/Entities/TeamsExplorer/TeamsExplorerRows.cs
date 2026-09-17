using System;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// The shapes EF materialises the raw Teams Explorer queries into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept separate from the API models on purpose. These mirror the SQL result sets exactly -
    /// including the aggregate types, which matters: EF's store-query shaper reads a column with a
    /// typed getter, so a <c>COUNT_BIG</c> (bigint) read into an <c>int</c> property throws at
    /// runtime rather than converting. Every property type here is chosen to match the SQL type the
    /// corresponding statement in <see cref="TeamsExplorerSql"/> actually returns.
    /// </para>
    /// <para>
    /// They are public because EF6's <c>Database.SqlQuery&lt;T&gt;</c> materialises by reflection over
    /// public settable properties.
    /// </para>
    /// </remarks>
    public static class TeamsExplorerRows
    {
        public class OverviewUsageRow
        {
            public int KnownUsers { get; set; }
            public int ActiveUsers { get; set; }

            /// <summary>Users the usage report covered at all - the denominator of the segment mix.</summary>
            public int MeasuredUsers { get; set; }

            public long ChannelMessages { get; set; }
            public long PrivateMessages { get; set; }
            public long MeetingsAttended { get; set; }
            public long MeetingsOrganised { get; set; }
            public long AudioSeconds { get; set; }
            public long VideoSeconds { get; set; }
            public long ScreenShareSeconds { get; set; }
        }

        public class CallCountRow
        {
            public long Calls { get; set; }
            public int OutOfHoursCalls { get; set; }
        }

        public class UsageTrendRow
        {
            public DateTime WeekStart { get; set; }
            public int ActiveUsers { get; set; }
            public long ChannelMessages { get; set; }
            public long PrivateMessages { get; set; }
            public long MeetingsAttended { get; set; }
        }

        public class CallTrendRow
        {
            public DateTime WeekStart { get; set; }
            public long Calls { get; set; }
            public double Minutes { get; set; }
            public int Attendees { get; set; }
        }

        /// <summary>
        /// Weekly call counts only. A separate type because EF's store-query shaper requires every
        /// property to have a matching column, so <see cref="CallTrendRow"/> cannot be reused for the
        /// lighter Overview query.
        /// </summary>
        public class WeekCallCountRow
        {
            public DateTime WeekStart { get; set; }
            public long Calls { get; set; }
        }

        public class ActiveDayHistogramRow
        {
            public int ActiveDays { get; set; }
            public long Users { get; set; }
        }

        public class WeeklyActiveDayHistogramRow
        {
            public DateTime WeekStart { get; set; }
            public int ActiveDays { get; set; }
            public long Users { get; set; }
        }

        public class RhythmRow
        {
            public double MeanDailyActiveUsers { get; set; }
            public int WeeklyActiveUsers { get; set; }
            public int MonthlyActiveUsers { get; set; }
        }

        public class BreakdownRow
        {
            public string Name { get; set; }
            public int KnownUsers { get; set; }
            public int ActiveUsers { get; set; }
            public long Messages { get; set; }
            public long Meetings { get; set; }
        }

        public class DevicesRow
        {
            public int MeasuredUsers { get; set; }
            public int UsedWindows { get; set; }
            public int UsedMac { get; set; }
            public int UsedWeb { get; set; }
            public int UsedIos { get; set; }
            public int UsedAndroid { get; set; }
            public int UsedLinux { get; set; }
            public int UsedChromeOs { get; set; }
            public int UsedMobile { get; set; }
        }

        public class LifecycleRow
        {
            public int NewUsers { get; set; }
            public int ReturningUsers { get; set; }
            public int LapsedUsers { get; set; }
        }

        public class CallKpiRow
        {
            public long Calls { get; set; }
            public int GroupCalls { get; set; }
            public int PeerToPeerCalls { get; set; }
            public long CallSeconds { get; set; }
            public long AttendeeSeconds { get; set; }
            public double MeanAttendees { get; set; }
            public int OutOfHoursCalls { get; set; }
            public int WeekendCalls { get; set; }
            public int DistinctAttendees { get; set; }

            /// <summary>Null when no call in the window had a measurable duration.</summary>
            public double? AttendeeEngagement { get; set; }
        }

        public class OrganiserVolumeRow
        {
            public int OrganiserId { get; set; }
            public long Calls { get; set; }
        }

        public class CallSizeRow
        {
            public long Attendees { get; set; }
            public long Calls { get; set; }
        }

        public class CallDurationRow
        {
            public int Minutes { get; set; }
            public long Calls { get; set; }
        }

        public class HeatRow
        {
            public int DayOfWeek { get; set; }
            public int Hour { get; set; }
            public long Calls { get; set; }
        }

        /// <summary>A name and a bigint count - the shape most ranked queries return.</summary>
        public class NameCountRow
        {
            public string Name { get; set; }
            public long Count { get; set; }
        }

        public class FeedbackRatingRow
        {
            public string Rating { get; set; }
            public long Feedback { get; set; }
        }

        public class FailureRow
        {
            public string Reason { get; set; }
            public string Stage { get; set; }
            public long Failures { get; set; }
        }

        public class CollaborationKpiRow
        {
            public int TotalTeams { get; set; }
            public int AuthorisedTeams { get; set; }
            public int TotalChannels { get; set; }
            public int OwnerlessTeams { get; set; }
            public int ActiveTeams { get; set; }
            public int ActiveChannels { get; set; }
            public long ChannelMessages { get; set; }
            public long Reactions { get; set; }
        }

        public class TeamLeaderboardRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int Members { get; set; }
            public int Owners { get; set; }
            public int Channels { get; set; }
            public int Tabs { get; set; }
            public long Messages { get; set; }
            public long Reactions { get; set; }
            public double? Sentiment { get; set; }
            public int ActiveDays { get; set; }

            /// <summary>1 when the team has a delegated token, so silence means "quiet" not "unmeasured".</summary>
            public int Authorised { get; set; }
        }

        public class ChannelLeaderboardRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string TeamName { get; set; }
            public int Tabs { get; set; }
            public long Messages { get; set; }
            public long Reactions { get; set; }

            /// <summary>Distinct people who reacted in the channel. See the note on the query.</summary>
            public int ReactingUsers { get; set; }

            public double? Sentiment { get; set; }
            public int ActiveDays { get; set; }
        }

        public class MembershipTrendRow
        {
            public DateTime WeekStart { get; set; }
            public int ActiveUsers { get; set; }
        }

        public class SentimentTrendRow
        {
            public DateTime WeekStart { get; set; }
            public double? Sentiment { get; set; }
            public long Messages { get; set; }
        }

        public class SentimentRow
        {
            public string Name { get; set; }
            public double Sentiment { get; set; }
            public long Messages { get; set; }
        }

        public class ScoredChannelDaysRow
        {
            public long ScoredChannelDays { get; set; }
        }

        public class PersonRow
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
            public DateTime? LastActivity { get; set; }
        }

        public class ObfuscationProbeRow
        {
            public int Sampled { get; set; }
            public int WithAddress { get; set; }
        }
    }
}
