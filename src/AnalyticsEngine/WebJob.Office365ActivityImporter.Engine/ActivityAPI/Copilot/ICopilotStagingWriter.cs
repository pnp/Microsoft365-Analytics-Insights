using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// How long each of the three Copilot staging tables took to commit. Returned rather than logged
    /// inside the adapter so <c>CopilotAuditEventManager</c> can keep emitting the existing
    /// "Copilot commit timing" trace verbatim - it interleaves these durations with the manager's own
    /// per-workload event counts, and that trace is a documented tool for diagnosing a slow import.
    /// </summary>
    internal class CopilotStagingCommitTimings
    {
        public TimeSpan SharePoint { get; set; }
        public TimeSpan Teams { get; set; }
        public TimeSpan ChatOnly { get; set; }

        public TimeSpan Total => SharePoint + Teams + ChatOnly;
    }

    /// <summary>
    /// Write port for the Copilot staging tables, so the manager's adaptation rules - the context
    /// priority order, agent-metadata-only mode, the null-record guard - can be exercised without a
    /// database. See issue #367.
    ///
    /// The SQL itself is unchanged: the adapter wraps the existing <c>InsertBatch&lt;T&gt;</c> instances
    /// and merge scripts. <c>InsertBatch</c> keeps its row-by-row implementation, per project convention.
    /// </summary>
    internal interface ICopilotStagingWriter
    {
        void StageSharePoint(SPCopilotLogTempEntity row);
        void StageTeams(TeamsCopilotLogTempEntity row);
        void StageChatOnly(ChatOnlyCopilotLogTempEntity row);

        /// <summary>
        /// Commits every staged row to its staging table + merge script, then clears the staged rows.
        /// </summary>
        Task<CopilotStagingCommitTimings> CommitAllChanges();
    }

    /// <summary>
    /// SQL Server adapter for <see cref="ICopilotStagingWriter"/>, holding the three
    /// <c>InsertBatch&lt;T&gt;</c> batches that <c>CopilotAuditEventManager</c> used to build itself.
    /// </summary>
    internal class SqlCopilotStagingWriter : ICopilotStagingWriter
    {
        /// <summary>
        /// Chunk size for the staging-table inserts. ParallelListProcessor spreads the batch across one
        /// connection per chunk, so a smaller chunk => more parallel inserts. The default (10000) meant a
        /// single thread for every realistic Copilot batch, serializing every row's insert round-trip.
        /// On Azure SQL those round-trips are network-latency-bound, so parallelising them is a large win;
        /// on LocalDB (no latency) it is a no-op. Kept modest so we do not open an excessive number of
        /// connections for the shared global temp table.
        /// </summary>
        private const int STAGING_INSERTS_PER_THREAD = 200;

        private readonly InsertBatch<SPCopilotLogTempEntity> _copilotInsertsSP;
        private readonly InsertBatch<TeamsCopilotLogTempEntity> _copilotInsertsTeams;
        private readonly InsertBatch<ChatOnlyCopilotLogTempEntity> _copilotInsertsChatsNoContext;
        private readonly ProjectResourceReader _rr;
        private readonly bool _throwOnPairingFailure;
        private readonly bool _enrichmentOnly;
        private readonly string _sharePointStagingTable;
        private readonly string _teamsStagingTable;
        private readonly string _chatOnlyStagingTable;

        public SqlCopilotStagingWriter(string connectionString, ILogger logger, bool throwOnPairingFailure = false,
            bool isolateStagingTables = false, bool enrichmentOnly = false)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _throwOnPairingFailure = throwOnPairingFailure;
            _enrichmentOnly = enrichmentOnly;
            // Operator replays run beside the live process; keep shards temporary even in DEBUG.
            var shard = isolateStagingTables ? Guid.NewGuid().ToString("N") : null;
            string StagingName(string name) => isolateStagingTables ? "##" + name.TrimStart('#') + "_" + shard : name;
            _sharePointStagingTable = StagingName(ActivityImportConstants.STAGING_TABLE_COPILOT_SP);
            _teamsStagingTable = StagingName(ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS);
            _chatOnlyStagingTable = StagingName(ActivityImportConstants.STAGING_TABLE_COPILOT_CHATONLY);
            _copilotInsertsSP = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsTeams = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsChatsNoContext = new InsertBatch<ChatOnlyCopilotLogTempEntity>(connectionString, logger);
        }

        public void StageSharePoint(SPCopilotLogTempEntity row) => _copilotInsertsSP.Rows.Add(row);

        public void StageTeams(TeamsCopilotLogTempEntity row) => _copilotInsertsTeams.Rows.Add(row);

        public void StageChatOnly(ChatOnlyCopilotLogTempEntity row) => _copilotInsertsChatsNoContext.Rows.Add(row);

        public async Task<CopilotStagingCommitTimings> CommitAllChanges()
        {
            var docsMergeSql = GetSql(_sharePointStagingTable, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql");
            var teamsMergeSql = GetSql(_teamsStagingTable, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql");
            var chatOnlyMergeSql = GetSql(_chatOnlyStagingTable, null);

            // Per-staging-table timing. Each of the three Copilot staging tables runs the shared
            // accessed-resource / agents merge (common_upsert_copilot_agents.sql), which on Copilot-heavy
            // tenants is the dominant save cost - so time each separately to see which workload's merge is
            // expensive (the chat-only path carries accessed resources too, so it is often the largest).
            var timings = new CopilotStagingCommitTimings();

            // Chat-only first, and every table even when an earlier one failed: a failure in a file or meeting
            // enrichment merge used to stop the plain interactions after it from being saved at all. Any failure is
            // rethrown once all three have run, so the batch is still retried (every merge here is idempotent).
            // Run order doesn't affect turn pairing, which happens whichever record arrives second.
            var failures = new List<Exception>();
            timings.ChatOnly = await CommitStagingTable(_copilotInsertsChatsNoContext, chatOnlyMergeSql, _chatOnlyStagingTable, failures);
            timings.SharePoint = await CommitStagingTable(_copilotInsertsSP, docsMergeSql, _sharePointStagingTable, failures);
            timings.Teams = await CommitStagingTable(_copilotInsertsTeams, teamsMergeSql, _teamsStagingTable, failures);
            if (failures.Count > 0)
            {
                CommitFailures.Rethrow(failures, "Copilot staging-table merges");
            }

            _copilotInsertsSP.Rows.Clear();
            _copilotInsertsTeams.Rows.Clear();
            _copilotInsertsChatsNoContext.Rows.Clear();

            return timings;
        }

        private static async Task<TimeSpan> CommitStagingTable<T>(InsertBatch<T> batch, string mergeSql, string stagingTable, List<Exception> failures)
            where T : class
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await batch.SaveToStagingTable(STAGING_INSERTS_PER_THREAD, mergeSql, stagingTable);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            sw.Stop();
            return sw.Elapsed;
        }

        private string GetSql(string tempTableName, string workloadSpecificScriptName)
        {
            var commonMergeSql = _enrichmentOnly ? string.Empty
                : _rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.common_upsert_copilot_agents.sql")
                    .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);

            var workloadSpecificSql = workloadSpecificScriptName != null
                ? _rr.ReadResourceString(workloadSpecificScriptName).Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName)
                : string.Empty;
            // Set both values explicitly: a pooled connection must never inherit strict repair mode.
            var strictMode = "EXEC sys.sp_set_session_context @key=N'copilot_history_strict', @value="
                + (_throwOnPairingFailure ? "1" : "0") + ";" + Environment.NewLine;
            return strictMode + commonMergeSql + Environment.NewLine + workloadSpecificSql;
        }
    }
}
