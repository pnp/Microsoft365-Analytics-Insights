using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// Write port for the two DLP staging tables, so the manager's adaptation rules - which matches are
    /// worth persisting, and whether each one was a block - can be exercised without a database.
    /// Mirrors <c>IPowerPlatformStagingWriter</c>.
    /// </summary>
    internal interface IDlpStagingWriter
    {
        /// <summary>A DLP policy match embedded in a Copilot interaction (agent-attributable).</summary>
        void StageCopilotDlp(CopilotDlpLogTempEntity row);

        /// <summary>A DLP rule match from the standalone DLP.All feed (no agent identity).</summary>
        void StageDlpRuleMatch(DlpRuleMatchLogTempEntity row);

        /// <summary>
        /// Commits every staged row to its staging table + merge script, then clears the staged rows.
        /// </summary>
        Task CommitAllChanges();
    }

    /// <summary>
    /// SQL Server adapter for <see cref="IDlpStagingWriter"/>.
    /// </summary>
    internal class SqlDlpStagingWriter : IDlpStagingWriter
    {
        private readonly ProjectResourceReader _rr;
        private readonly InsertBatch<CopilotDlpLogTempEntity> _copilotDlpInserts;
        private readonly InsertBatch<DlpRuleMatchLogTempEntity> _ruleMatchInserts;

        public SqlDlpStagingWriter(string connectionString, ILogger logger)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }

            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _copilotDlpInserts = new InsertBatch<CopilotDlpLogTempEntity>(connectionString, logger);
            _ruleMatchInserts = new InsertBatch<DlpRuleMatchLogTempEntity>(connectionString, logger);
        }

        public void StageCopilotDlp(CopilotDlpLogTempEntity row) => _copilotDlpInserts.Rows.Add(row);

        public void StageDlpRuleMatch(DlpRuleMatchLogTempEntity row) => _ruleMatchInserts.Rows.Add(row);

        public async Task CommitAllChanges()
        {
            await _copilotDlpInserts.SaveToStagingTable(GetSql(
                ActivityImportConstants.STAGING_TABLE_COPILOT_DLP,
                "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp.SQL.insert_copilot_dlp_events_from_staging_table.sql"));

            await _ruleMatchInserts.SaveToStagingTable(GetSql(
                ActivityImportConstants.STAGING_TABLE_DLP_RULE_MATCH,
                "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp.SQL.insert_dlp_rule_matches_from_staging_table.sql"));

            _copilotDlpInserts.Rows.Clear();
            _ruleMatchInserts.Rows.Clear();
        }

        private string GetSql(string tempTableName, string embeddedScriptName)
        {
            return _rr.ReadResourceString(embeddedScriptName)
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);
        }
    }
}
