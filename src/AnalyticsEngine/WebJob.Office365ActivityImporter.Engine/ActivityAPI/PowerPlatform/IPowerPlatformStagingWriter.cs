using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Write port for the six Power Platform staging tables, so the manager's adaptation rules -
    /// client-type normalisation, connector joining, share-record expansion, display-name fallback -
    /// can be exercised without a database. See issue #367.
    ///
    /// Internal because the staging entities it carries are internal; <c>InternalsVisibleTo("Tests.UnitTests")</c>
    /// makes it reachable from the test project. The SQL is unchanged: the adapter wraps the existing
    /// <c>InsertBatch&lt;T&gt;</c> instances and merge scripts, and <c>InsertBatch</c> keeps its row-by-row
    /// implementation per project convention.
    /// </summary>
    internal interface IPowerPlatformStagingWriter
    {
        void StagePowerApp(PowerAppLogTempEntity row);
        void StagePowerAppShare(PowerAppShareLogTempEntity row);
        void StageFlow(PowerAutomateFlowLogTempEntity row);
        void StageFlowShare(PowerAutomateFlowShareLogTempEntity row);
        void StagePowerBi(PowerBILogTempEntity row);
        void StageCopilotStudio(CopilotStudioLogTempEntity row);

        /// <summary>
        /// Commits every staged row to its staging table + merge script, then clears the staged rows.
        /// </summary>
        Task CommitAllChanges();
    }

    /// <summary>
    /// SQL Server adapter for <see cref="IPowerPlatformStagingWriter"/>, holding the six
    /// <c>InsertBatch&lt;T&gt;</c> batches that <c>PowerPlatformAuditEventManager</c> used to build itself.
    /// </summary>
    internal class SqlPowerPlatformStagingWriter : IPowerPlatformStagingWriter
    {
        private readonly ProjectResourceReader _rr;

        private readonly InsertBatch<PowerAppLogTempEntity> _appInserts;
        private readonly InsertBatch<PowerAppShareLogTempEntity> _appShareInserts;
        private readonly InsertBatch<PowerAutomateFlowLogTempEntity> _flowInserts;
        private readonly InsertBatch<PowerAutomateFlowShareLogTempEntity> _flowShareInserts;
        private readonly InsertBatch<PowerBILogTempEntity> _powerBiInserts;
        private readonly InsertBatch<CopilotStudioLogTempEntity> _copilotStudioInserts;

        public SqlPowerPlatformStagingWriter(string connectionString, ILogger logger)
        {
            // Deliberately no connection-string guard: the original manager did not validate it either.
            // InsertBatch<T> just stores the string, and a commit with no staged rows returns before any
            // connection is opened - so rejecting it here would be a new failure mode, not an extraction.
            // (The Copilot manager DID validate, so its adapter keeps that check.)
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _appInserts = new InsertBatch<PowerAppLogTempEntity>(connectionString, logger);
            _appShareInserts = new InsertBatch<PowerAppShareLogTempEntity>(connectionString, logger);
            _flowInserts = new InsertBatch<PowerAutomateFlowLogTempEntity>(connectionString, logger);
            _flowShareInserts = new InsertBatch<PowerAutomateFlowShareLogTempEntity>(connectionString, logger);
            _powerBiInserts = new InsertBatch<PowerBILogTempEntity>(connectionString, logger);
            _copilotStudioInserts = new InsertBatch<CopilotStudioLogTempEntity>(connectionString, logger);
        }

        public void StagePowerApp(PowerAppLogTempEntity row) => _appInserts.Rows.Add(row);

        public void StagePowerAppShare(PowerAppShareLogTempEntity row) => _appShareInserts.Rows.Add(row);

        public void StageFlow(PowerAutomateFlowLogTempEntity row) => _flowInserts.Rows.Add(row);

        public void StageFlowShare(PowerAutomateFlowShareLogTempEntity row) => _flowShareInserts.Rows.Add(row);

        public void StagePowerBi(PowerBILogTempEntity row) => _powerBiInserts.Rows.Add(row);

        public void StageCopilotStudio(CopilotStudioLogTempEntity row) => _copilotStudioInserts.Rows.Add(row);

        public async Task CommitAllChanges()
        {
            await _appInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_APP, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_events_from_staging_table.sql"));
            await _appShareInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_APP_SHARE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_share_events_from_staging_table.sql"));
            await _flowInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_events_from_staging_table.sql"));
            await _flowShareInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE_SHARE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_share_events_from_staging_table.sql"));
            await _powerBiInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_BI, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_bi_events_from_staging_table.sql"));
            await _copilotStudioInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_STUDIO, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_copilot_studio_events_from_staging_table.sql"));

            _appInserts.Rows.Clear();
            _appShareInserts.Rows.Clear();
            _flowInserts.Rows.Clear();
            _flowShareInserts.Rows.Clear();
            _powerBiInserts.Rows.Clear();
            _copilotStudioInserts.Rows.Clear();
        }

        private string GetSql(string tempTableName, string embeddedScriptName)
        {
            return _rr.ReadResourceString(embeddedScriptName)
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);
        }
    }
}
