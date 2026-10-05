using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// SQL for the modelled "time already saved by seat holders" estimate. Kept out of
    /// <see cref="CopilotAdoptionSql"/> so work on date-window handling can merge without touching this
    /// query. The query takes both <c>@from</c> and <c>@toExclusive</c>; today's caller passes the analysis
    /// timestamp as <c>@toExclusive</c>, and custom ranges can wire their explicit end into the same shape.
    /// </summary>
    /// <remarks>
    /// Surface mapping deliberately uses the same bounded app-host expression as the existing usage-by-app
    /// chart (<see cref="CopilotAdoptionSql.AppHostKey"/>), then maps exact keys: <c>Outlook</c> to the
    /// Outlook email-action credit, and <c>Word</c>, <c>PowerPoint</c> and <c>Excel</c> to the Office
    /// document-work credit. A meeting context takes precedence over every app host, so one meeting-linked
    /// interaction cannot also earn an Outlook, Office or uncredited action. Meeting credits are counted
    /// as distinct <c>copilot_event_meetings.meeting_id</c> foreign-key values (the <c>online_meetings</c> row id), once
    /// per seat holder, matching Microsoft's per-meeting credit unit.
    /// </remarks>
    public static class CopilotAdoptionSeatTimeSql
    {
        public static string SeatHolderTimeSavedSql(
            IEnumerable<int> seatLicenceTypeIds,
            IEnumerable<int> coworkAgentIds,
            bool useLicenceHistory = false)
        {
            var seats = string.Join(",", (seatLicenceTypeIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i));
            if (string.IsNullOrWhiteSpace(seats)) seats = "-1";

            // Negated below, so the agent-id term must be FALSE - never UNKNOWN - for the NULL agent_id that
            // every row kept here has. A bare `c.agent_id IN (...)` made NOT (...) UNKNOWN for all of them, so
            // a tenant with any Cowork agent row got no actions at all and every seat holder read zero.
            var coworkIds = (coworkAgentIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i).ToList();
            var cowork = coworkIds.Count == 0
                ? "(LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(100))) = 'cowork')"
                : "(LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(100))) = 'cowork' OR (c.agent_id IS NOT NULL AND c.agent_id IN (" + string.Join(",", coworkIds) + ")))";

            var host = "LOWER(" + CopilotAdoptionSql.AppHostKey("c.app_host", string.Empty) + ")";
            const string outlookHosts = "'outlook'";
            const string officeHosts = "'word','powerpoint','excel'";
            var seatUsers = useLicenceHistory
                ? "    SELECT DISTINCT h.user_id AS user_id\r\n" +
                  "    FROM dbo.user_license_history AS h\r\n" +
                  $"    WHERE h.license_type_id IN ({seats})\r\n" +
                  "      AND (h.valid_from_utc < @toExclusive OR (h.from_source = 0 AND h.valid_from_utc <= @historyStart))\r\n" +
                  "      AND (h.valid_to_utc IS NULL OR h.valid_to_utc > @from)\r\n"
                : "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                  "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                  $"    WHERE ul.license_type_id IN ({seats})\r\n";

            return
                "SET NOCOUNT ON;\r\n" +
                "IF OBJECT_ID('tempdb..#seat_time_grain') IS NOT NULL DROP TABLE #seat_time_grain;\r\n" +
                "WITH SeatUsers AS (\r\n" +
                seatUsers +
                ")\r\n" +
                "SELECT c.user_id AS user_id,\r\n" +
                "       " + host + " AS app_host,\r\n" +
                "       m.meeting_id AS meeting_id\r\n" +
                "INTO #seat_time_grain\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "JOIN SeatUsers AS seats ON seats.user_id = c.user_id\r\n" +
                "LEFT JOIN dbo.copilot_event_meetings AS m ON m.copilot_chat_id = c.event_id\r\n" +
                "WHERE c.time_stamp >= @from\r\n" +
                "  AND c.time_stamp < @toExclusive\r\n" +
                "  AND c.user_id IS NOT NULL\r\n" +
                "  AND c.agent_id IS NULL\r\n" +
                "  AND NOT (" + cowork + ");\r\n" +
                "\r\n" +
                "WITH NonMeeting AS (\r\n" +
                "    SELECT user_id,\r\n" +
                "           SUM(CAST(CASE WHEN meeting_id IS NULL AND app_host IN (" + outlookHosts + ") THEN 1 ELSE 0 END AS bigint)) AS OutlookActions,\r\n" +
                "           SUM(CAST(CASE WHEN meeting_id IS NULL AND app_host IN (" + officeHosts + ") THEN 1 ELSE 0 END AS bigint)) AS OfficeActions,\r\n" +
                "           SUM(CAST(CASE WHEN meeting_id IS NULL AND app_host NOT IN (" + outlookHosts + "," + officeHosts + ") THEN 1 ELSE 0 END AS bigint)) AS UncreditedActions\r\n" +
                "    FROM #seat_time_grain\r\n" +
                "    WHERE meeting_id IS NULL\r\n" +
                "    GROUP BY user_id\r\n" +
                "),\r\n" +
                "Meetings AS (\r\n" +
                "    SELECT user_id, CAST(COUNT_BIG(*) AS bigint) AS TeamsMeetingActions\r\n" +
                "    FROM (SELECT DISTINCT user_id, meeting_id FROM #seat_time_grain WHERE meeting_id IS NOT NULL) AS distinct_meetings\r\n" +
                "    GROUP BY user_id\r\n" +
                "),\r\n" +
                "UsersWithActions AS (\r\n" +
                "    SELECT user_id FROM NonMeeting\r\n" +
                "    UNION\r\n" +
                "    SELECT user_id FROM Meetings\r\n" +
                ")\r\n" +
                "SELECT a.user_id AS UserId,\r\n" +
                "       CAST(NULLIF(LTRIM(RTRIM(d.name)), '') AS nvarchar(400)) AS Department,\r\n" +
                "       ISNULL(n.OutlookActions, 0) AS OutlookActions,\r\n" +
                "       ISNULL(n.OfficeActions, 0) AS OfficeActions,\r\n" +
                "       ISNULL(m.TeamsMeetingActions, 0) AS TeamsMeetingActions,\r\n" +
                "       ISNULL(n.UncreditedActions, 0) AS UncreditedActions\r\n" +
                "FROM UsersWithActions AS a\r\n" +
                "LEFT JOIN NonMeeting AS n ON n.user_id = a.user_id\r\n" +
                "LEFT JOIN Meetings AS m ON m.user_id = a.user_id\r\n" +
                "LEFT JOIN dbo.users AS u ON u.id = a.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS d ON d.id = u.department_id;";
        }
    }
}
