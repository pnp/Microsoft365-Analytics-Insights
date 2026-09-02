using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Write port for the audit-log staging table: the SQL half of a save batch (create the staging table,
    /// bulk-load the staged rows into it, then run the merge into the normal tables).
    ///
    /// Extracted by issue #373 so the decisions around it - which staging table the merge is pointed at, the
    /// merge SQL that is built, and whether the shared-write lock is taken - can be asserted with no SQL
    /// Server. The SQL itself is unchanged: the adapter wraps the existing
    /// <c>EFInsertBatch&lt;AuditLogTempEntity&gt;</c>, and <c>InsertBatch&lt;T&gt;</c> keeps its row-by-row
    /// implementation per project convention.
    ///
    /// Why one <c>LoadAndMergeAsync</c> rather than #373's proposed separate create / load / merge calls:
    /// <c>InsertBatch.SaveToStagingTable</c> opens one connection, creates the staging table on it, fans the
    /// row inserts out over separate per-chunk connections, then runs the merge back on the original - and
    /// holds that original connection open throughout, which is what keeps the (Release-configuration)
    /// <c>##</c> global temp table alive and visible to the chunk connections and to the merge. Splitting
    /// that across three port calls would break the table's lifetime or force the port to hold connection
    /// state.
    ///
    /// Internal because <see cref="AuditLogTempEntity"/> is internal;
    /// <c>InternalsVisibleTo("Tests.UnitTests")</c> makes it reachable from the test project.
    /// </summary>
    internal interface IActivityStagingWriter
    {
        /// <summary>
        /// Start one save batch. Created per save (not per manager) because the underlying
        /// <c>InsertBatch</c> takes its connection string from the context this save runs on and owns that
        /// save's row list.
        /// </summary>
        IActivityStagingBatch CreateBatch(AnalyticsEntitiesContext db);
    }

    /// <summary>
    /// The staged rows for one save batch, plus the load+merge that commits them.
    /// </summary>
    internal interface IActivityStagingBatch
    {
        /// <summary>Add one row to the staging batch. Nothing is sent to SQL until the merge.</summary>
        void AddRow(AuditLogTempEntity row);

        /// <summary>
        /// Create + load the staging table and run <paramref name="mergeSql"/> against it.
        /// </summary>
        /// <param name="insertsPerThread">Rows per parallel insert thread (the production value is 10,000).</param>
        /// <param name="stagingTableName">
        /// The per-save sharded staging table, or <c>null</c> to use the type's own
        /// <c>[TempTableName]</c> (the single shared staging table used by the default serial path).
        /// </param>
        /// <param name="mergeLock">
        /// When supplied, serialises ONLY the merge - which writes shared lookup / fact tables - while the
        /// parallel staging load runs unlocked. <c>null</c> on the serial path, where the whole save is
        /// already serialised.
        /// </param>
        /// <returns>Rows affected by the merge, as reported by SQL Server.</returns>
        Task<int> LoadAndMergeAsync(int insertsPerThread, string mergeSql, string stagingTableName, SemaphoreSlim mergeLock);
    }

    /// <summary>
    /// Production <see cref="IActivityStagingWriter"/>: an <c>EFInsertBatch</c> constructed from the save
    /// context's connection string, which is exactly what <c>ActivityReportSqlPersistenceManager</c> built
    /// inline before #373.
    /// </summary>
    internal sealed class SqlActivityStagingWriter : IActivityStagingWriter
    {
        private readonly ILogger _logger;

        public SqlActivityStagingWriter(ILogger logger)
        {
            _logger = logger;
        }

        public IActivityStagingBatch CreateBatch(AnalyticsEntitiesContext db)
        {
            return new SqlActivityStagingBatch(new EFInsertBatch<AuditLogTempEntity>(db, _logger));
        }
    }

    /// <summary>
    /// SQL Server adapter for one save batch. A thin wrapper: the row list and the load+merge are still
    /// <c>InsertBatch&lt;T&gt;</c>'s.
    /// </summary>
    internal sealed class SqlActivityStagingBatch : IActivityStagingBatch
    {
        private readonly EFInsertBatch<AuditLogTempEntity> _batch;

        public SqlActivityStagingBatch(EFInsertBatch<AuditLogTempEntity> batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            _batch = batch;
        }

        public void AddRow(AuditLogTempEntity row) => _batch.Rows.Add(row);

        public Task<int> LoadAndMergeAsync(int insertsPerThread, string mergeSql, string stagingTableName, SemaphoreSlim mergeLock)
            => _batch.SaveToStagingTable(insertsPerThread, mergeSql, stagingTableName, mergeLock);
    }
}
