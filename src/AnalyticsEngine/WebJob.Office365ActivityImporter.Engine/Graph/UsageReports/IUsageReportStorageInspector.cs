using Common.Entities;
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Reflection;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Storage-shape questions and physical maintenance for a usage-report table - the things the daily
    /// loaders used to ask SQL Server directly, in raw SQL, from inside the import logic.
    ///
    /// Behind a port because neither is import logic: one is an index-shape question that selects a query
    /// strategy, the other is columnstore maintenance that belongs to deployment. See issue #375.
    /// </summary>
    public interface IUsageReportStorageInspector
    {
        /// <summary>
        /// Does this table have a non-disabled, non-filtered index whose FIRST key column is <c>date</c>?
        /// That decides which query shape the finalized-date scan uses: bounded existence seeks per
        /// candidate date when it does, one range scan when it does not.
        /// </summary>
        Task<bool> HasLeadingDateIndexAsync(string qualifiedTableName);

        /// <summary>
        /// Compact the table's columnstore delta rowgroups, if it has a columnstore index. A no-op (single
        /// metadata read) when it does not.
        /// </summary>
        Task CompactColumnstoreAsync(string qualifiedTableName);
    }

    /// <summary>
    /// Resolves the SQL table name an EF report entity maps to. Pure, so the loaders' two different
    /// reactions to a missing <see cref="TableAttribute"/> - throw for the index question, silently skip
    /// the maintenance - stay explicit and testable.
    /// </summary>
    public static class UsageReportTableName
    {
        /// <summary>The <c>schema.table</c> name, or null when the entity declares no table.</summary>
        public static string TryResolve(Type reportEntityType)
        {
            if (reportEntityType == null) throw new ArgumentNullException(nameof(reportEntityType));

            var table = reportEntityType.GetCustomAttribute<TableAttribute>();
            if (table == null) return null;

            return $"{table.Schema ?? "dbo"}.{table.Name}";
        }

        /// <summary>As <see cref="TryResolve"/>, but throws when the entity declares no table.</summary>
        public static string Resolve(Type reportEntityType)
        {
            var name = TryResolve(reportEntityType);
            if (name == null)
            {
                throw new InvalidOperationException(
                    $"{reportEntityType.Name} must declare TableAttribute to inspect its date index.");
            }
            return name;
        }
    }

    /// <summary>
    /// EF6/SQL Server <see cref="IUsageReportStorageInspector"/>. Both statements were moved verbatim out of
    /// <c>AbstractDailyActivityLoader</c> by issue #375 - the SQL itself is unchanged.
    /// </summary>
    public sealed class SqlUsageReportStorageInspector : IUsageReportStorageInspector
    {
        private readonly AnalyticsEntitiesContext _db;

        public SqlUsageReportStorageInspector(AnalyticsEntitiesContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<bool> HasLeadingDateIndexAsync(string qualifiedTableName)
        {
            const string sql = @"
SELECT CAST(CASE WHEN EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic
      ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    INNER JOIN sys.columns AS c
      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(@tableName)
      AND i.is_disabled = 0
      AND i.is_hypothetical = 0
      AND i.has_filter = 0
      AND ic.key_ordinal = 1
      AND c.name = N'date'
) THEN 1 ELSE 0 END AS bit);";

            return await _db.Database
                .SqlQuery<bool>(sql, new SqlParameter("@tableName", qualifiedTableName))
                .SingleAsync();
        }

        /// <summary>
        /// <para>
        /// The loaders write per-(date, user) upserts row by row, and only a bulk load of 102,400+ rows
        /// compresses straight into a compressed rowgroup - everything else lands in the rowstore delta
        /// store, which is scanned uncompressed. Left alone, the delta store grows every import cycle and
        /// the columnstore's advantage decays back towards the full-scan behaviour the
        /// <c>ColumnstoreUsageReportMetrics</c> migration exists to remove.
        /// </para>
        /// <para>
        /// Plain <c>REORGANIZE</c> rather than <c>WITH (COMPRESS_ALL_ROW_GROUPS = ON)</c>: it merges and
        /// compresses CLOSED delta rowgroups and removes deleted rows, which is the cheap, safe operation to
        /// run on every cycle. Forcing the currently-open rowgroup to compress as well would rewrite the
        /// trailing day's rows on every single import for very little gain.
        /// </para>
        /// </summary>
        public async Task CompactColumnstoreAsync(string qualifiedTableName)
        {
            // i.type = 6 is NONCLUSTERED COLUMNSTORE.
            const string sql = @"
DECLARE @ix sysname = (
    SELECT TOP (1) i.name
    FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(@tableName) AND i.type = 6 AND i.is_disabled = 0);

IF @ix IS NOT NULL
BEGIN
    DECLARE @sql nvarchar(max) =
        N'ALTER INDEX ' + QUOTENAME(@ix) + N' ON ' + @tableName + N' REORGANIZE;';
    EXEC sp_executesql @sql;
END";

            await _db.Database.ExecuteSqlCommandAsync(
                // DoNotEnsureTransaction: index maintenance has no business inside a transaction. EF6's
                // default (EnsureTransaction) would wrap a potentially long REORGANIZE in one, holding it
                // open and growing the log for no benefit. (Verified that REORGANIZE does still succeed
                // under EF's default behaviour on current SQL Server, so this is hardening rather than a
                // bug fix - but there is no reason to run maintenance transactionally.)
                TransactionalBehavior.DoNotEnsureTransaction,
                sql,
                new SqlParameter("@tableName", qualifiedTableName));
        }
    }
}
