using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotSpend
{
    /// <summary>
    /// The Copilot Spend report's own SQL: billed Copilot Studio usage by day and by agent, narrowed to the reader's scope in
    /// the statement itself. Everything else the report shows comes from the facts the investment report already reads and
    /// caches tenant-wide (latest balances, per-person Studio totals), narrowed in memory.
    /// </summary>
    public static class CopilotSpendSql
    {
        /// <summary>
        /// Billed credits and people per day: a daily ledger, so these add up over the period. People the user import never
        /// matched share a NULL user id: an unfiltered view counts their credits (the tenant's whole bill) but not as people,
        /// and a filtered view, which cannot show they belong to it, leaves them out.
        /// </summary>
        public const string StudioDaysSql =
            "SELECT r.usage_date AS UsageDate, CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.user_id) AS People\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "  /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "GROUP BY r.usage_date\r\n" +
            "ORDER BY r.usage_date;";

        /// <summary>
        /// Billed credits per agent, with the agent's newest name from the tenant ledger. The names are read once for the
        /// window's agents, not once per agent.
        /// </summary>
        public const string StudioAgentsSql =
            "WITH used AS (\r\n" +
            "    SELECT r.agent_id, SUM(r.billed_credits) AS credits, COUNT(DISTINCT r.user_id) AS people, COUNT(DISTINCT r.usage_date) AS days\r\n" +
            "    FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "    WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "      /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "    GROUP BY r.agent_id),\r\n" +
            "names AS (\r\n" +
            "    SELECT d.agent_id, d.agent_name, ROW_NUMBER() OVER (PARTITION BY d.agent_id ORDER BY d.usage_date DESC) AS newest\r\n" +
            "    FROM dbo.copilot_studio_credit_daily AS d\r\n" +
            "    WHERE d.usage_date >= @from AND d.usage_date < @toExclusive AND d.agent_name IS NOT NULL)\r\n" +
            "SELECT u.agent_id AS AgentId, n.agent_name AS AgentName, CAST(u.credits AS float) AS Credits, u.people AS People, u.days AS Days\r\n" +
            "FROM used AS u\r\n" +
            "LEFT JOIN names AS n ON n.agent_id = u.agent_id AND n.newest = 1\r\n" +
            "ORDER BY u.credits DESC, u.agent_id;";

        public const string LedgerPresentSql =
            "/*scope:none*/SELECT CASE WHEN OBJECT_ID(N'dbo.copilot_studio_credit_user_daily') IS NOT NULL AND OBJECT_ID(N'dbo.copilot_studio_credit_daily') IS NOT NULL THEN 1 ELSE 0 END;";
    }

    public interface ICopilotSpendFactsSource
    {
        /// <summary>Billed Studio credits by day and by agent over [<paramref name="from"/>, <paramref name="toExclusive"/>), in scope.</summary>
        Task<CopilotSpendStudioBreakdown> GetStudioBreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope, CancellationToken cancellationToken);
    }

    public sealed class SqlCopilotSpendFactsSource : ICopilotSpendFactsSource
    {
        private readonly Func<SqlConnection> _connection;

        public SqlCopilotSpendFactsSource(Func<SqlConnection> connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public async Task<CopilotSpendStudioBreakdown> GetStudioBreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope,
            CancellationToken cancellationToken)
        {
            var breakdown = new CopilotSpendStudioBreakdown();
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var probe = new SqlCommand(CopilotSpendSql.LedgerPresentSql, db))
                {
                    if (Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) return breakdown;
                }

                using (var cmd = Command(db, CopilotSpendSql.StudioDaysSql, from, toExclusive, scope))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        breakdown.Days.Add(new CopilotSpendDay
                        {
                            Date = reader.GetDateTime(0),
                            Credits = Math.Round(reader.IsDBNull(1) ? 0 : reader.GetDouble(1), 6),
                            People = reader.GetInt32(2),
                        });
                }

                using (var cmd = Command(db, CopilotSpendSql.StudioAgentsSql, from, toExclusive, scope))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        breakdown.Agents.Add(new CopilotSpendAgent
                        {
                            AgentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                            AgentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Credits = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 6),
                            People = reader.GetInt32(3),
                            Days = reader.GetInt32(4),
                        });
                }
            }
            return breakdown;
        }

        private static SqlCommand Command(SqlConnection db, string sql, DateTime from, DateTime toExclusive, ReportUserScope scope)
        {
            var cmd = new SqlCommand(ReportScopeSql.Apply(sql, scope ?? ReportUserScope.Everyone), db) { CommandTimeout = 120 };
            // The ledger's usage_date is datetime: a parameter of the same type keeps the date predicate a plain range seek.
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime) { Value = from.Date });
            cmd.Parameters.Add(new SqlParameter("@toExclusive", SqlDbType.DateTime) { Value = toExclusive.Date });
            var scopeParameter = ReportScopeSql.CreateParameter(scope);
            if (scopeParameter != null) cmd.Parameters.Add(scopeParameter);
            return cmd;
        }
    }
}
