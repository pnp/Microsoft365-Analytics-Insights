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
    /// The Copilot Spend report's own SQL: billed Copilot Studio usage by day and by environment, narrowed to the reader's
    /// scope in the statement itself, and, for an unfiltered view only, the tenant's per-agent ledger. Everything else the
    /// report shows comes from the facts the investment report already reads and caches tenant-wide (latest balances,
    /// per-person Studio totals), narrowed in memory.
    /// </summary>
    public static class CopilotSpendSql
    {
        /// <summary>
        /// Billed credits and people per day: a daily ledger, so these add up over the period. People the user import never
        /// matched share a NULL user id: an unfiltered view counts their credits (the tenant's whole bill) but not as people,
        /// and a filtered view, which cannot show they belong to it, leaves them out.
        /// <para>Measured on LocalDB over a synthetic 200,000-user ledger (1.2M rows, 180 days): a clustered index scan of
        /// 57.6K logical reads whatever the window, 0.16 s for 28 days and 0.59 s for 90; 0.10 s and 0.17 s narrowed to
        /// 20,000 people. The ledger has no index covering this query, so it reads the table; no index is added for it
        /// here, as none has been measured.</para>
        /// </summary>
        public const string StudioDaysSql =
            "SELECT r.usage_date AS UsageDate, CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.user_id) AS People\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "  /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "GROUP BY r.usage_date\r\n" +
            "ORDER BY r.usage_date;";

        /// <summary>
        /// Billed credits per Power Platform environment, from the same per-person ledger and scope as the days, so the two
        /// add up to the same total. The ledger is kept by day, person and environment; it has no agent.
        /// <para>Two distinct counts (people and days) make SQL Server spool the rows: on the same synthetic 200,000-user
        /// ledger 626K logical reads (mostly the worktable) and 0.58 s for 28 days, 1.94M and 1.96 s for 90; 97K / 0.14 s and
        /// 221K / 0.30 s narrowed to 20,000 people. GROUPING SETS, a per-person pre-aggregation and a single distinct count
        /// were measured too and none was faster (1.9M reads and 4.5 s, 115K reads and 6.2 s, 58K reads and 3.4 s for 90
        /// days), so this form stays. The result is cached per window and scope.</para>
        /// </summary>
        public const string StudioEnvironmentsSql =
            "SELECT r.environment_id AS EnvironmentId, MAX(r.environment_name) AS EnvironmentName,\r\n" +
            "       CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.user_id) AS People, COUNT(DISTINCT r.usage_date) AS Days\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "  /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "GROUP BY r.environment_id\r\n" +
            "ORDER BY Credits DESC, r.environment_id;";

        /// <summary>
        /// Billed credits per agent from the tenant's agent ledger, with each agent's newest name in the window. That ledger
        /// is recorded per agent and not per person, through a different route from the per-person one, so it is read only
        /// for an unfiltered view and its total is reported on its own, never added to or reconciled with the people's.
        /// Measured over a synthetic 86,400-row ledger (40 agents, 4 environments, 180 days): 9.6K logical reads, 0.02 s for
        /// 28 days and 0.35 s for 90.
        /// </summary>
        public const string LedgerAgentsSql =
            ReportScopeSql.TenantWideMarker + "WITH win AS (\r\n" +
            "    SELECT d.agent_id, d.agent_name, d.usage_date, d.billed_credits\r\n" +
            "    FROM dbo.copilot_studio_credit_daily AS d\r\n" +
            "    WHERE d.usage_date >= @from AND d.usage_date < @toExclusive),\r\n" +
            "totals AS (\r\n" +
            "    SELECT agent_id, SUM(billed_credits) AS credits, COUNT(DISTINCT usage_date) AS days FROM win GROUP BY agent_id),\r\n" +
            "names AS (\r\n" +
            "    SELECT agent_id, agent_name, ROW_NUMBER() OVER (PARTITION BY agent_id ORDER BY usage_date DESC, agent_name) AS newest\r\n" +
            "    FROM win WHERE agent_name IS NOT NULL)\r\n" +
            "SELECT t.agent_id AS AgentId, n.agent_name AS AgentName, CAST(t.credits AS float) AS Credits, t.days AS Days\r\n" +
            "FROM totals AS t\r\n" +
            "LEFT JOIN names AS n ON n.agent_id = t.agent_id AND n.newest = 1\r\n" +
            "ORDER BY t.credits DESC, t.agent_id;";

        public const string LedgerPresentSql =
            ReportScopeSql.TenantWideMarker + "SELECT CASE WHEN OBJECT_ID(N'dbo.copilot_studio_credit_user_daily') IS NOT NULL AND OBJECT_ID(N'dbo.copilot_studio_credit_daily') IS NOT NULL THEN 1 ELSE 0 END;";
    }

    public interface ICopilotSpendFactsSource
    {
        /// <summary>
        /// Billed Studio credits by day and by environment over [<paramref name="from"/>, <paramref name="toExclusive"/>), in
        /// scope; and by agent from the tenant agent ledger when <paramref name="scope"/> is not restricted.
        /// </summary>
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

                using (var cmd = Command(db, CopilotSpendSql.StudioEnvironmentsSql, from, toExclusive, scope))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        breakdown.Environments.Add(new CopilotSpendEnvironment
                        {
                            EnvironmentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                            EnvironmentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Credits = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 6),
                            People = reader.GetInt32(3),
                            Days = reader.GetInt32(4),
                        });
                }

                // The agent ledger is not kept per person, so it cannot be narrowed to a filtered view: it is not read for one.
                if (scope == null || !scope.IsRestricted)
                {
                    breakdown.Agents = new List<CopilotSpendAgent>();
                    using (var cmd = Command(db, CopilotSpendSql.LedgerAgentsSql, from, toExclusive, ReportUserScope.Everyone))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            breakdown.Agents.Add(new CopilotSpendAgent
                            {
                                AgentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                                AgentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Credits = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 6),
                                Days = reader.GetInt32(3),
                            });
                    }
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
