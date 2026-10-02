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
    public static class CopilotAdoptionSeatTimeSql
    {
        private const int AppHostKeyWidth = 100;

        public static string SeatHolderTimeSavedSql(IEnumerable<int> seatLicenceTypeIds, IEnumerable<int> coworkAgentIds)
        {
            var seats = string.Join(",", (seatLicenceTypeIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i));
            if (string.IsNullOrWhiteSpace(seats)) seats = "-1";

            var coworkIds = (coworkAgentIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(i => i).ToList();
            var cowork = coworkIds.Count == 0
                ? "(LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(100))) = 'cowork')"
                : "(LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(100))) = 'cowork' OR c.agent_id IN (" + string.Join(",", coworkIds) + "))";

            var host = "LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(" + AppHostKeyWidth + ")))";
            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({seats})\r\n" +
                "),\r\n" +
                "SeatActions AS (\r\n" +
                "    SELECT c.user_id AS user_id,\r\n" +
                "           CASE WHEN " + host + " LIKE '%outlook%' THEN 1 ELSE 0 END AS OutlookAction,\r\n" +
                "           CASE WHEN " + host + " LIKE '%word%' OR " + host + " LIKE '%powerpoint%' OR " + host + " LIKE '%excel%' THEN 1 ELSE 0 END AS OfficeAction,\r\n" +
                "           CASE WHEN EXISTS (SELECT 1 FROM dbo.copilot_event_meetings AS m WHERE m.copilot_chat_id = c.event_id) THEN 1 ELSE 0 END AS MeetingAction\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    JOIN SeatUsers AS seats ON seats.user_id = c.user_id\r\n" +
                "    WHERE c.time_stamp >= @from\r\n" +
                "      AND c.time_stamp < @toExclusive\r\n" +
                "      AND c.user_id IS NOT NULL\r\n" +
                "      AND c.agent_id IS NULL\r\n" +
                "      AND NOT (" + cowork + ")\r\n" +
                ")\r\n" +
                "SELECT a.user_id AS UserId,\r\n" +
                "       CAST(NULLIF(LTRIM(RTRIM(u.department)), '') AS nvarchar(400)) AS Department,\r\n" +
                "       SUM(CAST(a.OutlookAction AS bigint)) AS OutlookActions,\r\n" +
                "       SUM(CAST(a.OfficeAction AS bigint)) AS OfficeActions,\r\n" +
                "       SUM(CAST(a.MeetingAction AS bigint)) AS TeamsMeetingActions,\r\n" +
                "       SUM(CAST(CASE WHEN a.OutlookAction = 0 AND a.OfficeAction = 0 AND a.MeetingAction = 0 THEN 1 ELSE 0 END AS bigint)) AS UncreditedActions\r\n" +
                "FROM SeatActions AS a\r\n" +
                "LEFT JOIN dbo.users AS u ON u.id = a.user_id\r\n" +
                "GROUP BY a.user_id, CAST(NULLIF(LTRIM(RTRIM(u.department)), '') AS nvarchar(400));";
        }
    }
}
