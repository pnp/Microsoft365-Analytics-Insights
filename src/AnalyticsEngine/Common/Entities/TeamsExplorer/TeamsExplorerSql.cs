using System;
using System.Collections.Generic;
using System.Globalization;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// Every SQL statement the Teams Explorer runs, as constants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Base tables only.</b> None of these read the <c>vwTeams*</c> views. Those views are what the
    /// archived Power BI report was built on and they are being removed - they join through
    /// <c>dimDate</c>, materialise whole tables and do not scale past a small tenant. Every aggregate
    /// here is computed in SQL against the base tables, and nothing materialises a per-user or
    /// per-event row set into memory.
    /// </para>
    /// <para>
    /// <b>One parameter set for every query.</b> All statements are executed with the same list of
    /// parameters even when they use only a few of them. SQL Server accepts declared-but-unused
    /// parameters, and a single shared list is the only way to guarantee the SQL shown in the UI's
    /// popover declares exactly what the executed command supplied - a per-query list drifts the
    /// moment someone edits one and not the other.
    /// </para>
    /// <para>
    /// <b>Thresholds live in C#, not here.</b> Queries return raw distributions (active-day counts,
    /// attendee counts) and <see cref="TeamsExplorerScoring"/> turns them into segments and bands. If
    /// the segment boundaries were written into these statements they would exist in two places and
    /// could not be unit-tested.
    /// </para>
    /// <para>
    /// <b>Day arithmetic, never DATEPART(WEEKDAY).</b> 1900-01-01 was a Monday, so
    /// <c>DATEDIFF(DAY, 0, col) % 7</c> is 0 on Mondays and 5/6 at the weekend regardless of the
    /// connection's <c>DATEFIRST</c> or language. <c>DATEPART(WEEKDAY, ...)</c> is session-dependent
    /// and would silently produce different answers for different callers.
    /// </para>
    /// </remarks>
    public static class TeamsExplorerSql
    {
        /// <summary>
        /// Per-query command timeout. A single slow aggregate would otherwise run until Azure App
        /// Service kills the request (~230s) and return a 500; capping it degrades one section to an
        /// error message instead.
        /// </summary>
        public const int CommandTimeoutSeconds = 25;

        /// <summary>
        /// A user did something in Teams on a given day. Summed rather than tested column-by-column
        /// because Graph writes a row for EVERY user in the report scope - including users who did
        /// nothing at all - so the presence of a row means "measured", not "active".
        /// </summary>
        /// <remarks>
        /// Six of these eight columns are carried by <c>NCCI_teams_user_activity_log_metrics</c>;
        /// <c>calls_count</c> and <c>urgent_messages</c> are not, which is why the migration
        /// <c>WidenTeamsUsageColumnstore</c> exists. They are included anyway because a user whose only
        /// Teams activity was a 1:1 call genuinely is an active user, and quietly excluding them to
        /// suit an index would make the headline reach figure wrong.
        /// </remarks>
        public const string ActivitySum =
            "(t.private_chat_count + t.team_chat_count + t.post_messages + t.reply_messages"
            + " + t.meetings_attended_count + t.meetings_organized_count + t.calls_count + t.urgent_messages)";

        /// <summary>Messages posted where the whole team can see them.</summary>
        public const string ChannelMessageSum = "(t.team_chat_count + t.post_messages + t.reply_messages)";

        /// <summary>
        /// Directory users the reach percentage is measured against. Users with an unknown
        /// <c>account_enabled</c> are counted: treating unknown as disabled would silently shrink the
        /// denominator and inflate reach.
        /// </summary>
        public const string EnabledUsersPredicate = "(u.account_enabled IS NULL OR u.account_enabled = 1)";

        /// <summary>Buckets a datetime column to the Monday on or before it.</summary>
        public static string WeekBucket(string column)
        {
            return $"DATEADD(DAY, -(DATEDIFF(DAY, 0, {column}) % 7), CAST({column} AS date))";
        }

        /// <summary>Day of week as 0 = Monday ... 6 = Sunday, independent of DATEFIRST.</summary>
        public static string DayOfWeek(string column)
        {
            return $"(DATEDIFF(DAY, 0, {column}) % 7)";
        }

        /// <summary>True when a call started outside the assumed working window, or at the weekend.</summary>
        public static string OutOfHours(string column)
        {
            return $"(CASE WHEN DATEPART(HOUR, {column}) < @workStart OR DATEPART(HOUR, {column}) >= @workEnd"
                + $" OR {DayOfWeek(column)} >= 5 THEN 1 ELSE 0 END)";
        }

        #region Overview

        /// <summary>Tenant-wide usage-report totals and the active/known user counts behind reach.</summary>
        public const string OverviewUsage = @"
WITH PerUser AS (
    SELECT
        t.user_id,
        SUM(CASE WHEN " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS ActiveDays,
        SUM(CAST(" + ChannelMessageSum + @" AS bigint))          AS ChannelMessages,
        SUM(CAST(t.private_chat_count AS bigint))                AS PrivateMessages,
        SUM(CAST(t.meetings_attended_count AS bigint))           AS MeetingsAttended,
        SUM(CAST(t.meetings_organized_count AS bigint))          AS MeetingsOrganised,
        SUM(CAST(t.audio_duration_seconds AS bigint))            AS AudioSeconds,
        SUM(CAST(t.video_duration_seconds AS bigint))            AS VideoSeconds,
        SUM(CAST(t.screenshare_duration_seconds AS bigint))      AS ScreenShareSeconds
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT
    (SELECT COUNT(*) FROM dbo.users AS u WHERE " + EnabledUsersPredicate + @") AS KnownUsers,
    ISNULL(SUM(CASE WHEN p.ActiveDays > 0 THEN 1 ELSE 0 END), 0) AS ActiveUsers,
    COUNT(*)                                          AS MeasuredUsers,
    ISNULL(SUM(p.ChannelMessages), 0)                 AS ChannelMessages,
    ISNULL(SUM(p.PrivateMessages), 0)                 AS PrivateMessages,
    ISNULL(SUM(p.MeetingsAttended), 0)                AS MeetingsAttended,
    ISNULL(SUM(p.MeetingsOrganised), 0)               AS MeetingsOrganised,
    ISNULL(SUM(p.AudioSeconds), 0)                    AS AudioSeconds,
    ISNULL(SUM(p.VideoSeconds), 0)                    AS VideoSeconds,
    ISNULL(SUM(p.ScreenShareSeconds), 0)              AS ScreenShareSeconds
FROM PerUser AS p
OPTION (RECOMPILE);";

        /// <summary>
        /// Call volume and out-of-hours share in the live window. Separate from the usage figures
        /// because calls have no report lag, and deliberately much lighter than
        /// <see cref="CallKpis"/> - it never touches <c>call_sessions</c>, so it is a single seek of
        /// <c>IX_call_records_start</c> and is cheap enough to run on the landing tab.
        /// </summary>
        public static string OverviewCalls()
        {
            return $@"
SELECT
    COUNT_BIG(*)                                  AS Calls,
    ISNULL(SUM({OutOfHours("c.[start]")}), 0)     AS OutOfHoursCalls
FROM dbo.call_records AS c
WHERE c.[start] >= @from AND c.[start] < @to
OPTION (RECOMPILE);";
        }

        /// <summary>Weekly active users and message volume, for the Overview trend.</summary>
        public static string OverviewTrend()
        {
            var week = WeekBucket("t.[date]");

            return $@"
SELECT
    {week} AS WeekStart,
    COUNT(DISTINCT CASE WHEN {ActivitySum} > 0 THEN t.user_id END) AS ActiveUsers,
    ISNULL(SUM(CAST({ChannelMessageSum} AS bigint)), 0)            AS ChannelMessages,
    ISNULL(SUM(CAST(t.private_chat_count AS bigint)), 0)           AS PrivateMessages,
    ISNULL(SUM(CAST(t.meetings_attended_count AS bigint)), 0)      AS MeetingsAttended
FROM dbo.teams_user_activity_log AS t
WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
GROUP BY {week}
ORDER BY WeekStart
OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Weekly call counts for the Overview trend.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <see cref="CallTrend"/>, which also reports minutes and
        /// distinct attendees and therefore has to join <c>call_sessions</c>. The landing tab only
        /// needs the count, and that is a single seek of <c>IX_call_records_start</c>.
        /// </remarks>
        public static string OverviewCallTrend()
        {
            var week = WeekBucket("c.[start]");

            return $@"
SELECT {week} AS WeekStart, COUNT_BIG(*) AS Calls
FROM dbo.call_records AS c
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY {week}
ORDER BY WeekStart
OPTION (RECOMPILE);";
        }

        /// <summary>
        /// How many users were active on how many days. Returned as a raw histogram so
        /// <see cref="TeamsExplorerScoring.Segment"/> - and not this SQL - decides the bands.
        /// </summary>
        public const string SegmentHistogram = @"
WITH PerUser AS (
    SELECT t.user_id, SUM(CASE WHEN " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS ActiveDays
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT p.ActiveDays, COUNT_BIG(*) AS Users
FROM PerUser AS p
GROUP BY p.ActiveDays
ORDER BY p.ActiveDays
OPTION (RECOMPILE);";

        #endregion

        #region Adoption

        /// <summary>Mean DAU across working days, plus WAU and MAU at the end of the usage window.</summary>
        public static string AdoptionRhythm()
        {
            var dow = DayOfWeek("d.D");

            return $@"
WITH Daily AS (
    SELECT t.[date] AS D, COUNT(DISTINCT CASE WHEN {ActivitySum} > 0 THEN t.user_id END) AS Dau
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.[date]
)
SELECT
    (SELECT ISNULL(AVG(CAST(d.Dau AS float)), 0) FROM Daily AS d WHERE {dow} < 5) AS MeanDailyActiveUsers,
    (
        SELECT COUNT(DISTINCT t.user_id)
        FROM dbo.teams_user_activity_log AS t
        WHERE t.[date] >= @wauFrom AND t.[date] < @usageTo AND {ActivitySum} > 0
    ) AS WeeklyActiveUsers,
    (
        SELECT COUNT(DISTINCT t.user_id)
        FROM dbo.teams_user_activity_log AS t
        WHERE t.[date] >= @mauFrom AND t.[date] < @usageTo AND {ActivitySum} > 0
    ) AS MonthlyActiveUsers
OPTION (RECOMPILE);";
        }

        /// <summary>Per-week active-day histogram, so the segment mix can be charted over time.</summary>
        public static string SegmentTrendHistogram()
        {
            var week = WeekBucket("t.[date]");

            return $@"
WITH PerUserWeek AS (
    SELECT {week} AS WeekStart, t.user_id,
           SUM(CASE WHEN {ActivitySum} > 0 THEN 1 ELSE 0 END) AS ActiveDays
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY {week}, t.user_id
)
SELECT p.WeekStart, p.ActiveDays, COUNT_BIG(*) AS Users
FROM PerUserWeek AS p
WHERE p.ActiveDays > 0
GROUP BY p.WeekStart, p.ActiveDays
ORDER BY p.WeekStart, p.ActiveDays
OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Adoption broken down by one demographic dimension. The dimension is resolved from a
        /// whitelist by <see cref="DemographicJoin"/>, so there is no injection surface.
        /// </summary>
        public static string AdoptionBreakdown(string groupBy)
        {
            var join = DemographicJoin(groupBy);

            return $@"
WITH PerUser AS (
    SELECT
        t.user_id,
        SUM(CASE WHEN {ActivitySum} > 0 THEN 1 ELSE 0 END)   AS ActiveDays,
        SUM(CAST({ChannelMessageSum} + t.private_chat_count AS bigint)) AS Messages,
        SUM(CAST(t.meetings_attended_count AS bigint))       AS Meetings
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT TOP (@top)
    ISNULL(dim.[name], N'(not set)')                              AS Name,
    COUNT(DISTINCT u.id)                                          AS KnownUsers,
    COUNT(DISTINCT CASE WHEN p.ActiveDays > 0 THEN u.id END)      AS ActiveUsers,
    ISNULL(SUM(p.Messages), 0)                                    AS Messages,
    ISNULL(SUM(p.Meetings), 0)                                    AS Meetings
FROM dbo.users AS u
LEFT JOIN {join.Table} AS dim ON dim.id = u.{join.ForeignKey}
LEFT JOIN PerUser AS p ON p.user_id = u.id
WHERE {EnabledUsersPredicate}
GROUP BY ISNULL(dim.[name], N'(not set)')
ORDER BY ActiveUsers DESC, KnownUsers DESC
OPTION (RECOMPILE);";
        }

        /// <summary>Distinct users seen on each Teams client platform in the window.</summary>
        public const string AdoptionDevices = @"
WITH PerUser AS (
    SELECT
        d.user_id,
        MAX(CASE WHEN d.used_windows   = 1 THEN 1 ELSE 0 END) AS UsedWindows,
        MAX(CASE WHEN d.used_mac       = 1 THEN 1 ELSE 0 END) AS UsedMac,
        MAX(CASE WHEN d.used_web       = 1 THEN 1 ELSE 0 END) AS UsedWeb,
        MAX(CASE WHEN d.used_ios       = 1 THEN 1 ELSE 0 END) AS UsedIos,
        MAX(CASE WHEN d.used_android   = 1 THEN 1 ELSE 0 END) AS UsedAndroid,
        MAX(CASE WHEN d.used_linux     = 1 THEN 1 ELSE 0 END) AS UsedLinux,
        MAX(CASE WHEN d.used_chrome_os = 1 THEN 1 ELSE 0 END) AS UsedChromeOs
    FROM dbo.teams_user_device_usage_log AS d
    WHERE d.[date] >= @usageFrom AND d.[date] < @usageTo
    GROUP BY d.user_id
)
SELECT
    COUNT(*)                        AS MeasuredUsers,
    ISNULL(SUM(UsedWindows), 0)     AS UsedWindows,
    ISNULL(SUM(UsedMac), 0)         AS UsedMac,
    ISNULL(SUM(UsedWeb), 0)         AS UsedWeb,
    ISNULL(SUM(UsedIos), 0)         AS UsedIos,
    ISNULL(SUM(UsedAndroid), 0)     AS UsedAndroid,
    ISNULL(SUM(UsedLinux), 0)       AS UsedLinux,
    ISNULL(SUM(UsedChromeOs), 0)    AS UsedChromeOs,
    ISNULL(SUM(CASE WHEN UsedIos = 1 OR UsedAndroid = 1 THEN 1 ELSE 0 END), 0) AS UsedMobile
FROM PerUser
OPTION (RECOMPILE);";

        /// <summary>Who started, kept going or stopped using Teams across the two halves of the window.</summary>
        public const string AdoptionLifecycle = @"
WITH PerUser AS (
    SELECT
        t.user_id,
        MAX(CASE WHEN t.[date] <  @midpoint AND " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS FirstHalf,
        MAX(CASE WHEN t.[date] >= @midpoint AND " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS SecondHalf
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT
    ISNULL(SUM(CASE WHEN FirstHalf = 0 AND SecondHalf = 1 THEN 1 ELSE 0 END), 0) AS NewUsers,
    ISNULL(SUM(CASE WHEN FirstHalf = 1 AND SecondHalf = 1 THEN 1 ELSE 0 END), 0) AS ReturningUsers,
    ISNULL(SUM(CASE WHEN FirstHalf = 1 AND SecondHalf = 0 THEN 1 ELSE 0 END), 0) AS LapsedUsers
FROM PerUser
OPTION (RECOMPILE);";

        #endregion

        #region Meetings and calls

        /// <summary>Headline call figures, including the out-of-hours and attendee-engagement measures.</summary>
        public static string CallKpis()
        {
            return $@"
WITH Calls AS (
    SELECT c.id, c.call_type_id, c.[start],
           CAST(DATEDIFF(SECOND, c.[start], c.[end]) AS bigint) AS DurationSeconds,
           {OutOfHours("c.[start]")} AS OutOfHours,
           CASE WHEN {DayOfWeek("c.[start]")} >= 5 THEN 1 ELSE 0 END AS Weekend
    FROM dbo.call_records AS c
    WHERE c.[start] >= @from AND c.[start] < @to
),
PerCall AS (
    SELECT
        k.id, k.call_type_id, k.DurationSeconds, k.OutOfHours, k.Weekend,
        ISNULL(s.Attendees, 0)       AS Attendees,
        ISNULL(s.AttendeeSeconds, 0) AS AttendeeSeconds
    FROM Calls AS k
    LEFT JOIN (
        SELECT s.call_record_id,
               COUNT_BIG(*) AS Attendees,
               SUM(CAST(DATEDIFF(SECOND, s.[start], s.[end]) AS bigint)) AS AttendeeSeconds
        FROM dbo.call_sessions AS s
        INNER JOIN Calls AS k2 ON k2.id = s.call_record_id
        GROUP BY s.call_record_id
    ) AS s ON s.call_record_id = k.id
)
SELECT
    COUNT_BIG(*)                                                       AS Calls,
    ISNULL(SUM(CASE WHEN ct.[name] LIKE N'group%' THEN 1 ELSE 0 END), 0)      AS GroupCalls,
    ISNULL(SUM(CASE WHEN ct.[name] LIKE N'peer%'  THEN 1 ELSE 0 END), 0)      AS PeerToPeerCalls,
    ISNULL(SUM(p.DurationSeconds), 0)                                  AS CallSeconds,
    ISNULL(SUM(p.AttendeeSeconds), 0)                                  AS AttendeeSeconds,
    ISNULL(AVG(CAST(p.Attendees AS float)), 0)                         AS MeanAttendees,
    ISNULL(SUM(p.OutOfHours), 0)                                       AS OutOfHoursCalls,
    ISNULL(SUM(p.Weekend), 0)                                          AS WeekendCalls,
    (
        SELECT COUNT(DISTINCT s.attendee_user_id)
        FROM dbo.call_sessions AS s
        INNER JOIN dbo.call_records AS c2 ON c2.id = s.call_record_id
        WHERE c2.[start] >= @from AND c2.[start] < @to
    ) AS DistinctAttendees,
    (
        SELECT AVG(Engagement) FROM (
            SELECT CASE
                       WHEN DATEDIFF(SECOND, c3.[start], c3.[end]) <= 0 THEN NULL
                       ELSE CASE
                                WHEN CAST(DATEDIFF(SECOND, s.[start], s.[end]) AS float)
                                     / DATEDIFF(SECOND, c3.[start], c3.[end]) > 1.0 THEN 1.0
                                ELSE CAST(DATEDIFF(SECOND, s.[start], s.[end]) AS float)
                                     / DATEDIFF(SECOND, c3.[start], c3.[end])
                            END
                   END AS Engagement
            FROM dbo.call_sessions AS s
            INNER JOIN dbo.call_records AS c3 ON c3.id = s.call_record_id
            WHERE c3.[start] >= @from AND c3.[start] < @to
        ) AS e
    ) AS AttendeeEngagement
FROM PerCall AS p
LEFT JOIN dbo.call_types AS ct ON ct.id = p.call_type_id
OPTION (RECOMPILE);";
        }

        /// <summary>Meetings organised per organiser, so the concentration measure can be computed in C#.</summary>
        public const string CallOrganiserVolumes = @"
SELECT c.organizer_id AS OrganiserId, COUNT_BIG(*) AS Calls
FROM dbo.call_records AS c
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY c.organizer_id
OPTION (RECOMPILE);";

        /// <summary>Weekly call volume, minutes and distinct attendees.</summary>
        /// <remarks>
        /// The calls and the attendees are aggregated in separate CTEs and joined, rather than counted
        /// off one joined row set. Joining sessions first would multiply each call by its attendee
        /// count, so the duration sum would count a ten-person meeting ten times - and
        /// <c>SUM(DISTINCT ...)</c> does not fix that, it silently discards two different calls that
        /// happened to last the same number of seconds.
        /// </remarks>
        public static string CallTrend()
        {
            var week = WeekBucket("c.[start]");

            return $@"
WITH Calls AS (
    SELECT c.id, {week} AS WeekStart,
           CAST(DATEDIFF(SECOND, c.[start], c.[end]) AS bigint) AS Seconds
    FROM dbo.call_records AS c
    WHERE c.[start] >= @from AND c.[start] < @to
),
Weekly AS (
    SELECT k.WeekStart, COUNT_BIG(*) AS Calls, SUM(k.Seconds) AS Seconds
    FROM Calls AS k
    GROUP BY k.WeekStart
),
WeeklyAttendees AS (
    SELECT k.WeekStart, COUNT(DISTINCT s.attendee_user_id) AS Attendees
    FROM Calls AS k
    INNER JOIN dbo.call_sessions AS s ON s.call_record_id = k.id
    GROUP BY k.WeekStart
)
SELECT w.WeekStart, w.Calls, CAST(w.Seconds AS float) / 60.0 AS Minutes, ISNULL(a.Attendees, 0) AS Attendees
FROM Weekly AS w
LEFT JOIN WeeklyAttendees AS a ON a.WeekStart = w.WeekStart
ORDER BY w.WeekStart
OPTION (RECOMPILE);";
        }

        /// <summary>Attendees per call, as a raw count so the buckets stay in C#.</summary>
        public const string CallSizes = @"
WITH PerCall AS (
    SELECT c.id, (SELECT COUNT_BIG(*) FROM dbo.call_sessions AS s WHERE s.call_record_id = c.id) AS Attendees
    FROM dbo.call_records AS c
    WHERE c.[start] >= @from AND c.[start] < @to
)
SELECT p.Attendees, COUNT_BIG(*) AS Calls
FROM PerCall AS p
GROUP BY p.Attendees
ORDER BY p.Attendees
OPTION (RECOMPILE);";

        /// <summary>Call durations in whole minutes, bucketed in C#.</summary>
        public const string CallDurations = @"
SELECT
    CASE
        WHEN DATEDIFF(SECOND, c.[start], c.[end]) < 0 THEN 0
        ELSE DATEDIFF(SECOND, c.[start], c.[end]) / 60
    END AS Minutes,
    COUNT_BIG(*) AS Calls
FROM dbo.call_records AS c
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY CASE
        WHEN DATEDIFF(SECOND, c.[start], c.[end]) < 0 THEN 0
        ELSE DATEDIFF(SECOND, c.[start], c.[end]) / 60
    END
ORDER BY Minutes
OPTION (RECOMPILE);";

        /// <summary>Calls by day of week and UTC hour, for the pattern heatmap.</summary>
        public static string CallHeatmap()
        {
            var dow = DayOfWeek("c.[start]");

            return $@"
SELECT {dow} AS DayOfWeek, DATEPART(HOUR, c.[start]) AS [Hour], COUNT_BIG(*) AS Calls
FROM dbo.call_records AS c
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY {dow}, DATEPART(HOUR, c.[start])
ORDER BY DayOfWeek, [Hour]
OPTION (RECOMPILE);";
        }

        /// <summary>Which modalities calls actually used - is video and sharing being exploited at all?</summary>
        public const string CallModalities = @"
SELECT m.[name] AS Name, COUNT_BIG(DISTINCT l.call_session_id) AS [Count]
FROM dbo.call_session_call_modalities AS l
INNER JOIN dbo.call_modalities AS m ON m.id = l.call_modality_id
INNER JOIN dbo.call_sessions AS s ON s.id = l.call_session_id
INNER JOIN dbo.call_records AS c ON c.id = s.call_record_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY m.[name]
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>People who organise the most meetings.</summary>
        public const string TopOrganisers = @"
SELECT TOP (@top) u.user_name AS Name, COUNT_BIG(*) AS [Count]
FROM dbo.call_records AS c
INNER JOIN dbo.users AS u ON u.id = c.organizer_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY u.user_name
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>People who attend the most meetings.</summary>
        public const string TopAttendees = @"
SELECT TOP (@top) u.user_name AS Name, COUNT_BIG(DISTINCT s.call_record_id) AS [Count]
FROM dbo.call_sessions AS s
INNER JOIN dbo.call_records AS c ON c.id = s.call_record_id
INNER JOIN dbo.users AS u ON u.id = s.attendee_user_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY u.user_name
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>
        /// Call ratings left by users. Almost always empty: Graph only carries feedback a participant
        /// actually submitted, and most never do.
        /// </summary>
        public const string CallFeedbackRatings = @"
SELECT ISNULL(NULLIF(LTRIM(RTRIM(f.rating)), N''), N'(none)') AS Rating, COUNT_BIG(*) AS Feedback
FROM dbo.call_feedback AS f
INNER JOIN dbo.call_records AS c ON c.id = f.call_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(f.rating)), N''), N'(none)')
ORDER BY Feedback DESC
OPTION (RECOMPILE);";

        /// <summary>Why calls failed, and at which stage.</summary>
        public const string CallFailures = @"
SELECT
    ISNULL(NULLIF(LTRIM(RTRIM(fa.reason)), N''), N'(not stated)') AS Reason,
    ISNULL(NULLIF(LTRIM(RTRIM(fa.stage)),  N''), N'(not stated)') AS Stage,
    COUNT_BIG(*) AS Failures
FROM dbo.call_failures AS fa
INNER JOIN dbo.call_records AS c ON c.id = fa.call_id
WHERE c.[start] >= @from AND c.[start] < @to
GROUP BY
    ISNULL(NULLIF(LTRIM(RTRIM(fa.reason)), N''), N'(not stated)'),
    ISNULL(NULLIF(LTRIM(RTRIM(fa.stage)),  N''), N'(not stated)')
ORDER BY Failures DESC
OPTION (RECOMPILE);";

        #endregion

        #region Collaboration

        /// <summary>Governance headline: how many teams and channels exist, and how many are alive.</summary>
        public const string CollaborationKpis = @"
SELECT
    (SELECT COUNT(*) FROM dbo.teams)                                  AS TotalTeams,
    (SELECT COUNT(*) FROM dbo.teams WHERE has_refresh_token = 1)      AS AuthorisedTeams,
    (SELECT COUNT(*) FROM dbo.teams_channels)                         AS TotalChannels,
    (
        SELECT COUNT(*) FROM dbo.teams AS t
        WHERE NOT EXISTS (SELECT 1 FROM dbo.team_owners AS o WHERE o.team_id = t.id)
    ) AS OwnerlessTeams,
    (
        SELECT COUNT(DISTINCT ch.team_id)
        FROM dbo.teams_channel_stats_log AS s
        INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
        WHERE s.[date] >= @from AND s.[date] < @to AND ISNULL(s.chats_count, 0) > 0
    ) AS ActiveTeams,
    (
        SELECT COUNT(DISTINCT s.channel_id)
        FROM dbo.teams_channel_stats_log AS s
        WHERE s.[date] >= @from AND s.[date] < @to AND ISNULL(s.chats_count, 0) > 0
    ) AS ActiveChannels,
    (
        SELECT ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)
        FROM dbo.teams_channel_stats_log AS s
        WHERE s.[date] >= @from AND s.[date] < @to
    ) AS ChannelMessages,
    (
        SELECT COUNT_BIG(*) FROM dbo.teams_user_channel_reactions AS r
        WHERE r.[date] >= @from AND r.[date] < @to
    ) AS Reactions
OPTION (RECOMPILE);";

        /// <summary>
        /// The team leaderboard. Membership is taken from the most recent membership log date per team
        /// rather than from every row, because <c>team_membership_log</c> is an append-only history and
        /// counting all of it would count everyone who has ever been a member.
        /// </summary>
        public const string TeamLeaderboard = @"
WITH LatestMembership AS (
    SELECT m.team_id, MAX(m.[date]) AS LatestDate
    FROM dbo.team_membership_log AS m
    GROUP BY m.team_id
),
Members AS (
    SELECT m.team_id, COUNT(DISTINCT m.user_id) AS Members
    FROM dbo.team_membership_log AS m
    INNER JOIN LatestMembership AS l ON l.team_id = m.team_id AND l.LatestDate = m.[date]
    GROUP BY m.team_id
),
Stats AS (
    SELECT ch.team_id,
           SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)) AS Messages,
           -- COUNT(DISTINCT date), not SUM(1): the source row is per CHANNEL per date, so summing a
           -- flag while grouping by team counts channel-days. A team with ten busy channels would
           -- report 300 active days in a 30-day window - a figure that cannot exist.
           COUNT(DISTINCT CASE WHEN ISNULL(s.chats_count, 0) > 0 THEN s.[date] END) AS MessageDays,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE s.sentiment_score * ISNULL(s.chats_count, 0) END) AS SentimentWeight,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE CAST(ISNULL(s.chats_count, 0) AS bigint) END)     AS ScoredMessages
    FROM dbo.teams_channel_stats_log AS s
    INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
    WHERE s.[date] >= @from AND s.[date] < @to
    GROUP BY ch.team_id
),
Reactions AS (
    SELECT ch.team_id, COUNT_BIG(*) AS Reactions
    FROM dbo.teams_user_channel_reactions AS r
    INNER JOIN dbo.teams_channels AS ch ON ch.id = r.channel_id
    WHERE r.[date] >= @from AND r.[date] < @to
    GROUP BY ch.team_id
),
Tabs AS (
    SELECT ch.team_id, COUNT(DISTINCT tl.tab_id) AS Tabs
    FROM dbo.teams_channel_tabs_log AS tl
    INNER JOIN dbo.teams_channels AS ch ON ch.id = tl.channel_id
    WHERE tl.[date] >= @from AND tl.[date] < @to
    GROUP BY ch.team_id
)
SELECT TOP (@top)
    t.id                                            AS Id,
    t.[name]                                        AS Name,
    ISNULL(mem.Members, 0)                          AS Members,
    (SELECT COUNT(*) FROM dbo.team_owners AS o WHERE o.team_id = t.id)      AS Owners,
    (SELECT COUNT(*) FROM dbo.teams_channels AS c WHERE c.team_id = t.id)   AS Channels,
    ISNULL(tab.Tabs, 0)                             AS Tabs,
    ISNULL(st.Messages, 0)                          AS Messages,
    ISNULL(re.Reactions, 0)                         AS Reactions,
    CASE WHEN ISNULL(st.ScoredMessages, 0) > 0
         THEN st.SentimentWeight / st.ScoredMessages END AS Sentiment,
    ISNULL(st.MessageDays, 0)                       AS ActiveDays,
    CAST(t.has_refresh_token AS int)                AS Authorised
FROM dbo.teams AS t
LEFT JOIN Members   AS mem ON mem.team_id = t.id
LEFT JOIN Stats     AS st  ON st.team_id  = t.id
LEFT JOIN Reactions AS re  ON re.team_id  = t.id
LEFT JOIN Tabs      AS tab ON tab.team_id = t.id
ORDER BY ISNULL(st.Messages, 0) DESC, ISNULL(re.Reactions, 0) DESC, t.[name]
OPTION (RECOMPILE);";

        /// <summary>The channel leaderboard.</summary>
        /// <remarks>
        /// There is deliberately no "contributors" column. The only per-user, per-channel entity in
        /// the codebase (<c>ChannelUserLog</c> / <c>teams_channel_user_log</c>) is dead: it has no
        /// <c>DbSet</c>, no migration creates it, and nothing writes to it - so querying it would
        /// fail with <c>Invalid object name</c> on every customer database. Message and reaction
        /// counts are what the schema can actually support.
        /// </remarks>
        public const string ChannelLeaderboard = @"
WITH Stats AS (
    SELECT s.channel_id,
           SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)) AS Messages,
           SUM(CASE WHEN ISNULL(s.chats_count, 0) > 0 THEN 1 ELSE 0 END) AS MessageDays,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE s.sentiment_score * ISNULL(s.chats_count, 0) END) AS SentimentWeight,
           SUM(CASE WHEN s.sentiment_score IS NULL THEN 0
                    ELSE CAST(ISNULL(s.chats_count, 0) AS bigint) END)     AS ScoredMessages
    FROM dbo.teams_channel_stats_log AS s
    WHERE s.[date] >= @from AND s.[date] < @to
    GROUP BY s.channel_id
),
Reactions AS (
    SELECT r.channel_id,
           COUNT_BIG(*) AS Reactions,
           COUNT(DISTINCT r.user_id) AS ReactingUsers
    FROM dbo.teams_user_channel_reactions AS r
    WHERE r.[date] >= @from AND r.[date] < @to
    GROUP BY r.channel_id
),
Tabs AS (
    SELECT tl.channel_id, COUNT(DISTINCT tl.tab_id) AS Tabs
    FROM dbo.teams_channel_tabs_log AS tl
    WHERE tl.[date] >= @from AND tl.[date] < @to
    GROUP BY tl.channel_id
)
SELECT TOP (@top)
    ch.id                       AS Id,
    ch.[name]                   AS Name,
    t.[name]                    AS TeamName,
    ISNULL(tab.Tabs, 0)         AS Tabs,
    ISNULL(st.Messages, 0)      AS Messages,
    ISNULL(re.Reactions, 0)     AS Reactions,
    ISNULL(re.ReactingUsers, 0) AS ReactingUsers,
    CASE WHEN ISNULL(st.ScoredMessages, 0) > 0
         THEN st.SentimentWeight / st.ScoredMessages END AS Sentiment,
    ISNULL(st.MessageDays, 0)   AS ActiveDays
FROM dbo.teams_channels AS ch
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
LEFT JOIN Stats     AS st  ON st.channel_id = ch.id
LEFT JOIN Reactions AS re  ON re.channel_id = ch.id
LEFT JOIN Tabs      AS tab ON tab.channel_id = ch.id
ORDER BY ISNULL(st.Messages, 0) DESC, ISNULL(re.Reactions, 0) DESC, ch.[name]
OPTION (RECOMPILE);";

        /// <summary>Teams nobody owns - the governance finding that matters most.</summary>
        public const string OwnerlessTeams = @"
SELECT TOP (@top) t.[name] AS Name, CAST(DATEDIFF(DAY, t.discovered, @to) AS bigint) AS [Count]
FROM dbo.teams AS t
WHERE NOT EXISTS (SELECT 1 FROM dbo.team_owners AS o WHERE o.team_id = t.id)
ORDER BY t.discovered
OPTION (RECOMPILE);";

        /// <summary>
        /// Authorised teams that saw no channel message in the window. Restricted to authorised teams
        /// on purpose: an unauthorised team is not silent, it is unmeasured, and listing it as dormant
        /// would send an admin to archive a team that may be perfectly busy.
        /// </summary>
        public const string DormantTeams = @"
SELECT TOP (@top) t.[name] AS Name,
       CAST(ISNULL((SELECT COUNT(*) FROM dbo.teams_channels AS c WHERE c.team_id = t.id), 0) AS bigint) AS [Count]
FROM dbo.teams AS t
WHERE t.has_refresh_token = 1
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.teams_channel_stats_log AS s
        INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
        WHERE ch.team_id = t.id AND s.[date] >= @from AND s.[date] < @to AND ISNULL(s.chats_count, 0) > 0
      )
ORDER BY [Count] DESC, t.[name]
OPTION (RECOMPILE);";

        /// <summary>Which reactions people use - a cheap but genuine engagement signal.</summary>
        public const string ReactionMix = @"
SELECT ISNULL(rt.[name], N'(unknown)') AS Name, COUNT_BIG(*) AS [Count]
FROM dbo.teams_user_channel_reactions AS r
LEFT JOIN dbo.teams_reaction_types AS rt ON rt.id = r.reaction_id
WHERE r.[date] >= @from AND r.[date] < @to
GROUP BY ISNULL(rt.[name], N'(unknown)')
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>Which apps/tabs teams have pinned - are they using Teams as a workspace, or a chat app?</summary>
        public const string TabUsage = @"
SELECT TOP (@top) tb.[name] AS Name, COUNT_BIG(DISTINCT tl.channel_id) AS [Count]
FROM dbo.teams_channel_tabs_log AS tl
INNER JOIN dbo.teams_tabs AS tb ON tb.id = tl.tab_id
WHERE tl.[date] >= @from AND tl.[date] < @to
GROUP BY tb.[name]
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>Weekly distinct team members, for the membership-growth trend.</summary>
        public static string MembershipTrend()
        {
            var week = WeekBucket("m.[date]");

            return $@"
SELECT {week} AS WeekStart, COUNT(DISTINCT m.user_id) AS ActiveUsers
FROM dbo.team_membership_log AS m
WHERE m.[date] >= @from AND m.[date] < @to
GROUP BY {week}
ORDER BY WeekStart
OPTION (RECOMPILE);";
        }

        #endregion

        #region Conversations

        /// <summary>Most-used key phrases across channel conversation.</summary>
        public const string Keywords = @"
SELECT TOP (@top) k.[name] AS Name, SUM(CAST(kw.keyword_count AS bigint)) AS [Count]
FROM dbo.teams_channel_stats_log_keywords AS kw
INNER JOIN dbo.teams_channel_stats_log AS s ON s.id = kw.channel_stats_log_id
INNER JOIN dbo.keywords AS k ON k.id = kw.keyword_id
WHERE s.[date] >= @from AND s.[date] < @to
GROUP BY k.[name]
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>Languages detected in channel conversation.</summary>
        public const string Languages = @"
SELECT TOP (@top) l.[name] AS Name, COUNT_BIG(*) AS [Count]
FROM dbo.teams_channel_stats_log_langs AS cl
INNER JOIN dbo.teams_channel_stats_log AS s ON s.id = cl.channel_stats_log_id
INNER JOIN dbo.languages AS l ON l.id = cl.language_id
WHERE s.[date] >= @from AND s.[date] < @to
GROUP BY l.[name]
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>
        /// Weekly sentiment, weighted by message count exactly as the importer weights it when it
        /// merges a day's scores, so the chart agrees with the stored per-day figure.
        /// </summary>
        public static string SentimentTrend()
        {
            var week = WeekBucket("s.[date]");

            return $@"
SELECT
    {week} AS WeekStart,
    CASE WHEN SUM(CASE WHEN s.sentiment_score IS NULL THEN 0 ELSE CAST(ISNULL(s.chats_count, 0) AS bigint) END) > 0
         THEN SUM(CASE WHEN s.sentiment_score IS NULL THEN 0 ELSE s.sentiment_score * ISNULL(s.chats_count, 0) END)
              / SUM(CASE WHEN s.sentiment_score IS NULL THEN 0 ELSE CAST(ISNULL(s.chats_count, 0) AS bigint) END)
         END AS Sentiment,
    ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0) AS Messages
FROM dbo.teams_channel_stats_log AS s
WHERE s.[date] >= @from AND s.[date] < @to
GROUP BY {week}
ORDER BY WeekStart
OPTION (RECOMPILE);";
        }

        /// <summary>Sentiment per team, weighted by message count.</summary>
        public const string SentimentByTeam = @"
SELECT TOP (@top)
    t.[name] AS Name,
    SUM(s.sentiment_score * ISNULL(s.chats_count, 0))
        / NULLIF(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0) AS Sentiment,
    ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)       AS Messages
FROM dbo.teams_channel_stats_log AS s
INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
WHERE s.[date] >= @from AND s.[date] < @to
  AND s.sentiment_score IS NOT NULL
  AND ISNULL(s.chats_count, 0) > 0
GROUP BY t.[name]
ORDER BY Messages DESC
OPTION (RECOMPILE);";

        /// <summary>Sentiment per channel, weighted by message count.</summary>
        public const string SentimentByChannel = @"
SELECT TOP (@top)
    t.[name] + N' / ' + ch.[name] AS Name,
    SUM(s.sentiment_score * ISNULL(s.chats_count, 0))
        / NULLIF(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0) AS Sentiment,
    ISNULL(SUM(CAST(ISNULL(s.chats_count, 0) AS bigint)), 0)       AS Messages
FROM dbo.teams_channel_stats_log AS s
INNER JOIN dbo.teams_channels AS ch ON ch.id = s.channel_id
INNER JOIN dbo.teams AS t ON t.id = ch.team_id
WHERE s.[date] >= @from AND s.[date] < @to
  AND s.sentiment_score IS NOT NULL
  AND ISNULL(s.chats_count, 0) > 0
GROUP BY t.[name] + N' / ' + ch.[name]
ORDER BY Messages DESC
OPTION (RECOMPILE);";

        /// <summary>
        /// Channel-days that actually carry a score. Distinguishes "cognitive services are off" from
        /// "they are on but nothing has been scored yet", which look identical on an empty chart.
        /// </summary>
        public const string ScoredChannelDays = @"
SELECT COUNT_BIG(*) AS ScoredChannelDays
FROM dbo.teams_channel_stats_log AS s
WHERE s.[date] >= @from AND s.[date] < @to AND s.sentiment_score IS NOT NULL
OPTION (RECOMPILE);";

        #endregion

        #region People

        /// <summary>
        /// The champions leaderboard: the people getting most out of Teams, with the call figures that
        /// only the call-records import can supply.
        /// </summary>
        public const string Champions = @"
WITH PerUser AS (
    SELECT
        t.user_id,
        SUM(CASE WHEN " + ActivitySum + @" > 0 THEN 1 ELSE 0 END)    AS ActiveDays,
        SUM(CAST(" + ChannelMessageSum + @" AS bigint))              AS ChannelMessages,
        SUM(CAST(t.private_chat_count AS bigint))                    AS PrivateMessages,
        SUM(CAST(t.meetings_organized_count AS bigint))              AS MeetingsOrganised,
        SUM(CAST(t.meetings_attended_count AS bigint))               AS MeetingsAttended,
        MAX(t.last_activity_date)                                    AS LastActivity
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT TOP (@top)
    u.user_name                 AS UserPrincipalName,
    dep.[name]                  AS Department,
    p.ActiveDays                AS ActiveDays,
    p.ChannelMessages           AS ChannelMessages,
    p.PrivateMessages           AS PrivateMessages,
    p.MeetingsOrganised         AS MeetingsOrganised,
    p.MeetingsAttended          AS MeetingsAttended,
    ISNULL((
        SELECT COUNT_BIG(*) FROM dbo.call_records AS c
        WHERE c.organizer_id = u.id AND c.[start] >= @from AND c.[start] < @to
    ), 0)                       AS CallsHosted,
    ISNULL((
        SELECT COUNT_BIG(DISTINCT s.call_record_id)
        FROM dbo.call_sessions AS s
        INNER JOIN dbo.call_records AS c2 ON c2.id = s.call_record_id
        WHERE s.attendee_user_id = u.id AND c2.[start] >= @from AND c2.[start] < @to
    ), 0)                       AS CallsAttended,
    p.LastActivity              AS LastActivity
FROM PerUser AS p
INNER JOIN dbo.users AS u ON u.id = p.user_id
LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id
WHERE p.ActiveDays > 0
ORDER BY p.ActiveDays DESC,
         (p.ChannelMessages + p.PrivateMessages + p.MeetingsAttended) DESC,
         u.user_name
OPTION (RECOMPILE);";

        /// <summary>
        /// Users the usage report covered but who did nothing at all. These are the licence-holders an
        /// enablement programme should target first.
        /// </summary>
        public const string DormantUsers = @"
WITH PerUser AS (
    SELECT
        t.user_id,
        SUM(CASE WHEN " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS ActiveDays,
        MAX(t.last_activity_date)                                AS LastActivity
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT TOP (@top)
    u.user_name        AS UserPrincipalName,
    dep.[name]         AS Department,
    0                  AS ActiveDays,
    CAST(0 AS bigint)  AS ChannelMessages,
    CAST(0 AS bigint)  AS PrivateMessages,
    CAST(0 AS bigint)  AS MeetingsOrganised,
    CAST(0 AS bigint)  AS MeetingsAttended,
    CAST(0 AS bigint)  AS CallsHosted,
    CAST(0 AS bigint)  AS CallsAttended,
    p.LastActivity     AS LastActivity
FROM PerUser AS p
INNER JOIN dbo.users AS u ON u.id = p.user_id
LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id
WHERE p.ActiveDays = 0 AND " + EnabledUsersPredicate + @"
ORDER BY p.LastActivity, u.user_name
OPTION (RECOMPILE);";

        /// <summary>Where the champions are - the departments already carrying Teams adoption.</summary>
        public const string ChampionsByDepartment = @"
WITH PerUser AS (
    SELECT t.user_id, SUM(CASE WHEN " + ActivitySum + @" > 0 THEN 1 ELSE 0 END) AS ActiveDays
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
    GROUP BY t.user_id
)
SELECT TOP (@top) ISNULL(dep.[name], N'(not set)') AS Name, COUNT_BIG(*) AS [Count]
FROM PerUser AS p
INNER JOIN dbo.users AS u ON u.id = p.user_id
LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id
WHERE p.ActiveDays >= @championDays
GROUP BY ISNULL(dep.[name], N'(not set)')
ORDER BY [Count] DESC
OPTION (RECOMPILE);";

        /// <summary>
        /// A cheap test for the Microsoft 365 admin-centre "anonymised usage reports" setting. When it
        /// is on, Graph replaces every user principal name with an opaque token, so the leaderboards
        /// would list meaningless strings; detecting it lets the UI say why rather than look broken.
        /// </summary>
        /// <remarks>
        /// Detected from the data rather than from configuration because the setting lives in the
        /// Microsoft 365 admin centre and is not readable through any API this application holds a
        /// permission for. The test is "does a sample of recently-reported users have a name that
        /// looks like an address?", which is exactly the property obfuscation destroys.
        /// </remarks>
        public const string ObfuscationProbe = @"
SELECT
    COUNT(*)                                                            AS Sampled,
    ISNULL(SUM(CASE WHEN u.user_name LIKE N'%@%' THEN 1 ELSE 0 END), 0) AS WithAddress
FROM (
    SELECT DISTINCT TOP (200) t.user_id
    FROM dbo.teams_user_activity_log AS t
    WHERE t.[date] >= @usageFrom AND t.[date] < @usageTo
) AS sample
INNER JOIN dbo.users AS u ON u.id = sample.user_id
OPTION (RECOMPILE);";

        #endregion

        #region Helpers

        /// <summary>The lookup table and foreign key for one demographic dimension.</summary>
        public struct DemographicSource
        {
            public DemographicSource(string table, string foreignKey)
            {
                Table = table;
                ForeignKey = foreignKey;
            }

            public string Table { get; }
            public string ForeignKey { get; }
        }

        /// <summary>
        /// Resolves a grouping name to its table and foreign key. Anything unrecognised falls back to
        /// department, so the returned identifiers are always from this fixed set and never from the
        /// caller - that is what makes the interpolation in <see cref="AdoptionBreakdown"/> safe.
        /// </summary>
        public static DemographicSource DemographicJoin(string groupBy)
        {
            switch (TeamsExplorerQuery.NormaliseGrouping(groupBy))
            {
                case "country":
                    return new DemographicSource("dbo.user_country_or_region", "country_or_region_id");
                case "office":
                    return new DemographicSource("dbo.user_office_locations", "office_location_id");
                case "jobTitle":
                    return new DemographicSource("dbo.user_job_titles", "job_title_id");
                case "company":
                    return new DemographicSource("dbo.user_company_name", "company_name_id");
                default:
                    return new DemographicSource("dbo.user_departments", "department_id");
            }
        }

        /// <summary>
        /// The statement with its parameters declared, so an admin can paste it straight into SSMS.
        /// Every query is shown with the full parameter list because every query is executed with it.
        /// </summary>
        public static string Describe(string sql, TeamsExplorerQuery query, int championDays)
        {
            if (query == null) return sql;

            var lines = new List<string>
            {
                Declare("from", "date", query.FromUtc),
                Declare("to", "date", query.ToExclusiveUtc),
                Declare("usageFrom", "date", query.UsageFromUtc),
                Declare("usageTo", "date", query.UsageToExclusiveUtc),
                Declare("wauFrom", "date", WeeklyActiveFrom(query)),
                Declare("mauFrom", "date", MonthlyActiveFrom(query)),
                Declare("midpoint", "date", Midpoint(query)),
                $"DECLARE @top int = {query.Top};",
                $"DECLARE @workStart int = {TeamsExplorerScoring.WorkingDayStartHour};",
                $"DECLARE @workEnd int = {TeamsExplorerScoring.WorkingDayEndHour};",
                $"DECLARE @championDays int = {championDays};",
            };

            return string.Join("\r\n", lines) + "\r\n" + sql;
        }

        /// <summary>Start of the trailing 7-day window, never earlier than the reporting window itself.</summary>
        public static DateTime WeeklyActiveFrom(TeamsExplorerQuery query)
        {
            var from = query.UsageToExclusiveUtc.AddDays(-7);
            return from < query.UsageFromUtc ? query.UsageFromUtc : from;
        }

        /// <summary>Start of the trailing 28-day window, never earlier than the reporting window itself.</summary>
        public static DateTime MonthlyActiveFrom(TeamsExplorerQuery query)
        {
            var from = query.UsageToExclusiveUtc.AddDays(-28);
            return from < query.UsageFromUtc ? query.UsageFromUtc : from;
        }

        /// <summary>The date that splits the window into its two halves, for the lifecycle measure.</summary>
        public static DateTime Midpoint(TeamsExplorerQuery query)
        {
            return query.UsageFromUtc.AddDays(query.Days / 2);
        }

        private static string Declare(string name, string type, DateTime value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DECLARE @{0} {1} = '{2:yyyy-MM-dd}';",
                name,
                type,
                value);
        }

        #endregion
    }
}
