using System.Collections.Generic;
using System.Text;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>
    /// The SQL behind the Activity analysis page. Column lists are built from the catalogue's constants - the exact
    /// column names the profiling runbooks write, bracket-quoted - and never from anything a request carries.
    /// </summary>
    internal static class ActivityAnalysisSql
    {
        /// <summary>Generous for one scan of a period of the weekly table on a large tenant, but bounded.</summary>
        internal const int CommandTimeoutSeconds = 180;

        /// <summary>
        /// Whether the profiling tables exist, which columns the weekly table has, and its first and last weeks.
        /// </summary>
        /// <remarks>
        /// The statements naming the profiling tables only run when both exist; SQL Server resolves a missing table's
        /// name when the statement runs, so the batch compiles on a database the runbooks have never touched. MIN and
        /// MAX of <c>[date]</c> are two seeks on <c>IX_date</c>.
        /// </remarks>
        internal const string Schema = @"
SET NOCOUNT ON;

DECLARE @installed bit = CASE
    WHEN OBJECT_ID(N'profiling.ActivitiesWeeklyColumns', N'U') IS NOT NULL
     AND OBJECT_ID(N'profiling.users') IS NOT NULL THEN 1
    ELSE 0
END;

SELECT @installed AS installed;

IF @installed = 1
BEGIN
    SELECT c.name
    FROM sys.columns AS c
    WHERE c.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns', N'U');

    SELECT MIN(w.[date]) AS earliest, MAX(w.[date]) AS latest
    FROM profiling.ActivitiesWeeklyColumns AS w;
END";

        /// <summary>
        /// One period, in one round trip: every person's totals and the population's weekly figures from a single
        /// scan of the weekly table, then the licence types and who holds them.
        /// </summary>
        /// <remarks>
        /// <para><b>One scan, two groupings.</b> <c>GROUPING SETS ((user_id), (date))</c> aggregates the period's rows
        /// once into a row per person (<c>is_week = 0</c>, the <c>p</c> columns) and a row per week (<c>is_week = 1</c>,
        /// the <c>w</c> and <c>a</c> columns). Each grouping's columns are NULL on the other's rows.</para>
        /// <para><b>Small on the wire.</b> A person's totals are sent as <c>int</c>, saturated - no person's total over two
        /// years approaches 2^31 - and a zero total as NULL, which costs one byte rather than nine. Most people have no
        /// activity in most of the 58 metrics, so this is most of the data.</para>
        /// <para><b>Licences.</b> Every holding of a person in <c>profiling.users</c>; the loader keeps those of the
        /// people with a week in the period. Reading them all is cheaper than a second pass over the weekly table to
        /// find who had a week.</para>
        /// </remarks>
        internal static string BuildReadModel(IReadOnlyList<ActivityAnalysisMetric> metrics)
        {
            var sql = new StringBuilder(2000 + metrics.Count * 400);
            sql.Append(@"
SET NOCOUNT ON;

SELECT CAST(GROUPING(c.[user_id]) AS int) AS is_week,
       c.[user_id],
       c.[date]");

            for (var i = 0; i < metrics.Count; i++)
            {
                var column = "c." + metrics[i].SqlColumn;
                sql.Append(",\r\n       CAST(CASE WHEN GROUPING(c.[user_id]) = 0 THEN NULLIF(CASE WHEN SUM(")
                    .Append(column).Append(") > 2147483647 THEN 2147483647 WHEN SUM(")
                    .Append(column).Append(") < -2147483647 THEN -2147483647 ELSE SUM(")
                    .Append(column).Append(") END, 0) END AS int) AS p").Append(i);
            }

            AppendWeekColumns(sql, metrics, "CASE WHEN GROUPING(c.[user_id]) = 1 THEN ", " END");

            sql.Append(@"
FROM profiling.ActivitiesWeeklyColumns AS c
JOIN profiling.users AS u ON u.id = c.[user_id]
WHERE c.[date] BETWEEN @from AND @to
GROUP BY GROUPING SETS ((c.[user_id]), (c.[date]));

SELECT l.id, l.name, l.sku_id
FROM dbo.license_types AS l;

SELECT o.[user_id], o.license_type_id
FROM dbo.user_license_type_lookups AS o
WHERE o.[user_id] IN (SELECT u.id FROM profiling.users AS u)
GROUP BY o.[user_id], o.license_type_id;");
            return sql.ToString();
        }

        /// <summary>
        /// Every metric's weekly figures for exactly the people in <c>@people</c> - a JSON array of user ids, the form
        /// <c>ReportScopeSql</c> sends a scope in. Every available metric, so changing the selection never reads again.
        /// </summary>
        /// <remarks>
        /// The ids go into a temporary table rather than a table variable so the optimiser knows how many there are -
        /// a seek per person for a small set, a merge with the clustered key for a large one. <c>bigint</c> to match
        /// <c>ActivitiesWeeklyColumns.user_id</c>, so the join needs no conversion. Created inside the command's own
        /// <c>sp_executesql</c> call, so it is dropped when the command ends.
        /// </remarks>
        internal static string BuildWeeklyTotals(IReadOnlyList<ActivityAnalysisMetric> metrics)
        {
            var sql = new StringBuilder(1000 + metrics.Count * 200);
            sql.Append(@"
SET NOCOUNT ON;

CREATE TABLE #people ([user_id] bigint NOT NULL PRIMARY KEY);

INSERT INTO #people ([user_id])
SELECT CAST([value] AS bigint) FROM OPENJSON(@people);

SELECT c.[date]");

            AppendWeekColumns(sql, metrics, string.Empty, string.Empty);

            sql.Append(@"
FROM profiling.ActivitiesWeeklyColumns AS c
JOIN #people AS p ON p.[user_id] = c.[user_id]
WHERE c.[date] BETWEEN @from AND @to
GROUP BY c.[date];");
            return sql.ToString();
        }

        /// <summary>The weekly sum (<c>w</c>) and the people with a value above zero (<c>a</c>) of each metric.</summary>
        private static void AppendWeekColumns(StringBuilder sql, IReadOnlyList<ActivityAnalysisMetric> metrics, string prefix, string suffix)
        {
            for (var i = 0; i < metrics.Count; i++)
            {
                sql.Append(",\r\n       ").Append(prefix).Append("SUM(c.").Append(metrics[i].SqlColumn).Append(")").Append(suffix)
                    .Append(" AS w").Append(i);
            }

            for (var i = 0; i < metrics.Count; i++)
            {
                sql.Append(",\r\n       ").Append(prefix).Append("SUM(CASE WHEN c.").Append(metrics[i].SqlColumn)
                    .Append(" > 0 THEN 1 ELSE 0 END)").Append(suffix).Append(" AS a").Append(i);
            }
        }
    }
}
